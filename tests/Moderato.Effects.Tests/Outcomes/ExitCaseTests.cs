namespace Moderato.Effects.Tests;

public class ExitCaseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TryGetValue_CaseOrDefault_ReturnsOnlyMatchingCase(int index)
    {
        // Given
        ExitCase[] exits = [new Succeeded(), new Failed(null), new Die(new Exception("defect"), []), new Interrupted(), default];
        var exit = exits[index];
        var expected = exit.Value;

        // When
        var hasSucceeded = exit.TryGetValue(out Succeeded? succeeded);
        var hasFailed = exit.TryGetValue(out Failed? failed);
        var hasDie = exit.TryGetValue(out Die? die);
        var hasInterrupted = exit.TryGetValue(out Interrupted? interrupted);

        // Then
        Assert.Equal(index == 0, hasSucceeded);
        Assert.Equal(index == 1, hasFailed);
        Assert.Equal(index == 2, hasDie);
        Assert.Equal(index == 3, hasInterrupted);
        Assert.Same(index == 0 ? expected : null, succeeded);
        Assert.Same(index == 1 ? expected : null, failed);
        Assert.Same(index == 2 ? expected : null, die);
        Assert.Same(index == 3 ? expected : null, interrupted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HasValue_CaseOrDefault_MatchesDirectNullPattern(int index)
    {
        // Given
        ExitCase[] exits = [new Succeeded(), new Failed(null), new Die(new Exception("defect"), []), new Interrupted(), default];
        var exit = exits[index];

        // When
        var hasValue = exit.HasValue;
        var matchesNull = exit is null;

        // Then
        Assert.Equal(index != 4, hasValue);
        Assert.Equal(!hasValue, matchesNull);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TryGetValue_NullCase_ReturnsFalseAndNull(int index)
    {
        // Given
        ExitCase[] exits = [new((Succeeded)null!), new((Failed)null!), new((Die)null!), new((Interrupted)null!)];
        var exit = exits[index];

        // When
        var hasValue = exit.HasValue;
        var hasSucceeded = exit.TryGetValue(out Succeeded? succeeded);
        var hasFailed = exit.TryGetValue(out Failed? failed);
        var hasDie = exit.TryGetValue(out Die? die);
        var hasInterrupted = exit.TryGetValue(out Interrupted? interrupted);

        // Then
        Assert.False(hasValue);
        Assert.False(hasSucceeded);
        Assert.False(hasFailed);
        Assert.False(hasDie);
        Assert.False(hasInterrupted);
        Assert.Null(succeeded);
        Assert.Null(failed);
        Assert.Null(die);
        Assert.Null(interrupted);
    }

    [Fact]
    public void Suppressed_DefaultCase_Throws()
    {
        // Given
        ExitCase exit = default;

        // When
        void Act() => _ = new Suppressed(exit, Relation.Sequential);

        // Then
        Assert.Throws<ArgumentException>(Act);
    }

    [Fact]
    public void Suppressed_WithAnotherFailure_ReplacesTheCase()
    {
        // Given
        var suppressed = new Suppressed(new Interrupted(), Relation.Sequential);
        var failed = new Failed("error");

        // When
        var replaced = suppressed with { Case = failed };

        // Then
        Assert.Same(failed, replaced.Case.Value);
        Assert.Equal(Relation.Sequential, replaced.Relation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Suppressed_WithSucceededOrDefaultCase_Throws(bool succeeded)
    {
        // Given
        var suppressed = new Suppressed(new Interrupted(), Relation.Sequential);
        ExitCase exit = succeeded ? new ExitCase(new Succeeded()) : default;

        // When
        void Act() => _ = suppressed with { Case = exit };

        // Then
        Assert.Throws<ArgumentException>(Act);
    }

    [Fact]
    public void Die_ExceptionAndSuppressed_KeepsBoth()
    {
        // Given
        var exception = new InvalidOperationException("defect");
        Suppressed[] suppressed = [new(new Interrupted(), Relation.Parallel)];

        // When
        var die = new Die(exception, suppressed);

        // Then
        Assert.Same(exception, die.Exception);
        Assert.Same(suppressed, die.Suppressed);
        Assert.Equal("Die(InvalidOperationException)", new Exit<int, string>(die).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Die_NullMember_Throws(bool nullException)
    {
        // Given
        var exception = nullException ? null : new InvalidOperationException("defect");
        IReadOnlyList<Suppressed>? suppressed = nullException ? [] : null;

        // When
        void Act() => _ = new Die(exception!, suppressed!);

        // Then
        var thrown = Assert.Throws<ArgumentNullException>(Act);
        Assert.Equal(nullException ? "Exception" : "Suppressed", thrown.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Die_WithNullMember_Throws(bool nullException)
    {
        // Given
        var die = new Die(new InvalidOperationException("defect"), []);

        // When
        void Act() => _ = nullException ? die with { Exception = null! } : die with { Suppressed = null! };

        // Then
        var thrown = Assert.Throws<ArgumentNullException>(Act);
        Assert.Equal(nullException ? "Exception" : "Suppressed", thrown.ParamName);
    }
}
