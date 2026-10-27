namespace Moderato.Effects.Tests;

public class GroupSnapshotTests
{
    [Theory]
    [InlineData("All")]
    [InlineData("AllSettled")]
    [InlineData("AllErrors")]
    public async Task Group_ArrayChangesAfterConstruction_RunsTheOriginalLazySnapshotTwice(string operation)
    {
        // Given
        List<int> calls = [];
        Effect<int, string, EmptyEnvironment>[] effects =
        [
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(1); return 1; }),
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(2); return 2; }),
        ];

        // When
        var group = operation switch
        {
            "All" => Effect.All(effects).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            "AllSettled" => Effect.AllSettled(effects).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            _ => Effect.AllErrors(effects).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
        };
        var callsAtConstruction = calls.Count;
        effects[0] = Effect<EmptyEnvironment>.Fail("replacement");
        effects[1] = default;
        var first = await group.RunAsync(CancellationToken.None);
        var second = await group.RunAsync(CancellationToken.None);

        // Then
        Assert.Equal(0, callsAtConstruction);
        Assert.True(first.TryGetOk(out _));
        Assert.True(second.TryGetOk(out _));
        Assert.Equal([1, 2, 1, 2], calls);
    }

    [Theory]
    [InlineData("All", 0)]
    [InlineData("All", 1)]
    [InlineData("AllSettled", 0)]
    [InlineData("AllSettled", 1)]
    [InlineData("AllErrors", 0)]
    [InlineData("AllErrors", 1)]
    public void Group_ArrayContainsDefault_RejectsItDuringConstruction(string operation, int invalidIndex)
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> valid = Effect<EmptyEnvironment>.Sync(() => ++calls);
        Effect<int, string, EmptyEnvironment>[] effects = [valid, valid];
        effects[invalidIndex] = default;

        // When
        var exception = Record.Exception(() =>
        {
            switch (operation)
            {
                case "All": _ = Effect.All(effects); break;
                case "AllSettled": _ = Effect.AllSettled(effects); break;
                default: _ = Effect.AllErrors(effects); break;
            }
        });

        // Then
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, calls);
    }
}
