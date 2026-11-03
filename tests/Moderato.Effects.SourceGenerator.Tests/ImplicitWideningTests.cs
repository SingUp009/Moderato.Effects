namespace Moderato.Effects.SourceGenerator.Tests;

public class ImplicitWideningTests
{
    [Theory]
    [InlineData(false, false, "object")]
    [InlineData(false, false, "object?")]
    [InlineData(false, true, "object")]
    [InlineData(false, true, "object?")]
    [InlineData(true, false, "object")]
    [InlineData(true, false, "object?")]
    [InlineData(true, true, "object")]
    [InlineData(true, true, "object?")]
    public void Error_IntermediateUnionWithObjectCase_CompilesWithoutNullableCastWarnings(bool metadata, bool valueType, string leaf)
    {
        // Given
        var dependency = $$"""
            namespace App;
            [System.Runtime.CompilerServices.Union]
            public {{(valueType ? "readonly struct" : "sealed class")}} Inner
            {
                public Inner({{leaf}} value) => Value = value;
                public object? Value { get; }
            }
            """;
        var target = $$"""
            namespace App;
            [Error] public partial union Outer(Inner);
            public static class Use
            {
                public static Effect<Never, Outer, EmptyEnvironment> Widen()
                    => Effect<EmptyEnvironment>.Fail<{{leaf}}>(default!).Widen<Outer>();
            }
            """;

        // When
        var build = Compiler.Build(metadata ? target : dependency + target.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain(build.Diagnostics, static diagnostic => diagnostic.Id is "CS8600" or "CS8603" or "CS8604" or "CS8605");
    }
}
