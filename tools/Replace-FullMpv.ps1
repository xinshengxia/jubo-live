# Repack an already verified full release with a hash-locked mpv runtime.
# The launcher stays byte-identical and retains its original source commit/build ID.
param(
    [Parameter(Mandatory)][string]$InputZip,
    [Parameter(Mandatory)][string]$ExpectedInputSha256,
    [Parameter(Mandatory)][string]$MpvDirectory,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify package revision' }
$dirty = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Repackaging requires a clean committed source tree' }
if ((Get-FileHash -LiteralPath $InputZip -Algorithm SHA256).Hash -ne $ExpectedInputSha256) { throw 'Input release SHA-256 mismatch' }
& (Join-Path $root 'tools\Verify-ReleaseArchive.ps1') -Zip $InputZip
$destination = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $destination) { throw 'Output must be a new archive' }
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
$lockPath = Join-Path $root 'deps.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$player = @($lock.components | Where-Object name -EQ 'mpv')
$bundle = @($lock.bundles | Where-Object name -EQ 'mpv-dist')
if ($player.Count -ne 1 -or $bundle.Count -ne 1 -or $player[0].distribution -ne 'x86_64') { throw 'Expected one ordinary x86_64 mpv runtime in lock file' }
$replacements = @{}
foreach ($name in @('mpv.exe','mpv.com')) {
    $file = Join-Path $MpvDirectory $name
    $record = @($bundle[0].files | Where-Object path -EQ $name)
    if ($record.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne $record[0].size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record[0].sha256) { throw ('mpv runtime differs from lock: ' + $name) }
    $replacements['bin/mpv/' + $name] = [IO.File]::ReadAllBytes($file)
}
if ($player[0].sha256 -ne ($bundle[0].files | Where-Object path -EQ 'mpv.exe').sha256) { throw 'Component/bundle mismatch' }
$replacements['deps.lock.json'] = [IO.File]::ReadAllBytes($lockPath)
foreach ($name in @('SOURCES.json','mpv-Copyright.txt','mpv-LICENSE.GPL.txt','mpv-LICENSE.LGPL.txt')) {
    $replacements['licenses/' + $name] = [IO.File]::ReadAllBytes((Join-Path $root ('licenses/' + $name)))
}
$replacements['THIRD_PARTY_NOTICES.md'] = [IO.File]::ReadAllBytes((Join-Path $root 'THIRD_PARTY_NOTICES.md'))
$stream = [IO.File]::OpenRead($InputZip)
$source = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read)
try {
    $manifestReader = [IO.StreamReader]::new($source.GetEntry('manifest.json').Open())
    try { $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json } finally { $manifestReader.Dispose() }
    if ($manifest.variant -ne 'full' -or $manifest.releaseVersion -ne '1.0.1') { throw 'Only the requested 1.0.1 full variant can be repacked' }
    $codePaths = @('*.cs','live-title.lua','app.config','app.manifest','app.ico','live-hub.csproj','Build-Hardened.ps1','tools/Write-BuildInfo.ps1','lib','fonts','emoji-sheet.png','emoji-sheet.txt')
    & git -C $root diff --quiet $manifest.commit.full $head -- @codePaths
    if ($LASTEXITCODE -ne 0) { throw 'Launcher source or build inputs changed; a full rebuild is required' }
    $tree = [Text.StringBuilder]::new()
    [long]$bundleBytes = 0
    foreach ($record in @($bundle[0].files | Sort-Object { $_.path.ToLowerInvariant() })) {
        $name = 'bin/mpv/' + $record.path
        if ($replacements.ContainsKey($name)) { $bytes = $replacements[$name] }
        else {
            $entry = $source.GetEntry($name)
            if ($null -eq $entry) { throw ('Missing unchanged runtime script: ' + $name) }
            $memory = [IO.MemoryStream]::new(); $input = $entry.Open()
            try { $input.CopyTo($memory); $bytes = $memory.ToArray() } finally { $input.Dispose(); $memory.Dispose() }
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        if ($bytes.Length -ne $record.size -or $hash -ne $record.sha256) { throw ('Bundle contents disagree with lock: ' + $name) }
        [void]$tree.Append($record.path.ToLowerInvariant()).Append([char]10).Append($hash).Append([char]10)
        $bundleBytes += $bytes.Length
    }
    $treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($tree.ToString()))).ToLowerInvariant()
    if ($treeHash -ne $bundle[0].treeSha256 -or $bundleBytes -ne $bundle[0].bytes) { throw 'Runtime bundle tree hash mismatch' }
    foreach ($component in $manifest.components) {
        if ($component.name -eq 'mpv') {
            $component.version = $player[0].version; $component.sha256 = $player[0].sha256; $component.size = $player[0].size
            $component | Add-Member -NotePropertyName distribution -NotePropertyValue 'x86_64' -Force
        }
        if ($component.name -eq 'mpv-dist') { $component.sha256 = $treeHash; $component.size = $bundleBytes }
    }
    $manifest.lockFile.generatedAt = $lock.generatedAt
    $manifest | Add-Member -NotePropertyName packagingRevision -NotePropertyValue ([ordered]@{
        commit = $head; type = 'mpv-runtime'; distribution = 'x86_64'; upstreamCommit = $player[0].upstreamCommit
        builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        launcher = 'unchanged; buildId and source commit retained'; onlinePlayback = 'not-retested'
    }) -Force
    [long]$total = 0
    foreach ($artifact in $manifest.artifacts) {
        if ($replacements.ContainsKey($artifact.path)) {
            $bytes = $replacements[$artifact.path]
            $artifact.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            $artifact.size = $bytes.Length
        }
        $total += $artifact.size
    }
    $manifest.artifactsBytes = $total
    $replacements['manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 30))
    $outputStream = [IO.File]::Create($destination)
    $outputArchive = [IO.Compression.ZipArchive]::new($outputStream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $source.Entries) {
            $newEntry = $outputArchive.CreateEntry($entry.FullName,[IO.Compression.CompressionLevel]::Fastest)
            $newEntry.LastWriteTime = $entry.LastWriteTime
            $output = $newEntry.Open()
            try {
                if ($replacements.ContainsKey($entry.FullName)) { $bytes=$replacements[$entry.FullName]; $output.Write($bytes,0,$bytes.Length) }
                else { $input=$entry.Open(); try { $input.CopyTo($output) } finally { $input.Dispose() } }
            } finally { $output.Dispose() }
        }
    } finally { $outputArchive.Dispose(); $outputStream.Dispose() }
} finally { $source.Dispose(); $stream.Dispose() }
$digest = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($destination + '.sha256'),($digest + '  ' + [IO.Path]::GetFileName($destination) + "`n"),[Text.UTF8Encoding]::new($false))
& (Join-Path $root 'tools\Verify-ReleaseArchive.ps1') -Zip $destination
& (Join-Path $root 'tools\Test-PublishPrivacy.ps1') -Archive $destination
Write-Output ('PASS: full mpv runtime replaced; launcher bytes unchanged; SHA256=' + $digest)
