using System.Reflection;

namespace Moderato.Effects.Tests;

public class NeverTests
{
    [Fact]
    public void Never_Declaration_HasNoWayToCreateAValue()
    {
        // Given
        var type = typeof(Never);

        // When
        var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Then
        Assert.False(type.IsValueType);
        Assert.True(type.IsSealed);
        Assert.Empty(publicConstructors);
    }
}
