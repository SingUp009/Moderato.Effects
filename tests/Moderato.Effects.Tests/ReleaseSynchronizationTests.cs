using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ReleaseSynchronizationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    [InlineData(128)]
    public async Task Fork_ManyChildrenFailTogether_KeepsEveryReportedFailure(int count)
    {
        // Given
        var failures = Enumerable.Range(0, count).Select(i => new IOException($"child {i}")).ToArray();
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var remaining = count;
            var fibers = new Fiber<int, Never, EmptyEnvironment>[count];
            for (var i = 0; i < count; i++)
            {
                var failure = failures[i];
                var release = Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure);
                var child = Resource.Make(Effect<EmptyEnvironment>.Succeed(Unit.Value), (_, _) => release)
                    .Use(_ => Effect<EmptyEnvironment>.Async<int>(async _ =>
                    {
                        if (Interlocked.Decrement(ref remaining) == 0) ready.SetResult();
                        await go.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                        return 42;
                    }));
#pragma warning disable MEFF305
                fibers[i] = await scope.Fork(child);
#pragma warning restore MEFF305
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            go.SetResult();
            foreach (var fiber in fibers)
                await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return 42;
        });
        var exits = new Exit<int, Never>[8];

        // When
        for (var i = 0; i < exits.Length; i++)
            exits[i] = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        foreach (var exit in exits)
        {
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Contains(die.Exception, failures);
            Assert.Equal(Math.Min(100, count - 1), die.Suppressed.Count);
            Assert.Equal(Math.Max(0, count - 101), die.OmittedSuppressed);
            var others = die.Suppressed.Select(suppressed =>
            {
                Assert.Equal(Relation.Parallel, suppressed.Relation);
                return Assert.IsType<Die>(suppressed.Case.Value).Exception;
            }).ToArray();
            Assert.Equal(others.Length, others.Distinct().Count());
            Assert.DoesNotContain(die.Exception, others);
            Assert.All(others, failure => Assert.Contains(failure, failures));
        }
    }
}
