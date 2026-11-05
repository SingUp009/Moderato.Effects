using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moderato.Effects;
using Moderato.Effects.Http;
using Moderato.Results;

// Given
var services = new ServiceCollection();
services.AddEffectsHttp();
services.AddScoped<AppEnv>();
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
provider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
using var scope = provider.CreateScope();
var request = scope.ServiceProvider.GetRequiredService<EffectRequest>();
var environment = scope.ServiceProvider.GetRequiredService<AppEnv>();
Effect<int, AppError, AppEnv> effect = Effect<AppEnv>.Access(env => env.Resolve<Counter>().Value);

// When
var result = await request.RunAsync(effect, environment);
var response = Map(result);
Effect<int, AppError, AppEnv> missing = Effect<AppEnv>.Fail<AppError>(new Missing(7));
var failed = await request.RunAsync(missing, environment);
var errorResponse = Map(failed);

// Then
if (response.Result is not Microsoft.AspNetCore.Http.HttpResults.Ok<int> { Value: 42 })
    throw new InvalidOperationException("HTTP-only package consumer lost generated environment or success.");
if (errorResponse.Result is not Microsoft.AspNetCore.Http.HttpResults.NotFound<Missing> { Value.Id: 7 })
    throw new InvalidOperationException("HTTP-only package consumer lost generated error union or mapping.");
Console.WriteLine("PASS: HTTP-only package, generated environment/error union and typed responses");

static Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<int>, Microsoft.AspNetCore.Http.HttpResults.NotFound<Missing>> Map(Result<int, AppError> result)
    => result switch
    {
        Ok<int>(var value) => TypedResults.Ok(value),
        Err<AppError>(var error) => error switch { Missing missing => TypedResults.NotFound(missing) },
    };

[Environment]
public sealed partial record AppEnv(Counter Counter)
{
    public AppEnv() : this(new Counter()) { }
}

public sealed record Counter(int Value = 42);
public sealed record Missing(int Id);

[Error]
public partial union AppError(Missing);
