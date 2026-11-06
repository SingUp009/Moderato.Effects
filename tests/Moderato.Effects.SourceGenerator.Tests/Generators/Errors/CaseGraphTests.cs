using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Moderato.Effects.SourceGenerator;

namespace Moderato.Effects.SourceGenerator.Tests;

public class CaseGraphTests
{
    [Theory]
    [InlineData("Error")]
    [InlineData("RestOf<Source>")]
    public void Generate_ExpandingTypeArguments_ReportsLimitWithoutPartialOutput(string attribute)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Chain<T>(T, Chain<System.Collections.Generic.List<T>>);
            public union Source(Chain<int>, string);
            [{{attribute}}] public partial union Target(Chain<int>);
            [Error] public partial union Good(int);
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("union generation limit", build.Single("MEFF007").GetMessage());
        Assert.DoesNotContain(build.Generated.Keys, static key => key.Contains("Target"));
        Assert.Contains("App.Good.Error.g.cs", build.Generated.Keys);
        Assert.DoesNotContain("CS8785", build.Ids);
    }

    [Theory]
    [InlineData("Chain<T>(T, Chain<int>)", "Chain<string>")]
    [InlineData("Chain<T, U>(T, Chain<U, System.Collections.Generic.List<int>>)", "Chain<string, int>")]
    public void Generate_ConvergingTypeArguments_PreservesTransitiveCase(string declaration, string entry)
    {
        // Given
        var source = $$"""
            namespace App;
            public union {{declaration}};
            [Error] public partial union Target({{entry}}, System.Guid);
            public static class Use
            {
                public static Effect<int, Target, EmptyEnvironment> Fail()
                    => Effect<EmptyEnvironment>.Fail(42).Widen<Target>();
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionCase<Target, int>", build.Generated["App.Target.Error.g.cs"]);
    }

    [Fact]
    public void RestOf_CatchesWholeExpandingBranch_GeneratesWithoutInspectingItsDescendants()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Chain<T>(T, Chain<System.Collections.Generic.List<T>>);
            public union Source(Chain<int>, string);
            [RestOf<Source>] public partial union Remaining(string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.Remaining.RestOf.g.cs", build.Generated.Keys);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Generate_TypeExpressionAtBoundary_ReportsOnlyAboveLimit(int arrays, bool exceedsLimit)
    {
        // Given
        var shape = "int";
        for (var depth = 0; depth < 7; depth++) shape = $"Pair<{shape}, {shape}>";
        shape += string.Concat(Enumerable.Repeat("[]", arrays));
        var source = $$"""
            namespace App;
            public record Pair<T, U>;
            [Error] public partial union Target({{shape}});
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        if (exceedsLimit)
        {
            Assert.Contains("256 nodes in a type expression", build.Single("MEFF007").GetMessage());
            Assert.Empty(build.Generated);
        }
        else
        {
            Assert.Empty(build.Errors);
        }
    }

    [Theory]
    [InlineData(505, false)]
    [InlineData(506, true)]
    public void Generate_CasePathWorkAtBoundary_ReportsOnlyAboveLimit(int padding, bool exceedsLimit)
    {
        // Given
        var source = new System.Text.StringBuilder("namespace App;\n");
        for (var level = 0; level < 6; level++)
        {
            source.AppendLine($"public union Node{level}(Left{level}, Right{level});");
            source.AppendLine($"public union Left{level}(Node{level + 1});");
            source.AppendLine($"public union Right{level}(Node{level + 1});");
        }
        source.AppendLine("public union Node6(int);");
        for (var index = 0; index < padding; index++) source.AppendLine($"public record Padding{index};");
        source.AppendLine($"[Error] public partial union Target(Node0, {string.Join(", ", Enumerable.Range(0, padding).Select(static index => $"Padding{index}"))});");

        // When
        var build = Compiler.Build(source.ToString());

        // Then
        if (exceedsLimit)
        {
            Assert.Contains("4096 case-path segments", build.Single("MEFF007").GetMessage());
            Assert.Empty(build.Generated);
        }
        else
        {
            Assert.Empty(build.Errors);
        }
    }

    [Fact]
    public async Task Catch_ExpandingRestBeforeGeneratorsRun_ReportsLimit()
    {
        // Given
        var compilation = CatchCompilation();
        var analysis = compilation.WithAnalyzers([new CatchAnalyzer()]);

        // When
        var diagnostics = await analysis.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "MEFF007");
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
    }

    [Fact]
    public async Task Catch_CanceledAnalysis_StopsWithoutAnalyzerFailure()
    {
        // Given
        var analysis = CatchCompilation().WithAnalyzers([new CatchAnalyzer()]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // When
        var exception = await Record.ExceptionAsync(() => analysis.GetAnalyzerDiagnosticsAsync(cancellation.Token));

        // Then
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    private static CSharpCompilation CatchCompilation()
    {
        const string SOURCE = """
            using Moderato.Effects;
            public union Chain<T>(T, Chain<System.Collections.Generic.List<T>>);
            public union Source(Chain<int>, string);
            [RestOf<Source>] public partial union Remaining(Chain<int>);
            public static class Use
            {
                public static object Catch(Effect<int, Source, EmptyEnvironment> effect)
                    => effect.Catch<string, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;
        ImmutableArray<MetadataReference> references =
        [
            .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path)),
        ];
        return CSharpCompilation.Create("UserCode", [CSharpSyntaxTree.ParseText(SOURCE, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
