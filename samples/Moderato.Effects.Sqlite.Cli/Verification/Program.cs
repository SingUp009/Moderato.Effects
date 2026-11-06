using System.Diagnostics;
using Microsoft.Data.Sqlite;

var dotnet = args.Length > 0 ? args[0] : "dotnet";
var assembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "../../../../bin/Release/net11.0/Moderato.Effects.Sqlite.Cli.dll"));
var directory = Directory.CreateTempSubdirectory("moderato-crud-");
var database = Path.Combine(directory.FullName, "orders.db");

try
{
    await Run("create", ["--id", "1", "--name", "Coffee"], 0, "OK 1: Coffee");
    await Run("read", ["--id", "1"], 0, "OK 1: Coffee");
    await Run("create", ["--id", "1", "--name", "Duplicate"], 2, "ALREADY_EXISTS 1");
    await Run("update", ["--id", "1", "--name", ""], 2, "INVALID name must not be blank");
    await Run("read", ["--id", "1"], 0, "OK 1: Coffee");
    await Run("create", ["--id", "0", "--name", "Invalid"], 2, "INVALID id must be positive");
    await Run("update", ["--id", "99", "--name", "Missing"], 2, "NOT_FOUND 99");
    await Run("delete", ["--id", "99"], 2, "NOT_FOUND 99");
    await Run("update", ["--id", "1", "--name", "Tea"], 0, "OK 1: Tea");
    await Run("read", ["--id", "1"], 0, "OK 1: Tea");
    await CheckDatabase("SELECT COUNT(*) FROM Orders WHERE Id = 1 AND Name = 'Tea'", 1);
    await CheckDatabase("SELECT COUNT(*) FROM Orders", 1);
    await Run("delete", ["--id", "1"], 0, "OK 1: Tea");
    await Run("read", ["--id", "1"], 2, "NOT_FOUND 1");
    await CheckDatabase("SELECT COUNT(*) FROM Orders", 0);
    Console.WriteLine("PASS: CRUD, persistence, duplicate, validation, missing rows (12 CLI calls)");
}
finally
{
    SqliteConnection.ClearAllPools();
    directory.Delete(recursive: true);
}

async Task Run(string command, string[] arguments, int expectedCode, string expectedOutput)
{
    // Given
    var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { assembly, command }.Concat(arguments).Concat(["--database", database]))
        start.ArgumentList.Add(argument);

    // When
    using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await process.WaitForExitAsync(timeout.Token);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw;
    }

    // Then
    var output = await stdout + await stderr;
    if (process.ExitCode != expectedCode || !output.Contains(expectedOutput, StringComparison.Ordinal))
        throw new InvalidOperationException($"{command}: exit {process.ExitCode}, expected {expectedCode}: {output}");
}

async Task CheckDatabase(string sql, long expected)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    if (Convert.ToInt64(await command.ExecuteScalarAsync()) != expected)
        throw new InvalidOperationException($"Unexpected persisted data: {sql}");
}
