# ReliableFlow verification — 2026-11-14

**Current status: LOCAL REFERENCE IMPLEMENTATION VERIFIED.** Build, unit tests, actual
PostgreSQL concurrency, local RabbitMQ end-to-end messaging, and real process
termination/recovery were exercised on the developer's Windows/Rider machine.

**Not verified:** Docker Compose full-stack images and hosted GitHub Actions have
not been executed successfully yet. The repository has not been pushed to GitHub.
This is not a production security audit or production-readiness certification.

## Verified checks

| Scenario | Evidence | Outcome |
| --- | --- | --- |
| .NET 10 Release compile | `dotnet build ReliableFlow.slnx -c Release --no-restore -v quiet` | **PASS, 0 warnings/0 errors** |
| SQLite-backed transaction and idempotency unit tests | `dotnet test tests/ReliableFlow.Tests/ReliableFlow.Tests.csproj -c Release --no-build --no-restore` | **PASS, 9/9** |
| PostgreSQL, 32 simultaneous deliveries of one MessageId | `Run-LocalPostgresStress.ps1` (PostgreSQL 18) | **PASS: one Processed, 31 Duplicates, business effect once** |
| PostgreSQL, 24 different messages concurrently | Same stress harness | **PASS: 24 updates committed** |
| PostgreSQL, 16 outbox dispatchers competing for one message | Same stress harness | **PASS: one active lease/publish** |
| PostgreSQL expired-lease recovery | Same stress harness | **PASS: unexpired lease protected, expired lease reclaimed** |
| Local NuGet packaging and installation in independent .NET app | `dotnet pack ...`, `Run-PackageSmoke.ps1` | **PASS** |
| NuGet dependency advisory check | `dotnet list tests/ReliableFlow.Tests/ReliableFlow.Tests.csproj package --vulnerable --include-transitive --no-restore` | **PASS on 2026-11-14: no vulnerable packages reported from nuget.org** |
| Repository/XML/JSON structure | `python scripts/check_repository.py` | **PASS** |
| Real PostgreSQL + RabbitMQ normal event delivery | `Run-LocalBrokerLab.ps1 -SkipBuild -SkipHardKill` | **PASS: order received and stock reserved** |
| Real RabbitMQ publisher unavailable, then retry succeeds | Same local broker lab | **PASS: retry recorded, event delivered after recovery** |
| Publish confirmed, then adapter fault, then duplicate event | Same local broker lab | **PASS: consumer registered duplicate; one business effect** |
| **Real process kill after RabbitMQ confirms** | `Run-LocalBrokerLab.ps1 -SkipBuild -SkipE2E` | **PASS: process terminated, restarted, lease expired, event re-published, duplicate detected** |
| **Final hard-kill stock/reservation assertion** | Same local broker lab, 2026-11-14 | **PASS: stock 100 → 99, exactly one reservation** |

The hard-crash scenario above uses **Windows Stop-Process -Force** against the
Ordering process (an actual process termination), not a simulated C# exception.
The Linux container variant uses SIGKILL and remains unverified.

For local broker experiments we used PostgreSQL 18 in a disposable local
test cluster and RabbitMQ in WSL Ubuntu with a disposable, dedicated virtual
host and user. The test services listened only on localhost ports
19080/19081; the PostgreSQL test cluster used port 15432. The lab
temporarily selected `ReliableFlow:DemoLeaseSeconds=12` in **Development**
to finish lease-expiration/replay verification rapidly. Normal sample
settings keep the 40-second lease.

The current machine has .NET SDK 10.0.404. `global.json` retains a
10.0.100 minimum and uses `rollForward=latestFeature` to accept newer
compatible .NET 10 feature bands.

The local process transcripts from these tests were captured in
`.local-test/broker-lab-*.transcript.log` and are ignored by Git. Local
ephemeral test databases are cleaned by the lab script after a completed run.
User business databases and the existing PostgreSQL server on port 5432
were not modified.

## Still not verified (do not claim completed)

| Area | Planned command / scope | Disposition |
| --- | --- | --- |
| Full Docker Compose build/start on Linux, including images | `docker compose up -d --build` | **UNVERIFIED** (registry downloads stalled locally) |
| Docker-specific E2E and POSIX SIGKILL | `python3 scripts/e2e.py` and `scripts/e2e_hard_crash.py` | **UNVERIFIED on Docker** |
| GitHub-hosted Actions | `.github/workflows/ci.yml` | **NOT RUN** — repository not yet pushed |
| Cross-node failover/network partition | Multi-host cluster | **DEFERRED**, outside reference v0.1 |
| Production schema migrations, authentication, telemetry, retention and alerts | Hosting application's work | **OUT OF SCOPE for demo**, mandatory for production adoption |
| Exactly-once delivery including external side effects | Not a library guarantee | **NOT CLAIMED** |
| Global/per-aggregate ordering | Additional ordered processing design | **NOT CLAIMED** |

## How to reproduce

```powershell
dotnet restore ReliableFlow.slnx
dotnet build ReliableFlow.slnx -c Release --no-restore
dotnet test ReliableFlow.slnx -c Release --no-build
dotnet pack src/ReliableFlow.EntityFrameworkCore/ReliableFlow.EntityFrameworkCore.csproj -c Release -o artifacts
./scripts/Run-PackageSmoke.ps1
./scripts/Run-LocalPostgresStress.ps1
```

Windows/Rider local broker lab with PostgreSQL 18 and RabbitMQ in WSL:

```powershell
# Starts disposable local databases and RabbitMQ demo virtual host:
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipHardKill

# Exercises real process termination and recovery in a separate isolated run:
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipE2E
```

The scripts intentionally stop development demo processes and clean
only their own temporary resources. A timeout may occur in Rider after
all PASS statements while cleanup is completing; inspect the most recent
`.local-test/broker-lab-*.transcript.log` and confirm test ports have
closed before inferring a fully cleaned run.

When Docker images are available:

```bash
docker compose down -v
docker compose up -d --build
python3 scripts/e2e.py
python3 scripts/e2e_hard_crash.py
docker compose down -v
```

**Do not run destructive Docker tests against non-demo Compose volumes.**
See [Operations](OPERATIONS.md) and [Adoption](ADOPTION.md) for limitations
and the additional safeguards required for production.
