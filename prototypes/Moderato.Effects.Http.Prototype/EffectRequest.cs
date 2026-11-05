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
            provider.GetRequiredService<IHttpContextAccessor>(),
            provider.GetRequiredService<HttpDiagnostics>()));
        return services;
    }
}

public sealed class EffectHttpException : Exception
{
    internal EffectHttpException(ExitCase exit)
        : base(exit.Value is Die ? "The HTTP effect ended with a defect." : "The HTTP effect was interrupted without a request cancellation.",
            (exit.Value as Die)?.Exception) => Exit = exit;

    public ExitCase Exit { get; }
}

public sealed class EffectRequest : IDisposable
{
    private readonly IHttpContextAccessor accessor;
    private readonly object gate = new();
    private readonly EffectRunOptions options;
    private HttpDiagnostics? diagnostics;

    internal EffectRequest(IHttpContextAccessor accessor, HttpDiagnostics diagnostics)
    {
        this.accessor = accessor;
        this.diagnostics = diagnostics;
        options = new EffectRunOptions { UnobservedFailureHandler = Report };
    }

    public ValueTask<Result<T, E>> RunAsync<T, E, R>(Effect<T, E, R> effect, R environment)
    {
        lock (gate) ObjectDisposedException.ThrowIf(diagnostics is null, this);
        var requestAborted = accessor.HttpContext?.RequestAborted
            ?? throw new InvalidOperationException("EffectRequest requires an active HTTP request.");
        var execution = effect.RunExitAsync(environment, requestAborted, options);
        return Await(execution, requestAborted);

        async ValueTask<Result<T, E>> Await(ValueTask<Exit<T, E>> pending, CancellationToken runToken)
            => Convert(await pending.ConfigureAwait(false), runToken);
    }

    private static Result<T, E> Convert<T, E>(Exit<T, E> exit, CancellationToken requestAborted)
    {
        var canceled = requestAborted.IsCancellationRequested;
        if (exit.TryGetValue(out Ok<T> ok)) return new(ok);
        if (exit.TryGetValue(out Err<E> error)) return new(error);
        if (exit.TryGetValue(out Die? failure)) throw new EffectHttpException(failure);
        if (exit.TryGetValue(out Interrupted? interruption))
        {
            if (canceled) throw new OperationCanceledException(requestAborted);
            throw new EffectHttpException(interruption);
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
