using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class ValidationOriginTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AllErrors_MemberOrigins_KeepsOrderAndClassificationTogether(bool firstConsequential, bool lastConsequential, bool reverse)
    {
        // Given
        var first = Outcome<int, string>.Err("first", firstConsequential);
        var outside = Outcome<int[], string[]>.Err(["outside"]);
        var last = Outcome<int, string>.Err("last", lastConsequential);
        Effect<int, string, EmptyEnvironment> a = new((_, _) => new(first));
        Effect<int, string, EmptyEnvironment> b = new((_, _) => new(last));
        Effect<int[], string[], EmptyEnvironment> sibling = new((_, _) => new(outside));
        var validation = Effect.AllErrors<int, string, EmptyEnvironment>(reverse ? [b, a] : [a, b], Concurrency.Parallel);

        // When
        var exit = await Effect.Race(validation, sibling).RunExitAsync(CancellationToken.None);

        // Then
        Assert.True(exit.TryGetValue(out Err<string[]> error));
        Assert.Equal(firstConsequential ? ["outside"] : reverse ? ["last", "first"] : ["first", "last"], error.Error);
    }
}
