using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Moderato.Effects.SourceGenerator;

namespace Moderato.Effects.SourceGenerator.Tests;

/// <summary>What a build of the user's code produced.</summary>
public sealed record Build(ImmutableArray<Diagnostic> Diagnostics, ImmutableDictionary<string, string> Generated)
{
    public IEnumerable<string> Errors => Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.Id);

    public IEnumerable<string> Warnings => Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Warning).Select(static d => d.Id);

    public IEnumerable<string> Ids => Diagnostics.Select(static d => d.Id);

    public Diagnostic Single(string id) => Assert.Single(Diagnostics, d => d.Id == id);
}

/// <summary>Builds user code the way the SDK does: with the library, Moderato.Results, the source generators and the analyzers.</summary>
public static class Compiler
{
    private static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Preview);

    private static readonly ImmutableArray<MetadataReference> references =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)),
    ];

    private const string USINGS = """
        global using System;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Moderato.Effects;
        global using Moderato.Results;
        """;

    public static Build Build(string source)
    {
        var compilation = CSharpCompilation.Create(
            "UserCode",
            [CSharpSyntaxTree.ParseText(USINGS, parseOptions), CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ErrorGenerator().AsSourceGenerator(), new EnvironmentGenerator().AsSourceGenerator(), new RestOfGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results
            .SelectMany(static r => r.GeneratedSources)
            .ToImmutableDictionary(static s => s.HintName, static s => s.SourceText.ToString());
        var analyzerDiagnostics = output
            .WithAnalyzers([new GenBlockAnalyzer(), new ClockAnalyzer(), new CatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
        // Hidden diagnostics are IDE hints that a build never shows.
        ImmutableArray<Diagnostic> shown = [.. generatorDiagnostics.Concat(output.GetDiagnostics()).Concat(analyzerDiagnostics)
            .Where(static d => d.Severity != DiagnosticSeverity.Hidden)];
        return new Build(shown, generated);
    }
}
