<#
.SYNOPSIS
  Builds a deployable copy of the portal (API + built frontend) into ./artifacts/portal.
.DESCRIPTION
  The API serves the frontend itself, so the result is a single folder. Copy it to the server (see deploy/README.md).
#>
. "$PSScriptRoot/_common.ps1"

$frontend = Join-Path $script:RepoRoot 'frontend'
$out = Join-Path $script:RepoRoot 'artifacts/portal'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Push-Location $frontend
try {
    Invoke-Checked 'frontend install' { npm ci }
    Invoke-Checked 'frontend production build' { npm run build }
}
finally { Pop-Location }

Invoke-Checked 'backend publish' {
    dotnet publish (Join-Path $script:RepoRoot 'backend/src/Portal.Api') -c Release -o $out --no-self-contained
}
Copy-Item (Join-Path $frontend 'dist') (Join-Path $out 'wwwroot') -Recurse

Write-Host "Published to $out" -ForegroundColor Green
Write-Host "Set Frontend__DistPath to the wwwroot folder on the server." -ForegroundColor Yellow
