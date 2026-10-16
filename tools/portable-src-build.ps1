# Rebuild bundled source in isolation; replace the launcher only after validation.
param([string]$Workspace = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Workspace) { $Workspace = Join-Path $env:TEMP ('livehub-rebuild-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $Workspace) { throw 'Rebuild workspace must be new' }
$sourceView = Join-Path $Workspace 'source'
$output = Join-Path $Workspace 'output'
New-Item -ItemType Directory -Path $sourceView,$output -Force | Out-Null
. (Join-Path $root 'tools/Invoke-BoundedProcess.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
$source = @((Join-Path $PSScriptRoot 'live-hub-v2.cs'),(Join-Path $PSScriptRoot 'build-info.cs'))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live-hub-v2.cs') -Destination $sourceView
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live-title.lua') -Destination $sourceView
New-Item -ItemType Directory -Path (Join-Path $sourceView 'tests') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tests/fixtures') -Destination (Join-Path $sourceView 'tests') -Recurse
foreach ($directory in Get-ChildItem -LiteralPath $PSScriptRoot -Directory) {
    $code = @(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.cs' -File)
    if (-not $code.Count) { continue }
    $to = Join-Path $sourceView ('src/' + $directory.Name)
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    foreach ($file in $code) { $source += $file.FullName; Copy-Item -LiteralPath $file.FullName -Destination $to }
}
foreach ($asset in @('fonts','lib','licenses','tests','emoji-sheet.png','emoji-sheet.txt','THIRD_PARTY_NOTICES.md','live-title.lua')) {
    Copy-Item -LiteralPath (Join-Path $root $asset) -Destination $output -Recurse
}
& (Join-Path $root 'tools/New-OfflineLayout.ps1') -Source $sourceView -Destination $output -ToolsOnly
& (Join-Path $root 'tools/Copy-WebViewAssets.ps1') -SourceRoot $root -Destination $output
$exe = Join-Path $output '看直播.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($exe + '.config')
$arguments = @('/nologo','/target:winexe','/optimize+',('/out:' + $exe),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')),
    '/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:Microsoft.VisualBasic.dll',
    ('/r:' + (Join-Path $root 'lib/webview2/Microsoft.Web.WebView2.Core.dll')),('/r:' + (Join-Path $root 'lib/webview2/Microsoft.Web.WebView2.WinForms.dll'))) + $source
Invoke-BoundedProcess -FilePath $compiler -Arguments $arguments -Log (Join-Path $Workspace 'compile.log') -TimeoutSeconds 120
$testExe = Join-Path $output 'tests/HardeningTests.exe'
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' -File | ForEach-Object FullName)
Invoke-BoundedProcess -FilePath $compiler -Arguments (@('/nologo','/target:exe','/optimize+',('/out:' + $testExe)) + $testSources) -Log (Join-Path $Workspace 'test-compile.log') -TimeoutSeconds 120
Invoke-BoundedProcess -FilePath $testExe -Arguments @('--exe',$exe,'--src',$sourceView,'--report',(Join-Path $Workspace 'tests.json')) -Log (Join-Path $Workspace 'tests.log') -TimeoutSeconds 300
Invoke-BoundedProcess -FilePath $exe -Arguments @('--self-test') -Log (Join-Path $Workspace 'selftest.log') -TimeoutSeconds 30
$target = Join-Path $root '看直播.exe'
$probe = [IO.File]::Open($target,'Open','ReadWrite','None'); $probe.Dispose()
$backup = Join-Path $Workspace 'previous-launcher'
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $target,($target + '.config') -Destination $backup
try {
    Copy-Item -LiteralPath $exe -Destination $target -Force
    Copy-Item -LiteralPath ($exe + '.config') -Destination ($target + '.config') -Force
} catch {
    Copy-Item -LiteralPath (Join-Path $backup '看直播.exe'),(Join-Path $backup '看直播.exe.config') -Destination $root -Force
    throw
}
Write-Output 'PASS: bundled source build, offline regression and self-test; personal data preserved'
