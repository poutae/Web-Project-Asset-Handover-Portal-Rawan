<#
.SYNOPSIS
  One-time local setup: checks prerequisites, creates .env, restores dependencies.
#>
. "$PSScriptRoot/_common.ps1"

Assert-Command dotnet 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
Assert-Command node 'Install Node.js 22 LTS or newer from https://nodejs.org'
Assert-Command npm 'npm ships with Node.js'

$sdk = (& dotnet --version)
if (-not $sdk.StartsWith('10.')) { throw ".NET SDK 10.x is required, found $sdk" }

$envFile = Join-Path $script:RepoRoot '.env'
if (-not (Test-Path $envFile)) {
    Copy-Item (Join-Path $script:RepoRoot '.env.example') $envFile
    Write-Host "Created .env from .env.example - review the values." -ForegroundColor Yellow
}

Invoke-Checked 'dotnet restore' { dotnet restore (Join-Path $script:RepoRoot 'backend/Portal.slnx') }
Invoke-Checked 'npm ci' { Push-Location (Join-Path $script:RepoRoot 'frontend'); try { npm ci } finally { Pop-Location } }

Write-Host 'Setup complete.' -ForegroundColor Green
