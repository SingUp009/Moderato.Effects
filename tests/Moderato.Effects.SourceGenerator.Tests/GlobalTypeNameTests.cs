using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Moderato.Effects.SourceGenerator.Tests;

public class GlobalTypeNameTests
{
    [Theory]
    [InlineData("Error", "Alpha::Payload.Leaf")]
    [InlineData("Error", "Alpha::Payload.Leaf[]")]
    [InlineData("Error", "System.Collections.Generic.List<Alpha::Payload.Leaf>")]
    [InlineData("Error", "(Alpha::Payload.Leaf, int)")]
    [InlineData("RestOf<Source>", "Alpha::Payload.Leaf")]
    [InlineData("RestOf<Source>", "Alpha::Payload.Leaf[]")]
    [InlineData("RestOf<Source>", "System.Collections.Generic.List<Alpha::Payload.Leaf>")]
    [InlineData("RestOf<Source>", "(Alpha::Payload.Leaf, int)")]
    [InlineData("Environment", "Alpha::Payload.Leaf")]
    [InlineData("Environment", "Alpha::Payload.Leaf[]")]
    [InlineData("Environment", "System.Collections.Generic.List<Alpha::Payload.Leaf>")]
    [InlineData("Environment", "(Alpha::Payload.Leaf, int)")]
    public void Generate_AliasOnlyType_ReportsDiagnosticWithoutTargetOutput(string attribute, string caseType)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha")];
        var target = attribute == "Environment" ? $"record Target({caseType} Value)" : $"union Target({caseType})";
        var source = $$"""
            extern alias Alpha;
            namespace App;
            public union Source({{caseType}}, Guid);
            [{{attribute}}] public partial {{target}};
            [Error] public partial union Good(int);
            """;
        Assert.Empty(Compiler.Build(source.Replace($"[{attribute}]", ""), additionalReferences: references).Errors);

        // When
        var build = Compiler.Build(source, additionalReferences: references);

        // Then
        Assert.Equal(["MEFF009"], build.Errors);
        Assert.DoesNotContain(build.Generated.Keys, key => key.Contains("Target"));
        Assert.Contains("App.Good.Error.g.cs", build.Generated.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_SameNameFromDistinctAssemblies_RejectsTheReferenceWithoutFalseAmbiguity(bool global)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha", global), Reference("Beta", global)];
        const string SOURCE = """
            extern alias Alpha;
            extern alias Beta;
            namespace App;
            [Error] public partial union Target(Alpha::Payload.Leaf, Beta::Payload.Leaf);
            """;
        Assert.Empty(Compiler.Build(SOURCE.Replace("[Error]", ""), additionalReferences: references).Errors);

        // When
        var build = Compiler.Build(SOURCE, additionalReferences: references);

        // Then
        Assert.Equal(["MEFF009"], build.Errors);
        Assert.DoesNotContain("MEFF003", build.Ids);
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_NestedAliasedTypes_DistinguishesRealAmbiguityFromMatchingNames(bool sameAssembly)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha"), Reference("Beta")];
        var source = $$"""
            extern alias Alpha;
            extern alias Beta;
            namespace App;
            public union Left(Alpha::Payload.Leaf);
            public union Right({{(sameAssembly ? "Alpha" : "Beta")}}::Payload.Leaf);
            [Error] public partial union Target(Left, Right);
            """;

        // When
        var build = Compiler.Build(source, additionalReferences: references);

        // Then
        if (sameAssembly)
        {
            Assert.Empty(build.Errors);
            build.Single("MEFF003");
            Assert.Contains("App.Target.Error.g.cs", build.Generated.Keys);
        }
        else
        {
            Assert.Equal(["MEFF009"], build.Errors);
            Assert.DoesNotContain("MEFF003", build.Ids);
            Assert.Empty(build.Generated);
        }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("RestOf<Source>")]
    [InlineData("Environment")]
    public void Generate_AliasedTypeAlsoAvailableGlobally_Compiles(string attribute)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha", global: true)];
        var target = attribute == "Environment" ? "record Target(Alpha::Payload.Leaf Value)" : "union Target(Alpha::Payload.Leaf)";
        var source = $$"""
            extern alias Alpha;
            namespace App;
            public union Source(Alpha::Payload.Leaf, Guid);
            [{{attribute}}] public partial {{target}};
            """;

