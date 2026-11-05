using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moderato.Effects;
using Moderato.Effects.Database.Prototype;
using Moderato.Results;

await RunOutboxFlow();
await RunTransactionControls();
await RunConsumerRollbackControl();
Console.WriteLine("PASS: Core-only application flow; prototype, not a database package API.");

static async Task RunOutboxFlow()
{
    // Given
    await using var database = await OpenDatabase();
    var broker = new StubBroker { PauseAfterAccept = true };
    var environment = new PrototypeEnvironment(database, broker);
    var order = new Order("order-001", 1250);

    // When
    var placed = await Workflow.PlaceOrder(order).RunExitAsync(environment);

    // Then
    Require(placed is Ok<Order>, "Order creation must succeed.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM orders") == 1, "Order must be stored.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM outbox WHERE published = 0") == 1, "Outbox must be stored in the same commit.");
    Console.WriteLine("STATE placed: orders=1, pending=1, broker-deliveries=0");

    // Given
    using var stop = new CancellationTokenSource();

    // When
    var sending = Workflow.DispatchPending().RunExitAsync(environment, stop.Token).AsTask();
    try
    {
        await broker.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally
    {
        stop.Cancel();
    }
    var stopped = await sending.WaitAsync(TimeSpan.FromSeconds(10));

    // Then
    Require(stopped is Interrupted, "An interrupted send must remain Interrupted.");
    Require(broker.Deliveries.Count == 1, "The broker accepted before the sender observed interruption.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM outbox WHERE published = 0") == 1, "An interrupted send must not mark the row sent.");
    Console.WriteLine("STATE interrupted: exit=Interrupted, pending=1, broker-deliveries=1");

    // Given
    broker.PauseAfterAccept = false;
    var restartedEnvironment = new PrototypeEnvironment(database, broker);
    await using var receiverDatabase = await OpenDatabase();
    var receiverEnvironment = new PrototypeEnvironment(receiverDatabase, broker);

    // When
    var restarted = await Workflow.DispatchPending().RunExitAsync(restartedEnvironment);
    var applied = 0;
    foreach (var delivery in broker.Deliveries)
    {
        var consumed = await Workflow.Consume(delivery).RunExitAsync(receiverEnvironment);
        Require(consumed.TryGetValue(out Ok<bool> result), "Consumer must complete successfully.");
        if (result.Value) applied++;
    }
    var emptyPass = await Workflow.DispatchPending().RunExitAsync(restartedEnvironment);

    // Then
    Require(restarted is Ok<int> { Value: 1 } && emptyPass is Ok<int> { Value: 0 }, "Restart must send one pending row and then find none.");
    Require(broker.Deliveries.Count == 2 && broker.Deliveries[0].EventId == broker.Deliveries[1].EventId, "Restart must reuse the persisted identity.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM outbox WHERE published = 0") == 0, "Acknowledged send must mark the row.");
    Require(await Scalar(receiverDatabase, "SELECT COUNT(*) FROM inbox") == 1, "Inbox must record one identity.");
    var applications = await Scalar(receiverDatabase, "SELECT applications FROM handled_orders");
    Require(applied == 1 && applications == 1, "Duplicate delivery must not apply the business mutation twice.");
    Console.WriteLine("STATE restarted: pending=0, broker-deliveries=2, inbox=1, business-applications=1");
}

