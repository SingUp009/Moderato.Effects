namespace Moderato.Effects.Tests;

public class CompositionBoundaryLawsTests
{
    [Fact]
    public async Task AllErrors_ReverseErrorObservation_CollectsErrorsInInputOrder()
    {
        // Given
        // Observation: observe the complete error array, not member origin or group completion order.
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<string>();
        var a = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await middle.Task;
            return Result.Err("a");
        }).TapErr(error => observed.Add(error));
        var b = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await last.Task;
            return Result.Err("b");
        }).TapErr(error => { observed.Add(error); middle.SetResult(); });
        Effect<int, string, EmptyEnvironment> c = Effect<EmptyEnvironment>.Fail("c");
        c = c.TapErr(error => { observed.Add(error); last.SetResult(); });

        // When
        var exit = await Effect.AllErrors([a, b, c], Concurrency.Parallel).RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<string[]> errors));
        Assert.Equal(["a", "b", "c"], errors.Error);
        Assert.Equal(["c", "b", "a"], observed);
    }

    [Fact]
    public async Task AllErrors_CompletedAggregate_UsesANewFailureOrigin()
    {
        // Given
        // Observation: observe the outer primary failure; an aggregate is new, not its earliest member's failure.
        var member = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aggregated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[]? collected = null;
        Effect<int, string, EmptyEnvironment> a = Effect<EmptyEnvironment>.Fail("a");
        a = a.TapErr(_ => member.SetResult());
        var b = Effect<EmptyEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await finish.Task;
            return Result.Err("b");
        });
        var aggregate = Effect.AllErrors([a, b], Concurrency.Parallel)
            .TapErr(errors => { collected = errors; aggregated.SetResult(); });
        var outside = Effect<EmptyEnvironment>.FromResultAsync<int[], string[]>(async _ =>
        {
            await member.Task;
            return Result.Err(new[] { "outside" });
        });
        Effect<Unit, string[], EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
        var heldOutside = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
        {
            finish.SetResult();
            await aggregated.Task;
            return Unit.Value;
        })).Use(_ => outside);

        // When
        var exit = await Effect.Race(aggregate, heldOutside).RunExitAsync(CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.NotNull(collected);
        Assert.Equal(["a", "b"], collected);
        Assert.True(exit.TryGetValue(out Err<string[]> error));
        Assert.Equal(["outside"], error.Error);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("body")]
    [InlineData("acquire")]
    [InlineData("release")]
    public async Task ResourceAndThen_Bracketing_PreservesAcquisitionAndReverseReleaseOrder(string failureAt)
    {
        // Given
        // Observation: observe public Exit plus acquisition/use/release order for dependent resources.
        var trace = new List<string>();
        var defect = new IOException("release");
        Resource<int, string, EmptyEnvironment> Acquire(int value)
        {
            Effect<int, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Sync(() => { trace.Add($"acquire {value}"); return value; });
            if (failureAt == "acquire" && value == 2) acquire = acquire.AndThen<int>(_ => Effect<EmptyEnvironment>.Fail("acquire"));
            return Resource.Make(acquire, (item, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                trace.Add($"release {item}");
                if (failureAt == "release" && item == 2) throw defect;
                return Unit.Value;
            }));
        }
        Resource<int, string, EmptyEnvironment> F(int value) => Acquire(value + 1);
        Resource<int, string, EmptyEnvironment> G(int value) => Acquire(value + 1);
        Effect<int, string, EmptyEnvironment> Use(int value)
        {
            trace.Add($"use {value}");
            return failureAt == "body" ? Effect<EmptyEnvironment>.Fail("body") : Effect<EmptyEnvironment>.Succeed(value);
        }
        var source = Acquire(1);

        // When
        var left = await source.AndThen(F).AndThen(G).Use(Use).RunExitAsync(CancellationToken.None);
        var leftTrace = trace.ToArray();
        trace.Clear();
        var right = await source.AndThen(value => F(value).AndThen(G)).Use(Use).RunExitAsync(CancellationToken.None);

        // Then
        if (failureAt == "release")
        {
            foreach (var exit in new[] { left, right })
            {
                Assert.True(exit.TryGetValue(out Die? die));
                Assert.Same(defect, die.Exception);
                Assert.Empty(die.Suppressed);
            }
        }
        else
        {
            Assert.Equal(left, right);
            Assert.Equal<Exit<int, string>>(failureAt == "none" ? new Ok<int>(3) : new Err<string>(failureAt), left);
        }
        Assert.Equal(failureAt == "acquire" ? ["acquire 1", "acquire 2", "release 1"]
            : new[] { "acquire 1", "acquire 2", "acquire 3", "use 3", "release 3", "release 2", "release 1" }, leftTrace);
        Assert.Equal(leftTrace, trace);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 3)]
    public async Task Retry_NestedFiniteSchedules_MultipliesAttemptCounts(int m, int n)
    {
        // Given
        // Observation: observe final error and attempt count with immediate finite schedules and no cancellation.
        var attempts = 0;
        var failure = Effect<TimeEnvironment>.FromResultAsync<int, string>(_ =>
        {
            attempts++;
            return new(Result.Err("failed"));
        });
        var env = new TimeEnvironment(new RecordingClock());

        // When
        var exit = await failure.Retry(Schedule.Immediately.UpTo(m)).Retry(Schedule.Immediately.UpTo(n))
            .RunExitAsync(env, CancellationToken.None);

        // Then
        Assert.Equal<Exit<int, string>>(new Err<string>("failed"), exit);
        Assert.Equal((m + 1) * (n + 1), attempts);
    }

    [Fact]
    public async Task Repeat_ZeroAdditionalRepeats_RunsOnceAndReturnsItsValue()
    {
        // Given
        // Observation: observe the value and count; UpTo counts repeats, not the initial execution.
        var calls = 0;
        var effect = Effect<TimeEnvironment>.Sync(() => ++calls);

        // When
        var exit = await effect.Repeat(Schedule.Immediately.UpTo(0))
            .RunExitAsync(new TimeEnvironment(new RecordingClock()), CancellationToken.None);

        // Then
        Assert.Equal<Exit<int, Never>>(new Ok<int>(1), exit);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task All_IndependentFailureDuringCleanup_RegroupingPreservesPrimaryAndClassification()
    {
        // Given
        // Observation: same independent failures; observe primary and relation, not arbitrary Race associativity or context chronology.
        var defect = new IOException("later independent defect");
        Effect<int[], string, EmptyEnvironment> Build(bool nested)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Effect<int, string, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail("early");
            Effect<Unit, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(Unit.Value);
            var a = Resource.Make(acquire, (_, _) => Effect<EmptyEnvironment>.Async(async _ =>
            {
                held.SetResult();
                await finish.Task;
                return Unit.Value;
            })).Use(_ => failure);
            Effect<int, string, EmptyEnvironment> b = Effect<EmptyEnvironment>.Async<int>(async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            });
            Effect<int, string, EmptyEnvironment> c = Effect<EmptyEnvironment>.Async<int>(async ct =>
            {
                _ = ct.Register(() => finish.TrySetResult());
                await Task.WhenAll(held.Task, started.Task);
                throw defect;
            });
            return nested
                ? Effect.All([Effect.All([a, b], Concurrency.Parallel).Map(static values => values.Length), c], Concurrency.Parallel)
                : Effect.All([a, b, c], Concurrency.Parallel);
        }
        var flat = Build(false);
        var nested = Build(true);

        // When
        var flatExit = await flat.RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var nestedExit = await nested.RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        foreach (var exit in new[] { flatExit, nestedExit })
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(defect, die.Exception);
            var context = Assert.Single(die.Suppressed);
            Assert.Equal("early", Assert.IsType<Failed>(context.Case.Value).Error);
            Assert.Equal(Relation.Parallel, context.Relation);
        }
    }
}

