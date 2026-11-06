namespace Moderato.Effects.SourceGenerator.Tests;

public class DynamicPatternTests
{
    [Theory]
    [InlineData("dynamic[]", "object[]")]
    [InlineData("System.Collections.Generic.List<dynamic>", "global::System.Collections.Generic.List<object>")]
    [InlineData("(dynamic Value, int Code)", "global::System.ValueTuple<object, int>")]
    public void Error_DynamicCase_CompilesWithObjectPattern(string caseType, string pattern)
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
        Assert.Contains($"if (self is {pattern} found)", build.Generated["App.Problem.Error.g.cs"]);
    }

    [Theory]
    [InlineData("dynamic[]", "object[]")]
    [InlineData("System.Collections.Generic.List<dynamic>", "global::System.Collections.Generic.List<object>")]
    [InlineData("(dynamic Value, int Code)", "global::System.ValueTuple<object, int>")]
    public void RestOf_RetainedDynamicCase_CompilesWithObjectPattern(string caseType, string pattern)
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
        Assert.Contains($"case {pattern} other0:", build.Generated["App.Rest.RestOf.g.cs"]);
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic?")]
    public void Error_NestedDynamicCase_Compiles(string caseType)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Detail({{caseType}});
            [Error] public partial union Problem(Detail, bool);
            """;
        Assert.Empty(Compiler.Build(source.Replace("[Error]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("step0 is object found", build.Generated["App.Problem.Error.g.cs"]);
    }
}
