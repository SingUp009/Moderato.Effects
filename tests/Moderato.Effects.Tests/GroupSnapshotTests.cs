namespace Moderato.Effects.Tests;

public class GroupSnapshotTests
{
    [Theory]
    [InlineData("All", false)]
    [InlineData("All", true)]
    [InlineData("AllSettled", false)]
    [InlineData("AllSettled", true)]
    [InlineData("AllErrors", false)]
    [InlineData("AllErrors", true)]
    public async Task Group_InputCollectionChangesAfterConstruction_RunsTheOriginalLazySnapshotTwice(string operation, bool useList)
    {
        // Given
        List<int> calls = [];
        Effect<int, string, EmptyEnvironment>[] effects =
        [
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(1); return 1; }),
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(2); return 2; }),
        ];
        IList<Effect<int, string, EmptyEnvironment>> source = useList ? effects.ToList() : effects;

        // When
        var group = operation switch
        {
            "All" => Effect.All(source).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            "AllSettled" => Effect.AllSettled(source).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            _ => Effect.AllErrors(source).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
        };
        var callsAtConstruction = calls.Count;
        source[0] = Effect<EmptyEnvironment>.Fail("replacement");
        source[1] = default;
        var first = await group.RunAsync(CancellationToken.None);
        var second = await group.RunAsync(CancellationToken.None);

        // Then
        Assert.Equal(0, callsAtConstruction);
        Assert.True(first.TryGetOk(out _));
        Assert.True(second.TryGetOk(out _));
        Assert.Equal([1, 2, 1, 2], calls);
    }

    [Theory]
    [InlineData("All", 0, false)]
    [InlineData("All", 0, true)]
    [InlineData("All", 1, false)]
    [InlineData("All", 1, true)]
    [InlineData("AllSettled", 0, false)]
    [InlineData("AllSettled", 0, true)]
    [InlineData("AllSettled", 1, false)]
    [InlineData("AllSettled", 1, true)]
    [InlineData("AllErrors", 0, false)]
    [InlineData("AllErrors", 0, true)]
    [InlineData("AllErrors", 1, false)]
    [InlineData("AllErrors", 1, true)]
    public void Group_InputCollectionContainsDefault_RejectsItDuringConstruction(string operation, int invalidIndex, bool useList)
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> valid = Effect<EmptyEnvironment>.Sync(() => ++calls);
        Effect<int, string, EmptyEnvironment>[] effects = [valid, valid];
        effects[invalidIndex] = default;
        IList<Effect<int, string, EmptyEnvironment>> source = useList ? effects.ToList() : effects;

        // When
        var exception = Record.Exception(() =>
        {
            switch (operation)
            {
                case "All": _ = Effect.All(source); break;
                case "AllSettled": _ = Effect.AllSettled(source); break;
                default: _ = Effect.AllErrors(source); break;
            }
        });

        // Then
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("All", false)]
    [InlineData("All", true)]
    [InlineData("AllSettled", false)]
    [InlineData("AllSettled", true)]
    [InlineData("AllErrors", false)]
    [InlineData("AllErrors", true)]
    public async Task Group_EmptyInputCollection_StaysEmptyAfterConstruction(string operation, bool useList)
    {
        // Given
        IList<Effect<int, string, EmptyEnvironment>> source = useList
            ? new List<Effect<int, string, EmptyEnvironment>>()
            : Array.Empty<Effect<int, string, EmptyEnvironment>>();

        // When
        var group = operation switch
        {
            "All" => Effect.All(source).Map(static values => values.Length).MapErr(static _ => Unit.Value),
            "AllSettled" => Effect.AllSettled(source).Map(static values => values.Length).MapErr(static _ => Unit.Value),
            _ => Effect.AllErrors(source).Map(static values => values.Length).MapErr(static _ => Unit.Value),
        };
        if (useList) source.Add(Effect<EmptyEnvironment>.Fail("later"));
        var result = await group.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var length));
        Assert.Equal(0, length);
    }
}
