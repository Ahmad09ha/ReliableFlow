# Publishing ReliableFlow to GitHub

The repository is currently a **local Git repository initialized on `main`**
at `D:\Works\PrivateWorks\ReliableFlow`. No commit or push has been
performed by the automation. **Do not publish claims of test success that
are not confirmed in [Verification](VERIFICATION.md).**

## 1. Run readiness checks

From PowerShell in the project folder:

```powershell
dotnet restore ReliableFlow.slnx
dotnet build ReliableFlow.slnx -c Release --no-restore
dotnet test ReliableFlow.slnx -c Release --no-build
dotnet pack src/ReliableFlow.EntityFrameworkCore/ReliableFlow.EntityFrameworkCore.csproj -c Release -o artifacts
./scripts/Run-PackageSmoke.ps1
./scripts/Run-LocalPostgresStress.ps1
python scripts/check_repository.py
```

The local PostgreSQL script uses an isolated disposable test cluster on port
15433. It does not edit the user's existing PostgreSQL service.

The local RabbitMQ failure journey has also passed on Windows + WSL
(including a real process kill), independently of Docker. See
`docs/VERIFICATION.md` and `docs/OPERATIONS.md`. Repeat the following
when needed:

```powershell
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipHardKill
./scripts/Run-LocalBrokerLab.ps1 -SkipBuild -SkipE2E
```

## 2. Validate the complete broker journey

With Docker Engine/Compose available:

```bash
docker compose down -v
docker compose up -d --build
python3 scripts/e2e.py
python3 scripts/e2e_hard_crash.py
docker compose down -v
```

**Warning:** `docker compose down -v` destroys only this demo's named
Compose volumes. Never run it against a Compose project with data you care
about. The real hard-crash test SIGKILLs the demo's Ordering container and
then restarts it. See [Operations](OPERATIONS.md).

The GitHub Actions workflow repeats these checks on Linux after the push.
Do not mark CI PASS until GitHub displays a successful run.

## 3. Confirm metadata and public content

Before public publication:

- Verify the canonical repository URL is
  `https://github.com/Ahmad09ha/ReliableFlow` in the README, package metadata,
  and LinkedIn post.
- Confirm you want the `MIT` license, repository title, author and
  package description. The current package version is `0.1.0`.
- Keep demo credentials and fault endpoints strictly local. Do not publish
  private credentials, production connection strings, `.env` files, or
  local `.local-test` data.
- Inspect `git status --short` and `git diff --cached --stat` before committing.
- Decide whether to publish as a **reference implementation**; it is not
  advertised as a fully audited, drop-in production solution.

## 4. Create repository and push — only when you choose to

From the existing local repository:

```powershell
git add .
git status --short
git commit -m "feat: publish ReliableFlow outbox and inbox reference"
git branch -M main
```

Option A, if authenticated with the GitHub CLI:

```powershell
git remote add origin https://github.com/Ahmad09ha/ReliableFlow.git
git push -u origin main
```

If the `origin` remote already exists, verify it rather than adding it again:

```powershell
git remote -v
git push -u origin main
```

If `git config user.name` / `git config user.email` are not set, configure
your own identity first. Never invent identity values.

## 5. Check hosted CI before the LinkedIn post

Visit the Actions tab. Confirm that the unit/build/package consumer job and
the Docker E2E/concurrency/hard-kill job have succeeded, or document
any platform-specific failure with its evidence before making public claims.
Then use the verified repository URL in `LINKEDIN_POST.md` and attach
the README architecture visual.

Suggested topics: `dotnet`, `ef-core`, `distributed-systems`,
`transactional-outbox`, `idempotency`, `rabbitmq`, `postgresql`.
