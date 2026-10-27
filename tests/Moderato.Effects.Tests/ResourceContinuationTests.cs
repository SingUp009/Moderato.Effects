using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ResourceContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Use_AcquisitionSuspendsAndMapperThrows_PreservesFailureAndRelease(bool acquireFails)
    {
        // Given
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var defect = new IOException("mapper failed");
        var released = new List<int>();
        Effect<int, string, EmptyEnvironment> pending = Effect<EmptyEnvironment>.Async<int>(async _ => await finish.Task);
        var acquisition = pending.AndThen<int>(value => acquireFails
            ? Effect<EmptyEnvironment>.Fail("acquisition failed")
            : Effect<EmptyEnvironment>.Succeed(value));
        var work = Resource.Make(acquisition, (value, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                released.Add(value);
                return Unit.Value;
            }))
            .Map<int>(_ => throw defect)
            .MapErr<int>(_ => throw defect)
            .Use(value => Effect<EmptyEnvironment>.Succeed(value));

        // When
        var running = work.RunExitAsync(TestContext.Current.CancellationToken).AsTask();
        var suspended = !running.IsCompleted;
        finish.SetResult(42);
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(suspended);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(defect, die.Exception);
        Assert.Equal(acquireFails ? [] : new[] { 42 }, released);
        if (acquireFails)
        {
            var previous = Assert.Single(die.Suppressed);
            Assert.True(previous.Case is Failed("acquisition failed"));
        }
        else
        {
            Assert.Empty(die.Suppressed);
        }
    }
}
