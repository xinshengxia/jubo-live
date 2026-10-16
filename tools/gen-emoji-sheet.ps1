# gen-emoji-sheet.ps1 —— 生成彩色 emoji 精灵图（构建期跑一次，运行时只查表贴图）
#
# 为什么要这么做：net48 的 GDI/GDI+ 画不出彩色 emoji（实测四条路径彩色像素都是 0），
# 而 Segoe UI Emoji 可能使用 COLRv1（paint graph），需要相应的彩色字体渲染支持。
# 所以改成构建期用无头 Chrome 渲染一次 —— 零运行时依赖。
#
# 产物：
#   <项目>\emoji-sheet.png  彩色 emoji 精灵图（每格一个，48 列）
#   <项目>\emoji-sheet.txt  清单（格子尺寸 + 区段表）—— **运行时会读它**，两边不会对不上
#
# 变体选择符（VS16）：❤(U+2764) 默认是**文字型**（画出来是黑白轮廓），
# ❤️(U+2764 U+FE0F) 才是彩色 emoji。所以对这些 BMP 符号区段额外渲染一份「码点+FE0F」的格子，
# 运行时按后面有没有跟 FE0F 决定用哪一格。
param(
    [string]$OutDir = (Split-Path -Parent $PSScriptRoot),
    [int]$Cell = 40,
    [int]$Columns = 48
)
$ErrorActionPreference = 'Stop'
$chrome = Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'
if (-not (Test-Path $chrome)) { throw "找不到 Chrome：$chrome" }

# 普通区段（按原样渲染）。未分配的码点会渲染成豆腐，但运行时靠字形覆盖检查不会走到它们。
$plainRanges = @(
    @(0x2600, 0x27BF),   # 杂项符号 + 装饰符号
    @(0x2B00, 0x2BFF),   # 杂项符号与箭头
    @(0x2300, 0x23FF),   # 杂项技术符号（⌚⏰⌛⏳⌨ …）
    @(0x1F000, 0x1F0FF), # 麻将 / 多米诺 / 扑克
    @(0x1F100, 0x1F2FF), # 带圈字母数字补充 + 区域指示符（国旗字母）
    @(0x1F300, 0x1F5FF), # 杂项符号和象形文字
    @(0x1F600, 0x1F64F), # 表情
    @(0x1F680, 0x1F6FF), # 交通与地图
    @(0x1F900, 0x1F9FF), # 补充符号
    @(0x1FA70, 0x1FAFF), # 扩展 A
    @(0x00A9, 0x00A9), @(0x00AE, 0x00AE),   # © ®
    @(0x203C, 0x203C), @(0x2049, 0x2049),   # ‼ ⁉
    @(0x2122, 0x2122), @(0x2139, 0x2139),   # ™ ℹ
    @(0x3030, 0x3030), @(0x303D, 0x303D),   # 〰 〽
    @(0x3297, 0x3297), @(0x3299, 0x3299)    # ㊗ ㊙
)
# 需要额外渲染「+FE0F」变体的区段（这些码点默认文字型，加了变体选择符才是彩色 emoji）
$vsRanges = @(
    @(0x2600, 0x27BF),
    @(0x2B00, 0x2BFF),
    @(0x00A9, 0x00A9), @(0x00AE, 0x00AE),
    @(0x203C, 0x203C), @(0x2049, 0x2049),
    @(0x2122, 0x2122), @(0x2139, 0x2139),
    @(0x3030, 0x3030), @(0x303D, 0x303D),
    @(0x3297, 0x3297), @(0x3299, 0x3299)
)

function Expand-Ranges($ranges) {
    $list = New-Object System.Collections.Generic.List[int]
    foreach ($r in $ranges) { for ($c = $r[0]; $c -le $r[1]; $c++) { $list.Add($c) } }
    # 必须逗号包一层：PowerShell 会把返回的 List 拆成 object[]，AddRange 就接不上了
    return ,$list
}
function Format-Ranges($ranges) { ($ranges | ForEach-Object { '{0:X}-{1:X}' -f $_[0], $_[1] }) -join ',' }

