using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Moderato.Effects.SourceGenerator.Tests;

public class LibraryAliasTests
{
    [Theory]
    [InlineData("Error", false)]
    [InlineData("Environment", false)]
    [InlineData("RestOf<Source>", false)]
    [InlineData("Error", true)]
    [InlineData("Environment", true)]
    [InlineData("RestOf<Source>", true)]
    public void Generate_LibraryHasAnAlias_RequiresItsGlobalName(string attribute, bool global)
    {
        // Given
        var target = attribute == "Environment" ? "record Target(int Value)" : "union Target(int)";
        var source = $$"""
            extern alias Fx;
            using System;
            namespace App;
            public union Source(int, Guid);
            [Fx::Moderato.Effects.{{attribute}}] public partial {{target}};
            """;
        var compilation = Compilation(source, global);
        var native = Compilation(source.Replace($"[Fx::Moderato.Effects.{attribute}]", ""), global);
        Assert.DoesNotContain(native.GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // When
        var build = Compiler.Build(compilation);

        // Then
        if (global)
        {
            Assert.Empty(build.Errors);
            Assert.Single(build.Generated);
        }
        else
        {
            Assert.Equal(["MEFF009"], build.Errors);
            Assert.Empty(build.Generated);
        }
    }

    [Fact]
    public void Widen_LibraryHasOnlyAnAlias_ManualInterfacesCompile()
    {
        // Given
        const string SOURCE = """
            extern alias Fx;
            using Fx::Moderato.Effects;
            namespace App;
            public union Target(int) : IUnionCase<Target, int>
            {
                static Target IUnionCase<Target, int>.From(int value) => value;
                static bool IUnionCase<Target, int>.TryGet(Target self, out int value)
                {
                    if (self is int found) { value = found; return true; }
                    value = default;
                    return false;
                }
            }
            public static class Use
            {
                public static Effect<int, Target, EmptyEnvironment> Widen(Effect<int, int, EmptyEnvironment> effect)
                    => effect.Widen<Target>();
            }
            """;
        var compilation = Compilation(SOURCE, global: false);

        // When
        var build = Compiler.Build(compilation);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData("Error", "public sealed class ErrorAttribute : System.Attribute { }")]
    [InlineData("Environment", "public sealed class EnvironmentAttribute : System.Attribute { }")]
    [InlineData("RestOf<Source>", "public sealed class RestOfAttribute<T> : System.Attribute { }")]
    public void Generate_SourceShadowsTheMarker_StillReferencesTheCorrectProtocol(string attribute, string shadow)
    {
        // Given
        var target = attribute == "Environment" ? "record Target(int Value)" : "union Target(int)";
        var source = $$"""
            extern alias Fx;
            using System;
            namespace App;
            public union Source(int, Guid);
            [Fx::Moderato.Effects.{{attribute}}] public partial {{target}};
            """;
        var compilation = Compilation(source, global: true).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace Moderato.Effects { " + shadow + " }", new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken));

        // When
        var build = Compiler.Build(compilation);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Generated);
    }

    [Theory]
    [InlineData("Error", "IUnionCase<TSelf, TCase>")]
    [InlineData("Environment", "IHas<T>")]
    [InlineData("RestOf<Source>", "IUnionRest<TSelf, E, TCase>")]
    public void Generate_SourceShadowsTheProtocol_RejectsTheWrongGlobalBinding(string attribute, string protocol)
    {
        // Given
        var target = attribute == "Environment" ? "record Target(int Value)" : "union Target(int)";
        var source = $$"""
            extern alias Fx;
            using System;
            namespace App;
            public union Source(int, Guid);
            [Fx::Moderato.Effects.{{attribute}}] public partial {{target}};
            """;
        var compilation = Compilation(source, global: true).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace Moderato.Effects { public interface " + protocol + " { } }", new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken));

        // When
        var build = Compiler.Build(compilation);

        // Then
        Assert.Equal(["MEFF009"], build.Errors);
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData("Error", "ErrorAttribute", true)]
    [InlineData("Error", "ErrorAttribute", false)]
    [InlineData("Environment", "EnvironmentAttribute", true)]
    [InlineData("Environment", "EnvironmentAttribute", false)]
    [InlineData("RestOf<Source>", "RestOfAttribute<T>", true)]
    [InlineData("RestOf<Source>", "RestOfAttribute<T>", false)]
    public void Generate_SeveralMatchingMarkers_ValidatesTheLibraryProtocolRegardlessOfOrder(string attribute, string shadow, bool sourceFirst)
    {
        // Given
        var target = attribute == "Environment" ? "record Target(int Value)" : "union Target(int)";
        var sourceMarker = "global::Moderato.Effects." + attribute;
        var libraryMarker = "Fx::Moderato.Effects." + attribute;
        var markers = sourceFirst ? sourceMarker + ", " + libraryMarker : libraryMarker + ", " + sourceMarker;
        var source = $$"""
            extern alias Fx;
            using System;
            namespace App;
            public union Source(int, Guid);
            [{{markers}}] public partial {{target}};
            """;
        var compilation = Compilation(source, global: false).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace Moderato.Effects { public sealed class " + shadow + " : System.Attribute { } }",
            new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // When
        var build = Compiler.Build(compilation);

        // Then
        Assert.Equal(["MEFF009"], build.Errors);
        Assert.Empty(build.Generated);
    }

    private static CSharpCompilation Compilation(string source, bool global)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => Path.GetFileName(path) == "Moderato.Effects.dll"
                ? MetadataReference.CreateFromFile(path, MetadataReferenceProperties.Assembly.WithAliases(global ? ["global", "Fx"] : ["Fx"]))
                : MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("UserCode",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
