using Moderato.Results;

namespace Moderato.Effects.Tests;

public class ReleaseStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gen_ThreeReleasesFail_KeepsEveryFailureInReleaseOrder(bool bodyFails)
    {
        // Given
        var bodyFailure = new IOException("body");
        var first = new IOException("first registered");
        var second = new IOException("second registered");
        var third = new IOException("third registered");
        var calls = new List<int>();
        var work = Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
        {
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(1); throw first; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(2); throw second; }));
            scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => { calls.Add(3); throw third; }));
            if (bodyFails) throw bodyFailure;
            return new(42);
        });

        // When
        var exit = await work.RunExitAsync(CancellationToken.None);

        // Then
        Assert.Equal([3, 2, 1], calls);
        Assert.True(exit.TryGetValue(out Die? die));
        Assert.Same(bodyFails ? bodyFailure : third, die.Exception);
        Exception[] expected = bodyFails ? [third, second, first] : [second, first];
        Assert.Equal(expected, die.Suppressed.Select(entry => Assert.IsType<Die>(entry.Case.Value).Exception));
        Assert.All(die.Suppressed, entry => Assert.Equal(Relation.Sequential, entry.Relation));
        Assert.Equal(0, die.OmittedSuppressed);
    }
}