# 国旗段：区域指示符（U+1F1E6..U+1F1FF）**必须成对**渲染才是旗子，单渲染一个是「带框字母」。
#
# **部分渲染环境仍会得到字母对（AM / CI / CJ …），而不是旗子** ——
# 原因是 Windows 的 Segoe UI Emoji **根本不含国旗字形**（微软没做这一块），
# Chrome 只能退回渲染成两个带框字母。所以 `🇨🇳` 在 Windows 上**任何程序**（Chrome、B站 自家网页）
# 都显示成「CN」字母框。既然渲染不出旗子，这 676 格纯属浪费，默认关掉。
# 换到含国旗字形的字体/系统时把 $renderFlags 打开即可（运行时逻辑已支持）。
$renderFlags = $false
$flagFrom = 0x1F1E6
$flagTo = 0x1F1FF
$flags = New-Object System.Collections.Generic.List[string]
if ($renderFlags) {
    foreach ($a in $flagFrom..$flagTo) {
        foreach ($b in $flagFrom..$flagTo) {
            $flags.Add(([char]::ConvertFromUtf32($a)) + [char]::ConvertFromUtf32($b))
        }
    }
}

$plain = Expand-Ranges $plainRanges
$vs = Expand-Ranges $vsRanges

# ZWJ 组合序列 + 键帽：这些必须**整串**渲染才是一个 emoji（👨‍👩‍👧 是三个码点用 ZWJ 连起来的），
# 拆开渲染就变成三个独立的人。所以单独一段，运行时按整串查表。
# 写法：码点用 '-' 连接；运行时会**忽略 FE0F** 再匹配，所以带不带变体选择符都能命中。
$sequenceHex = @(
    # —— 家庭 ——
    '1F468-200D-1F469-200D-1F466', '1F468-200D-1F469-200D-1F467',
    '1F468-200D-1F469-200D-1F467-200D-1F466', '1F468-200D-1F469-200D-1F466-200D-1F466',
    '1F468-200D-1F468-200D-1F466', '1F468-200D-1F468-200D-1F467',
    '1F468-200D-1F468-200D-1F467-200D-1F466', '1F468-200D-1F468-200D-1F466-200D-1F466',
    '1F469-200D-1F469-200D-1F466', '1F469-200D-1F469-200D-1F467',
    '1F469-200D-1F469-200D-1F467-200D-1F466', '1F469-200D-1F469-200D-1F467-200D-1F467',
    '1F468-200D-1F466', '1F468-200D-1F467', '1F469-200D-1F466', '1F469-200D-1F467',
    '1F9D1-200D-1F9D1-200D-1F466', '1F9D1-200D-1F9D1-200D-1F467',
    '1F9D1-200D-1F91D-200D-1F9D1', '1F46F-200D-2642-FE0F', '1F46F-200D-2640-FE0F',
    # —— 情侣 / 心 ——
    '1F469-200D-2764-FE0F-200D-1F468', '1F468-200D-2764-FE0F-200D-1F468',
    '1F469-200D-2764-FE0F-200D-1F469', '1F469-200D-2764-FE0F-200D-1F48B-200D-1F468',
    '1F468-200D-2764-FE0F-200D-1F48B-200D-1F468', '1F469-200D-2764-FE0F-200D-1F48B-200D-1F469',
    '2764-FE0F-200D-1F525', '2764-FE0F-200D-1FA79',
    # —— 旗 ——
    '1F3F3-FE0F-200D-1F308', '1F3F3-FE0F-200D-1F26A-FE0F', '1F3F4-200D-2620-FE0F',
    # —— 动物 ——
    '1F43B-200D-2744-FE0F', '1F426-200D-2B1B', '1F408-200D-2B1B', '1F415-200D-1F9BA',
    # —— 职业：男 / 女 / 中性，各来一遍 ——
    '1F468-200D-2695-FE0F', '1F469-200D-2695-FE0F', '1F9D1-200D-2695-FE0F',
    '1F468-200D-1F393', '1F469-200D-1F393', '1F9D1-200D-1F393',
    '1F468-200D-1F3EB', '1F469-200D-1F3EB', '1F9D1-200D-1F3EB',
    '1F468-200D-1F4BB', '1F469-200D-1F4BB', '1F9D1-200D-1F4BB',
    '1F468-200D-1F373', '1F469-200D-1F373', '1F9D1-200D-1F373',
    '1F468-200D-1F680', '1F469-200D-1F680', '1F9D1-200D-1F680',
    '1F468-200D-2708-FE0F', '1F469-200D-2708-FE0F', '1F9D1-200D-2708-FE0F',
    '1F468-200D-1F3A8', '1F469-200D-1F3A8', '1F9D1-200D-1F3A8',
    '1F468-200D-1F3ED', '1F469-200D-1F3ED', '1F9D1-200D-1F3ED',
    '1F468-200D-1F4BC', '1F469-200D-1F4BC', '1F9D1-200D-1F4BC',
    '1F468-200D-1F527', '1F469-200D-1F527', '1F9D1-200D-1F527',
    '1F468-200D-1F52C', '1F469-200D-1F52C', '1F9D1-200D-1F52C',
    '1F468-200D-1F3A4', '1F469-200D-1F3A4', '1F9D1-200D-1F3A4',
    '1F468-200D-1F692', '1F469-200D-1F692', '1F9D1-200D-1F692',
    '1F468-200D-2696-FE0F', '1F469-200D-2696-FE0F', '1F9D1-200D-2696-FE0F',
    '1F468-200D-1F33E', '1F469-200D-1F33E', '1F9D1-200D-1F33E',
    '1F468-200D-1F3E2', '1F469-200D-1F3E2',
    '1F46E-200D-2642-FE0F', '1F46E-200D-2640-FE0F',
    '1F482-200D-2642-FE0F', '1F482-200D-2640-FE0F',
    '1F575-FE0F-200D-2642-FE0F', '1F575-FE0F-200D-2640-FE0F',
    '1F477-200D-2642-FE0F', '1F477-200D-2640-FE0F',
    # —— 活动：男女版本 ——
    '1F3C3-200D-2640-FE0F', '1F3C3-200D-2642-FE0F',
    '1F3C4-200D-2640-FE0F', '1F3C4-200D-2642-FE0F',
    '1F6B4-200D-2640-FE0F', '1F6B4-200D-2642-FE0F',
    '1F6B5-200D-2640-FE0F', '1F6B5-200D-2642-FE0F',
    '1F3CB-FE0F-200D-2640-FE0F', '1F3CB-FE0F-200D-2642-FE0F',
    '1F3CC-FE0F-200D-2640-FE0F', '1F3CC-FE0F-200D-2642-FE0F',
    '1F938-200D-2640-FE0F', '1F938-200D-2642-FE0F',
    '1F93C-200D-2640-FE0F', '1F93C-200D-2642-FE0F',
    '1F93D-200D-2640-FE0F', '1F93D-200D-2642-FE0F',
    '1F93E-200D-2640-FE0F', '1F93E-200D-2642-FE0F',
    '1F939-200D-2640-FE0F', '1F939-200D-2642-FE0F',
    '1F9D8-200D-2640-FE0F', '1F9D8-200D-2642-FE0F',
    '1F9D6-200D-2640-FE0F', '1F9D6-200D-2642-FE0F',
    '1F9D7-200D-2640-FE0F', '1F9D7-200D-2642-FE0F',
    '1F9B8-200D-2640-FE0F', '1F9B8-200D-2642-FE0F',
    '1F9B9-200D-2640-FE0F', '1F9B9-200D-2642-FE0F',
    '1F9CD-200D-2640-FE0F', '1F9CD-200D-2642-FE0F',
    '1F9CE-200D-2640-FE0F', '1F9CE-200D-2642-FE0F',
    # —— 中性人 / 辅助 ——
    '1F9D1-200D-1F384', '1F9D1-200D-1F37C',
    '1F9D1-200D-1F9AF', '1F9D1-200D-1F9BC', '1F9D1-200D-1F9BD',
    # —— 发型：中性 / 男 / 女 ——
    '1F9D1-200D-1F9B0', '1F9D1-200D-1F9B1', '1F9D1-200D-1F9B2', '1F9D1-200D-1F9B3',
    '1F468-200D-1F9B0', '1F468-200D-1F9B1', '1F468-200D-1F9B2', '1F468-200D-1F9B3',
    '1F469-200D-1F9B0', '1F469-200D-1F9B1', '1F469-200D-1F9B2', '1F469-200D-1F9B3',
    # —— 键帽（0-9 # *）——
    '0030-FE0F-20E3', '0031-FE0F-20E3', '0032-FE0F-20E3', '0033-FE0F-20E3', '0034-FE0F-20E3',
    '0035-FE0F-20E3', '0036-FE0F-20E3', '0037-FE0F-20E3', '0038-FE0F-20E3', '0039-FE0F-20E3',
    '0023-FE0F-20E3', '002A-FE0F-20E3'
)
$sequenceChars = New-Object System.Collections.Generic.List[string]
foreach ($hex in $sequenceHex) {
    $builder = New-Object Text.StringBuilder
    foreach ($part in $hex.Split('-')) { [void]$builder.Append([char]::ConvertFromUtf32([Convert]::ToInt32($part, 16))) }
    $sequenceChars.Add($builder.ToString())
}

