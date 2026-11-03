namespace Moderato.Effects.Tests;

public class GroupSnapshotTests
{
    [Theory]
    [InlineData("All", "Array")]
    [InlineData("All", "List")]
    [InlineData("All", "Iterator")]
    [InlineData("AllSettled", "Array")]
    [InlineData("AllSettled", "List")]
    [InlineData("AllSettled", "Iterator")]
    [InlineData("AllErrors", "Array")]
    [InlineData("AllErrors", "List")]
    [InlineData("AllErrors", "Iterator")]
    public async Task Group_InputCollectionChangesAfterConstruction_RunsTheOriginalLazySnapshotTwice(string operation, string shape)
    {
        // Given
        List<int> calls = [];
        Effect<int, string, EmptyEnvironment>[] effects =
        [
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(1); return 1; }),
            Effect<EmptyEnvironment>.Sync(() => { calls.Add(2); return 2; }),
        ];
        IList<Effect<int, string, EmptyEnvironment>> source = shape == "Array" ? effects : effects.ToList();
        IEnumerable<Effect<int, string, EmptyEnvironment>> input = shape == "Iterator" ? Enumerate(source) : source;

        // When
        var group = operation switch
        {
            "All" => Effect.All(input).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            "AllSettled" => Effect.AllSettled(input).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
            _ => Effect.AllErrors(input).Map(static _ => Unit.Value).MapErr(static _ => Unit.Value),
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
    [InlineData("All", 0, "Array")]
    [InlineData("All", 0, "List")]
    [InlineData("All", 0, "Iterator")]
    [InlineData("All", 1, "Array")]
    [InlineData("All", 1, "List")]
    [InlineData("All", 1, "Iterator")]
    [InlineData("AllSettled", 0, "Array")]
    [InlineData("AllSettled", 0, "List")]
    [InlineData("AllSettled", 0, "Iterator")]
    [InlineData("AllSettled", 1, "Array")]
    [InlineData("AllSettled", 1, "List")]
    [InlineData("AllSettled", 1, "Iterator")]
    [InlineData("AllErrors", 0, "Array")]
    [InlineData("AllErrors", 0, "List")]
    [InlineData("AllErrors", 0, "Iterator")]
    [InlineData("AllErrors", 1, "Array")]
    [InlineData("AllErrors", 1, "List")]
    [InlineData("AllErrors", 1, "Iterator")]
    public void Group_InputCollectionContainsDefault_RejectsItDuringConstruction(string operation, int invalidIndex, string shape)
    {
        // Given
        var calls = 0;
        Effect<int, string, EmptyEnvironment> valid = Effect<EmptyEnvironment>.Sync(() => ++calls);
        Effect<int, string, EmptyEnvironment>[] effects = [valid, valid];
        effects[invalidIndex] = default;
        IList<Effect<int, string, EmptyEnvironment>> source = shape == "Array" ? effects : effects.ToList();
        IEnumerable<Effect<int, string, EmptyEnvironment>> input = shape == "Iterator" ? Enumerate(source) : source;

        // When
        var exception = Record.Exception(() =>
        {
            switch (operation)
            {
                case "All": _ = Effect.All(input); break;
                case "AllSettled": _ = Effect.AllSettled(input); break;
                default: _ = Effect.AllErrors(input); break;
            }
        });

        // Then
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("All", "Array")]
    [InlineData("All", "List")]
    [InlineData("All", "Iterator")]
    [InlineData("AllSettled", "Array")]
    [InlineData("AllSettled", "List")]
    [InlineData("AllSettled", "Iterator")]
    [InlineData("AllErrors", "Array")]
    [InlineData("AllErrors", "List")]
    [InlineData("AllErrors", "Iterator")]
    public async Task Group_EmptyInputCollection_StaysEmptyAfterConstruction(string operation, string shape)
    {
        // Given
        IList<Effect<int, string, EmptyEnvironment>> source = shape == "Array"
            ? Array.Empty<Effect<int, string, EmptyEnvironment>>()
            : new List<Effect<int, string, EmptyEnvironment>>();
        IEnumerable<Effect<int, string, EmptyEnvironment>> input = shape == "Iterator" ? Enumerate(source) : source;

        // When
        var group = operation switch
        {
            "All" => Effect.All(input).Map(static values => values.Length).MapErr(static _ => Unit.Value),
            "AllSettled" => Effect.AllSettled(input).Map(static values => values.Length).MapErr(static _ => Unit.Value),
            _ => Effect.AllErrors(input).Map(static values => values.Length).MapErr(static _ => Unit.Value),
        };
        if (shape != "Array") source.Add(Effect<EmptyEnvironment>.Fail("later"));
        var result = await group.RunAsync(CancellationToken.None);

        // Then
        Assert.True(result.TryGetOk(out var length));
        Assert.Equal(0, length);
    }
    [Theory]
    [InlineData("All")]
    [InlineData("AllSettled")]
    [InlineData("AllErrors")]
    public void Group_InputEnumerationThrows_ThrowsDuringConstructionWithoutRunningChildren(string operation)
    {
        // Given
        var calls = 0;
        var defect = new IOException("enumeration");
        Effect<int, string, EmptyEnvironment> valid = Effect<EmptyEnvironment>.Sync(() => ++calls);
        IEnumerable<Effect<int, string, EmptyEnvironment>> Inputs()
        {
            yield return valid;
            throw defect;
        }

        // When
        var exception = Record.Exception(() =>
        {
            switch (operation)
            {
                case "All": _ = Effect.All(Inputs()); break;
                case "AllSettled": _ = Effect.AllSettled(Inputs()); break;
                default: _ = Effect.AllErrors(Inputs()); break;
            }
        });

        // Then
        Assert.Same(defect, exception);
        Assert.Equal(0, calls);
    }

    private static IEnumerable<Effect<int, string, EmptyEnvironment>> Enumerate(IEnumerable<Effect<int, string, EmptyEnvironment>> source)
    {
        foreach (var effect in source) yield return effect;
    }

}
