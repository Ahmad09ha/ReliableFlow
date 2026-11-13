# ReliableFlow.EntityFrameworkCore

A **NuGet-packable, broker-agnostic** infrastructure library for the transactional outbox and idempotent inbox patterns. It is a sample-quality library, not an audited production release.

Install the package after publishing it to your own NuGet feed, or reference its project source. See the repository root README for integration instructions and important guarantees/limits.

**Producer**: `modelBuilder.AddReliableFlow()`; `db.EnqueueOutbox("orders.placed.v1", payload)`; commit changes using the *same* DbContext transaction; register `AddReliableOutbox<TDbContext>()`; supply `IOutboxTransport` with broker confirms.

**Consumer**: `modelBuilder.AddReliableFlow()`; `AddReliableInbox<TDbContext>()`; call `InboxProcessor<TDbContext>.ProcessAsync(db, consumerName, messageId, (sameDb, ct) => ...)` and ack only after success/duplicate.

The library guarantees neither globally exactly-once delivery nor transactional safety for external HTTP calls, emails or payment gateways. Consumers must implement business-key deduplication where appropriate.
