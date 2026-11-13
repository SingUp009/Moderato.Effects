using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Moderato.Effects.Tests;

[Collection(nameof(DiagnosticsCollection))]
public class ExceptionDataTests
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
    public async Task RunAsync_ExceptionDataRejectsMetadata_RethrowsOriginalDefectAndReportsMetadataFailure(bool readOnly, bool asynchronous, bool suppressed)
    {
        // Given
        var original = new RestrictedDataException(readOnly);
        var releaseFailure = new IOException("release");
        var reported = new List<ExitCase>();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = asynchronous
            ? Effect<EmptyEnvironment>.Async<int>(_ => new(completion.Task))
            : Effect<EmptyEnvironment>.Sync<int>(() => throw original);
        var effect = suppressed
            ? Resource.Make(Effect<EmptyEnvironment>.Succeed(1),
                (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw releaseFailure)).Use(_ => source)
            : source;
        Exception? thrown = null;
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;

        try
        {
            // When
            try
            {
                var running = effect.RunAsync(CancellationToken.None);
                if (asynchronous) completion.SetException(original);
                await running;
            }
            catch (Exception exception)
            {
                thrown = exception;
            }

            // Then
            Assert.Same(original, thrown);
            var metadataFailure = Assert.IsType<Die>(Assert.Single(reported).Value);
            if (readOnly) Assert.IsType<NotSupportedException>(metadataFailure.Exception);
            else Assert.Same(original.MetadataFailure, metadataFailure.Exception);
            if (suppressed)
            {
                var context = Assert.Single(metadataFailure.Suppressed);
                Assert.Equal(Relation.Sequential, context.Relation);
                Assert.Same(releaseFailure, Assert.IsType<Die>(context.Case.Value).Exception);
            }
            else
            {
                Assert.Empty(metadataFailure.Suppressed);
            }
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Fact]
    public async Task RunAsync_CanceledCallerAndMetadataOce_RethrowsOriginalDefect()
    {
        // Given
        using var caller = new CancellationTokenSource();
        var original = new RestrictedDataException(false) { MetadataFailure = new OperationCanceledException(caller.Token) };
        var effect = Effect<EmptyEnvironment>.Sync<int>(() =>
        {
            caller.Cancel();
            throw original;
        });
        var reported = new List<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;

        try
        {
            // When
            var thrown = await Assert.ThrowsAsync<RestrictedDataException>(async () => await effect.RunAsync(caller.Token));

            // Then
            Assert.Same(original, thrown);
            Assert.Same(original.MetadataFailure, Assert.IsType<Die>(Assert.Single(reported).Value).Exception);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Fact]
    public async Task RunAsync_MetadataDiagnosticHandlerThrows_RethrowsOriginalDefect()
    {
        // Given
        var original = new RestrictedDataException(false);
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw original);
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = _ => throw new IOException("handler");

        try
        {
            // When
            var thrown = await Assert.ThrowsAsync<RestrictedDataException>(async () => await effect.RunAsync(CancellationToken.None));

            // Then
            Assert.Same(original, thrown);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_ExceptionDataRejectsMetadata_PreservesDefectAndContext(bool readOnly)
    {
        // Given
        var original = new RestrictedDataException(readOnly);
        var releaseFailure = new IOException("release");
        var effect = Resource.Make(Effect<EmptyEnvironment>.Succeed(1),
            (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw releaseFailure))
            .Use(_ => Effect<EmptyEnvironment>.Sync<int>(() => throw original));
        var reported = new List<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;

        try
        {
            // When
            var exit = await effect.RunExitAsync(CancellationToken.None);

            // Then
            Assert.True(exit.TryGetValue(out Die? die));
            Assert.Same(original, die.Exception);
            var context = Assert.Single(die.Suppressed);
            Assert.Same(releaseFailure, Assert.IsType<Die>(context.Case.Value).Exception);
            Assert.Empty(reported);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(150, 50)]
    public async Task RunAsync_DefectWithManySuppressedFailures_AttachesTheOmittedCountWhenAnyWereLeftOut(int releases, int? omitted)
    {
        // Given
        var effect = Effect.Gen<int, string, EmptyEnvironment>(async scope =>
        {
            for (var i = 0; i <= releases; i++)
            {
                var failure = new IOException($"release {i}");
#pragma warning disable MEFF305
                scope.Defer(_ => Effect<EmptyEnvironment>.Sync<Unit>(() => throw failure));
#pragma warning restore MEFF305
            }
            await Task.Yield();
            return 1;
        });

        // When
        var thrown = await Assert.ThrowsAsync<IOException>(async () => await effect.RunAsync(TestContext.Current.CancellationToken));

        // Then
        var attached = Assert.IsType<ImmutableArray<Suppressed>>(thrown.Data[Effect<int, string, EmptyEnvironment>.SUPPRESSED_DATA_KEY]);
        Assert.Equal(100, attached.Length);
        Assert.Equal(omitted, (int?)thrown.Data[Effect<int, string, EmptyEnvironment>.OMITTED_SUPPRESSED_DATA_KEY]);
    }

    [Fact]
    public async Task RunAsync_ReusedExceptionWithAStaleOmittedCount_RemovesIt()
    {
        // Given
        var original = new IOException("original");
        original.Data[Effect<int, Never, EmptyEnvironment>.OMITTED_SUPPRESSED_DATA_KEY] = 7;
        var source = Effect<EmptyEnvironment>.Sync<int>(() => throw original);

        // When
        var thrown = await Assert.ThrowsAsync<IOException>(async () => await source.RunAsync(TestContext.Current.CancellationToken));

        // Then
        Assert.Same(original, thrown);
        Assert.False(original.Data.Contains(Effect<int, Never, EmptyEnvironment>.OMITTED_SUPPRESSED_DATA_KEY));
    }

    [Fact]
    public async Task RunAsync_ReusedMutableException_ReplacesThenRemovesOnlyLibraryMetadata()
    {
        // Given
        var original = new IOException("original");
        original.Data["caller"] = "keep";
        original.Data[Effect<int, Never, EmptyEnvironment>.SUPPRESSED_DATA_KEY] = "stale";
        var releaseFailure = new IOException("release");
        var source = Effect<EmptyEnvironment>.Sync<int>(() => throw original);
        var withContext = Resource.Make(Effect<EmptyEnvironment>.Succeed(1),
            (_, _) => Effect<EmptyEnvironment>.Sync<Unit>(() => throw releaseFailure)).Use(_ => source);
        var reported = new List<ExitCase>();
        var previous = EffectDiagnostics.UnobservedFailureHandler;
        EffectDiagnostics.UnobservedFailureHandler = reported.Add;

        try
        {
            // When
            var first = await Assert.ThrowsAsync<IOException>(async () => await withContext.RunAsync(CancellationToken.None));
            var attached = original.Data[Effect<int, Never, EmptyEnvironment>.SUPPRESSED_DATA_KEY];
            var second = await Assert.ThrowsAsync<IOException>(async () => await source.RunAsync(CancellationToken.None));

            // Then
            Assert.Same(original, first);
            Assert.Same(original, second);
            var context = Assert.Single(Assert.IsType<ImmutableArray<Suppressed>>(attached));
            Assert.Same(releaseFailure, Assert.IsType<Die>(context.Case.Value).Exception);
            Assert.False(original.Data.Contains(Effect<int, Never, EmptyEnvironment>.SUPPRESSED_DATA_KEY));
            Assert.Equal("keep", original.Data["caller"]);
            Assert.Empty(reported);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = previous;
        }
    }

    private sealed class RestrictedDataException(bool readOnly) : Exception("original defect")
    {
        private readonly IDictionary data = new ReadOnlyDictionary<object, object?>(new Dictionary<object, object?>());

        public Exception MetadataFailure { get; init; } = new InvalidOperationException("metadata getter");

        public override IDictionary Data => readOnly ? data : throw MetadataFailure;
    }
}
