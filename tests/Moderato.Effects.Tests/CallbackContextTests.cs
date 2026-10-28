using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class CallbackContextTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(99, false)]
    [InlineData(100, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, true)]
    public async Task RunExitAsync_CancellationWithExistingContext_PreservesPrefixAndOmissions(int count, bool callbackThrows)
    {
        // Given
        var bodyFailure = new IOException("body");
        var callbackFailure = new IOException("callback");
        var context = Enumerable.Range(0, count).Select(i => new Suppressed(new Failed(i), Relation.Sequential)).ToArray();
        var outcome = Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(bodyFailure), [.. context], omitted: 7);
        using var caller = new CancellationTokenSource();
        Effect<int, string, CancellationTokenSource> effect = new((environment, run) =>
        {
            using var registration = run.CancellationToken.Register(() => { if (callbackThrows) throw callbackFailure; });
            environment.Cancel();
            return new(outcome);
        });

        // When
        var exit = await effect.RunExitAsync(caller, caller.Token);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bodyFailure, die.Exception);
        var prefix = callbackThrows ? 1 : 0;
        Assert.Equal(Math.Min(100, count + prefix), die.Suppressed.Count);
        Assert.Equal(7 + Math.Max(0, count + prefix - 100), die.OmittedSuppressed);
        if (callbackThrows)
        {
            Assert.Same(callbackFailure, Assert.IsType<Die>(die.Suppressed[0].Case.Value).Exception);
            Assert.Equal(Relation.Consequence, die.Suppressed[0].Relation);
        }
        for (var i = 0; i < die.Suppressed.Count - prefix; i++) Assert.Same(context[i], die.Suppressed[i + prefix]);
    }
}
