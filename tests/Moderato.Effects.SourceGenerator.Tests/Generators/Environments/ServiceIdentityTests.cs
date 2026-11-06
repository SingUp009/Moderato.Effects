namespace Moderato.Effects.SourceGenerator.Tests;

public class ServiceIdentityTests
{
    [Theory]
    [InlineData("string? First, [Expose<string>] string Second")]
    [InlineData("(int Id, string Name) First, (int Code, string Text) Second")]
    [InlineData("System.Collections.Generic.List<string?> First, System.Collections.Generic.List<string> Second")]
    [InlineData("(int Id, (string? Name, int Code) Detail) First, (int Key, (string Text, int Number) Data) Second")]
    [InlineData("(int Id, string Name)[] First, (int Code, string Text)[] Second")]
    public void Environment_EquivalentServiceTypes_ReportsMEFF103(string parameters)
    {
        // Given
        var source = $$"""
            namespace App;
            [Environment] public sealed partial record AppEnv({{parameters}});
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Equal(["MEFF103"], build.Errors);
        Assert.Contains("'First' and 'Second'", build.Single("MEFF103").GetMessage());
        Assert.Empty(build.Generated);
    }

    [Fact]
    public void Environment_NamedTupleService_PreservesElementNames()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Environment] public sealed partial record AppEnv((int Id, string Name) Owner);
            public static class Use
            {
                public static int Id(AppEnv env) => env.Resolve<(int Id, string Name)>().Id;
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IHas<(int Id, string Name)>", build.Generated["App.AppEnv.Environment.g.cs"]);
    }

    [Fact]
    public void Environment_ValueAndNullableValueServices_KeepBothTypes()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Environment] public sealed partial record AppEnv(int Count, int? Limit);
            public static class Use
            {
                public static int Read(AppEnv env) => env.Resolve<int>() + (env.Resolve<int?>() ?? 0);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }
}
