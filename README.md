# Web Project Asset & Handover Portal

A multi-tenant agency/client portal for managing web projects: organizations and users, projects and members, invitations, milestones, notes, documents/assets, environments, deployments, and deployment history/logs/status.

## Stack

- **Backend:** C# 14, .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10, SQL Server
- **Frontend:** React 19, TypeScript, Vite, React Router, TanStack Query, Tailwind
- **Testing:** xUnit v3, Vitest, Playwright

## Repository layout

```text
backend/    .NET solution (Portal.Api, Portal.Domain, Portal.Infrastructure, tests/Portal.Tests)
frontend/   React + Vite app
e2e/        Playwright end-to-end tests
scripts/    PowerShell scripts for Windows development
.github/    CI workflows
```

## What it does

- **Organizations and users** with cookie sign-in, roles (admin, member, client), and invitations.
- **Projects** with members and per-project roles; **milestones**, **notes** (internal or client-visible), and **documents/assets** (validated uploads, authorized downloads).
- **Realtime sync** between tabs and users (SignalR), an **offline change queue** (IndexedDB) that survives refreshes and keeps its order, and a **conflict dialog** when two people edit the same thing.
- **Deploy on Our Platform:** connect a Git repository to a project environment and deploy it as a live static site, with status, logs, health checks, redeploy, and rollback.

Tenant isolation and authorization are enforced on the server for every request; the UI only reflects them.

## How it fits together

| Concern | How |
|---|---|
| Tenancy | Every tenant-owned row has an `OrganizationId`; an EF Core global query filter and a `SaveChanges` guard reject anything outside the caller's organization. The tenant comes only from the server-issued auth cookie. |
| Concurrency | Every entity has a SQL Server `rowversion`, sent as `ETag` and required back in `If-Match`. A stale version is `409 Conflict` with the current state; a missing one is `428`. There is no last-write-wins. |
| Idempotency | Mutations carry an `Idempotency-Key`. A retry replays the stored response instead of running twice. |
| Realtime | After each change the server pushes a content-free event to exactly the people who may see the project; clients re-fetch through the normal authorized API. |
| Offline | Every UI change is written to an ordered IndexedDB outbox first. Each tab owns its own entries; a surviving tab adopts a closed tab's (Web Locks). |
| Files | `IFileStorage` (local disk). Random keys, extension allow-list plus content check, size limit enforced while streaming, downloads as sandboxed attachments. |
| Deployments | SQL-backed job queue + hosted worker. `IDeploymentProvider` (platform static-site provider) builds in an isolated sandbox, stores immutable releases, switches them atomically, and only reports success after a real HTTP health check. |
| Housekeeping | A hosted worker runs every 6 hours (first pass a minute after start-up) and removes idempotency records older than 30 days, finished job-queue rows older than 30 days, stored files no document points to (only once they are 24 hours old, at most 500 per pass), and half-written uploads. Settings are under `Cleanup__*` (see below). |

## Local development (Windows)

Prerequisites: .NET 10 SDK, Node.js 22+, git, and SQL Server running on `localhost`.

```powershell
./scripts/setup.ps1     # checks prerequisites, creates .env, restores packages
./scripts/dev.ps1       # starts the API (http://localhost:5080) and Vite (http://localhost:5173)
./scripts/verify.ps1    # runs every check that CI runs
./scripts/e2e.ps1       # builds, then runs the Playwright tests (e2e/two-tab.spec.ts) against a fresh database
./scripts/publish.ps1   # builds a deployable folder for your server
./scripts/migrate.ps1   # applies database migrations (production never migrates automatically)
```

### Running from Visual Studio

Needs Visual Studio 2026 (or 2022 17.14+, for `.slnx` and .NET 10) and SQL Server on `localhost`.

1. Open `backend/Portal.slnx`, set **Portal.Api** as the startup project, and press F5 (profile `http`, `http://localhost:5080`). It uses the connection string in `appsettings.Development.json` (`PortalDev` on `localhost`, Windows sign-in) and creates or updates the database on start-up.
2. Visual Studio does not start the frontend. In a terminal: `cd frontend`, `npm ci` (first time), `npm run dev`, then open <http://localhost:5173>. It proxies `/api` and `/hubs` to port 5080.
3. To use another SQL Server, set the `ConnectionStrings__Default` environment variable (or a user-secrets value) instead of editing the file.

"Deploy on Our Platform" also needs `Deploy__BaseDomain` and, on Windows, `Deploy__Sandbox=local-unsafe` (development only); everything else works without them.

Running on your own server: see [`deploy/README.md`](deploy/README.md).

### Tests

