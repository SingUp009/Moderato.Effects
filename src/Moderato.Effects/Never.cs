namespace Moderato.Effects;

/// <summary>A type with no values: a <see cref="Never"/> error cannot fail, a <see cref="Never"/> value cannot succeed.</summary>
public sealed class Never
{
    // A struct's default would be a value of a type that must have none.
    private Never() { }
}
