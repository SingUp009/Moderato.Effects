using Moderato.Results;

namespace Moderato.Effects.Tests;

public class GroupResultTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task All_RunAgain_ReturnsValuesUnaffectedByEarlierResultChanges(int count)
    {
        // Given
        var expected = Enumerable.Range(0, count).ToArray();
        var work = Effect.All(expected.Select(Effect<EmptyEnvironment>.Succeed));

        // When
        var first = (await work.RunAsync(CancellationToken.None)).UnwrapOr([]);
        if (count > 0) first[0] = -1;
        var second = (await work.RunAsync(CancellationToken.None)).UnwrapOr([]);

        // Then
        Assert.Equal(expected, second);
        if (count > 0) Assert.NotSame(first, second);
    }

    [Fact]
    public async Task AllErrors_MixedChildrenRunAgain_ReturnsOnlyErrorsInInputOrder()
    {
        // Given
        Effect<int, string, EmptyEnvironment>[] children =
        [
            Effect<EmptyEnvironment>.Succeed(1),
            Effect<EmptyEnvironment>.Fail("first"),
            Effect<EmptyEnvironment>.Succeed(2),
            Effect<EmptyEnvironment>.Fail("second"),
            Effect<EmptyEnvironment>.Succeed(3),
        ];
        var work = Effect.AllErrors(children);

        // When
        var first = await work.RunAsync(CancellationToken.None);
        var second = await work.RunAsync(CancellationToken.None);

        // Then
        Assert.True(first.TryGetErr(out var firstErrors));
        Assert.True(second.TryGetErr(out var secondErrors));
        Assert.Equal(["first", "second"], firstErrors);
        Assert.Equal(firstErrors, secondErrors);
        Assert.NotSame(firstErrors, secondErrors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public async Task AllSettled_RunAgain_ReturnsIndependentExitsInInputOrder(int count)
    {
        // Given
        Effect<int, string, EmptyEnvironment>[] children = [.. Enumerable.Range(0, count).Select(static i =>
            i % 2 == 0 ? (Effect<int, string, EmptyEnvironment>)Effect<EmptyEnvironment>.Succeed(i) : Effect<EmptyEnvironment>.Fail(i.ToString()))];
        var work = Effect.AllSettled(children);

        // When
        var first = (await work.RunAsync(CancellationToken.None)).UnwrapOr([]);
        var second = (await work.RunAsync(CancellationToken.None)).UnwrapOr([]);

        // Then
        Assert.Equal(count, second.Length);
        Assert.Equal(first, second);
        for (var i = 0; i < count; i++)
        {
            Exit<int, string> expected = i % 2 == 0 ? new Ok<int>(i) : new Err<string>(i.ToString());
            Assert.Equal(expected, second[i]);
        }
        if (count > 0) Assert.NotSame(first, second);
    }
}
