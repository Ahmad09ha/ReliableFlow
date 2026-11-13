# ReliableFlow operations and security runbook

This is an educational reference, not a managed production eventing platform.
Run the following **read-only SQL** queries against each relevant PostgreSQL
database for diagnosis; business and outbox tables reside in the producer's
database, inbox and receiving domain tables in the consumer's database.

## Detect delayed or stuck publication

```sql
SELECT COUNT(*) AS pending_count,
       MAX(now() - "CreatedAtUtc") AS oldest_pending_age,
       COUNT(*) FILTER (WHERE "AttemptCount" > 0) AS retrying_count
FROM reliable_outbox
WHERE "PublishedAtUtc" IS NULL;
```

```sql
SELECT "Id", "EventType", "AttemptCount", "NextAttemptAtUtc",
       "LeaseExpiresAtUtc", "LastError"
FROM reliable_outbox
WHERE "PublishedAtUtc" IS NULL
ORDER BY "CreatedAtUtc"
LIMIT 50;
```

Consider alerting when pending age exceeds the application's delivery
objective, when retries climb, or when a persistent set of messages never
reaches PublishedAtUtc. A relay can retry forever in this reference;
dead-lettering/pausing permanently failing *producer events* is intentionally
left to the host.

## Consumer-side diagnostics

```sql
SELECT "ConsumerName", COUNT(*) AS processed
FROM reliable_inbox
GROUP BY "ConsumerName"
ORDER BY processed DESC;
```

The RabbitMQ demo exposes a management UI at http://localhost:15672
(demo-only credentials in compose.yaml) for inspecting queue and dead-letter
depth. The development-only `/demo/consumer-metrics` endpoint counts
processed and duplicate deliveries **in process memory**; these counters
reset on service restart and are **not** operational metrics.

## Retention and replay safety

- Never delete an **unpublished** outbox row automatically to reclaim disk:
  doing so discards the intent to publish and can lose business events.
- Published outbox rows can be pruned **only after** the organization's
  audit, retention and replay window expires. Deletion must use bounded
  batches and be monitored.
- An inbox marker protects a given `(ConsumerName, MessageId)` only as long
  as it is retained. Deleting inbox markers too early permits old broker
  redeliveries or manual replays to repeat business writes.
- There is **no built-in cleanup job** in this package; the host owns its
  retention policy. Don't copy a sample SQL DELETE into production without
  deciding the maximum replay horizon.
- Replay previously published events with their **original MessageId** if
  the business effect must remain idempotent. A new ID represents a distinct
  delivery identity.

## Security boundaries

- `compose.yaml` is **local development only**: fixed demo credentials,
  development routes, unsecured HTTP and bound loopback host ports.
- Do not expose `/orders`, `/outbox`, `/inbox`, `/stock`,
  `/reservations`, any `/demo/faults/*`, or RabbitMQ management endpoints to
  public networks without authentication, authorization, transport security,
  and rate controls.
- The transport adapter creates a connection per message for teaching
  clarity. Production broker adapters should use well-managed connections
  and channels, publisher confirms, backpressure, and durable routing.
- Treat incoming messages as **untrusted input**: validate type and schema,
  enforce producer authorization or authenticated broker access, and avoid
  logging sensitive message bodies or secrets in LastError.
- The Inbox is not a payment/email exactly-once solution. Calls to external
  systems need provider-specific idempotency keys, compensating actions or
  orchestration as appropriate.

## Hard-kill rehearsal (local only)

### Windows + WSL RabbitMQ alternative

On a Windows workstation with PostgreSQL 18 installed and RabbitMQ running
inside WSL Ubuntu, use the repository's isolated **Windows process**
experiment. It does not require Docker images or alter an existing
PostgreSQL installation's business databases.

```powershell
dotnet build ReliableFlow.slnx -c Release
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipHardKill
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipE2E
```

The first run checks real broker normal delivery, temporary publisher outage
and a broker-confirmed replay. The second run forcibly terminates the
`Ordering.Api` Windows process after a broker confirmation, waits for lease
recovery and proves the inventory effect occurs once. The test only uses
**Development** fault endpoints and temporarily sets the demo lease to
12 seconds; standard demo configuration uses 40 seconds.

All lab ports are loopback-only on Windows: Ordering 19080, Inventory 19081
and disposable PostgreSQL 15432. RabbitMQ uses a temporary demo virtual host
and temporary user in WSL. Each completed run removes only its own resources.
If a tool timeout interrupts cleanup, inspect the latest ignored
`.local-test/broker-lab-*.transcript.log` and confirm test ports have
closed before rerunning. This is still a local reference test rather than a
production security review.

### Docker Compose alternative

Start the demo and run the normal E2E test first:

```bash
docker compose down -v
docker compose up -d --build
python3 scripts/e2e.py
python3 scripts/e2e_hard_crash.py
```

The hard-crash script arms the publisher to **pause after RabbitMQ confirms
a message**, SIGKILLs the `ordering` container, starts it again, waits for
the DB lease to expire and checks that the Inventory consumer observed a
duplicate without changing stock twice. It intentionally disrupts the
ordering demo and should never run against production.

## PostgreSQL concurrency rehearsal (local only)

The stress harness requires a PostgreSQL admin connection to the local
Compose database and privileges to CREATE and DROP **only a newly generated
database** whose name starts with `rfstress_`. It never drops the original
`postgres` database and refuses non-loopback destinations.

```bash
export RF_PG_ADMIN_CONNECTION='Host=127.0.0.1;Port=15432;Database=postgres;Username=demo;Password=demo_only'
dotnet run --project tests/ReliableFlow.PostgresStress -c Release
```

Tests cover identical-message concurrency, distinct-message atomic updates,
competing dispatcher leases and expiration/reclaim. The harness cleans up
its own temporary database even if an assertion fails.

If PostgreSQL 18 is already installed on Windows but Docker is unavailable,
run the alternative isolated local cluster harness from PowerShell:

```powershell
./scripts/Run-LocalPostgresStress.ps1
```

It creates a disposable PostgreSQL data directory under ignored
`.local-test/postgres`, listens **only on 127.0.0.1:15433**, executes the same
stress suite and shuts the test server down. It does not touch an existing
Windows PostgreSQL service or its databases.

## Production-adoption checklist

1. Replace demo startup `EnsureCreatedAsync` with reviewed EF migrations.
2. Configure proper credentials, TLS, authorization and least-privilege roles.
3. Define aggregate ordering/version requirements; this package does not
   guarantee event ordering.
4. Establish monitoring for oldest pending outbox row, retry rate, lease
   staleness, queue depth, DLQ activity and consumer failures.
5. Define retention and disaster-recovery/replay policy before creating jobs.
6. Run stress, failover and SIGKILL/lease-expiry tests on the actual provider.
7. Review externally visible effects, especially payment and email side
   effects, independently of the local database transaction.

See [Architecture](ARCHITECTURE.md) and [Verification](VERIFICATION.md).
