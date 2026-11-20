using System.Diagnostics;
using System.Reflection;
using Moderato.Results;

namespace Moderato.Effects.WasmRunner;

/// <summary>No code has compiled successfully yet.</summary>
public sealed record NothingCompiled;

/// <summary>The compiled code has no entry point to run.</summary>
public sealed record NoEntryPoint;

/// <summary>The reader's program threw.</summary>
/// <param name="Exception">What it threw, with the reflection wrapper removed.</param>
public sealed record ProgramFailed(Exception Exception);

/// <summary>The ways running the reader's program can end without success.</summary>
public union RunError(NothingCompiled, NoEntryPoint, ProgramFailed);

/// <summary>Runs a compiled program, described as an effect.</summary>
internal static class UserProgram
{
    // Whatever ends the run, everything the program wrote reaches the page.
    private static readonly Resource<Unit, Never, EmptyEnvironment> flushedConsole = Resource.Make(
        acquire: Effect<EmptyEnvironment>.Succeed(Unit.Value),
        release: (_, _) => Effect<EmptyEnvironment>.Sync(() =>
        {
            Console.Out.Flush();
            Console.Error.Flush();
            return Unit.Value;
        }));

    /// <summary>Loads <paramref name="image"/> and runs its entry point.</summary>
    /// <returns>An effect whose value is the elapsed milliseconds.</returns>
    public static Effect<double, RunError, EmptyEnvironment> Run(byte[]? image) =>
        Effect.Gen(async (Scope<RunError, EmptyEnvironment> scope) =>
        {
            var bytes = image ?? throw scope.Fail(new NothingCompiled());
            var watch = Stopwatch.StartNew();
            await scope.Acquire(flushedConsole);

            var entryPoint = scope.Get(FindEntryPoint(bytes));
            await scope.Run(Invoke(entryPoint));
            return watch.Elapsed.TotalMilliseconds;
        });

    // Each run's assembly stays loaded for good: about 400 KB of runtime memory per run, measured.
    // A collectible AssemblyLoadContext does not unload on the browser runtime, so only a new worker
    // gives the memory back.
    private static Result<MethodInfo, RunError> FindEntryPoint(byte[] image) =>
        Assembly.Load(image).EntryPoint is { } entryPoint
            ? new Ok<MethodInfo>(AsyncBody(entryPoint))
            : new Err<RunError>(new NoEntryPoint());

    private static Effect<Unit, RunError, EmptyEnvironment> Invoke(MethodInfo entryPoint) =>
        Effect<EmptyEnvironment>.TryAsync<Unit, RunError>(
            async _ =>
            {
                object?[]? arguments = entryPoint.GetParameters().Length == 0 ? null : [Array.Empty<string>()];
                if (entryPoint.Invoke(null, arguments) is Task task)
                {
                    await task.ConfigureAwait(false);
                }

                return Unit.Value;
            },
            static exception => new ProgramFailed(
                exception is TargetInvocationException { InnerException: { } inner } ? inner : exception));

    // An async Main gets a synchronous wrapper that blocks on the task, which the single-threaded
    // browser runtime cannot do; awaiting the method the wrapper calls avoids it.
    private static MethodInfo AsyncBody(MethodInfo entryPoint)
    {
        if (typeof(Task).IsAssignableFrom(entryPoint.ReturnType))
        {
            return entryPoint;
        }

        var parameters = entryPoint.GetParameters().Select(static p => p.ParameterType).ToArray();
        return entryPoint.DeclaringType!
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m != entryPoint
                && m.Name is "Main" or "<Main>$"
                && typeof(Task).IsAssignableFrom(m.ReturnType)
                && m.GetParameters().Select(static p => p.ParameterType).SequenceEqual(parameters))
            ?? entryPoint;
    }
}
