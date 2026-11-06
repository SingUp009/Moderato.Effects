namespace Moderato.Effects.SourceGenerator.Tests;

public class DeconstructLoopTests
{
    [Theory]
    [InlineData("foreach (var (x, y) in new[] { (1, 2) })", "scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));")]
    [InlineData("foreach ((int x, int y) in new[] { (1, 2) })", "scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));")]
    [InlineData("await foreach (var (x, y) in Values())", "scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));")]
    [InlineData("foreach (var (x, y) in new[] { (1, 2) })", "_ = await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)));")]
    [InlineData("foreach (var (x, y) in new[] { (1, 2) })", "_ = await scope.Fork(Effect<EmptyEnvironment>.Succeed(1));")]
    [InlineData("foreach (var pair in new[] { (1, 2) })", "scope.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));")]
    [InlineData("foreach (var pair in new[] { (1, 2) })", "_ = await scope.Acquire(Resource.Make(Effect<EmptyEnvironment>.Succeed(1), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value)));")]
    [InlineData("foreach (var pair in new[] { (1, 2) })", "_ = await scope.Fork(Effect<EmptyEnvironment>.Succeed(1));")]
    public void GenBlock_ResourceOperationInForeach_ReportsMEFF305(string loop, string operation)
    {
        // Given
        var source = InGen($"{loop} {{ {operation} }} return 0;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF305", Assert.Single(build.Ids));
    }

    [Fact]
    public void GenBlock_PerIterationNestedScopeInDeconstructLoop_ReportsNothing()
    {
        // Given
        var source = InGen("""
            foreach (var (x, y) in new[] { (1, 2) })
            {
                _ = await scope.Run(Scope<Never, EmptyEnvironment>.Gen(async nested =>
                {
                    nested.Defer(_ => Effect<EmptyEnvironment>.Succeed(Unit.Value));
                    await Task.Yield();
                    return x + y;
                }));
            }
            return 0;
            """);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    private static string InGen(string body) => $$"""
        public static class Demo
        {
            private static async System.Collections.Generic.IAsyncEnumerable<(int, int)> Values()
            {
                await Task.Yield();
                yield return (1, 2);
            }

            public static Effect<int, Never, EmptyEnvironment> Make() => Scope<Never, EmptyEnvironment>.Gen(async scope =>
            {
                await Task.Yield();
                {{body}}
            });
        }
        """;
}
