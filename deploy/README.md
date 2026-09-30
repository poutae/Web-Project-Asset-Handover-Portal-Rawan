# Running the portal on your own server

This describes a single Linux server (Ubuntu/Debian with systemd). Everything runs as one .NET service; SQL Server
runs on the same machine or one you can reach. No containers are used.

## 1. Prerequisites

- .NET 10 runtime (`aspnetcore-runtime-10.0`), Node.js 22+ and `git` (used by the build sandbox for client sites)
- SQL Server (2022 or 2025) with an empty database and a login for the portal
- A reverse proxy that terminates TLS in front of `127.0.0.1:5080` (Caddy or nginx). The portal trusts
  `X-Forwarded-For`/`-Proto` from loopback only.
- DNS: the portal host (for example `portal.example.com`) and a **separate** wildcard for client sites
  (`*.sites.example.com`) pointing at the same proxy. Use a different registrable domain for sites than for the
  portal if you can, so client sites can never share cookies with the portal.

## 2. Publish and copy

```powershell
# On your Windows machine, from the repository root
./scripts/publish.ps1          # builds the frontend and API into ./artifacts/portal
```

Copy `artifacts/portal` to `/opt/portal` on the server.

## 3. Users, directories, and the build sandbox

```bash
sudo ./deploy/install-build-sandbox.sh
sudo install -m 0640 -o root -g portal deploy/portal.env.example /etc/portal/portal.env   # then edit it
sudo install -m 0644 deploy/portal.service /etc/systemd/system/portal.service
```

Edit `/etc/portal/portal.env`: connection string, `Deploy__BaseDomain`, paths. Never commit this file.

## 4. Create the database schema

Migrations are applied by an operator, not automatically, in production:

```powershell
$env:ConnectionStrings__Default = '<production connection string>'
./scripts/migrate.ps1
```

## 5. Start

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now portal
curl -s http://127.0.0.1:5080/api/health/ready
```

## How client builds are isolated

"Deploy on Our Platform" runs code that clients wrote. Each step (a `git` fetch, then the project's build command)
runs through `/usr/local/lib/portal/run-build`, which the portal may call via `sudo` and nothing else. The helper
starts the step with `systemd-run` as the separate `portal-build` user with:

| Control | Effect |
|---|---|
| Separate OS user | cannot read the portal's files, secrets, or database credentials |
| `ProtectSystem=strict`, `ReadWritePaths=<workspace>` | the whole filesystem is read-only except that build's own folder |
| `ProtectHome`, `PrivateTmp`, `PrivateDevices` | no home directories, private `/tmp`, no devices |
| `MemoryMax`, `CPUQuota`, `TasksMax`, `RuntimeMaxSec` | a build cannot exhaust the machine or run forever |
| `IPAddressDeny` for loopback/private/link-local | a build cannot reach SQL Server, the portal API, or cloud metadata (DNS is allowed explicitly) |
| `NoNewPrivileges`, empty capability set | no privilege escalation |
| Environment allow-list | only a few variables reach the build; the portal's own environment does not |

The portal additionally refuses symbolic links in build output, caps artifact size/file count, only fetches
`https://` repositories on public hosts, and keeps Git tokens encrypted (ASP.NET Data Protection) and out of logs.

**What this does not do.** It is OS-level isolation on the same host, not a virtual machine. It protects the portal
and other projects from a build, but a kernel vulnerability could still be used to escape. Only deploy projects you
trust, or move builds to a separate machine (see the decision log in the top-level README).
The systemd sandbox was written for Linux and has not been exercised in this repository's CI (which runs on
Windows); test it on your server with a sample project before relying on it.

## Serving deployed sites

The portal answers requests whose host is `{site-label}.{Deploy__BaseDomain}` straight from the live release folder,
before any portal code runs, so client sites cannot reach the portal API or its cookies and keep working if the
database is down. Your proxy must forward the wildcard host to the portal unchanged (`Host` header preserved).

## Backups

Back up the SQL Server database, `/var/lib/portal/storage` (uploaded documents), and `/var/lib/portal/keys`
(without the keys, stored Git tokens cannot be decrypted). `/var/lib/portal/deployments` can be rebuilt by
redeploying.
