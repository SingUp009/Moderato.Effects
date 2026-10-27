using Moderato.Effects.Internal;
using Moderato.Effects.Testing;

namespace Moderato.Effects.Tests;

public class AllocationTests
{
    private const int WARM_UP_RUNS = 1024;
    private const int RUNS = 4096;

    // A one-off runtime allocation can land in one window, while one made by every run lands in all of them.
    private const int WINDOWS = 3;

    public static TheoryData<AllocationCase> Cases => new(Enum.GetValues<AllocationCase>());

    public static TheoryData<SchedulingAllocationCase> SchedulingCases => new(Enum.GetValues<SchedulingAllocationCase>());

    [Fact]
    public async Task RunExitAsync_SingleReleaseFailureHasContext_AllocatesLikeEmptyContext()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var failure = new IOException("release");
        var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
        var effects = new[] { 0, 100 }.Select(count =>
        {
            var outcome = Outcome<Unit, Never>.Die(captured, [.. context.Take(count)], omitted: 3);
            var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(outcome));
            return Resource.Make(Effect<EmptyEnvironment>.Succeed(42), (_, _) => release)
                .Use(Effect<EmptyEnvironment>.Succeed);
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, Never>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(failure, die.Exception);
            Assert.Equal(context.Take(scenario * 100), die.Suppressed);
            Assert.Equal(3, die.OmittedSuppressed);
        }

        Assert.True(allocated[1] <= allocated[0], $"Empty context: {allocated[0]}; full context: {allocated[1]}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_FullPolicyContext_DoesNotAllocateMoreThanPartialPrefix(bool retry)
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var other = new Suppressed(new Failed("other"), Relation.Sequential);
        var last = retry ? Outcome<int, string>.Err("last").WithSuppressed([], 2)
            : Outcome<int, string>.Ok(42).WithSuppressed([other], 2);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(last));
        var defect = new IOException("policy");
        var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(defect);
        var effects = new[] { 99, 100 }.Select(count =>
        {
            var decision = Outcome<bool, Never>.Die(captured, [.. context.Take(count)], omitted: 3);
            var policy = new Effect<bool, Never, EmptyEnvironment>((_, _) => new(decision));
            return retry ? source.Retry(_ => policy) : source.Repeat(_ => policy);
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, string>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(defect, die.Exception);
            Assert.Equal(100, die.Suppressed.Count);
            Assert.Equal(5 + scenario, die.OmittedSuppressed);
            if (scenario == 1) Assert.Same(context[99], die.Suppressed[^1]);
            else if (retry) Assert.True(die.Suppressed[^1].Case is Failed("last"));
            else Assert.Same(other, die.Suppressed[^1]);
        }

        Assert.True(allocated[1] <= allocated[0], $"Partial policy: {allocated[0]}; full policy: {allocated[1]}");
    }

