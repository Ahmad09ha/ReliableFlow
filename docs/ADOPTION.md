# Adoption checklist — integrating the library into your own project

1. **Pick the right scope.** Use a relational EF Core provider with transactions and unique constraints. For systems using MongoDB, DynamoDB, or specialized transactional boundaries, build a provider-specific adapter rather than copy EF Core assumptions.
2. **Add the package.** `dotnet pack` the source project and install the local package or reference the `.csproj`. Keep library and host .NET / EF Core major versions compatible.
3. **Configure model.** `modelBuilder.AddReliableFlow()` on the producer and/or consumer `DbContext`. It registers both tables even if the host only uses one of them.
4. **Generate your EF Core migration.** Add and deploy migrations via `dotnet ef migrations add ...` and `dotnet ef database update ...` (or your established CI/CD migration pipeline). `EnsureCreatedAsync` is used *only* in the standalone demo and must not be your production schema strategy.
5. **Change event emission.** Replace `SaveChanges(); broker.Publish()` with `db.EnqueueOutbox("domain.action.v1", payload); SaveChanges();`. The business row and event row MUST share the same DB transaction.
6. **Implement the broker adapter.** Implement `IOutboxTransport`. Keep the same `message.Id` as the broker's `MessageId` across every retry. Return only after broker persistence/confirmation. Provide authentication, TLS, connection reuse, telemetry, topology, and routing per production constraints.
7. **Run the dispatcher.** Register `AddReliableOutbox<YourDbContext>()` once per producer service; confirm your app has a registered `IOutboxTransport`. Tune retry and lease based on throughput and broker outage expectations.
8. **Integrate receivers.** Register `AddReliableInbox<YourReceivingDbContext>()`. Use the same DbContext inside `InboxProcessor.ProcessAsync` for database mutations. ACK after it returns, never before.
9. **Protect business keys.** Use unique business identifiers where *different* message IDs can refer to the same operation; don't confuse message dedup with business-command dedup.
10. **Handle external operations separately.** Use payment gateway idempotency keys, service API idempotency contracts, Sagas, or compensation as appropriate.
11. **Operations.** Instrument pending outbox age, retry/error counts, poison queue depth, oldest inbox marker, concurrency conflicts, message handling duration, and broker publish latency. Add safe retention/archival rules and replay/runbook.
12. **Test the actual provider.** Run multi-instance DB concurrency tests against your real database, not just in-memory stores. Inject broker outages and process crashes; test deployments and lease expiry.

## Versioning contracts

Use stable event names and explicit version suffixes (`inventory.reserved.v1`). Keep event payloads backward compatible; do not rely on .NET type names as a public wire contract. In the demo `Shared.Contracts.OrderPlacedV1` is a convenient compile-time representation; separate deployments may prefer schema registry / JSON schema.

## Production risk notes

- Network partitions and process restarts can produce **duplicate** publishes; this is part of the contract.
- No ordering per aggregate; a later event may overtake an earlier one.
- Inbox markers have no automatic TTL/cleanup; keep them for the entire replay horizon.
- No standardized OpenTelemetry instrumentation, admin dashboard, or replay endpoint in the reusable library yet.
- The demo's RabbitMQ adapter uses one short-lived connection per message for readability. A production adapter should safely reuse channels/connections, handle topology and unroutable publishes, and implement backpressure.
- A row lease can expire while a slow publish is still in flight. The consumer's inbox is the final transactional-effect guard.
- `OutboxDispatcher` assumes a relational EF Core provider supporting `ExecuteUpdateAsync`. For non-relational EF Core providers, implement a provider-specific dispatcher.
- The sample's RabbitMQ connection credentials are demonstration-only and exposed only on localhost in Compose.
