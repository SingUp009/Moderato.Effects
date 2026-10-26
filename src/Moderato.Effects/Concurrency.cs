namespace Moderato.Effects;

/// <summary>How a group of effects runs; <c>default</c> = <see cref="Sequential"/>.</summary>
public readonly struct Concurrency : IEquatable<Concurrency>
{
    // 0 = sequential, -1 = unbounded, n > 0 = at most n at a time.
    private readonly int maxDegree;

    private Concurrency(int maxDegree) => this.maxDegree = maxDegree;

    /// <summary>One at a time, in order, on the caller's flow, so non-thread-safe services are safe.</summary>
    public static Concurrency Sequential => default;

    /// <summary>All at once, each started on the thread pool.</summary>
    public static Concurrency Parallel => new(-1);

    /// <summary>Whether the effects run at the same time.</summary>
    public bool IsParallel => maxDegree != 0;

    /// <summary>The most effects running at once; <see langword="null"/> = unbounded or sequential.</summary>
    public int? MaxDegree => maxDegree > 0 ? maxDegree : null;

    /// <summary>Parallel, with at most <paramref name="maxDegree"/> effects running at the same time.</summary>
    /// <param name="maxDegree">The limit, ≥ 1.</param>
    /// <returns>The limited concurrency.</returns>
    public Concurrency WithMaxDegree(int maxDegree)
    {
        if (!IsParallel) throw new InvalidOperationException("Sequential runs one at a time already. Use Concurrency.Parallel.WithMaxDegree(n).");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, 1);
        return new(maxDegree);
    }

    /// <inheritdoc/>
    public bool Equals(Concurrency other) => maxDegree == other.maxDegree;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Concurrency other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => maxDegree;

    /// <inheritdoc/>
    public override string ToString() => maxDegree switch
    {
        0 => "Sequential",
        -1 => "Parallel",
        var n => $"Parallel(max {n})",
    };

    /// <summary>Equality.</summary>
    public static bool operator ==(Concurrency left, Concurrency right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(Concurrency left, Concurrency right) => !left.Equals(right);
}
