# 三端窗口标题验证：用 exe 自己的 --plan 生成真实启动命令，跑有限帧数后查标题
# douyu/huya/douyin 走本地中转：--plan --relay 会真的起中转、把真实端口回填进 ARGS，并写好码率/标题提示文件。
param([ValidateSet('bili','huya','douyu','douyin')][string]$Platform = 'bili', [string]$Room = '7777', [string]$Frames = '240')
$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $hub '看直播-mpv.exe'
$runtime = Join-Path $hub 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$planFile = Join-Path $runtime "plan-$Platform-$Room.txt"
$planArgs = @('--plan')
if ($Platform -in @('douyu','huya','douyin')) { $planArgs += '--relay' }   # 走本地中转的三端：真起中转并把端口回填进 ARGS
$planArgs += "${Platform}:${Room}"
$planArgs += $planFile
$planProc = Start-Process -FilePath $exe -ArgumentList $planArgs -Wait -PassThru
if ($planProc.ExitCode -ne 0) { Write-Host "plan 失败 exit=$($planProc.ExitCode)"; exit 2 }
$plan = Get-Content -LiteralPath $planFile
$file  = (($plan | Where-Object { $_ -like 'FILE=*' }) -replace '^FILE=', '')
$args0 = (($plan | Where-Object { $_ -like 'ARGS=*' }) -replace '^ARGS=', '')
$anchor = (($plan | Where-Object { $_ -like 'ANCHOR=*' }) -replace '^ANCHOR=', '')
Write-Host "plan: $file"
Write-Host "anchor: $anchor"
$relay = (($plan | Where-Object { $_ -like 'RELAY=*' }) -replace '^RELAY=', '')
Write-Host "relay: $relay"
$mpvLogDir = Join-Path $runtime 'logs'
New-Item -ItemType Directory -Force -Path $mpvLogDir | Out-Null
$mpvLogWin = Join-Path $mpvLogDir ('{0}-title-test.log' -f $Platform)
$mpvLogFwd = $mpvLogWin -replace '\\', '/'
if (Test-Path $mpvLogWin) { Remove-Item $mpvLogWin -Force }
$extra = "--log-file=$mpvLogFwd --vo=null --ao=null --frames=$Frames"
$args2 = if ($args0 -match '--force-window=\w+') { $args0 -replace '--force-window=\w+', $extra } else { "$extra $args0" }
if ($Platform -in @('douyu','huya','douyin')) {
    # 中转已在 --plan --relay 阶段起好；这里仍留 2 秒余量再连（三端一致，实测连太快会连不上）
    Start-Sleep -Seconds 2
}
# 码率/标题提示文件已由 --plan --relay 在起中转时写好，这里不再反射补写（原 douyu 分支的反射补写已删）
$proc = Start-Process -FilePath $file -ArgumentList $args2 -PassThru
$null = $proc.WaitForExit(180000)
Start-Sleep -Milliseconds 600
Write-Host '--- 标题 / 脚本 ---'
Select-String -LiteralPath $mpvLogWin -Pattern '窗口标题已设置|live-resync.lua|live-quality-title' |
    Where-Object { $_.Line -notmatch 'Loading lua script @' } | Select-Object -First 8 | ForEach-Object { '  ' + $_.Line }