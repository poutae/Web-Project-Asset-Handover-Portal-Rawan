<#
.SYNOPSIS
  Runs every check that CI runs: build, format, tests, lint, typecheck, production build.
#>
. "$PSScriptRoot/_common.ps1"

$backend = Join-Path $script:RepoRoot 'backend'
$frontend = Join-Path $script:RepoRoot 'frontend'

Invoke-Checked 'backend build' { dotnet build (Join-Path $backend 'Portal.slnx') -c Release }
Invoke-Checked 'backend format' { dotnet format (Join-Path $backend 'Portal.slnx') --verify-no-changes }
Invoke-Checked 'backend tests' { dotnet test --solution (Join-Path $backend 'Portal.slnx') -c Release }

Push-Location $frontend
try {
    Invoke-Checked 'frontend format' { npm run format:check }
    Invoke-Checked 'frontend lint' { npm run lint }
    Invoke-Checked 'frontend typecheck' { npm run typecheck }
    Invoke-Checked 'frontend tests' { npm test }
    Invoke-Checked 'frontend production build' { npm run build }
}
finally { Pop-Location }

Write-Host 'All checks passed.' -ForegroundColor Green
