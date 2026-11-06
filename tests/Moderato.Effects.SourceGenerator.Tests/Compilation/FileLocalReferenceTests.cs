namespace Moderato.Effects.SourceGenerator.Tests;

public class FileLocalReferenceTests
{
    [Theory]
    [InlineData("[Error] public partial union Target(Secret, string);")]
    [InlineData("[Error] public partial union Target(Secret[], string);")]
    [InlineData("[Error] public partial union Target(System.Collections.Generic.List<Secret>, string);")]
    [InlineData("[Error] public partial union Target((Secret Item, int Id), string);")]
    [InlineData("public union Inner(Secret, int); [Error] public partial union Target(Inner, string);")]
    [InlineData("file union Hidden(int, bool); public union Inner(Hidden, string); [Error] public partial union Target(Inner, long);")]
    [InlineData("public union Source(Secret, string); [RestOf<Source>] public partial union Target(string);")]
    [InlineData("public union Source(Secret, string); [RestOf<Source>] public partial union Target(Secret);")]
    [InlineData("public union Source(int, string); file union Hidden(int); [RestOf<Source>] public partial union Target(Hidden);")]
    [InlineData("public union Source<T>(T, string); [RestOf<Source<Secret>>] public partial union Target(string);")]
    [InlineData("file class Outer { public record Nested(int Id); } [Error] public partial union Target(Outer.Nested, string);")]
    [InlineData("public class Outer<T> { public record Nested(int Id); } [Error] public partial union Target(Outer<Secret>.Nested, string);")]
    public void Generator_FileLocalReference_ReportsMEFF006WithoutEmitting(string declaration)
    {
        // Given
        var source = $$"""
            namespace App;
            file record Secret(int Id);
            {{declaration}}
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF006"], build.Errors);
        Assert.Empty(build.Generated);
        Assert.DoesNotContain(build.Diagnostics, d => d.Location.SourceTree?.FilePath.EndsWith(".g.cs") == true);
    }

    [Theory]
    [InlineData("file interface ISecret; public sealed class Concrete : ISecret;", "ISecret", "ISecret")]
    [InlineData("file class Outer { public interface ISecret; } public sealed class Concrete : Outer.ISecret;", "Outer.ISecret", "Outer")]
    public void Environment_FileLocalExposedType_ReportsMEFF006AtParameter(string declarations, string exposed, string hidden)
    {
        // Given
        var source = $$"""
            namespace App;
            {{declarations}}
            [Environment] public partial class Target([Expose<{{exposed}}>] Concrete Service);
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF006"], build.Errors);
        var diagnostic = build.Single("MEFF006");
        Assert.Contains($"file-local type '{hidden}'", diagnostic.GetMessage());
        Assert.Equal("Service", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData("[Error] public partial union Target(Visible, string);")]
    [InlineData("[Error] public partial union Target(System.Collections.Generic.List<Visible>, string);")]
    [InlineData("public union Inner(Visible, int); [Error] public partial union Target(Inner, string);")]
    [InlineData("public union Inner(Secret, int); public union Source(Inner, string); [RestOf<Source>] public partial union Target(string);")]
    [InlineData("public union Inner(Secret, int); public union Source(Inner, string); [RestOf<Source>] public partial union Target(Inner);")]
    [InlineData("public union Inner(Secret, int); [Environment] public partial class Target(Inner Service);")]
    [InlineData("public union Inner(Secret, int); [Error] public partial union Target(System.Collections.Generic.List<Inner>, string);")]
    [InlineData("file union Hidden(int); public union Left(Hidden); public union Right(Hidden); [Error] public partial union Target(int, Left, Right);")]
    public void Generator_OnlyNamesAccessibleTypes_Compiles(string declaration)
    {
        // Given
        var source = $$"""
            namespace App;
            file record Secret(int Id);
            public record Visible(int Id);
            {{declaration}}
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.NotEmpty(build.Generated);
    }
}
