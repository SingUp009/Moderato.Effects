namespace Moderato.Effects.SourceGenerator.Tests;

public class DynamicServiceTests
{
    [Theory]
    [InlineData("object")]
    [InlineData("dynamic")]
    [InlineData("dynamic[]")]
    [InlineData("System.Collections.Generic.List<dynamic>")]
    public void Environment_ObjectServiceSpelling_Compiles(string service)
    {
        // Given
        var source = $$"""
            namespace App;
            [Environment] public sealed partial class Env({{service}} service);
            public static class Use
            {
                public static {{service.Replace("dynamic", "object")}} Read(Env env) => env.Resolve<{{service.Replace("dynamic", "object")}}>();
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData("nint", "IntPtr")]
    [InlineData("nuint", "UIntPtr")]
    [InlineData("dynamic", "object")]
    [InlineData("dynamic[]", "object[]")]
    [InlineData("System.Collections.Generic.List<dynamic>", "System.Collections.Generic.List<object>")]
    public void Environment_EquivalentServiceSpellings_ReportsDuplicateService(string first, string second)
    {
        // Given
        var source = $$"""
            namespace App;
            [Environment] public sealed partial class Env({{first}} first, {{second}} second);
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Contains("MEFF103", build.Errors);
    }

    [Theory]
    [InlineData("nint", "IntPtr")]
    [InlineData("nuint", "UIntPtr")]
    public void RestOf_NativeIntegerSpelledAsFrameworkType_Compiles(string native, string framework)
    {
        // Given
        var source = $$"""
            namespace App;
            [Error] public partial union Source({{native}}, string);
            [RestOf<Source>] public partial union Rest({{framework}});
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
    }
}
