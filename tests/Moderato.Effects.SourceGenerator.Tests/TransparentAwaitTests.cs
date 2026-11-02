namespace Moderato.Effects.SourceGenerator.Tests;

public class TransparentAwaitTests
{
    public static TheoryData<string, string, bool> ConsumedCases
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var operation in new[] { "Run", "Acquire", "Fork" })
            {
                foreach (var wrapper in new[] { "plain", "parenthesized", "cast", "suppressed", "checked", "unchecked" })
                {
                    foreach (var returned in new[] { false, true }) cases.Add(operation, wrapper, returned);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ConsumedCases))]
    public void Gen_TransparentWrapperConsumesStep_DoesNotReportMEFF310(string operation, string wrapper, bool returned)
    {
        // Given
        var (call, result) = Operation(operation);
        var expression = Wrap(call, result, wrapper);
        var body = returned ? $"scope => {expression}" : $"async scope => await {expression}";
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
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("cast", false)]
    [InlineData("cast", true)]
    [InlineData("suppressed", false)]
    [InlineData("suppressed", true)]
    public void Gen_TransparentWrapperInBlockBody_DoesNotReportMEFF310(string wrapper, bool returned)
    {
        // Given
        var (call, result) = Operation("Run");
        var expression = Wrap(call, result, wrapper);
        var body = returned ? $"scope => {{ return {expression}; }}" : $"async scope => {{ return await {expression}; }}";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>({{body}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("((ValueTask<int>)scope.Run(Effect<EmptyEnvironment>.Succeed(42))).ConfigureAwait(false)")]
    [InlineData("(scope.Run(Effect<EmptyEnvironment>.Succeed(42))!).AsTask().ConfigureAwait(false)")]
    [InlineData("(ValueTask<int>)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).Preserve()")]
    [InlineData("(Task<int>)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()")]
    public void Gen_TransparentWrapperAndStandardAdapter_DoesNotReportMEFF310(string expression)
    {
        // Given
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope => await {{expression}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("(Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()")]
    [InlineData("(Task)(scope.Run(Effect<EmptyEnvironment>.Succeed(42))!).AsTask()")]
    [InlineData("((Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()).ConfigureAwait(false)")]
    [InlineData("((Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()).ConfigureAwait(ConfigureAwaitOptions.None)")]
    public void Gen_TaskUpcastConsumesStep_DoesNotReportMEFF310(string expression)
    {
        // Given
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
                    {
                        await {{expression}};
                        return 42;
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("Run", "cast")]
    [InlineData("Run", "suppressed")]
    [InlineData("Acquire", "cast")]
    [InlineData("Acquire", "suppressed")]
    [InlineData("Fork", "cast")]
    [InlineData("Fork", "suppressed")]
    [InlineData("Run", "checked")]
    [InlineData("Run", "unchecked")]
    [InlineData("Acquire", "checked")]
    [InlineData("Acquire", "unchecked")]
    [InlineData("Fork", "checked")]
    [InlineData("Fork", "unchecked")]
    public void Gen_TransparentWrapperDiscardsStep_ReportsMEFF310(string operation, string wrapper)
    {
        // Given
        var (call, result) = Operation(operation);
        var expression = Wrap(call, result, wrapper);
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(scope =>
                    {
                        _ = {{expression}};
                        return new(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gen_TaskUpcastNotImmediatelyConsumed_ReportsMEFF310(bool stored)
    {
        // Given
        var body = stored
            ? "async scope => { var pending = ((Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()).ConfigureAwait(false); await Task.Yield(); await pending; return 42; }"
            : "scope => { _ = (Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask(); return new(42); }";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>({{body}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [InlineData("(DiscardAwaitable)scope.Run(Effect<EmptyEnvironment>.Succeed(42))")]
    [InlineData("(DiscardAwaitable)((ValueTask<int>)scope.Run(Effect<EmptyEnvironment>.Succeed(42)))")]
    [InlineData("((DiscardAwaitable)scope.Run(Effect<EmptyEnvironment>.Succeed(42)))!")]
    public void Gen_UserConversionDiscardsStep_ReportsMEFF310(string expression)
    {
        // Given
        var source = $$"""
            namespace App;
            public readonly struct DiscardAwaitable
            {
                public static explicit operator DiscardAwaitable(ValueTask<int> step) => default;
                public System.Runtime.CompilerServices.TaskAwaiter<int> GetAwaiter() => Task.FromResult(42).GetAwaiter();
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope => await {{expression}});
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("IDisposable")]
    public void Gen_ReferenceCastWithUserAwaiterDiscardsStep_ReportsMEFF310(string target)
    {
        // Given
        var source = $$"""
            namespace App;
            public static class OtherAwaiter
            {
                public static System.Runtime.CompilerServices.TaskAwaiter GetAwaiter(this {{target}} value)
                    => Task.CompletedTask.GetAwaiter();
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
                    {
                        await ({{target}})scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask();
                        return 42;
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF310");
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Fact]
    public void Gen_TaskUpcastWithUserConfigureAwait_ReportsMEFF310()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class OtherAdapter
            {
                public static int ConfigureAwait(this Task task, string option) => 42;
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
                        await Task.FromResult(((Task)scope.Run(Effect<EmptyEnvironment>.Succeed(42)).AsTask()).ConfigureAwait("discard")));
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
    public void Gen_WrappedStepInCleanupOrLoop_KeepsOtherDiagnostics(bool loop)
    {
        // Given
        var (call, result) = Operation(loop ? "Acquire" : "Run");
        var expression = Wrap(call, result, "cast");
        var statement = loop ? $"for (var i = 0; i < 2; i++) await {expression};" : $"try {{ }} finally {{ await {expression}; }}";
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Work()
                    => Scope<Never, EmptyEnvironment>.Gen<int>(async scope =>
                    {
                        {{statement}}
                        return 42;
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains(loop ? "MEFF305" : "MEFF302", build.Ids);
        Assert.DoesNotContain("MEFF310", build.Ids);
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    private static (string Call, string Result) Operation(string operation) => operation switch
    {
        "Acquire" => ("scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(42), static (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))", "int"),
        "Fork" => ("scope.Fork(Effect<EmptyEnvironment>.Succeed(42))", "Fiber<int, Never, EmptyEnvironment>"),
        _ => ("scope.Run(Effect<EmptyEnvironment>.Succeed(42))", "int"),
    };

    private static string Wrap(string call, string result, string wrapper) => wrapper switch
    {
        "cast" => $"(ValueTask<{result}>)({call})",
        "suppressed" => $"{call}!",
        "parenthesized" => $"({call})",
        "checked" => $"checked({call})",
        "unchecked" => $"unchecked({call})",
        _ => call,
    };
}
