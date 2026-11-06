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

await using var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(context =>
    TypedResults.Problem(statusCode: 500, title: "The request failed.").ExecuteAsync(context)));
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapPost("/orders", OrderEndpoints.CreateOrder).ProducesProblem(500);
AuthorizedOrderExample.Map(app);

await app.RunAsync();
