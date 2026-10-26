using Microsoft.CodeAnalysis;

namespace Moderato.Effects.SourceGenerator.Tests;

file static class Code
{
    public static string InGen(string body) => $$"""
        namespace App;
        public record NotFound(int Id);
        public record Forbidden(string Reason);
        [Error] public partial union AppError(NotFound, Forbidden);
        public static class Holder { public static object? Saved; }

        public static class Steps<R> where R : IHas<TimeProvider>
        {
            static Effect<int, AppError, R> Load() => Effect<R>.Succeed(1);

            static async Task<int> Helper(Scope<AppError, R> scope) => await scope.Run(Load());

            public static Effect<int, AppError, R> Work() => Scope<AppError, R>.Gen(async scope =>
            {
                {{body}}
            });
        }
        """;

    public static string InClass(string members) => $$"""
        namespace App;
        public static class Steps<R> where R : IHas<TimeProvider>
        {
            {{members}}
        }
        """;
}

public class CatchAllInGenTests
{
    [Fact]
    public void GenBlock_CatchAllAroundAScopeStep_ReportsMEFF301()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } catch (Exception) { return 0; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF301", build.Ids);
    }

    [Fact]
    public void GenBlock_CatchWithoutAType_ReportsMEFF301()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } catch { return 0; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF301", build.Ids);
    }

    [Fact]
    public void GenBlock_CatchAllThatFiltersOutScopeFailure_ReportsNothing()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } catch (Exception ex) when (ex is not ScopeFailure) { return 0; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("MEFF301", build.Ids);
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void GenBlock_CatchOfASpecificException_ReportsNothing()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } catch (FormatException) { return 0; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("MEFF301", build.Ids);
    }

    [Fact]
    public void GenBlock_CatchAllThatRethrows_ReportsNothing()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } catch (Exception) { Console.WriteLine(\"failed\"); throw; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("MEFF301", build.Ids);
    }

    [Fact]
    public void GenBlock_CatchAllAroundCodeWithoutTheScope_ReportsNothing()
    {
        // Given
        var source = Code.InGen("try { return int.Parse(\"1\"); } catch (Exception) { return 0; }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("MEFF301", build.Ids);
    }
}

public class StepInCleanupTests
{
    [Fact]
    public void GenBlock_ScopeRunInFinally_ReportsMEFF302()
    {
        // Given
        var source = Code.InGen("try { return await scope.Run(Load()); } finally { await scope.Run(Load()); }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("'scope.Run'", build.Single("MEFF302").GetMessage());
    }

    [Fact]
    public void GenBlock_ScopeRunInACatchOfASpecificException_ReportsNothing()
    {
        // Given
        var source = Code.InGen("try { return int.Parse(\"x\"); } catch (FormatException) { return await scope.Run(Load()); }");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("MEFF302", build.Ids);
    }

    [Fact]
    public void GenBlock_DeferForCleanup_ReportsNothing()
    {
        // Given
        var source = Code.InGen("scope.Defer(exit => Effect<R>.Succeed(Unit.Value)); return await scope.Run(Load());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class ScopeInNestedFunctionTests
{
    [Fact]
    public void GenBlock_ScopeUsedInALambda_ReportsMEFF303()
    {
        // Given
        var source = Code.InGen("Func<Task<int>> load = async () => await scope.Run(Load()); return await load();");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF303", build.Ids);
    }

    [Fact]
    public void GenBlock_OuterScopeUsedInANestedGenBlock_ReportsMEFF303()
    {
        // Given
        var source = Code.InGen("return await scope.Run(Scope<AppError, R>.Gen(async inner => await scope.Run(Load())));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("'scope'", build.Single("MEFF303").GetMessage());
    }

    [Fact]
    public void GenBlock_NestedGenBlockUsesItsOwnScope_ReportsNothing()
    {
        // Given
        var source = Code.InGen("return await scope.Run(Scope<AppError, R>.Gen(async inner => await inner.Run(Load())));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class ScopeEscapesTests
{
    [Fact]
    public void GenBlock_ScopeStoredInAField_ReportsMEFF304()
    {
        // Given
        var source = Code.InGen("Holder.Saved = scope; return await scope.Run(Load());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF304", build.Ids);
    }

    [Fact]
    public void GenBlock_ScopePassedToAHelper_ReportsNothing()
    {
        // Given
        var source = Code.InGen("return await Helper(scope);");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class ResourceInLoopTests
{
    [Fact]
    public void GenBlock_DeferInALoop_ReportsMEFF305()
    {
        // Given
        var source = Code.InGen("foreach (var i in new[] { 1, 2 }) { scope.Defer(_ => Effect<R>.Succeed(Unit.Value)); } return 0;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("'scope.Defer'", build.Single("MEFF305").GetMessage());
    }

    [Fact]
    public void GenBlock_RunInALoop_ReportsNothing()
    {
        // Given
        var source = Code.InGen("var total = 0; for (var i = 0; i < 3; i++) { total += await scope.Run(Load()); } return total;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class BlockingWaitTests
{
    [Fact]
    public void GenBlock_TaskResult_ReportsMEFF306()
    {
        // Given
        var source = Code.InGen("await Task.Yield(); return Task.FromResult(1).Result;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("'Result'", build.Single("MEFF306").GetMessage());
    }

    [Fact]
    public void GenBlock_TaskWait_ReportsMEFF306()
    {
        // Given
        var source = Code.InGen("Task.CompletedTask.Wait(); return await scope.Run(Load());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF306", build.Ids);
    }

    [Fact]
    public void GenBlock_GetAwaiterGetResult_ReportsMEFF306()
    {
        // Given
        var source = Code.InGen("await Task.Yield(); return Task.FromResult(1).GetAwaiter().GetResult();");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF306", build.Ids);
    }

    [Fact]
    public void GenBlock_Await_ReportsNothing()
    {
        // Given
        var source = Code.InGen("return await Task.FromResult(1);");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class SystemClockTests
{
    [Fact]
    public void GenBlock_DateTimeUtcNow_ReportsMEFF307AsInfo()
    {
        // Given
        var source = Code.InGen("await Task.Yield(); return DateTime.UtcNow.Second;");

        // When
        var build = Compiler.Build(source);

        // Then
        var diagnostic = build.Single("MEFF307");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("'DateTime.UtcNow'", diagnostic.GetMessage());
    }

    [Fact]
    public void GenBlock_TaskDelay_SuggestsEffectSleep()
    {
        // Given
        var source = Code.InGen("await Task.Delay(10); return 0;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("'Effect.Sleep'", build.Single("MEFF307").GetMessage());
    }

    [Fact]
    public void Succeed_ValueReadFromTheClock_ReportsMEFF307()
    {
        // Given
        var source = Code.InClass("public static Effect<DateTimeOffset, Never, R> Now() => Effect<R>.Succeed(DateTimeOffset.UtcNow);");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF307", build.Ids);
    }

    [Fact]
    public void Sync_LambdaReadingTheTickCount_ReportsMEFF307()
    {
        // Given
        var source = Code.InClass("public static Effect<long, Never, R> Ticks() => Effect<R>.Sync(() => Environment.TickCount64);");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF307", build.Ids);
    }

    [Fact]
    public void Access_ClockFromTheEnvironment_ReportsNothing()
    {
        // Given
        var source = Code.InClass("public static Effect<DateTimeOffset, Never, R> Now() => Effect<R>.Access(env => env.Resolve<TimeProvider>().GetUtcNow());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Fact]
    public void ClockOutsideAnyEffect_ReportsNothing()
    {
        // Given
        var source = Code.InClass("public static DateTime Now() => DateTime.UtcNow;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}

public class CatchConstraintTests
{
    private const string ERRORS = """
        namespace App;
        public record User(string Name);
        public record NotFound(int Id);
        public record Forbidden(string Reason);
        [Error] public partial union AppError(NotFound, Forbidden, TimeoutError);
        """;

    [Fact]
    public void Catch_RestNotDeclaredForTheUnion_ReportsMEFF308()
    {
        // Given
        const string SOURCE = ERRORS + """

            [Error] public partial union AccessError(Forbidden, TimeoutError);

            public static class Use<R>
            {
                public static Effect<User, AccessError, R> Find(Effect<User, AppError, R> find)
                    => find.Catch<NotFound, AccessError>(nf => Effect<R>.Succeed(new User("guest")));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Equal("'AccessError' is not declared [RestOf<AppError>]", build.Single("MEFF308").GetMessage());
    }

    [Fact]
    public void Catch_CaseTheRestStillHolds_ReportsMEFF309()
    {
        // Given
        const string SOURCE = ERRORS + """

            [Error, RestOf<AppError>] public partial union AccessError(Forbidden, TimeoutError);

            public static class Use<R>
            {
                public static Effect<User, AccessError, R> Find(Effect<User, AppError, R> find)
                    => find.Catch<Forbidden, AccessError>(f => Effect<R>.Succeed(new User("guest")));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Equal("'AccessError' leaves out 'NotFound', not 'Forbidden'", build.Single("MEFF309").GetMessage());
    }
}