- `dotnet test` (xUnit): unit tests run anywhere; integration tests (`Category=Integration`) need SQL Server and read `PORTAL_TEST_CONNECTION` (a connection string without a database name; each test class gets its own database). Deployment tests run real `git` and `node` builds.
- `npm test` in `frontend/` (Vitest): outbox, HTTP layer, and overlay logic.
- `e2e/two-tab.spec.ts` (Playwright): realtime between two tabs in under a second, offline changes surviving refresh and reconnect in order, concurrent edits producing a conflict dialog, and per-tab outbox isolation. It runs against the production build on a real database, in CI and via `scripts/e2e.ps1`.

### Housekeeping settings

All optional (defaults shown; durations are `d.hh:mm:ss`):

```text
Cleanup__Enabled=true
Cleanup__Interval=06:00:00
Cleanup__InitialDelay=00:01:00
Cleanup__IdempotencyRetention=30.00:00:00
Cleanup__JobRetention=30.00:00:00
Cleanup__OrphanGracePeriod=1.00:00:00     # never less than one hour
Cleanup__MaxOrphansPerRun=500             # safety net if the app is pointed at the wrong database
```

## Workflow

- `main` only receives changes through reviewed pull requests (the initial bootstrap commit is the single exception).
- All work happens on `feature/*`, `fix/*`, `test/*`, `chore/*` branches.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
- CI must pass before merge, and nothing is merged without the owner's approval.

## Configuration

Configuration comes from environment variables, loaded from a local `.env` during development. Only `.env.example` is committed. Secrets, credentials, tokens, and production infrastructure values are never committed.

## Decisions log

Decisions made by the project owner.

| Date | Area | Decision |
|---|---|---|
| 2026-09-30 | Tooling | **Docker is not used** anywhere: development, testing, CI, build isolation, or deployment. |
| 2026-09-30 | Architecture | **Modular monolith.** One deployable API. Background workers run as hosted services inside the same codebase. |
| 2026-09-30 | Multi-tenancy | **Shared database and schema with a `TenantId` column**, enforced server-side by EF Core global query filters plus explicit authorization checks. |
| 2026-09-30 | Authentication | **ASP.NET Core Identity with cookie authentication** (`HttpOnly`, `Secure`, `SameSite`, antiforgery). |
| 2026-09-30 | Realtime | **SignalR.** No backplane is needed while running on a single server. |
| 2026-09-30 | Queues / workers | **SQL Server-backed job table** processed by a hosted `BackgroundService`. No external broker. |
| 2026-09-30 | File storage | **Local disk behind an `IFileStorage` abstraction.** |
| 2026-09-30 | Database | **SQL Server on `localhost`** for development. Tests use a real SQL Server instance (no in-memory substitutes for isolation and concurrency tests). |
| 2026-09-30 | Deployment isolation | **Same host, restricted OS user plus cgroup limits** for client builds, behind an `IDeploymentProvider` abstraction. Only suitable for client projects the operator trusts. |
| 2026-09-30 | Hosting | Linux host is assumed (cgroups). Exact reverse proxy and process manager are still to be decided. |
| 2026-09-30 | Deployments: scope | **Static sites only** in the first version (a build that produces a folder with `index.html`). No long-running server apps yet. |
| 2026-09-30 | Deployments: source | **Git repository URL** (https, public host) with an optional read-only access token stored encrypted and never logged. Branch, tag, or commit can be deployed; a redeploy rebuilds the exact commit; a rollback re-activates a stored build. |
| 2026-09-30 | Deployments: serving | **The portal serves sites by hostname** (`{label}.{Deploy:BaseDomain}`), straight from the live release folder, before any portal code runs. Needs a wildcard DNS record (and wildcard TLS at your proxy). |
| 2026-09-30 | Deployments: isolation | **`sudo` helper + `systemd-run`**: the portal is unprivileged and may run one fixed helper that starts each build step as a separate low-privilege user with CPU/memory/task/time limits, a read-only filesystem apart from its workspace, and no route to loopback/private networks. Builds get outbound internet. Not exercised in CI (Windows); see `deploy/README.md`. |
| 2026-09-30 | Product | Roles: agency admin, agency member, client. First milestone is a vertical slice (auth, organizations, projects, notes) with realtime, offline queue, and conflict handling. |

## Known limitations and open decisions

- **Email:** invitations return a one-time link to the admin; nothing is emailed (needs an external service, your decision).
- **Malware scanning** of uploaded files is not done (needs an external scanner).
- **Uploads and deployments are online-only** by design; all other edits work offline.
- **Cleanup limits:** the idempotency retention (30 days) must be longer than a client can stay offline and still retry a queued change; if you lengthen offline use, raise `Cleanup__IdempotencyRetention`. Deployment logs and old releases are kept (no retention policy yet).
- **The systemd build sandbox** has not been run in CI. **The Linux server assumption** (cgroups, systemd) is only needed for "Deploy on Our Platform"; the rest of the portal is OS-independent.
