# Transactional installation of a previously verified build. Never strips user data.
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$BuiltLayout,
    [Parameter(Mandatory)][string]$TestReport,
    [Parameter(Mandatory)][string]$Backup,
    [ValidateSet('portable','development')][string]$Layout = 'portable',
    [string]$MpvDirectory = '',
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$targetRoot = (Get-Item -LiteralPath $Package -Force).FullName.TrimEnd('\','/')
if ((Get-Item -LiteralPath $targetRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation root cannot be a reparse point' }
$backupRoot = [IO.Path]::GetFullPath($Backup)
if (Test-Path -LiteralPath $backupRoot) { throw 'Backup must be a new directory' }
if ($backupRoot.StartsWith($targetRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be outside the installation' }
$head = (& git -C $Repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or @(& git -C $Repo status --porcelain).Count) { throw 'Installation requires clean committed source' }
$version = & (Join-Path $Repo 'tools\Get-ReleaseVersion.ps1') -Repo $Repo
$sourceExe = Join-Path $BuiltLayout '看直播-mpv.exe'
$buildId = (Get-Item -LiteralPath $sourceExe).VersionInfo.ProductVersion
if ($buildId -match 'dirty|nogit' -or -not $buildId.StartsWith($version + '+') -or -not $buildId.Contains($head.Substring(0,12))) { throw 'Build does not match clean source HEAD/VERSION' }
$tests = Get-Content -LiteralPath $TestReport -Raw | ConvertFrom-Json
if ($tests.totals.failed -ne 0 -or $tests.totals.passed -lt 1) { throw 'Build has no passing regression report' }
$verification = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $TestReport) 'verification-manifest.json') -Raw | ConvertFrom-Json
$record = @($verification.artifacts | Where-Object { [IO.Path]::GetFullPath($_.path) -eq [IO.Path]::GetFullPath($sourceExe) })
if ($verification.commit -ne $head -or $verification.buildId -ne $buildId -or $record.Count -ne 1 -or $record[0].sha256 -ne (Get-FileHash -LiteralPath $sourceExe).Hash) { throw 'Build evidence does not identify this exact executable and source' }
$files = @{}
$exeName = if ($Layout -eq 'portable') { '看直播.exe' } else { '看直播-mpv.exe' }
if (-not (Test-Path -LiteralPath (Join-Path $targetRoot $exeName) -PathType Leaf)) { throw 'Expected existing installation launcher is missing' }
$files[$exeName] = $sourceExe
$files[$exeName + '.config'] = $sourceExe + '.config'
foreach ($relative in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','lib/zxing.dll','lib/webview2/assets.json','lib/webview2/Microsoft.Web.WebView2.Core.dll','lib/webview2/Microsoft.Web.WebView2.WinForms.dll','lib/webview2/x64/WebView2Loader.dll','lib/webview2/x86/WebView2Loader.dll','emoji-sheet.png','emoji-sheet.txt')) {
    $files[$relative] = Join-Path $BuiltLayout $relative
}
foreach ($directory in @('fonts','licenses')) {
    $base = Join-Path $Repo $directory
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) { $files[$directory + '/' + $file.FullName.Substring($base.Length + 1).Replace('\','/')] = $file.FullName }
}
foreach ($name in @('live-title.lua','live-resync.lua')) { $files['lua/' + $name] = Join-Path $Repo $name }
$files['live-title.lua'] = Join-Path $Repo 'live-title.lua'
function Get-InstallationDocument([string]$name) {
    $public = Join-Path $Repo ('release-docs/' + $name)
    if ($Layout -eq 'portable' -and (Test-Path -LiteralPath $public)) { return $public }
    return Join-Path $Repo $name
}
$files['LICENSE'] = Get-InstallationDocument 'LICENSE'
$files['THIRD_PARTY_NOTICES.md'] = Get-InstallationDocument 'THIRD_PARTY_NOTICES.md'
if ($Layout -eq 'portable') {
    $lock = Get-Content -LiteralPath (Join-Path $Repo 'deps.lock.json') -Raw | ConvertFrom-Json
    $bundle = $lock.bundles | Where-Object name -EQ 'mpv-dist'
    foreach ($name in @('mpv.exe','mpv.com')) {
        $path = Join-Path $MpvDirectory $name
        $expected = $bundle.files | Where-Object path -EQ $name
        if ((Get-FileHash -LiteralPath $path).Hash -ne $expected.sha256) { throw 'Ordinary x64 runtime does not match dependency lock' }
        $files['bin/mpv/' + $name] = $path
    }
    $files['使用说明.md'] = Get-InstallationDocument '使用说明.md'
    if (Test-Path -LiteralPath (Join-Path $targetRoot 'src')) {
        $files['src/live-hub-v2.cs'] = Join-Path $Repo 'live-hub-v2.cs'
        foreach ($directory in @('src','tests')) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Repo $directory) -Recurse -File -Filter '*.cs') {
                $relative=$file.FullName.Substring((Join-Path $Repo $directory).Length+1).Replace('\','/')
                $files[$directory + '/' + $relative] = $file.FullName
            }
        }
        foreach ($name in @('app.config','app.manifest','app.ico')) { $files['src/' + $name] = Join-Path $Repo $name }
        $generatedVersion = Join-Path $BuiltLayout 'install-build-info.cs'
        & (Join-Path $Repo 'tools/Write-BuildInfo.ps1') -Repo $Repo -Out $generatedVersion | Out-Null
        $files['src/build-info.cs'] = $generatedVersion
        $files['src/build.ps1'] = Join-Path $Repo 'tools/portable-src-build.ps1'
        $files['src/live-title.lua'] = Join-Path $Repo 'live-title.lua'
        $files['src/THIRD_PARTY_NOTICES.md'] = Get-InstallationDocument 'THIRD_PARTY_NOTICES.md'
        foreach ($name in @('Copy-WebViewAssets.ps1','Invoke-BoundedProcess.ps1','New-OfflineLayout.ps1')) { $files['tools/' + $name] = Join-Path $Repo ('tools/' + $name) }
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Repo 'tests/fixtures') -Recurse -File) {
            $files['tests/fixtures/' + $file.FullName.Substring((Join-Path $Repo 'tests/fixtures').Length + 1).Replace('\','/')] = $file.FullName
        }
    }
}
$plan = [Collections.Generic.List[object]]::new()
foreach ($relative in $files.Keys) {
    if ($relative -match '(^|/)(data|runtime|profiles?|[^/]*-(login|verify)-profile)(/|$)|cookies|\.dpapi$|^live-hub\.json$') { throw 'Installation plan touches private data' }
    $source = [IO.Path]::GetFullPath($files[$relative])
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing verified build input: ' + $relative) }
    $target = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
    if (-not $target.StartsWith($targetRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installation target escaped root' }
    $parent=Split-Path -Parent $target
    while($parent.Length -ge $targetRoot.Length) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Managed path contains a reparse point' }
        if($parent -eq $targetRoot){break};$parent=Split-Path -Parent $parent
    }
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Managed target cannot be a reparse point' }
        if ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $target).Hash) { continue }
        if ($relative -match '\.exe$|\.dll$') { $probe=[IO.File]::Open($target,'Open','ReadWrite','None');$probe.Dispose() }
    }
    $plan.Add([pscustomobject]@{relative=$relative;source=$source;target=$target;existed=(Test-Path -LiteralPath $target)})
}
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach($item in $plan){if($item.existed){$save=Join-Path $backupRoot $item.relative;New-Item -ItemType Directory -Path (Split-Path -Parent $save) -Force|Out-Null;Copy-Item -LiteralPath $item.target -Destination $save}}
$installed=[Collections.Generic.List[object]]::new()
try {
    foreach($item in $plan){New-Item -ItemType Directory -Path (Split-Path -Parent $item.target) -Force|Out-Null;Copy-Item -LiteralPath $item.source -Destination $item.target -Force;$installed.Add($item)}
    $validationRuntime=Join-Path $backupRoot 'selftest-runtime'
    $process=Start-Process -FilePath (Join-Path $targetRoot $exeName) -ArgumentList @('--self-test',('--runtime="'+$validationRuntime+'"')) -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(30000)){Stop-Process -Id $process.Id -Force;throw 'Installed self-test timed out'}
    if($process.ExitCode -ne 0){throw 'Installed self-test failed'}
    foreach($item in $plan){if((Get-FileHash -LiteralPath $item.target).Hash -ne (Get-FileHash -LiteralPath $item.source).Hash){throw 'Installed file hash mismatch'}}
} catch {
    foreach($item in $installed){if($item.existed){Copy-Item -LiteralPath (Join-Path $backupRoot $item.relative) -Destination $item.target -Force}else{Remove-Item -LiteralPath $item.target -Force}}
    throw
}
$plan | Select-Object relative,existed | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'installed-files.json') -Encoding utf8
Write-Output ('PASS: installed ' + $plan.Count + ' managed files; personal data and configuration preserved; BUILD=' + $buildId)
