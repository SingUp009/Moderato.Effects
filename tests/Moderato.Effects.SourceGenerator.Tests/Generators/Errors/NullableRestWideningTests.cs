namespace Moderato.Effects.SourceGenerator.Tests;

public class NullableRestWideningTests
{
    [Theory]
    [InlineData("int", "double", false)]
    [InlineData("int", "double", true)]
    [InlineData("int", "long", false)]
    [InlineData("int", "long", true)]
    [InlineData("short", "int", false)]
    [InlineData("short", "int", true)]
    [InlineData("float", "double", false)]
    [InlineData("float", "double", true)]
    [InlineData("uint", "long", false)]
    [InlineData("uint", "long", true)]
    [InlineData("long", "decimal", false)]
    [InlineData("long", "decimal", true)]
    [InlineData("int", "bool", false)]
    [InlineData("int", "bool", true)]
    public void RestOf_NullableValueCase_CompilesWithoutWideningAmbiguity(string valueType, string sibling, bool grouped)
    {
        // Given
        var group = grouped ? $"[Error] public partial union Group({sibling}, {valueType}?);" : "";
        var restCases = grouped ? "Group" : $"{sibling}, {valueType}?";
        var source = $$"""
            namespace App;
            public sealed record Leaf(int Id);
            [Error] public partial union Top(Leaf, {{sibling}}, {{valueType}}?);
            {{group}}
            [Error] [RestOf<Top>] public partial union Rest({{restCases}});
            """;
        Assert.Empty(Compiler.Build(source.Replace("[Error]", "").Replace("[RestOf<Top>]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }
}
