using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ReleaseDefectTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(100, false)]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    public async Task Defer_CarriedContextExceedsLimit_KeepsFinalFailureContextThenReleaseOrder(int bodyCount, bool releaseFails)
    {
        // Given
        var failure = new IOException("body");
        var releaseFailure = new IOException("release");
        var bodyContext = Enumerable.Range(0, bodyCount).Select(i => new Suppressed(new Failed($"body {i}"), Relation.Parallel)).ToArray();
        var firstContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"first {i}"), Relation.Sequential)).ToArray();
        var lastContext = Enumerable.Range(0, 60).Select(i => new Suppressed(new Failed($"last {i}"), Relation.Consequence)).ToArray();
        var failedContext = new Suppressed(new Failed("failed release context"), Relation.Parallel);
        var order = new List<int>();
        var firstOutcome = Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([.. firstContext], 2);
        var lastOutcome = Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([.. lastContext], 3);
        var bodyOutcome = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(failure), [.. bodyContext], omitted: 7);
        var middleOutcome = releaseFails
            ? Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(releaseFailure), [failedContext], omitted: 5)
            : Outcome<Unit, Never>.Ok(Unit.Value).WithSuppressed([], 5);
        var first = new Effect<Unit, Never, EmptyEnvironment>((_, _) => { order.Add(1); return new(firstOutcome); });
        var middle = new Effect<Unit, Never, EmptyEnvironment>((_, _) => { order.Add(2); return new(middleOutcome); });
        var last = new Effect<Unit, Never, EmptyEnvironment>((_, _) => { order.Add(3); return new(lastOutcome); });
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => last);
            scope.Defer(_ => middle);
            scope.Defer(_ => first);
            return await scope.Run(body);
        });
        Suppressed[] expected = releaseFails
            ? [.. bodyContext, new(new Die(releaseFailure, []), Relation.Sequential), failedContext, .. firstContext, .. lastContext]
            : [.. bodyContext, .. firstContext, .. lastContext];

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Equal([1, 2, 3], order);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(17 + expected.Length - 100, die.OmittedSuppressed);
        for (var i = 0; i < bodyCount; i++) Assert.Same(bodyContext[i], die.Suppressed[i]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(257)]
    public async Task Defer_ManyFailedReleases_PreservesEveryFailureInReverseOrder(int count)
    {
        // Given
        var failures = Enumerable.Range(0, count).Select(index => new IOException($"release {index}")).ToArray();
        var releases = failures.Select(failure => Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure)).ToArray();
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            foreach (var release in releases)
            {
#pragma warning disable MEFF305
                scope.Defer(_ => release);
#pragma warning restore MEFF305
            }

            return new(42);
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failures[^1], die.Exception);
        Assert.Equal(Math.Min(count - 1, 100), die.Suppressed.Count);
        for (var i = 0; i < die.Suppressed.Count; i++)
        {
            Assert.Same(failures[count - 2 - i], Assert.IsType<Die>(die.Suppressed[i].Case.Value).Exception);
            Assert.Equal(Relation.Sequential, die.Suppressed[i].Relation);
        }

        Assert.Equal(Math.Max(0, count - 101), die.OmittedSuppressed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Defer_FailedReleasesHaveOmittedFailures_PreservesOrderIdentityAndCounts(bool bodyFails)
    {
        // Given
        var bodyFailure = new IOException("body");
        var firstFailure = new IOException("first release");
        var lastFailure = new IOException("last release");
        var first = new Effect<Unit, Never, EmptyEnvironment>((_, _) =>
            new(Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(firstFailure), omitted: 2)));
        var last = new Effect<Unit, Never, EmptyEnvironment>((_, _) =>
            new(Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(lastFailure), omitted: 3)));
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyFails
            ? Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(bodyFailure), omitted: 7)
            : Outcome<int, Never>.Ok(42).WithSuppressed([], 7)));
        var work = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            scope.Defer(_ => last);
            scope.Defer(_ => first);
            return await scope.Run(body);
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bodyFails ? bodyFailure : firstFailure, die.Exception);
        Assert.Equal(12, die.OmittedSuppressed);
        Assert.Equal(bodyFails ? 2 : 1, die.Suppressed.Count);
        Assert.Same(lastFailure, Assert.IsType<Die>(die.Suppressed[^1].Case.Value).Exception);
        if (bodyFails) Assert.Same(firstFailure, Assert.IsType<Die>(die.Suppressed[0].Case.Value).Exception);
        Assert.All(die.Suppressed, failure => Assert.Equal(Relation.Sequential, failure.Relation));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 100)]
    [InlineData(50, 0)]
    [InlineData(50, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 100)]
    public async Task Use_SingleReleaseFailsAfterSuccessfulBody_KeepsBodyContextBeforeReleaseContext(int bodyCount, int releaseCount)
    {
        // Given
        var failure = new IOException("release");
        var bodyContext = Enumerable.Range(0, bodyCount).Select(i => new Suppressed(new Failed($"body {i}"), Relation.Parallel)).ToArray();
        var releaseContext = Enumerable.Range(0, releaseCount).Select(i => new Suppressed(new Failed($"release {i}"), Relation.Sequential)).ToArray();
        var bodyOutcome = Outcome<int, Never>.Ok(42).WithSuppressed([.. bodyContext], 2);
        var releaseOutcome = Outcome<Unit, Never>.Die(ExceptionDispatchInfo.Capture(failure), [.. releaseContext], omitted: 3);
        var body = new Effect<int, Never, EmptyEnvironment>((_, _) => new(bodyOutcome));
        var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) => new(releaseOutcome));
        var exits = new List<ExitCase>();
        var resource = Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, exit) => { exits.Add(exit); return release; });
        Suppressed[] expected = [.. bodyContext, .. releaseContext];

        // When
        var exit = await resource.Use(_ => body).RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(Assert.Single(exits).Value is Succeeded);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(expected.Take(100), die.Suppressed);
        Assert.Equal(5 + Math.Max(0, expected.Length - 100), die.OmittedSuppressed);
        for (var i = 0; i < Math.Min(100, expected.Length); i++) Assert.Same(expected[i], die.Suppressed[i]);
    }

    [Fact]
    public async Task Defer_FailedReleaseCountsOverflow_ReportsOverflow()
    {
        // Given
        var failure = ExceptionDispatchInfo.Capture(new IOException("release"));
        var release = new Effect<Unit, Never, EmptyEnvironment>((_, _) =>
            new(Outcome<Unit, Never>.Die(failure, omitted: int.MaxValue)));
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => release);
            scope.Defer(_ => release);
            return new(42);
        });

        // When
        var exit = await work.RunExitAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.IsType<OverflowException>(die.Exception);
    }
}
