namespace Moderato.Effects.SourceGenerator.Tests;

public class AsyncStepReturnTests
{
    public static TheoryData<string, string, bool> AsyncReturns
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var operation in new[] { "Run", "Acquire", "Fork" })
            {
                foreach (var adapter in new[] { "none", "AsTask", "Preserve", "ConfigureAwait", "AsTaskConfigured" })
                {
                    foreach (var block in new[] { false, true }) cases.Add(operation, adapter, block);
                }
            }
            return cases;
        }
    }

    public static TheoryData<string, string, bool> DirectReturns
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var operation in new[] { "Run", "Acquire", "Fork" })
            {
                foreach (var adapter in new[] { "none", "Preserve" })
                {
                    foreach (var block in new[] { false, true }) cases.Add(operation, adapter, block);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(AsyncReturns))]
    public void Gen_AsyncBodyReturnsStepAsValue_ReportsMEFF310(string operation, string adapter, bool block)
    {
        // Given
        var source = Source(operation, adapter, asyncBody: true, block);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [MemberData(nameof(DirectReturns))]
    public void Gen_NonAsyncBodyReturnsStepForGenToAwait_ReportsNoDiagnostics(string operation, string adapter, bool block)
    {
        // Given
        var source = Source(operation, adapter, asyncBody: false, block);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("Run")]
    [InlineData("Acquire")]
    [InlineData("Fork")]
    public void Gen_AsyncBodyAwaitsStep_ReportsNoDiagnostics(string operation)
    {
        // Given
        var source = Source(operation, "none", asyncBody: true, block: true, awaitStep: true);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("conditional", false)]
    [InlineData("conditional", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public void Gen_ReturnedBranches_OnlyReportsAsyncValueReturns(string kind, bool asyncBody)
    {
        // Given
        const string CALL = "scope.Run(Effect<EmptyEnvironment>.Succeed(42))";
        var expression = kind == "conditional" ? $"choose ? {CALL} : {CALL}" : $"choose switch {{ true => {CALL}, _ => {CALL} }}";
        var result = asyncBody ? "ValueTask<int>" : "int";
        var body = $"{(asyncBody ? "async " : "")}scope => {{ {(asyncBody ? "await Task.Yield(); " : "")}return {expression}; }}";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<{{result}}, Never, EmptyEnvironment> Work(bool choose)
                    => Scope<Never, EmptyEnvironment>.Gen<{{result}}>({{body}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(asyncBody ? 2 : 0, build.Diagnostics.Length);
        Assert.All(build.Diagnostics, static diagnostic => Assert.Equal("MEFF310", diagnostic.Id));
    }

    [Fact]
    public void Gen_AsyncAnonymousMethodReturnsStepAsValue_ReportsMEFF310()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class Use
            {
                public static Effect<ValueTask<int>, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<ValueTask<int>>(async delegate (Scope<Never, EmptyEnvironment> scope)
                    {
                        await Task.Yield();
                        return scope.Run(Effect<EmptyEnvironment>.Succeed(42));
                    });
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gen_AwaitedStepContainsAwaitableValue_ReportsNoDiagnostics(bool task)
    {
        // Given
        var result = task ? "Task<int>" : "ValueTask<int>";
        var value = task ? "Task.FromResult(42)" : "new ValueTask<int>(42)";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<{{result}}, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<{{result}}>(async scope =>
                        await scope.Run(Effect<EmptyEnvironment>.Succeed({{value}})));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Fact]
    public void Gen_AsyncBodyBoxesStepAsValue_ReportsMEFF310()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class Use
            {
                public static Effect<object, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<object>(async scope =>
                    {
                        await Task.Yield();
                        return scope.Run(Effect<EmptyEnvironment>.Succeed(42));
                    });
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Fact]
    public void Gen_AsyncBodyDiscardsStepBeforeReturningAwaitableValue_ReportsMEFF310()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class Use
            {
                public static Effect<ValueTask<int>, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<ValueTask<int>>(async scope =>
                    {
                        await Task.Yield();
                        _ = scope.Run(Effect<EmptyEnvironment>.Succeed(42));
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gen_StaticBodyReturnsStep_OnlyReportsAsyncValueReturn(bool asyncBody)
    {
        // Given
        var result = asyncBody ? "ValueTask<int>" : "int";
        var body = asyncBody
            ? "static async scope => { await Task.Yield(); return scope.Run(Effect<EmptyEnvironment>.Succeed(42)); }"
            : "static scope => scope.Run(Effect<EmptyEnvironment>.Succeed(42))";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<{{result}}, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<{{result}}>({{body}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(asyncBody ? 1 : 0, build.Diagnostics.Length);
        Assert.All(build.Diagnostics, static diagnostic => Assert.Equal("MEFF310", diagnostic.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gen_TypedBodyInEffectFactory_OnlyReportsAsyncValueReturn(bool asyncBody)
    {
        // Given
        var result = asyncBody ? "ValueTask<int>" : "int";
        var body = asyncBody
            ? "async (Scope<Never, EmptyEnvironment> scope) => { await Task.Yield(); return scope.Run(Effect<EmptyEnvironment>.Succeed(42)); }"
            : "(Scope<Never, EmptyEnvironment> scope) => scope.Run(Effect<EmptyEnvironment>.Succeed(42))";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<{{result}}, Never, EmptyEnvironment> Work()
                    => Effect.Gen<{{result}}, Never, EmptyEnvironment>({{body}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(asyncBody ? 1 : 0, build.Diagnostics.Length);
        Assert.All(build.Diagnostics, static diagnostic => Assert.Equal("MEFF310", diagnostic.Id));
    }

    private static string Source(string operation, string adapter, bool asyncBody, bool block, bool awaitStep = false)
    {
        var (call, valueType) = operation switch
        {
            "Acquire" => ("scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(42), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))", "int"),
            "Fork" => ("scope.Fork(Effect<EmptyEnvironment>.Succeed(42))", "Fiber<int, Never, EmptyEnvironment>"),
            _ => ("scope.Run(Effect<EmptyEnvironment>.Succeed(42))", "int"),
        };
        var (expression, adaptedType) = adapter switch
        {
            "AsTask" => ($"{call}.AsTask()", $"Task<{valueType}>"),
            "Preserve" => ($"{call}.Preserve()", $"ValueTask<{valueType}>"),
            "ConfigureAwait" => ($"{call}.ConfigureAwait(false)", $"System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<{valueType}>"),
            "AsTaskConfigured" => ($"{call}.AsTask().ConfigureAwait(false)", $"System.Runtime.CompilerServices.ConfiguredTaskAwaitable<{valueType}>"),
            _ => (call, $"ValueTask<{valueType}>"),
        };
        var result = asyncBody && !awaitStep ? adaptedType : valueType;
        var returned = awaitStep ? $"await {expression}" : expression;
        var prefix = asyncBody ? "async " : "";
        var body = block
            ? $"{prefix}scope => {{ {(asyncBody && !awaitStep ? "await Task.Yield(); " : "")}return {returned}; }}"
            : $"{prefix}scope => {returned}";
        return $$"""
            #pragma warning disable CS1998
            namespace App;
            public static class Use
            {
                public static Effect<{{result}}, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<{{result}}>({{body}});
            }
            """;
    }
}
