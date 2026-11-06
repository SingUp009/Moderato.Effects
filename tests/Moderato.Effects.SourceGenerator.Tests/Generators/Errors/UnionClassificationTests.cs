namespace Moderato.Effects.SourceGenerator.Tests;

public class UnionClassificationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Error_UntaggedUnionCarrier_TreatsAsOpaqueCase(bool metadata, bool valueType, bool conversion)
    {
        // Given
        var carrier = Carrier(valueType, conversion, tagged: false);
        const string TARGET = """
            namespace App;
            [Error] public partial union Top(Carrier, string);
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : carrier + TARGET.Replace("namespace App;", ""), metadata ? carrier : null);

        // Then
        Assert.Empty(build.Errors);
        var generated = build.Generated["App.Top.Error.g.cs"];
        Assert.Contains("IUnionCase<Top, global::App.Carrier>", generated);
        Assert.DoesNotContain("global::App.Payload", generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_TaggedManualUnion_PreservesTransitiveCase(bool metadata)
    {
        // Given
        var carrier = Carrier(valueType: true, conversion: true, tagged: true);
        const string TARGET = """
            namespace App;
            [Error] public partial union Top(Carrier, string);
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : carrier + TARGET.Replace("namespace App;", ""), metadata ? carrier : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionCase<Top, global::App.Payload>", build.Generated["App.Top.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_CompilerUnion_PreservesTransitiveCase(bool metadata)
    {
        // Given
        const string DEPENDENCY = "namespace App; public union Inner(int, string);";
        const string TARGET = "namespace App; [Error] public partial union Top(Inner, System.Guid);";

        // When
        var build = Compiler.Build(metadata ? TARGET : DEPENDENCY + TARGET.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionCase<Top, int>", build.Generated["App.Top.Error.g.cs"]);
    }

    [Fact]
    public void Error_UntaggedCarrierTarget_ReportsPartialUnionRequirement()
    {
        // Given
        const string SOURCE = """
            namespace App;
            [Error] public sealed partial class Carrier(int value) : System.Runtime.CompilerServices.IUnion
            {
                public object? Value => value;
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        build.Single("MEFF001");
        Assert.Empty(build.Generated);
    }

    [Fact]
    public void RestOf_UntaggedCarrierTarget_ReportsPartialUnionRequirement()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public union Source(int, string);
            [RestOf<Source>] public sealed partial class Carrier(int value) : System.Runtime.CompilerServices.IUnion
            {
                public object? Value => value;
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        build.Single("MEFF001");
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestOf_UntaggedCarrierSource_ReportsUnionRequirement(bool metadata)
    {
        // Given
        var carrier = Carrier(valueType: false, conversion: true, tagged: false);
        const string TARGET = "namespace App; [RestOf<Carrier>] public partial union Remaining(string);";

        // When
        var build = Compiler.Build(metadata ? TARGET : carrier + TARGET.Replace("namespace App;", ""), metadata ? carrier : null);

        // Then
        build.Single("MEFF201");
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestOf_UntaggedCarrierCase_PreservesOpaqueRemainingCase(bool metadata)
    {
        // Given
        var carrier = Carrier(valueType: false, conversion: true, tagged: false);
        const string TARGET = """
            namespace App;
            public union Source(Carrier, string);
            [Error, RestOf<Source>] public partial union Remaining(Carrier);
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Source, EmptyEnvironment> effect)
                    => effect.Catch<string, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : carrier + TARGET.Replace("namespace App;", ""), metadata ? carrier : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain(build.Generated.Values, static text => text.Contains("global::App.Payload"));
    }

    private static string Carrier(bool valueType, bool conversion, bool tagged)
        => $$"""
            namespace App;
            public sealed record Payload(int Code);
            {{(tagged ? "[System.Runtime.CompilerServices.Union]" : "")}}
            public {{(valueType ? "readonly struct" : "sealed class")}} Carrier(Payload value) : System.Runtime.CompilerServices.IUnion
            {
                public object? Value => value;
                {{(conversion ? "public static implicit operator Carrier(Payload value) => new(value);" : "")}}
            }

            """;
}
