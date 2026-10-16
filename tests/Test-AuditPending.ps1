param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Runner,
    [Parameter(Mandatory)][string]$Mpv,
    [Parameter(Mandatory)][string]$Workspace,
    [string]$Source = (Split-Path -Parent $PSScriptRoot),
    [switch]$Desktop
)
$ErrorActionPreference = 'Stop'
$Exe = [IO.Path]::GetFullPath($Exe); $Runner = [IO.Path]::GetFullPath($Runner)
$Mpv = [IO.Path]::GetFullPath($Mpv); $Workspace = [IO.Path]::GetFullPath($Workspace)
if (Test-Path -LiteralPath $Workspace) { throw '实验目录必须为新目录' }
New-Item -ItemType Directory -Path $Workspace | Out-Null

function Invoke-SyntheticMpv([string[]]$PlayerArgs, [string]$Log) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Mpv; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $PlayerArgs) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'mpv 启动失败' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw '合成视频 mpv 实验超时' }
        [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { throw "mpv 退出 $($process.ExitCode)，见 $Log" }
    } finally { $process.Dispose() }
}

$targetReport = Join-Path $Workspace 'targeted-tests.json'
& $Runner --exe $Exe --src $Source --filter 审计待确认 --report $targetReport *> (Join-Path $Workspace 'targeted-tests.log')
if ($LASTEXITCODE -ne 0) { throw '待确认事项回归失败，见 targeted-tests.log' }

$encoded = Join-Path $Workspace 'source.flv'; $forwarded = Join-Path $Workspace 'forwarded.flv'
Invoke-SyntheticMpv -PlayerArgs @('--no-config','--load-scripts=no','--ovc=libx264','--oac=aac','--of=flv','--end=8',"--o=$encoded",'--audio-file=av://lavfi:sine=frequency=440:sample_rate=44100','av://lavfi:testsrc2=size=320x180:rate=10') -Log (Join-Path $Workspace 'encode.log')
& $Runner --audit-flv-capture $Exe $encoded $forwarded *> (Join-Path $Workspace 'relay-tags.log')
if ($LASTEXITCODE -ne 0) { throw '真实编码多段 FLV 中转实验失败，见 relay-tags.log' }
$decoderLog = Join-Path $Workspace 'decoder.log'
Invoke-SyntheticMpv -PlayerArgs @('--no-config','--load-scripts=no','--vo=null','--ao=null','--untimed','--speed=100','--vf=lavfi=showinfo','--af=lavfi=ashowinfo','--msg-level=ffmpeg=info',"--log-file=$decoderLog",$forwarded) -Log (Join-Path $Workspace 'decode-output.log')
$videoFrames = @(Select-String -LiteralPath $decoderLog -Pattern 'Parsed_showinfo.*n:\s*[0-9]+').Count
$audioFrames = @(Select-String -LiteralPath $decoderLog -Pattern 'Parsed_ashowinfo.*n:\s*[0-9]+').Count
if ($videoFrames -lt 230 -or $audioFrames -lt 900) { throw "实际解码帧不足：video=$videoFrames audio=$audioFrames" }

Add-Type -AssemblyName System.Drawing
$largeImage = Join-Path $Workspace 'oversized-valid.png'
$bitmap = [Drawing.Bitmap]::new(4096,4096)
try { $bitmap.Save($largeImage,[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
$memoryReport = Join-Path $Workspace 'image-memory.json'
& $Runner --audit-image-memory $Exe $largeImage $memoryReport *> (Join-Path $Workspace 'image-memory.log')
if ($LASTEXITCODE -ne 0) { throw '图片内存实验失败' }
$memory = Get-Content -LiteralPath $memoryReport -Raw | ConvertFrom-Json
if ($memory.rejected -ne 8 -or $memory.growthBytes -gt 32MB) { throw '大尺寸 PNG 未在解码前拒绝，或出现异常内存峰值' }

$physical = 'unverified: desktop mode not selected'; $desktopResult = $null
if ($Desktop) {
    $desktopReport = Join-Path $Workspace 'desktop-tests.json'
    & $Runner --exe $Exe --src $Source --filter 审计桌面 --audit-desktop --report $desktopReport *> (Join-Path $Workspace 'desktop-tests.log')
    if ($LASTEXITCODE -ne 0) { throw '桌面像素/DPI 实验失败，见 desktop-tests.log' }
    $inventory = Join-Path $Source 'artifacts\audit-pending-20261010\desktop\monitor-inventory.json'
    Copy-Item -LiteralPath $inventory -Destination (Join-Path $Workspace 'monitor-inventory.json')
    $physical = (Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json).physicalMixedDpi
    $desktopResult = (Get-Content -LiteralPath $desktopReport -Raw | ConvertFrom-Json).totals
}
$summary = [ordered]@{
    verifiedAt = [DateTimeOffset]::Now.ToString('o')
    exe = @{path=$Exe;sha256=(Get-FileHash -LiteralPath $Exe).Hash}
    runner = @{path=$Runner;sha256=(Get-FileHash -LiteralPath $Runner).Hash}
    mpv = @{path=$Mpv;sha256=(Get-FileHash -LiteralPath $Mpv).Hash}
    regression = (Get-Content -LiteralPath $targetReport -Raw | ConvertFrom-Json).totals
    flv = @{videoDecodedFrames=$videoFrames;audioDecodedFrames=$audioFrames;timeline='verified: two resets with overlap, independent audio/video'}
    image = $memory
    desktop = $desktopResult
    physicalMixedDpi = $physical
    onlinePlatforms = 'unverified: experiments use synthetic local inputs'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Workspace 'summary.json') -Encoding utf8
Write-Output "实验通过：视频解码 $videoFrames 帧，音频解码 $audioFrames 帧；混合 DPI 跨屏=$physical"
