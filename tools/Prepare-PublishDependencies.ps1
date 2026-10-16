# Copy only hash-locked redistributable dependencies into a fresh directory.
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$MpvDirectory,
    [Parameter(Mandatory)][string]$Out,
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $destination) { throw 'Dependency output must be a new directory' }
$lock = Get-Content -LiteralPath (Join-Path $Repo 'deps.lock.json') -Raw | ConvertFrom-Json
$selected = @{}
foreach ($component in $lock.components) {
    if ($component.name -eq 'potplayer' -or $component.source -notlike 'installed:*') { continue }
    $relative = $component.source.Substring('installed:'.Length)
    $selected[$relative] = @{sha256=$component.sha256;size=$component.size}
}
foreach ($bundle in $lock.bundles) {
    if ($bundle.name -eq 'potplayer-dist' -or $bundle.sourceDir -notlike 'installed:*') { continue }
    $prefix = $bundle.sourceDir.Substring('installed:'.Length).TrimEnd('/')
    foreach ($item in $bundle.files) { $selected[$prefix + '/' + $item.path] = @{sha256=$item.sha256;size=$item.size} }
}
$plan = foreach ($relative in $selected.Keys) {
    if ($relative -match '^[/\\]|(^|/)\.\.(/|$)|:') { throw 'Unsafe dependency path' }
    $from = if ($relative -in @('bin/mpv/mpv.exe','bin/mpv/mpv.com')) {
        Join-Path $MpvDirectory ([IO.Path]::GetFileName($relative))
    } else { Join-Path $Source $relative }
    $item = Get-Item -LiteralPath $from -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Dependency file cannot be a reparse point' }
    if ($item.Length -ne $selected[$relative].size -or (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $selected[$relative].sha256) {
        throw ('Dependency source does not match lock: ' + $relative)
    }
    [pscustomobject]@{relative=$relative;source=$from}
}
foreach ($item in $plan) {
    $target = Join-Path $destination $item.relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $item.source -Destination $target
}
& (Join-Path $Repo 'tools\Test-PublishPrivacy.ps1') -Directory $destination
Write-Output ('PASS: prepared ' + $selected.Count + ' locked dependencies; no personal data or PotPlayer copied')
