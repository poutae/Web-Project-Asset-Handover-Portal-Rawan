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

## Local development (Windows)

Prerequisites: .NET 10 SDK, Node.js 22+, and SQL Server running on `localhost`.

```powershell
./scripts/setup.ps1     # checks prerequisites, creates .env, restores packages
./scripts/dev.ps1       # starts the API (http://localhost:5080) and Vite (http://localhost:5173)
./scripts/verify.ps1    # runs every check that CI runs
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
| 2026-09-30 | Product | Roles: agency admin, agency member, client. First milestone is a vertical slice (auth, organizations, projects, notes) with realtime, offline queue, and conflict handling. |
