using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Moderato.Effects.SourceGenerator;

namespace Moderato.Effects.SourceGenerator.Tests;

public class CatchTypeIdentityTests
{
    [Theory]
    [InlineData("(int, int)", "(int A, int B)", false, false, false)]
    [InlineData("(int, int)", "(int A, int B)", false, false, true)]
    [InlineData("(int, int)", "(int A, int B)", false, true, false)]
    [InlineData("(int, int)", "(int A, int B)", false, true, true)]
    [InlineData("(int, int)", "(int A, int B)", true, false, false)]
    [InlineData("(int, int)", "(int A, int B)", true, false, true)]
    [InlineData("(int, int)", "(int A, int B)", true, true, false)]
    [InlineData("(int, int)", "(int A, int B)", true, true, true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false, false, false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false, false, true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false, true, false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false, true, true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true, false, false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true, false, true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true, true, false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true, true, true)]
    public void Catch_EquivalentSourceType_PreservesCompilerDiagnostics(string original, string alias, bool metadata, bool manual, bool wrongHandler)
    {
        // Given
        const string DEPENDENCY = "namespace App; public union Source<T>(T, Guid);";
        var rest = manual ? $$"""
            public readonly struct Remaining : IUnionRest<Remaining, Source<{{original}}>, Guid>
            {
                static bool IUnionRest<Remaining, Source<{{original}}>, Guid>.TrySplit(
                    Source<{{original}}> source, out Guid caught, out Remaining rest) => throw new NotSupportedException();
            }
            """ : $"[RestOf<Source<{original}>>] public partial union Remaining({original});";
        var handler = wrongHandler ? """Effect<EmptyEnvironment>.Succeed("wrong")""" : "Effect<EmptyEnvironment>.Succeed(42)";
        var target = $$"""
            namespace App;
            {{rest}}
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source<{{alias}}>, EmptyEnvironment> effect)
                    => effect.Catch<Guid, Remaining>(_ => {{handler}});
            }
            """;
        var control = target.Replace($"Source<{alias}>, EmptyEnvironment> effect", $"Source<{original}>, EmptyEnvironment> effect");
        var native = Compiler.Build(metadata ? control : DEPENDENCY + control.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);
        Assert.Equal(wrongHandler, native.Errors.Any());

        // When
        var build = Compiler.Build(metadata ? target : DEPENDENCY + target.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);

        // Then
        Assert.DoesNotContain("MEFF308", build.Ids);
        Assert.DoesNotContain("MEFF309", build.Ids);
        Assert.Equal(native.Errors.Where(static id => id.StartsWith("CS", StringComparison.Ordinal)).Order(),
            build.Errors.Where(static id => id.StartsWith("CS", StringComparison.Ordinal)).Order());
    }

    [Theory]
    [InlineData("(int, int)", "(int A, int B)", false)]
    [InlineData("(int, int)", "(int A, int B)", true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true)]
    public void Catch_EquivalentSourceAndWrongCase_ReportsActualMissingCase(string original, string alias, bool metadata)
    {
        // Given
        const string DEPENDENCY = "namespace App; public union Source<T>(T, Guid);";
        var target = $$"""
            namespace App;
            [RestOf<Source<{{original}}>>] public partial union Remaining({{original}});
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source<{{alias}}>, EmptyEnvironment> effect)
                    => effect.Catch<{{original}}, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(metadata ? target : DEPENDENCY + target.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);

        // Then
        Assert.Contains("Guid", build.Single("MEFF309").GetMessage());
        Assert.DoesNotContain("MEFF308", build.Ids);
        Assert.Contains("CS0315", build.Errors);
    }

    [Fact]
    public void Catch_SourceNamesMatchAcrossAssemblies_StillReportsMissingRest()
    {
        // Given
        const string DEPENDENCY = """
            namespace App;
            public union Source(int, Guid);
            public readonly struct Remaining : IUnionRest<Remaining, Source, Guid>
            {
                static bool IUnionRest<Remaining, Source, Guid>.TrySplit(Source source, out Guid caught, out Remaining rest)
                    => throw new NotSupportedException();
            }
            """;
        const string TARGET = """
            namespace App;
            public union Source(int, Guid);
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source, EmptyEnvironment> effect)
                    => effect.Catch<Guid, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(TARGET, DEPENDENCY);

        // Then
        build.Single("MEFF308");
        Assert.Contains("CS0315", build.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Catch_ManualRestWithComplexCase_SkipsValidCallsAndBoundsInvalidCalls(bool matchingCase)
    {
        // Given
        var shape = "int";
        for (var depth = 0; depth < 7; depth++) shape = $"Pair<{shape}, {shape}>";
        shape += "[][]";
        var caught = matchingCase ? shape : "Guid";
        var source = $$"""
            namespace App;
            public record Pair<T, U>;
            public union Source({{shape}}, Guid);
            public readonly struct Remaining : IUnionRest<Remaining, Source, {{shape}}>
            {
                static bool IUnionRest<Remaining, Source, {{shape}}>.TrySplit(Source source, out {{shape}} caught, out Remaining rest)
                    => throw new NotSupportedException();
            }
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source, EmptyEnvironment> effect)
                    => effect.Catch<{{caught}}, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("AD0001", build.Ids);
        if (matchingCase)
        {
            Assert.Empty(build.Errors);
        }
        else
        {
            build.Single("MEFF007");
            Assert.DoesNotContain("MEFF308", build.Ids);
        }
    }

    [Theory]
    [InlineData("(int, int)", "(int A, int B)", true)]
    [InlineData("(int, int)", "(int A, int B)", false)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", true)]
    [InlineData("System.Collections.Generic.List<object>", "System.Collections.Generic.List<dynamic>", false)]
    public async Task Catch_BeforeGeneratorsRun_OnlyReportsWrongCase(string original, string alias, bool matchingCase)
    {
        // Given
        var caught = matchingCase ? "Guid" : original;
        var source = $$"""
            using System;
            using Moderato.Effects;
            public union Source<T>(T, Guid);
            [RestOf<Source<{{original}}>>] public partial union Remaining({{original}});
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source<{{alias}}>, EmptyEnvironment> effect)
                    => effect.Catch<{{caught}}, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("UserCode", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analysis = compilation.WithAnalyzers([new CatchAnalyzer()]);

        // When
        var diagnostics = await analysis.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        // Then
        if (matchingCase)
        {
            Assert.Empty(diagnostics);
        }
        else
        {
            Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "MEFF309");
        }
    }
}
