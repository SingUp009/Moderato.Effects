namespace Moderato.Effects.SourceGenerator.Tests;

public class UnevaluatedAnalyzerTests
{
    [Theory]
    [InlineData("DateTime.Now")]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Today")]
    [InlineData("DateTimeOffset.Now")]
    [InlineData("DateTimeOffset.UtcNow")]
    [InlineData("TimeProvider.System")]
    [InlineData("Environment.TickCount")]
    [InlineData("Environment.TickCount64")]
    public void GenBlock_NameOfClockProperty_ReportsNothing(string property)
    {
        // Given
        var source = Gen($"return nameof({property});");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("DateTime.Now")]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Today")]
    [InlineData("DateTimeOffset.Now")]
    [InlineData("DateTimeOffset.UtcNow")]
    [InlineData("TimeProvider.System")]
    [InlineData("Environment.TickCount")]
    [InlineData("Environment.TickCount64")]
    public void Succeed_NameOfClockProperty_ReportsNothing(string property)
    {
        // Given
        var source = $$"""
            public static class Demo
            {
                public static Effect<string, Never, EmptyEnvironment> Make() =>
                    Effect<EmptyEnvironment>.Succeed(nameof({{property}}));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("DateTime.Now")]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Today")]
    [InlineData("DateTimeOffset.Now")]
    [InlineData("DateTimeOffset.UtcNow")]
    [InlineData("TimeProvider.System")]
    [InlineData("Environment.TickCount")]
    [InlineData("Environment.TickCount64")]
    public void Sync_NameOfClockProperty_ReportsNothing(string property)
    {
        // Given
        var source = $$"""
            public static class Demo
            {
                public static Effect<string, Never, EmptyEnvironment> Make() =>
                    Effect<EmptyEnvironment>.Sync(() => nameof({{property}}));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("DateTime.Now")]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Today")]
    [InlineData("DateTimeOffset.Now")]
    [InlineData("DateTimeOffset.UtcNow")]
    [InlineData("TimeProvider.System")]
    [InlineData("Environment.TickCount")]
    [InlineData("Environment.TickCount64")]
    public void GenBlock_ClockPropertyRead_ReportsMEFF307(string property)
    {
        // Given
        var source = Gen($"return {property}.ToString()!;");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal("MEFF307", Assert.Single(build.Ids));
    }

    [Theory]
    [InlineData("Func<string> name = () => nameof(scope.Run); return name();")]
    [InlineData("string Name() => nameof(scope.Run); return Name();")]
    [InlineData("Func<string> name = delegate { return nameof(scope.CancellationToken); }; return name();")]
    [InlineData("var inner = Scope<Never, EmptyEnvironment>.Gen(async nested => { await Task.Yield(); return nameof(scope); }); return await scope.Run(inner);")]
    public void GenBlock_NameOfScopeInNestedFunction_ReportsNothing(string body)
    {
        // Given
        var source = Gen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("scope.Run")]
    [InlineData("scope.CancellationToken")]
    public void GenBlock_CatchAroundOnlyNameOfScope_ReportsNothing(string operand)
    {
        // Given
        var source = Gen($"try {{ return nameof({operand}); }} catch (Exception) {{ return \"caught\"; }}");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }

    [Theory]
    [InlineData("return @nameof(DateTime.UtcNow);", "MEFF307")]
    [InlineData("return nameof(DateTime.UtcNow);", "MEFF307")]
    [InlineData("Func<string> name = () => @nameof(scope); return name();", "MEFF303")]
    [InlineData("try { return @nameof(scope); } catch (Exception) { return \"caught\"; }", "MEFF301")]
    public void GenBlock_UserFunctionNamedNameof_ReportsEvaluatedUse(string body, string diagnostic)
    {
        // Given
        var source = Gen(body, userFunction: true);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(diagnostic, Assert.Single(build.Ids));
    }

    [Theory]
    [InlineData("return nameof(DateTime.UtcNow) + DateTime.UtcNow;", "MEFF307")]
    [InlineData("Func<string> name = () => nameof(scope) + scope.CancellationToken.ToString(); return name();", "MEFF303")]
    [InlineData("try { _ = nameof(scope.Run); return await scope.Run(Effect<EmptyEnvironment>.Succeed(\"actual\")); } catch (Exception) { return \"caught\"; }", "MEFF301")]
    public void GenBlock_NameOfAlongsideActualUse_ReportsActualUse(string body, string diagnostic)
    {
        // Given
        var source = Gen(body);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(diagnostic, Assert.Single(build.Ids));
    }

    private static string Gen(string body, bool userFunction = false) => $$"""
        public static class Demo
        {
            {{(userFunction ? "private static string @nameof(object value) => value.ToString()!;" : "")}}

            public static Effect<string, Never, EmptyEnvironment> Make() =>
                Scope<Never, EmptyEnvironment>.Gen(async scope =>
                {
                    await Task.Yield();
                    {{body}}
                });
        }
        """;
}
