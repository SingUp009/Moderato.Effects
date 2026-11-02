namespace Moderato.Effects.SourceGenerator.Tests;

public class DynamicPatternTests
{
    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic?")]
    [InlineData("dynamic[]")]
    [InlineData("System.Collections.Generic.List<dynamic>")]
    [InlineData("(dynamic Value, int Code)")]
    public void Error_DynamicCase_Compiles(string caseType)
    {
        // Given
        var source = $$"""
            namespace App;
            [Error] public partial union Problem({{caseType}}, string);
            """;
        Assert.Empty(Compiler.Build(source.Replace("[Error]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic?")]
    [InlineData("dynamic[]")]
    [InlineData("System.Collections.Generic.List<dynamic>")]
    [InlineData("(dynamic Value, int Code)")]
    public void RestOf_RetainedDynamicCase_Compiles(string caseType)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Problem({{caseType}}, string);
            [RestOf<Problem>] public partial union Rest({{caseType}});
            """;
        Assert.Empty(Compiler.Build(source.Replace("[RestOf<Problem>]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic?")]
    public void Error_NestedDynamicCase_Compiles(string caseType)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Detail({{caseType}}, string);
            [Error] public partial union Problem(Detail, bool);
            """;
        Assert.Empty(Compiler.Build(source.Replace("[Error]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }
}
