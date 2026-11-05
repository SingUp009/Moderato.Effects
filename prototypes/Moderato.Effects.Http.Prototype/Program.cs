using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Moderato.Effects;
using Moderato.Effects.Http.Prototype;
using Moderato.Results;
using HttpResults = Microsoft.AspNetCore.Http.HttpResults;

var verify = args.Contains("--verify", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--verify").ToArray());
builder.Services.AddEffectsHttp();
builder.Services.AddScoped<AppEnv>();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<ObservedFailures>();
if (verify) builder.WebHost.UseUrls("http://127.0.0.1:0");
if (verify) builder.Logging.SetMinimumLevel(LogLevel.Warning);

await using var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    app.Services.GetRequiredService<ObservedFailures>().Errors.Enqueue(error);
    await TypedResults.Problem(statusCode: 500, title: "The request failed.").ExecuteAsync(context);
}));

app.MapOpenApi();
app.MapPost("/orders", CreateOrder).ProducesProblem(500);

if (verify)
{
    PrototypeChecks.MapEndpoints(app);
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    await PrototypeChecks.Run(app, address);
    await app.StopAsync();
}
else
{
    await app.RunAsync();
}

static async Task<HttpResults.Results<HttpResults.Created<Order>, HttpResults.BadRequest<ValidationError>, HttpResults.Conflict<DuplicateOrder>>>
CreateOrder(CreateOrderRequest command, AppEnv env, EffectRequest request)
{
    var result = await request.RunAsync(Orders<AppEnv>.Create(command), env);
    return result switch
    {
        Ok<Order>(var order) => TypedResults.Created($"/orders/{order.Id}", order),
        Err<CreateOrderError>(var error) => error switch
        {
            ValidationError e => TypedResults.BadRequest(e),
            DuplicateOrder e => TypedResults.Conflict(e),
        },
    };
}

public sealed record Order(int Id, string Sku, int Quantity);
public sealed record CreateOrderRequest(string Sku, int Quantity);
public sealed record ValidationError(string Message);
public sealed record DuplicateOrder(string Sku);

[Error]
public partial union CreateOrderError(ValidationError, DuplicateOrder);

[Environment]
public sealed partial record AppEnv(OrderStore Orders);

public sealed class OrderStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Order> orders = new(StringComparer.Ordinal);
    private int nextId;

    public Result<Order, CreateOrderError> Create(CreateOrderRequest command)
    {
        if (string.IsNullOrWhiteSpace(command.Sku) || command.Quantity <= 0)
            return new Err<CreateOrderError>(new ValidationError("SKU and a positive quantity are required."));
        var order = new Order(Interlocked.Increment(ref nextId), command.Sku, command.Quantity);
        return orders.TryAdd(command.Sku, order)
            ? new Ok<Order>(order)
            : new Err<CreateOrderError>(new DuplicateOrder(command.Sku));
    }
}

public static class Orders<R> where R : IHas<OrderStore>
{
    public static Effect<Order, CreateOrderError, R> Create(CreateOrderRequest command)
    {
        Effect<Result<Order, CreateOrderError>, CreateOrderError, R> read =
            Effect<R>.Access(env => env.Resolve<OrderStore>().Create(command));
        return read.AndThen(result => Effect<R>.FromResult(result));
    }
}

public sealed class ObservedFailures
{
    public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
}
