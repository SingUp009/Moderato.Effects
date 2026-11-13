using System.Text.RegularExpressions;

namespace Moderato.Effects.Tests;

[Collection("Global effect tracker")]
public sealed class EffectRecordingTests
{
    [Fact]
    public async Task Record_RetryWithoutScopes_RetainsEachAttempt()
    {
        // Given
        using var recording = EffectTracker.Record();
        Effect<int, ReadUnavailable, EmptyEnvironment> source = Effect<EmptyEnvironment>.Fail(new ReadUnavailable());
        var effect = source.WithName("Read").Retry(context => Effect<EmptyEnvironment>.Succeed(context.Attempt < 3));

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Err<ReadUnavailable> _));
        var tree = recording.GetTree();
        Assert.Equal(3, Regex.Matches(tree, "attempt #").Count);
        Assert.DoesNotContain("scope #", tree);
        Assert.Contains("incomplete no", tree);
        Assert.DoesNotContain("Running", tree);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("value")]
    [InlineData("environment")]
    public async Task Record_NamedConstantWidened_KeepsItsNameAndLocation(string widened)
    {
        // Given
        using var recording = EffectTracker.Record();
        var succeed = Effect<EmptyEnvironment>.Succeed(1).WithName("Constant");
        var fail = Effect<EmptyEnvironment>.Fail("unavailable").WithName("Constant");
        Effect<int, string, EmptyEnvironment> effect = widened switch
        {
            "error" => succeed,
            "value" => fail,
            _ => succeed.WidenEnv<EmptyEnvironment>(),
        };

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(widened == "value" ? new Err<string>("unavailable") : new Ok<int>(1), exit);
        var entry = Assert.Single(recording.GetSnapshot().Entries, static entry => entry.Kind == "op");
        Assert.Equal("Constant", entry.Name);
        Assert.StartsWith("EffectRecordingTests.cs:", entry.Location);
    }

    [Fact]
    public async Task Record_UnnamedConstantWidened_KeepsItsFactoryName()
    {
        // Given
        using var recording = EffectTracker.Record();
        Effect<int, string, EmptyEnvironment> effect = Effect<EmptyEnvironment>.Succeed(1);

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.Equal(new Ok<int>(1), exit);
        var entry = Assert.Single(recording.GetSnapshot().Entries, static entry => entry.Kind == "op");
        Assert.Equal("Succeed", entry.Name);
        Assert.Null(entry.Location);
    }

    [Theory]
    [InlineData("Timeout", 5)]
    [InlineData("Timeout", 0)]
    [InlineData("TimeoutResult", 5)]
    [InlineData("TimeoutResult", 0)]
    public async Task Record_Timeout_UsesThePublicOperationName(string operation, int seconds)
    {
        // Given
        using var recording = EffectTracker.Record();
        var source = Effect<DefaultEnvironment>.Sync(static () => 1);
        var after = TimeSpan.FromSeconds(seconds);
        var effect = operation == "Timeout"
            ? source.Timeout(after).Map(static _ => Unit.Value)
            : source.TimeoutResult(after).Map(static _ => Unit.Value).MapErr(static _ => new TimeoutError(TimeSpan.Zero));

        // When
        await effect.RunExitAsync(DefaultEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        var names = recording.GetSnapshot().Entries.Where(static entry => entry.Kind == "op").Select(static entry => entry.Name).ToArray();
        Assert.Contains(operation, names);
        Assert.DoesNotContain("Create", names);
    }

    [Theory]
    [InlineData("implicit", "Widen")]
    [InlineData("widen", "Widen")]
    [InlineData("select", "Select")]
    [InlineData("selectMany", "SelectMany")]
    [InlineData("query", "SelectMany")]
    [InlineData("catchCase", "CatchCase")]
    [InlineData("map", "Map")]
    [InlineData("mapErr", "MapErr")]
    [InlineData("andThen", "AndThen")]
    public async Task Record_OperatorBuiltOnAnother_UsesTheMemberTheUserWrote(string composition, string expected)
    {
        // Given
        using var recording = EffectTracker.Record();
        var source = Effect<EmptyEnvironment>.Sync(static () => 1).WithName("Source");
        Effect<int, NotFound, EmptyEnvironment> typed = Effect<EmptyEnvironment>.Sync(static () => 1).WithName("Source").MapErr(static _ => new NotFound(1));
        Effect<int, AppError, EmptyEnvironment> effect = composition switch
        {
            "implicit" => source,
            "widen" => typed.Widen<AppError>(),
            "select" => from value in typed.Widen<AppError>() select value + 1,
            "selectMany" => typed.Widen<AppError>().SelectMany(static value => (Effect<int, AppError, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(value)),
            "query" => from first in typed.Widen<AppError>() from second in typed.Widen<AppError>() select first + second,
            "catchCase" => typed.Widen<AppError>().CatchCase((NotFound _) => (Effect<int, AppError, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(0)),
            "map" => typed.Widen<AppError>().Map(static value => value + 1),
            "mapErr" => typed.MapErr(static error => (AppError)error),
            _ => typed.Widen<AppError>().AndThen(static value => (Effect<int, AppError, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(value)),
        };

        // When
        var exit = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Ok<int> _));
        var entries = recording.GetSnapshot().Entries.Where(static entry => entry.Kind == "op").ToArray();
        Assert.Equal(expected, entries[0].Name);
        Assert.Contains(entries, static entry => entry.Name == "Source");
        Assert.DoesNotContain(entries, static entry => entry.Name is "op_Implicit" or "Bind");
    }

    [Fact]
    public async Task Record_FailedBodyAndBlockedRelease_ShowsBodyAndFinalSeparately()
    {
        // Given
        using var recording = EffectTracker.Record();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(new Buffer()), (_, _) =>
            Effect<EmptyEnvironment>.Async<Unit>(async _ =>
            {
                entered.TrySetResult();
                await finish.Task.ConfigureAwait(false);
                throw new TimeoutException();
            }));
        Effect<int, ReadUnavailable, EmptyEnvironment> failure = Effect<EmptyEnvironment>.Fail(new ReadUnavailable());
        var effect = Scope<ReadUnavailable, EmptyEnvironment>.Gen<int>(async scope =>
        {
            await scope.Acquire(resource);
            return await scope.Run(failure);
        });

        // When
        var running = effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken).AsTask();
        string live;
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            live = recording.GetTree();
        }
        finally
        {
            finish.TrySetResult();
        }
        var exit = await running;

        // Then
        Assert.Contains("body Failed ReadUnavailable  final Running", live);
        Assert.Matches("release #[0-9]+ Release  Running", live);
        Assert.True(exit.TryGetValue(out Die? _));
        var completed = recording.GetTree();
        Assert.Contains("body Failed ReadUnavailable  final Die TimeoutException", completed);
        Assert.Matches("resource #[0-9]+ Buffer  lifetime Die TimeoutException  owner #[0-9]+  acquired-by #[0-9]+", completed);
        Assert.DoesNotContain("Running", completed);
    }

    [Fact]
    public async Task Record_ReusedEffectInParallel_KeepsSeparateRoots()
    {
        // Given
        using var recording = EffectTracker.Record();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<EmptyEnvironment>.Async<Unit>(async _ =>
        {
            await finish.Task.ConfigureAwait(false);
            return Unit.Value;
        }).WithName("Work");

        // When
        var first = effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken).AsTask();
        var second = effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken).AsTask();
        var live = recording.GetTree();
        finish.TrySetResult();
        await Task.WhenAll(first, second);

        // Then
        Assert.Equal(2, Regex.Matches(live, "run #").Count);
        Assert.Equal(2, Regex.Matches(live, "op #[0-9]+ Work  Running").Count);
        Assert.DoesNotContain("Running", recording.GetTree());
    }

    [Fact]
    public async Task Record_CapacityExceeded_ReportsIncompleteWithoutChangingResult()
    {
        // Given
        using var recording = EffectTracker.Record(1);
        var effect = Effect<EmptyEnvironment>.Succeed(42);

        // When
        var result = await effect.RunAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(42, value);
        Assert.Contains("dropped 1", recording.GetTree());
        Assert.Contains("incomplete yes", recording.GetTree());
        Assert.DoesNotContain("Running", recording.GetTree());
    }

    [Fact]
    public async Task Record_ErrorWithThrowingToString_DoesNotReadItsPayload()
    {
        // Given
        using var recording = EffectTracker.Record();
        var effect = Effect<EmptyEnvironment>.Fail(new UnprintableError());

        // When
        var result = await effect.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetValue(out Err<UnprintableError> _));
        Assert.Contains("Failed UnprintableError", recording.GetTree());
    }

    [Fact]
    public async Task Record_DeepComposition_RetainsCompletedFrames()
    {
        // Given
        using var recording = EffectTracker.Record();
        var effect = Effect<EmptyEnvironment>.Succeed(0);
        for (var i = 0; i < 300; i++) effect = effect.Map(static value => value + 1);

        // When
        var result = await effect.RunAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(300, value);
        var tree = recording.GetTree();
        Assert.Contains("incomplete no", tree);
        Assert.DoesNotContain("Running", tree);
        Assert.Equal(301, Regex.Matches(tree, "op #").Count);
    }

    [Fact]
    public async Task Dispose_AlreadyRunningExecution_FinishesButNewRootIsNotCaptured()
    {
        // Given
        using var recording = EffectTracker.Record();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await finish.Task.ConfigureAwait(false);
            return 42;
        });

        // When
        var running = effect.RunAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken).AsTask();
        recording.Dispose();
        finish.TrySetResult();
        await running;
        await effect.RunAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

        // Then
        var tree = recording.GetTree();
        Assert.Single(Regex.Matches(tree, "run #").Cast<Match>());
        Assert.DoesNotContain("Running", tree);
    }

    [Fact]
    public async Task Record_SynchronousCallback_PreservesCallerExecutionContext()
    {
        // Given
        using var recording = EffectTracker.Record();
        var ambient = new AsyncLocal<int>();
        var effect = Effect<EmptyEnvironment>.Sync(() => ambient.Value = 7);

        // When
        var result = await effect.RunAsync(EmptyEnvironment.Value, CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var value));
        Assert.Equal(7, value);
        Assert.Equal(7, ambient.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task Record_PendingSource_PreservesHostContextBoundaries(int padding)
    {
        // Given
        using var recording = EffectTracker.Record();
        var ambient = new AsyncLocal<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task.ConfigureAwait(false);
            return 1;
        });
        Effect<int, Never, EmptyEnvironment> Set(int value)
            => Effect<EmptyEnvironment>.Sync(() => { ambient.Value = 7; return value; });
        Effect<int, Never, EmptyEnvironment> Read(int _)
            => Effect<EmptyEnvironment>.Sync(() => ambient.Value);
        var left = source.AndThen(Set).AndThen(Read);
        var right = source.AndThen(value => Set(value).AndThen(Read));
        for (var i = 0; i < padding; i++)
        {
            left = left.Map(static value => value);
            right = right.Map(static value => value);
        }

        // When
        var first = left.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None);
        var second = right.RunExitAsync(EmptyEnvironment.Value, CancellationToken.None);
        gate.TrySetResult();
        var firstExit = await first;
        var secondExit = await second;

        // Then
        Assert.Equal(0, Assert.IsType<Ok<int>>(firstExit.Value).Value);
        Assert.Equal(7, Assert.IsType<Ok<int>>(secondExit.Value).Value);
        Assert.Equal(0, ambient.Value);
        Assert.DoesNotContain("Running", recording.GetTree());
    }

    private sealed class Buffer;
    private sealed record ReadUnavailable;
    private sealed class UnprintableError
    {
        public override string ToString() => throw new InvalidOperationException();
    }
}
