namespace Moderato.Effects.SourceGenerator.Tests;

public class NamedGenBodyTests
{
    private static string InClass(string members) => $$"""
        namespace App;
        public static class Steps<R> where R : IHas<TimeProvider>
        {
            static Effect<int, string, R> Load() => Effect<R>.Succeed(1);

            static Resource<int, string, R> Open() => Resource.Make(Load(), static (_, _) => Effect<R>.Succeed(Unit.Value));

            {{members}}
        }
        """;

    [Fact]
    public void GenBlock_MethodGroupBodySwallowsScopeFailure_ReportsMEFF301()
    {
        // Given
        var source = InClass("""
            public static Effect<int, string, R> Work() => Scope<string, R>.Gen(Body);

            static async ValueTask<int> Body(Scope<string, R> scope)
            {
                try { return await scope.Run(Load()); }
                catch { return 0; }
            }
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF301"], build.Ids);
    }

    [Fact]
    public void GenBlock_MethodBodyRunsAnUnawaitedStepInFinally_ReportsMEFF302AndMEFF310()
    {
        // Given
        var source = InClass("""
            static async ValueTask<int> Body(Scope<string, R> scope)
            {
                try { return await scope.Run(Load()); }
                finally { _ = scope.Run(Load()); }
            }
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF302", "MEFF310"], build.Ids.Order());
    }

    [Fact]
    public void GenBlock_LocalFunctionBodyAcquiresInALoopAndBlocks_ReportsEachOnce()
    {
        // Given
        var source = InClass("""
            public static Effect<int, string, R> Work()
            {
                return Scope<string, R>.Gen(Body);

                static async ValueTask<int> Body(Scope<string, R> scope)
                {
                    var total = 0;
                    for (var i = 0; i < 3; i++) total += await scope.Acquire(Open());
                    return total + Task.FromResult(1).Result;
                }
            }
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF305", "MEFF306"], build.Ids.Order());
    }

    [Fact]
    public void GenBlock_LocalFunctionWithItsOwnScopeInsideAGenBlock_ReportsItsBlockingWaitOnce()
    {
        // Given
        var source = InClass("""
            public static Effect<int, string, R> Work() => Scope<string, R>.Gen(async scope =>
            {
                return await scope.Run(Scope<string, R>.Gen(Inner));

                static async ValueTask<int> Inner(Scope<string, R> inner)
                    => await inner.Run(Load()) + Task.FromResult(1).Result;
            });
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF306"], build.Ids);
    }

    [Theory]
    [InlineData("static async ValueTask<int> Helper(Scope<string, R> scope) => await scope.Run(Load());")]
    [InlineData("static ValueTask<int> Helper(Scope<string, R> scope) => scope.Run(Load());")]
    [InlineData("static ValueTask<int> Helper(Scope<string, R> scope) { return scope.Run(Load()); }")]
    [InlineData("static Task<int> Helper(Scope<string, R> scope) => scope.Run(Load()).AsTask();")]
    public void GenBlock_HelperAwaitsOrReturnsItsStep_ReportsNothing(string helper)
    {
        // Given
        var source = InClass(helper + """

            public static Effect<int, string, R> Work() => Scope<string, R>.Gen(async scope => await Helper(scope));
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Fact]
    public void GenBlock_AsyncHelperReturnsItsStepAsAValue_ReportsMEFF310()
    {
        // Given
        var source = InClass("""
            static async Task<ValueTask<int>> Helper(Scope<string, R> scope)
            {
                await Task.Yield();
                return scope.Run(Load());
            }
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF310"], build.Ids);
    }

    [Fact]
    public void GenBlock_AbstractMethodTakingAScope_ReportsNothing()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public abstract class Step
            {
                public abstract ValueTask<int> Run(Scope<string, EmptyEnvironment> scope);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("static void Keep(Scope<string, R> scope, out Scope<string, R> kept) { kept = scope; }", true)]
    [InlineData("static void Keep(Scope<string, R> scope, ref Scope<string, R>? kept) => kept = scope;", true)]
    [InlineData("static void Keep(Scope<string, R> scope, Scope<string, R>? other) { other = scope; _ = other; }", false)]
    public void GenBlock_ScopeAssignedToTheFunctionsOwnParameter_ReportsMEFF304OnlyForOutAndRef(string member, bool reported)
    {
        // Given
        var source = InClass(member);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(reported ? ["MEFF304"] : [], build.Ids);
    }
}
