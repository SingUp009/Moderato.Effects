using System.Security.Cryptography;
using Moderato.Results;

namespace Moderato.Effects.Tracking.Samples.Execution;

public static class Hashing
{
    public static Effect<string, Never, EmptyEnvironment> Compute(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hash = Resource.Make(Effect<EmptyEnvironment>.Sync(SHA256.Create).WithName("SHA256.Create"),
            (algorithm, _) => Effect<EmptyEnvironment>.Sync(() =>
            {
                algorithm.Dispose();
                return Unit.Value;
            }).WithName("SHA256.Dispose"));
        return hash.Use(algorithm => Effect<EmptyEnvironment>.Async<byte[]>(
                token => new(algorithm.ComputeHashAsync(input, token))))
            .Map(Convert.ToHexStringLower).WithName("SHA256.Compute");
    }
}
