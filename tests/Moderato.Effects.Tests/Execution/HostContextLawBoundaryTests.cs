namespace Moderato.Effects.Tests;

public class HostContextLawBoundaryTests
{
    [Fact]
    public async Task AndThen_BinderHasConstructionSideEffects_LeftUnitDoesNotEquateConstructionTiming()
    {
        // Given
        var constructions = 0;
        Effect<int, Never, EmptyEnvironment> Build(int value)
        {
            constructions++;
            return Effect<EmptyEnvironment>.Succeed(value);
        }

        // When
        var composed = Effect<EmptyEnvironment>.Succeed(5).AndThen(Build);
        var beforeDirect = constructions;
        var direct = Build(5);
        var beforeRun = constructions;
        var left = await composed.RunAsync(CancellationToken.None);
        var right = await direct.RunAsync(CancellationToken.None);

        // Then
        Assert.Equal(0, beforeDirect);
        Assert.Equal(1, beforeRun);
        Assert.Equal(2, constructions);
        Assert.Equal(left, right);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task AndThen_PendingSourceAndAmbientMutation_AssociationChangesTheHostContextBoundary(int padding)
    {
        // Given
        var ambient = new AsyncLocal<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await gate.Task;
            return 1;
        });
        Effect<int, Never, EmptyEnvironment> F(int value)
            => Effect<EmptyEnvironment>.Sync(() => { ambient.Value = 7; return value; });
        Effect<int, Never, EmptyEnvironment> G(int _)
            => Effect<EmptyEnvironment>.Sync(() => ambient.Value);
        var left = source.AndThen(F).AndThen(G);
        var right = source.AndThen(value => F(value).AndThen(G));
        for (var i = 0; i < padding; i++)
        {
            left = left.Map(static value => value);
            right = right.Map(static value => value);
        }

        // When
        var first = left.RunExitAsync(CancellationToken.None);
        var second = right.RunExitAsync(CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        gate.SetResult();
        var firstExit = await first;
        var secondExit = await second;

        // Then
        Assert.Equal(0, Assert.IsType<Ok<int>>(firstExit.Value).Value);
        Assert.Equal(7, Assert.IsType<Ok<int>>(secondExit.Value).Value);
        Assert.Equal(0, ambient.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task AndThen_PendingSourceAndExplicitState_AssociatesValueAndExecutionTrace(int padding)
    {
        // Given
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Effect<State>.AccessAsync<int>(async (state, _) =>
        {
            state.Trace.Add("source");
            await gate.Task;
            return 1;
        });
        static Effect<int, Never, State> F(int value) => Effect<State>.Access(state =>
        {
            state.Trace.Add("f");
            state.Value = 7;
            return value;
        });
        static Effect<int, Never, State> G(int _) => Effect<State>.Access(state =>
        {
            state.Trace.Add("g");
            return state.Value;
        });
        var left = source.AndThen(F).AndThen(G);
        var right = source.AndThen(value => F(value).AndThen(G));
        for (var i = 0; i < padding; i++)
        {
            left = left.Map(static value => value);
            right = right.Map(static value => value);
        }
        var firstState = new State();
        var secondState = new State();

        // When
        var first = left.RunExitAsync(firstState, CancellationToken.None);
        var second = right.RunExitAsync(secondState, CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        gate.SetResult();
        var firstExit = await first;
        var secondExit = await second;

        // Then
        Assert.Equal(firstExit, secondExit);
        Assert.Equal(7, Assert.IsType<Ok<int>>(firstExit.Value).Value);
        Assert.Equal(["source", "f", "g"], firstState.Trace);
        Assert.Equal(firstState.Trace, secondState.Trace);
    }

    private sealed class State
    {
        internal int Value { get; set; }
        internal List<string> Trace { get; } = [];
    }
}
