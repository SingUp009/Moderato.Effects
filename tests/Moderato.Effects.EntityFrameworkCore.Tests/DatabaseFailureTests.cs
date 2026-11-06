namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class DatabaseFailureTests
{
    [Fact]
    public void DatabaseFailure_NullCause_ThrowsArgumentNullException()
    {
        // Given
        Exception cause = null!;

        // When
        var failure = Assert.Throws<ArgumentNullException>(() => new DatabaseFailure(cause));

        // Then
        Assert.Equal("cause", failure.ParamName);
    }

    [Fact]
    public void ConstraintViolation_UniqueConstraint_PreservesItsCause()
    {
        // Given
        var cause = new InvalidOperationException("constraint");

        // When
        var failure = new ConstraintViolation(ConstraintKind.Unique, cause);

        // Then
        Assert.Equal(ConstraintKind.Unique, failure.Kind);
        Assert.Same(cause, failure.Cause);
    }

    [Fact]
    public void CommitNotConfirmed_OriginalException_PreservesItsCause()
    {
        // Given
        var cause = new IOException("commit");

        // When
        var failure = new CommitNotConfirmed(cause);

        // Then
        Assert.Same(cause, failure.Cause);
    }
}
