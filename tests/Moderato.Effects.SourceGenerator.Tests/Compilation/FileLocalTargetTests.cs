namespace Moderato.Effects.SourceGenerator.Tests;

public class FileLocalTargetTests
{
    [Theory]
    [InlineData("Environment", "class Target(TimeProvider Clock)")]
    [InlineData("Environment", "record Target(TimeProvider Clock)")]
    [InlineData("Environment", "struct Target(TimeProvider Clock)")]
    [InlineData("Environment", "record struct Target(TimeProvider Clock)")]
    [InlineData("Error", "union Target(int, string)")]
    [InlineData("RestOf<Source>", "union Target(int)")]
    public void Generator_FileLocalTarget_ReportsMEFF006WithoutGeneratingAType(string attribute, string declaration)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Source(int, string);
            [{{attribute}}] file partial {{declaration}};
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF006"], build.Errors);
        var diagnostic = build.Single("MEFF006");
        Assert.Contains("file-local type 'Target'", diagnostic.GetMessage());
        Assert.Equal("Target", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Empty(build.Generated);
    }
    [Fact]
    public void RestOf_FileLocalSource_ReportsMEFF006WithoutGeneratingAType()
    {
        // Given
        var source = """
            namespace App;
            file union Source(int, string);
            [RestOf<Source>] internal partial union Rest(int);
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF006"], build.Errors);
        var diagnostic = build.Single("MEFF006");
        Assert.Contains("file-local type 'Source'", diagnostic.GetMessage());
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData("public union Source<T>(T, string);", "Source<int>")]
    [InlineData("public class Container { public union Source(int, string); }", "Container.Source")]
    public void RestOf_AccessibleSource_Compiles(string declaration, string type)
    {
        // Given
        var source = $$"""
            namespace App;
            {{declaration}}
            [RestOf<{{type}}>] public partial union Rest(int);
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.NotEmpty(build.Generated);
    }
}
