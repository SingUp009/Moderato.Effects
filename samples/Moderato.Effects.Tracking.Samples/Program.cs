using System.Net;
using Moderato.Effects;
using Moderato.Effects.Http;
using Moderato.Effects.Tracking.Samples.Components;
using Moderato.Effects.Tracking.Samples.Execution;
using Moderato.Results;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const long MAX_REQUEST_BYTES = 1024 * 1024;
const int DEFAULT_PORT = 5217;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, builder.Configuration.GetValue("Tracking:Port", DEFAULT_PORT));
    options.Limits.MaxRequestBodySize = MAX_REQUEST_BYTES;
});
builder.Services.AddHostFiltering(options => options.AllowedHosts = ["localhost", "127.0.0.1"]);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<TrackingSession>();
builder.Services.AddEffectsHttp();
builder.Services.AddProblemDetails();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource("Moderato.Effects").AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter("Moderato.Effects", "System.Runtime").AddOtlpExporter());
builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter());

var app = builder.Build();
var session = app.Services.GetRequiredService<TrackingSession>();
app.Lifetime.ApplicationStopped.Register(session.Dispose);
app.UseHostFiltering();
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin;
    if (origin.Count != 0 && (origin.Count != 1 || !string.Equals(origin[0], $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = error => error is EffectHttpException { InnerException: BadHttpRequestException rejected }
        ? rejected.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapPost("/sha256", async (Stream body, EffectRequest request) =>
{
    var result = await request.RunAsync(Hashing.Compute(body), EmptyEnvironment.Value);
    return result switch
    {
        Ok<string>(var digest) => TypedResults.Ok(new Digest(digest)),
        _ => throw new InvalidOperationException("A Never error cannot occur."),
    };
});
app.Run();

internal sealed record Digest(string Sha256);
public partial class Program;
