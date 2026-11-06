using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Moderato.Effects.Http.Tests;

public class RequestTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_TypedResult_PreservesValue(bool asynchronous, bool failure)
    {
        // Given
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        Effect<int, string, object> effect = failure ? Effect<object>.Fail("invalid") : Effect<object>.Succeed(42);
        if (asynchronous)
        {
            var result = failure ? new Result<int, string>(new Err<string>("invalid")) : new(new Ok<int>(42));
            effect = Effect<object>.FromResultAsync(async _ => { await Task.Yield(); return result; });
        }

        // When
        var actual = await request.RunAsync(effect, new object());

        // Then
        if (failure) Assert.Equal("invalid", Assert.IsType<Err<string>>(actual.Value).Error);
        else Assert.Equal(42, Assert.IsType<Ok<int>>(actual.Value).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_Defect_FaultsReturnedValueTask(bool asynchronous)
    {
        // Given
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var original = new IOException("defect");
        var effect = asynchronous
            ? Effect<object>.Async<int>(async _ => { await Task.Yield(); throw original; })
            : Effect<object>.Sync<int>(() => throw original);

        // When
        var pending = request.RunAsync(effect, new object());

        // Then
        var error = await Assert.ThrowsAsync<EffectHttpException>(() => pending.AsTask());
        Assert.Same(original, error.InnerException);
        Assert.Same(original, Assert.IsType<Die>(error.Exit.Value).Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CancellationDuringCleanup_PreservesDefect(bool io)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        Exception original = io ? new IOException("cleanup") : new OperationCanceledException("cleanup");
        var resource = Resource.Make(Effect<object>.Succeed(Unit.Value), (_, _) => Effect<object>.Sync<Unit>(() =>
        {
            cancellation.Cancel();
            throw original;
        }));

        // When
        var pending = request.RunAsync(resource.Use(_ => Effect<object>.Succeed(1)), new object());

        // Then
        var error = await Assert.ThrowsAsync<EffectHttpException>(() => pending.AsTask());
        Assert.Same(original, error.InnerException);
        Assert.IsType<Die>(error.Exit.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CancellationAfterBody_PreservesSettledResult(bool failure)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        Effect<Unit, string, object> acquire = Effect<object>.Succeed(Unit.Value);
        var resource = Resource.Make(acquire, (_, _) => Effect<object>.Sync(() =>
        {
            cancellation.Cancel();
            return Unit.Value;
        }));
        Effect<int, string, object> body = failure ? Effect<object>.Fail("error") : Effect<object>.Succeed(7);

        // When
        var result = await request.RunAsync(resource.Use(_ => body), new object());

        // Then
        Assert.True(cancellation.IsCancellationRequested);
        if (failure) Assert.Equal("error", Assert.IsType<Err<string>>(result.Value).Error);
        else Assert.Equal(7, Assert.IsType<Ok<int>>(result.Value).Value);
    }

    [Fact]
    public async Task RunAsync_TokenReplacedAfterResolution_UsesReplacement()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        services.GetRequiredService<IHttpContextAccessor>().HttpContext!.RequestAborted = cancellation.Token;
        cancellation.Cancel();

        // When
        var pending = request.RunAsync(Effect<object>.Succeed(1), new object());

        // Then
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_TokenReplacedDuringExecution_ObservesCapturedToken()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var services = ServicesWithToken(cancellation.Token);
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext!;
        var effect = Effect<object>.Async<int>(async token =>
        {
            context.RequestAborted = CancellationToken.None;
            cancellation.Cancel();
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return 1;
        });

        // When
        var pending = request.RunAsync(effect, new object());

        // Then
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void RunAsync_NoActiveRequest_RejectsExecution()
    {
        // Given
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

        // When
        var error = Record.Exception(() => request.RunAsync(Effect<object>.Succeed(1), new object()));

        // Then
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void RunAsync_DisposedRequest_RejectsExecution()
    {
        // Given
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        request.Dispose();

        // When
        var error = Record.Exception(() => request.RunAsync(Effect<object>.Succeed(1), new object()));

        // Then
        Assert.IsType<ObjectDisposedException>(error);
    }

    [Fact]
    public async Task RunAsync_MultipleCleanupFailures_PreservesOrderedContextAndOmissionCount()
    {
        // Given
        using var services = Services();
        using var scope = services.CreateScope();
        var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
        var original = new IOException("body");
        var cleanup = Enumerable.Range(0, 103).Select(i => new IOException($"release {i}")).ToArray();
        var effect = Scope<Never, object>.Gen<int>(async execution =>
        {
            foreach (var error in cleanup)
                execution.Defer(_ => Effect<object>.Sync<Unit>(() => throw error));
            await execution.Run(Effect<object>.Sync<int>(() => throw original));
            return 0;
        });

        // When
        var pending = request.RunAsync(effect, new object());

        // Then
        var error = await Assert.ThrowsAsync<EffectHttpException>(() => pending.AsTask());
        var die = Assert.IsType<Die>(error.Exit.Value);
        Assert.Same(original, error.InnerException);
        Assert.Equal(100, die.Suppressed.Count);
        Assert.Equal(3, die.OmittedSuppressed);
        for (var i = 0; i < die.Suppressed.Count; i++)
        {
            Assert.Equal(Relation.Sequential, die.Suppressed[i].Relation);
            Assert.Same(cleanup[102 - i], Assert.IsType<Die>(die.Suppressed[i].Case.Value).Exception);
        }
    }

    [Fact]
    public void AddEffectsHttp_NullServices_RejectsRegistration()
    {
        // Given
        IServiceCollection services = null!;

        // When
        var error = Record.Exception(() => services.AddEffectsHttp());

        // Then
        Assert.IsType<ArgumentNullException>(error);
    }

    internal static ServiceProvider Services() => ServicesWithToken(CancellationToken.None);

    internal static ServiceProvider ServicesWithToken(CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddEffectsHttp();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { RequestAborted = token };
        return provider;
    }
}
