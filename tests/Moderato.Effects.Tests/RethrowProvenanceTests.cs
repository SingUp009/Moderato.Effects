using Microsoft.Extensions.Time.Testing;
using Moderato.Results;

namespace Moderato.Effects.Tests;

public class RethrowProvenanceTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, true, true, false, true)]
    public async Task Gen_CatchesAnotherDefect_PreservesRethrowOrRecovery(bool timeout, bool pending, bool catchAnother, bool recover, bool siblingDefect)
    {
        // Given
        var original = new IOException("original");
        var other = new FormatException("handled");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caught = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        var env = new TimeEnvironment(clock);
        var delay = TimeSpan.FromSeconds(1);
        var source = pending
            ? Effect<TimeEnvironment>.Async<int>(async _ => { await start.Task; throw original; })
            : Effect<TimeEnvironment>.Sync<int>(() => throw original);
        var first = Scope<Never, TimeEnvironment>.Gen<int>(async scope =>
        {
            try { return await scope.Run(source); }
            catch (IOException)
            {
                if (catchAnother)
                {
                    try { await scope.Run(Effect<TimeEnvironment>.Sync<int>(() => throw other)); }
                    catch (FormatException) { }
                }
                caught.SetResult();
                if (recover) return 42;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, scope.CancellationToken); }
                catch (OperationCanceledException) { }
                throw;
            }
        });
        Effect<int, string, TimeEnvironment> widened = first;
        var sibling = Effect<TimeEnvironment>.FromResultAsync<int, string>(async _ =>
        {
            await caught.Task;
            if (siblingDefect) throw new InvalidOperationException("later independent defect");
            return recover ? Result.Ok(1) : Result.Err("sibling");
        });

        // When
        if (timeout)
        {
            var running = first.Timeout(delay).RunExitAsync(env, CancellationToken.None).AsTask();
            start.SetResult();
            await caught.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (!recover) clock.Advance(delay);
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            if (recover)
            {
                Assert.True(exit.TryGetValue(out Ok<int> _));
            }
            else
            {
                Assert.True(exit.TryGetValue(out Die? die), $"Expected original Die, got {exit}");
                Assert.Same(original, die.Exception);
            }
        }
        else
        {
            var running = Effect.All([widened, sibling], Concurrency.Parallel).RunExitAsync(env, CancellationToken.None).AsTask();
            start.SetResult();
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Then
            if (recover)
            {
                Assert.True(exit.TryGetValue(out Ok<int[]> _));
            }
            else
            {
                Assert.True(exit.TryGetValue(out Die? die), $"Expected original Die, got {exit}");
                Assert.Same(original, die.Exception);
                Assert.DoesNotContain(die.Suppressed, s => s.Case.Value is Die d && ReferenceEquals(d.Exception, other));
            }
        }
    }
}
