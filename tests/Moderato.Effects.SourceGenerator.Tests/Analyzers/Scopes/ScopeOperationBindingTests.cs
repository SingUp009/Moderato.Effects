namespace Moderato.Effects.SourceGenerator.Tests;

public class ScopeOperationBindingTests
{
    [Fact]
    public void Gen_UnrelatedExtensionInNestedFunction_KeepsScopeCaptureDiagnostic()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class Extras
            {
                public static int Run(this Scope<Never, EmptyEnvironment> scope, int value) => value;
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        Func<int> nested = () => scope.Run(42);
                        _ = nested;
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF303");
        Assert.DoesNotContain(build.Ids, static id => id is "MEFF302" or "MEFF305" or "MEFF310");
    }

    [Fact]
    public void Gen_UserTypeNamedScopeExtensions_DoesNotImpersonateLibraryOperations()
    {
        // Given
        const string SOURCE = """
            namespace Moderato.Effects
            {
                public static class ScopeExtensions
                {
                    public static int Run(this Scope<Never, EmptyEnvironment> scope, int value) => value;
                }
            }
            namespace App
            {
                public static class Use
                {
                    public static Effect<int, Never, EmptyEnvironment> Make()
                        => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>(scope.Run(42)));
                }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain(build.Ids, static id => id is "MEFF302" or "MEFF305" or "MEFF310");
    }

    [Theory]
    [InlineData("Run", "Direct")]
    [InlineData("Get", "Direct")]
    [InlineData("Acquire", "Direct")]
    [InlineData("Fork", "Direct")]
    [InlineData("Defer", "Direct")]
    [InlineData("Run", "Loop")]
    [InlineData("Get", "Loop")]
    [InlineData("Acquire", "Loop")]
    [InlineData("Fork", "Loop")]
    [InlineData("Defer", "Loop")]
    [InlineData("Run", "Cleanup")]
    [InlineData("Get", "Cleanup")]
    [InlineData("Acquire", "Cleanup")]
    [InlineData("Fork", "Cleanup")]
    [InlineData("Defer", "Cleanup")]
    public void Gen_UnrelatedScopeExtension_DoesNotReportStepDiagnostics(string method, string position)
    {
        // Given
        var call = $"_ = scope.{method}(42);";
        var statement = position switch
        {
            "Loop" => $"for (var index = 0; index < 1; index++) {{ {call} }}",
            "Cleanup" => $"try {{ }} finally {{ {call} }}",
            _ => call,
        };
        var source = $$"""
            namespace App;
            public static class Extras
            {
                public static int {{method}}(this Scope<Never, EmptyEnvironment> scope, int value) => value;
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        {{statement}}
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain(build.Ids, static id => id is "MEFF302" or "MEFF305" or "MEFF310");
    }

    [Theory]
    [InlineData("Run", false)]
    [InlineData("Get", false)]
    [InlineData("Acquire", false)]
    [InlineData("Fork", false)]
    [InlineData("Defer", false)]
    [InlineData("Run", true)]
    [InlineData("Get", true)]
    [InlineData("Acquire", true)]
    [InlineData("Fork", true)]
    [InlineData("Defer", true)]
    public void Gen_UninvokedScopeMethodGroup_DoesNotReportStepDiagnostics(string method, bool loop)
    {
        // Given
        var delegateType = method switch
        {
            "Run" => "Func<Effect<int, Never, EmptyEnvironment>, ValueTask<int>>",
            "Get" => "Func<Result<int, Never>, int>",
            "Acquire" => "Func<Resource<int, Never, EmptyEnvironment>, ValueTask<int>>",
            "Fork" => "Func<Effect<int, string, EmptyEnvironment>, ValueTask<Fiber<int, string, EmptyEnvironment>>>",
            _ => "Action<Func<ExitCase, Effect<Unit, Never, EmptyEnvironment>>>",
        };
        var reference = $"var callback = ({delegateType})scope.{method}; _ = callback;";
        var statement = loop
            ? $"for (var index = 0; index < 1; index++) {{ {reference} }}"
            : $"try {{ }} finally {{ {reference} }}";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        {{statement}}
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain(build.Ids, static id => id is "MEFF302" or "MEFF305" or "MEFF310");
    }

    [Theory]
    [InlineData("Run", "MEFF310")]
    [InlineData("Acquire", "MEFF310")]
    [InlineData("Get", "MEFF302")]
    public void Gen_LibraryCaseExtension_KeepsStepDiagnostics(string method, string expected)
    {
        // Given
        var statement = method switch
        {
            "Run" => "_ = scope.Run(source);",
            "Acquire" => "var resource = Resource.Make(source, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)); _ = scope.Acquire(resource);",
            _ => "try { } finally { _ = scope.Get(new Result<int, First>(new Err<First>(new First()))); }",
        };
        var source = $$"""
            namespace App;
            public record First;
            public record Second;
            [Error] public partial union Full(First, Second);
            public static class Use
            {
                public static Effect<int, Full, EmptyEnvironment> Make()
                {
                    Effect<int, First, EmptyEnvironment> source = Effect<EmptyEnvironment>.Fail(new First());
                    return Scope<Full, EmptyEnvironment>.Gen(scope =>
                    {
                        {{statement}}
                        return new ValueTask<int>(42);
                    });
                }
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, diagnostic => diagnostic.Id == expected);
    }
}
