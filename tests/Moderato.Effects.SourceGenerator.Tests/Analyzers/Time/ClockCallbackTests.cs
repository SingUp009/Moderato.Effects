namespace Moderato.Effects.SourceGenerator.Tests;

public class ClockCallbackTests
{
    private static string InClass(string members) => $$"""
        namespace App;
        public static class Steps<R> where R : IHas<TimeProvider>
        {
            static Effect<int, string, R> Load() => Effect<R>.Succeed(1);

            {{members}}
        }
        """;

    [Theory]
    [InlineData("Load().Map(_ => DateTime.UtcNow.Second)")]
    [InlineData("Load().AndThen(_ => { Thread.Sleep(1); return Load(); })")]
    [InlineData("Load().Tap(_ => Console.WriteLine(DateTime.Now))")]
    [InlineData("Load().MapErr(error => error + DateTimeOffset.UtcNow)")]
    [InlineData("Load().Match(value => value, _ => Environment.TickCount)")]
    [InlineData("from value in Load() select value + DateTime.UtcNow.Second")]
    [InlineData("Load().Retry(context => { _ = DateTime.UtcNow; return Effect<R>.Succeed(false); })")]
    [InlineData("Load().Retry(Schedule.Immediately.UpTo(1), _ => DateTime.UtcNow.Second == 0)")]
    [InlineData("Load().Timeout(TimeSpan.FromSeconds(1), _ => DateTime.UtcNow.ToString())")]
    [InlineData("Resource.Make(Load(), (_, _) => { _ = DateTime.UtcNow; return Effect<R>.Succeed(Unit.Value); }).Use(_ => Load())")]
    [InlineData("Resource.Make(Load(), (_, _) => Effect<R>.Succeed(Unit.Value)).Use(value => Load().Map(_ => value + DateTime.UtcNow.Second))")]
    [InlineData("Load().Map(value => new[] { value }.Select(item => item + DateTime.UtcNow.Second).Sum())")]
    public void Callback_ReadsTheSystemClockInALibraryCallback_ReportsMEFF307(string expression)
    {
        // Given
        var source = "using System.Linq;\n" + InClass($"public static object Work() => {expression};");

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF307");
    }

    [Theory]
    [InlineData("static async ValueTask<int> Body(Scope<string, R> scope) { await Task.Delay(1); return 0; }")]
    [InlineData("static ValueTask<int> Body(Scope<string, R> scope) => new(DateTime.UtcNow.Second);")]
    public void Method_TakesAScopeAndReadsTheSystemClock_ReportsMEFF307(string method)
    {
        // Given
        var source = InClass(method);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF307");
    }

    [Theory]
    [InlineData("public static object Work() => new[] { 1 }.Select(item => item + DateTime.UtcNow.Second).Sum();")]
    [InlineData("public static object Work() => Schedule.Spaced(TimeSpan.FromSeconds(DateTime.UtcNow.Second));")]
    [InlineData("public static object Work() { var now = DateTime.UtcNow; return Load().Map(value => value + now.Second); }")]
    [InlineData("public static object Work() => Load().Map(_ => nameof(DateTime.UtcNow));")]
    [InlineData("public static object Work() => Load().Map(_ => TimeProvider.System.GetUtcNow().Second + 0 * 0).Provide<R>(env => env);")]
    public void Callback_ClockReadOutsideALibraryCallback_ReportsOnlyWhatRunsInTheEffect(string member)
    {
        // Given
        var expected = member.Contains("TimeProvider.System") ? 1 : 0;
        var source = "using System.Linq;\n" + InClass(member);

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.Equal(expected, build.Diagnostics.Count(static diagnostic => diagnostic.Id == "MEFF307"));
    }

    [Fact]
    public void Callback_PassedToApplicationCodeInALibraryLikeNamespace_ReportsNothing()
    {
        // Given
        const string SOURCE = """
            namespace Moderato.Effects.MyApp
            {
                public static class Util
                {
                    public static T Twice<T>(Func<T> callback) { callback(); return callback(); }
                }
            }

            namespace App
            {
                public static class Clock
                {
                    public static DateTime Now() => Moderato.Effects.MyApp.Util.Twice(() => DateTime.UtcNow);
                }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Diagnostics);
    }
}
