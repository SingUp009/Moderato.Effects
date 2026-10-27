var arena = new Arena(3);
var constants = new ConstantPool<int, string, int>(arena, 2);
var maps = new MapPool<int, int, string, int>(arena, 8);
var texts = new MapPool<int, string, string, int>(arena, 1);
Func<int, int> increment = static x => x + 1;
Func<int, string> label = static _ => "done";
Computation<int, string, int>[] mixed = new Computation<int, string, int>[2];
for (int i = 0; i < 2048; i++) Exercise();
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 4096; i++) Exercise();
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated == 0, "Bounded construction or execution allocated after preparation.");
Check(arena.TryReset() == Status.Ok, "Reset failed.");
Check(constants.TryAdd(Result<int, string>.Ok(42), out var old) == Status.Ok, "Setup failed.");
Check(arena.TryReset() == Status.Ok, "Reset failed.");
Check(constants.TryAdd(Result<int, string>.Ok(99), out var newer) == Status.Ok, "Reuse failed.");
Check(old.Run(0).Status == Status.Invalid, "Old handle aliased new slot.");
Check(newer.Run(0).Value == 99, "New handle failed.");
Check(maps.TryAdd(old, increment, out _) == Status.Invalid, "Stale source accepted.");
Check(default(Computation<int, string, int>).Run(0).Status == Status.Invalid, "Default accepted.");
var other = new Arena(1);
var foreign = new ConstantPool<int, string, int>(other, 1);
Check(foreign.TryAdd(Result<int, string>.Ok(5), out var outsider) == Status.Ok, "Foreign setup failed.");
Check(maps.TryAdd(outsider, increment, out _) == Status.Invalid, "Foreign owner accepted.");
Check(maps.TryAdd(newer, value => { Check(arena.TryReset() == Status.Busy, "Reset during run succeeded."); return value; }, out var guarded) == Status.Ok, "Guard setup failed.");
Check(guarded.Run(0).Value == 99, "Guarded run failed.");
Console.WriteLine($"Prepared arena construction, reassignment, mixed-node array, typed mapping, error short circuit and execution: {allocated} bytes.");
Console.WriteLine("Capacity rejection, unchanged existing nodes, stale/default/foreign handles and reset during execution verified.");

PendingChecks.Run();

