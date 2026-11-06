using Moderato.Effects;
using Moderato.Results;

internal static class ControlNestingProbe
{
    internal static async Task<int> Run(string scenario, int depth, bool pending, bool constant)
    {
        // Given
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        var effect = Effect<DefaultEnvironment>.Async<int>(async _ =>
        {
            entries++;
            await completion.Task.ConfigureAwait(false);
            return 0;
        });
        if (constant) effect = Effect<DefaultEnvironment>.Succeed(0);
        for (var i = 0; i < depth; i++)
        {
            effect = scenario == "retry-nest"
                ? effect.Retry(Schedule.Immediately.UpTo(1))
                : effect.Timeout(TimeSpan.FromMinutes(5)).MapErr(static _ => (Never)null!);
        }
        if (!pending) completion.SetResult();

        // When
        var running = effect.RunExitAsync(DefaultEnvironment.Value, CancellationToken.None);
        var completed = running.IsCompletedSuccessfully;
        if (pending) completion.SetResult();
        var exit = await running.ConfigureAwait(false);
        var rerun = await effect.RunExitAsync(DefaultEnvironment.Value, CancellationToken.None).ConfigureAwait(false);

        // Then
        if (completed == pending || exit.Value is not Ok<int>(0) || rerun.Value is not Ok<int>(0) || entries != (constant ? 0 : 2))
            throw new InvalidOperationException("Control nesting changed completion or rerun behavior.");
        Console.WriteLine($"PASS {scenario} depth={depth} {(constant ? "constant" : pending ? "pending" : "sync")}");
        return 0;
    }
}
