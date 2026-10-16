param([Parameter(Mandatory)][string]$Mpv, [Parameter(Mandatory)][string]$Workspace,
    [ValidateSet('Unit','Native','All')][string]$Mode = 'All', [string]$TitleScript = '')
$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
if (-not $TitleScript) { $TitleScript = Join-Path $source 'live-title.lua' }
New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
function Mpv-Value([string]$value) { $value = $value.Replace('\','/'); return '%' + [Text.Encoding]::UTF8.GetByteCount($value) + '%' + $value }
function Invoke-TestMpv([string[]]$PlayerArgs, [string]$Log) {
    $quoted = @(@('--terminal=yes') + $PlayerArgs | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    })
    $process = Start-Process -WindowStyle Hidden -FilePath $Mpv -ArgumentList ($quoted -join ' ') -PassThru -RedirectStandardOutput $Log -RedirectStandardError ($Log + '.stderr')
    if (-not $process.WaitForExit(10000)) { $process.Kill($true); throw "mpv 验收超时：$Log" }
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $Log, ($Log + '.stderr') -Encoding UTF8
        throw "mpv 验收失败，退出码 $($process.ExitCode)：$Log"
    }
}
if ($Mode -ne 'Native') {
    $options = 'title_test_source=' + (Mpv-Value $TitleScript) + ',title_test_dir=' + (Mpv-Value $Workspace)
    Invoke-TestMpv -PlayerArgs @('--no-config','--load-scripts=no','--idle=yes','--vo=null','--ao=null','--osc=no',"--script=$PSScriptRoot\live-title-tests.lua","--script-opts=$options") -Log (Join-Path $Workspace 'unit.log')
    Get-Content -LiteralPath (Join-Path $Workspace 'unit.log') -Encoding UTF8
}
if ($Mode -ne 'Unit') {
    $probe = @'
local mp = require 'mp'
local opts = mp.get_property_native('options/script-opts')
mp.add_timeout(2.4, function()
    local file = assert(io.open(opts.title_test_expected, 'rb'))
    local expected = file:read('*a'); file:close()
    local media = mp.get_property('media-title') or ''
    local window = mp.get_property('title') or ''
    local current = expected .. ' ' .. os.date('%Y-%m-%d %H:%M')
    local previous = expected .. ' ' .. os.date('%Y-%m-%d %H:%M', os.time()-3)
    local ok = (media == current or media == previous) and window:find('主播 - ',1,true) == 1 and window:find('640x360',1,true)
    local out = assert(io.open(opts.title_test_report, 'wb'))
    out:write(require('mp.utils').format_json({ok=not not ok, media=media, window=window})); out:close()
    mp.commandv('quit', ok and 0 or 1)
end)
'@
    $probePath = Join-Path $Workspace 'probe.lua'
    [IO.File]::WriteAllText($probePath, $probe, [Text.UTF8Encoding]::new($false))
    foreach ($platform in @('bili','huya','douyu','douyin')) {
        $title = "$platform 直播标题 - 中文，逗号,等号=测试"
        $base = Join-Path $Workspace "$platform-base,中文.txt"
        $media = Join-Path $Workspace "$platform-media,中文.txt"
        $report = Join-Path $Workspace "$platform-native.json"
        [IO.File]::WriteAllText($base, '主播 - ' + $title, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($media, $title, [Text.UTF8Encoding]::new($false))
        $options = 'live_title_base_file=' + (Mpv-Value $base) + ',title_test_expected=' + (Mpv-Value $media) + ',title_test_report=' + (Mpv-Value $report)
        $append = 'live_title_media_file=' + $media.Replace('\','/')
        Invoke-TestMpv -PlayerArgs @('--no-config','--load-scripts=no','--idle=yes','--vo=null','--ao=null','--ytdl=no','--force-media-title=live.flv?t=synthetic',"--script=$TitleScript","--script=$probePath","--script-opts=$options",'av://lavfi:color=c=black:s=640x360:r=25',"--script-opts-append=$append") -Log (Join-Path $Workspace "$platform-native.log")
        if (-not (Test-Path -LiteralPath $report) -or -not (Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json).ok) { throw "真实 mpv 属性验收失败：$platform，见 $Workspace" }
        Write-Output "PASS: $platform 真实 mpv media-title 显示标题/当前时间，顶部保留主播与技术信息"
    }
}
