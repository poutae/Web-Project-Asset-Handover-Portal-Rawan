# Web Project Asset & Handover Portal

A multi-tenant agency/client portal for managing web projects: organizations and users, projects and members, invitations, milestones, notes, documents/assets, environments, deployments, and deployment history/logs/status.

> **Status:** repository bootstrap. No application code yet.

## Stack

- **Backend:** C# 14, .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10, SQL Server
- **Frontend:** React 19, TypeScript, Vite, React Router, TanStack Query, Tailwind
- **Testing:** xUnit, Vitest, Playwright

## Workflow

- `main` is protected by convention: no direct commits after the bootstrap commit.
- All work happens on `feature/*`, `fix/*`, `test/*`, `chore/*` branches and lands through pull requests.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
- CI must pass before merge, and nothing is merged without the owner's approval.

## Configuration

Configuration comes from environment variables, loaded from a local `.env` during development. Only `.env.example` is committed. Secrets, credentials, tokens, and production infrastructure values are never committed.

## Decisions log

Decisions made by the project owner are recorded here.

| Date | Area | Decision |
|---|---|---|
| 2026-09-30 | Tooling / infrastructure | **Docker is not used** anywhere in this project (development, testing, CI, build isolation, or deployment). Alternatives for SQL Server, deployment isolation, and hosting are still to be decided. |
