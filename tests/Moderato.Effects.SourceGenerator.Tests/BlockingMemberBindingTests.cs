namespace Moderato.Effects.SourceGenerator.Tests;

public class BlockingMemberBindingTests
{
    [Fact]
    public void Gen_FunctionPointerMemberOnAwaiterType_DoesNotCrashAnalyzer()
    {
        // Given
        const string SOURCE = """
            namespace System.Runtime.CompilerServices
            {
                public unsafe struct PointerAwaiter
                {
                    public delegate*<int> GetResult;
                }
            }
            namespace App
            {
                public static unsafe class Use
                {
                    public static Effect<int, Never, EmptyEnvironment> Make(System.Runtime.CompilerServices.PointerAwaiter receiver)
                        => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>(receiver.GetResult()));
                }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Contains("CS0227", build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
    }

    [Fact]
    public void Gen_InheritedAwaiterInterfaceMethod_KeepsBlockingWaitDiagnostic()
    {
        // Given
        const string SOURCE = """
            namespace System.Runtime.CompilerServices
            {
                public interface IParentAwaiter : INotifyCompletion
                {
                    bool IsCompleted { get; }
                    int GetResult();
                }
                public interface IChildAwaiter : IParentAwaiter;
            }
            namespace App
            {
                public static class Use
                {
                    public static Effect<int, Never, EmptyEnvironment> Make(System.Runtime.CompilerServices.IChildAwaiter receiver)
                        => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>(receiver.GetResult()));
                }
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF306");
    }

    [Theory]
    [InlineData("Task", "Task.CompletedTask", "Wait")]
    [InlineData("TaskAwaiter<int>", "Task.FromResult(1).GetAwaiter()", "GetResult")]
    public void Gen_ExtensionBlockWaitOrGetResult_DoesNotReportBlockingWait(string receiverType, string receiver, string method)
    {
        // Given
        var source = $$"""
            using System.Runtime.CompilerServices;
            namespace App;
            public static class Extras
            {
                extension({{receiverType}} receiver)
                {
                    public int {{method}}(string label) => 42;
                }
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>({{receiver}}.{{method}}("label")));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain("MEFF306", build.Ids);
    }

    public static IEnumerable<object[]> Members =>
    [
        ["Task", "Task.CompletedTask", "Wait", "Action"],
        ["Task<int>", "Task.FromResult(1)", "Wait", "Action"],
        ["TaskAwaiter", "Task.CompletedTask.GetAwaiter()", "GetResult", "Action"],
        ["TaskAwaiter<int>", "Task.FromResult(1).GetAwaiter()", "GetResult", "Func<int>"],
        ["ValueTaskAwaiter", "ValueTask.CompletedTask.GetAwaiter()", "GetResult", "Action"],
        ["ValueTaskAwaiter<int>", "new ValueTask<int>(1).GetAwaiter()", "GetResult", "Func<int>"],
        ["ConfiguredTaskAwaitable.ConfiguredTaskAwaiter", "Task.CompletedTask.ConfigureAwait(false).GetAwaiter()", "GetResult", "Action"],
        ["ConfiguredTaskAwaitable<int>.ConfiguredTaskAwaiter", "Task.FromResult(1).ConfigureAwait(false).GetAwaiter()", "GetResult", "Func<int>"],
        ["ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter", "ValueTask.CompletedTask.ConfigureAwait(false).GetAwaiter()", "GetResult", "Action"],
        ["ConfiguredValueTaskAwaitable<int>.ConfiguredValueTaskAwaiter", "new ValueTask<int>(1).ConfigureAwait(false).GetAwaiter()", "GetResult", "Func<int>"],
    ];

    public static IEnumerable<object[]> Calls => Members.Select(static row => row.Take(3).ToArray());

    [Theory]
    [MemberData(nameof(Calls))]
    public void Gen_UnrelatedWaitOrGetResultExtension_DoesNotReportBlockingWait(string receiverType, string receiver, string method)
    {
        // Given
        var source = $$"""
            using System.Runtime.CompilerServices;
            namespace App;
            public static class Extras
            {
                public static int {{method}}(this {{receiverType}} receiver, string label) => 42;
            }
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        {{receiverType}} receiver = {{receiver}};
                        return new ValueTask<int>(receiver.{{method}}("label"));
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain("MEFF306", build.Ids);
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void Gen_UninvokedBlockingMethodGroup_DoesNotReportBlockingWait(string receiverType, string receiver, string method, string delegateType)
    {
        // Given
        var source = $$"""
            using System.Runtime.CompilerServices;
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        {{receiverType}} receiver = {{receiver}};
                        var callback = ({{delegateType}})receiver.{{method}};
                        _ = callback;
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain("MEFF306", build.Ids);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public void Gen_InvokedBlockingMethod_ReportsBlockingWait(string receiverType, string receiver, string method)
    {
        // Given
        var source = $$"""
            using System.Runtime.CompilerServices;
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope =>
                    {
                        {{receiverType}} receiver = {{receiver}};
                        receiver.{{method}}();
                        return new ValueTask<int>(42);
                    });
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF306");
    }

    [Theory]
    [InlineData("Task<int>")]
    [InlineData("ValueTask<int>")]
    public void Gen_ResultMentionedInNameof_DoesNotReportBlockingWait(string type)
    {
        // Given
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>(nameof({{type}}.Result).Length));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.DoesNotContain("MEFF306", build.Ids);
    }

    [Theory]
    [InlineData("Task.FromResult(1)")]
    [InlineData("new ValueTask<int>(1)")]
    public void Gen_ResultPropertyRead_ReportsBlockingWait(string receiver)
    {
        // Given
        var source = $$"""
            namespace App;
            public static class Use
            {
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>({{receiver}}.Result));
            }
            """;

        // When
        var build = Compiler.Build(source);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF306");
    }

    [Fact]
    public void Gen_ResultPassedToUserMethodNamedNameof_ReportsBlockingWait()
    {
        // Given
        const string SOURCE = """
            namespace App;
            public static class Use
            {
                private static int @nameof(int value) => value;
                public static Effect<int, Never, EmptyEnvironment> Make()
                    => Scope<Never, EmptyEnvironment>.Gen(scope => new ValueTask<int>(@nameof(Task.FromResult(1).Result)));
            }
            """;

        // When
        var build = Compiler.Build(SOURCE);

        // Then
        Assert.Empty(build.Errors);
        Assert.DoesNotContain("AD0001", build.Ids);
        Assert.Single(build.Diagnostics, static diagnostic => diagnostic.Id == "MEFF306");
    }
}
