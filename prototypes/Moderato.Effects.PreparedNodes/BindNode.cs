readonly record struct BuildResult<T, E, R>(Status Status, Computation<T, E, R> Computation);

sealed class BindNode<T, U, E, R>(Arena owner) : Node<U, E, R>(owner)
{
    public Computation<T, E, R> Source;
    public Func<T, BuildResult<U, E, R>>? Bind;

    public override Result<U, E> Run(R environment)
    {
        var source = Source.Read(environment);
        if (source.Status != Status.Ok) return new(source.Status, default!, source.Error, source.Exception);
        var next = Bind!(source.Value);
        return next.Status == Status.Ok ? next.Computation.Run(environment) : Result<U, E>.Stop(next.Status);
    }
}

sealed class BindPool<T, U, E, R> : IPool
{
    private readonly Arena owner;
    private readonly BindNode<T, U, E, R>[] nodes;
    private int count;

    public BindPool(Arena owner, int capacity)
    {
        this.owner = owner;
        nodes = new BindNode<T, U, E, R>[capacity];
        for (var i = 0; i < capacity; i++) nodes[i] = new(owner);
        owner.Register(this);
    }

    public Status TryAdd(Computation<T, E, R> source, Func<T, BuildResult<U, E, R>> bind, out Computation<U, E, R> effect)
    {
        effect = default;
        if (!owner.TryWrite()) return Status.Busy;
        try
        {
            if (!source.BelongsTo(owner) || bind is null) return Status.Invalid;
            if (count == nodes.Length) return Status.Capacity;
            var node = nodes[count++];
            node.Source = source;
            node.Bind = bind;
            effect = new(node, owner.Epoch);
            return Status.Ok;
        }
        finally { owner.EndWrite(); }
    }

    public void Clear()
    {
        for (var i = 0; i < count; i++)
        {
            nodes[i].Source = default;
            nodes[i].Bind = null;
        }
        count = 0;
    }
}
