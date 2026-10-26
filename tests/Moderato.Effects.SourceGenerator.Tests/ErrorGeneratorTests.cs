namespace Moderato.Effects.SourceGenerator.Tests;

public class ErrorGeneratorTests
{
    [Fact]
    public void Error_PartialUnion_ImplementsIUnionCaseForEachCase()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record NotFound(int Id);
            public record Forbidden(string Reason);
            [Error] public partial union AppError(NotFound, Forbidden);

            public static class Use
            {
                static E Widen<E, C>(C value) where E : IUnionCase<E, C> => E.From(value);
                public static AppError Both() => Widen<AppError, NotFound>(new NotFound(1)) is NotFound ? Widen<AppError, Forbidden>(new Forbidden("x")) : default;
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("App.AppError.Error.g.cs", build.Generated.Keys);
    }

    [Fact]
    public void Error_CaseThatIsAUnion_AlsoImplementsItsCases()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record ConnFailed(string Host);
            public record QueryFailed(string Sql);
            public record NotFound(int Id);
            public union DbError(ConnFailed, QueryFailed);
            [Error] public partial union AppError(DbError, NotFound);

            public static class Use
            {
                static E Widen<E, C>(C value) where E : IUnionCase<E, C> => E.From(value);
                public static AppError Conn() => Widen<AppError, ConnFailed>(new ConnFailed("db"));
                public static AppError Db() => Widen<AppError, DbError>(new QueryFailed("select"));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionCase<AppError, global::App.ConnFailed>", build.Generated["App.AppError.Error.g.cs"]);
    }

    [Fact]
    public void Error_CaseReachableTwoWays_WarnsAndSkipsTheNestedConversion()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record Timeout(int Ms);
            public record ConnFailed(string Host);
            public record Dns(string Name);
            public union DbError(ConnFailed, Timeout);
            public union NetError(Dns, Timeout);
            [Error] public partial union AppError(DbError, NetError);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("Timeout", build.Single("MEFF003").GetMessage());
        Assert.DoesNotContain("global::App.Timeout>", build.Generated["App.AppError.Error.g.cs"]);
    }

    [Fact]
    public void Error_UnionWithoutTheAttribute_GeneratesNothing()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record NotFound(int Id);
            public partial union AppError(NotFound, string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Generated);
    }

    [Fact]
    public void Error_NotPartial_ReportsMEFF001()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record NotFound(int Id);
            [Error] public union AppError(NotFound, string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF001", build.Errors);
    }

    [Fact]
    public void Error_NotAUnion_ReportsMEFF001()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Error] public partial record AppError(string Message);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF001", build.Errors);
    }

    [Fact]
    public void Error_GenericUnion_ReportsMEFF002()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record NotFound<T>(T Id);
            [Error] public partial union AppError<T>(NotFound<T>, string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF002", build.Errors);
    }

    [Fact]
    public void Error_NestedUnion_ReportsMEFF004()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public record NotFound(int Id);
            public static partial class Errors
            {
                [Error] public partial union AppError(NotFound, string);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF004", build.Errors);
    }

    [Fact]
    public void Error_NullableReferenceCase_Compiles()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Error] public partial union Note(string?, int);
            [RestOf<Note>] public partial union NoteNumber(int);
            [RestOf<Note>] public partial union NoteText(string?);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Error_NullableValueCase_Compiles()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Error] public partial union Count(int?, string);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }
}
