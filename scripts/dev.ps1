<#
.SYNOPSIS
  Starts the API and the Vite dev server for local development.
#>
. "$PSScriptRoot/_common.ps1"
Import-DotEnv

$api = Start-Process dotnet -ArgumentList 'run', '--no-launch-profile', '--project', (Join-Path $script:RepoRoot 'backend/src/Portal.Api') `
    -PassThru -NoNewWindow
try {
    Push-Location (Join-Path $script:RepoRoot 'frontend')
    npm run dev
}
finally {
    Pop-Location
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
