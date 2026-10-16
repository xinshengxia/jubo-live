param([string]$Workspace = '')
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\Protect-PackageCredentials.ps1')
if (-not $Workspace) { $Workspace = Join-Path $env:TEMP ('livehub-package-test-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $Workspace) { throw '测试工作目录必须尚不存在，不能操作已有包' }
New-Item -ItemType Directory -Path $Workspace | Out-Null
$package = Join-Path $Workspace 'synthetic-package'
New-Item -ItemType Directory -Path $package | Out-Null
$cookie = Join-Path $package 'douyin-cookies.txt'
$hidden = Join-Path $package 'huya-cookies.txt.json'
$profile = Join-Path $package 'douyin-login-profile\Default'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
[IO.File]::WriteAllText($cookie, 'synthetic credential')
[IO.File]::WriteAllText($hidden, '{}')
[IO.File]::WriteAllText((Join-Path $package 'credential.dpapi'), 'synthetic encrypted credential')
[IO.File]::SetAttributes($hidden, [IO.FileAttributes]::Hidden)
[IO.File]::WriteAllText((Join-Path $profile 'Cookies'), 'synthetic profile')
$webviewProfile = Join-Path $package 'runtime\douyin-webview-login-profile\Default'
New-Item -ItemType Directory -Path $webviewProfile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $webviewProfile 'Cookies'), 'synthetic webview credential')
$locked = [IO.File]::Open($cookie, 'Open', 'ReadWrite', 'None')
$failed = $false
$messages = [Collections.Generic.List[string]]::new()
try {
    Protect-PackageCredentials -Package $package | ForEach-Object { $messages.Add($_) }
} catch { $failed = $true } finally { $locked.Dispose() }
if (-not $failed -or @($messages | Where-Object { $_ -match '复扫 0 命中' }).Count) { throw '锁定凭据时错误地宣告成功' }
Write-Output 'PASS: 锁定凭据令剥离失败，未宣告成功'
Protect-PackageCredentials -Package $package
if (@(Find-PackageCredentials -Package $package).Count -ne 0) { throw '剥离后仍残留凭据' }
Write-Output 'PASS: 隐藏凭据和 profile 全部剥离，复扫零命中'
[IO.File]::WriteAllText($cookie, 'synthetic credential')
Protect-PackageCredentials -Package $package -IncludeCredentials
if (-not (Test-Path -LiteralPath $cookie)) { throw '显式保留凭据失效' }
Write-Output 'PASS: 显式保留凭据'
# 保留合成目录作为诊断证据；下一次测试创建新目录。
