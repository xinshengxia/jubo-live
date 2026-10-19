param(
    [Parameter(Mandatory)][string]$Mpv,
    [Parameter(Mandatory)][string]$Workspace,
    [ValidateRange(5, 45)][int]$Seconds = 15
)
$ErrorActionPreference = 'Stop'
$Mpv = [IO.Path]::GetFullPath($Mpv)
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $Mpv -PathType Leaf)) { throw '找不到指定的 mpv 程序' }
if (Test-Path -LiteralPath $Workspace) { throw '验收目录必须为新目录，避免覆盖已有证据' }
New-Item -ItemType Directory -Path $Workspace | Out-Null

function Mpv-Value([string]$Value) {
    $Value = $Value.Replace('\', '/')
    return '%' + [Text.Encoding]::UTF8.GetByteCount($Value) + '%' + $Value
}

function Invoke-SyntheticMpv([string[]]$PlayerArgs, [string]$Log) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Mpv
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $PlayerArgs) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'mpv 启动失败' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit(($Seconds + 12) * 1000)
        if (-not $completed) {
            $process.Kill()
            $process.WaitForExit()
        }
        [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        if (-not $completed) { throw "合成音视频验收超时，见 $Log" }
        if ($process.ExitCode -ne 0) { throw "mpv 验收失败，退出码 $($process.ExitCode)，见 $Log" }
    } finally { $process.Dispose() }
}

# A synthetic user configuration exercises command-line precedence without reading
# or changing the user's actual mpv configuration or loading global scripts.
$config = Join-Path $Workspace 'synthetic-user.conf'
[IO.File]::WriteAllText($config, "video-sync=display-resample`nframedrop=no`n", [Text.UTF8Encoding]::new($false))
$cases = @(
    @{ Name = 'synthetic-user'; Extra = @() },
    @{ Name = 'high-quality'; Extra = @('--profile=high-quality') }
)
$results = @()
foreach ($case in $cases) {
    $report = Join-Path $Workspace ($case.Name + '.json')
    $options = 'sync_test_report=' + (Mpv-Value $report) + ',sync_test_seconds=' + $Seconds + ',sync_test_case=' + $case.Name
    $playerArgs = @('--no-config', '--load-scripts=no', '--terminal=yes', '--msg-level=all=warn', '--osc=no', '--media-controls=no', '--ytdl=no', "--include=$config")
    $playerArgs += $case.Extra
    # Keep the live policy after includes and rendering profiles, as in the launcher.
    $playerArgs += @('--video-sync=audio', '--framedrop=vo', '--vo=null', '--ao=null', "--script=$PSScriptRoot\mpv-live-sync-probe.lua", "--script-opts=$options", '--audio-file=av://lavfi:sine=frequency=440:sample_rate=48000', 'av://lavfi:testsrc2=size=640x360:rate=60')
    Invoke-SyntheticMpv -PlayerArgs $playerArgs -Log (Join-Path $Workspace ($case.Name + '.log'))
    if (-not (Test-Path -LiteralPath $report)) { throw "mpv 未生成验收报告：$($case.Name)" }
    $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $result.ok) { throw "音视频同步策略验收失败：$($case.Name)，见 $report" }
    $results += $result
    Write-Output ("PASS: {0}, audio/vo 生效，{1} 次有效 A/V 取样，最大差值 {2:N4} 秒" -f $case.Name, $result.validSamples, $result.maxAbsAvsync)
}
[ordered]@{
    ok = $true
    scope = 'Synthetic local media with null video and audio outputs; no platform, physical display, audio driver, or long-duration playback validation.'
    cases = $results
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Workspace 'summary.json') -Encoding utf8
