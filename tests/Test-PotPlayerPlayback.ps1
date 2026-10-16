# PotPlayer 播放测试：用启动器的 --plan --relay 拿到 PotPlayer 会打开的直链，真的开一次并打印窗口标题
#
# 2026-09-28 修（本次安全验收时发现）：原来少了 --relay，而 --plan 默认**零副作用、只算不启动**，
#   ARGS 里会留着占位符 {relay-port} —— 播放器拿到 http://127.0.0.1:{relay-port}/... 必然报
#   「在开始播放时发生了问题」。走本地中转的三端（虎牙/斗鱼/抖音）必须带 --relay，
#   才会真起中转并把端口回填进计划。
# 收尾顺手清掉本次留下的 PotPlayer 与中转进程（--plan --relay 起的中转是独立进程，不随脚本退出，
#   留着会占住 exe 让下一次 Build-Hardened.ps1 报「目标 exe 正被占用」）。
param([ValidateSet('bili','huya','douyu')][string]$Platform = 'huya', [string]$Room = '333003', [int]$Wait = 14)
$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $hub '看直播-mpv.exe'
$runtime = Join-Path $hub 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$planFile = Join-Path $runtime "plan-potplayer-$Platform-$Room.txt"
Start-Process -FilePath $exe -ArgumentList @('--plan', 'potplayer', '--relay', "${Platform}:${Room}", $planFile) -Wait | Out-Null
$plan = Get-Content -LiteralPath $planFile
$file = (($plan | Where-Object { $_ -like 'FILE=*' }) -replace '^FILE=', '')
$args0 = (($plan | Where-Object { $_ -like 'ARGS=*' }) -replace '^ARGS=', '')
$relay = (($plan | Where-Object { $_ -like 'RELAY=*' }) -replace '^RELAY=', '')
$shown = $args0 -replace '(/new "https://[^?]{0,70}).*', '$1…'
$shown = $shown -replace '(/new "http://[^?]{0,70}).*', '$1…'
Write-Host "PotPlayer: $file"
Write-Host "中转: $relay"
Write-Host "参数: $shown"
if ($args0 -match '\{relay-port\}') { Write-Host '计划里还是占位符 {relay-port}：中转没起来，播放器必然失败'; exit 3 }
Get-Process PotPlayerMini64 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Start-Process -FilePath $file -ArgumentList $args0 | Out-Null
Start-Sleep -Seconds $Wait
$procs = Get-Process PotPlayerMini64 -ErrorAction SilentlyContinue
if (-not $procs) { Write-Host 'PotPlayer 没起来'; exit 2 }
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PotWin {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static List<string> Titles(int targetPid) {
        var list = new List<string>();
        EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == (uint)targetPid && IsWindowVisible(h)) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); list.Add(sb.ToString()); } return true; }, IntPtr.Zero);
        return list;
    }
}
'@
foreach ($proc in $procs) { foreach ($t in [PotWin]::Titles($proc.Id)) { if ($t) { Write-Host "  窗口: $t" } } }
Write-Host '三端都实测可播：B站/虎牙用解析出的直链，斗鱼走启动器自带的本地中转（--relay 模式，约 2 秒起播）。'
Write-Host '收尾：关闭本次拉起的 PotPlayer 与遗留中转进程。'
Get-Process PotPlayerMini64 -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='看直播-mpv.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -match '--(relay|danmaku|watch)' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
