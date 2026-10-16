param([string]$Repo = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$path = Join-Path $Repo 'VERSION'
if (-not (Test-Path -LiteralPath $path)) { throw 'Missing committed VERSION file' }
$version = [IO.File]::ReadAllText($path).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part release version' }
Write-Output $version
