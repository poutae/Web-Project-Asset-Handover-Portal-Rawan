<#
.SYNOPSIS
  Runs the Playwright end-to-end tests (including e2e/two-tab.spec.ts) against a production build.
.DESCRIPTION
  Builds the frontend and API, then starts the API on a fresh database on the SQL Server in -SqlServer and runs
  the browser tests. The default is the SQL Server on localhost with Windows authentication.
#>
param([string]$SqlServer = 'Server=localhost;Integrated Security=true;TrustServerCertificate=true')
. "$PSScriptRoot/_common.ps1"

Push-Location (Join-Path $script:RepoRoot 'frontend')
try { Invoke-Checked 'frontend build' { npm ci; npm run build } } finally { Pop-Location }

Invoke-Checked 'API build' { dotnet build (Join-Path $script:RepoRoot 'backend/Portal.slnx') -c Release }

Push-Location (Join-Path $script:RepoRoot 'e2e')
try {
    Invoke-Checked 'e2e install' { npm ci; npx playwright install chromium }
    $env:E2E_SQL_SERVER = $SqlServer
    Invoke-Checked 'playwright' { npx playwright test }
}
finally { Pop-Location }
