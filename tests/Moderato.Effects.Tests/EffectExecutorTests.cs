using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class EffectExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectMany_DeepMixedTypes_PreservesEnvironmentAndNeverConversions(bool pending)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Effect<int, string, int> effect = Effect<int>.AccessAsync<int>(async (environment, _) =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return environment;
        });
        for (var i = 0; i < 1024; i++)
        {
            effect = from first in effect
                     from second in Effect<int>.Succeed(new Box(first + 1))
                     select second.Value;
        }

        // When
        var firstRun = effect.RunAsync(3, CancellationToken.None);
        var secondRun = effect.RunAsync(7, CancellationToken.None);
        var firstCompleted = firstRun.IsCompletedSuccessfully;
        var secondCompleted = secondRun.IsCompletedSuccessfully;
        gate.SetResult();
        var firstResult = await firstRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var secondResult = await secondRun.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var repeated = await effect.RunAsync(11, CancellationToken.None);

        // Then
        Assert.Equal(!pending, firstCompleted);
        Assert.Equal(!pending, secondCompleted);
        Assert.Equal<Result<int, string>>(Result.Ok(1027), firstResult);
        Assert.Equal<Result<int, string>>(Result.Ok(1031), secondResult);
        Assert.Equal<Result<int, string>>(Result.Ok(1035), repeated);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1024)]
    public async Task OrElse_DynamicallyReturnsRecovery_ContinuesWithoutLosingTheResult(int depth)
    {
        // Given
        static Effect<int, int, EmptyEnvironment> Recover(int remaining)
            => ((Effect<int, int, EmptyEnvironment>)Effect<EmptyEnvironment>.Fail(remaining))
                .OrElse<int>(value => value == 0 ? Effect<EmptyEnvironment>.Succeed(42) : Recover(value - 1));
        var effect = Recover(depth);

        // When
        var running = effect.RunAsync(CancellationToken.None);
        var result = await running;

        // Then
        Assert.True(running.IsCompletedSuccessfully);
        Assert.Equal<Result<int, int>>(Result.Ok(42), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_DeepFailure_PreservesTheOriginalDefectAndBoundedContext(bool pending)
    {
        // Given
        var exception = new IOException("original");
        var context = Enumerable.Range(0, 100).Select(value => new Suppressed(new Failed(value), Relation.Sequential)).ToArray();
        var original = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(exception), [.. context], omitted: 7);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var effect = new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return original;
        });
        for (var i = 0; i < 1024; i++)
        {
            effect = effect.Map(value =>
            {
                calls++;
                return value;
            });
        }

        // When
        var running = effect.RunExitAsync(CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        gate.SetResult();
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(exception, die.Exception);
        Assert.Equal(context, die.Suppressed);
        Assert.Equal(7, die.OmittedSuppressed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RunExitAsync_DeepPendingChainCanceled_InterruptsWithoutRunningTheContinuations()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Async<int>(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return 1;
        });
        for (var i = 0; i < 1024; i++) effect = effect.Tap(_ => calls++);

        // When
        var running = effect.RunExitAsync(cancellation.Token);
        var suspended = !running.IsCompleted;
        cancellation.Cancel();
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.IsType<Interrupted>(exit.Value);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapErr_DeepMapperThrows_KeepsTheReplacedErrorAndBoundedContext(bool pending)
    {
        // Given
        var exception = new IOException("mapper");
        var context = Enumerable.Range(0, 100).Select(value => new Suppressed(new Failed(value), Relation.Sequential)).ToArray();
        var original = Outcome<int, string>.Err("original").WithSuppressed([.. context], 7);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return original;
        });
        var effect = source.MapErr<int>(_ => throw exception);
        for (var i = 0; i < 1024; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunExitAsync(CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        gate.SetResult();
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(exception, die.Exception);
        Assert.Equal(new Suppressed(new Failed("original"), Relation.Sequential), die.Suppressed[0]);
        Assert.Equal(context.Take(99), die.Suppressed.Skip(1));
        Assert.Equal(8, die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_PendingCallbackChangesSynchronizationContext_MatchesTheDirectPath(bool suppressFlow)
    {
        // Given
        var marker = new SynchronizationContext();
        var scenarios = new[] { 0, 128 }.Select(padding =>
        {
            var gate = new PendingValue<int>();
            SynchronizationContext? observed = null;
            var effect = Effect<EmptyEnvironment>.Async<int>(_ => gate.Task)
                .Map(value =>
                {
                    SynchronizationContext.SetSynchronizationContext(marker);
                    return value;
                })
                .Map(value =>
                {
                    observed = SynchronizationContext.Current;
                    return value;
                });
            for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);
            return (effect, gate, observed: (Func<SynchronizationContext?>)(() => observed));
        }).ToArray();
        var results = new List<Result<int, Never>>();

        // When
        foreach (var scenario in scenarios)
        {
            ValueTask<Result<int, Never>> running;
            if (suppressFlow)
            {
                using (ExecutionContext.SuppressFlow()) running = scenario.effect.RunAsync(CancellationToken.None);
            }
            else
            {
                running = scenario.effect.RunAsync(CancellationToken.None);
            }
            await scenario.gate.Registered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            scenario.gate.Complete(42);
            results.Add(await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        // Then
        Assert.All(results, result => Assert.Equal<Result<int, Never>>(Result.Ok(42), result));
        Assert.Same(marker, scenarios[0].observed());
        Assert.Same(scenarios[0].observed(), scenarios[1].observed());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectMany_TwoPendingSteps_PreservesAmbientContextsAcrossDynamicChildren(bool suppressFlow)
    {
        // Given
        var scenarios = new[] { 0, 128 }.Select(padding =>
        {
            var local = new AsyncLocal<string?> { Value = "caller" };
            var seen = new List<string?>();
            var first = new PendingValue<int>();
            var second = new PendingValue<int>();
            var effect = Effect<EmptyEnvironment>.Async<int>(_ => first.Task)
                .Map(value =>
                {
                    seen.Add(local.Value);
                    local.Value = "mapped";
                    return value;
                })
                .SelectMany(value =>
                {
                    seen.Add(local.Value);
                    local.Value = "binder";
                    return Effect<EmptyEnvironment>.Async<int>(_ => second.Task).Map(next =>
                    {
                        seen.Add(local.Value);
                        local.Value = "child";
                        return next;
                    });
                }, (firstValue, secondValue) =>
                {
                    seen.Add(local.Value);
                    return firstValue + secondValue;
                })
                .Map(value =>
                {
                    seen.Add(local.Value);
                    return value;
                });
            for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);
            return (effect, first, second, local, seen);
        }).ToArray();
        var results = new List<Result<int, Never>>();
        var callerValues = new List<string?>();

        // When
        foreach (var scenario in scenarios)
        {
            ValueTask<Result<int, Never>> running;
            if (suppressFlow)
            {
                using (ExecutionContext.SuppressFlow()) running = scenario.effect.RunAsync(CancellationToken.None);
            }
            else
            {
                running = scenario.effect.RunAsync(CancellationToken.None);
            }
            callerValues.Add(scenario.local.Value);
            await scenario.first.Registered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            scenario.first.Complete(2);
            await scenario.second.Registered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            scenario.second.Complete(3);
            results.Add(await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        // Then
        Assert.All(results, result => Assert.Equal<Result<int, Never>>(Result.Ok(5), result));
        Assert.Equal(["caller", "caller"], callerValues);
        Assert.Equal(scenarios[0].seen, scenarios[1].seen);
        if (!suppressFlow) Assert.Equal(["caller", "caller", "binder", "binder", "caller"], scenarios[1].seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(128)]
    public async Task AndThen_DefaultChild_ReturnsDieAndSkipsLaterCallbacks(int padding)
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Succeed(42)
            .AndThen(static _ => default(Effect<int, Never, EmptyEnvironment>))
            .Tap(_ => calls++);
        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);

        // When
        var running = effect.RunExitAsync(CancellationToken.None);
        var exit = await running;

        // Then
        Assert.True(running.IsCompletedSuccessfully);
        Assert.IsType<InvalidOperationException>(Assert.IsType<Die>(exit.Value).Exception);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Gen_ObservesADeepEffect_PreservesSuccessAndExpectedFailure(bool pending, bool fail)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            if (pending) await gate.Task.ConfigureAwait(false);
            return fail ? Result.Err("failure") : Result.Ok(1);
        });
        for (var i = 0; i < 1024; i++) source = source.Map(static value => value + 1);
        var calls = 0;
        var effect = Scope<string, EmptyEnvironment>.Gen(async scope =>
        {
            var value = await scope.Run(source);
            calls++;
            return value;
        });

        // When
        var running = effect.RunExitAsync(CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        gate.SetResult();
        var exit = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(!pending, completed);
        Assert.Equal(fail ? 0 : 1, calls);
        if (fail) Assert.Equal("failure", Assert.IsType<Err<string>>(exit.Value).Error);
        else Assert.Equal(1025, Assert.IsType<Ok<int>>(exit.Value).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ConcurrentCompletionsRaceRegistration_KeepsEachRunsStateIndependent(bool suppressFlow)
    {
        // Given
        var effect = Effect<int>.AccessAsync<int>((environment, _) => new(Task.Run(() => environment)));
        for (var i = 0; i < 512; i++) effect = effect.Map(static value => value + 1);
        effect = effect.AndThen(first => Effect<int>.AccessAsync<int>((environment, _) => new(Task.Run(() => first + environment))));
        for (var i = 0; i < 128; i++) effect = effect.Map(static value => value);
        Task<Result<int, Never>>[] runs;

        // When
        if (suppressFlow)
        {
            using (ExecutionContext.SuppressFlow())
                runs = Enumerable.Range(0, 64).Select(environment => effect.RunAsync(environment, CancellationToken.None).AsTask()).ToArray();
        }
        else
        {
            runs = Enumerable.Range(0, 64).Select(environment => effect.RunAsync(environment, CancellationToken.None).AsTask()).ToArray();
        }
        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(Enumerable.Range(0, 64).Select(environment => (Result<int, Never>)Result.Ok(512 + 2 * environment)), results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task Map_PendingCallbackChangesAsyncLocal_RestoresNestedAwaitScopesInOrder(int padding)
    {
        // Given
        var transitions = new List<(string? Previous, string? Current)>();
        var events = new List<string>();
        var local = new AsyncLocal<string?>(change =>
        {
            if (change.ThreadContextChanged)
            {
                transitions.Add((change.PreviousValue, change.CurrentValue));
                events.Add($"{change.PreviousValue}->{change.CurrentValue}");
            }
        })
        { Value = "caller" };
        var gate = new PendingValue<int>(runContinuationsAsynchronously: false);
        string? seen = null;
        var effect = Effect<EmptyEnvironment>.Async<int>(_ => gate.Task)
            .Map(value =>
            {
                events.Add("map");
                local.Value = "mapped";
                return value;
            });
        for (var i = 0; i < padding; i++) effect = effect.Map(static value => value);
        effect = effect.Map(value =>
        {
            seen = local.Value;
            events.Add("outer");
            return value;
        });

        // When
        var running = effect.RunAsync(CancellationToken.None);
        var suspended = !running.IsCompleted;
        gate.Complete(42);
        var observed = transitions.ToArray();
        var observedEvents = events.ToArray();
        var result = await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.Equal<Result<int, Never>>(Result.Ok(42), result);
        Assert.Equal("caller", seen);
        Assert.Equal(new[]
        {
            ((string?)"mapped", (string?)"caller"),
            ((string?)"caller", (string?)"mapped"),
            ((string?)"mapped", (string?)"caller")
        }, observed);
        Assert.Equal(["map", "mapped->caller", "outer", "caller->mapped", "mapped->caller"], observedEvents);
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private readonly record struct Box(int Value);
}
