namespace Moderato.Effects.SourceGenerator.Tests;

public class TupleCaseTests
{
    [Theory]
    [InlineData("(int X, int Y)")]
    [InlineData("(int, int)")]
    [InlineData("(int X, (string Name, int Code) Detail)")]
    [InlineData("(int X, int Y)?")]
    [InlineData("(int A, int B, int C, int D, int E, int F, int G, int H)")]
    [InlineData("(string? Name, int? Code)")]
    [InlineData("(int X, int Y)[]")]
    public void Error_TupleCase_Compiles(string tuple)
    {
        // Given
        var source = $$"""
            namespace App;
            [Error] public partial union Problem({{tuple}}, string);
            """;
        Assert.Empty(Compiler.Build(source.Replace("[Error]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains($"out {tuple} value", build.Generated["App.Problem.Error.g.cs"]);
    }

    [Theory]
    [InlineData("(int X, int Y)")]
    [InlineData("(int, int)")]
    [InlineData("(int X, (string Name, int Code) Detail)")]
    [InlineData("(int X, int Y)?")]
    [InlineData("(int A, int B, int C, int D, int E, int F, int G, int H)")]
    [InlineData("(string? Name, int? Code)")]
    [InlineData("(int X, int Y)[]")]
    public void RestOf_TupleCaughtOrRetained_Compiles(string tuple)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Problem({{tuple}}, string);
            [RestOf<Problem>] public partial union TextOnly(string);
            [RestOf<Problem>] public partial union TupleOnly({{tuple}});
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains($"out {tuple} caught", build.Generated["App.TextOnly.RestOf.g.cs"]);
    }

    [Fact]
    public void Error_TupleInsideAnInnerUnion_Compiles()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Detail((int X, int Y), string);
            [Error] public partial union Problem(Detail, bool);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("out (int X, int Y) value", build.Generated["App.Problem.Error.g.cs"]);
    }

}
