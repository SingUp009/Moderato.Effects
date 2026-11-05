using Microsoft.Extensions.DependencyInjection.Extensions;
using Moderato.Results;

namespace Moderato.Effects.Http.Prototype;

public static class HttpRegistration
{
    public static IServiceCollection AddEffectsHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<HttpDiagnostics>();
        services.TryAddScoped(provider => new EffectRequest(
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.RequestAborted
                ?? throw new InvalidOperationException("EffectRequest requires an active HTTP request."),
            provider.GetRequiredService<HttpDiagnostics>()));
        return services;
    }
}

public sealed class EffectHttpException(Die failure)
    : Exception("The HTTP effect ended with a defect.", failure.Exception)
{
    public Die Failure { get; } = failure;
}

public sealed class EffectHttpInterruptedException(Interrupted interruption)
    : Exception("The HTTP effect was interrupted without a request cancellation.")
{
    public Interrupted Interruption { get; } = interruption;
}

public sealed class EffectRequest : IDisposable
{
    private readonly CancellationToken requestAborted;
    private readonly object gate = new();
    private readonly EffectRunOptions options;
    private HttpDiagnostics? diagnostics;

    internal EffectRequest(CancellationToken requestAborted, HttpDiagnostics diagnostics)
    {
        this.requestAborted = requestAborted;
        this.diagnostics = diagnostics;
        options = new EffectRunOptions { UnobservedFailureHandler = Report };
    }

    public ValueTask<Result<T, E>> RunAsync<T, E, R>(Effect<T, E, R> effect, R environment)
    {
        lock (gate) ObjectDisposedException.ThrowIf(diagnostics is null, this);
        var execution = effect.RunExitAsync(environment, requestAborted, options);
        return execution.IsCompletedSuccessfully
            ? new(Convert(execution.Result))
            : Await(execution);

        async ValueTask<Result<T, E>> Await(ValueTask<Exit<T, E>> pending)
            => Convert(await pending.ConfigureAwait(false));
    }

    private Result<T, E> Convert<T, E>(Exit<T, E> exit)
    {
        var canceled = requestAborted.IsCancellationRequested;
        if (exit.TryGetValue(out Ok<T> ok)) return new(ok);
        if (exit.TryGetValue(out Err<E> error)) return new(error);
        if (exit.TryGetValue(out Die? failure)) throw new EffectHttpException(failure);
        if (exit.TryGetValue(out Interrupted? interruption))
        {
            if (canceled) throw new OperationCanceledException(requestAborted);
            throw new EffectHttpInterruptedException(interruption);
        }
        throw new InvalidOperationException("Invalid exit.");
    }

    private void Report(ExitCase failure)
    {
        lock (gate)
        {
            if (diagnostics is { } current)
            {
                current.Report(failure);
                return;
            }
        }
        HttpDiagnostics.Fallback(failure);
    }

    public void Dispose()
    {
        lock (gate) diagnostics = null;
    }
}