void Exercise()
{
    Check(arena.TryReset() == Status.Ok, "Reset failed.");
    Check(constants.TryAdd(Result<int, string>.Ok(1), out var current) == Status.Ok, "Constant failed.");
    mixed[0] = current;
    for (int i = 0; i < 7; i++)
    {
        Check(maps.TryAdd(current, increment, out var next) == Status.Ok, "Map failed.");
        current = next;
    }
    mixed[1] = current;
    Check(mixed[0].Run(0).Value == 1 && mixed[1].Run(0).Value == 8, "Mixed node results wrong.");
    Check(texts.TryAdd(current, label, out var text) == Status.Ok && text.Run(0).Value == "done", "Type-changing map failed.");
    Check(constants.TryAdd(Result<int, string>.Err("failed"), out var failed) == Status.Ok, "Error source failed.");
    Check(maps.TryAdd(failed, increment, out var mappedFailure) == Status.Ok, "Error map failed.");
    var error = mappedFailure.Run(0);
    Check(error.Status == Status.Error && error.Error == "failed", "Error short circuit failed.");
    Check(maps.TryAdd(current, increment, out _) == Status.Capacity, "Capacity grew implicitly.");
    Check(current.Run(0).Value == 8, "Capacity rejection damaged a node.");
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

enum Status { Invalid, Ok, Error, Capacity, Busy, EpochExhausted, Pending, Faulted }
readonly record struct Result<T, E>(Status Status, T Value, E Error, Exception? Exception = null)
{
    public static Result<T, E> Ok(T value) => new(Status.Ok, value, default!);
    public static Result<T, E> Err(E error) => new(Status.Error, default!, error);
    public static Result<T, E> Stop(Status status) => new(status, default!, default!);
}
interface IPool { void Clear(); }
sealed class Arena(int poolCapacity)
{
    private readonly IPool[] pools = new IPool[poolCapacity];
    private int poolCount;
    private int state;
    public long Epoch { get; private set; } = 1;
    public void Register(IPool pool)
    {
        if (poolCount == pools.Length) throw new ArgumentException("Preparation capacity exhausted.");
        pools[poolCount++] = pool;
    }
    public bool TryWrite() => Interlocked.CompareExchange(ref state, -1, 0) == 0;
    public void EndWrite() => Volatile.Write(ref state, 0);
    public bool TryRead()
    {
        while (true)
        {
            int observed = Volatile.Read(ref state);
            if (observed < 0 || observed == int.MaxValue) return false;
            if (Interlocked.CompareExchange(ref state, observed + 1, observed) == observed) return true;
        }
    }
    public void EndRead() => Interlocked.Decrement(ref state);
    public Status TryReset()
    {
        if (!TryWrite()) return Status.Busy;
        try
        {
            if (Epoch == long.MaxValue) return Status.EpochExhausted;
            Epoch++;
            for (int i = 0; i < poolCount; i++) pools[i].Clear();
            return Status.Ok;
        }
        finally { EndWrite(); }
    }
}
readonly record struct Computation<T, E, R>(Node<T, E, R>? Node, long Epoch)
{
    public bool BelongsTo(Arena owner) => Node is not null && ReferenceEquals(Node.Owner, owner) && Epoch == owner.Epoch;
    public Result<T, E> Run(R environment)
    {
        if (Node is null) return Result<T, E>.Stop(Status.Invalid);
        if (!Node.Owner.TryRead()) return Result<T, E>.Stop(Status.Busy);
        try { return Read(environment); }
        finally { Node.Owner.EndRead(); }
    }
    public Result<T, E> Read(R environment) => Node is null || Epoch != Node.Owner.Epoch
        ? Result<T, E>.Stop(Status.Invalid) : Node.Run(environment);
}
abstract class Node<T, E, R>(Arena owner)
{
    public Arena Owner { get; } = owner;
    public abstract Result<T, E> Run(R environment);
}
sealed class ConstantNode<T, E, R>(Arena owner) : Node<T, E, R>(owner)
{
    public Result<T, E> Value;
    public override Result<T, E> Run(R environment) => Value;
}
sealed class MapNode<T, U, E, R>(Arena owner) : Node<U, E, R>(owner)
{
    public Computation<T, E, R> Source;
    public Func<T, U>? Map;
    public override Result<U, E> Run(R environment)
    {
        var source = Source.Read(environment);
        return source.Status == Status.Ok ? Result<U, E>.Ok(Map!(source.Value))
            : source.Status == Status.Error ? Result<U, E>.Err(source.Error)
            : Result<U, E>.Stop(source.Status);
    }
}
sealed class ConstantPool<T, E, R> : IPool
{
    private readonly Arena owner;
    private readonly ConstantNode<T, E, R>[] nodes;
    private int count;
    public ConstantPool(Arena owner, int capacity)
    {
        this.owner = owner;
        nodes = new ConstantNode<T, E, R>[capacity];
        for (int i = 0; i < capacity; i++) nodes[i] = new(owner);
        owner.Register(this);
    }
    public Status TryAdd(Result<T, E> value, out Computation<T, E, R> effect)
    {
        effect = default;
        if (!owner.TryWrite()) return Status.Busy;
        try
        {
            if (count == nodes.Length) return Status.Capacity;
            var node = nodes[count++];
            node.Value = value;
            effect = new(node, owner.Epoch);
            return Status.Ok;
        }
        finally { owner.EndWrite(); }
    }
    public void Clear()
    {
        for (int i = 0; i < count; i++) nodes[i].Value = default;
        count = 0;
    }
}
sealed class MapPool<T, U, E, R> : IPool
{
    private readonly Arena owner;
    private readonly MapNode<T, U, E, R>[] nodes;
    private int count;
    public MapPool(Arena owner, int capacity)
    {
        this.owner = owner;
        nodes = new MapNode<T, U, E, R>[capacity];
        for (int i = 0; i < capacity; i++) nodes[i] = new(owner);
        owner.Register(this);
    }
    public Status TryAdd(Computation<T, E, R> source, Func<T, U> map, out Computation<U, E, R> effect)
    {
        effect = default;
        if (!owner.TryWrite()) return Status.Busy;
        try
        {
            if (!source.BelongsTo(owner) || map is null) return Status.Invalid;
            if (count == nodes.Length) return Status.Capacity;
            var node = nodes[count++];
            node.Source = source;
            node.Map = map;
            effect = new(node, owner.Epoch);
            return Status.Ok;
        }
        finally { owner.EndWrite(); }
    }
    public void Clear()
    {
        for (int i = 0; i < count; i++) { nodes[i].Source = default; nodes[i].Map = null; }
        count = 0;
    }
}
