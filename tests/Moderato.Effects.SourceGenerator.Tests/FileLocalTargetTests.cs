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
}
