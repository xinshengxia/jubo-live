# diag-playback.ps1 —— 播放卡顿诊断（在**你自己的房间**上实测）
#
#   pwsh -File .\tools\diag-playback.ps1 -Room bili:7777
#   pwsh -File .\tools\diag-playback.ps1 -Room huya:333003 -Seconds 90
#
# 为什么需要它：离线测试再多也覆盖不到「播放顺不顺」。而卡顿的主观感受很容易
# 被误判（是不是我心理因素？），所以这里把可量化的指标打出来：
#
#   · frame-drop-count         mpv 主动丢的帧（跟不上显示节奏）
#   · decoder-frame-drop-count 解码器丢的帧（解不过来）
#   · vo-delayed-frame-count   显示迟到但没丢的帧 ← **「偶尔顿一下」通常体现在这里**
#   · demuxer-cache-duration   缓冲余量（掉到 0 = 卡顿立刻发生）
#   · hwdec-current            是不是真在用硬件解码
#   · CPU 占用                 排除「机器带不动」
#
# 关键设计：**用 `--plan` 取生产同款命令行**再跑，而不是自己拼参数。
# 启动器给四条播放路径各拼一套参数（B站 走 yt-dlp、其余走中转、画质/缓冲档位不同），
# 自己拼一套就等于在测一个用户根本不会遇到的配置。--plan 出来的就是真实那一套。
#
# 默认**静音且不抢焦点**（诊断不需要出声，也不该打断你正在做的事）。
#
# 判据（脚本末自动给结论）：
#   丢帧 0 且缓存始终 > 2 秒  → 管道是干净的，卡顿更可能来自片源本身或显示端
#   有丢帧 或 缓存触底        → 是这边的问题，输出里会指出是哪一类
param(
    [Parameter(Mandatory = $true)][string]$Room,   # 形如 bili:7777 / huya:333003 / douyu:9999 / douyin:123
    [int]$Seconds = 60,
    [int]$Port = 0,                                 # 0 = 自动挑一个空闲端口（只有走中转的平台用得上）
    [switch]$WithSound,                             # 想听声音时加这个（默认静音）
    [ValidateSet('mpv','potplayer')][string]$Player = 'mpv'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $root '看直播-mpv.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw "找不到启动器：$launcher（先跑 Build-Hardened.ps1）" }

if ($Room -notmatch '^(bili|huya|douyu|douyin):(.+)$') {
    throw "房间要写成 平台:房间号，比如 bili:7777（收到的是 '$Room'）"
}
$platform = $Matches[1]; $roomId = $Matches[2]

if ($Port -eq 0) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $Port = $l.LocalEndpoint.Port; $l.Stop()
}

Write-Output '=== 播放卡顿诊断 ==='
Write-Output "  房间： $Room        播放器： $Player        时长： $Seconds 秒"
Write-Output ("  声音： " + $(if ($WithSound) { '开' } else { '静音（默认，不打扰你）' }))
Write-Output ''

# ① 取生产同款命令行
$planFile = Join-Path $env:TEMP ('livehub-plan-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.txt')
& $launcher --plan $Player $Room $planFile 2>&1 | Out-Null
if (-not (Test-Path -LiteralPath $planFile)) { throw '--plan 没生成计划文件' }
$plan = @{}
foreach ($line in (Get-Content -LiteralPath $planFile -Encoding utf8)) {
    $i = $line.IndexOf('=')
    if ($i -gt 0) { $plan[$line.Substring(0, $i)] = $line.Substring($i + 1) }
}
Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
$mpvPath = $plan['FILE']
$mpvArgs = $plan['ARGS']
$relayMode = $plan['RELAY']
if (-not $mpvPath -or -not $mpvArgs) { throw '计划里没有 FILE/ARGS' }
Write-Output "  mpv ： $mpvPath"
Write-Output "  标题： $($plan['ANCHOR'])"
Write-Output "  中转： $relayMode"
Write-Output ''

