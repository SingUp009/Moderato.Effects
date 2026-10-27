using System.Runtime.CompilerServices;
using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

public class GroupSelectionTests
{
    [Theory]
    [InlineData(nameof(Effect.All))]
    [InlineData(nameof(Effect.AllErrors))]
    [InlineData(nameof(Effect.AllSettled))]
    [InlineData(nameof(Effect.Race))]
    [InlineData(nameof(Effect.RaceFirst))]
    public async Task RunExitAsync_GroupSelectionEncountersInvalidInternalOutcome_ReturnsDie(string operation)
    {
        // Given
        // Public factories cannot create this state; it exercises the defensive selection boundary.
        Effect<int, string, EmptyEnvironment> invalid = new((_, _) => new(default(Outcome<int, string>)));

        // When
        object exit = operation switch
        {
            nameof(Effect.All) => await Effect.All([invalid]).RunExitAsync(CancellationToken.None),
            nameof(Effect.AllErrors) => await Effect.AllErrors([invalid]).RunExitAsync(CancellationToken.None),
            nameof(Effect.AllSettled) => await Effect.AllSettled([invalid]).RunExitAsync(CancellationToken.None),
            nameof(Effect.Race) => await Effect.Race(invalid, invalid).RunExitAsync(CancellationToken.None),
            nameof(Effect.RaceFirst) => await Effect.RaceFirst(invalid, invalid).RunExitAsync(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        // Then
        var die = Assert.IsType<Die>(Assert.IsAssignableFrom<IUnion>(exit).Value);
        Assert.IsType<NullReferenceException>(die.Exception);
        Assert.Empty(die.Suppressed);
        Assert.Equal(0, die.OmittedSuppressed);
    }
}
