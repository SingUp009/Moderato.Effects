namespace Moderato.Effects.SourceGenerator.Tests;

public class RestOfGeneratorTests
{
    private const string ERRORS = """
        namespace App;
        public record User(string Name);
        public record NotFound(int Id);
        public record Forbidden(string Reason);
        [Error] public partial union AppError(NotFound, Forbidden, TimeoutError);
        """;

    [Fact]
    public void RestOf_LeavesOutOneCase_LetsCatchRemoveIt()
    {
        // Given
        const string SOURCE = ERRORS + """

            [Error, RestOf<AppError>] public partial union AccessError(Forbidden, TimeoutError);

            public static class Use<R>
            {
                public static Effect<User, AccessError, R> Find(Effect<User, AppError, R> find)
                    => find.Catch<NotFound, AccessError>(nf => Effect<R>.Succeed(new User("guest")));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void RestOf_CatchingACaseTheRestStillHolds_DoesNotCompile()
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
        Assert.Contains("CS0315", build.Errors);
    }

    [Fact]
    public void RestOf_LeavesOutTwoCases_ReportsMEFF202()
    {
        // Given
        const string SOURCE = ERRORS + """

            [RestOf<AppError>] public partial union AccessError(Forbidden);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        var message = build.Single("MEFF202").GetMessage();
        Assert.Contains("NotFound", message);
        Assert.Contains("TimeoutError", message);
    }

    [Fact]
    public void RestOf_LeavesOutNothing_ReportsMEFF202()
    {
        // Given
        const string SOURCE = ERRORS + """

            [RestOf<AppError>] public partial union Everything(NotFound, Forbidden, TimeoutError);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("none", build.Single("MEFF202").GetMessage());
    }

    [Fact]
    public void RestOf_SourceIsNotAUnion_ReportsMEFF201()
    {
        // Given
        const string SOURCE = ERRORS + """

            [RestOf<NotFound>] public partial union AccessError(Forbidden, TimeoutError);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF201", build.Errors);
    }

    [Fact]
    public void RestOf_NestedUnion_ReportsMEFF004()
    {
        // Given
        const string SOURCE = ERRORS + """

            public static partial class Errors
            {
                [RestOf<AppError>] public partial union AccessError(Forbidden, TimeoutError);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("[RestOf]", build.Single("MEFF004").GetMessage());
    }
}
