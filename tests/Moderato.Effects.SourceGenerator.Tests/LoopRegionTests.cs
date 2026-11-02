namespace Moderato.Effects.SourceGenerator.Tests;

public class LoopRegionTests
{
    [Theory]
    [InlineData("for (scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); Check();) { }")]
    [InlineData("for (var i = await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(0), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))); i < 0; i++) { }")]
    [InlineData("for (var fiber = await scope.Fork(Effect<EmptyEnvironment>.Succeed(1)); Check();) { }")]
    [InlineData("foreach (var item in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(new[] { 1, 2 }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { }")]
    [InlineData("await foreach (var item in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(Values()), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { }")]
    [InlineData("foreach (var (x, y) in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(new[] { (1, 2) }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { }")]
    public void GenBlock_ResourceOperationInOnceOnlyLoopSetup_ReportsNothing(string body)
    {
        // Given
        var source = InGen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("for (; await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(false), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)));) { }")]
    [InlineData("for (var i = 0; i < 1; scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)), i++) { }")]
    [InlineData("foreach (var item in new[] { 1, 2 }) { scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); }")]
    [InlineData("foreach (var outer in new[] { 1, 2 }) { foreach (var item in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(new[] { 1, 2 }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { } }")]
    [InlineData("for (var i = 0; i < 1; i++) { scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); }")]
    [InlineData("while (await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(false), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { }")]
    [InlineData("while (Check()) { scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); }")]
    [InlineData("do { } while (await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(false), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))));")]
    [InlineData("do { scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); } while (Check());")]
    [InlineData("foreach (var outer in new[] { 1, 2 }) { for (scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); Check();) { } }")]
    [InlineData("for (var outer = 0; outer < 2; outer++) { foreach (var item in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(new[] { 1, 2 }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { } }")]
    [InlineData("while (Check()) { for (scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); Check();) { } }")]
    [InlineData("do { foreach (var item in await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(new[] { 1, 2 }), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)))) { } } while (Check());")]
    [InlineData("for (var outer = 0; outer < 2; outer++) { for (scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value)); Check();) { } }")]
    public void GenBlock_ResourceOperationCanRepeat_ReportsMEFF305(string body)
    {
        // Given
        var source = InGen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF305", Assert.Single(build.Ids));
    }

    private static string InGen(string body) => $$"""
        public static class Demo
        {
            private static bool Check() => false;

            private static async System.Collections.Generic.IAsyncEnumerable<int> Values()
            {
                await Task.Yield();
                yield return 1;
            }

            public static Effect<int, Never, EmptyEnvironment> Make() => Scope<Never, EmptyEnvironment>.Gen(async scope =>
            {
                await Task.Yield();
                {{body}}
                return 0;
            });
        }
        """;
}
