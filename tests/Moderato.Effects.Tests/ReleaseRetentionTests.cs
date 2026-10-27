using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ReleaseRetentionTests
{
    [Theory]
    [InlineData("Ok", 0)]
    [InlineData("Ok", 50)]
    [InlineData("Ok", 100)]
    [InlineData("Err", 0)]
    [InlineData("Err", 50)]
    [InlineData("Err", 100)]
    [InlineData("Die", 0)]
    [InlineData("Die", 50)]
    [InlineData("Die", 100)]
    [InlineData("Consequential", 0)]
    [InlineData("Consequential", 50)]
    [InlineData("Consequential", 100)]
    [InlineData("Interrupted", 0)]
    [InlineData("Interrupted", 50)]
    [InlineData("Interrupted", 100)]
    public async Task Use_MultipleReleaseFailuresExceedLimit_KeepsOrderedPrefixAndCountsOmissions(string kind, int bodyCount)
    {
        // Given
        var bodyFailure = new IOException("body");
        var firstFailure = new IOException("first release");
        var lastFailure = new IOException("last release");
        var bodyContext = Enumerable.Range(0, bodyCount).Select(i => new Suppressed(new Failed($"body {i}"), Relation.Parallel)).ToArray();
        var firstContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"first {i}"), Relation.Consequence)).ToArray();
        var lastContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"last {i}"), Relation.Sequential)).ToArray();
        var bodyOutcome = (kind switch
        {
            "Ok" => Outcome<int, string>.Ok(42),
            "Err" => Outcome<int, string>.Err("body error"),
            "Interrupted" => Outcome<int, string>.Interrupted(),
            _ => Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(bodyFailure), consequential: kind == "Consequential"),
        }).WithSuppressed([.. bodyContext], 7);
        var firstOutcome = Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(firstFailure), [.. firstContext], omitted: 2);
        var lastOutcome = Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(lastFailure), [.. lastContext], omitted: 3);
        var order = new List<int>();
        var body = new Effect<int, string, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var first = new Effect<Unit, Never, EmptyEnvironment>((_, _) => { order.Add(1); return new(firstOutcome); });
        var last = new Effect<Unit, Never, EmptyEnvironment>((_, _) => { order.Add(2); return new(lastOutcome); });
        Effect<int, string, EmptyEnvironment> acquire = Effect<EmptyEnvironment>.Succeed(1);
        var outer = Resource.Make(acquire, (_, _) => last);
        var inner = Resource.Make(acquire, (_, _) => first);
        var expected = new List<Suppressed>();
        if (kind == "Err") expected.Add(new(new Failed("body error"), Relation.Sequential));
        if (kind == "Interrupted") expected.Add(new(new Interrupted(), Relation.Sequential));
        if (kind == "Consequential") expected.Add(new(new Die(bodyFailure, bodyOutcome.Suppressed) { OmittedSuppressed = 7 }, Relation.Consequence));
        expected.AddRange(bodyContext);
        if (kind == "Die") expected.Add(new(new Die(firstFailure, []), Relation.Sequential));
        expected.AddRange(firstContext);
        expected.Add(new(new Die(lastFailure, []), Relation.Sequential));
        expected.AddRange(lastContext);

        // When
        var exit = await outer.AndThen(_ => inner).Use(_ => body).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([1, 2], order);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(kind == "Die" ? bodyFailure : firstFailure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(12 + expected.Count - 100, die.OmittedSuppressed);
    }
}
