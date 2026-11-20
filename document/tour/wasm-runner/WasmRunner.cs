using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Moderato.Effects.SourceGenerator;

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
    /// <returns>Elapsed milliseconds.</returns>
    [JSExport]
    public static async Task<double> ExecuteAsync()
    {
        if (image is null)
        {
            throw new InvalidOperationException("No successfully compiled code to run.");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var entryPoint = AsyncBody(Assembly.Load(image).EntryPoint
                ?? throw new InvalidOperationException("The compiled code has no entry point."));
            object?[]? arguments = entryPoint.GetParameters().Length == 0 ? null : [Array.Empty<string>()];

            if (entryPoint.Invoke(null, arguments) is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            var unwrapped = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Console.Error.WriteLine(unwrapped);
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }

        return watch.Elapsed.TotalMilliseconds;
    }

    // An async Main gets a synchronous wrapper that blocks on the task, which the single-threaded
    // browser runtime cannot do; awaiting the method the wrapper calls avoids it.
    private static MethodInfo AsyncBody(MethodInfo entryPoint)
    {
        if (typeof(Task).IsAssignableFrom(entryPoint.ReturnType))
        {
            return entryPoint;
        }

        var parameters = entryPoint.GetParameters().Select(static p => p.ParameterType).ToArray();
        return entryPoint.DeclaringType!
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m != entryPoint
                && m.Name is "Main" or "<Main>$"
                && typeof(Task).IsAssignableFrom(m.ReturnType)
                && m.GetParameters().Select(static p => p.ParameterType).SequenceEqual(parameters))
            ?? entryPoint;
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
