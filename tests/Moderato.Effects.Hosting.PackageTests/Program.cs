using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moderato.Effects;
using Moderato.Effects.Hosting;
using Moderato.Results;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
for (var bodyFails = 0; bodyFails < 2; bodyFails++)
{
    for (var disposalFails = 0; disposalFails < 2; disposalFails++)
        await CheckScopeFailures(bodyFails != 0, disposalFails != 0, deadline.Token);
}
await CheckTypedErrorHandler(deadline.Token);
await CheckDiagnosticOwnership(deadline.Token);
Console.WriteLine("Hosting package works: scoped outcomes, typed errors, generators and diagnostic ownership");

static async Task CheckScopeFailures(bool bodyFails, bool disposalFails, CancellationToken token)
{
    // Given
    var body = new IOException("body");
    var cleanup = new IOException("scope");
    var environment = new WorkerEnvironment(disposalFails ? cleanup : null);
    var builder = Builder();
    builder.Services.AddScoped(_ => environment);
    Effect<Unit, WorkerError, WorkerEnvironment> effect = Effect<WorkerEnvironment>.Sync(() => bodyFails ? throw body : Unit.Value);
    builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<WorkerEnvironment>(),
        static (_, _, _) => throw new InvalidOperationException("Unexpected Err"));
    using var host = builder.Build();

    // When
    await host.StartAsync(token);
    var execution = host.Services.GetServices<IHostedService>().Cast<BackgroundService>().Single().ExecuteTask!;
    EffectHostedServiceException? failure = null;
    try { await execution.WaitAsync(token); }
    catch (EffectHostedServiceException exception) { failure = exception; }

    // Then
    Check(environment.Disposed, "Execution scope was not disposed.");
    if (!bodyFails && !disposalFails)
    {
        Check(failure is null, "A successful effect and disposal must complete normally.");
        return;
    }
    var die = failure?.Exit.Value as Die ?? throw new InvalidOperationException("Expected a preserved Die.");
    Check(ReferenceEquals(die.Exception, bodyFails ? body : cleanup), "Primary defect changed.");
    Check(die.OmittedSuppressed == 0, "Unexpected omission.");
    Check(die.Suppressed.Count == (bodyFails && disposalFails ? 1 : 0), "Incorrect cleanup context.");
    if (bodyFails && disposalFails)
    {
        Check(die.Suppressed[0] is { Relation: Relation.Sequential } &&
            die.Suppressed[0].Case.Value is Die suppressed && ReferenceEquals(suppressed.Exception, cleanup), "Cleanup defect was lost.");
    }
}

static async Task CheckTypedErrorHandler(CancellationToken token)
{
    // Given
    var builder = Builder();
    var environment = new WorkerEnvironment(null);
    builder.Services.AddScoped(_ => environment);
    var defect = new IOException("handler");
    Effect<Unit, Rejected, WorkerEnvironment> source = Effect<WorkerEnvironment>.Fail(new Rejected("job"));
    var effect = source.Widen<WorkerError>();
    builder.Services.AddEffectHostedService(effect, services => services.GetRequiredService<WorkerEnvironment>(), async (error, services, _) =>
    {
        Check(error.Value is Rejected { Job: "job" }, "Transitive error generation failed.");
        Check(ReferenceEquals(environment, services) && !services.Disposed, "Handler lost its live environment.");
        await Task.Yield();
        throw defect;
    });
    using var host = builder.Build();

    // When
    await host.StartAsync(token);
    var execution = host.Services.GetServices<IHostedService>().Cast<BackgroundService>().Single().ExecuteTask!;
    EffectHostedServiceException? failure = null;
    try { await execution.WaitAsync(token); }
    catch (EffectHostedServiceException exception) { failure = exception; }

    // Then
    Check(environment.Disposed && ReferenceEquals(failure?.InnerException, defect), "Handler defect or scope lifetime changed.");
    var die = failure?.Exit.Value as Die ?? throw new InvalidOperationException("Expected handler Die.");
    Check(die.Suppressed.Count == 1 && die.Suppressed[0].Relation == Relation.Sequential &&
        die.Suppressed[0].Case.Value is Failed { Error: Rejected }, "Original typed Err was lost.");
}

static async Task CheckDiagnosticOwnership(CancellationToken token)
{
    // Given
    var previous = EffectDiagnostics.UnobservedFailureHandler;
    var diagnostics = new RecordingDiagnostics();
    var builder = Builder();
    builder.Services.AddEffectsHosting(options => options.Diagnostics = _ => diagnostics);
    Effect<Unit, WorkerError, WorkerEnvironment> effect = Scope<WorkerError, WorkerEnvironment>.Gen(async scope =>
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await scope.Fork(Effect<WorkerEnvironment>.Fail(new Rejected("child")).TapErr(_ => ended.TrySetResult()));
        await ended.Task;
        return Unit.Value;
    });
    builder.Services.AddEffectHostedService(effect, _ => new WorkerEnvironment(null), static (_, _, _) => ValueTask.CompletedTask);
    builder.Services.AddEffectHostedService(effect, _ => new WorkerEnvironment(null), static (_, _, _) => ValueTask.CompletedTask);
    using var host = builder.Build();

    // When
    await host.StartAsync(token);
    await Task.WhenAll(host.Services.GetServices<IHostedService>().Cast<BackgroundService>().Select(service => service.ExecuteTask!)).WaitAsync(token);
    host.Dispose();

    // Then
    Check(diagnostics.Failures.Count == 2 && diagnostics.Failures.All(failure => failure.Value is Failed), "Duplicate registrations or diagnostics were lost.");
    Check(diagnostics.Disposals == 1, "DI must dispose its diagnostic implementation exactly once.");
    Check(ReferenceEquals(previous, EffectDiagnostics.UnobservedFailureHandler), "Hosting changed the global diagnostic handler.");
}

static HostApplicationBuilder Builder()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
    builder.Logging.ClearProviders();
    builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
    return builder;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public record Rejected(string Job);

[Error]
public partial union WorkerError(Rejected);

public sealed class WorkerEnvironment(Exception? disposalFailure) : IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return disposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure);
    }
}

public sealed class RecordingDiagnostics : IEffectDiagnostics, IDisposable
{
    public ConcurrentQueue<ExitCase> Failures { get; } = new();
    public int Disposals { get; private set; }
    public void Report(ExitCase failure) => Failures.Enqueue(failure);
    public void Dispose() => Disposals++;
}