[Collection(nameof(DiagnosticsCollection))]
public class RecoveryLawDiagnosticsTests
{
    [Fact]
    public async Task OrElse_FailRightUnit_PreservesExitButReportsAndConsumesDefectContext()
    {
        // Given
        // Observation: public Exit is equal, but recovery reports context before the continuation; these observations are distinct.
        var defect = new IOException("context");
        var trace = new List<string>();
        var reported = new List<ExitCase>();
        var failure = Moderato.Effects.Internal.Outcome<int, string>.Err("error").WithSuppressed([new Suppressed(new Die(defect, []), Relation.Sequential)], 0);
        Effect<int, string, EmptyEnvironment> source = new((_, _) => new(failure));
        EffectDiagnostics.UnobservedFailureHandler = item => { reported.Add(item); trace.Add("report"); };
        try
        {
            // When
            var original = await source.RunExitAsync(CancellationToken.None);
            var reportsFromOriginal = reported.ToArray();
            trace.Clear();
            reported.Clear();
            var recovered = await source.OrElse<string>(static error => Effect<EmptyEnvironment>.Fail(error))
                .TapErr(_ => trace.Add("after recovery")).RunExitAsync(CancellationToken.None);

            // Then
            Assert.Equal<Exit<int, string>>(new Err<string>("error"), original);
            Assert.Equal(original, recovered);
            Assert.Same(defect, Assert.IsType<Die>(Assert.Single(reportsFromOriginal).Value).Exception);
            Assert.Same(defect, Assert.IsType<Die>(Assert.Single(reported).Value).Exception);
            Assert.Equal(new[] { "report", "after recovery" }, trace);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }
}
