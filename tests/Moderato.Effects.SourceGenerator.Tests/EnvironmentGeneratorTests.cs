namespace Moderato.Effects.SourceGenerator.Tests;

public class EnvironmentGeneratorTests
{
    private const string SERVICES = """
        namespace App;
        public interface IUserRepository;
        public interface IAuditLogger;
        public sealed class SqlUsers : IUserRepository;
        """;

    [Fact]
    public void Environment_Record_ImplementsIHasForEachParameter()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial record AppEnv(IUserRepository Users, IAuditLogger Audit, TimeProvider Clock);

            public static class Use
            {
                public static TimeProvider Clock(AppEnv env) => env.Resolve<TimeProvider>();
                public static IUserRepository Users(AppEnv env) => env.Resolve<IUserRepository>();
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Environment_Class_ImplementsIHasForEachParameter()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial class AppEnv(IUserRepository users, TimeProvider clock);

            public static class Use
            {
                public static TimeProvider Clock(AppEnv env) => env.Resolve<TimeProvider>();
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Environment_DeclaresADependencyInterface_SatisfiesIt()
    {
        // Given
        const string SOURCE = SERVICES + """

            public interface IProfileDeps : IHas<IUserRepository>, IHas<TimeProvider>;

            [Environment]
            public sealed partial record AppEnv(IUserRepository Users, TimeProvider Clock) : IProfileDeps;

            public static class Profiles<R> where R : IProfileDeps
            {
                public static Effect<DateTimeOffset, Never, R> Now() => Effect<R>.Access(env => env.Resolve<TimeProvider>().GetUtcNow());
            }

            public static class Use
            {
                public static Effect<DateTimeOffset, Never, AppEnv> Now() => Profiles<AppEnv>.Now();
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Environment_ParameterWithExpose_ProvidesTheExposedType()
    {
        // Given
        const string SOURCE = SERVICES + """

            public sealed class FakeClock : TimeProvider;

            [Environment]
            public sealed partial record TestEnv([Expose<TimeProvider>] FakeClock Clock, [Expose<IUserRepository>] SqlUsers Users);

            public static class Use
            {
                public static Effect<Unit, Never, TestEnv> Nap() => Effect.Sleep<TestEnv>(TimeSpan.FromSeconds(1));
                public static FakeClock Fake(TestEnv env) => env.Clock;
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
    }

    [Fact]
    public void Environment_TwoParametersOfTheSameType_ReportsMEFF103()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial record AppEnv(IUserRepository Primary, IUserRepository Replica);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("Replica", build.Single("MEFF103").GetMessage());
    }

    [Fact]
    public void Environment_ExposedTypeNotImplemented_ReportsMEFF104()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial record AppEnv([Expose<IAuditLogger>] SqlUsers Users);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF104", build.Errors);
    }

    [Fact]
    public void Environment_NoPrimaryConstructor_ReportsMEFF102()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial class AppEnv
            {
                public AppEnv(IUserRepository users) { }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF102", build.Errors);
    }

    [Fact]
    public void Environment_NotPartial_ReportsMEFF101()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed record AppEnv(IUserRepository Users);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("MEFF101", build.Errors);
    }

    [Fact]
    public void Environment_GenericType_ReportsMEFF002()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial record AppEnv<T>(IUserRepository Users, T Extra);
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("[Environment]", build.Single("MEFF002").GetMessage());
    }

    [Fact]
    public void Environment_NestedType_ReportsMEFF004()
    {
        // Given
        const string SOURCE = SERVICES + """

            public static partial class Environments
            {
                [Environment]
                public sealed partial record AppEnv(IUserRepository Users);
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("[Environment]", build.Single("MEFF004").GetMessage());
    }

    [Fact]
    public void Environment_KeywordTypeName_Compiles()
    {
        // Given
        const string SOURCE = SERVICES + """

            [Environment]
            public sealed partial record @class(IUserRepository Users, TimeProvider Clock);

            public static class Use
            {
                public static TimeProvider Clock(@class env) => env.Resolve<TimeProvider>();
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.Empty(build.Warnings);
    }
}
