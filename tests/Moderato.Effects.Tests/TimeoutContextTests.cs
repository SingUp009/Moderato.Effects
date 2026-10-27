using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class TimeoutContextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Timeout_IndependentDefectHasContext_RemovesOnlyInterruptionsAndKeepsOrder(int shape)
    {
        // Given
        var time = new FakeTimeProvider();
        var failure = new IOException("independent defect");
        Suppressed[] context = shape switch
        {
            0 => [],
            1 => [new(new Interrupted(), Relation.Consequence)],
            2 => [new(new Failed("first"), Relation.Parallel), new(new Interrupted(), Relation.Sequential),
                new(new Die(new IOException("second"), []), Relation.Consequence),
                new(new Interrupted(), Relation.Parallel), new(new Failed("third"), Relation.Sequential)],
            _ => [.. Enumerable.Range(0, 100).Select(i => new Suppressed(new Failed(i), Relation.Parallel))],
        };
        var expected = context.Where(s => s.Case is not Interrupted).ToArray();
        var work = new Effect<int, string, TimeEnvironment>((_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return new(Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(failure), [.. context], omitted: 7));
        });
        var handlerCalled = false;

        // When
        var exit = await work.Timeout(TimeSpan.FromSeconds(1), _ => { handlerCalled = true; return "timeout"; })
            .RunExitAsync(new TimeEnvironment(time), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.False(handlerCalled);
        Assert.Equal(new Suppressed(new Failed(new TimeoutError(TimeSpan.FromSeconds(1))), Relation.Sequential), die.Suppressed[0]);
        Assert.Equal(1 + Math.Min(99, expected.Length), die.Suppressed.Count);
        for (var i = 1; i < die.Suppressed.Count; i++) Assert.Same(expected[i - 1], die.Suppressed[i]);
        Assert.Equal(7 + Math.Max(0, expected.Length - 99), die.OmittedSuppressed);
    }
}
