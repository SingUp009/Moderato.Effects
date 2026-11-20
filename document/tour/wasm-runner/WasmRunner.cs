using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Moderato.Effects.SourceGenerator;
using Moderato.Results;

namespace Moderato.Effects.WasmRunner;

/// <summary>Compiles and runs user code inside the browser, keeping compiler state between calls.</summary>
public static partial class WasmRunner
{
    private const string USER_FILE_PATH = "Program.cs";
    private const string GLOBAL_USINGS = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Latest);

    private static CSharpCompilation? compilation;
    private static GeneratorDriver? driver;
    private static SyntaxTree? userTree;
    private static byte[]? image;

    /// <summary>Loads the reference assemblies and routes the console to JavaScript.</summary>
    /// <returns>Elapsed milliseconds.</returns>
    [JSExport]
    public static double Initialize()
    {
        var watch = Stopwatch.StartNew();

        Console.SetOut(new JsConsoleWriter(isError: false));
        Console.SetError(new JsConsoleWriter(isError: true));

        compilation = CSharpCompilation.Create(
            assemblyName: "UserCode",
            syntaxTrees: [CSharpSyntaxTree.ParseText(GLOBAL_USINGS, parseOptions, path: "GlobalUsings.cs")],
            references: LoadReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Enable,
                // The browser runtime is single-threaded, so parallel compilation only adds scheduling overhead.
                concurrentBuild: false));

        driver = CSharpGeneratorDriver.Create(
            generators: [
                new EnvironmentGenerator().AsSourceGenerator(),
                new ErrorGenerator().AsSourceGenerator(),
                new RestOfGenerator().AsSourceGenerator(),
            ],
            parseOptions: parseOptions);

        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Compiles <paramref name="code"/> and keeps the assembly for <see cref="ExecuteAsync"/>.</summary>
    /// <returns>A JSON object with <c>success</c>, <c>diagnostics</c> and per-stage <c>timings</c> in milliseconds.</returns>
    [JSExport]
    public static string Compile(string code)
    {
        if (compilation is null || driver is null)
        {
            throw new InvalidOperationException("Call Initialize first.");
        }

        var watch = Stopwatch.StartNew();

        var tree = CSharpSyntaxTree.ParseText(code, parseOptions, path: USER_FILE_PATH);
        compilation = userTree is null
            ? compilation.AddSyntaxTrees(tree)
            : compilation.ReplaceSyntaxTree(userTree, tree);
        userTree = tree;
        var parsed = watch.Elapsed.TotalMilliseconds;

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
        var generatedAt = watch.Elapsed.TotalMilliseconds;

        using var stream = new MemoryStream();
        var result = generated.Emit(stream);
        image = result.Success ? stream.ToArray() : null;
        var emitted = watch.Elapsed.TotalMilliseconds;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("success", result.Success);
            json.WriteStartArray("diagnostics");
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity < DiagnosticSeverity.Warning)
                {
                    continue;
                }

                var span = diagnostic.Location.GetMappedLineSpan();
                json.WriteStartObject();
                json.WriteString("id", diagnostic.Id);
                json.WriteString("severity", diagnostic.Severity.ToString());
                json.WriteString("message", diagnostic.GetMessage());
                json.WriteString("path", span.Path);
                json.WriteNumber("line", span.StartLinePosition.Line + 1);
                json.WriteNumber("column", span.StartLinePosition.Character + 1);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartObject("timings");
            json.WriteNumber("parse", parsed);
            json.WriteNumber("generate", generatedAt - parsed);
            json.WriteNumber("emit", emitted - generatedAt);
            json.WriteNumber("total", emitted);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Runs the assembly produced by the last successful <see cref="Compile"/>.</summary>
    /// <returns>A JSON object with <c>success</c> and, on success, <c>elapsed</c> milliseconds or, on failure, the <c>error</c> kind.</returns>
    [JSExport]
    public static async Task<string> ExecuteAsync()
    {
        var result = await UserProgram.Run(image).RunAsync().ConfigureAwait(false);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("success", result is Ok<double>);
            switch (result)
            {
                case Ok<double>(var elapsed):
                    json.WriteNumber("elapsed", elapsed);
                    break;
                case Err<RunError>(ProgramFailed failed):
                    // The reader sees what their program threw where its own output goes.
                    Console.Error.WriteLine(failed.Exception);
                    Console.Error.Flush();
                    json.WriteString("error", "programFailed");
                    break;
                case Err<RunError>(NoEntryPoint):
                    json.WriteString("error", "noEntryPoint");
                    break;
                case Err<RunError>(NothingCompiled):
                    json.WriteString("error", "nothingCompiled");
                    break;
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reports memory after a full collection, for measuring growth over many runs.</summary>
    /// <returns>A JSON object with the loaded assembly count and the managed heap size.</returns>
    [JSExport]
    public static string Diagnostics()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("assemblies", AppDomain.CurrentDomain.GetAssemblies().Length);
            json.WriteNumber("managedBytes", GC.GetTotalMemory(forceFullCollection: true));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static List<MetadataReference> LoadReferences()
    {
        var assembly = typeof(WasmRunner).Assembly;
        var references = new List<MetadataReference>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("refs/", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            references.Add(MetadataReference.CreateFromStream(stream, filePath: name));
        }

        return references;
    }
}
