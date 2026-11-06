using Moderato.Effects.Http;
using Moderato.Results;
using HttpResults = Microsoft.AspNetCore.Http.HttpResults;

namespace Moderato.Effects.Http.Samples;

public static class OrderEndpoints
{
    public static async Task<HttpResults.Results<HttpResults.Created<Order>, HttpResults.BadRequest<ValidationError>, HttpResults.Conflict<DuplicateOrder>>>
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
}
