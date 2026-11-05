namespace Moderato.Effects.SourceGenerator.Tests;

public class ExitCaseAccessTests
{
    [Fact]
    public void TryGetValue_CorrectCases_CompilesWithNonNullOutputs()
    {
        // Given
        const string SOURCE = """
            public static class Use
            {
                public static string Read(ExitCase exit)
                {
                    if (exit.TryGetValue(out Succeeded? succeeded)) return succeeded.ToString();
                    if (exit.TryGetValue(out Failed? failed)) return failed.ToString();
                    if (exit.TryGetValue(out Die? die)) return die.Exception.Message;
                    if (exit.TryGetValue(out Interrupted? interrupted)) return interrupted.ToString();
                    return exit.HasValue.ToString();
                }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Warnings);
    }

    [Fact]
    public void TryGetValue_OkUnitCase_ReportsTypeError()
    {
        // Given
        const string SOURCE = """
            public static class Use
            {
                public static bool Read(ExitCase exit) => exit.TryGetValue(out Ok<Unit> success);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("CS1503", build.Errors);
    }
}
