using System.Threading.Tasks.Sources;

namespace Moderato.Effects.Tests;

[Collection(nameof(DiagnosticsCollection))]
public class StepConsumptionTests
{
    public static TheoryData<string, string, bool, bool, bool> Cases
    {
        get
        {
            var cases = new TheoryData<string, string, bool, bool, bool>();
            foreach (var method in new[] { "Run", "Acquire" })
            {
                foreach (var adapter in new[] { "AsTask", "Preserve" })
                {
                    foreach (var beforeAdapter in new[] { false, true })
                    {
                        foreach (var bodyDies in new[] { false, true })
                        {
                            foreach (var expectedError in new[] { false, true }) cases.Add(method, adapter, beforeAdapter, bodyDies, expectedError);
                        }
                    }
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Gen_AdapterConsumesBeforeBodySnapshot_KeepsTransferredFailureOnTask(string method, string adapter, bool beforeAdapter, bool bodyDies, bool expectedError)
    {
        // Given
        var completion = new TaskCompletionSource<Result<int, string>>();
        var defect = new InvalidOperationException("transferred defect");
        var bodyDefect = new IOException("body defect");
        var operation = Effect<EmptyEnvironment>.FromResultAsync<int, string>(_ => new(completion.Task));
        var resource = Resource.Make(operation, (_, _) => Effect<EmptyEnvironment>.Succeed(Unit.Value));
        Task<int>? transferred = null;
        var reported = new List<ExitCase>();
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;
        var effect = Scope<string, EmptyEnvironment>.Gen<int>(scope =>
        {
#pragma warning disable MEFF310
            var step = method == "Acquire" ? scope.Acquire(resource) : scope.Run(operation);
#pragma warning restore MEFF310
            if (beforeAdapter) Complete();
            transferred = adapter == "AsTask" ? step.AsTask() : step.Preserve().AsTask();
            if (!beforeAdapter) Complete();
            Assert.True(transferred.IsFaulted);
            if (bodyDies) throw bodyDefect;
            return new(42);
        });

        try
        {
            // When
            var exit = await effect.RunExitAsync(TestContext.Current.CancellationToken);

            // Then
            Assert.NotNull(transferred);
            Assert.True(transferred.IsFaulted);
            if (bodyDies)
            {
                var die = Assert.IsType<Die>(exit.Value);
                Assert.Same(bodyDefect, die.Exception);
                if (expectedError)
                {
                    Assert.Equal("invalid", Assert.IsType<Failed>(Assert.Single(die.Suppressed).Case.Value).Error);
                }
                else
                {
                    Assert.Empty(die.Suppressed);
                }
            }
            else if (expectedError)
            {
                Assert.Equal("invalid", Assert.IsType<Err<string>>(exit.Value).Error);
            }
            else
            {
                Assert.Equal(42, Assert.IsType<Ok<int>>(exit.Value).Value);
            }
            Assert.Empty(reported);
            if (expectedError) await Assert.ThrowsAsync<ScopeFailure>(() => transferred);
            else Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => transferred));
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }

        void Complete()
        {
            if (expectedError) completion.SetResult(new Err<string>("invalid"));
            else completion.SetException(defect);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTask_AdapterTransfersFailure_TaskAwaitDoesNotReadSourceAgain(bool preserve)
    {
        // Given
        var source = new CountingSource();
        var valueTask = new ValueTask<int>(source, source.Version);
        var defect = new InvalidOperationException("source");

        // When
        var task = preserve ? valueTask.Preserve().AsTask() : valueTask.AsTask();
        source.Fail(defect);

        // Then
        Assert.True(task.IsFaulted);
        Assert.Equal(1, source.Reads);
        var accesses = source.Accesses;
        Assert.Same(defect, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
        Assert.Equal(1, source.Reads);
        Assert.Equal(accesses, source.Accesses);
    }

    private sealed class CountingSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> core;

        internal short Version => core.Version;

        internal int Reads { get; private set; }

        internal int Accesses { get; private set; }

        internal void Fail(Exception exception) => core.SetException(exception);

        public int GetResult(short token)
        {
            Accesses++;
            Reads++;
            return core.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            Accesses++;
            return core.GetStatus(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Accesses++;
            core.OnCompleted(continuation, state, token, flags);
        }
    }
}
