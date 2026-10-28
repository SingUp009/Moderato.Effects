using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class SingleChildContextTests
{
    [Theory]
    [InlineData("Ok", 0, 60)]
    [InlineData("Ok", 40, 60)]
    [InlineData("Ok", 100, 60)]
    [InlineData("Interrupted", 0, 60)]
    [InlineData("Interrupted", 40, 60)]
    [InlineData("Interrupted", 100, 60)]
    [InlineData("Same", 0, 60)]
    [InlineData("Same", 40, 60)]
    [InlineData("Same", 100, 60)]
    [InlineData("Die", 0, 60)]
    [InlineData("Die", 100, 60)]
    [InlineData("Err", 0, 60)]
    [InlineData("Err", 100, 60)]
    [InlineData("Ok", 0, 0)]
    [InlineData("Ok", 100, 0)]
    [InlineData("Interrupted", 0, 0)]
    [InlineData("Interrupted", 100, 0)]
    [InlineData("Same", 0, 0)]
    [InlineData("Same", 100, 0)]
    public async Task Use_OneChildDefect_KeepsChildContextBeforeBodyContext(string kind, int childCount, int bodyCount)
    {
        // Given
        var childFailure = new IOException("child");
        var bodyFailure = kind == "Same" ? childFailure : new IOException("body");
        var childContext = Enumerable.Range(0, childCount).Select(i => new Suppressed(new Failed($"child {i}"), Relation.Sequential)).ToArray();
        var bodyContext = Enumerable.Range(0, bodyCount).Select(i => new Suppressed(new Failed($"body {i}"), Relation.Parallel)).ToArray();
        var childOutcome = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(childFailure), [.. childContext], omitted: 2);
        var bodyOutcome = (kind switch
        {
            "Ok" => Outcome<int, string>.Ok(42),
            "Interrupted" => Outcome<int, string>.Interrupted(),
            "Err" => Outcome<int, string>.Err("body error", consequential: true),
            _ => Outcome<int, string>.Die(ExceptionDispatchInfo.Capture(bodyFailure)),
        }).WithSuppressed([.. bodyContext], 7);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Effect<int, Never, EmptyEnvironment>(async (_, _) =>
        {
            started.SetResult();
            await go.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return childOutcome;
        });
        Resource<Fiber<int, Never, EmptyEnvironment>, string, EmptyEnvironment> resource = child.Fork();
        var work = resource.Use(fiber => new Effect<int, string, EmptyEnvironment>(async (_, _) =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            go.SetResult();
            await fiber.Join().RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return bodyOutcome;
        }));
        List<Suppressed> expected = [.. childContext];
        if (kind == "Die") expected.Add(new(new Die(bodyFailure, bodyOutcome.Suppressed) { OmittedSuppressed = 7 }, Relation.Parallel));
        if (kind == "Err") expected.Add(new(new Failed("body error"), Relation.Consequence));
        expected.AddRange(bodyContext);

        // When
        var exit = await work.RunExitAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(childFailure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(9 + Math.Max(0, expected.Count - 100), die.OmittedSuppressed);
    }
}
