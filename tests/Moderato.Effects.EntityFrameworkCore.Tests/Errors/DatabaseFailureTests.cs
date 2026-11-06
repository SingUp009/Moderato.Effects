namespace Moderato.Effects.EntityFrameworkCore.Tests;

public sealed class DatabaseFailureTests
{
    [Fact]
    public void UnclassifiedDatabaseFailure_NullCause_ThrowsArgumentNullException()
    {
        // Given
        Exception cause = null!;

        // When
        var failure = Assert.Throws<ArgumentNullException>(() => new UnclassifiedDatabaseFailure(cause));

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
    [Fact]
    public void DatabaseFailure_UnclassifiedCase_PreservesItsCauseAndIdentity()
    {
        // Given
        var cause = new IOException("provider");
        var unclassified = new UnclassifiedDatabaseFailure(cause);

        // When
        DatabaseFailure failure = unclassified;

        // Then
        Assert.Same(unclassified, failure.Value);
        Assert.Same(cause, failure.Cause);
        Assert.Equal("unclassified", failure switch
        {
            ConstraintViolation => "constraint",
            OptimisticConcurrencyConflict => "optimistic concurrency",
            CommitNotConfirmed => "unconfirmed commit",
            UnclassifiedDatabaseFailure => "unclassified",
        });
    }

    [Fact]
    public void DatabaseFailure_InApplicationError_PreservesTheNestedUnion()
    {
        // Given
        var cause = new IOException("commit");
        var unconfirmed = new CommitNotConfirmed(cause);
        DatabaseFailure failure = unconfirmed;

        // When
        TestError error = failure;

        // Then
        var nested = Assert.IsType<DatabaseFailure>(error.Value);
        Assert.Same(unconfirmed, nested.Value);
        Assert.Same(cause, nested.Cause);
    }

    [Fact]
    public void DatabaseFailure_DefaultCause_ThrowsInvalidOperationException()
    {
        // Given
        DatabaseFailure failure = default;

        // When
        var exception = Record.Exception(() => failure.Cause);

        // Then
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DatabaseFailure_ClassifiedCaseWithNullCause_ThrowsArgumentNullException(bool commit)
    {
        // Given
        Exception cause = null!;

        // When
        var exception = Record.Exception(() => commit
            ? (DatabaseFailure)new CommitNotConfirmed(cause)
            : new ConstraintViolation(ConstraintKind.Unique, cause));

        // Then
        Assert.Equal("cause", Assert.IsType<ArgumentNullException>(exception).ParamName);
    }

    [Fact]
    public void OptimisticConcurrencyConflict_NullCause_ThrowsArgumentNullException()
    {
        // Given
        Exception cause = null!;

        // When
        var exception = Assert.Throws<ArgumentNullException>(() => new OptimisticConcurrencyConflict(cause));

        // Then
        Assert.Equal("cause", exception.ParamName);
    }

    [Fact]
    public void DatabaseFailure_OptimisticConcurrencyConflict_PreservesTheNestedCaseAndCause()
    {
        // Given
        var cause = new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("conflict");
        var conflict = new OptimisticConcurrencyConflict(cause);
        DatabaseFailure failure = conflict;

        // When
        TestError error = failure;

        // Then
        var nested = Assert.IsType<DatabaseFailure>(error.Value);
        Assert.Same(conflict, nested.Value);
        Assert.Same(cause, nested.Cause);
    }
}
