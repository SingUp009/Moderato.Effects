using Moderato.Effects;
using Moderato.Results;

if (args.Length != 3 || !int.TryParse(args[1], out var depth) || depth < 0
    || args[2] is not ("sync" or "pending")
    || args[0] is not ("map" or "bind" or "recursive-bind" or "provide" or "resource"))
{
    Console.Error.WriteLine("Usage: <map|bind|recursive-bind|provide|resource> <nonnegative-depth> <sync|pending>");
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
    case "resource":
        var resource = Make(source.Map(static value => new Lease(value, value, 0)));
        for (var i = 1; i <= depth; i++)
        {
            var index = i;
            resource = resource.AndThen(value => Make(Effect<int>.Succeed(new Lease(value.Origin, value.Value + 1, index))));
        }
        effect = resource.Use(static value => Effect<int>.Succeed(value.Value));
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
if (args[0] == "resource")
{
    var expected = Enumerable.Range(0, depth + 1).Reverse().ToArray();
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

static Effect<int, Never, int> Continue(int value, int remaining)
    => remaining == 0 ? Effect<int>.Succeed(value)
        : Effect<int>.Succeed(value + 1).AndThen(next => Continue(next, remaining - 1));

internal readonly record struct Box(int Value);

internal readonly record struct Lease(int Origin, int Value, int Index);