    [Fact]
    public async Task RunAsync_GroupContextExceedsLimit_AllocatesLikeRetainedPrefix()
    {
        // Given
        var earlier = Outcome<int, string>.Ok(1).WithSuppressed(
            [.. Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential))], 2);
        var retained = Outcome<int, string>.Ok(2).WithSuppressed([], 3);
        var overflowing = Outcome<int, string>.Ok(2).WithSuppressed(earlier.Suppressed, 3);
        var first = new Effect<int, string, EmptyEnvironment>((_, _) => new(earlier));
        Effect<int, string, EmptyEnvironment>[] next =
        [
            new((_, _) => new(retained)),
            new((_, _) => new(overflowing)),
        ];
        var effects = next.Select(second => Effect.All([first, second])).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Result<int[], string>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        foreach (var result in results)
        {
            Assert.True(result.TryGetOk(out var values));
            Assert.Equal([1, 2], values);
        }

        Assert.Equal(allocated[0], allocated[1]);
    }

    [Fact]
    public async Task RunExitAsync_ReplacingSaturatedErrorContext_AllocatesLikeRetainedPrefix()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel)).ToArray();
        var defect = new IOException("mapper failed");
        var effects = new[] { 99, 100 }.Select(count =>
        {
            var outcome = Outcome<int, string>.Err("original").WithSuppressed([.. context.Take(count)], 7);
            return new Effect<int, string, EmptyEnvironment>((_, _) => new(outcome)).MapErr<int>(_ => throw defect);
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, int>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(defect, die.Exception);
            Assert.Equal(100, die.Suppressed.Count);
            Assert.Equal(7 + scenario, die.OmittedSuppressed);
        }

        Assert.Equal(allocated[0], allocated[1]);
    }

    [Fact]
    public async Task RunAsync_ContextExceedsLimit_AllocatesLikeRetainedPrefix()
    {
        // Given
        var earlier = Outcome<int, string>.Ok(1).WithSuppressed(
            [.. Enumerable.Range(0, 99).Select(i => new Suppressed(new Failed(i), Relation.Sequential))], 2);
        var retained = Outcome<int, string>.Ok(42).WithSuppressed([earlier.Suppressed[0]], 3);
        var overflowing = Outcome<int, string>.Ok(42).WithSuppressed(earlier.Suppressed, 3);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(earlier));
        Effect<int, string, EmptyEnvironment>[] next =
        [
            new((_, _) => new(retained)),
            new((_, _) => new(overflowing)),
        ];
        var effects = next.Select(step => source.AndThen(_ => step)).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        Result<int, string>[] results = new Result<int, string>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.All(results, result => Assert.Equal<Result<int, string>>(Result.Ok(42), result));
        Assert.Equal(allocated[0], allocated[1]);
    }

    [Fact]
    public async Task RunAsync_ContinuationAfterSaturatedContext_AllocatesNothing()
    {
        // Given
        // Prebuilt contexts isolate continuation allocation from failure creation.
        var earlier = Outcome<int, string>.Ok(1).WithSuppressed(
            [.. Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential))], 2);
        var later = Outcome<int, string>.Ok(42).WithSuppressed(earlier.Suppressed, 3);
        var source = new Effect<int, string, EmptyEnvironment>((_, _) => new(earlier));
        var next = new Effect<int, string, EmptyEnvironment>((_, _) => new(later));
        var effect = source.AndThen(_ => next);
        await WarmUpAsync(() => effect.RunAsync(CancellationToken.None));

        // When
        var completedAtOnce = true;
        Result<int, string> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunAsync(CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal<Result<int, string>>(Result.Ok(42), result);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task RunAsync_NewConstantEffect_AllocatesNothing(int factory, bool cancelable)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancelable ? cancellation.Token : CancellationToken.None;
        Result<int, string> expected = factory is 1 or 3 ? Result.Err("failed") : Result.Ok(42);
        await WarmUpAsync(() =>
        {
            Effect<int, string, EmptyEnvironment> effect = factory switch
            {
                0 => Effect<EmptyEnvironment>.Succeed(42),
                1 => Effect<EmptyEnvironment>.Fail("failed"),
                _ => Effect<EmptyEnvironment>.FromResult(expected),
            };
            return effect.RunAsync(cancellationToken);
        });

        // When
        var completedAtOnce = true;
        Result<int, string> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                Effect<int, string, EmptyEnvironment> effect = factory switch
                {
                    0 => Effect<EmptyEnvironment>.Succeed(42),
                    1 => Effect<EmptyEnvironment>.Fail("failed"),
                    _ => Effect<EmptyEnvironment>.FromResult(expected),
                };
                var run = effect.RunAsync(cancellationToken);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task RunExitAsync_TokenAlreadyCanceled_AllocatesNothing()
    {
        // Given
        var calls = 0;
        var effect = Effect<EmptyEnvironment>.Sync(() => ++calls);
        var cancellationToken = new CancellationToken(canceled: true);
        await WarmUpAsync(() => effect.RunExitAsync(cancellationToken));

        // When
        var completedAtOnce = true;
        Exit<int, Never> exit = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunExitAsync(cancellationToken);
                completedAtOnce &= run.IsCompletedSuccessfully;
                exit = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.True(exit.TryGetValue(out Interrupted? _));
        Assert.Equal(0, calls);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RunAsync_PrebuiltSynchronousEffect_AllocatesNothing(AllocationCase scenario)
    {
        // Given
        var (effect, expected) = AllocationScenarios.Create(scenario);
        await WarmUpAsync(() => effect.RunAsync(CancellationToken.None));

        // When
        var completedAtOnce = true;
        Result<int, string> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunAsync(CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RunExitAsync_PrebuiltSynchronousEffect_AllocatesNothing(AllocationCase scenario)
    {
        // Given
        var (effect, expected) = AllocationScenarios.Create(scenario);
        await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));

        // When
        var completedAtOnce = true;
        Exit<int, string> exit = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunExitAsync(CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                exit = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(ToExit(expected), exit);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ValueTypeEnvironmentAndError_AllocatesNothing(bool fail)
    {
        // Given
        Result<int, int> input = fail ? Result.Err(7) : Result.Ok(42);
        Result<int, int> expected = fail ? Result.Err(8) : Result.Ok(43);
        var effect = Effect<int>.FromResult(input).Map(static x => x + 1).MapErr(static error => error + 1);
        await WarmUpAsync(() => effect.RunAsync(0, CancellationToken.None));

        // When
        var completedAtOnce = true;
        Result<int, int> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunAsync(0, CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [MemberData(nameof(SchedulingCases))]
    public async Task RunAsync_BuiltInScheduleWithoutWaiting_AllocatesNothing(SchedulingAllocationCase scenario)
    {
        // Given
        var (effect, expected) = SchedulingAllocationScenarios.Create(scenario);
        await WarmUpAsync(() => effect.RunAsync(CancellationToken.None));

        // When
        var completedAtOnce = true;
        Result<int, string> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunAsync(CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [MemberData(nameof(SchedulingCases))]
    public async Task RunExitAsync_BuiltInScheduleWithoutWaiting_AllocatesNothing(SchedulingAllocationCase scenario)
    {
        // Given
        var (effect, expected) = SchedulingAllocationScenarios.Create(scenario);
        await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));

        // When
        var completedAtOnce = true;
        Exit<int, string> exit = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunExitAsync(CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                exit = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(ToExit(expected), exit);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_ValueTypeClock_AllocatesNothing(int operation)
    {
        // Given
        var (effect, expected) = ValueClockAllocationScenarios.Create(operation);
        await WarmUpAsync(() => effect.RunAsync(default, CancellationToken.None));

        // When
        var completedAtOnce = true;
        Result<int, string> result = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunAsync(default, CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                result = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(expected, result);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunExitAsync_ValueTypeClock_AllocatesNothing(int operation)
    {
        // Given
        var (effect, expected) = ValueClockAllocationScenarios.Create(operation);
        await WarmUpAsync(() => effect.RunExitAsync(default, CancellationToken.None));

        // When
        var completedAtOnce = true;
        Exit<int, string> exit = default;
        var allocated = long.MaxValue;
        for (var window = 0; window < WINDOWS && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < RUNS; i++)
            {
                var run = effect.RunExitAsync(default, CancellationToken.None);
                completedAtOnce &= run.IsCompletedSuccessfully;
                exit = await run;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Then
        Assert.True(completedAtOnce);
        Assert.Equal(ToExit(expected), exit);
        Assert.Equal(0L, allocated);
    }

    // Warm-up first, so one-time static, delegate and JIT allocations stay out of the measurement.
    [Fact]
    public async Task RunExitAsync_FullPrimaryContext_DoesNotAllocateMoreThanPartialPrefix()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var other = new Suppressed(new Failed("other"), Relation.Parallel);
        var earlier = Outcome<int, string>.Ok(1).WithSuppressed([other], 3);
        var first = new Effect<int, string, EmptyEnvironment>((_, _) => new(earlier));
        var defect = new IOException("primary");
        var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(defect);
        var effects = new[] { 99, 100 }.Select(count =>
        {
            var outcome = Outcome<int, string>.Die(captured, [.. context.Take(count)], omitted: 2);
            var primary = new Effect<int, string, EmptyEnvironment>((_, _) => new(outcome));
            return Effect.All([first, primary]);
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int[], string>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(defect, die.Exception);
            Assert.Equal(100, die.Suppressed.Count);
            Assert.Equal(5 + scenario, die.OmittedSuppressed);
            Assert.Same(scenario == 0 ? other : context[99], die.Suppressed[^1]);
        }

        Assert.True(allocated[1] <= allocated[0], $"Partial prefix: {allocated[0]}; full prefix: {allocated[1]}");
    }

    [Fact]
    public async Task RunExitAsync_ReleaseContextExceedsLimit_AllocatesLikeRetainedPrefix()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var full = Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([.. context], 2);
        var empty = Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([], 2);
        var first = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(full));
        var failure = new IOException("body");
        var bodyOutcome = Outcome<int, Never>.Die(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure), omitted: 3);
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var effects = new[] { empty, full }.Select(outcome =>
        {
            var second = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(outcome));
            return Scope<Never, EmptyEnvironment>.Gen(async scope =>
            {
                scope.Defer(_ => second);
                scope.Defer(_ => first);
                return await scope.Run(body);
            });
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, Never>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(failure, die.Exception);
            Assert.Equal(context, die.Suppressed);
            Assert.Equal(7 + scenario * 100, die.OmittedSuppressed);
        }

        Assert.True(allocated[1] <= allocated[0], $"Retained prefix: {allocated[0]}; overflowing: {allocated[1]}");
    }

    [Fact]
    public async Task RunExitAsync_FailedReleaseContextIsDiscarded_AllocatesLikeEmptyContext()
    {
        // Given
        var context = Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var bodyFailure = new IOException("body");
        var bodyOutcome = Outcome<int, Never>.Die(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFailure), [.. context], omitted: 3);
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var releaseFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new IOException("release"));
        var effects = new[] { 0, 100 }.Select(count =>
        {
            var outcome = Outcome<Unit, Never>.Die(releaseFailure, [.. context.Take(count)], omitted: 2);
            var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(outcome));
            var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => release);
            return resource.AndThen(_ => resource).Use(_ => body);
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, Never>[2];

        // When
        var completedAtOnce = true;
        for (var scenario = 0; scenario < effects.Length; scenario++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[scenario].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[scenario] = await run;
                }

                allocated[scenario] = Math.Min(allocated[scenario], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var scenario = 0; scenario < results.Length; scenario++)
        {
            Assert.True(results[scenario].TryGetValue(out Die? die));
            Assert.Same(bodyFailure, die.Exception);
            Assert.Equal(context, die.Suppressed);
            Assert.Equal(9 + scenario * 200, die.OmittedSuppressed);
        }

        Assert.True(allocated[1] <= allocated[0], $"Empty release context: {allocated[0]}; full release context: {allocated[1]}");
    }

    [Theory]
    [InlineData("Group")]
    [InlineData("Gen")]
    [InlineData("CarriedRelease")]
    [InlineData("FailedRelease")]
    public async Task RunExitAsync_ContextFillsBuffer_DoesNotAllocateMoreThanPartialBuffer(string scenario)
    {
        // Given
        var context = Enumerable.Range(0, 8).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var failure = new IOException("primary");
        var releaseFailure = new IOException("release");
        var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
        var bodyOutcome = Outcome<int, Never>.Die(captured, omitted: 3);
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var effects = new[] { 7, 8 }.Select(count =>
        {
            var stepOutcome = Outcome<int, Never>.Ok(42).WithSuppressed([.. context.Take(count)], 2);
            var step = new Effect<int, Never, EmptyEnvironment>((_, _) => new(stepOutcome));
            var releaseOutcome = scenario == "FailedRelease"
                ? Outcome<Unit, Never>.Die(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(releaseFailure), [.. context.Take(count - 1)], omitted: 2)
                : Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([.. context.Take(count)], 2);
            var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(releaseOutcome));
            return scenario switch
            {
                "Group" => Effect.All([step, body]).Map(static _ => 42),
                "Gen" => Scope<Never, EmptyEnvironment>.Gen(async scope =>
                {
                    await scope.Run(step);
                    return await scope.Run(body);
                }),
                _ => Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => release).Use(_ => body),
            };
        }).ToArray();
        foreach (var effect in effects) await WarmUpAsync(() => effect.RunExitAsync(CancellationToken.None));
        long[] allocated = [long.MaxValue, long.MaxValue];
        var results = new Exit<int, Never>[2];

        // When
        var completedAtOnce = true;
        for (var shape = 0; shape < effects.Length; shape++)
        {
            for (var window = 0; window < WINDOWS; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < RUNS; i++)
                {
                    var run = effects[shape].RunExitAsync(CancellationToken.None);
                    completedAtOnce &= run.IsCompletedSuccessfully;
                    results[shape] = await run;
                }

                allocated[shape] = Math.Min(allocated[shape], GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Then
        Assert.True(completedAtOnce);
        for (var shape = 0; shape < results.Length; shape++)
        {
            Assert.True(results[shape].TryGetValue(out Die? die));
            Assert.Same(failure, die.Exception);
            Assert.Equal(7 + shape, die.Suppressed.Count);
            Assert.Equal(5, die.OmittedSuppressed);
            if (scenario == "FailedRelease")
            {
                Assert.Same(releaseFailure, Assert.IsType<Die>(die.Suppressed[0].Case.Value).Exception);
                Assert.Equal(context.Take(6 + shape), die.Suppressed.Skip(1));
            }
            else
            {
                Assert.Equal(context.Take(7 + shape), die.Suppressed);
            }
        }

        Assert.True(allocated[1] <= allocated[0], $"Partial buffer: {allocated[0]}; full buffer: {allocated[1]}");
    }

    private static async Task WarmUpAsync<T>(Func<ValueTask<T>> run)
    {
        for (var i = 0; i < WARM_UP_RUNS; i++) await run();
    }

    private static Exit<int, string> ToExit(Result<int, string> expected)
        => expected.TryGetOk(out var value) ? new(new Ok<int>(value))
            : expected.TryGetErr(out var error) ? new(new Err<string>(error))
            : throw new InvalidOperationException("The scenario must have an initialized expected result.");
}
