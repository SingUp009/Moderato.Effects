namespace Moderato.Effects.SourceGenerator.Tests;

public class ConfigureAwaitBindingTests
{
    [Theory]
    [InlineData("return await scope.Run(Load()).ConfigureAwait(\"label\");")]
    [InlineData("return await scope.Run(Load()).AsTask().ConfigureAwait(\"label\");")]
    [InlineData("return await scope.Run(Load()).Preserve().ConfigureAwait(\"label\");")]
    [InlineData("return await scope.Acquire(Resource.Make(Load(), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))).ConfigureAwait(\"label\");")]
    [InlineData("var fiber = await scope.Fork(Load()).ConfigureAwait(\"label\"); return 0;")]
    [InlineData("return await scope.Acquire(Resource.Make(Load(), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))).AsTask().ConfigureAwait(\"label\");")]
    [InlineData("return await scope.Acquire(Resource.Make(Load(), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))).Preserve().ConfigureAwait(\"label\");")]
    [InlineData("var fiber = await scope.Fork(Load()).AsTask().ConfigureAwait(\"label\"); return 0;")]
    [InlineData("var fiber = await scope.Fork(Load()).Preserve().ConfigureAwait(\"label\"); return 0;")]
    public void GenBlock_UserConfigureAwaitDoesNotConsumeStep_ReportsMEFF310(string body)
    {
        // Given
        var source = InGen(body) + """

            public static class Adapters
            {
                public static ValueTask<T> ConfigureAwait<T>(this ValueTask<T> value, string ignored) => new(default(T)!);
                public static ValueTask<T> ConfigureAwait<T>(this Task<T> value, string ignored) => new(default(T)!);
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF310", Assert.Single(build.Ids));
    }

    [Theory]
    [InlineData("return await scope.Run(Load()).ConfigureAwait(false);")]
    [InlineData("return await scope.Run(Load()).AsTask().ConfigureAwait(ConfigureAwaitOptions.None);")]
    [InlineData("return await scope.Run(Load()).Preserve().ConfigureAwait(true);")]
    [InlineData("return await scope.Run(Load()).AsTask().ConfigureAwait(false);")]
    [InlineData("return await scope.Run(Load()).Preserve().AsTask().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);")]
    [InlineData("return await scope.Acquire(Resource.Make(Load(), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))).ConfigureAwait(false);")]
    [InlineData("return await scope.Acquire(Resource.Make(Load(), (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value))).AsTask().ConfigureAwait(ConfigureAwaitOptions.None);")]
    [InlineData("var fiber = await scope.Fork(Load()).ConfigureAwait(false); return 0;")]
    [InlineData("var fiber = await scope.Fork(Load()).AsTask().ConfigureAwait(ConfigureAwaitOptions.None); return 0;")]
    public void GenBlock_StandardConfigureAwaitConsumesStep_ReportsNothing(string body)
    {
        // Given
        var source = InGen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("return await scope.Run(Load()).ConfigureAwait(\"label\");")]
    [InlineData("return await scope.Run(Load()).AsTask().ConfigureAwait(\"label\");")]
    public void GenBlock_UserExtensionBlockConfigureAwait_ReportsMEFF310(string body)
    {
        // Given
        var source = InGen(body) + """

            public static class Adapters
            {
                extension<T>(ValueTask<T> value)
                {
                    public ValueTask<T> ConfigureAwait(string ignored) => new(default(T)!);
                }

                extension<T>(Task<T> value)
                {
                    public ValueTask<T> ConfigureAwait(string ignored) => new(default(T)!);
                }
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF310", Assert.Single(build.Ids));
    }

    [Fact]
    public void GenBlock_ReturnedUserConfigureAwait_ReportsMEFF310()
    {
        // Given
        var source = InGen("return scope.Run(Load()).ConfigureAwait(\"label\");", asyncBody: false) + """

            public static class Adapters
            {
                public static ValueTask<T> ConfigureAwait<T>(this ValueTask<T> value, string ignored) => new(default(T)!);
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF310", Assert.Single(build.Ids));
    }

    [Theory]
    [InlineData("return await scope.Run(Load()).ConfigureAwait();", "CS7036")]
    [InlineData("return await scope.Run(Load()).ConfigureAwait(\"label\");", "CS1503")]
    public void GenBlock_UnresolvedConfigureAwait_PreservesCompilerDiagnostic(string body, string error)
    {
        // Given
        var source = InGen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains(error, build.Errors);
        Assert.DoesNotContain("MEFF310", build.Ids);
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    private static string InGen(string body, bool asyncBody = true) => $$"""
        public static class Steps
        {
            private static Effect<int, Never, EmptyEnvironment> Load() => Effect<EmptyEnvironment>.Succeed(1);

            public static Effect<int, Never, EmptyEnvironment> Work() => Scope<Never, EmptyEnvironment>.Gen({{(asyncBody ? "async" : "")}} scope =>
            {
                {{body}}
            });
        }
        """;

}
