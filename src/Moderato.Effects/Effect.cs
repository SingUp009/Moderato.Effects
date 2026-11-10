using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>A lazy, rerunnable description of work that needs <typeparamref name="R"/> and ends with <typeparamref name="T"/> or <typeparamref name="E"/>; <c>default</c> is invalid.</summary>
/// <remarks>Equality and hash codes depend on internal representation and do not establish equivalent computations.</remarks>
[DebuggerDisplay("Effect<{typeof(T).Name,nq}, {typeof(E).Name,nq}, {typeof(R).Name,nq}>")]
public readonly partial struct Effect<T, E, R>
{
    private const string RUN_OPTIONS_JUSTIFICATION = "The original overload preserves compatibility; explicit options require a token to keep default arguments unambiguous.";

    private readonly Result<T, E> constant;
    private readonly Func<R, RunContext, ValueTask<Outcome<T, E>>>? run;

    private readonly string? trackingName;
    private readonly string? trackingLocation;

    internal Effect(Func<R, RunContext, ValueTask<Outcome<T, E>>> run, [CallerMemberName] string? name = null)
    {
        this.run = run;
        trackingName = name;
    }

    // The delegate target carries instructions without growing each Effect value.
    internal Effect(EffectInstruction<T, E, R> instruction, [CallerMemberName] string? name = null)
    {
        run = instruction.Run;
        trackingName = name;
    }

    internal Effect(Result<T, E> constant, [CallerMemberName] string? name = null)
    {
        this.constant = constant;
        trackingName = name;
    }

    private Effect(Effect<T, E, R> source, string name, string location)
    {
        constant = source.constant;
        run = source.run;
        trackingName = name;
        trackingLocation = location;
    }

    /// <summary>Names an operation in execution recordings without changing its work.</summary>
    public Effect<T, E, R> WithName(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(Validate(), name, $"{Path.GetFileName(file)}:{line}");
    }

    internal string TrackingName => trackingName ?? "Effect";

    internal Effect<T, E, R> Validate()
        => constant.HasValue || run is not null ? this : ThrowHelper.ThrowUninitializedEffect<Effect<T, E, R>>();

    // Opaque async scopes call this entry; an extra dispatch frame reduces their nesting headroom.
    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context)
        => context.Observation?.HasRecording == true ? RunTracked(environment, context, null) : RunCore(environment, context);

    internal ValueTask<Outcome<T, E>> RunCore(R environment, RunContext context)
        => constant.HasValue ? new(Steps.FromResult(constant, context))
            : (run ?? ThrowHelper.ThrowUninitializedEffect<Func<R, RunContext, ValueTask<Outcome<T, E>>>>())(environment, context);

    internal ValueTask<Outcome<T, E>> Run(R environment, RunContext context, int depth)
    {
        if (context.Observation?.HasRecording == true && (depth != 0 || run?.Target is not EffectInstruction<T, E, R>)) return RunTracked(environment, context, depth);
        return RunDirectCore(environment, context, depth);
    }

    private ValueTask<Outcome<T, E>> RunDirectCore(R environment, RunContext context, int depth)
    {
        if (constant.HasValue) return new(Steps.FromResult(constant, context));
        if (run?.Target is EffectInstruction<T, E, R> instruction)
            return depth == 0 ? EffectExecutor.Run(this, environment, context) : instruction.RunDirect(environment, context, depth - 1);
        return (run ?? ThrowHelper.ThrowUninitializedEffect<Func<R, RunContext, ValueTask<Outcome<T, E>>>>())(environment, context);
    }

    internal EffectFrame<T, E> Enter(R environment, RunContext context, EffectFrame? parent)
        => context.Observation?.HasRecording == true ? new TrackingFrame<T, E, R>(this, environment, context, parent) : EnterCore(environment, context, parent);

    internal EffectFrame<T, E> EnterCore(R environment, RunContext context, EffectFrame? parent)
        => run?.Target is EffectInstruction<T, E, R> instruction
            ? instruction.Enter(environment, context, parent) : new LeafFrame<T, E, R>(this, environment, context, parent);

    /// <summary>Continues with the outcome and construction-time state; thrown callbacks become defects.</summary>
    internal Effect<U, F, R> Then<U, F, TState>(TState state,
        Func<Outcome<T, E>, R, RunContext, TState, ValueTask<Outcome<U, F>>> next, [CallerMemberName] string? name = null)
        => new(new ThenInstruction<T, E, U, F, R, TState>(Validate(), state, next), name);

    internal Effect<V, G, R> Bind<U, F, V, G, TState>(TState state,
        Func<Outcome<T, E>, TState, RunContext, EffectSelection<U, F, V, G, R>> select,
        Func<Outcome<U, F>, Outcome<T, E>, RunContext, TState, Outcome<V, G>> project, bool isolate = false, [CallerMemberName] string? name = null)
        => new(new BindInstruction<T, E, U, F, V, G, R, TState>(Validate(), state, select, project, isolate), name);

    /// <summary>An effect that cannot fail can stand in for one with any error type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<T, Never, R> effect)
    {
        effect = effect.Validate();
        if (effect.constant.TryGetOk(out var value)) return new(new Result<T, E>(new Ok<T>(value)));
        return Deferred(effect);

        static Effect<T, E, R> Deferred(Effect<T, Never, R> effect)
            => effect.Then<T, E, Unit>(Unit.Value, static (outcome, _, _, _) => new(outcome.WithErrorType<E>()));
    }

    /// <summary>An effect that cannot succeed can stand in for one with any value type.</summary>
    public static implicit operator Effect<T, E, R>(Effect<Never, E, R> effect)
    {
        effect = effect.Validate();
        if (effect.constant.TryGetErr(out var error)) return new(new Result<T, E>(new Err<E>(error)));
        return Deferred(effect);

        static Effect<T, E, R> Deferred(Effect<Never, E, R> effect)
            => effect.Then<T, E, Unit>(Unit.Value, static (outcome, _, _, _) => new(outcome.WithValueType<T>()));
    }

    /// <summary>An effect that ends in the result.</summary>
    public static implicit operator Effect<T, E, R>(Result<T, E> result) => Effect<R>.FromResult(result);

    /// <summary>Runs the effect, rethrowing defects as thrown and the caller's cancellation as <see cref="OperationCanceledException"/>.</summary>
    /// <param name="cancellationToken">Cancels the run, which sees a token derived from it.</param>
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = RUN_OPTIONS_JUSTIFICATION)]
    public ValueTask<Result<T, E>> RunAsync(R environment, CancellationToken cancellationToken = default)
        => RunResult(environment, new RunContext(cancellationToken));

    /// <summary>Runs the effect with diagnostics scoped to this execution.</summary>
    public ValueTask<Result<T, E>> RunAsync(R environment, CancellationToken cancellationToken, EffectRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunResult(environment, new RunContext(cancellationToken, options.UnobservedFailureHandler));
    }

    private ValueTask<Result<T, E>> RunResult(R environment, RunContext context)
    {
        Validate();
        if (EffectObservation.Enabled) return RunResultObserved(environment, context);
        var cancellationToken = context.CancellationToken;
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<Result<T, E>>(cancellationToken);
        if (constant.HasValue) return new(constant);

        try
        {
            var task = Start(environment, context);
            return task.IsCompletedSuccessfully ? ToResult(task.Result, context) : Await(task, context);
        }
        catch (Exception exception)
        {
            return ToResult(Defects.From<T, E>(exception, context), context);
        }

        static async ValueTask<Result<T, E>> Await(ValueTask<Outcome<T, E>> task, RunContext context)
            => await ToResult(await task.ConfigureAwait(false), context).ConfigureAwait(false);
    }

    /// <summary>Runs the effect and returns how it ended, without throwing.</summary>
    /// <param name="cancellationToken">Cancels the run, which sees a token derived from it.</param>
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = RUN_OPTIONS_JUSTIFICATION)]
    public ValueTask<Exit<T, E>> RunExitAsync(R environment, CancellationToken cancellationToken = default)
        => RunExit(environment, new RunContext(cancellationToken));

    /// <summary>Runs the effect with scoped diagnostics and returns how it ended.</summary>
    public ValueTask<Exit<T, E>> RunExitAsync(R environment, CancellationToken cancellationToken, EffectRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunExit(environment, new RunContext(cancellationToken, options.UnobservedFailureHandler));
    }

    private ValueTask<Exit<T, E>> RunExit(R environment, RunContext context)
    {
        Validate();
        if (EffectObservation.Enabled) return RunExitObserved(environment, context);
        var cancellationToken = context.CancellationToken;
        if (cancellationToken.IsCancellationRequested) return new(new Exit<T, E>(ExitCases.Interrupted));
        if (constant.HasValue) return new(ToExit(Steps.FromResult(constant, context), context));

        try
        {
            var task = Start(environment, context);
            return task.IsCompletedSuccessfully ? new(ToExit(task.Result, context)) : Await(task, context);
        }
        catch (Exception exception)
        {
            return new(ToExit(Defects.From<T, E>(exception, context), context));
        }

        static async ValueTask<Exit<T, E>> Await(ValueTask<Outcome<T, E>> task, RunContext context)
        {
            try
            {
                return ToExit(await task.ConfigureAwait(false), context);
            }
            catch (Exception exception)
            {
                return ToExit(Defects.From<T, E>(exception, context), context);
            }
        }
    }

    internal EffectObservation? BeginTracking(RunContext context)
        => EffectObservation.Begin("op", context, TrackingName, location: trackingLocation);

    private ValueTask<Outcome<T, E>> RunTracked(R environment, RunContext context, int? depth)
    {
        var node = BeginTracking(context);
        try
        {
            var child = context.WithObservation(node);
            var task = depth is { } remaining ? RunDirectCore(environment, child, remaining) : RunCore(environment, child);
            return TraceTasks.Observe(task, node);
        }
        catch (Exception exception)
        {
            if (node is not null) node.Fault(exception);
            throw;
        }
    }

    private ValueTask<Outcome<T, E>> Start(R environment, RunContext context)
    {
        if (!context.CancellationToken.CanBeCanceled) return Run(environment, context);
        return Isolated(this, environment, context);

        static async ValueTask<Outcome<T, E>> Isolated(Effect<T, E, R> effect, R environment, RunContext context)
        {
            using var caller = Cancellation.ForCaller(context);
            var body = caller.Context;
            Outcome<T, E> outcome;
            try
            {
                outcome = await effect.Run(environment, body).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outcome = Defects.From<T, E>(exception, body);
            }

            return caller.WithCallbackFailure(outcome);
        }
    }

    private static ValueTask<Result<T, E>> ToResult(Outcome<T, E> outcome, RunContext context)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                // Result cannot carry suppressed failures, so their defects are reported here.
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                return new(new Result<T, E>(new Ok<T>(outcome.Value)));
            case OutcomeKind.Err:
                UnobservedFailures.ReportDefects(outcome.Suppressed, context);
                return new(new Result<T, E>(new Err<E>(outcome.Error)));
            case OutcomeKind.Die:
                var exception = outcome.Defect!.SourceException;
                AttachSuppressed(exception, outcome.Suppressed, context);
                return ValueTask.FromException<Result<T, E>>(exception);
            default:
                UnobservedFailures.ReportInterrupted(outcome.Suppressed, context);
                // An inner cancellation can interrupt the run while the caller's token is not canceled.
                return ValueTask.FromCanceled<Result<T, E>>(context.CancellationToken.IsCancellationRequested
                    ? context.CancellationToken : new CancellationToken(canceled: true));
        }
    }

    private static Exit<T, E> ToExit(Outcome<T, E> outcome, RunContext context) => Exits.From(outcome, context);

    // Overwritten, so a rethrown exception does not accumulate stale entries.
    private static void AttachSuppressed(Exception exception, ImmutableArray<Suppressed> suppressed, RunContext context)
    {
        try
        {
            if (suppressed.IsEmpty) exception.Data.Remove(SUPPRESSED_DATA_KEY);
            else exception.Data[SUPPRESSED_DATA_KEY] = suppressed;
        }
        catch (Exception metadataFailure)
        {
            // Metadata failures must not replace the exception being rethrown.
            UnobservedFailures.Report(new Die(metadataFailure, suppressed), context);
        }
    }

    /// <summary>The <see cref="Exception.Data"/> key holding a rethrown defect's suppressed failures.</summary>
    public const string SUPPRESSED_DATA_KEY = "Moderato.Effects.Suppressed";
}
