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
    private const byte StateDefault = 0;
    private const byte StateOk = 1;
    private const byte StateErr = 2;
    private const byte StateDie = 3;
    private const byte StateInterrupted = 4;

    private readonly T _value;
    private readonly E _error;
    private readonly object? _other;
    private readonly byte _state;

    /// <summary>Creates an Ok exit.</summary>
    /// <param name="ok">The value.</param>
    public Exit(Ok<T> ok)
    {
        _value = ok.Value;
        _error = default!;
        _other = null;
        _state = StateOk;
    }

    /// <summary>Creates an Err exit.</summary>
    /// <param name="err">The expected error.</param>
    public Exit(Err<E> err)
    {
        _value = default!;
        _error = err.Error;
        _other = null;
        _state = StateErr;
    }

    /// <summary>Creates a Die exit.</summary>
    /// <param name="die">The defect.</param>
    public Exit(Die die)
    {
        ArgumentNullException.ThrowIfNull(die);
        _value = default!;
        _error = default!;
        _other = die;
        _state = StateDie;
    }

    /// <summary>Creates an Interrupted exit.</summary>
    /// <param name="interrupted">The interruption.</param>
    public Exit(Interrupted interrupted)
    {
        ArgumentNullException.ThrowIfNull(interrupted);
        _value = default!;
        _error = default!;
        _other = interrupted;
        _state = StateInterrupted;
    }

    /// <summary>The boxed case, or <see langword="null"/> for <c>default</c>. Allocates; pattern matching does not use it.</summary>
    public object? Value => _state switch
    {
        StateOk => new Ok<T>(_value),
        StateErr => new Err<E>(_error),
        StateDie or StateInterrupted => _other,
        _ => null,
    };

    /// <summary>False only for <c>default</c>.</summary>
    public bool HasValue => _state != StateDefault;

    /// <summary>Non-boxing access to the Ok case.</summary>
    /// <param name="ok">The Ok case, when this is one.</param>
    /// <returns>True if Ok.</returns>
    public bool TryGetValue(out Ok<T> ok)
    {
        ok = new Ok<T>(_value);
        return _state == StateOk;
    }

    /// <summary>Non-boxing access to the Err case.</summary>
    /// <param name="err">The Err case, when this is one.</param>
    /// <returns>True if Err.</returns>
    public bool TryGetValue(out Err<E> err)
    {
        err = new Err<E>(_error);
        return _state == StateErr;
    }

    /// <summary>Access to the Die case.</summary>
    /// <param name="die">The Die case, when this is one.</param>
    /// <returns>True if Die.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Die? die)
    {
        die = _other as Die;
        return _state == StateDie;
    }

    /// <summary>Access to the Interrupted case.</summary>
    /// <param name="interrupted">The Interrupted case, when this is one.</param>
    /// <returns>True if Interrupted.</returns>
    public bool TryGetValue([NotNullWhen(true)] out Interrupted? interrupted)
    {
        interrupted = _other as Interrupted;
        return _state == StateInterrupted;
    }

    /// <inheritdoc/>
    public bool Equals(Exit<T, E> other)
    {
        if (_state != other._state) return false;
        return _state switch
        {
            StateOk => EqualityComparer<T>.Default.Equals(_value, other._value),
            StateErr => EqualityComparer<E>.Default.Equals(_error, other._error),
            StateDie => ReferenceEquals(_other, other._other),
            _ => true,
        };
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Exit<T, E> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _state switch
    {
        StateOk => HashCode.Combine(_state, _value),
        StateErr => HashCode.Combine(_state, _error),
        StateDie => HashCode.Combine(_state, _other),
        _ => _state,
    };

    /// <summary>Compares two exits.</summary>
    public static bool operator ==(Exit<T, E> left, Exit<T, E> right) => left.Equals(right);

    /// <summary>Compares two exits.</summary>
    public static bool operator !=(Exit<T, E> left, Exit<T, E> right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => _state switch
    {
        StateOk => $"Ok({_value})",
        StateErr => $"Err({_error})",
        StateDie => $"Die({((Die)_other!).Exception.GetType().Name})",
        StateInterrupted => "Interrupted",
        _ => "default",
    };
}
