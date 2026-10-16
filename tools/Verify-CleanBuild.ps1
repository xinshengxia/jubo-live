# 两条 net48 构建路径的纯离线干净检出验收。保留全部证据，不删除目录。
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Workspace = '',
    [switch]$KeepTemp
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-BoundedProcess.ps1')
if (-not $Workspace) { $Workspace = Join-Path (Split-Path -Parent $Repo) ('livehub-cleancheck-' + [guid]::NewGuid().ToString('N')) }
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (Test-Path -LiteralPath $Workspace) { throw "验收目录必须是新目录：$Workspace" }
New-Item -ItemType Directory -Path $Workspace | Out-Null
$checkout = Join-Path $Workspace 'checkout'
$legacy = Join-Path $Workspace 'legacy-output'
$sdk = Join-Path $Workspace 'sdk-output'
$previousTemp = $env:TEMP; $previousTmp = $env:TMP
$previousCli = $env:DOTNET_CLI_HOME; $previousPackages = $env:NUGET_PACKAGES
try {
    $env:TEMP = $env:TMP = Join-Path $Workspace 'temp'
    $env:DOTNET_CLI_HOME = Join-Path $Workspace 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $Workspace 'nuget-packages'
    $emptySource = Join-Path $Workspace 'empty-nuget-source'
    New-Item -ItemType Directory -Force -Path $env:TEMP, $emptySource | Out-Null
    & git clone --quiet --no-local $Repo $checkout
    if ($LASTEXITCODE -ne 0) { throw 'Git 克隆失败' }
    $head = (& git -C $checkout rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-f0-9]{40}$') { throw '无法取得检出 commit' }
    foreach ($layout in @($legacy, $sdk)) { & (Join-Path $checkout 'tools\New-OfflineLayout.ps1') -Source $checkout -Destination $layout -ToolsOnly }
    $legacyExe = Join-Path $legacy '看直播-mpv.exe'
    Invoke-BoundedProcess -FilePath 'pwsh' -Arguments @('-NoProfile','-File',(Join-Path $checkout 'Build-Hardened.ps1'),'-Out',$legacyExe,'-Report',(Join-Path $Workspace 'legacy-tests.json')) -Log (Join-Path $Workspace 'legacy-build.log') -TimeoutSeconds 360
    Invoke-BoundedProcess -FilePath 'dotnet' -Arguments @('build',(Join-Path $checkout 'live-hub.csproj'),'-c','Release',"-p:OutputPath=$sdk\",'-p:AppendTargetFrameworkToOutputPath=false',"-p:RestoreSources=$emptySource") -Log (Join-Path $Workspace 'sdk-build.log') -TimeoutSeconds 180
    $sdkExe = Join-Path $sdk '看直播-mpv.exe'
    Invoke-BoundedProcess -FilePath (Join-Path $legacy 'tests\HardeningTests.exe') -Arguments @('--exe',$sdkExe,'--src',$checkout,'--report',(Join-Path $Workspace 'sdk-tests.json')) -Log (Join-Path $Workspace 'sdk-tests.log') -TimeoutSeconds 300
    Invoke-BoundedProcess -FilePath $sdkExe -Arguments @('--self-test') -Log (Join-Path $Workspace 'sdk-selftest.log') -TimeoutSeconds 30
    & (Join-Path $checkout 'tests/Test-PublishPrivacy.ps1') -Source $checkout -Workspace (Join-Path $Workspace 'privacy-cases')
    $legacyVersion = (Get-Item -LiteralPath $legacyExe).VersionInfo.ProductVersion
    $sdkVersion = (Get-Item -LiteralPath $sdkExe).VersionInfo.ProductVersion
    if ($legacyVersion -ne $sdkVersion -or $legacyVersion -match 'dirty|nogit' -or -not $legacyVersion.Contains($head.Substring(0,12))) { throw '两条路径的版本不匹配或来源不可追踪' }
    $sourceFiles = @(& git -c core.quotepath=false -C $checkout ls-files)
    $sources = @($sourceFiles | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $checkout $_) -Algorithm SHA256).Hash } })
    $manifest = [ordered]@{
        schemaVersion = 1; commit = $head; buildId = $legacyVersion; verifiedUtc = [DateTime]::UtcNow.ToString('o')
        mode = 'offline'; toolBinaries = 'synthetic placeholders; NOT a playback package'
        onlinePlatforms = 'unverified'; guiDesktopAndDpi = 'unverified'
        sources = $sources
        artifacts = @(@{ path = $legacyExe; sha256 = (Get-FileHash -LiteralPath $legacyExe -Algorithm SHA256).Hash }, @{ path = $sdkExe; sha256 = (Get-FileHash -LiteralPath $sdkExe -Algorithm SHA256).Hash })
        tests = @((Get-Content -LiteralPath (Join-Path $Workspace 'legacy-tests.json') -Raw | ConvertFrom-Json).totals, (Get-Content -LiteralPath (Join-Path $Workspace 'sdk-tests.json') -Raw | ConvertFrom-Json).totals)
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Workspace 'verification-manifest.json') -Encoding utf8
    Write-Output "两条干净构建和离线回归通过。commit=$head BUILD=$legacyVersion"
    Write-Output "证据目录：$Workspace"
} finally {
    $env:TEMP = $previousTemp; $env:TMP = $previousTmp
    $env:DOTNET_CLI_HOME = $previousCli; $env:NUGET_PACKAGES = $previousPackages
}
