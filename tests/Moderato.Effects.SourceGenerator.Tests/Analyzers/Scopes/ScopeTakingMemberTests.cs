namespace Moderato.Effects.SourceGenerator.Tests;

public class ScopeTakingMemberTests
{
    private const string PRELUDE = """
        namespace App;
        using E0 = Moderato.Effects.EmptyEnvironment;

        public static class Shared
        {
            public static Scope<string, E0>? Kept;
            public static readonly Task<int> Pending = Task.FromResult(1);
            public static Result<int, string> One => Result.Ok(1);
        }

        """;

    [Theory]
    [InlineData("public Member(Scope<string, E0> scope) { Shared.Kept = scope; }", "MEFF304")]
    [InlineData("public Member(Scope<string, E0> scope) { try { scope.Get(Shared.One); } catch { } }", "MEFF301")]
    [InlineData("public static int operator +(Member left, Scope<string, E0> scope) { Shared.Kept = scope; return 0; }", "MEFF304")]
    [InlineData("public static int operator +(Member left, Scope<string, E0> scope) => Shared.Pending.Result + scope.Get(Shared.One);", "MEFF306")]
    [InlineData("public static explicit operator Member(Scope<string, E0> scope) { Shared.Kept = scope; return new Member(); }", "MEFF304")]
    [InlineData("public int this[Scope<string, E0> scope] { get { Shared.Kept = scope; return 0; } }", "MEFF304")]
    [InlineData("public int this[Scope<string, E0> scope] => Shared.Pending.Result + scope.Get(Shared.One);", "MEFF306")]
    [InlineData("public Scope<string, E0> Current { set { Shared.Kept = value; } }", "MEFF304")]
    [InlineData("public Scope<string, E0> Current { init => Shared.Kept = value; }", "MEFF304")]
    public void Member_TakesAScopeAndMisusesIt_ReportsLikeAMethod(string member, string expected)
    {
        // Given
        var constructor = member.Contains("public Member(") ? "" : "public Member() { }";
        var source = PRELUDE + $$"""
            public sealed class Member
            {
                {{constructor}}
                {{member}}
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal([expected], build.Ids);
    }

    [Theory]
    [InlineData("public sealed class Holder(Scope<string, E0> scope) { public Scope<string, E0> Kept { get; } = scope; }")]
    [InlineData("public sealed class Holder(Scope<string, E0> scope) { public int Read() => scope.Get(Shared.One); }")]
    [InlineData("public sealed record Holder(Scope<string, E0> Scope);")]
    [InlineData("public readonly record struct Holder(Scope<string, E0> Scope);")]
    public void PrimaryConstructor_KeepsItsScopeParameter_ReportsMEFF304(string type)
    {
        // Given
        var source = PRELUDE + type;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("'" + (type.Contains("record") ? "Scope" : "scope") + "'", build.Single("MEFF304").GetMessage());
    }

    [Theory]
    [InlineData("public sealed class Member { public Member(Scope<string, E0> scope) { _ = scope.Get(Shared.One); } }")]
    [InlineData("public class Base(int value) { public int Value => value; } public sealed class Member(Scope<string, E0> scope) : Base(scope.Get(Shared.One));")]
    [InlineData("public sealed class Member { public int this[Scope<string, E0> scope] => scope.Get(Shared.One); }")]
    public void Member_UsesItsScopeWithoutKeepingIt_ReportsNothing(string type)
    {
        // Given
        var source = PRELUDE + type;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}
