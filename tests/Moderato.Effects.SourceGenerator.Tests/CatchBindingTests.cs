namespace Moderato.Effects.SourceGenerator.Tests;

public class CatchBindingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Catch_CompetingExtensionWithSameArgumentShape_FollowsTheBoundMethod(bool declared, bool matchingCase)
    {
        // Given
        var caught = matchingCase ? "First" : "Second";
        var returnType = matchingCase ? "Effect<int, Rest, EmptyEnvironment>" : "int";
        var source = $$"""
            namespace App;
            public record First;
            public record Second;
            public union Full(First, Second);
            {{(declared ? "[RestOf<Full>]" : "")}}
            public partial union Rest(Second);
            public static class Extras
            {
                public static int Catch<TCase, F>(this Effect<int, Full, EmptyEnvironment> effect,
                    Func<TCase, Effect<int, F, EmptyEnvironment>> handler) => 99;
            }
            public static class Use
            {
                public static {{returnType}} Call(Effect<int, Full, EmptyEnvironment> effect)
                    => effect.Catch<{{caught}}, Rest>(static _ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData(false, false, "MEFF308")]
    [InlineData(true, false, "MEFF309")]
    [InlineData(true, true, null)]
    public void Catch_LibraryCallWithUnrelatedExtensionInScope_KeepsUnionDiagnostics(bool declared, bool matchingCase, string? expected)
    {
        // Given
        var caught = matchingCase ? "First" : "Second";
        var source = $$"""
            namespace App;
            public record First;
            public record Second;
            public union Full(First, Second);
            {{(declared ? "[RestOf<Full>]" : "")}}
            public partial union Rest(Second);
            public static class Extras
            {
                public static int Catch<TCase, F>(this Effect<int, Full, EmptyEnvironment> effect, int fallback)
                    => fallback;
            }
            public static class Use
            {
                public static Effect<int, Rest, EmptyEnvironment> Call(Effect<int, Full, EmptyEnvironment> effect)
                    => effect.Catch<{{caught}}, Rest>(static _ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("AD0001", build.Ids);
        if (expected is null) Assert.Empty(build.Errors);
        else Assert.Single(build.Diagnostics, diagnostic => diagnostic.Id == expected);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Catch_UnrelatedBoundExtension_DoesNotReportUnionDiagnostics(bool declared, bool matchingCase, bool handler)
    {
        // Given
        var caught = matchingCase ? "First" : "Second";
        var argument = handler ? "static _ => 42" : "42";
        var parameter = handler ? "Func<TCase, int> fallback" : "int fallback";
        var result = handler ? "fallback(default!)" : "fallback";
        var source = $$"""
            namespace App;
            public record First;
            public record Second;
            public union Full(First, Second);
            {{(declared ? "[RestOf<Full>]" : "")}}
            public partial union Rest(Second);
            public static class Extras
            {
                public static int Catch<TCase, F>(this Effect<int, Full, EmptyEnvironment> effect, {{parameter}})
                    => {{result}};
            }
            public static class Use
            {
                public static int Call(Effect<int, Full, EmptyEnvironment> effect)
                    => effect.Catch<{{caught}}, Rest>({{argument}});
            }
            """;
        var staticCall = source.Replace($"effect.Catch<{caught}, Rest>({argument})", $"Extras.Catch<{caught}, Rest>(effect, {argument})");
        Assert.Empty(Compiler.Build(staticCall).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Empty(build.Errors);
        Assert.DoesNotContain(build.Diagnostics, static diagnostic => diagnostic.Id is "MEFF308" or "MEFF309");
    }
}
