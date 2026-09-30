<#
.SYNOPSIS
  Applies EF Core migrations to the database named by ConnectionStrings__Default (from .env or the environment).
.DESCRIPTION
  Production never migrates automatically; run this deliberately, ideally after a backup.
  Use -Script to print the SQL instead of applying it.
#>
param([switch]$Script)
. "$PSScriptRoot/_common.ps1"

if (-not $env:ConnectionStrings__Default) { Import-DotEnv }
if (-not $env:ConnectionStrings__Default) { throw 'ConnectionStrings__Default is not set.' }

Assert-Command dotnet 'Install the .NET 10 SDK.'
if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue)) {
    Invoke-Checked 'install dotnet-ef' { dotnet tool install --global dotnet-ef }
}

$project = Join-Path $script:RepoRoot 'backend/src/Portal.Infrastructure'
$startup = Join-Path $script:RepoRoot 'backend/src/Portal.Api'

if ($Script) {
    Invoke-Checked 'generate SQL' { dotnet ef migrations script --idempotent --project $project --startup-project $startup }
}
else {
    Invoke-Checked 'apply migrations' { dotnet ef database update --project $project --startup-project $startup }
}
