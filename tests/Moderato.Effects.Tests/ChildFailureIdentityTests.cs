namespace Moderato.Effects.Tests;

public class ChildFailureIdentityTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Gen_ParentAndChildrenThrowTheSameException_CountsEachFailureOnce(int childCount, bool joinChild)
    {
        // Given
        var shared = new InvalidOperationException("shared");
        var child = Resource.Make(Effect<EmptyEnvironment>.Succeed(0),
            (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw shared))
            .Use(value => Effect<EmptyEnvironment>.Succeed(value));
        var parent = Scope<Never, EmptyEnvironment>.Gen(async scope =>
        {
            var first = await scope.Fork(child);
            Fiber<int, Never, EmptyEnvironment>? second = null;
            if (childCount == 2) second = await scope.Fork(child);
            await first.Join().RunExitAsync(TestContext.Current.CancellationToken);
            if (second is not null) await second.Join().RunExitAsync(TestContext.Current.CancellationToken);
            return await scope.Run(joinChild ? first.Join() : Effect<EmptyEnvironment>.Sync<int>(() => throw shared));
        });

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(shared, die.Exception);
        Assert.Equal(childCount - (joinChild ? 1 : 0), die.Suppressed.Count);
        Assert.All(die.Suppressed, failure => Assert.Same(shared, Assert.IsType<Die>(failure.Case.Value).Exception));
        Assert.Equal(childCount - 1, die.Suppressed.Count(failure => failure.Relation == Relation.Parallel));
        Assert.Equal(joinChild ? 0 : 1, die.Suppressed.Count(failure => failure.Relation == Relation.Consequence));
        Assert.Equal(0, die.OmittedSuppressed);
    }

    [Fact]
    public async Task Use_ParentAndChildThrowTheSameException_KeepsBothFailures()
    {
        // Given
        var shared = new InvalidOperationException("shared");
        var child = Effect<EmptyEnvironment>.Sync<int>(() => throw shared);
        var parent = child.Fork().Use(fiber => Effect<EmptyEnvironment>.Async<int>(async _ =>
        {
            await fiber.Join().RunExitAsync(TestContext.Current.CancellationToken);
            throw shared;
        }));

        // When
        var exit = await parent.RunExitAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(shared, die.Exception);
        var failure = Assert.Single(die.Suppressed);
        Assert.Same(shared, Assert.IsType<Die>(failure.Case.Value).Exception);
        Assert.Equal(Relation.Consequence, failure.Relation);
        Assert.Equal(0, die.OmittedSuppressed);
    }
}
