namespace Moderato.Effects.SourceGenerator.Tests;

public class CaseIdentityTests
{
    [Theory]
    [InlineData("(int X, int Y)", "(int A, int B)")]
    [InlineData("(int X, int Y)", "(int, int)")]
    [InlineData("(int X, int Y)[]", "(int A, int B)[]")]
    [InlineData("System.Collections.Generic.List<(int X, int Y)>", "System.Collections.Generic.List<(int A, int B)>")]
    [InlineData("(int X, int Y)?", "(int A, int B)?")]
    [InlineData("(int X, (string Name, int Code) Detail)", "(int A, (string Text, int Value) Data)")]
    public void RestOf_RenamedTupleCase_RetainsTheSameType(string original, string retained)
    {
        // Given
        var source = $$"""
            namespace App;
            public union Full({{original}}, string);
            [RestOf<Full>] public partial union Rest({{retained}});
            """;
        Assert.Empty(Compiler.Build(source.Replace("[RestOf<Full>]", "")).Errors);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionRest<Rest, global::App.Full, string>", build.Generated["App.Rest.RestOf.g.cs"]);
        if (retained.EndsWith('?')) Assert.Contains("case null:", build.Generated["App.Rest.RestOf.g.cs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_RenamedTupleReachedTwice_ReportsAmbiguityAndKeepsOnlyADirectCase(bool direct)
    {
        // Given
        var cases = direct ? "(int X, int Y), Right" : "Left, Right";
        var source = $$"""
            namespace App;
            public union Left((int X, int Y), string);
            public union Right((int A, int B), bool);
            [Error] public partial union Full({{cases}});
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, d => d.Id == "MEFF003");
        var generated = build.Generated["App.Full.Error.g.cs"];
        Assert.DoesNotContain("out (int A, int B) value", generated);
        if (direct) Assert.Contains("out (int X, int Y) value", generated);
        else Assert.DoesNotContain("out (int X, int Y) value", generated);
    }

    [Fact]
    public void RestOf_RenamedTupleInsideAGroupingUnion_Compiles()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Full((int X, int Y), bool, string);
            public union Group((int A, int B), bool);
            [RestOf<Full>] public partial union Rest(Group);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("(global::App.Group)other", build.Generated["App.Rest.RestOf.g.cs"]);
    }

    [Fact]
    public void RestOf_NullableValueTupleIsDifferent_ReportsTwoMissingCases()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Full((int X, int Y), (int X, int Y)?, string);
            [RestOf<Full>] public partial union Rest((int X, int Y));
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Equal(["MEFF202"], build.Errors);
    }

    [Fact]
    public void Catch_RenamedTupleTypeArgument_CompilesWithoutAnalyzerErrors()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Full((int X, int Y), string);
            [RestOf<Full>] public partial union Rest(string);
            public static class Use
            {
                public static Effect<int, Rest, EmptyEnvironment> Handle(Effect<int, Full, EmptyEnvironment> effect)
                    => effect.Catch<(int A, int B), Rest>(_ => Effect<EmptyEnvironment>.Succeed(1));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Catch_RestRetainsRenamedTuple_ReportsTheActuallyOmittedCase()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Full((int X, int Y), string);
            [RestOf<Full>] public partial union Rest((int A, int B));
            public static class Use
            {
                public static Effect<int, Rest, EmptyEnvironment> Handle(Effect<int, Full, EmptyEnvironment> effect)
                    => effect.Catch<(int X, int Y), Rest>(_ => Effect<EmptyEnvironment>.Succeed(1));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("'String'", build.Single("MEFF309").GetMessage());
        Assert.DoesNotContain("MEFF202", build.Errors);
    }
}
