using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Moderato.Effects.SourceGenerator;

namespace Moderato.Effects.SourceGenerator.Tests;

public class DisjointUnionTests
{
    public static TheoryData<string, string, string> OverlappingCases => new()
    {
        { "public class Base; public sealed class Derived : Base;", "Base", "Derived" },
        { "public interface IMarker; public sealed class Impl : IMarker;", "IMarker", "Impl" },
        { "public sealed class Leaf;", "Leaf", "object" },
        { "", "int", "int?" },
        { "public interface IA; public interface IB; public sealed class Both : IA, IB;", "IA", "IB" },
        { "public class Base; public interface IMarker; public sealed class Derived : Base, IMarker;", "Base", "IMarker" },
        { "", "string[]", "object[]" },
        { "", "System.Collections.Generic.IEnumerable<string>", "System.Collections.Generic.IEnumerable<object>" },
        { "", "object", "int" },
        { "", "System.Enum", "System.DayOfWeek" },
        { "", "System.ValueType", "int" },
        { "", "int?", "System.IComparable<int>" },
        { "", "object[]", "System.Collections.Generic.IEnumerable<string>" },
        { "", "int[]", "uint[]" },
        { "", "System.DayOfWeek[]", "int[]" },
        { "", "int[]", "System.Collections.Generic.IList<uint>" },
        { "", "dynamic", "string" },
        { "", "dynamic?", "string" },
        { "", "object?", "string" },
    };

    public static TheoryData<string, string, string> DisjointCases => new()
    {
        { "public class A; public class B;", "A", "B" },
        { "public interface IMarker; public sealed class Leaf;", "IMarker", "Leaf" },
        { "public interface IMarker;", "IMarker", "int" },
        { "", "int", "long" },
        { "", "string[]", "int[]" },
        { "", "int[]", "System.Collections.Generic.IEnumerable<string>" },
        { "", "System.DayOfWeek", "int" },
        { "", "int[,]", "uint[]" },
        { "", "bool[]", "byte[]" },
        { "", "char[]", "ushort[]" },
    };

    [Theory]
    [MemberData(nameof(OverlappingCases))]
    public void Error_OverlappingCases_ReportsDiagnosticInsteadOfGeneratingAmbiguousConversions(string declarations, string first, string second)
    {
        // Given
        var source = $"namespace App; {declarations} [Error] public partial union Target({first}, {second});";

        // When
        var build = Compiler.Build(source);

        // Then
        build.Single("MEFF008");
        Assert.Empty(build.Generated);
        Assert.DoesNotContain("CS8120", build.Errors);
    }

    [Theory]
    [MemberData(nameof(OverlappingCases))]
    public void RestOf_OverlappingSourceCases_ReportsDiagnosticInsteadOfUnreachableGeneratedSwitch(string declarations, string first, string second)
    {
        // Given
        var source = $"namespace App; {declarations} public union Source({first}, {second}); [RestOf<Source>] public partial union Remaining({second});";

        // When
        var build = Compiler.Build(source);

        // Then
        build.Single("MEFF008");
        Assert.Empty(build.Generated);
        Assert.DoesNotContain("CS8120", build.Errors);
    }

    [Theory]
    [MemberData(nameof(DisjointCases))]
    public void Error_DisjointCases_PreservesGeneratedConversions(string declarations, string first, string second)
    {
        // Given
        var source = $"namespace App; {declarations} [Error] public partial union Target({first}, {second});";

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.Target.Error.g.cs", build.Generated.Keys);
    }

    [Fact]
    public void Error_OverlappingCasesInReferencedUnion_ReportsDiagnostic()
    {
        // Given
        const string DEPENDENCY = "namespace App; public class Base; public sealed class Derived : Base; public union Inner(Base, Derived);";
        const string SOURCE = "namespace App; [Error] public partial union Target(Inner, string);";

        // When
        var build = Compiler.Build(SOURCE, DEPENDENCY);

        // Then
        build.Single("MEFF008");
        Assert.Empty(build.Generated);
    }

    [Fact]
    public void Error_OverlappingPayloadsInSeparateWrapperCases_PreservesDisjointPaths()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public class Base;
            public sealed class Derived : Base;
            public union First(Base);
            public union Second(Derived);
            [Error] public partial union Target(First, Second);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("global::App.Base>", build.Generated["App.Target.Error.g.cs"]);
        Assert.Contains("global::App.Derived>", build.Generated["App.Target.Error.g.cs"]);
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic?")]
    [InlineData("object")]
    [InlineData("object?")]
    public void Error_SingleDynamicCase_PreservesObjectPattern(string caseType)
    {
        // Given
        var source = $"namespace App; [Error] public partial union Target({caseType});";

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("if (self is object found)", build.Generated["App.Target.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catch_OverlappingCasesBeforeGeneratorsRun_ReportsDiagnostic(bool restOverlaps)
    {
        // Given
        var source = $$"""
            using Moderato.Effects;
            public class Base;
            public sealed class Derived : Base;
            public union Source(Base, Derived, string);
            [RestOf<Source>] public partial union Remaining({{(restOverlaps ? "Base, Derived" : "Derived, string")}});
            public static class Use
            {
                public static object Catch(Effect<int, Source, EmptyEnvironment> effect)
                    => effect.Catch<{{(restOverlaps ? "string" : "Base")}}, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("UserCode", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // When
        var diagnostics = await compilation.WithAnalyzers([new CatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        // Then
        Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "MEFF008");
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
    }

    [Fact]
    public void RestOf_CatchesWholeOverlappingBranch_PreservesOpaqueRemoval()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public class Base;
            public sealed class Derived : Base;
            public union Inner(Base, Derived);
            public union Source(Inner, string);
            [RestOf<Source>] public partial union Remaining(string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.Remaining.RestOf.g.cs", build.Generated.Keys);
    }

    [Theory]
    [InlineData("Derived", "Base")]
    [InlineData("int?", "int")]
    public void Error_ReversingOverlappingCases_StillReportsDiagnostic(string first, string second)
    {
        // Given
        var source = $"namespace App; public class Base; public sealed class Derived : Base; [Error] public partial union Target({first}, {second});";

        // When
        var build = Compiler.Build(source);

        // Then
        build.Single("MEFF008");
        Assert.Empty(build.Generated);
    }
}
