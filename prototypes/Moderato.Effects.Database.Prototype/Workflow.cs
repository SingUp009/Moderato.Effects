using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moderato.Effects;
using Moderato.Results;

namespace Moderato.Effects.Database.Prototype;

public sealed record Order(string Id, long AmountCents);
public sealed record OrderPlaced(string OrderId, long AmountCents);
public sealed record OrderRejected(string Reason);
public union WorkflowError(OrderRejected);
public sealed record Message(string EventId, string Payload);

public enum OrderFault
{
    None,
    RejectAfterOrderWrite,
    ThrowAfterOutboxWrite,
    FailInnerRelease,
    PauseBeforeCommit,
    LoseCommitReply,
    ThrowAfterAcknowledgedCommit,
}

public sealed record PrototypeEnvironment(SqliteConnection Database, StubBroker Broker)
{
    public TaskCompletionSource WritesStaged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Cleanup { get; } = [];
}

public static class Workflow
{
    public static Effect<Order, WorkflowError, PrototypeEnvironment> PlaceOrder(Order order, OrderFault fault = OrderFault.None)
        => InTransaction(transaction => Scope<WorkflowError, PrototypeEnvironment>.Gen(async scope =>
        {
            await scope.Run(InsertOrder(transaction, order));
            if (fault == OrderFault.RejectAfterOrderWrite)
                throw scope.Fail(new OrderRejected("simulated business rejection after the first write"));

            await scope.Run(InsertOutbox(transaction, order));
            if (fault == OrderFault.ThrowAfterOutboxWrite)
                throw new InvalidOperationException("simulated body defect");
            if (fault == OrderFault.FailInnerRelease)
                scope.Defer(_ => Effect<PrototypeEnvironment>.Sync<Unit>(() => throw new InvalidOperationException("simulated inner release defect")));
            if (fault == OrderFault.PauseBeforeCommit)
            {
                await scope.Run(Effect<PrototypeEnvironment>.AccessAsync<Unit>(async (environment, token) =>
                {
                    environment.WritesStaged.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Unit.Value;
                }));
            }
            return order;
        }), commitFault: fault);

    public static Effect<int, WorkflowError, PrototypeEnvironment> DispatchPending()
        => Scope<WorkflowError, PrototypeEnvironment>.Gen(async scope =>
        {
            var messages = await scope.Run(ReadPending());
            foreach (var message in messages)
            {
                // P4: An interrupted send may already be accepted; retries must reuse the persisted event ID.
                await scope.Run(Effect<PrototypeEnvironment>.AccessAsync<Unit>(async (environment, token) =>
                {
                    await environment.Broker.SendAsync(message, token);
                    return Unit.Value;
                }));
                await scope.Run(MarkPublished(message.EventId));
            }
            return messages.Length;
        });

    public static Effect<bool, WorkflowError, PrototypeEnvironment> Consume(Message message)
        => InTransaction(transaction => Scope<WorkflowError, PrototypeEnvironment>.Gen(async scope =>
        {
            // P5: Deduplication and the receiver's database mutation must commit together.
            var firstDelivery = await scope.Run(RecordInbox(transaction, message.EventId));
            if (firstDelivery == 0) return false;
            var order = JsonSerializer.Deserialize<OrderPlaced>(message.Payload)
                ?? throw new InvalidOperationException("Missing order event payload.");
            await scope.Run(ApplyOrder(transaction, order));
            return true;
        }));

