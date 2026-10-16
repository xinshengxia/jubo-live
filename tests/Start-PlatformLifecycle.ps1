# 生命周期测试第一步：用 看直播-mpv.exe 的 --plan --relay 生成真实启动命令（并真起本地中转），
# 然后由本进程拉起播放器、把 pid 写进 runtime\，随即退出。
# 之后用 Check-PlatformLifecycle.ps1 检查播放器/streamlinkw 是否仍在供流（关掉启动器不应断流）。
# 用法： pwsh -File Start-PlatformLifecycle.ps1 -Platform douyu -Room 952595 -Frames 600
#
# 2026-09-20 两处调整：
#   ① 日志 -> runtime\logs\，pid -> runtime\（此前都落在项目根）；
#   ② 启动计划改用 CLI 的 --plan --relay，删掉反射调 BuildStart ——
#      该方法的签名已是 BuildStart(Target, string player)，老写法传 1 个参数会抛 Parameter count mismatch。
param([ValidateSet('huya','douyu')][string]$Platform = 'douyu', [string]$Room = '952595', [string]$Frames = '600')
$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $hub '看直播-mpv.exe'
$runtimeDir = Join-Path $hub 'runtime'
$logDir = Join-Path $runtimeDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$planFile = Join-Path $runtimeDir ('plan-{0}-{1}.txt' -f $Platform, $Room)
$pidFile  = Join-Path $runtimeDir ('{0}-lifecycle.pid' -f $Platform)
$log      = Join-Path $logDir ('{0}-lifecycle-test.log' -f $Platform)

$planArgs = @('--plan')
$planArgs += '--relay'                       # 真起中转并把实际端口回填进 ARGS
$planArgs += "${Platform}:${Room}"
$planArgs += $planFile
$planProc = Start-Process -FilePath $exe -ArgumentList $planArgs -Wait -PassThru
if ($planProc.ExitCode -ne 0) { Write-Host "plan 失败 exit=$($planProc.ExitCode)"; exit 2 }
$plan = Get-Content -LiteralPath $planFile
$file  = (($plan | Where-Object { $_ -like 'FILE=*' }) -replace '^FILE=', '')
$args0 = (($plan | Where-Object { $_ -like 'ARGS=*' }) -replace '^ARGS=', '')
$relay = (($plan | Where-Object { $_ -like 'RELAY=*' }) -replace '^RELAY=', '')
if (-not $file -or -not $args0) { Write-Host "计划不完整，见 $planFile"; exit 3 }
$extra = "--log-file=$log --vo=null --ao=null --frames=$Frames"
$playerArgs = if ($args0 -match '--force-window=\w+') { $args0 -replace '--force-window=\w+', $extra } else { "$extra $args0" }
Write-Host "plan: $file"
Write-Host "relay: $relay"
Start-Sleep -Seconds 2   # 与标题测试一致：给中转 2 秒余量再连（实测连太快会连不上）
$p = Start-Process -FilePath $file -ArgumentList $playerArgs -PassThru
Set-Content -LiteralPath $pidFile -Value $p.Id -Encoding ASCII
Write-Host "已启动播放器 PID=$($p.Id)；pid 文件: $pidFile；启动它的进程现在退出"
