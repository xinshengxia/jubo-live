# Update an existing personal installation from an isolated, verified clean build.
# Public distribution uses tools/Publish-Release.ps1 and a fresh staging directory.
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Backup,
    [Parameter(Mandatory)][string]$MpvDirectory,
    [switch]$IncludeCredentials
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (@(& git -C $root status --porcelain).Count -or $LASTEXITCODE -ne 0) { throw 'Commit source changes before updating an installation' }
# IncludeCredentials is accepted for old callers; personal updates always preserve data.
& (Join-Path $root 'tools/Verify-CleanBuild.ps1') -Repo $root -Workspace $Workspace
& (Join-Path $root 'tools/Install-LocalBuild.ps1') -Repo $root -Package $Package -BuiltLayout (Join-Path $Workspace 'legacy-output') -TestReport (Join-Path $Workspace 'legacy-tests.json') -Backup $Backup -MpvDirectory $MpvDirectory