        // When
        var build = Compiler.Build(source, additionalReferences: references);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Generated);
    }

    [Theory]
    [InlineData("Inner")]
    [InlineData("int")]
    public void RestOf_KeepsOrCatchesWholeUnion_DoesNotRequireNamesOfItsInternalCases(string retained)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha")];
        var source = $$"""
            extern alias Alpha;
            namespace App;
            public union Inner(Alpha::Payload.Leaf);
            public union Source(Inner, int);
            [RestOf<Source>] public partial union Remaining({{retained}});
            """;

        // When
        var build = Compiler.Build(source, additionalReferences: references);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.Remaining.RestOf.g.cs", build.Generated.Keys);
    }

    [Fact]
    public void Environment_ExposesGlobalView_DoesNotRequireTheParameterTypeName()
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha")];
        const string SOURCE = """
            extern alias Alpha;
            namespace App;
            [Environment] public partial record Target([Expose<object>] Alpha::Payload.Leaf Value);
            """;

        // When
        var build = Compiler.Build(SOURCE, additionalReferences: references);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IHas<object>", build.Generated["App.Target.Environment.g.cs"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestOf_AliasedSourceOrCaughtCase_ReportsUnavailableName(bool sourceIsAliased)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha", declarations: "public union Source(int, System.Guid); public sealed record Leaf(int Code);")];
        var source = sourceIsAliased ? """
            extern alias Alpha;
            namespace App;
            [RestOf<Alpha::Payload.Source>] public partial union Remaining(int);
            """ : """
            extern alias Alpha;
            namespace App;
            public union Source(Alpha::Payload.Leaf, int);
            [RestOf<Source>] public partial union Remaining(int);
            """;

        // When
        var build = Compiler.Build(source, additionalReferences: references);

        // Then
        Assert.Equal(["MEFF009"], build.Errors);
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Environment_NestedConstructedMetadataType_ValidatesItsGlobalDefinition(bool global)
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha", global,
            "public sealed class Outer<T> { public sealed class Inner<U> { } }")];
        const string SOURCE = """
            extern alias Alpha;
            namespace App;
            [Environment] public partial record Target(Alpha::Payload.Outer<int>.Inner<string> Value);
            """;

        // When
        var build = Compiler.Build(SOURCE, additionalReferences: references);

        // Then
        if (global) Assert.Empty(build.Errors);
        else Assert.Equal(["MEFF009"], build.Errors);
    }

    [Fact]
    public void Widen_ManualInterfaceUsesAliasedType_Compiles()
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha")];
        const string SOURCE = """
            extern alias Alpha;
            namespace App;
            public union Target(Alpha::Payload.Leaf) : IUnionCase<Target, Alpha::Payload.Leaf>
            {
                static Target IUnionCase<Target, Alpha::Payload.Leaf>.From(Alpha::Payload.Leaf value) => value;
                static bool IUnionCase<Target, Alpha::Payload.Leaf>.TryGet(Target self, out Alpha::Payload.Leaf value)
                {
                    if (self is Alpha::Payload.Leaf found) { value = found; return true; }
                    value = default!;
                    return false;
                }
            }
            public static class Use
            {
                public static Effect<int, Target, EmptyEnvironment> Widen(Effect<int, Alpha::Payload.Leaf, EmptyEnvironment> effect)
                    => effect.Widen<Target>();
            }
            """;

        // When
        var build = Compiler.Build(SOURCE, additionalReferences: references);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Generated);
    }

    [Fact]
    public void Generate_MetadataNamespaceIsAKeyword_UsesItsUnescapedMetadataName()
    {
        // Given
        ImmutableArray<MetadataReference> references = [Reference("Alpha", global: true, @namespace: "@event")];
        const string SOURCE = """
            extern alias Alpha;
            namespace App;
            [Error] public partial union Target(Alpha::@event.Leaf);
            """;

        // When
        var build = Compiler.Build(SOURCE, additionalReferences: references);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.Target.Error.g.cs", build.Generated.Keys);
    }

    private static MetadataReference Reference(string alias, bool global = false, string declarations = "public sealed record Leaf(int Code);", string @namespace = "Payload")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var dependency = CSharpCompilation.Create(alias,
            [CSharpSyntaxTree.ParseText("namespace " + @namespace + "; " + declarations, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = dependency.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray(), MetadataReferenceProperties.Assembly.WithAliases(global ? ["global", alias] : [alias]));
    }
}
