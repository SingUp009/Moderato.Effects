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

    /// <summary>Creates an Ok exit.</summary>
    /// <param name="ok">The value.</param>
    public Exit(Ok<T> ok)
    {
        value = ok.Value;
        error = default!;
        other = null;
        state = STATE_OK;
    }

    /// <summary>Creates an Err exit.</summary>
    /// <param name="err">The expected error.</param>
    public Exit(Err<E> err)
    {
        value = default!;
        error = err.Error;
        other = null;
        state = STATE_ERR;
    }

    /// <summary>Creates a Die exit.</summary>
    /// <param name="die">The defect.</param>
    public Exit(Die die)
    {
        ArgumentNullException.ThrowIfNull(die);
        value = default!;
        error = default!;
        other = die;
        state = STATE_DIE;
    }

    /// <summary>Creates an Interrupted exit.</summary>
    /// <param name="interrupted">The interruption.</param>
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
    /// <param name="ok">The Ok case, when this is one.</param>
    /// <returns>True if Ok.</returns>
    public bool TryGetValue(out Ok<T> ok)
    {
        ok = new Ok<T>(value);
        return state == STATE_OK;
    }

    /// <summary>Non-boxing access to the Err case.</summary>
    /// <param name="err">The Err case, when this is one.</param>
    /// <returns>True if Err.</returns>
    public bool TryGetValue(out Err<E> err)
    {
        err = new Err<E>(error);
        return state == STATE_ERR;
    }

    /// <summary>Access to the Die case.</summary>
    /// <param name="die">The Die case, when this is one.</param>
    /// <returns>True if Die.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Die? die)
    {
        die = other as Die;
        return state == STATE_DIE;
    }

    /// <summary>Access to the Interrupted case.</summary>
    /// <param name="interrupted">The Interrupted case, when this is one.</param>
    /// <returns>True if Interrupted.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Interrupted? interrupted)
    {
        interrupted = other as Interrupted;
        return state == STATE_INTERRUPTED;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Exit<T, E> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => state switch
    {
        STATE_OK => HashCode.Combine(state, value),
        STATE_ERR => HashCode.Combine(state, error),
        STATE_DIE => HashCode.Combine(state, other),
        _ => state,
    };

    /// <summary>Compares two exits.</summary>
    public static bool operator ==(Exit<T, E> left, Exit<T, E> right) => left.Equals(right);

    /// <summary>Compares two exits.</summary>
    public static bool operator !=(Exit<T, E> left, Exit<T, E> right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => state switch
    {
        STATE_OK => $"Ok({value})",
        STATE_ERR => $"Err({error})",
        STATE_DIE => $"Die({((Die)other!).Exception.GetType().Name})",
        STATE_INTERRUPTED => "Interrupted",
        _ => "default",
    };
}
