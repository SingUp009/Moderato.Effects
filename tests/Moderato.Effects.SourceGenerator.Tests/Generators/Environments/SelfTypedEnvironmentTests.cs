namespace Moderato.Effects.SourceGenerator.Tests;

public class SelfTypedEnvironmentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Environment_ClassWithOwnTypeParameter_ImplementsIHas(bool anotherService, bool nullable)
    {
        // Given
        var source = $$"""
            namespace App;
            [Environment]
            public sealed partial class Env(Env{{(nullable ? "?" : "")}} parent{{(anotherService ? ", string name" : "")}});
            public static class Use
            {
                public static Env? Parent(Env env) => env.Resolve<Env?>();
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }
    [Theory]
    [InlineData("[Environment] public sealed partial record Env(string Name);", false)]
    [InlineData("[Environment] public partial record struct Env(int Id);", false)]
    [InlineData("[Environment] public sealed partial record Env;", true)]
    [InlineData("[Environment] public sealed partial class Env { public Env(Env? parent) { } }", true)]
    public void Environment_ConstructorKinds_RequiresAPrimaryConstructor(string declaration, bool missing)
    {
        // Given
        var source = $"namespace App; {declaration}";

        // When
        var build = Compiler.Build(source);

        // Then
        if (missing)
        {
            Assert.Equal(["MEFF102"], build.Errors);
            Assert.Empty(build.Generated);
        }
        else
        {
            Assert.Empty(build.Errors);
            Assert.NotEmpty(build.Generated);
        }
    }
}
