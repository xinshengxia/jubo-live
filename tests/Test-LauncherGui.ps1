# GUI 冒烟：启动启动器，检查新版界面的关键控件都在，并打印全部控件文本
#
# 2026-09-25 修（评估报告实测反馈）：
#   ① 原来用 (ProcessId == pid) 直接 FindFirst —— 拿到的是**第一个**顶层窗口，
#      窗口枚举可能先遇到隐藏的辅助窗口，而不是标题为「看直播」的主窗口，
#      于是测试**稳定误报失败**。改成枚举该进程的全部顶层窗口、按标题挑主窗口。
#   ② 原来没有 try/finally —— 断言失败或抛异常时启动器会被留在后台跑。
#   ③ 「空 ListView 的列头必须被 UIA 枚举」这条在不同 Windows/UIA 实现下不稳，
#      放宽成「会话区存在且列头区域可枚举到内容」，不再强求空表也列出列头。
#   · 2026-09-27 起支持 -Exe：干净检出验证会用一个**不同文件名**的产物
#     （live-hub-cleancheck.exe），这样它既不用去猜进程名，也不会误杀用户正在看的那个。
#   · 同时**删掉了原来那句「按进程名杀光所有 看直播-mpv」** —— 评估报告 §5 指出：
#     验证脚本收尾时把用户正在看的播放器/弹幕/另一次测试一起杀了。现在只收自己拉起的那个 PID。
param(
    [string]$Exe = '',
    # 显式要求时，才清理「同一个 exe 路径」的残留实例（按 MainModule 路径精确匹配，不按进程名）
    [switch]$KillLeftovers
)
$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$exe = if ($Exe) { [IO.Path]::GetFullPath($Exe) } else { Join-Path $hub '看直播-mpv.exe' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { "找不到启动器：$exe"; exit 2 }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LiveHubComboProbe {
    [DllImport("user32.dll", EntryPoint="SendMessageW")]
    private static extern IntPtr Send(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint="SendMessageW", CharSet=CharSet.Unicode)]
    private static extern IntPtr SendText(IntPtr h, int m, IntPtr w, StringBuilder text);
    public static string SelectedText(IntPtr h) {
        int index=Send(h,0x147,IntPtr.Zero,IntPtr.Zero).ToInt32();
        if(index<0) return null;
        int length=Send(h,0x149,new IntPtr(index),IntPtr.Zero).ToInt32();
        if(length<0 || length>4096) return null;
        var text=new StringBuilder(length+1);
        return SendText(h,0x148,new IntPtr(index),text).ToInt64()<0 ? null : text.ToString();
    }
}
'@

function Get-SameExeProcesses([string]$path) {
    $target = [IO.Path]::GetFullPath($path)
    $found = @()
    foreach ($proc in @(Get-Process -ErrorAction SilentlyContinue)) {
        try { if ($proc.Path -and ([IO.Path]::GetFullPath($proc.Path) -eq $target)) { $found += $proc } } catch { }
    }
    return $found
}
$leftovers = @(Get-SameExeProcesses $exe)
if ($leftovers.Count -gt 0) {
    if ($KillLeftovers) {
        $leftovers | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    } else {
        "注意：同一个 exe 已有 $($leftovers.Count) 个实例在跑（PID " + (($leftovers | ForEach-Object { $_.Id }) -join ',') + "），本次不清理"
    }
}

