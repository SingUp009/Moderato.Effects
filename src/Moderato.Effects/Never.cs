namespace Moderato.Effects;

/// <summary>A type with no values. An effect whose error is <see cref="Never"/> cannot fail; one whose value is <see cref="Never"/> cannot succeed.</summary>
public sealed class Never
{
    // A struct's default would be a value of a type that must have none.
    private Never() { }
}
