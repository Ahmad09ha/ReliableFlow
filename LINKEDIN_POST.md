# LinkedIn post — publish only AFTER uploading the repository

Your API returned 201 Created. The order exists. Inventory never received the event.

That's the dual-write problem — and a successful database transaction doesn't solve it.

Imagine an ASP.NET Core Ordering API:
1. Commit an Order using EF Core.
2. Publish OrderPlaced to RabbitMQ.

Crash after step 1? The order exists, but no event is delivered.
Publish first? The consumer might act on an order that never commits.

I built ReliableFlow for .NET: a reusable EF Core library and two sample services.

Here's how it works.

1) TRANSACTIONAL OUTBOX — protect the producer

Instead of publishing from the HTTP request, the API writes the Order and an OutboxMessage using the SAME DbContext and transaction.

db.Orders.Add(order);
db.EnqueueOutbox("orders.placed.v1", eventPayload);
await db.SaveChangesAsync();

Either both records commit, or neither does. The HTTP request no longer depends on RabbitMQ being available.

2) OUTBOX DISPATCHER — deliver with retries

A background worker polls unsent messages, atomically claims each row with a database lease, then publishes through IOutboxTransport.

RabbitMQ publisher confirms acknowledge accepted messages. Failures trigger exponential-backoff retries; lease tokens prevent stale dispatchers from marking messages as sent.

But there's another failure window: RabbitMQ confirms the event, and the worker crashes BEFORE marking it published. The retry can publish the same event again.

That's at-least-once delivery, not exactly-once delivery.

3) IDEMPOTENT INBOX — protect the consumer

The Inventory Service uses a stable MessageId and a UNIQUE (ConsumerName, MessageId) database key.

The inbox marker and inventory changes commit in the SAME receiving database transaction. RabbitMQ is ACKed only after successful processing or confirmed duplication.

A simple 'already processed?' query isn't sufficient: two consumers can both pass that check concurrently. The database constraint is what arbitrates the race.

Result: replaying the SAME message doesn't reserve stock twice.

Limits: external payments/emails need their own idempotency mechanisms. Event ordering isn't guaranteed. Different message IDs for one business operation need business-key protection.

WHAT I VERIFIED
• .NET 10 Release build: passed
• 9/9 SQLite-backed automated tests: passed
• PostgreSQL stress: 32 competing consumers / 16 dispatchers passed
• RabbitMQ delivery, retries, duplicates and real process-kill recovery: passed
• NuGet package built locally; dependency audit found no known vulnerable packages
• Linux Docker Compose E2E and hosted GitHub Actions: not yet verified

Repository: https://github.com/Ahmad09ha/ReliableFlow

How do you handle expired leases, duplicate publishes, and replay-safe consumers in production?

#DotNet #DistributedSystems #EventDrivenArchitecture #EFCore #BackendEngineering

---

Publication note (NOT part of the LinkedIn post): verify that the GitHub repository is pushed and CI results are accurate before publishing. Local .NET build, 9 tests, PostgreSQL stress and Windows/WSL RabbitMQ end-to-end + hard-kill tests passed in Rider. Linux Docker Compose and hosted GitHub Actions remain unverified until their respective runs succeed. Do not claim CI success until the workflow runs successfully.
