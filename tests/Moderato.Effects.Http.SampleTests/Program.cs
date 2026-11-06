using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Moderato.Effects.Http;
using Moderato.Effects.Http.Samples;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEffectsHttp();
builder.Services.AddScoped<AppEnv>();
builder.Services.AddSingleton<OrderStore>();
AuthorizedOrderExample.AddServices(builder.Services);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    AuthorizedOrderExample.DescribeAuthentication(document);
    return Task.CompletedTask;
}));
builder.Services.AddSingleton<ObservedFailures>();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

await using var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (EffectHttpException error)
    {
        app.Services.GetRequiredService<ObservedFailures>().AfterResponseStarted.TrySetResult(error);
        throw;
    }
});
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    app.Services.GetRequiredService<ObservedFailures>().Errors.Enqueue(error);
    await TypedResults.Problem(statusCode: 500, title: "The request failed.").ExecuteAsync(context);
}));
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapPost("/orders", OrderEndpoints.CreateOrder).ProducesProblem(500);
AuthorizedOrderExample.Map(app);

HttpChecks.MapEndpoints(app);
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
await HttpChecks.Run(app, address);
await app.StopAsync();
