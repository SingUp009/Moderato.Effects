namespace Moderato.Effects.SourceGenerator.Tests;

public class UnionMemberProviderTests
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
    public void Error_UnionMemberProvider_PreservesTransitiveCases(bool metadata, bool referenceType, bool inherited)
    {
        // Given
        var dependency = Provider(referenceType, inherited);
        const string TARGET = """
            namespace App;
            [Error] public partial union Top(Outcome<string>, Guid);
            public static class Use
            {
                public static Effect<Never, Top, EmptyEnvironment> Widen()
                    => Effect<EmptyEnvironment>.Fail("hello").Widen<Top>();
            }
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : dependency + TARGET.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("MEFF003", build.Warnings);
        Assert.Contains("IUnionCase<Top, string>", build.Generated["App.Top.Error.g.cs"]);
        Assert.Contains("IUnionCase<Top, global::System.Exception>", build.Generated["App.Top.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RestOf_ProviderSourceAndTarget_CatchesDirectCase(bool metadata, bool providerTarget)
    {
        // Given
        var dependency = Provider(referenceType: false, inherited: true);
        var target = "namespace App; " + (providerTarget ? """
            [System.Runtime.CompilerServices.Union, Error, RestOf<Outcome<string>>]
            public partial struct Remaining : Remaining.IUnionMembers
            {
                private readonly object? value;
                private Remaining(object? value) => this.value = value;
                public interface IUnionMembers
                {
                    static Remaining Create(string value) => new(value);
                    object? Value { get; }
                }
                object? IUnionMembers.Value => value;
            }
            """ : "[Error, RestOf<Outcome<string>>] public partial union Remaining(string);") + """
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Outcome<string>, EmptyEnvironment> effect)
                    => effect.Catch<Exception, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(metadata ? target : dependency + target.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionRest<Remaining, global::App.Outcome<string>, global::System.Exception>", build.Generated["App.Remaining.RestOf.g.cs"]);
        Assert.Contains("IUnionCase<Remaining, string>", build.Generated["App.Remaining.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catch_ProviderSourceAndWrongCase_ReportsActualMissingCase(bool metadata)
    {
        // Given
        var dependency = Provider(referenceType: false, inherited: true);
        const string TARGET = """
            namespace App;
            [RestOf<Outcome<string>>] public partial union Remaining(string);
            public static class Use
            {
                public static Effect<int, Remaining, EmptyEnvironment> Catch(Effect<int, Outcome<string>, EmptyEnvironment> effect)
                    => effect.Catch<string, Remaining>(_ => Effect<EmptyEnvironment>.Succeed(42));
            }
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : dependency + TARGET.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        var diagnostic = build.Single("MEFF309");
        Assert.Contains("Exception", diagnostic.GetMessage());
        Assert.DoesNotContain("MEFF202", build.Errors);
    }

    [Theory]
    [InlineData(false, "Error")]
    [InlineData(false, "RestOf<Source>")]
    [InlineData(true, "Error")]
    [InlineData(true, "RestOf<Source>")]
    public void Generate_ExpandingProvider_ReportsLimitWithoutPartialOutput(bool metadata, string attribute)
    {
        // Given
        const string DEPENDENCY = """
            namespace App;
            [System.Runtime.CompilerServices.Union]
            public struct Chain<T> : Chain<T>.IUnionMembers
            {
                private readonly object? value;
                private Chain(object? value) => this.value = value;
                public interface IUnionMembers
                {
                    static Chain<T> Create(T value) => new(value);
                    static Chain<T> Create(Chain<System.Collections.Generic.List<T>> value) => new(value);
                    object? Value { get; }
                }
                object? IUnionMembers.Value => value;
            }
            """;
        var target = $$"""
            namespace App;
            public union Source(Chain<int>, string);
            [{{attribute}}] public partial union Target(Chain<int>);
            [Error] public partial union Good(int);
            """;

        // When
        var build = Compiler.Build(metadata ? target : DEPENDENCY + target.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);

        // Then
        build.Single("MEFF007");
        Assert.DoesNotContain(build.Generated.Keys, static key => key.Contains("Target"));
        Assert.Contains("App.Good.Error.g.cs", build.Generated.Keys);
        Assert.DoesNotContain("CS8785", build.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_OverlappingProviderCases_ReportsOverlap(bool metadata)
    {
        // Given
        const string DEPENDENCY = """
            namespace App;
            public interface IA;
            public interface IB;
            [System.Runtime.CompilerServices.Union]
            public struct Ambiguous : Ambiguous.IUnionMembers
            {
                private readonly object? value;
                private Ambiguous(object? value) => this.value = value;
                public interface IUnionMembers
                {
                    static Ambiguous Create(IA value) => new(value);
                    static Ambiguous Create(IB value) => new(value);
                    object? Value { get; }
                }
                object? IUnionMembers.Value => value;
            }
            """;
        const string TARGET = "namespace App; [Error] public partial union Top(Ambiguous);";

        // When
        var build = Compiler.Build(metadata ? TARGET : DEPENDENCY + TARGET.Replace("namespace App;", ""), metadata ? DEPENDENCY : null);

        // Then
        build.Single("MEFF008");
        Assert.Empty(build.Generated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_SelfCaseProvider_StopsCycleAndRetainsOtherCases(bool metadata)
    {
        // Given
        var dependency = Provider(referenceType: false, inherited: false).Replace(
            "static Outcome<T> Create(Exception value)",
            "static Outcome<T> Create(Outcome<T> value) => new(value); static Outcome<T> Create(Exception value)");
        const string TARGET = "namespace App; [Error] public partial union Top(Outcome<string>, Guid);";

        // When
        var build = Compiler.Build(metadata ? TARGET : dependency + TARGET.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        build.Single("MEFF003");
        Assert.Contains("IUnionCase<Top, string>", build.Generated["App.Top.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Error_InCreationParameter_PreservesCase(bool metadata, bool provider)
    {
        // Given
        var dependency = provider ? Provider(referenceType: false, inherited: false).Replace("Create(T value)", "Create(in T value)") : """
            namespace App;
            [System.Runtime.CompilerServices.Union]
            public readonly struct Outcome<T>
            {
                public Outcome(in T value) => Value = value;
                public object? Value { get; }
            }
            """;
        const string TARGET = "namespace App; [Error] public partial union Top(Outcome<string>, Guid);";

        // When
        var build = Compiler.Build(metadata ? TARGET : dependency + TARGET.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.Contains("IUnionCase<Top, string>", build.Generated["App.Top.Error.g.cs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Error_ProviderHidesInheritedFactory_DoesNotInventDuplicatePath(bool metadata)
    {
        // Given
        var dependency = Provider(referenceType: false, inherited: true).Replace(
            "static object Create(decimal value)",
            "new static Outcome<T> Create(T value) => new(value); static object Create(decimal value)");
        const string TARGET = """
            namespace App;
            [Error] public partial union Top(Outcome<string>, Guid);
            public static class Use
            {
                public static Effect<Never, Top, EmptyEnvironment> Widen()
                    => Effect<EmptyEnvironment>.Fail("hello").Widen<Top>();
            }
            """;

        // When
        var build = Compiler.Build(metadata ? TARGET : dependency + TARGET.Replace("namespace App;", ""), metadata ? dependency : null);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("MEFF003", build.Warnings);
    }

    private static string Provider(bool referenceType, bool inherited)
        => $$"""
            namespace App;
            [System.Runtime.CompilerServices.Union]
            public {{(referenceType ? "sealed class" : "struct")}} Outcome<T> : Outcome<T>.IUnionMembers
            {
                private readonly object? value;
                private Outcome(object? value) => this.value = value;
                public Outcome(Guid ignored) => value = null;
                {{(inherited ? "public interface IFactory { static Outcome<T> Create(T value) => new(value); }" : "")}}
                public interface IUnionMembers {{(inherited ? ": IFactory" : "")}}
                {
                    {{(inherited ? "" : "static Outcome<T> Create(T value) => new(value);")}}
                    static Outcome<T> Create(Exception value) => new(value);
                    static object Create(decimal value) => value;
                    static Outcome<T> Create<U>(U value) => new(value);
                    Outcome<T> Create(DateTime value) => new((object?)null);
                    static Outcome<T> Create(DateOnly value, bool ignored) => new((object?)null);
                    object? Value { get; }
                }
                object? IUnionMembers.Value => value;
            }

            """;
}
