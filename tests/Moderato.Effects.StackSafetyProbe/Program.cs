using Moderato.Effects;
using Moderato.Results;

if (args.Length != 3 || !int.TryParse(args[1], out var depth) || depth < 0
    || args[2] is not ("sync" or "pending")
    || args[0] is not ("map" or "bind" or "recursive-bind" or "provide" or "query" or "recover" or "conversions"
        or "resource" or "resource-map" or "resource-query" or "recursive-resource" or "resource-conversions" or "scope"))
{
    Console.Error.WriteLine("Usage: <map|bind|recursive-bind|provide|query|recover|conversions|resource|resource-map|resource-query|recursive-resource|resource-conversions|scope> <nonnegative-depth> <sync|pending>");
    return 2;
}

// Given
var pending = args[2] == "pending";
var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releases = new Dictionary<int, List<int>>();
var entries = 0;
var source = pending
    ? Effect<int>.AccessAsync<int>(async (environment, _) =>
    {
        entries++;
        await gate.Task.ConfigureAwait(false);
        return environment;
    })
    : Effect<int>.Access(environment =>
    {
        entries++;
        return environment;
    });
Effect<int, Never, int> effect = source;
switch (args[0])
{
    case "map":
        for (var i = 0; i < depth; i++)
            effect = effect.Map(static value => new Box(value + 1)).Map(static box => box.Value);
        break;
    case "bind":
        for (var i = 0; i < depth; i++)
            effect = effect.AndThen(static value => Effect<int>.Succeed(value + 1));
        break;
    case "recursive-bind":
        effect = source.AndThen(value => Continue(value, depth));
        break;
    case "provide":
        for (var i = 0; i < depth; i++)
            effect = effect.Provide<int>(static environment => environment + 1);
        break;
    case "query":
        for (var i = 0; i < depth; i++)
            effect = from firstValue in effect from secondValue in Effect<int>.Succeed(new Box(firstValue + 1)) select secondValue.Value;
        break;
    case "recover":
        Effect<int, int, int> recoverSource = source;
        effect = recoverSource.AndThen(value => Recover(value, depth)).UnwrapOr(-1);
        break;
    case "conversions":
        for (var i = 0; i < depth; i++)
        {
            Effect<int, string, int> widened = effect;
            effect = widened.MapErr<Never>(static _ => throw new InvalidOperationException("An error cannot inhabit Never."))
                .Map(static value => value + 1);
        }
        break;
    case "resource":
    case "resource-map":
    case "resource-query":
    case "recursive-resource":
    case "resource-conversions":
    case "scope":
        var resource = Make(source.Map(static value => new Lease(value, value, 0)));
        if (args[0] == "recursive-resource")
        {
            resource = Recurse(source.Map(static value => new Lease(value, value, 0)), depth);
        }
        else
        {
            for (var i = 1; i <= depth; i++)
            {
                var index = i;
                switch (args[0])
                {
                    case "resource-map":
                        resource = resource.Map(static value => value with { Value = value.Value + 1 });
                        break;
                    case "resource-conversions":
                        Resource<Lease, string, int> widened = resource;
                        resource = widened.MapErr<Never>(static _ => throw new InvalidOperationException("An error cannot inhabit Never."))
                            .Map(static value => value with { Value = value.Value + 1 });
                        break;
                    case "resource-query":
                        resource = from firstValue in resource
                                   from secondValue in Make(Effect<int>.Succeed(new Lease(firstValue.Origin, firstValue.Value + 1, index)))
                                   select secondValue;
                        break;
                    default:
                        resource = resource.AndThen(value => Make(Effect<int>.Succeed(new Lease(value.Origin, value.Value + 1, index))));
                        break;
                }
            }
        }
        effect = args[0] == "scope"
            ? Scope<Never, int>.Gen(async scope => (await scope.Acquire(resource)).Value)
            : resource.Use(static value => Effect<int>.Succeed(value.Value));
        break;
}

// When
var first = effect.RunAsync(3, CancellationToken.None);
var second = effect.RunAsync(7, CancellationToken.None);
var firstCompleted = first.IsCompletedSuccessfully;
var secondCompleted = second.IsCompletedSuccessfully;
gate.SetResult();
var firstResult = await first.AsTask().WaitAsync(TimeSpan.FromSeconds(30));
var secondResult = await second.AsTask().WaitAsync(TimeSpan.FromSeconds(30));
var repeated = await effect.RunAsync(11, CancellationToken.None);

// Then
if (firstCompleted == pending || secondCompleted == pending)
    throw new InvalidOperationException("Synchronous completion changed.");
if (firstResult != Result.Ok(3 + depth) || secondResult != Result.Ok(7 + depth)
    || repeated != Result.Ok(11 + depth) || entries != 3)
{
    throw new InvalidOperationException("A run lost its value, environment, or lazy source evaluation.");
}
if (args[0].Contains("resource", StringComparison.Ordinal) || args[0] == "scope")
{
    var count = args[0] is "resource-map" or "resource-conversions" ? 1 : depth + 1;
    var expected = Enumerable.Range(0, count).Reverse().ToArray();
    if (releases.Count != 3 || new[] { 3, 7, 11 }.Any(origin => !releases[origin].SequenceEqual(expected)))
        throw new InvalidOperationException("Resources were not released exactly once in reverse acquisition order.");
}
Console.WriteLine($"PASS {args[0]} depth={depth} {args[2]}: two independent runs and one rerun");
return 0;

Resource<Lease, Never, int> Make(Effect<Lease, Never, int> acquire)
    => Resource.Make(acquire, (value, _) => Effect<int>.Sync(() =>
    {
        lock (releases)
        {
            if (!releases.TryGetValue(value.Origin, out var log)) releases.Add(value.Origin, log = []);
            log.Add(value.Index);
        }
        return Unit.Value;
    }));

Resource<Lease, Never, int> Recurse(Effect<Lease, Never, int> acquire, int remaining)
    => remaining == 0 ? Make(acquire) : Make(acquire).AndThen(value =>
        Recurse(Effect<int>.Succeed(new Lease(value.Origin, value.Value + 1, value.Index + 1)), remaining - 1));

static Effect<int, Never, int> Continue(int value, int remaining)
    => remaining == 0 ? Effect<int>.Succeed(value)
        : Effect<int>.Succeed(value + 1).AndThen(next => Continue(next, remaining - 1));

static Effect<int, int, int> Recover(int value, int remaining)
    => ((Effect<int, int, int>)Effect<int>.Fail(remaining))
        .OrElse<int>(count => count == 0 ? Effect<int>.Succeed(value) : Recover(value + 1, count - 1));

internal readonly record struct Box(int Value);

internal readonly record struct Lease(int Origin, int Value, int Index);
