using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Moderato.Results;

namespace Moderato.Effects;

/// <summary>How a run ended: <see cref="Ok{T}"/>, <see cref="Err{E}"/>, <see cref="Effects.Die"/> or <see cref="Effects.Interrupted"/>. <c>default</c> is invalid.</summary>
[Union]
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct Exit<T, E> : IUnion, IEquatable<Exit<T, E>>
{
    private const byte STATE_DEFAULT = 0;
    private const byte STATE_OK = 1;
    private const byte STATE_ERR = 2;
    private const byte STATE_DIE = 3;
    private const byte STATE_INTERRUPTED = 4;

    private readonly T value;
    private readonly E error;
    private readonly object? other;
    private readonly byte state;

    public Exit(Ok<T> ok)
    {
        value = ok.Value;
        error = default!;
        other = null;
        state = STATE_OK;
    }

    public Exit(Err<E> err)
    {
        value = default!;
        error = err.Error;
        other = null;
        state = STATE_ERR;
    }

    public Exit(Die die)
    {
        ArgumentNullException.ThrowIfNull(die);
        value = default!;
        error = default!;
        other = die;
        state = STATE_DIE;
    }

    public Exit(Interrupted interrupted)
    {
        ArgumentNullException.ThrowIfNull(interrupted);
        value = default!;
        error = default!;
        other = interrupted;
        state = STATE_INTERRUPTED;
    }

    /// <summary>The boxed case, or <see langword="null"/> for <c>default</c>; it allocates.</summary>
    public object? Value => state switch
    {
        STATE_OK => new Ok<T>(value),
        STATE_ERR => new Err<E>(error),
        STATE_DIE or STATE_INTERRUPTED => other,
        _ => null,
    };

    /// <summary>False only for <c>default</c>.</summary>
    public bool HasValue => state != STATE_DEFAULT;

    /// <summary>Non-boxing access to the Ok case.</summary>
    public bool TryGetValue(out Ok<T> ok)
    {
        ok = new Ok<T>(value);
        return state == STATE_OK;
    }

    /// <summary>Non-boxing access to the Err case.</summary>
    public bool TryGetValue(out Err<E> err)
    {
        err = new Err<E>(error);
        return state == STATE_ERR;
    }

    public bool TryGetValue([NotNullWhen(true)] out Die? die)
    {
        die = other as Die;
        return state == STATE_DIE;
    }

    public bool TryGetValue([NotNullWhen(true)] out Interrupted? interrupted)
    {
        interrupted = other as Interrupted;
        return state == STATE_INTERRUPTED;
    }

    public bool Equals(Exit<T, E> other)
    {
        if (state != other.state) return false;
        return state switch
        {
            STATE_OK => EqualityComparer<T>.Default.Equals(value, other.value),
            STATE_ERR => EqualityComparer<E>.Default.Equals(error, other.error),
            STATE_DIE => ReferenceEquals(this.other, other.other),
            _ => true,
        };
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Exit<T, E> other && Equals(other);

    public override int GetHashCode() => state switch
    {
        STATE_OK => HashCode.Combine(state, value),
        STATE_ERR => HashCode.Combine(state, error),
        STATE_DIE => HashCode.Combine(state, other),
        _ => state,
    };

    public static bool operator ==(Exit<T, E> left, Exit<T, E> right) => left.Equals(right);

    public static bool operator !=(Exit<T, E> left, Exit<T, E> right) => !left.Equals(right);

    public override string ToString() => state switch
    {
        STATE_OK => $"Ok({value})",
        STATE_ERR => $"Err({error})",
        STATE_DIE => $"Die({((Die)other!).Exception.GetType().Name})",
        STATE_INTERRUPTED => "Interrupted",
        _ => "default",
    };
}
