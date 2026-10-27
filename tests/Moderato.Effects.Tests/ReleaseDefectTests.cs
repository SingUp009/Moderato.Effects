using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;
using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ReleaseDefectTests
{
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
