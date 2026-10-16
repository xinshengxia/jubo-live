# 生命周期测试第二步：检查 streamlinkw 是否还活着、有没有 conhost（控制台窗口）、标题是否来自本平台脚本。
# 用法： pwsh -File Check-PlatformLifecycle.ps1 -Platform douyu
param([ValidateSet('huya','douyu')][string]$Platform = 'douyu')
$ErrorActionPreference = 'Continue'
$hub = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $hub ('runtime/{0}-lifecycle.pid' -f $Platform)
if (-not (Test-Path $pidFile)) { Write-Host '没有 pid 文件，先跑 Start-PlatformLifecycle.ps1'; exit 2 }
$targetPid = [int](Get-Content -LiteralPath $pidFile)
$proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
if ($null -eq $proc) { Write-Host "PID $targetPid 已退出" } else { Write-Host "PID $targetPid 仍在运行: $($proc.ProcessName)" }
Write-Host '--- 子进程 ---'
Get-CimInstance Win32_Process -Filter "ParentProcessId=$targetPid" -ErrorAction SilentlyContinue | ForEach-Object { "  $($_.ProcessId) $($_.Name)" }
$conhost = Get-Process conhost -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-3) }
Write-Host "最近 3 分钟新起的 conhost 个数: $(@($conhost).Count)  （0 = 没有弹出 CMD 窗口）"
Write-Host '--- 窗口标题 ---'
Select-String -LiteralPath (Join-Path $hub ('runtime/logs/{0}-lifecycle-test.log' -f $Platform)) -Pattern '窗口标题已设置' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }