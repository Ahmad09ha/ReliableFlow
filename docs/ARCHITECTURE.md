# ReliableFlow Architecture

## Module boundary

The library owns **outbox/inbox persistence primitives and an EF Core polling dispatcher**. It does not own business events, external payment/email idempotency, authorization, queue topology, or broker adapters. The demo intentionally provides a RabbitMQ adapter as sample code, rather than embedding transport dependencies inside the reusable package.

## Atomicity invariants

- **INV-01**: One logical domain write and its integration event(s) are committed or rolled back together using the **same DbContext transaction**.
- **INV-02**: An outbox record is marked published only after a confirmed transport acceptance.
- **INV-03**: A `(ConsumerName, MessageId)` marker and all database business changes it protects are committed or rolled back in the **same receiving database transaction**.
- **INV-04**: The receiving RabbitMQ delivery is ACKed only after the inbox processor has returned success or a known duplicate.
- **INV-05**: Unique business operations may need a separate business-level key: in the inventory demo `Reservation.OrderId` is unique.
- **INV-06**: A successful broker publish may occur multiple times. Do not assume message delivery order or exactly-once delivery.

## Important failure windows

**Producer before database commit:** neither the domain row nor outbox row persists (same local transaction).

**Producer after commit / before publish:** the durable outbox record remains. Relay eventually publishes after recovery.

**Relay after broker confirmation / before marker update:** the event may be sent again. This is expected. The stable outbox `Id` becomes the broker `MessageId` every time.

**Two relay instances:** both may discover the same candidate, but only one wins the conditional SQL claim at a time. Lease expiry allows at-least-once overlap during unusually slow transport operations, not duplicate business effects.

**Consumer reconnection:** the demo RabbitMQ client enables automatic connection/topology recovery; inbound deliveries may be redelivered after reconnect and are deduplicated by the inbox.

**Consumer before business commit:** inbox marker and business effects roll back together, and RabbitMQ may redeliver.

**Consumer after business commit / before ACK:** RabbitMQ redelivers; inbox returns `Duplicate`, handler is not invoked again, then ACK succeeds.

**Concurrent duplicate deliveries:** unique inbox composite primary key arbitrates transactions. Duplicate insert throws/rolls back; processor checks for the committed marker after rollback, then returns `Duplicate`.

**Different message IDs for one order:** the inventory service also uses a unique `OrderId` reservation key and checks for existing order processing. Database updates on stock are performed with `ExecuteUpdateAsync`, avoiding a lost-update read-modify-write race.

## Data model

`reliable_outbox`: `Id PK`, `EventType`, `Payload`, `CreatedAtUtc`, `NextAttemptAtUtc`, `PublishedAtUtc nullable`, `AttemptCount`, `LeaseId nullable`, `LeaseExpiresAtUtc nullable`, `LastError nullable`.

`reliable_inbox`: `ConsumerName + MessageId PK`, `ProcessedAtUtc`.

## Polling delivery mechanics

1. Scan un-published messages due for retry and not leased (or with expired lease).
2. Try an atomic `ExecuteUpdateAsync` conditional claim with a newly generated `LeaseId` and lease expiry.
3. Load claimed message; publish through `IOutboxTransport`.
4. After broker confirmation, set `PublishedAtUtc` *only if the same LeaseId still owns the record*.
5. On failure, log and schedule bounded exponential backoff; release the lease if still owned.

The caller configures `BatchSize`, `PollInterval`, `LeaseDuration`, `BaseRetryDelay`, and `MaxRetryDelay` through `AddReliableOutbox<TDbContext>`.

## Threat / concurrency discussion

- Message IDs originate at the producer and are stable across retries; if processing untrusted external messages, authentication and message authorization must be implemented at the receiving boundary.
- Different consumers have distinct inbox namespaces; the same message can legitimately be processed by multiple business handlers.
- A failing handler must never ACK as a success. Invalid messages go to the DLQ; transient failures get broker-level redelivery.
- Leases are not distributed transactions: duplicates remain possible and are specifically addressed by the inbox.
- Outbox storage grows over time; add safe retention jobs only after settling maximum retry/replay windows.
- Unordered events can be harmful for state transitions; add per-aggregate sequencing/version checks when event order matters.
- External HTTP calls, non-transactional caches, emailing, and payments are outside this library's transaction boundary.

## Verified vs not verified

See [VERIFICATION.md](VERIFICATION.md). This repository includes a CI workflow that can validate unit and E2E behavior **after** it is pushed. Source creation is not evidence of a successful build/run.

## Primary references

- [AWS Prescriptive Guidance — Transactional outbox pattern](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html)
- [Azure Architecture Center — Idempotent Consumer pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/idempotent-consumer)
- [RabbitMQ — Reliable publishing with publisher confirms (.NET)](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet)
- [RabbitMQ — .NET client API guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
- [Microservices.io — Transactional Outbox](https://microservices.io/patterns/data/transactional-outbox)

Implementation choices in this repository are educational examples, not a claim of certification by these organizations.