$all = New-Object System.Collections.Generic.List[int]
$all.AddRange($plain)
$all.AddRange($vs)
$flagStart = $all.Count
for ($k = 0; $k -lt $flags.Count; $k++) { $all.Add(0) }   # 占位，后面单独生成字符
$seqStart = $all.Count
for ($k = 0; $k -lt $sequenceChars.Count; $k++) { $all.Add(0) }
Write-Output ("普通格 $($plain.Count) + 变体格 $($vs.Count) + 国旗格 $($flags.Count) + 序列格 $($sequenceChars.Count) = 共 $($all.Count) 格，每格 ${Cell}px")

$rows = [Math]::Ceiling($all.Count / $Columns)
$width = $Columns * $Cell
$height = $rows * $Cell

$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine('<!doctype html><meta charset="utf-8"><style>')
[void]$sb.AppendLine('html,body{margin:0;padding:0;background:transparent;}')
[void]$sb.AppendLine('table{border-collapse:collapse;table-layout:fixed;}')
[void]$sb.AppendLine("td{width:${Cell}px;height:${Cell}px;padding:0;margin:0;text-align:center;vertical-align:middle;")
[void]$sb.AppendLine("   font-family:'Segoe UI Emoji';font-size:$([int]($Cell*0.80))px;line-height:${Cell}px;color:#fff;}")
[void]$sb.AppendLine('</style><table>')
$i = 0
foreach ($cp in $all) {
    if ($i -lt $flagStart) {
        $ch = [char]::ConvertFromUtf32($cp)
        if ($i -ge $plain.Count) { $ch = $ch + [char]0xFE0F }   # 变体段：码点后面接 FE0F
    } elseif ($i -lt $seqStart) {
        $ch = $flags[$i - $flagStart]                            # 国旗段：两个区域指示符拼一起
    } else {
        $ch = $sequenceChars[$i - $seqStart]                     # 序列段：ZWJ / 键帽整串
    }
    if ($i % $Columns -eq 0) { [void]$sb.Append('<tr>') }
    [void]$sb.Append("<td>$ch</td>")
    $i++
    if ($i % $Columns -eq 0) { [void]$sb.AppendLine('</tr>') }
}
if ($i % $Columns -ne 0) { [void]$sb.AppendLine('</tr>') }
[void]$sb.AppendLine('</table>')