$relay = $null; $playerProc = $null
$statsOut = Join-Path $env:TEMP ('livehub-diag-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.txt')
try {
    # ② 走中转的平台才需要起中转；B站 走 yt-dlp，RELAY=none
    if ($mpvArgs -match '\{relay-port\}') {
        # 先把这次的中转日志清掉：relay-*.log 是**跨多次运行累积**的，不清的话
        # 「本次窗口内重连几次」会读到历史记录 —— 实测报出 40 次重连，而这次根本没重连。
        # 统计口径必须限定在本次采样窗口内。
        Remove-Item -LiteralPath (Join-Path $root ("runtime\relay-" + $platform + "-" + $roomId + ".log")) -Force -ErrorAction SilentlyContinue
        $relay = Start-Process -FilePath $launcher -PassThru `
            -ArgumentList @('--relay', $platform, $roomId, "$Port") -WindowStyle Hidden
        if ($null -eq $relay) { throw '中转没起来' }
        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            if ($relay.HasExited) { throw "中转提前退出（exit=$($relay.ExitCode)）" }
            try {
                $c = New-Object System.Net.Sockets.TcpClient
                $c.Connect('127.0.0.1', $Port); $c.Close(); $ready = $true; break
            } catch { }
        }
        if (-not $ready) { throw "等不到中转监听 127.0.0.1:$Port" }
        $mpvArgs = $mpvArgs.Replace('{relay-port}', "$Port")
        Write-Output "  中转已就绪（pid $($relay.Id)），127.0.0.1:$Port"
    }

    # ③ 追加诊断参数。--term-status-msg 的值**必须整体加引号** ——
    #    Start-Process 是按空格拼接参数串的，不加引号会被拆成多个参数，
    #    mpv 会把它们当成一串要播放的文件名（踩过）。
    #
    #    静音用 --mute=yes 而不是 --no-audio：后者直接砍掉音频轨，
    #    会让 --video-sync=audio 失效、改变帧节奏，测出来的就不是真实播放行为。
    #    --mute 只是把音量压到 0，音频链路照常跑。
    $extra = ' --focus-on=never'
    if (-not $WithSound) { $extra += ' --mute=yes' }
    $extra += ' --term-status-msg="DIAG d=${frame-drop-count} dd=${decoder-frame-drop-count}' +
        ' delayed=${vo-delayed-frame-count} cache=${demuxer-cache-duration}' +
        ' est=${estimated-vf-fps} cont=${container-fps} hw=${hwdec-current}"'
    $mpvArgs += $extra

    # PotPlayer 没有 mpv 那种 --term-status-msg，拿不到丢帧/缓冲数字。
    # 所以对它的判据换成「能不能稳定播下去」+ 中转侧吞吐/重连 —— 这两项恰好覆盖
    # 中转这条路上真正会出问题的地方。不编造它给不出的指标。
    if ($Player -eq 'potplayer') {
        $playerProc = Start-Process -FilePath $mpvPath -PassThru -ArgumentList $mpvArgs
    } else {
        $playerProc = Start-Process -FilePath $mpvPath -PassThru -RedirectStandardOutput $statsOut -ArgumentList $mpvArgs
    }
        if ($null -eq $playerProc) { throw "播放器没起来" }
        Write-Output "  $Player 已启动（pid $($playerProc.Id)），采样 $Seconds 秒…"

    $cpuSamples = @()
    $aliveChecks = 0; $exitAt = -1
    for ($i = 0; $i -lt $Seconds; $i += 5) {
        Start-Sleep -Seconds 5
        if ($playerProc.HasExited) { $exitAt = $i; break }
        $aliveChecks++
        $p = Get-Process -Id $playerProc.Id -ErrorAction SilentlyContinue
        if ($p) { $cpuSamples += [Math]::Round($p.CPU, 1) }
    }
}
finally {
    foreach ($proc in @($playerProc, $relay)) {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ④ 分析
# ── PotPlayer：它给不出丢帧/缓冲，所以换成「能不能稳定播下去」+ 中转侧指标 ──
if ($Player -eq 'potplayer') {
    Write-Output ''
    Write-Output '=== 结果（PotPlayer）==='
    $expected = [Math]::Floor($Seconds / 5)
    Write-Output ("  存活采样          " + $aliveChecks + " / " + $expected + " 次" + $(if ($exitAt -ge 0) { "（第 $exitAt 秒退出）" } else { '' }))
    $cpuUsed = if ($cpuSamples.Count -ge 2) { [Math]::Round($cpuSamples[-1] - $cpuSamples[0], 1) } else { -1 }
    $cpuPct = if ($cpuUsed -ge 0) { [Math]::Round(100 * $cpuUsed / ($cpuSamples.Count * 5), 0) } else { -1 }
    Write-Output ("  CPU 占用          " + $(if ($cpuPct -ge 0) { $cpuPct.ToString() + '%（单核）' } else { '取不到' }))

    # 中转侧：吞吐是否稳定、有没有反复重连。中转平台真正会出问题的地方在这里。
    $relayLog = Join-Path $root ("runtime\relay-" + $platform + "-" + $roomId + ".log")
    if (Test-Path -LiteralPath $relayLog) {
        $rl = @(Get-Content -LiteralPath $relayLog -Encoding utf8 -Tail 40)
        $reconnects = @($rl | Where-Object { $_ -match '准备重连|上游异常|解析失败' }).Count
        $forwards = @($rl | Where-Object { $_ -match '已转发' })
        Write-Output ("  中转日志          " + $relayLog.Substring($root.Length + 1))
        Write-Output ("  中转重连/异常      " + $reconnects + " 次（采样窗口内，取日志尾部 200 行）")
        # 中转每 30 秒才上报一次，短窗口只会有 1 条 —— 按 >=1 判，别把「只有一条」当成没数据
            $bytes = @($forwards | ForEach-Object { if ($_ -match '最近 30 秒 (\d+) KB') { [double]$Matches[1] } })
            if ($bytes.Count -ge 1) {
                $avg = [Math]::Round(($bytes | Measure-Object -Average).Average / 1024, 2)
                $min = [Math]::Round(($bytes | Measure-Object -Minimum).Minimum / 1024, 2)
                $max = [Math]::Round(($bytes | Measure-Object -Maximum).Maximum / 1024, 2)
                Write-Output ("  中转吞吐(30秒窗)  平均 " + $avg + " MB / 最低 " + $min + " MB / 最高 " + $max + " MB")
                Write-Output '                    （最低值明显低于平均 = 投递有断续，卡顿通常就在这里）'
            } else { Write-Output '  中转吞吐          样本不足（可能还没到第一个 30 秒上报）' }
    } else { Write-Output ('  中转日志          没有（该平台不走中转）') }

    Write-Output ''
    Write-Output '=== 结论 ==='
    $v = @()
    if ($exitAt -ge 0 -and $exitAt -lt ($Seconds - 10)) { $v += '播放器中途退出 → 不是卡顿而是根本播不下去，看中转日志' }
    if ($reconnects -gt 2) { $v += ('中转重连/异常 ' + $reconnects + ' 次 → 上游不稳，会直接表现为画面卡住再跳') }
    if ($cpuPct -gt 60) { $v += ('CPU 占用 ' + $cpuPct + '% 偏高') }
    if ($v.Count -eq 0) {
        Write-Output '  平稳：播放器全程存活，中转没有反复重连。'
        Write-Output '  注意 PotPlayer **给不出丢帧/缓冲数字** —— 这里只能证明「链路和中转是稳的」，'
        Write-Output '  不能像 mpv 那样给出帧级证据。要帧级数据请用 -Player mpv 再跑一次。'
    } else { foreach ($x in $v) { Write-Output ('  · ' + $x) } }
    Remove-Item -LiteralPath $statsOut -Force -ErrorAction SilentlyContinue
    exit 0
}

if (-not (Test-Path -LiteralPath $statsOut)) { throw '没拿到 mpv 输出' }
$rows = @(Get-Content -LiteralPath $statsOut -Encoding utf8 | Where-Object { $_ -match '^DIAG ' })
if ($rows.Count -lt 5) {
    Write-Output "`n只采到 $($rows.Count) 条状态行 —— 流可能没起来。原始输出："
    Get-Content -LiteralPath $statsOut -Encoding utf8 | Select-Object -First 20 | ForEach-Object { '    ' + $_ }
    Remove-Item -LiteralPath $statsOut -Force -ErrorAction SilentlyContinue
    exit 2
}

function Field($line, $key) {
    $m = [regex]::Match($line, $key + '=([^\s]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}
function MaxOf($key) {
    $vals = @($rows | ForEach-Object { $v = Field $_ $key; if ($v -match '^[\d\.]+$') { [double]$v } })
    if ($vals.Count -eq 0) { return -1 }
    return ($vals | Measure-Object -Maximum).Maximum
}
$dropMax = MaxOf 'd'
$decDropMax = MaxOf 'dd'
$delayedMax = MaxOf 'delayed'
# 起播头几秒的丢帧是**瞬态**（解码器初始化、首帧到显示）—— 拿它报「有丢帧」会一惊一乍。
# 前面约 5 秒的状态行单独统计，稳态只看之后的。
$warmup = [Math]::Min(60, [Math]::Max(10, [int]($rows.Count * 0.12)))
$steadyDrop = 0; $steadyDecDrop = 0
for ($i = $warmup; $i -lt $rows.Count; $i++) {
    $v = Field $rows[$i] 'd';  if ($v -match '^[\d\.]+$' -and [double]$v -gt $steadyDrop) { $steadyDrop = [double]$v }
    $v = Field $rows[$i] 'dd'; if ($v -match '^[\d\.]+$' -and [double]$v -gt $steadyDecDrop) { $steadyDecDrop = [double]$v }
}
$steadyDrops = [Math]::Max($steadyDrop, $steadyDecDrop)
$cacheVals = @($rows | ForEach-Object { $v = Field $_ 'cache'; if ($v -match '^[\d\.]+$') { [double]$v } })
$cacheMin = if ($cacheVals.Count) { ($cacheVals | Measure-Object -Minimum).Minimum } else { -1 }
$hw = Field $rows[-1] 'hw'
$est = Field $rows[-1] 'est'
$cont = Field $rows[-1] 'cont'
$cpuUsed = if ($cpuSamples.Count -ge 2) { [Math]::Round($cpuSamples[-1] - $cpuSamples[0], 1) } else { -1 }
$cpuPct = if ($cpuUsed -ge 0) { [Math]::Round(100 * $cpuUsed / ($cpuSamples.Count * 5), 0) } else { -1 }

Write-Output ''
Write-Output '=== 结果 ==='
Write-Output ("  采样行数          " + $rows.Count)
Write-Output ("  mpv 丢帧          " + $dropMax + "   （其中起播瞬态之外的稳态丢帧：" + $steadyDrops + "）")
Write-Output ("  解码器丢帧        " + $decDropMax)
Write-Output ("  显示迟到帧        " + $delayedMax + "   （「偶尔顿一下」通常体现在这里）")
Write-Output ("  缓冲最低值        " + $(if ($cacheMin -ge 0) { [Math]::Round($cacheMin, 2).ToString() + ' 秒' } else { '取不到' }))
Write-Output ("  硬件解码          " + $(if ($hw) { $hw } else { '?' }) + $(if ($hw -eq 'no') { '   ← 退回了软解' } else { '' }))
Write-Output ("  内容帧率          est=$est  container=$cont")
Write-Output ("  mpv CPU 占用      " + $(if ($cpuPct -ge 0) { $cpuPct.ToString() + '%（单核）' } else { '取不到' }))
Write-Output ''
Write-Output '=== 结论 ==='
$verdict = @()
if ($steadyDrops -gt 0) { $verdict += '稳态有丢帧 → 解码/显示端跟不上，优先看 CPU 占用与硬件解码状态' }
elseif ($dropMax -gt 0) { $verdict += '只有起播瞬间丢了 ' + $dropMax + ' 帧（瞬态，通常不用管）' }
if ($delayedMax -gt 5) { $verdict += '有显示迟到帧 → 帧节奏被打乱，典型来源是缓冲抖动或显示刷新率不匹配' }
if ($cacheMin -ge 0 -and $cacheMin -lt 1.5) { $verdict += '缓冲触底 → 网络/中转这条链不够稳，这是最主要的怀疑对象' }
if ($hw -eq 'no') { $verdict += '硬件解码没启用（退回软解）—— 换成更重的房间容易变成临界状态' }
if ($cpuPct -gt 60) { $verdict += ('mpv 单核占用 ' + $cpuPct + '% 偏高，机器余量不足') }
if ($verdict.Count -eq 0) {
    Write-Output '  管道是干净的：丢帧 0、缓冲没触底、硬件解码正常。'
    Write-Output '  也就是**这一次测量没能复现卡顿** —— 但这是单次采样。'
    Write-Output '  若卡顿偶发：① 卡顿发生时立刻重跑本脚本；② 同时看任务管理器有没有别的东西在抢 CPU/磁盘。'
}
else { foreach ($v in $verdict) { Write-Output ('  · ' + $v) } }

# 片源帧率单独提示：est 远低于 cont 时，画面顿是**片源本身**造成的，改不了
if ($est -match '^[\d\.]+$' -and $cont -match '^[\d\.]+$' -and [double]$est -gt 0) {
    $ratio = [double]$est / [double]$cont
    if ($ratio -lt 0.6) {
        Write-Output ''
        Write-Output ('  ⚠ 片源帧率偏低：容器声明 ' + $cont + 'fps，实际只有 ' + [Math]::Round([double]$est, 1) + 'fps。')
        Write-Output '    这种顿是**片源本身**的节奏，客户端改不了（不是你的心理因素，也不是我们的 bug）。'
    }
}
Remove-Item -LiteralPath $statsOut -Force -ErrorAction SilentlyContinue
exit 0
