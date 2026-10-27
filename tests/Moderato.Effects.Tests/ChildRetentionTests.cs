using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ChildRetentionTests
{
    [Theory]
    [InlineData("Ok", 0)]
    [InlineData("Ok", 100)]
    [InlineData("Err", 0)]
    [InlineData("Err", 100)]
    [InlineData("Die", 0)]
    [InlineData("Die", 100)]
    [InlineData("Consequential", 0)]
    [InlineData("Consequential", 100)]
    [InlineData("Interrupted", 0)]
    [InlineData("Interrupted", 100)]
    [InlineData("First", 0)]
    [InlineData("First", 100)]
    [InlineData("Last", 0)]
    [InlineData("Last", 100)]
    [InlineData("Wrapped", 100)]
    [InlineData("Overflow", 100)]
    public async Task Use_ChildDefectsExceedLimit_KeepsChildOrderAndCountsBodyOnce(string kind, int firstCount)
    {
        // Given
        var firstFailure = new IOException("first child");
        var lastFailure = new IOException("last child");
        var bodyFailure = kind == "First" ? firstFailure : kind == "Last" ? lastFailure : new IOException("body");
        var firstContext = Enumerable.Range(0, firstCount).Select(i => new Suppressed(new Failed($"first {i}"), Relation.Sequential)).ToArray();
        var lastContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"last {i}"), Relation.Consequence)).ToArray();
        var bodyContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"body {i}"), Relation.Parallel)).ToArray();
        var error = new ObservedError();
        var bodyOutcome = (kind switch
        {
            "Ok" or "Wrapped" or "Overflow" => Outcome<int, object>.Ok(42),
            "Err" => Outcome<int, object>.Err(error),
            "Interrupted" => Outcome<int, object>.Interrupted(),
            _ => Outcome<int, object>.Die(ExceptionDispatchInfo.Capture(bodyFailure), consequential: kind == "Consequential"),
        }).WithSuppressed([.. bodyContext], 7);
        var firstOmitted = kind is "Wrapped" or "Overflow" ? int.MaxValue : 2;
        var lastOmitted = kind == "Wrapped" ? 0 : 3;
        var firstOutcome = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(firstFailure), [.. firstContext], omitted: firstOmitted);
        var lastOutcome = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(lastFailure), [.. lastContext], omitted: lastOmitted);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Effect<int, Never, EmptyEnvironment>(async (_, _) =>
        {
            firstStarted.SetResult();
            await firstGo.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return firstOutcome;
        });
        var last = new Effect<int, Never, EmptyEnvironment>(async (_, _) =>
        {
            lastStarted.SetResult();
            await lastGo.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return lastOutcome;
        });
        var resource = first.Fork().MapErr<object>(static never => never).AndThen(f1 =>
            last.Fork().MapErr<object>(static never => never).Map(f2 => (first: f1, last: f2)));
        var work = resource.Use(pair => new Effect<int, object, EmptyEnvironment>(async (_, _) =>
        {
            await Task.WhenAll(firstStarted.Task, lastStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            firstGo.SetResult();
            await pair.first.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            lastGo.SetResult();
            await pair.last.Join().RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return bodyOutcome;
        }));
        List<Suppressed> expected = [.. firstContext, new(new Die(lastFailure, []), Relation.Parallel), .. lastContext];
        if (kind == "Err") expected.Add(new(new Failed("body error"), Relation.Consequence));
        if (kind is "Die" or "Consequential")
        {
            expected.Add(new(new Die(bodyFailure, bodyOutcome.Suppressed) { OmittedSuppressed = 7 },
                kind == "Die" ? Relation.Parallel : Relation.Consequence));
        }
        expected.AddRange(bodyContext);

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        if (kind == "Overflow")
        {
            Assert.IsType<OverflowException>(die.Exception);
            return;
        }

        Assert.Same(firstFailure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(unchecked(firstOmitted + lastOmitted + 7 + expected.Count - 100), die.OmittedSuppressed);
        Assert.Equal(kind == "Err" ? 1 : 0, error.Reads);
    }

    private sealed class ObservedError : System.Runtime.CompilerServices.IUnion
    {
        internal int Reads { get; private set; }

        public object? Value
        {
            get
            {
                Reads++;
                return "body error";
            }
        }
    }
}
