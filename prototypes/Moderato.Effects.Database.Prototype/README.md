# Core-only order and Outbox prototype

This is a disposable application example on `prototype/409-outbox-flow`, for [#409](https://github.com/SingUp009/Moderato.Effects/issues/409). The question is: **where does correct application code become repetitive or difficult enough that a database integration package should help?** These are application functions, not proposed package APIs. Keep the prototype branch separate from product `main`.

Run from the repository root with the SDK selected by `global.json`:

```bash
dotnet run --project prototypes/Moderato.Effects.Database.Prototype -c Release
```

The program exits nonzero if an observation is wrong. It creates fresh in-memory SQLite databases, sends only to an in-memory stub, and prints the database and delivery state. It does not connect to PostgreSQL or Kafka. No credentials or external service are required.

## The application flow

`Workflow.PlaceOrder` composes two writes with the existing `Scope`, `Resource`, `AndThen` and `RunExitAsync` APIs:

1. Begin a transaction on the application's borrowed connection.
2. Write an order and its stable-ID Outbox message in that transaction.
3. Wait for the inner business scope, including its releases.
4. Commit in the effect body. Rollback attempts and disposal belong to release.

`Workflow.DispatchPending` reads materialized pending messages, sends each one, and marks it published only after the send returns successfully. There is no database transaction held around the network send.

The stub first accepts a message and then pauses its local completion. The caller cancels that publisher. The broker has one delivery, while the Outbox row is still pending. A fresh effect and execution environment use the same database and broker, send the same persisted event ID again, and mark the row. They do not reuse a remembered publisher cursor.

`Workflow.Consume` uses a separate receiver database, inserting the delivery ID into Inbox and updating the receiving application's table in one transaction. A duplicate ID skips the second business mutation. The mutation intentionally increments a counter if executed twice, so the self-check detects broken deduplication. The sender and receiver transactions are separate; no cross-database atomicity is implied.

```text
placed:       orders=1  pending=1  broker-deliveries=0
interrupted:            pending=1  broker-deliveries=1  Exit=Interrupted
restarted:              pending=0  broker-deliveries=2
consumed:                         inbox=1  business-applications=1
```

The application owns the connection until the scenario ends. Each transaction is owned by its Resource; commands and readers are disposed before their effect completes. The workflow never disposes the borrowed connection.

## Failure observations

Each control uses a separate fresh database. The `OrderFault` cases and broker pause are fixture controls, not business API options.

| Injected boundary | Local outcome | Orders / Outbox | Cleanup |
|---|---|---|---|
| Business rejection after the order write | Err | 0 / 0 | Rollback and dispose attempted |
| Defect after both writes | Die | 0 / 0 | Rollback and dispose attempted |
| Inner scope release fails after its body succeeds | Die | 0 / 0 | Commit skipped; rollback and dispose attempted |
| Caller cancels while waiting before commit | Interrupted | 0 / 0 | Rollback and dispose attempted |
| Commit succeeds, then the simulated reply is lost | Die(IOException) | 1 / 1 | Rollback attempted and fails; dispose still attempted |
| Commit is acknowledged, then a later step fails | Die(IOException) | 1 / 1 | No second rollback; dispose attempted |

The lost-reply fixture deliberately throws immediately after a successful SQLite `CommitAsync`, before the application records its acknowledgment. The fixture can inspect the physical rows; a remote client losing a real commit response may not know that state. Failed rollback is retained as sequential suppressed context while the lost-reply exception remains primary. A local Die is not evidence that data was rolled back.

This prototype leaves that case as Die, rather than deciding a product `CommitOutcomeUnknown` error type. It neither retries a transaction automatically nor treats rollback as a way to undo an already completed commit.

A separate receiver control inserts its Inbox identity and then encounters malformed JSON. The failed transaction leaves no Inbox marker or business mutation. Retrying the same identity with a corrected payload succeeds. This checks the atomicity of receiver deduplication with handling, not just the successful duplicate path.

As a negative control, temporarily removing the `firstDelivery == 0` guard makes the receiver business-state assertion fail on duplicate delivery. The runnable source retains the guard.

## Friction to discuss with the package author

The short `P1`–`P5` comments in `Workflow.cs` mark the corresponding code. These are observed application obligations, not a list of functions the database package must automatically expose.

| Mark | What the application currently has to get right | Layer / possible ownership |
|---|---|---|
| P1 | Wrap acquisition and the complete inner scope; commit in the body; track acknowledgment separately from local Exit; choose how to represent an unknown outcome | Relational/provider boundary. A database package could own transaction composition and expose the uncertainty contract. The public error shape is undecided |
| P2 | Register disposal before attempting rollback so both are tried; preserve the original failure and cleanup context; explicitly use an uncancelable rollback token inside the nested cleanup Scope | Core composition plus provider cleanup. The outer release is detached from caller cancellation, but the nested Scope has its own token. Testing `CanBeCanceled == false` inside that Scope was an incorrect initial prototype assumption |
| P3 | Repeat the Effect/async-command wrapper, dispose commands/readers, pass cancellation, set every command's Transaction, and use a connection from the matching environment | ADO.NET ownership/enlistment and adapter ergonomics. Core does not statically prove that a command uses this transaction or that the resource never escapes |
| P4 | Keep a persisted message identity, distinguish acceptance from observed send completion, and choose when to mark sent | Messaging/Outbox policy. A plain database adapter should not silently decide delivery semantics |
| P5 | Make the receiver's Inbox insert and database mutation atomic; decide the scope of idempotency | Receiving application/Outbox integration. This does not make external HTTP calls or email idempotent |

`WorkflowError` contains one application rejection. Ordinary provider exceptions still become Die under the existing Core rules. Expected SQL-error classification, provider error identities, unknown commit representation and retry policy remain package design questions, not a blanket conversion of every exception into E.

## Limits and the EF Core comparison

- “Restart” means a new publisher invocation and environment in the same process. The database connection and stub broker remain alive. This is not an OS-crash or durable-storage test.
- The stub is an in-memory acceptance log with deterministic pause/cancellation. It does not model Kafka transactions, offsets, partitions, acknowledgments, fencing or durable delivery.
- The run uses one connection per sender/receiver and one publisher sequentially. There is no multi-worker leasing, contention, ordering, retention or poison-message policy. Do not use this code as a production Outbox implementation.
- Both transaction bodies in this example are `Scope.Gen` blocks. A product combinator must establish the child/release boundary for its supported body shapes; this private helper does not demonstrate that arbitrary callbacks cannot leave work in another scope.
- Inbox protects only the receiving database mutation executed in its transaction. It is not an exactly-once guarantee for arbitrary side effects.
- The provider is Microsoft.Data.Sqlite 10.0.9 with the SQLitePCLRaw native library pinned to 3.53.3, matching the earlier local SQLite control. These are prototype dependencies, not selected product package versions.
- The source targets .NET 11 RC3. Async-shaped APIs do not establish asynchronous database I/O. No Native AOT, performance, real PostgreSQL, EF Core or Kafka result is claimed.
- No DbContext or EF execution strategy is exercised here. This example cannot establish whether an EF-specific package is needed. A narrow EF version of the same workflow would need to compare execution-strategy ownership and the relationship between SaveChanges and an existing transaction, including fresh per-attempt context/state.
- Borrowed/owned resources and concurrency limits also exist in ADO.NET/provider code. They are not exclusively EF concerns. The EF-specific work is connecting those rules to DbContext, its factory/DI ownership and its own execution model.

The goal for the next iteration is to reduce the repeated transaction/adapter machinery while keeping the business steps, lifetime boundaries and uncertain outcomes visible. Do not freeze a generic database API or split an EF package solely from this SQLite example.
