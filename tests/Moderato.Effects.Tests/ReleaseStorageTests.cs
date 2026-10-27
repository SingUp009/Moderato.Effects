using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ReleaseStorageTests
{
    [Fact]
    public async Task Gen_ReleaseSuspendsThenAnotherFails_AwaitsAndRunsEveryReleaseInReverseOrder()
    {
        // Given
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("release failed");
        var calls = new List<int>();
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync(() => { calls.Add(1); return Unit.Value; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(2); throw failure; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Async(async _ =>
            {
                calls.Add(3);
                started.SetResult();
                await resume.Task;
                calls.Add(4);
                return Unit.Value;
            }));
            return new(42);
        });

        // When
        var running = work.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var pending = !running.IsCompleted;
        var beforeResume = calls.ToArray();
        resume.SetResult();
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(pending);
        Assert.Equal([3], beforeResume);
        Assert.Equal([3, 4, 2, 1], calls);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gen_ThreeReleasesFail_KeepsEveryFailureInReleaseOrder(bool bodyFails)
    {
        // Given
        var bodyFailure = new IOException("body");
        var first = new IOException("first registered");
        var second = new IOException("second registered");
        var third = new IOException("third registered");
        var calls = new List<int>();
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(1); throw first; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(2); throw second; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(3); throw third; }));
            if (bodyFails) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.Equal([3, 2, 1], calls);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bodyFails ? bodyFailure : third, die.Exception);
        Exception[] expected = bodyFails ? [third, second, first] : [second, first];
        Assert.Equal(expected, die.Suppressed.Select(entry => Assert.IsType<Die>(entry.Case.Value).Exception));
        Assert.All(die.Suppressed, entry => Assert.Equal(Relation.Sequential, entry.Relation));
        Assert.Equal(0, die.OmittedSuppressed);
    }
}
