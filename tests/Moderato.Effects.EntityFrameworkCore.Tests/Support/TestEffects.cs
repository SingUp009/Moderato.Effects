namespace Moderato.Effects.EntityFrameworkCore.Tests;

internal static class TestEffects
{

    internal static Effect<int, TestError, TestDb> Insert(int id)
        => Scope<TestError, TestDb>.Gen(async scope =>
        {
            scope.Env.Rows.Add(new Row { Id = id, Name = "saved" });
            return await scope.Env.SaveChangesAsync(scope.CancellationToken);
        });
}
