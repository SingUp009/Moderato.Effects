# Moderato.Effects.Http

ASP.NET Core integration for Moderato.Effects. Register `AddEffectsHttp()`, inject a request-scoped `EffectRequest`, and map the returned typed result with standard ASP.NET Core `TypedResults`.

Uses standard routing, dependency injection, logging, authorization and OpenAPI metadata. The first preview supports ordinary values; streaming lifetime management is deferred.

See the [user guide](https://github.com/SingUp009/Moderato.Effects/wiki).