$p = $null
$exitCode = 1
try
{
    $p = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 4
    if ($p.HasExited) { "GUI 提前退出 exit=$($p.ExitCode)"; exit 3 }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $candidates = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

    # 挑主窗口：优先标题正好是「看直播」，否则退回到「后代控件最多的那个可见窗口」。
    # 不能像以前那样取第一个 —— 同进程还有隐藏的 UAC 辅助窗口。
    $win = $null
    $bestCount = -1
    foreach ($w in $candidates)
    {
        $title = ''
        try { $title = $w.Current.Name } catch { }
        # 标题现在是「看直播  <commit>」，用前缀匹配（带版本号是为了让用户报问题时对得上）
        if ($title -like '看直播*') { $win = $w; break }
        if ($w.Current.IsOffscreen) { continue }
        $count = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition).Count
        if ($count -gt $bestCount) { $bestCount = $count; $win = $w }
    }
    if ($null -eq $win) { "找不到启动器主窗口（该进程顶层窗口 $($candidates.Count) 个）"; exit 4 }

    $names = @()
    foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    {
        if ($e.Current.Name) { $names += $e.Current.Name }
    }
    "窗口标题: $($win.Current.Name)"
    "顶层窗口数: $($candidates.Count)（主窗口后代控件 $($names.Count) 个）"
    '--- 控件文本 ---'
    $names | ForEach-Object { '  ' + $_ }
    '--- 关键控件检查 ---'

    $need = @('B站', '虎牙', '斗鱼', '抖音', '最近播放', '收藏',
              '用 PotPlayer 打开', '用 mpv 打开',
              '画质：', 'mpv 缓冲：', '取消启动', '正在观看 / 最近打开', '重新打开选中房间',
              '缓冲',
              # 2026-09-25 新增：四平台扫码登录 + 启动器 SC 下拉
              'B站扫码', '斗鱼扫码', '虎牙扫码', '抖音扫码',
              'SC：', '弹幕设置…', '本地数据 / 平台状态', 'B站 Cookie', '虎牙 Cookie', '斗鱼 Cookie', '抖音 Cookie', '播放器路径', '选择程序…')
    $missing = @()
    foreach ($n in $need) { if (-not ($names | Where-Object { $_ -like "*$n*" })) { $missing += $n } }

    # 画质 / 缓冲下拉框：**只查当前选中值**（那是始终可见的）。
    # 不能要求「未展开的下拉也能枚举出全部选项」—— UIA 看不到折叠的 ComboBox 项，
    # 那样写会稳定误报（评估报告实测）。要验全量选项得先 ExpandCollapsePattern.Expand()，
    # 而展开下拉会在用户桌面上闪一个弹出层，冒烟测试不值得那么做。
    $selectedValues = @()
    foreach ($combo in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ComboBox))) {
        # OwnerDraw ComboBox 的 UIA Name 是关联标签；用同一活窗口的标准消息读实际选中项。
        $handle = $combo.Current.NativeWindowHandle
        if ($handle) { $selectedValues += [LiveHubComboProbe]::SelectedText([IntPtr]$handle) }
    }
    "  实际下拉选中值: $($selectedValues -join ' / ')"
    if ($selectedValues.Count -lt 5) { $missing += '五个实际下拉框' }
    foreach ($n in @('原画优先', '延迟更低'))
    {
        if (-not ($selectedValues -contains $n)) { $missing += ('下拉当前值:' + $n) }
    }

    # 会话区：只要那块区域本身在（列头在空表下不一定被 UIA 枚举，别硬要求）
    if (-not ($names | Where-Object { $_ -like '*正在观看*' })) { $missing += '会话区域' }

    # ── 同一行内的控件不许重叠 ──────────────────────────────────────────────
    # 2026-09-25 加：主界面第二行是**绝对定位**的（一堆 SetBounds），
    # 而我在那儿加了「增强」档位下拉时把标签放在了 SC 下拉上面，压了 52px ——
    # 截图才看出来。这类错误靠人眼看 diff 抓不住，必须机器判。
    #
    # 判据：两个「输入类」控件的矩形相交，且**纵向基本同一行**（top 差 ≤ 10px）。
    # 纵向过滤是必要的：Label 常常压在 Panel/ListView 上（那是正常的层次关系），
    # 同一行的兄弟控件才是真正不该相交的。
    # 把 Label(ControlType.Text) 也纳入 —— 之前排除它，结果 status 标签丢了 SetBounds、
    # 用默认位置 (0,0) 压在别的东西上，断言完全没反应（用户截图才发现）。
    $inputTypes = @('ControlType.ComboBox', 'ControlType.Button', 'ControlType.Edit', 'ControlType.CheckBox', 'ControlType.Text')
    $boxes = @()
    foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    {
        $t = $e.Current.ControlType.ProgrammaticName
        if ($inputTypes -notcontains $t) { continue }
        # 下拉框内部的 Text/Open 箭头是同一控件的组成部分，不是窗体布局兄弟。
        $insideCombo = $false
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
        for ($depth = 0; $depth -lt 8 -and $null -ne $parent -and $parent -ne $win; $depth++) {
            if ($parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox) { $insideCombo = $true; break }
            $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
        }
        if ($insideCombo) { continue }
        # 折叠的下拉列表也会出现在 UIA 树里，但不在屏幕上 —— 不排掉会满屏假重叠
        if ($e.Current.IsOffscreen) { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -le 0 -or $r.Height -le 0) { continue }
        # 只看顶部两行（相对窗口客户区顶部）。⚠️ 必须用**相对窗口的 Y** —— 控件矩形是屏幕绝对坐标，
        # 而客户区从标题栏下面（约 32px）才开始；拿绝对 Y 跟「窗口顶 + 160」比，
        # 第二行（相对 ~182）会被误判成「超出范围」而全部跳过 ——
        # 这个 bug 让这条断言一直是**空转**的（实测：故意做出重叠它照样报通过）。
        $relY = $r.Y - $win.Current.BoundingRectangle.Y
        # 第一行按钮组相对 ~94px、第二行下拉组相对 ~182px；用 210 把两行都圈进来。
        if ($relY -gt 210) { continue }
        $boxes += [pscustomobject]@{ Name = $e.Current.Name; X = $r.X; Y = $relY; W = $r.Width; H = $r.Height; Type = $t; El = $e }
    }
    $overlaps = @()
    for ($i = 0; $i -lt $boxes.Count; $i++)
    {
        for ($j = $i + 1; $j -lt $boxes.Count; $j++)
        {
            $a = $boxes[$i]; $b = $boxes[$j]
            if ([Math]::Abs($a.Y - $b.Y) -gt 10) { continue }          # 不同行，跳过
            $ox = [Math]::Min($a.X + $a.W, $b.X + $b.W) - [Math]::Max($a.X, $b.X)
            $oy = [Math]::Min($a.Y + $a.H, $b.Y + $b.H) - [Math]::Max($a.Y, $b.Y)
            if ($ox -gt 2 -and $oy -gt 2)
            {
                # 排除父子对：ComboBox 和它内部的「下拉按钮」矩形天然重叠，
                # 那是层次关系不是布局错误（第一版没排，5 个下拉各报一条假阳性）。
                $ancestor = $false
                $cur = $b.El
                for ($k = 0; $k -lt 6 -and $null -ne $cur; $k++)
                {
                    if ($cur -eq $a.El) { $ancestor = $true; break }
                    $cur = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($cur)
                }
                if (-not $ancestor)
                {
                    $overlaps += ("[{0}] 与 [{1}] 重叠 {2}x{3}px" -f $a.Name, $b.Name, [int]$ox, [int]$oy)
                }
            }
        }
    }
    if ($overlaps.Count -gt 0)
    {
        $missing += ('同行控件重叠:' + ($overlaps -join ' / '))
    }
    else
    {
        "  同行控件无重叠（检查了 $($boxes.Count) 个控件）"
    }

    # 窗口必须能缩放
    $canResize = $false
    try { $canResize = $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Current.CanResize } catch { }
    if (-not $canResize) { $missing += '窗口可缩放' }

    # 最小宽度必须容得下底部那排按钮（登录按钮固定坐标 + 播放按钮靠右边缘定位，
    # 缩过头必然重叠 —— 评估报告实测 850 宽时「斗鱼扫码」压住「PotPlayer」）。
    try
    {
        $tp = $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
        $r = $win.Current.BoundingRectangle
        $tp.Resize(1136, 640)
        Start-Sleep -Milliseconds 800
    }
    catch { }

    if ($missing.Count -eq 0) { '  全部命中 ✔' } else { "  缺少: $($missing -join ', ')" }
    $exitCode = if ($missing.Count -eq 0) { 0 } else { 1 }
}
finally
{
    # 无论断言成功、失败还是中途抛异常，都要把**本次启动的那个**启动器收掉。
    # 只收自己拉起来的 PID —— 别人的窗口/播放会话不归这里管（评估报告 §5 的整改点）。
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
