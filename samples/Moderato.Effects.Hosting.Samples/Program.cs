using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moderato.Effects;
using Moderato.Effects.Hosting;
using Moderato.Results;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped<WorkerEnvironment>();
builder.Services.AddEffectsHosting(options => options.Diagnostics = services =>
    new LoggerDiagnostics(services.GetRequiredService<ILoggerFactory>()));

Effect<Unit, string, WorkerEnvironment> job = Effect<WorkerEnvironment>.Access(environment =>
{
    environment.Logger.LogInformation("Processing one job");
    return Unit.Value;
});
builder.Services.AddEffectHostedService(job,
    services => services.GetRequiredService<WorkerEnvironment>(),
    static (error, environment, _) =>
    {
        environment.Logger.LogWarning("Job failed: {Error}", error);
        return ValueTask.CompletedTask;
    });

using var host = builder.Build();
await host.StartAsync();
// A finite sample stops explicitly; production workers can use host.RunAsync().
await Task.WhenAll(host.Services.GetServices<IHostedService>().Cast<BackgroundService>().Select(service => service.ExecuteTask!));
await host.StopAsync();

public sealed record WorkerEnvironment(ILogger<WorkerEnvironment> Logger);
