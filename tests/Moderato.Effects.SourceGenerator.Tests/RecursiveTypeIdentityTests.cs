namespace Moderato.Effects.SourceGenerator.Tests;

public class RecursiveTypeIdentityTests
{
    [Theory]
    [InlineData("object", "dynamic", false)]
    [InlineData("dynamic", "object", false)]
    [InlineData("(int A, int B)", "(int, int)", false)]
    [InlineData("(int, int)", "(int A, int B)", false)]
    [InlineData("object", "dynamic", true)]
    [InlineData("dynamic", "object", true)]
    [InlineData("(int A, int B)", "(int, int)", true)]
    [InlineData("(int, int)", "(int A, int B)", true)]
    public void Error_SelfConstructorUsesEquivalentType_PreservesLeafConversion(string entry, string recursive, bool metadata)
    {
        // Given
        var dependency = $$"""
            namespace App;
            public sealed record Payload<T>(T Value);
            public union Inner<T>(Payload<T>, Inner<{{recursive}}>);
            """;
        var use = $$"""
            namespace App;
            [Error] public partial union Outer(Inner<{{entry}}>);
            public static class Use
            {
                public static Effect<int, Outer, EmptyEnvironment> Widen(Effect<int, Payload<{{entry}}>, EmptyEnvironment> effect)
                    => effect.Widen<Outer>();
            }
            """;

        // When
        var build = Compiler.Build(metadata ? use : dependency + use.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("MEFF003", build.Ids);
        Assert.Contains("App.Outer.Error.g.cs", build.Generated.Keys);
    }

    [Theory]
    [InlineData("object", "dynamic", false)]
    [InlineData("dynamic", "object", false)]
    [InlineData("(int A, int B)", "(int, int)", false)]
    [InlineData("(int, int)", "(int A, int B)", false)]
    [InlineData("object", "dynamic", true)]
    [InlineData("dynamic", "object", true)]
    [InlineData("(int A, int B)", "(int, int)", true)]
    [InlineData("(int, int)", "(int A, int B)", true)]
    public void Error_MutualCycleUsesEquivalentType_PreservesLeafConversion(string entry, string recursive, bool metadata)
    {
        // Given
        var dependency = $$"""
            namespace App;
            public sealed record Payload<T>(T Value);
            public union Inner<T>(Payload<T>, Other<{{recursive}}>);
            public union Other<T>(Inner<{{recursive}}>);
            """;
        var use = $$"""
            namespace App;
            [Error] public partial union Outer(Inner<{{entry}}>);
            public static class Use
            {
                public static Effect<int, Outer, EmptyEnvironment> Widen(Effect<int, Payload<{{entry}}>, EmptyEnvironment> effect)
                    => effect.Widen<Outer>();
            }
            """;

        // When
        var build = Compiler.Build(metadata ? use : dependency + use.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, diagnostic => diagnostic.Id == "MEFF003");
        Assert.Contains("App.Outer.Error.g.cs", build.Generated.Keys);
    }
}
