using System.Runtime.ExceptionServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class GroupContextTests
{
    [Fact]
    public async Task All_ChildrenHaveOnlyOmittedFailures_PreservesOmittedCount()
    {
        // Given
        var failure = new IOException("projection failed");
        Effect<int, Never, EmptyEnvironment>[] children =
        [
            new((_, _) => new(Outcome<int, Never>.Ok(1).WithSuppressed([], 2))),
            new((_, _) => new(Outcome<int, Never>.Ok(2).WithSuppressed([], 3))),
        ];

        // When
        var exit = await Effect.All(children).Map<int>(_ => throw failure).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Empty(die.Suppressed);
        Assert.Equal(5, die.OmittedSuppressed);
    }

    [Fact]
    public async Task All_PrimaryAndEarlierChildHaveOnlyOmittedFailures_CountsEachOnce()
    {
        // Given
        var failure = new IOException("primary");
        var primary = Outcome<int, Never>.Die(ExceptionDispatchInfo.Capture(failure), omitted: 3);
        var unstartedCalls = 0;
        Effect<int, Never, EmptyEnvironment>[] children =
        [
            new((_, _) => new(Outcome<int, Never>.Ok(1).WithSuppressed([], 2))),
            new((_, _) => new(primary)),
            Effect<EmptyEnvironment>.Sync(() => { unstartedCalls++; return 3; }),
        ];

        // When
        var exit = await Effect.All(children).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Empty(die.Suppressed);
        Assert.Equal(5, die.OmittedSuppressed);
        Assert.Equal(0, unstartedCalls);
    }

    [Fact]
    public async Task AllErrors_ChildrenHaveOnlyOmittedFailures_KeepsCountsWithoutDuplicatingErrors()
    {
        // Given
        var failure = new IOException("error projection failed");
        Effect<int, string, EmptyEnvironment>[] children =
        [
            new((_, _) => new(Outcome<int, string>.Err("first").WithSuppressed([], 2))),
            new((_, _) => new(Outcome<int, string>.Err("second").WithSuppressed([], 3))),
        ];

        // When
        var exit = await Effect.AllErrors(children).MapErr<string>(_ => throw failure).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(failure, die.Exception);
        Assert.Equal(5, die.OmittedSuppressed);
        var previous = Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value);
        Assert.Equal(["first", "second"], Assert.IsType<string[]>(previous.Error));
    }
}
