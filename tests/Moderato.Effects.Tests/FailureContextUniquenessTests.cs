using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

[Collection(nameof(DiagnosticsCollection))]
public class FailureContextUniquenessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Timeout_CanceledGroupAndReleaseFailure_ReportsEachDefectOnce(bool gen, bool releaseFails)
    {
        // Given
        var time = new FakeTimeProvider();
        var duration = TimeSpan.FromSeconds(1);
        var failures = new[] { new IOException("first"), new IOException("second"), new IOException("release") };
        var started = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var children = Enumerable.Range(0, 2).Select(index => Effect<TimeEnvironment>.Async<int>(async _ =>
        {
            started[index].SetResult();
            await finish.Task;
            throw failures[index];
        })).ToArray();
        Effect<int, string, TimeEnvironment> body = Effect.All(children, Concurrency.Parallel).Map(static values => values.Length);
        var release = releaseFails
            ? Effect<TimeEnvironment>.Sync<Unit>(() => throw failures[2])
            : Effect<TimeEnvironment>.Succeed(Unit.Value);
        var guarded = gen
            ? Scope<string, TimeEnvironment>.Gen(async scope =>
            {
                scope.Defer(_ => release);
                return await scope.Run(body);
            })
            : Resource.Make<int, string, TimeEnvironment>(Effect<TimeEnvironment>.Succeed(1), (_, _) => release).Use(_ => body);
        var reported = new List<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;

        try
        {
            // When
            var running = guarded.Timeout(duration, static _ => "timeout").RunExitAsync(new TimeEnvironment(time), CancellationToken.None);
            await Task.WhenAll(started.Select(static signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            time.Advance(duration);
            finish.SetResult();
            var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            if (releaseFails) Assert.Same(failures[2], Assert.IsType<Die>(exit.Value).Exception);
            else Assert.Equal("timeout", Assert.IsType<Err<string>>(exit.Value).Error);
            var observed = reported.SelectMany(static context => Failures(context)).Concat(Failures(exit.Value)).ToArray();
            Assert.Equal(1, observed.Count(failure => ReferenceEquals(failure, failures[0])));
            Assert.Equal(1, observed.Count(failure => ReferenceEquals(failure, failures[1])));
            Assert.Equal(releaseFails ? 1 : 0, observed.Count(failure => ReferenceEquals(failure, failures[2])));
        }
        finally
        {
            finish.TrySetResult();
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gen_ChildDefectOverridesContextualBodyDefect_KeepsEachDefectOnce(bool bodyHasContext)
    {
        // Given
        var childFailure = new IOException("child");
        var bodyFailures = Enumerable.Range(0, bodyHasContext ? 2 : 1).Select(index => new IOException($"body {index}")).ToArray();
        var childGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = bodyFailures.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var child = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await childGo.Task;
            throw childFailure;
        });
        var body = Effect.All(bodyFailures.Select((failure, index) => Effect<EmptyEnvironment>.Async<int>(async ct =>
        {
            using var registration = ct.Register(() => canceled.TrySetResult());
            started[index].SetResult();
            await bodyGo.Task;
            throw failure;
        })).ToArray(), Concurrency.Parallel);
        var guarded = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            await scope.Fork(child);
            return (await scope.Run(body)).Length;
        });

        try
        {
            // When
            var running = guarded.RunExitAsync(CancellationToken.None);
            await Task.WhenAll(started.Select(static signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            childGo.SetResult();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            bodyGo.SetResult();
            var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Same(childFailure, die.Exception);
            var observed = Failures(exit.Value).ToArray();
            Assert.Equal(1, observed.Count(failure => ReferenceEquals(failure, childFailure)));
            foreach (var failure in bodyFailures) Assert.Equal(1, observed.Count(value => ReferenceEquals(value, failure)));
            Assert.Equal(0, die.OmittedSuppressed);
        }
        finally
        {
            childGo.TrySetResult();
            bodyGo.TrySetResult();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    public async Task Use_ReleaseOverridesContextualBodyDefect_FlattensContextAndCountsOmissionsOnce(int count)
    {
        // Given
        var bodyFailure = new IOException("body");
        var releaseFailure = new IOException("release");
        var context = Enumerable.Range(0, count).Select(index => new Suppressed(new Die(new IOException($"context {index}"), []), Relation.Parallel)).ToArray();
        var outcome = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(bodyFailure), [.. context], consequential: true, omitted: 7);
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(outcome));
        var guarded = Resource.Make(Effect<EmptyEnvironment>.Succeed(1),
            (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw releaseFailure)).Use(_ => body);

        // When
        var exit = await guarded.RunExitAsync(CancellationToken.None);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(releaseFailure, die.Exception);
        var retainedBody = Assert.IsType<Die>(die.Suppressed[0].Case.Value);
        Assert.Same(bodyFailure, retainedBody.Exception);
        Assert.Empty(retainedBody.Suppressed);
        Assert.Equal(0, retainedBody.OmittedSuppressed);
        Assert.Equal(context.Take(99), die.Suppressed.Skip(1));
        Assert.Equal(7 + Math.Max(0, count - 99), die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Gen_UnawaitedStepWithContextDuringDrain_KeepsContextOnce(bool acquire, bool bodyDies)
    {
        // Given
        var bodyFailure = new IOException("body");
        var stepFailure = new IOException("step");
        var contextFailure = new IOException("context");
        var context = new Suppressed(new Die(contextFailure, []), Relation.Parallel);
        var outcome = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(stepFailure), [context], omitted: 7);
        var completion = new TaskCompletionSource<Outcome<int, string>>();
        var step = new Effect<int, string, EmptyEnvironment>((_, _) => new(completion.Task));
        var resource = Resource.Make(step, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        CancellationTokenRegistration registration = default;
        var guarded = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() => { registration.Dispose(); return Unit.Value; }));
            registration = scope.CancellationToken.Register(() => completion.SetResult(outcome));
#pragma warning disable MEFF310
            _ = acquire ? scope.Acquire(resource) : scope.Run(step);
#pragma warning restore MEFF310
            if (bodyDies) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await guarded.RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(bodyDies ? bodyFailure : stepFailure, die.Exception);
        var observed = Failures(exit.Value).ToArray();
        Assert.Equal(1, observed.Count(failure => ReferenceEquals(failure, stepFailure)));
        Assert.Equal(1, observed.Count(failure => ReferenceEquals(failure, contextFailure)));
        Assert.Equal(bodyDies ? 1 : 0, observed.Count(failure => ReferenceEquals(failure, bodyFailure)));
        Assert.Equal(7, die.OmittedSuppressed);
    }

    private static IEnumerable<Exception> Failures(object? value)
    {
        if (value is ExitCase exit) value = exit.Value;
        if (value is not Die die) yield break;
        yield return die.Exception;
        foreach (var context in die.Suppressed)
        {
            foreach (var failure in Failures(context.Case))
                yield return failure;
        }
    }
}
