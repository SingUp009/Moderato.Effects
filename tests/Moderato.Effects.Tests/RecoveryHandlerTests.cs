using Moderato.Effects.Internal;

namespace Moderato.Effects.Tests;

[Collection(nameof(DiagnosticsCollection))]
public class RecoveryHandlerTests
{
    [Theory]
    [InlineData("OrElse", false)]
    [InlineData("OrElse", true)]
    [InlineData("Catch", false)]
    [InlineData("Catch", true)]
    [InlineData("CatchCase", false)]
    [InlineData("CatchCase", true)]
    [InlineData("CatchCaseNever", false)]
    [InlineData("CatchCaseNever", true)]
    public async Task Recovery_HandlerThrowsOrReturnsDefault_KeepsDefectContextWithoutReporting(string operation, bool returnsDefault)
    {
        // Given
        var defect = new IOException("context");
        var handlerFailure = new FormatException("handler");
        var reported = new List<ExitCase>();
        var failure = Outcome<int, AppError>.Err(new NotFound(1))
            .WithSuppressed([new Suppressed(new Die(defect, []), Relation.Parallel)], 0);
        Effect<int, AppError, EmptyEnvironment> source = new((_, _) => new(failure));
        var recovered = operation switch
        {
            "OrElse" => source.OrElse<AppError>(_ => returnsDefault ? default : throw handlerFailure).MapErr(static error => (object)error),
            "Catch" => source.Catch<NotFound, Denied>(_ => returnsDefault ? default : throw handlerFailure).MapErr(static error => (object)error),
            "CatchCase" => source.CatchCase(Effect<int, AppError, EmptyEnvironment> (NotFound _) => returnsDefault ? default : throw handlerFailure).MapErr(static error => (object)error),
            _ => source.CatchCase(Effect<int, Never, EmptyEnvironment> (NotFound _) => returnsDefault ? default : throw handlerFailure).MapErr(static error => (object)error),
        };
        EffectDiagnostics.UnobservedFailureHandler = item => reported.Add(item);
        try
        {
            // When
            var exit = await recovered.RunExitAsync(EmptyEnvironment.Value, TestContext.Current.CancellationToken);

            // Then
            var die = Assert.IsType<Die>(exit.Value);
            if (returnsDefault) Assert.IsType<InvalidOperationException>(die.Exception);
            else Assert.Same(handlerFailure, die.Exception);
            Assert.Single(die.Suppressed, item => item.Case.Value is Die found && ReferenceEquals(found.Exception, defect));
            Assert.Empty(reported);
        }
        finally
        {
            EffectDiagnostics.UnobservedFailureHandler = null;
        }
    }
}
