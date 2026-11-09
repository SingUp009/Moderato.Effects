using System.Security.Cryptography;
using Moderato.Effects;
using Moderato.Effects.Http;
using Moderato.Results;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Resource = Moderato.Effects.Resource;

const long MAX_REQUEST_BYTES = 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MAX_REQUEST_BYTES);
builder.Services.AddEffectsHttp();
builder.Services.AddProblemDetails();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("Moderato.Effects")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter("Moderato.Effects", "System.Runtime")
        .AddOtlpExporter());
builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter());

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = error => error is EffectHttpException { InnerException: BadHttpRequestException rejected }
        ? rejected.StatusCode : StatusCodes.Status500InternalServerError,
});
app.MapPost("/sha256", async (Stream body, EffectRequest request) =>
{
    var hash = Resource.Make(Effect<EmptyEnvironment>.Sync(SHA256.Create),
        (algorithm, _) => Effect<EmptyEnvironment>.Sync(() => { algorithm.Dispose(); return Unit.Value; }));
    var effect = hash.Use(algorithm => Effect<EmptyEnvironment>.Async<byte[]>(
        token => new(algorithm.ComputeHashAsync(body, token))));
    var result = await request.RunAsync(effect, EmptyEnvironment.Value);
    return result switch
    {
        Ok<byte[]>(var bytes) => TypedResults.Ok(new Digest(Convert.ToHexStringLower(bytes))),
        _ => throw new InvalidOperationException("A Never error cannot occur."),
    };
});
app.Run();

internal sealed record Digest(string Sha256);

public partial class Program;
