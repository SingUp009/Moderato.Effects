using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Moderato.Results;
using HttpResults = Microsoft.AspNetCore.Http.HttpResults;

namespace Moderato.Effects.Http.Samples;

internal static class AuthorizedOrderExample
{
    internal static void AddServices(IServiceCollection services)
    {
        services.AddAuthentication(SampleAuthentication.SCHEME_NAME)
            .AddScheme<AuthenticationSchemeOptions, SampleAuthentication>(SampleAuthentication.SCHEME_NAME, null);
        services.AddAuthorization();
        services.AddSingleton<ProtectedOrderStore>();
        services.AddScoped<AuthorizedEnv>();
    }

    internal static void DescribeAuthentication(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SampleAuthentication.SCHEME_NAME] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = SampleAuthentication.HEADER_NAME,
            In = ParameterLocation.Header,
            Description = "Sample only: alice or bob. This header does not authenticate real identities.",
        };
    }

    internal static void Map(WebApplication app)
    {
        var orders = app.MapGroup("/authorized-orders").RequireAuthorization()
            .AddOpenApiOperationTransformer((operation, context, _) =>
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses["401"] = new OpenApiResponse { Description = "Unauthorized" };
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SampleAuthentication.SCHEME_NAME, context.Document)] = [],
                });
                return Task.CompletedTask;
            });
        orders.MapGet("/{id:int}", FindOrder)
            .Produces<AccessDenied>(StatusCodes.Status403Forbidden)
            .ProducesProblem(500);
    }

    private static async Task<HttpResults.Results<HttpResults.Ok<Order>, HttpResults.NotFound<OrderNotFound>, HttpResults.JsonHttpResult<AccessDenied>>>
    FindOrder(int id, ClaimsPrincipal user, AuthorizedEnv env, EffectRequest request)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The sample authentication scheme must supply a user identifier.");
        var result = await request.RunAsync(AuthorizedOrders<AuthorizedEnv>.FindFor(userId, id), env);
        return result switch
        {
            Ok<Order>(var order) => TypedResults.Ok(order),
            Err<FindOrderError>(var error) => error switch
            {
                OrderNotFound missing => TypedResults.NotFound(missing),
                AccessDenied denied => TypedResults.Json(denied, statusCode: StatusCodes.Status403Forbidden),
            },
        };
    }
}

// The header deliberately trusts caller input for local demonstrations; replace it with real authentication before deployment.
internal sealed class SampleAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    internal const string SCHEME_NAME = "SampleUser";
    internal const string HEADER_NAME = "X-Sample-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HEADER_NAME, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || values[0] is not ("alice" or "bob"))
            return Task.FromResult(AuthenticateResult.Fail("Unknown sample user."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, values[0]!)], SCHEME_NAME);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SCHEME_NAME);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed record OrderNotFound(int Id);
public sealed record AccessDenied(string Reason);

[Error]
public partial union FindOrderError(OrderNotFound, AccessDenied);

[Environment]
public sealed partial record AuthorizedEnv(ProtectedOrderStore Orders);

public sealed class ProtectedOrderStore
{
    public Result<Order, FindOrderError> FindFor(string userId, int id)
    {
        if (id != 1) return new Err<FindOrderError>(new OrderNotFound(id));
        if (userId != "alice") return new Err<FindOrderError>(new AccessDenied("Access denied."));
        return new Ok<Order>(new Order(1, "protected-sample", 2));
    }
}

public static class AuthorizedOrders<R> where R : IHas<ProtectedOrderStore>
{
    public static Effect<Order, FindOrderError, R> FindFor(string userId, int id)
    {
        Effect<Result<Order, FindOrderError>, FindOrderError, R> read =
            Effect<R>.Access(env => env.Resolve<ProtectedOrderStore>().FindFor(userId, id));
        return read.AndThen(result => Effect<R>.FromResult(result));
    }
}
