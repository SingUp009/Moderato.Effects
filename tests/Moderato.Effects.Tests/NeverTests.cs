using System.Reflection;

namespace Moderato.Effects.Tests;

public class NeverTests
{
    [Fact]
    public void Never_HasNoValues()
    {
        var type = typeof(Never);

        Assert.False(type.IsValueType);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }
}
