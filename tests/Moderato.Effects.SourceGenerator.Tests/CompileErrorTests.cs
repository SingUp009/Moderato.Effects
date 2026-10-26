namespace Moderato.Effects.SourceGenerator.Tests;

// Mistakes the types must reject (the failing cases of spike S1, plus the random number need of a jittered schedule).
public class CompileErrorTests
{
    private static string Program(string body) => $$"""
        namespace App;
        public record User(string Name, bool IsActive);
        public record NotFound(int Id);
        public record Forbidden(string Reason);
        [Error] public partial union AppError(NotFound, Forbidden);
        public sealed class AppEnv;

        public static class Cases<R> where R : IHas<TimeProvider>
        {
            static Effect<User, AppError, R> FindUser(int id) => default;

            public static object M()
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public void ScopeRun_EffectWithAnUnrelatedError_DoesNotCompile()
    {
        // Given
        var source = Program("return Scope<AppError, R>.Gen(async s => await s.Run(default(Effect<int, string, R>)));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS1503", build.Errors);
    }

    [Fact]
    public void Timeout_ErrorWithoutATimeoutCase_DoesNotCompile()
    {
        // Given
        var source = Program("return default(Effect<int, string, R>).Timeout(TimeSpan.FromSeconds(1));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS0311", build.Errors);
    }

    [Fact]
    public void ScopeRun_EffectForAnotherEnvironment_DoesNotCompile()
    {
        // Given
        var source = Program("return Scope<AppError, R>.Gen(async s => await s.Run(default(Effect<int, AppError, AppEnv>)));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS1503", build.Errors);
    }

    [Fact]
    public void AndThen_FailWithACaseInsteadOfTheUnion_DoesNotCompile()
    {
        // Given
        var source = Program("return FindUser(1).AndThen<string>(u => u.IsActive ? Effect<R>.Succeed(u.Name) : Effect<R>.Fail(new Forbidden(\"x\")));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS1662", build.Errors);
    }

    [Fact]
    public void CatchCase_TypeThatIsNotACase_DoesNotCompile()
    {
        // Given
        var source = Program("return FindUser(1).CatchCase((string s) => Effect<R>.Succeed(new User(s, true)));");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS0315", build.Errors);
    }

    [Fact]
    public void Retry_JitteredScheduleWithoutARandomInTheEnvironment_DoesNotCompile()
    {
        // Given
        var source = Program("return FindUser(1).Retry(Schedule.Spaced(TimeSpan.FromSeconds(1)).UpTo(3).Jittered());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("CS0314", build.Errors);
    }

    [Fact]
    public void Retry_JitteredScheduleWithARandomInTheEnvironment_Compiles()
    {
        // Given
        var source = Program("return default(Effect<int, AppError, DefaultEnvironment>).Retry(Schedule.Spaced(TimeSpan.FromSeconds(1)).UpTo(3).Jittered());");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }
}