$tmpHtml = Join-Path $env:TEMP 'emoji-sheet.html'
[IO.File]::WriteAllText($tmpHtml, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
Write-Output ("HTML: $tmpHtml  画布 ${width}x${height}")

$shot = Join-Path $env:TEMP 'emoji-sheet.png'
Remove-Item $shot -ErrorAction SilentlyContinue
$profile = Join-Path $env:TEMP ('chrome-emoji-' + [guid]::NewGuid().ToString('N'))
# --default-background-color=00000000 是关键：不加的话截出来是不透明黑底，贴上去就是黑方块
& $chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
    --default-background-color=00000000 --user-data-dir="$profile" `
    --window-size="$width,$height" --screenshot="$shot" "file:///$($tmpHtml -replace '\\','/')" 2>&1 | Out-Null
Start-Sleep -Seconds 2
if (-not (Test-Path $shot)) { throw "Chrome 没生成截图：$shot" }

$pngOut = Join-Path $OutDir 'emoji-sheet.png'
$txtOut = Join-Path $OutDir 'emoji-sheet.txt'
Copy-Item $shot $pngOut -Force

$manifest = @(
    '# 彩色 emoji 精灵图清单 —— 运行时（danmaku-7-emoji.cs）会读这个文件，',
    '# 这样格子尺寸与区段表只有一处定义，不会出现「图换了、代码没换」的错位。',
    '# 由 tools/gen-emoji-sheet.ps1 生成，勿手改。',
    "CELL=$Cell",
    "COLUMNS=$Columns",
    "PLAINCOUNT=$($plain.Count)",
    "VSCOUNT=$($vs.Count)",
    "FLAGSTART=$flagStart",
    "FLAGCOUNT=$($flags.Count)",
    "FLAGBASE=$('{0:X}' -f $flagFrom)",
    "SEQSTART=$seqStart",
    "SEQCOUNT=$($sequenceChars.Count)",
    "PLAIN=$(Format-Ranges $plainRanges)",
    "VS=$(Format-Ranges $vsRanges)",
    "SEQ=$($sequenceHex -join ',')"
)
[IO.File]::WriteAllLines($txtOut, $manifest, (New-Object Text.UTF8Encoding($false)))
Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ("已生成 " + $pngOut + "（" + [math]::Round((Get-Item $pngOut).Length / 1MB, 2) + " MB，" + $width + "x" + $height + "）")
Write-Output ("已生成 " + $txtOut)
Write-Output ("解码后位图内存约 " + [math]::Round($width * $height * 4 / 1MB, 1) + " MB")