static async Task RunTransactionControls()
{
    foreach (var fault in new[] { OrderFault.RejectAfterOrderWrite, OrderFault.ThrowAfterOutboxWrite, OrderFault.FailInnerRelease, OrderFault.PauseBeforeCommit, OrderFault.LoseCommitReply, OrderFault.ThrowAfterAcknowledgedCommit })
    {
        // Given
        await using var database = await OpenDatabase();
        var environment = new PrototypeEnvironment(database, new StubBroker());
        using var caller = new CancellationTokenSource();

        // When
        var running = Workflow.PlaceOrder(new Order("controlled", 100), fault).RunExitAsync(environment, caller.Token).AsTask();
        if (fault == OrderFault.PauseBeforeCommit)
        {
            try
            {
                await environment.WritesStaged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                caller.Cancel();
            }
        }
        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10));
        var orders = await Scalar(database, "SELECT COUNT(*) FROM orders");
        var outbox = await Scalar(database, "SELECT COUNT(*) FROM outbox");

        // Then
        var expectedRows = fault is OrderFault.LoseCommitReply or OrderFault.ThrowAfterAcknowledgedCommit ? 1 : 0;
        Require(orders == expectedRows && outbox == expectedRows, "Order and outbox must be observed together.");
        Require(environment.Cleanup.Contains("rollback attempted") == (fault != OrderFault.ThrowAfterAcknowledgedCommit), "Rollback must be attempted unless commit is acknowledged.");
        Require(environment.Cleanup.Contains("dispose attempted"), "Disposal must still be attempted.");
        switch (fault)
        {
            case OrderFault.RejectAfterOrderWrite:
                Require(exit is Err<WorkflowError>, "Business rejection must remain Err.");
                break;
            case OrderFault.ThrowAfterOutboxWrite:
            case OrderFault.FailInnerRelease:
                Require(exit.TryGetValue(out Die? bodyDie) && bodyDie.Exception is InvalidOperationException, "Body defect must remain primary.");
                break;
            case OrderFault.PauseBeforeCommit:
                Require(exit is Interrupted, "Cancellation before commit must remain Interrupted.");
                break;
            case OrderFault.LoseCommitReply:
                Require(exit.TryGetValue(out Die? commitDie) && commitDie.Exception is IOException, "Lost commit reply must remain the main defect.");
                Require(commitDie.Suppressed.Any(failure => failure.Relation == Relation.Sequential && failure.Case is Die), "Failed rollback after commit must be retained as sequential context.");
                break;
            case OrderFault.ThrowAfterAcknowledgedCommit:
                Require(exit.TryGetValue(out Die? afterCommitDie) && afterCommitDie.Exception is IOException && afterCommitDie.Suppressed.Count == 0, "An acknowledged commit must not trigger a second rollback on later failure.");
                break;
        }
        Console.WriteLine($"STATE {fault}: exit={exit}, orders={orders}, outbox={outbox}, cleanup=[{string.Join(", ", environment.Cleanup)}]");
    }
}

static async Task RunConsumerRollbackControl()
{
    // Given
    await using var database = await OpenDatabase();
    var environment = new PrototypeEnvironment(database, new StubBroker());
    var malformed = new Message("receiver-event", "{");

    // When
    var failed = await Workflow.Consume(malformed).RunExitAsync(environment);

    // Then
    Require(failed.TryGetValue(out Die? die) && die.Exception is JsonException, "Malformed payload must fail after the Inbox insert.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM inbox") == 0, "A failed handler must not retain its deduplication marker.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM handled_orders") == 0, "Failed handling must not apply the business mutation.");

    // Given
    var corrected = malformed with { Payload = JsonSerializer.Serialize(new OrderPlaced("receiver-order", 100)) };

    // When
    var retry = await Workflow.Consume(corrected).RunExitAsync(environment);

    // Then
    Require(retry is Ok<bool> { Value: true }, "The same identity must remain processable after failed handling.");
    Require(await Scalar(database, "SELECT COUNT(*) FROM inbox") == 1 && await Scalar(database, "SELECT applications FROM handled_orders") == 1, "Successful handling must commit both Inbox and mutation.");
    Console.WriteLine("STATE receiver-retry: failed handling inbox=0, retry inbox=1, business-applications=1");
}

static async Task<SqliteConnection> OpenDatabase()
{
    var database = new SqliteConnection("Data Source=:memory:");
    try
    {
        await database.OpenAsync();
        using var schema = database.CreateCommand();
        schema.CommandText = """
            CREATE TABLE orders(id TEXT PRIMARY KEY, amount_cents INTEGER NOT NULL);
            CREATE TABLE outbox(event_id TEXT PRIMARY KEY, payload TEXT NOT NULL, published INTEGER NOT NULL);
            CREATE TABLE inbox(event_id TEXT PRIMARY KEY);
            CREATE TABLE handled_orders(order_id TEXT PRIMARY KEY, applications INTEGER NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync();
        return database;
    }
    catch
    {
        await database.DisposeAsync();
        throw;
    }
}

static async Task<long> Scalar(SqliteConnection database, string sql)
{
    using var command = database.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