    private static Effect<T, WorkflowError, PrototypeEnvironment> InTransaction<T>(
        Func<SqliteTransaction, Effect<T, WorkflowError, PrototypeEnvironment>> body, OrderFault commitFault = OrderFault.None)
    {
        ArgumentNullException.ThrowIfNull(body);
        Effect<TransactionLease, WorkflowError, PrototypeEnvironment> acquire =
            Effect<PrototypeEnvironment>.AccessAsync<TransactionLease>(async (environment, token) =>
                new((SqliteTransaction)await environment.Database.BeginTransactionAsync(token)));
        var transaction = Resource.Make(acquire, (lease, exit) => Scope<Never, PrototypeEnvironment>.Gen(async cleanup =>
        {
            cleanup.Env.Cleanup.Add(exit.TryGetValue(out Succeeded? _) ? "release: Succeeded" : "release: failure");
            // P2: A rollback failure must not skip disposal or replace the original failure with a raw finally.
            cleanup.Defer(_ => Effect<PrototypeEnvironment>.AccessAsync<Unit>(async (environment, _) =>
            {
                environment.Cleanup.Add("dispose attempted");
                await lease.Transaction.DisposeAsync();
                return Unit.Value;
            }));
            if (!lease.CommitAcknowledged)
            {
                await cleanup.Run(Effect<PrototypeEnvironment>.AccessAsync<Unit>(async (environment, _) =>
                {
                    environment.Cleanup.Add("rollback attempted");
                    // The nested Scope has its own token; rollback must remain uncancelable as well.
                    await lease.Transaction.RollbackAsync(CancellationToken.None);
                    return Unit.Value;
                }));
            }
            return Unit.Value;
        }));

        return transaction.Use(lease => body(lease.Transaction).AndThen(value =>
            Effect<PrototypeEnvironment>.Async<T>(async token =>
            {
                await lease.Transaction.CommitAsync(token);
                if (commitFault == OrderFault.LoseCommitReply) throw new IOException("simulated loss of the successful commit reply");
                // P1: Exit alone cannot distinguish an acknowledged commit from an unknown database outcome.
                lease.CommitAcknowledged = true;
                if (commitFault == OrderFault.ThrowAfterAcknowledgedCommit) throw new IOException("simulated failure after acknowledging commit");
                return value;
            })));
    }

    private sealed class TransactionLease(SqliteTransaction transaction)
    {
        public SqliteTransaction Transaction { get; } = transaction;
        public bool CommitAcknowledged { get; set; }
    }

    private static Effect<int, WorkflowError, PrototypeEnvironment> InsertOrder(SqliteTransaction transaction, Order order)
        => Effect<PrototypeEnvironment>.AccessAsync<int>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            // P3: Passing the environment does not prove that each command joins the intended transaction.
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO orders(id, amount_cents) VALUES ($id, $amount)";
            command.Parameters.AddWithValue("$id", order.Id);
            command.Parameters.AddWithValue("$amount", order.AmountCents);
            return await command.ExecuteNonQueryAsync(token);
        });

    private static Effect<int, WorkflowError, PrototypeEnvironment> InsertOutbox(SqliteTransaction transaction, Order order)
        => Effect<PrototypeEnvironment>.AccessAsync<int>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO outbox(event_id, payload, published) VALUES ($id, $payload, 0)";
            command.Parameters.AddWithValue("$id", $"{order.Id}:created");
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new OrderPlaced(order.Id, order.AmountCents)));
            return await command.ExecuteNonQueryAsync(token);
        });

    private static Effect<Message[], Never, PrototypeEnvironment> ReadPending()
        => Effect<PrototypeEnvironment>.AccessAsync<Message[]>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            command.CommandText = "SELECT event_id, payload FROM outbox WHERE published = 0 ORDER BY event_id";
            await using var reader = await command.ExecuteReaderAsync(token);
            var messages = new List<Message>();
            while (await reader.ReadAsync(token)) messages.Add(new(reader.GetString(0), reader.GetString(1)));
            return messages.ToArray();
        });

    private static Effect<Unit, Never, PrototypeEnvironment> MarkPublished(string eventId)
        => Effect<PrototypeEnvironment>.AccessAsync<Unit>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            command.CommandText = "UPDATE outbox SET published = 1 WHERE event_id = $id AND published = 0";
            command.Parameters.AddWithValue("$id", eventId);
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Expected one pending outbox row.");
            return Unit.Value;
        });

    private static Effect<int, WorkflowError, PrototypeEnvironment> RecordInbox(SqliteTransaction transaction, string eventId)
        => Effect<PrototypeEnvironment>.AccessAsync<int>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO inbox(event_id) VALUES ($id) ON CONFLICT(event_id) DO NOTHING";
            command.Parameters.AddWithValue("$id", eventId);
            return await command.ExecuteNonQueryAsync(token);
        });

    private static Effect<int, WorkflowError, PrototypeEnvironment> ApplyOrder(SqliteTransaction transaction, OrderPlaced order)
        => Effect<PrototypeEnvironment>.AccessAsync<int>(async (environment, token) =>
        {
            using var command = environment.Database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO handled_orders(order_id, applications) VALUES ($id, 1)
                ON CONFLICT(order_id) DO UPDATE SET applications = applications + 1
                """;
            command.Parameters.AddWithValue("$id", order.OrderId);
            return await command.ExecuteNonQueryAsync(token);
        });
}
