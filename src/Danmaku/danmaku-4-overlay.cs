// --------------------------------------------------------------------------------------------
// 2026-09-25：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// （在此之前这些模块是被生成器拼进 live-hub-v2.cs 的，靠宿主文件的 using 才编得过。）
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
// --------------------------------------------------------------------------------------------
// 覆盖层窗口：分层 + 点击穿透 + 跟随播放器窗口 + 托盘菜单 + 全局热键
//
// 实测要点（Build 29671 / 200% 缩放）：
//  · 必须声明 PerMonitorV2，否则 GetWindowRect 拿到的是被 DPI 虚拟化的坐标（差约 2 倍）。
//  · 取目标矩形用 DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)，它比 GetWindowRect
//    少一圈不可见阴影/边框（实测左右下各少 11px）。
//  · mpv 顶层窗口类名是 "mpv"，PotPlayer 是 "PotPlayer64"；两者都没有「可直接挂载的视频子窗口」
//    或脚本注入通道，所以覆盖层是独立顶层窗口 + 跟随。
// --------------------------------------------------------------------------------------------
internal sealed class DanmakuOverlayForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;

    private const int WmHotkey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseActivate = 0x0021;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmMouseMove = 0x0200;
    private const int WmSetCursor = 0x0020;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int MaNoActivate = 3;
    private const uint GwHwndPrev = 3;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
    private const uint SwpNoZOrder = 0x0004;
    private const int UlwAlpha = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const int HotkeyToggleDanmaku = 0xA001;
    private const int HotkeyCycleSc = 0xA002;
    private const int HotkeyToggleOverlay = 0xA003;
    private const int HotkeyFontUp = 0xA004;
    private const int HotkeyFontDown = 0xA005;
    private const int HotkeyAreaUp = 0xA006;
    private const int HotkeyAreaDown = 0xA007;
    private const int HotkeySpeedUp = 0xA008;
    private const int HotkeySpeedDown = 0xA009;
    private const int HotkeyDelayLess = 0xA00A;
    private const int HotkeyDelayMore = 0xA00B;

    // B站 播放器的档位：字号「超小 / 小 / 标准 / 大 / 超大」，显示区域「1/4 屏 / 半屏 / 3/4 屏 / 全屏」
    private static readonly int[] FontSteps = new int[] { 60, 80, 100, 130, 170 };
    private static readonly string[] FontLabels = new string[] { "超小", "小", "标准", "大", "超大" };
    private static readonly int[] AreaSteps = new int[] { 25, 50, 75, 100 };
    private static readonly string[] AreaLabels = new string[] { "1/4 屏", "半屏", "3/4 屏", "全屏" };
    private static readonly int[] OpacitySteps = new int[] { 100, 75, 50, 25 };
    private static readonly int[] SpeedSteps = new int[] { 60, 80, 100, 130, 170 };
    private static readonly string[] SpeedLabels = new string[] { "很慢", "慢", "标准", "快", "很快" };
    private static readonly int[] DensitySteps = new int[] { 400, 150, 80, 40 };
    private static readonly string[] DensityLabels = new string[] { "不限", "正常", "较少", "最少" };

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);
    [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
    /// 彩色 emoji 精灵图（由 scripts\gen-emoji-sheet.ps1 构建期生成）：
    /// 便携包在 exe 旁边或 data\ 下，开发机在 live-hub 根目录。
    public static string EmojiSheetPath()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string[] candidates = new string[]
        {
            Path.Combine(dir, "emoji-sheet.png"),
            Path.Combine(dir, @"data\emoji-sheet.png"),
            Path.Combine(dir, @"lua\emoji-sheet.png")
        };
        for (int i = 0; i < candidates.Length; i++)
            if (File.Exists(candidates[i])) return candidates[i];
        return candidates[0];
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceAlpha, AlphaFormat; }

    private readonly DanmakuConfig _config;
    private readonly string _configPath;
    private readonly int _targetPid;
    private readonly string _title;
    private readonly DanmakuRenderer _renderer;
    private readonly object _gate = new object();
    private readonly Queue<object> _inbox = new Queue<object>();

    private IntPtr _targetHwnd = IntPtr.Zero;
    private Bitmap _canvas;
    private Graphics _canvasGraphics;
    // 弹幕延迟补偿队列。
    //
    // 为什么需要：弹幕是**实时到达**的（平台协议一推就推过来），而视频是**缓冲之后**
    // 才显示的。实测各平台缓冲深度 B站 3.5s / 抖音 3.9s / 虎牙 2.6s / 斗鱼 2.6s ——
    // 不补偿的话弹幕会领先画面 2.5~4 秒，等于**剧透**：
    // 先看到「哈哈哈他没了」，几秒后画面里才没。
    //
    // 做法：到达后不直接交给渲染器，先按 ReleaseAt 排队，到点再放。
    // 这样防重叠逻辑只看到「真正要显示的」弹幕，不受影响。
    private struct PendingDanmaku
    {
        public DanmakuItem Item;
        public DateTime ReleaseAt;
    }
    private readonly List<PendingDanmaku> _pendingDanmaku = new List<PendingDanmaku>();

    private System.Windows.Forms.Timer _tick;
    private DateTime _lastFrame = DateTime.UtcNow;
    private DateTime _lastConfigCheck = DateTime.MinValue;
    private DateTime _lastConfigWrite = DateTime.MinValue;
    private NotifyIcon _tray;
    private bool _overlayVisible = true;
    private bool _userHidden;                       // 用户用热键/托盘手动隐藏的（与「不在前台」区分开）
    private string _statusText = "";
    private RECT _lastRect;
    private int? _windowMissingTick;
    private bool _loggedWindowMissing;
    private Process _targetProcess;
    private bool _targetProcessMissing;
    private int _lastProcessCheck;
    private bool? _targetProcessAlive;

    /// 开发/验收用：把覆盖层画布按时间点直接存成 PNG。
    /// 好处是**完全不依赖窗口层级**（截图会被别的窗口挡住、SetForegroundWindow 又常被系统拒绝）。
    public string ShotPath;
    private readonly List<int> _shotAtSeconds = new List<int>(new int[] { 5, 12, 20, 40, 60 });
    private int _shotIndex;
    private DateTime _startedAt = DateTime.UtcNow;

    public DanmakuOverlayForm(DanmakuConfig config, string configPath, int targetPid, string title)
    {
        _config = config;
        _configPath = configPath;
        _targetPid = targetPid;
        try { _targetProcess = Process.GetProcessById(targetPid); }
        catch (ArgumentException) { _targetProcessMissing = true; }
        catch (Exception ex) { Diagnostics.Report("播放器进程查询", ex); }
        _title = title;
        _renderer = new DanmakuRenderer(config);
        // 告诉 LiveHub「本进程是覆盖层」：出错时不弹模态框，改成画面顶部飘一行字
        // （模态框会挡住游戏/直播，还会把渲染循环停住等用户点确定）。
        LiveHub.OverlayMessage = _renderer.ShowTransient;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = title;
        Bounds = new Rectangle(-4000, -4000, 640, 360);   // 先待在屏幕外，避免闪一下
        BackColor = Color.Black;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // 注意这里**故意不加**两个东西：
            //  · WS_EX_TRANSPARENT —— 加了整个窗口都收不到鼠标，SC 就没法用鼠标点着折叠。
            //    改成在 WM_NCHITTEST 里对「非 SC 区域」返回 HTTRANSPARENT，把点击放行给下面的播放器，
            //    只有 SC 卡片那几块矩形吃鼠标事件。
            //  · WS_EX_TOPMOST —— 加了会压在所有程序之上。默认改成「贴在播放器正上方」（见 EnsureZOrder），
            //    播放器被别的窗口盖住时弹幕一起被盖住，不会一直霸着最前面。
            parameters.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotkeys();
        base.OnHandleDestroyed(e);
    }

    // ------------------------------------------------------------------ 对外接口（可从任意线程调用）
    public void PostDanmaku(DanmakuItem item)
    {
        lock (_gate) { _inbox.Enqueue(item); }
    }
    public void PostLiveEvent(LiveEvent item) { lock (_gate) { _inbox.Enqueue(item); } }

    public void PostSuperChat(SuperChatItem item)
    {
        lock (_gate) { _inbox.Enqueue(item); }
    }

    public void PostSuperChatDelete(long id)
    {
        lock (_gate) { _inbox.Enqueue(new ScDelete(id)); }
    }

    public void PostStatus(string text)
    {
        lock (_gate) { _inbox.Enqueue(new StatusNote(text)); }
    }

    /// 表情字典拉好了。走 inbox 而不是直接调渲染器 —— 字典是在客户端线程填的，
    /// 而 SetEmoticons 会清文本位图缓存，必须在 UI 线程做。
    public void PostEmoticons(EmoticonDictionary dict)
    {
        lock (_gate) { _inbox.Enqueue(new EmoticonsNote(dict)); }
    }

    private sealed class ScDelete { public ScDelete(long id) { Id = id; } public long Id; }
    private sealed class StatusNote { public StatusNote(string text) { Text = text; } public string Text; }
    private sealed class EmoticonsNote { public EmoticonsNote(EmoticonDictionary dict) { Dict = dict; } public EmoticonDictionary Dict; }

    // ------------------------------------------------------------------ 启动
    public void Start()
    {
        Show();
        _canvas = new Bitmap(640, 360, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _canvasGraphics = Graphics.FromImage(_canvas);
        SetTray();

        _tick = new System.Windows.Forms.Timer();
        _tick.Interval = 33;                       // ≈30fps；弹幕场景足够，也给主渲染留余量
        _tick.Tick += delegate { Frame(); };
        _tick.Start();
        _renderer.ShowTransient(_config.Enabled ? "弹幕已开启" : "弹幕已关闭");
    }

    private void RegisterHotkeys()
    {
        TryHotkey(HotkeyToggleDanmaku, _config.HotkeyToggleDanmaku);
        TryHotkey(HotkeyCycleSc, _config.HotkeyCycleSc);
        TryHotkey(HotkeyToggleOverlay, _config.HotkeyToggleOverlay);
        TryHotkey(HotkeyFontUp, _config.HotkeyFontUp);
        TryHotkey(HotkeyFontDown, _config.HotkeyFontDown);
        TryHotkey(HotkeyAreaUp, _config.HotkeyAreaUp);
        TryHotkey(HotkeyAreaDown, _config.HotkeyAreaDown);
        TryHotkey(HotkeySpeedUp, _config.HotkeySpeedUp);
        TryHotkey(HotkeySpeedDown, _config.HotkeySpeedDown);
        TryHotkey(HotkeyDelayLess, _config.HotkeyDelayLess);
        TryHotkey(HotkeyDelayMore, _config.HotkeyDelayMore);
    }

    private void TryHotkey(int id, string spec)
    {
        uint modifiers, key;
        if (!ParseHotkey(spec, out modifiers, out key)) return;
        try
        {
            // ⚠️ RegisterHotKey **失败时返回 false 而不是抛异常** —— 原来不看返回值，
            //    于是「热键被别的程序占了」这种情况完全静默：用户按了没反应，日志里也没有。
            if (!RegisterHotKey(Handle, id, modifiers, key))
                DanmakuHost.Log("热键注册失败（多半被别的程序占了）：" + spec);
        }
        catch (Exception ex) { DanmakuHost.Log("热键注册异常 " + spec + "：" + ex.Message); }
    }

    private void UnregisterHotkeys()
    {
        foreach (int id in new int[] { HotkeyToggleDanmaku, HotkeyCycleSc, HotkeyToggleOverlay,
            HotkeyFontUp, HotkeyFontDown, HotkeyAreaUp, HotkeyAreaDown,
            HotkeySpeedUp, HotkeySpeedDown, HotkeyDelayLess, HotkeyDelayMore })
            try { UnregisterHotKey(Handle, id); } catch (Exception) { }
    }

    private static bool ParseHotkey(string spec, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (spec == null) return false;
        string[] parts = spec.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0) continue;
            string upper = token.ToUpperInvariant();
            if (upper == "CTRL" || upper == "CONTROL") modifiers |= 0x0002;
            else if (upper == "ALT") modifiers |= 0x0001;
            else if (upper == "SHIFT") modifiers |= 0x0004;
            else if (upper == "WIN") modifiers |= 0x0008;
            else if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= 'A' && c <= 'Z') key = (uint)c;
                else if (c >= '0' && c <= '9') key = (uint)c;
                // ⚠️ 方括号必须写在**这个分支里面**：token.Length == 1 会先命中，
                //    写在分支外面（原来那样）永远到不了 → 热键静默失效。
                //    是 TestEnhanceConfig 的解析断言抓出来的。
                else if (token == "[") key = 0xDB;      // VK_OEM_4
                else if (token == "]") key = 0xDD;      // VK_OEM_6
            }
            else if (upper.Length >= 2 && upper[0] == 'F')
            {
                int index;
                if (int.TryParse(upper.Substring(1), out index) && index >= 1 && index <= 24)
                    key = (uint)(0x70 + index - 1);
            }
            else if (upper == "UP") key = 0x26;
            else if (upper == "DOWN") key = 0x28;
            else if (upper == "LEFT") key = 0x25;
            else if (upper == "RIGHT") key = 0x27;
            else if (upper == "PGUP") key = 0x21;
            else if (upper == "PGDN") key = 0x22;
            // 弹幕延迟微调用的方括号。原来只认字母/数字/F 键/方向键，
            // 而 [ ] 在「延迟加减」这个语义上最直观。
        }
        return key != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyToggleDanmaku) ToggleDanmaku();
            else if (id == HotkeyCycleSc) CycleSuperChatMode();
            else if (id == HotkeyToggleOverlay) ToggleOverlay();
            else if (id == HotkeyFontUp) StepFont(+1);
            else if (id == HotkeyFontDown) StepFont(-1);
            else if (id == HotkeyAreaUp) StepArea(+1);
            else if (id == HotkeyAreaDown) StepArea(-1);
            else if (id == HotkeySpeedUp) StepSpeed(+1);
            else if (id == HotkeySpeedDown) StepSpeed(-1);
            else if (id == HotkeyDelayLess) StepDelay(-500);
            else if (id == HotkeyDelayMore) StepDelay(+500);
        }
        else if (m.Msg == WmNcHitTest)
        {
            // 只有 SC 卡片那几块矩形吃鼠标；其余一律 HTTRANSPARENT 放行给下面的播放器。
            // 这就是「覆盖层不再整窗穿透、但也不挡播放器操作」的实现方式。
            int packed = m.LParam.ToInt32();
            POINT point = new POINT();
            point.x = (short)(packed & 0xFFFF);
            point.y = (short)((packed >> 16) & 0xFFFF);
            ScreenToClient(Handle, ref point);
            // 放行判据 = **能点 或 需要滚**。以前只看 ScClickable，
            // 于是关掉点击后滚轮也一起失效了（用户实测反馈）—— 两件事该分开：
            // 关点击是怕误触，不代表不想用滚轮翻看攒下来的卡片。
            bool interactive = _config.ScClickable || _renderer.SuperChatScrollable;
            // 滚动条也算「我们的区域」—— 不然后面那 8px 会被放行给播放器，根本拖不到
            bool overSc = interactive
                && (_renderer.HitTestSc(point.x, point.y) != null || _renderer.HitTestScBar(point.x, point.y));
            m.Result = new IntPtr(overSc ? HtClient : HtTransparent);
            return;
        }
        else if (m.Msg == WmMouseActivate)
        {
            m.Result = new IntPtr(MaNoActivate);     // 点 SC 也不要抢播放器的焦点
            return;
        }
        else if (m.Msg == WmMouseWheel)
        {
            // 只在光标确实位于 SC 卡片区域时滚动 —— 否则会抢走播放器的音量滚轮。
            // （WM_NCHITTEST 已经把非 SC 区域放行给下面了，所以正常情况下滚轮本来就到不了这里；
            //   这一层判断是兜底，避免边缘情况误吃。）
            POINT wheelPoint;
            if (GetCursorPos(out wheelPoint))
            {
                ScreenToClient(Handle, ref wheelPoint);
                if (_renderer.HitTestSc(wheelPoint.x, wheelPoint.y) != null)
                {
                    int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                    _renderer.ScrollSuperChats(delta);
                    return;
                }
            }
        }
        else if (m.Msg == WmLButtonDown)
        {
            // 滚动条：按下就开始拖（命中轨道会先把滑块跳过去）。吃掉了就不再当点击处理。
            // ⚠️ 2026-09-28 新增：之前滚动条只是画出来的装饰、不参与命中，
            //    用户只能靠滚轮；现在可以按住拖动（用户要求「有个滚动条，我可以控制滚动看」）。
            POINT downPoint;
            if (GetCursorPos(out downPoint))
            {
                ScreenToClient(Handle, ref downPoint);
                if ((_config.ScClickable || _renderer.SuperChatScrollable)
                    && _renderer.ScBarMouseDown(downPoint.x, downPoint.y))
                {
                    _scBarDragged = true;
                    Capture = true;        // 拖出窗口范围也能继续拖
                    return;
                }
            }
            _scBarDragged = false;
        }
        else if (m.Msg == WmMouseMove)
        {
            if (_renderer.ScBarDragging)
            {
                POINT movePoint;
                if (GetCursorPos(out movePoint))
                {
                    ScreenToClient(Handle, ref movePoint);
                    _renderer.ScBarDragTo(movePoint.y);
                    return;
                }
            }
        }
        else if (m.Msg == WmLButtonUp)
        {
            bool wasBar = _scBarDragged || _renderer.ScBarDragging;
            _renderer.ScBarMouseUp();
            if (Capture) Capture = false;
            _scBarDragged = false;
            // 拖过滚动条就不要当点击 —— 否则松开手会顺手把 SC 关掉或切换形态
            if (!wasBar && _config.ScClickable) HandleScClick();
            return;
        }
        else if (m.Msg == WmSetCursor)
        {
            POINT point;
            if (GetCursorPos(out point))
            {
                ScreenToClient(Handle, ref point);
                if ((_config.ScClickable || _renderer.SuperChatScrollable)
                    && _renderer.HitTestScBar(point.x, point.y))
                {
                    Cursor.Current = Cursors.SizeNS;     // 竖向滚动条 → 上下箭头
                    m.Result = new IntPtr(1);
                    return;
                }
                if ((_config.ScClickable || _renderer.SuperChatScrollable)
                    && _renderer.HitTestSc(point.x, point.y) != null)
                {
                    Cursor.Current = Cursors.Hand;
                    m.Result = new IntPtr(1);
                    return;
                }
            }
        }
        base.WndProc(ref m);
    }

    /// 本次按下是不是落在滚动条上（松开时据此**不**触发点击）
    private bool _scBarDragged;

    /// 点 SC 面板：点在卡片右上角的 × 上就关掉那一条，点其它地方就切换形态。
    /// 插一条**测试用**的 SC 卡片。
    /// 为什么需要它：SC 有时限（几分钟就消失），想确认「卡片显示是否正常」时
    /// 不可能等别人刚好发一条 —— 用户实测反馈过 SC 里的 emoji 变成豆腐块，
    /// 而那条 SC 转眼就过期了，根本没法复看。有了这个入口随时能验。
    ///
    /// 内置的正文**故意包含 emoji，而且长度刚好让折行卡在 emoji 附近** ——
    /// 那正是当初出豆腐块的场景（折行把 UTF-16 代理对劈开了）。
    private void ShowTestSuperChat()
    {
        SuperChatItem item = new SuperChatItem();
        item.Id = -1;                                   // 负数：不会和真实 SC 的 id 撞
        item.Price = 30;
        item.DurationSec = 120;                         // 给足时间慢慢看
        item.Message = "测试卡片：emoji 🎉🎊😀👍 会不会变成豆腐块？"
            + "这条故意写长一点，让折行正好卡在 emoji 中间，看看还会不会出现那个「口」。🎮🔥";
        item.UserName = "测试用户";
        item.UserLevel = 6;
        item.ArrivedAt = DateTime.UtcNow;
        item.ExpiresAt = DateTime.UtcNow.AddSeconds(120);
        _renderer.AddSuperChat(item);   // 注意：测试卡不受 ScMode 影响，SC 关着也能看到
        _renderer.ShowTransient("已插入测试 SC（含 emoji，2 分钟后消失）");
    }

    private void HandleScClick()
    {
        POINT point;
        if (!GetCursorPos(out point)) return;
        ScreenToClient(Handle, ref point);
        DanmakuRenderer.ScHit hit = _renderer.HitTestSc(point.x, point.y);
        if (hit == null) return;
        if (hit.DismissId != 0)
        {
            _renderer.RemoveSuperChat(hit.DismissId);
            _renderer.ShowTransient("已关闭这条 SC");
            return;
        }
        CycleSuperChatMode();
    }

    private void StepFont(int direction)
    {
        int index = NearestIndex(FontSteps, _config.FontScalePercent);
        index = Math.Max(0, Math.Min(FontSteps.Length - 1, index + direction));
        if (FontSteps[index] == _config.FontScalePercent) return;
        _config.FontScalePercent = FontSteps[index];
        _renderer.ApplyConfig();
        SaveConfig();
        _renderer.ShowTransient("弹幕字号：" + FontLabels[index] + "（" + FontSteps[index] + "%）");
    }

    /// 弹幕速度：PgUp 更快 / PgDn 更慢（与字号 / 显示区域的热键同一套手感）
    private void StepSpeed(int direction)
    {
        int index = NearestIndex(SpeedSteps, _config.SpeedPercent);
        index = Math.Max(0, Math.Min(SpeedSteps.Length - 1, index + direction));
        if (SpeedSteps[index] == _config.SpeedPercent) return;
        _config.SpeedPercent = SpeedSteps[index];
        _renderer.ApplyConfig();
        SaveConfig();
        _renderer.ShowTransient("弹幕速度：" + SpeedLabels[index]);
    }

    /// 把到点的弹幕交给渲染器。**顺序很重要**：必须按原始到达顺序释放，
    /// 所以从队首扫到第一个还没到点的就停（队列天然按 ReleaseAt 递增）。
    private void ReleasePendingDanmaku(DateTime now)
    {
        if (_pendingDanmaku.Count == 0) return;
        int released = 0;
        while (_pendingDanmaku.Count > 0 && _pendingDanmaku[0].ReleaseAt <= now)
        {
            if (!_renderer.IsBlocked(_pendingDanmaku[0].Item))
                _renderer.AddDanmaku(_pendingDanmaku[0].Item);
            _pendingDanmaku.RemoveAt(0);
            released++;
            if (released > 2000) break;    // 防呆：延迟突然调大/时钟跳变时别一次灌爆
        }
    }

    /// 调弹幕延迟（毫秒）。步进 500ms —— 各平台缓冲差 2.6~3.9 秒，
    /// 500ms 粒度足够对齐，又不至于要按几十次。
    private void StepDelay(int deltaMs)
    {
        int next = _config.DanmakuDelayMs + deltaMs;
        if (next < 0) next = 0;
        if (next > 15000) next = 15000;
        if (next == _config.DanmakuDelayMs) return;
        _config.DanmakuDelayMs = next;
        SaveConfig();
        if (next == 0) _renderer.ShowTransient("弹幕延迟补偿：关闭");
        else _renderer.ShowTransient("弹幕延迟补偿：" + (next / 1000.0).ToString("0.0") + " 秒（弹幕会等这么久再出现，用来对齐画面）");
    }

    private void StepArea(int direction)
    {
        int index = NearestIndex(AreaSteps, _config.DisplayAreaPercent);
        index = Math.Max(0, Math.Min(AreaSteps.Length - 1, index + direction));
        if (AreaSteps[index] == _config.DisplayAreaPercent) return;
        _config.DisplayAreaPercent = AreaSteps[index];
        _renderer.ApplyConfig();
        SaveConfig();
        _renderer.ShowTransient("弹幕显示区域：" + AreaLabels[index]);
    }

    private static int NearestIndex(int[] values, int value)
    {
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            int distance = Math.Abs(values[i] - value);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------ 每帧
    private void Frame()
    {
        DateTime now = DateTime.UtcNow;
        float dt = (float)(now - _lastFrame).TotalSeconds;
        if (dt <= 0f || dt > 0.5f) dt = 1f / 30f;
        _lastFrame = now;

        DrainInbox();
        ReloadConfigIfChanged();
        // 即使窗口暂时消失也清理过期弹幕，恢复时不会灌出积攒的旧消息。
        ReleasePendingDanmaku(now);
        _renderer.Advance(now, dt);

        int tick = Environment.TickCount;
        if (_targetProcessMissing || unchecked(tick - _lastProcessCheck) >= 1000 || _lastProcessCheck == 0)
        {
            _lastProcessCheck = tick;
            if (_targetProcessMissing) _targetProcessAlive = false;
            else
            {
                try
                {
                    if (_targetProcess == null) _targetProcess = Process.GetProcessById(_targetPid);
                    _targetProcessAlive = !_targetProcess.HasExited;
                }
                catch (ArgumentException) { _targetProcessAlive = false; }
                catch (Exception ex) { _targetProcessAlive = null; Diagnostics.Report("播放器进程查询", ex); }
            }
        }
        if (_targetProcessAlive == false)
        {
            HideOverlay();
            DanmakuHost.Log("播放器进程已退出，弹幕会话结束（pid=" + _targetPid + "）");
            Close();
            return;
        }

        // 目标窗口没了才算「跟丢」；「不在前台」只是暂时藏起来，不能当成退出信号。
        if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) _targetHwnd = FindTargetWindow(_targetPid);
        if (_targetHwnd == IntPtr.Zero)
        {
            HideOverlay();
            if (_windowMissingTick == null) _windowMissingTick = tick;
            int missingMs = unchecked(tick - _windowMissingTick.Value);
            if (!_loggedWindowMissing && missingMs >= 1000)
            {
                _loggedWindowMissing = true;
                DanmakuHost.Log("还没找到播放器窗口（pid=" + _targetPid + "），最多等待 30 秒");
            }
            if (PlayerFollowPolicy.ShouldExit(_targetProcessAlive, missingMs))
            {
                DanmakuHost.Log("播放器窗口消失超过 30 秒，弹幕会话结束（pid=" + _targetPid + "）");
                Close();
            }
            return;
        }
        if (_loggedWindowMissing) DanmakuHost.Log("已重新找到播放器窗口（pid=" + _targetPid + "）");
        _windowMissingTick = null;
        _loggedWindowMissing = false;

        if (IsIconic(_targetHwnd) || _userHidden || !IsOverlayWanted())
        {
            HideOverlay();
            return;
        }
        if (!_overlayVisible) { _overlayVisible = true; ShowWindow(Handle, SwShowNoActivate); }

        RECT rect;
        if (!TryGetTargetRect(out rect)) return;
        if (rect.Right - rect.Left < 16 || rect.Bottom - rect.Top < 16) return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        EnsureCanvas(width, height);
        _renderer.SetDisplayDpi(DpiForm.WindowDpi(this));
        _renderer.Resize(width, height);

        _canvasGraphics.Clear(Color.Transparent);
        _renderer.Draw(_canvasGraphics);
        Blit(rect);
        EnsureZOrder();

        if (!_loggedFirstBlit)
        {
            _loggedFirstBlit = true;
            DanmakuHost.Log("覆盖层就位：目标 hwnd=0x" + _targetHwnd.ToInt64().ToString("X")
                + " 客户区 " + width + "x" + height + "@" + rect.Left + "," + rect.Top
                + " 弹幕数=" + _renderer.ActiveCount + " 缩放=" + _renderer.UiScale.ToString("0.00"));
        }
        SaveShotIfDue();
        LogBitmapStatsIfDue();
    }

    private void HideOverlay()
    {
        if (_overlayVisible) { ShowWindow(Handle, SwHide); _overlayVisible = false; }
    }

    /// 每 10 秒记一次「共享位图存活数」——所有权模型下它必须**收敛**（缓存容量有界），
    /// 一路单调涨就是漏还租约。demo 模式实跑时靠这条看趋势（离线回归只看得到单点）。
    private void LogBitmapStatsIfDue()
    {
        if ((DateTime.UtcNow - _lastStatsAt).TotalSeconds < 10) return;
        _lastStatsAt = DateTime.UtcNow;
        DanmakuHost.Log("位图统计：存活=" + SharedBitmap.LiveCount
            + " 累计释放=" + SharedBitmap.FreedTotal
            + " 弹幕数=" + _renderer.ActiveCount);
        DanmakuHost.Log("弹幕统计：" + _renderer.StatisticsText.Replace("\r\n", "；"));
    }

    private DateTime _lastStatsAt = DateTime.UtcNow;

    private void SaveShotIfDue()
    {
        if (ShotPath == null || _shotIndex >= _shotAtSeconds.Count) return;
        double elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
        if (elapsed < _shotAtSeconds[_shotIndex]) return;
        try
        {
            string path = _shotIndex == 0
                ? ShotPath
                : Path.Combine(Path.GetDirectoryName(ShotPath),
                    Path.GetFileNameWithoutExtension(ShotPath) + "-" + _shotIndex + Path.GetExtension(ShotPath));
            // 画布是带透明通道的，直接存 PNG 的话看的时候会被合成到白底上，
            // 白色弹幕就「看不见」了。这里先垫一层中灰，跟真实视频画面更接近，也方便看描边。
            using (Bitmap flattened = new Bitmap(_canvas.Width, _canvas.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(flattened))
                {
                    g.Clear(Color.FromArgb(255, 118, 122, 128));
                    g.DrawImage(_canvas, 0, 0, _canvas.Width, _canvas.Height);
                }
                flattened.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            DanmakuHost.Log("已存帧 " + path + "（t=" + elapsed.ToString("0.0") + "s，弹幕 " + _renderer.ActiveCount
                + " 条，SC " + _renderer.SuperChatActiveCount + " 条）");
        }
        catch (Exception ex) { DanmakuHost.Log("存帧失败: " + ex.Message); }
        _shotIndex++;
    }

    private bool _loggedFirstBlit;
    private DateTime _lastTopmostAt = DateTime.MinValue;
    private bool _wasTopmost;

    /// 覆盖层的层叠方式。
    ///
    /// 默认 **贴在播放器正上方**（不是 HWND_TOPMOST）：用 `GetWindow(目标, GW_HWNDPREV)` 拿到
    /// 「当前正好压在播放器上面的那个窗口」，把自己插到它后面 —— 结果就是覆盖层紧贴播放器之上，
    /// 而**别的程序盖住播放器时，弹幕也一起被盖住**，不会一直霸着最前面。
    /// 播放器被激活到最前时 `GW_HWNDPREV` 返回 NULL（等价 HWND_TOP），正好又贴回播放器上方。
    ///
    /// 只有 `AlwaysOnTop=true`（放副屏常驻）时才用 HWND_TOPMOST。
    private void EnsureZOrder()
    {
        if (_config.AlwaysOnTop)
        {
            if (!_wasTopmost || (DateTime.UtcNow - _lastTopmostAt).TotalMilliseconds > 800)
            {
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                _wasTopmost = true;
                _lastTopmostAt = DateTime.UtcNow;
            }
            return;
        }
        if (_wasTopmost)
        {
            SetWindowPos(Handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            _wasTopmost = false;
        }
        IntPtr above = GetWindow(_targetHwnd, GwHwndPrev);
        if (above == Handle) return;     // 已经正好贴在播放器之上，省掉每帧一次 SetWindowPos
        SetWindowPos(Handle, above, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private void EnsureCanvas(int width, int height)
    {
        if (_canvas != null && _canvas.Width == width && _canvas.Height == height) return;
        if (_canvasGraphics != null) _canvasGraphics.Dispose();
        if (_canvas != null) _canvas.Dispose();
        _canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _canvasGraphics = Graphics.FromImage(_canvas);
        _canvasGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _canvasGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        _canvasGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    internal static int LayerAlpha(DanmakuConfig config)
    {
        return config.SolidText ? 255 : (int)Math.Round(255.0 * config.OpacityPercent / 100.0);
    }

    private void Blit(RECT rect)
    {
        int alpha = LayerAlpha(_config);
        if (alpha < 1) alpha = 1;
        if (alpha > 255) alpha = 255;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = _canvas.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memoryDc, hBitmap);
        try
        {
            SIZE size = new SIZE();
            size.cx = _canvas.Width;
            size.cy = _canvas.Height;
            POINT dst = new POINT();
            dst.x = rect.Left;
            dst.y = rect.Top;
            POINT src = new POINT();
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceAlpha = (byte)alpha;
            blend.AlphaFormat = 1;                  // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memoryDc, ref src, 0, ref blend, UlwAlpha);
            // 位置/尺寸交给 UpdateLayeredWindow，这里只保证「可见」，**不动 z 序**（SWP_NOZORDER）——
            // z 序由 EnsureZOrder 统一管，否则每帧一次 TOPMOST 就又变成霸屏了。
            SetWindowPos(Handle, IntPtr.Zero, rect.Left, rect.Top, size.cx, size.cy,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow);
            _lastRect = rect;
        }
        finally
        {
            SelectObject(memoryDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ------------------------------------------------------------------ 跟随目标窗口
    private bool TryGetTargetRect(out RECT rect)
    {
        rect = new RECT();
        if (_targetHwnd == IntPtr.Zero || !IsWindow(_targetHwnd)) return false;

        // 客户区 = 真正的画面区域（窗口模式下不盖标题栏）
        RECT client;
        if (GetClientRect(_targetHwnd, out client))
        {
            POINT origin = new POINT();
            origin.x = 0; origin.y = 0;
            if (ClientToScreen(_targetHwnd, ref origin))
            {
                rect.Left = origin.x;
                rect.Top = origin.y;
                rect.Right = origin.x + (client.Right - client.Left);
                rect.Bottom = origin.y + (client.Bottom - client.Top);
                if (rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
            }
        }
        // 兜底：DWM 无阴影外框；再不行才用 GetWindowRect（它多一圈不可见边框，实测 11px）
        RECT frame;
        if (DwmGetWindowAttribute(_targetHwnd, 9, out frame, Marshal.SizeOf(typeof(RECT))) == 0
            && frame.Right > frame.Left) { rect = frame; return true; }
        if (GetWindowRect(_targetHwnd, out frame)) { rect = frame; return true; }
        return false;
    }

    /// 什么时候把覆盖层藏起来。
    /// 默认模式（贴在播放器正上方）**不需要藏** —— 别的程序盖住播放器时，覆盖层自然一起被盖住。
    /// 只有 AlwaysOnTop（HWND_TOPMOST）时才有必要：那种情况下不藏就会一直霸在最前面。
    private bool IsOverlayWanted()
    {
        if (!_config.AlwaysOnTop) return true;
        if (!_config.HideWhenNotForeground) return true;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return true;
        if (foreground == _targetHwnd) return true;
        // mpv / PotPlayer 的弹窗（右键菜单等）是它自己的子窗口
        IntPtr parent = foreground;
        for (int i = 0; i < 4 && parent != IntPtr.Zero; i++)
        {
            if (parent == _targetHwnd) return true;
            parent = GetParent(parent);
        }
        // 前台是我们自己的托盘/菜单时不隐藏
        if (foreground == Handle) return true;
        return false;
    }

    private static IntPtr FindTargetWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        try
        {
            EnumWindows(delegate (IntPtr hwnd, IntPtr param)
            {
                uint windowPid;
                GetWindowThreadProcessId(hwnd, out windowPid);
                if (windowPid != (uint)pid) return true;
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, 4) != IntPtr.Zero) return true;      // GW_OWNER != 0 → 是工具窗/弹窗
                RECT rect;
                if (!GetWindowRect(hwnd, out rect)) return true;
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }
        return best;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    // ------------------------------------------------------------------ 收件箱
    private void DrainInbox()
    {
        for (int guard = 0; guard < 5000; guard++)
        {
            object message;
            lock (_gate)
            {
                if (_inbox.Count == 0) break;
                message = _inbox.Dequeue();
            }
            DanmakuItem danmaku = message as DanmakuItem;
            if (danmaku != null)
            {
                if (_config.DanmakuDelayMs > 0)
                {
                    PendingDanmaku pending;
                    pending.Item = danmaku;
                    pending.ReleaseAt = DateTime.UtcNow.AddMilliseconds(_config.DanmakuDelayMs);
                    _pendingDanmaku.Add(pending);
                }
                else _renderer.AddDanmaku(danmaku);
                continue;
            }
            LiveEvent liveEvent = message as LiveEvent;
            if (liveEvent != null) { _renderer.AddLiveEvent(liveEvent); continue; }
            SuperChatItem sc = message as SuperChatItem;
            if (sc != null) { _renderer.AddSuperChat(sc); continue; }
            ScDelete delete = message as ScDelete;
            if (delete != null) { _renderer.RemoveSuperChat(delete.Id); continue; }
            StatusNote note = message as StatusNote;
            if (note != null) { _statusText = note.Text; continue; }
            EmoticonsNote emoticons = message as EmoticonsNote;
            if (emoticons != null) { _renderer.SetEmoticons(emoticons.Dict); continue; }
        }
    }

    // ------------------------------------------------------------------ 配置热重载
    private bool _configReloadFailed;
    private void ReloadConfigIfChanged()
    {
        if ((DateTime.UtcNow - _lastConfigCheck).TotalSeconds < 1) return;
        _lastConfigCheck = DateTime.UtcNow;
        try
        {
            if (!File.Exists(_configPath)) return;
            DateTime stamp = File.GetLastWriteTimeUtc(_configPath);
            if (stamp <= _lastConfigWrite) return;
            DanmakuConfig fresh;
            if (!DanmakuConfig.TryLoad(_configPath, out fresh))
            {
                if (!_configReloadFailed) _renderer.ShowTransient("弹幕设置读取失败，保留上次设置；文件恢复后自动重试");
                _configReloadFailed = true;
                return; // 不推进成功时间戳，解锁后即使 mtime 不变也会重试。
            }
            CopyInto(_config, fresh);
            ApplyConfigAndHotkeys();
            _renderer.ShowTransient("弹幕设置已重新加载");
            _lastConfigWrite = stamp;
            _configReloadFailed = false;
        }
        catch (Exception ex) { Diagnostics.Report("配置热重载", ex); }
    }

    private static void CopyInto(DanmakuConfig target, DanmakuConfig source)
    {
        target.CopyFrom(source);
    }

    private void ApplyConfigAndHotkeys()
    {
        if (IsHandleCreated) { UnregisterHotkeys(); RegisterHotkeys(); }
        _renderer.ApplyConfig();
    }

    private void SaveConfig()
    {
        try
        {
            if (!_config.Save(_configPath))
            {
                Diagnostics.Report("覆盖层配置保存", null);
                _renderer.ShowTransient("弹幕设置保存失败，本次更改尚未保存");
                return;
            }
            ApplyConfigAndHotkeys();
            _lastConfigWrite = File.GetLastWriteTimeUtc(_configPath);
        }
        catch (Exception ex) { Diagnostics.Report("覆盖层配置保存", ex); _renderer.ShowTransient("弹幕设置保存失败，本次更改尚未保存"); }
    }

    // ------------------------------------------------------------------ 交互
    private void ToggleDanmaku()
    {
        _config.Enabled = !_config.Enabled;
        _renderer.ShowTransient(_config.Enabled ? "弹幕已开启" : "弹幕已关闭");
        SaveConfig();
    }

    private void ToggleOverlay()
    {
        _userHidden = !_userHidden;
        if (_userHidden) _renderer.ClearSuperChats();
        _renderer.ShowTransient(_userHidden ? "覆盖层已隐藏" : "覆盖层已显示");
    }

    private void CycleSuperChatMode()
    {
        string[] order = new string[] { "full", "compact", "badge", "off" };
        int index = Array.IndexOf(order, _config.ScMode);
        _config.ScMode = order[(index + 1 + order.Length) % order.Length];
        _renderer.ShowTransient("SC：" + ScModeName(_config.ScMode));
        SaveConfig();
    }

    private static string ScModeName(string mode)
    {
        if (mode == "compact") return "折叠成一行";
        if (mode == "badge") return "只留计数角标";
        if (mode == "off") return "关闭";
        return "完整卡片";
    }

    private static string CornerName(string corner)
    {
        if (corner == "br") return "右下";
        if (corner == "tl") return "左上";
        if (corner == "bl") return "左下";
        return "右上";
    }

    private static string FontLabelOf(int percent)
    {
        int index = NearestIndex(FontSteps, percent);
        return FontLabels[index] + "（" + FontSteps[index] + "%）";
    }

    private static string AreaLabelOf(int percent)
    {
        int index = NearestIndex(AreaSteps, percent);
        return AreaLabels[index];
    }

    private static string SpeedLabelOf(int percent)
    {
        int index = NearestIndex(SpeedSteps, percent);
        return SpeedLabels[index];
    }

    private static string DensityLabelOf(int max)
    {
        int index = NearestIndex(DensitySteps, max);
        return DensityLabels[index];
    }

    // ------------------------------------------------------------------ 托盘
    private void SetTray()
    {
        try
        {
            _tray = new NotifyIcon();
            _tray.Icon = BuildTrayIcon();
            _tray.Text = _title;
            _tray.Visible = true;
            _tray.ContextMenuStrip = new ContextMenuStrip();
            _tray.ContextMenuStrip.Opening += delegate { RebuildTrayMenu(); };
            RebuildTrayMenu();
            _tray.DoubleClick += delegate { ToggleOverlay(); };
        }
        catch (Exception) { }
    }

    private static Icon BuildTrayIcon()
    {
        using (Bitmap bitmap = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush back = new SolidBrush(Color.FromArgb(255, 0, 161, 214)))
                    g.FillEllipse(back, 0, 0, 31, 31);
                using (Font font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString("弹", font, brush, new RectangleF(0, 0, 32, 32), format);
                }
            }
            IntPtr handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private void RebuildTrayMenu()
    {
        if (_tray == null) return;
        ContextMenuStrip menu = _tray.ContextMenuStrip;
        menu.Items.Clear();

        ToolStripMenuItem header = new ToolStripMenuItem(_statusText.Length == 0 ? _title : _statusText);
        header.Enabled = false;
        menu.Items.Add(header);
        // 静默失败摘要：日志写不进去、配置存不上这类事，用户在这里才看得到。
        // 平时这一行不出现（Summary() 返回 null）。
        string diag = Diagnostics.Summary();
        if (diag != null)
        {
            ToolStripMenuItem diagItem = new ToolStripMenuItem("⚠ " + diag);
            diagItem.ForeColor = Color.Firebrick;
            menu.Items.Add(diagItem);
        }
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem toggle = new ToolStripMenuItem("显示弹幕", null,
            delegate { ToggleDanmaku(); });
        toggle.Checked = _config.Enabled;
        toggle.CheckOnClick = false;
        menu.Items.Add(toggle);

        ToolStripMenuItem overlay = new ToolStripMenuItem(_userHidden ? "显示覆盖层（Ctrl+Alt+O）" : "隐藏覆盖层（Ctrl+Alt+O）",
            null, delegate { ToggleOverlay(); });
        menu.Items.Add(overlay);

        ToolStripMenuItem sc = new ToolStripMenuItem("SC 醒目留言： " + ScModeName(_config.ScMode) + "（Ctrl+Alt+S 切换）");
        string[] modes = new string[] { "full", "compact", "badge", "off" };
        for (int i = 0; i < modes.Length; i++)
        {
            string mode = modes[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(ScModeName(mode), null, delegate
            {
                _config.ScMode = mode;
                _renderer.ShowTransient("SC：" + ScModeName(mode));
                SaveConfig();
            });
            entry.Checked = _config.ScMode == mode;
            sc.DropDownItems.Add(entry);
        }
        menu.Items.Add(sc);
        ToolStripMenuItem testSc = new ToolStripMenuItem("显示测试 SC（验 emoji 有没有豆腐块）",
            null, delegate { ShowTestSuperChat(); });
        menu.Items.Add(testSc);
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem size = new ToolStripMenuItem("弹幕字号：" + FontLabelOf(_config.FontScalePercent)
            + "（Ctrl+Alt+↑ / ↓）");
        for (int i = 0; i < FontSteps.Length; i++)
        {
            int value = FontSteps[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(FontLabels[i] + "（" + value + "%）", null, delegate
            {
                _config.FontScalePercent = value;
                _renderer.ApplyConfig();
                SaveConfig();
                _renderer.ShowTransient("弹幕字号：" + FontLabelOf(value));
            });
            entry.Checked = _config.FontScalePercent == value;
            size.DropDownItems.Add(entry);
        }
        menu.Items.Add(size);

        ToolStripMenuItem area = new ToolStripMenuItem("弹幕显示区域：" + AreaLabelOf(_config.DisplayAreaPercent)
            + "（Ctrl+Alt+← / →）");
        for (int i = 0; i < AreaSteps.Length; i++)
        {
            int value = AreaSteps[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(AreaLabels[i], null, delegate
            {
                _config.DisplayAreaPercent = value;
                _renderer.ApplyConfig();
                SaveConfig();
                _renderer.ShowTransient("弹幕显示区域：" + AreaLabelOf(value));
            });
            entry.Checked = _config.DisplayAreaPercent == value;
            area.DropDownItems.Add(entry);
        }
        menu.Items.Add(area);

        ToolStripMenuItem opacity = new ToolStripMenuItem("不透明度：" + _config.OpacityPercent + "%");
        for (int i = 0; i < OpacitySteps.Length; i++)
        {
            int value = OpacitySteps[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(value + "%", null, delegate
            {
                _config.OpacityPercent = value;
                SaveConfig();
            });
            entry.Checked = _config.OpacityPercent == value;
            opacity.DropDownItems.Add(entry);
        }
        menu.Items.Add(opacity);

        ToolStripMenuItem speed = new ToolStripMenuItem("弹幕速度：" + SpeedLabelOf(_config.SpeedPercent)
            + "（Ctrl+Alt+PgUp 更快 / PgDn 更慢）");
        for (int i = 0; i < SpeedSteps.Length; i++)
        {
            int value = SpeedSteps[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(SpeedLabels[i], null, delegate
            {
                _config.SpeedPercent = value;
                _renderer.ApplyConfig();
                SaveConfig();
            });
            entry.Checked = _config.SpeedPercent == value;
            speed.DropDownItems.Add(entry);
        }
        menu.Items.Add(speed);

        // 弹幕延迟补偿：各平台缓冲深度不同（B站 3.5s / 抖音 3.9s / 虎牙 2.6s / 斗鱼 2.6s），
        // 所以给几个实测值做快捷项，而不是只让用户盲调。
        ToolStripMenuItem delay = new ToolStripMenuItem("弹幕延迟补偿："
            + (_config.DanmakuDelayMs == 0 ? "关闭" : (_config.DanmakuDelayMs / 1000.0).ToString("0.0") + " 秒"));
        int[] delaySteps = new int[] { 0, 2000, 2500, 3000, 3500, 4000, 5000 };
        foreach (int ms in delaySteps)
        {
            int value = ms;
            string label = value == 0 ? "关闭（保持原行为）" : (value / 1000.0).ToString("0.0") + " 秒";
            if (value == 2600) label += "  ← 虎牙/斗鱼实测缓冲";
            if (value == 3500) label += "  ← B站实测缓冲";
            if (value == 4000) label += "  ← 抖音实测缓冲";
            ToolStripMenuItem entry = new ToolStripMenuItem(label, null, delegate
            {
                _config.DanmakuDelayMs = value;
                SaveConfig();
                _renderer.ShowTransient(value == 0 ? "弹幕延迟补偿：关闭"
                    : "弹幕延迟补偿：" + (value / 1000.0).ToString("0.0") + " 秒");
            });
            entry.Checked = _config.DanmakuDelayMs == value;
            delay.DropDownItems.Add(entry);
        }
        menu.Items.Add(delay);

        ToolStripMenuItem density = new ToolStripMenuItem("弹幕密度：" + DensityLabelOf(_config.MaxOnScreen));
        for (int i = 0; i < DensitySteps.Length; i++)
        {
            int value = DensitySteps[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(DensityLabels[i], null, delegate
            {
                _config.MaxOnScreen = value;
                SaveConfig();
            });
            entry.Checked = _config.MaxOnScreen == value;
            density.DropDownItems.Add(entry);
        }
        menu.Items.Add(density);
        menu.Items.Add(new ToolStripMenuItem("弹幕统计…", null, delegate {
            MessageBox.Show(this, _renderer.StatisticsText, "弹幕统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));

        ToolStripMenuItem fontName = new ToolStripMenuItem("字体：" + _config.FontName);
        string[] fonts = new string[] { "微软雅黑", "黑体", "宋体" };
        for (int i = 0; i < fonts.Length; i++)
        {
            string font = fonts[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(font, null, delegate
            {
                _config.FontName = font;
                _config.Normalize();
                _renderer.ApplyConfig();
                SaveConfig();
            });
            entry.Checked = _config.FontName == font;
            fontName.DropDownItems.Add(entry);
        }
        menu.Items.Add(fontName);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Check("显示粉丝勋章", _config.ShowMedal, delegate (bool on)
        { _config.ShowMedal = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("显示用户名", _config.ShowUserName, delegate (bool on)
        { _config.ShowUserName = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("显示用户等级", _config.ShowUserLevel, delegate (bool on)
        { _config.ShowUserLevel = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("显示舰长标记", _config.ShowGuard, delegate (bool on)
        { _config.ShowGuard = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("显示表情图片", _config.ShowEmoticon, delegate (bool on)
        { _config.ShowEmoticon = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("emoji 用彩色（精灵图）", _config.ShowColorEmoji, delegate (bool on)
        { _config.ShowColorEmoji = on; _renderer.ApplyConfig(); SaveConfig();
          _renderer.ShowTransient(on ? "emoji：彩色" : "emoji：系统字体单色"); }));
        menu.Items.Add(Check("黑色描边", _config.StrokeEnabled, delegate (bool on)
        { _config.StrokeEnabled = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("防挡字幕（底部留空）", _config.AntiBlockSubtitle, delegate (bool on)
        { _config.AntiBlockSubtitle = on; _renderer.ApplyConfig(); SaveConfig(); }));
        menu.Items.Add(Check("点 SC 面板可切换形态", _config.ScClickable, delegate (bool on)
        { _config.ScClickable = on; SaveConfig();
          _renderer.ShowTransient(on ? "SC 可点击：开" : "SC 可点击：关（整层点击穿透）"); }));
        menu.Items.Add(Check("始终压在所有窗口之上", _config.AlwaysOnTop, delegate (bool on)
        { _config.AlwaysOnTop = on; _wasTopmost = false; SaveConfig();
          _renderer.ShowTransient(on ? "覆盖层：始终最前" : "覆盖层：贴在播放器上方"); }));
        menu.Items.Add(Check("始终最前时，非前台自动隐藏", _config.HideWhenNotForeground, delegate (bool on)
        { _config.HideWhenNotForeground = on; SaveConfig(); }));
        menu.Items.Add(Check("字号随画面大小缩放", _config.ScaleWithWindow, delegate (bool on)
        { _config.ScaleWithWindow = on; _renderer.ApplyConfig(); SaveConfig(); _renderer.ShowTransient(
            on ? "字号随画面缩放：开" : "字号随画面缩放：关（固定像素）"); }));

        ToolStripMenuItem scCorner = new ToolStripMenuItem("SC 位置：" + CornerName(_config.ScCorner));
        string[] corners = new string[] { "tr", "br", "tl", "bl" };
        for (int i = 0; i < corners.Length; i++)
        {
            string corner = corners[i];
            ToolStripMenuItem entry = new ToolStripMenuItem(CornerName(corner), null, delegate
            {
                _config.ScCorner = corner;
                _renderer.ShowTransient("SC 位置：" + CornerName(corner));
                SaveConfig();
            });
            entry.Checked = _config.ScCorner == corner;
            scCorner.DropDownItems.Add(entry);
        }
        menu.Items.Add(scCorner);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开设置文件…", null, delegate
        {
            // 走 LiveHub.OpenInEditor：绝对路径记事本 / 没有就资源管理器选中，
            // **不依赖系统关联**，所以不会把用户丢进「打开方式」选择框（2026-09-27 改）。
            if (LiveHub.OpenInEditor(_configPath)) _renderer.ShowTransient("已打开设置文件");
            else _renderer.ShowTransient("打开失败，设置文件在：" + _configPath);
        }));
        menu.Items.Add(new ToolStripMenuItem("重新加载设置", null, delegate
        {
            DanmakuConfig fresh = DanmakuConfig.Load(_configPath);
            CopyInto(_config, fresh);
            _renderer.ApplyConfig();
            _renderer.ShowTransient("弹幕设置已重新加载");
        }));
        menu.Items.Add(new ToolStripMenuItem("退出弹幕", null, delegate { Close(); }));
    }

    private static ToolStripMenuItem Check(string text, bool value, Action<bool> apply)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Checked = value;
        item.CheckOnClick = true;
        item.Click += delegate { apply(item.Checked); };
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_tick != null) { _tick.Stop(); _tick.Dispose(); }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_canvasGraphics != null) _canvasGraphics.Dispose();
            if (_canvas != null) _canvas.Dispose();
            if (_targetProcess != null) _targetProcess.Dispose();
            if (LiveHub.OverlayMessage == new Action<string>(_renderer.ShowTransient)) LiveHub.OverlayMessage = null;
            _renderer.Dispose();
        }
        base.Dispose(disposing);
    }
}

// --------------------------------------------------------------------------------------------
// 子命令入口：--danmaku（带界面）与 --danmaku-probe（无界面自检）
// --------------------------------------------------------------------------------------------
internal static class DanmakuHost
{
    /// 弹幕进程自己的日志：runtime\logs\danmaku-<会话>.log（janitor 不动 logs 目录）
    public static void Log(string line)
    {
        if (!LogQuiet(line)) Diagnostics.ReportLogBroken(null);
    }

    /// 写日志，返回是否成功。**不抛、不登记** —— 供 Diagnostics 自己用（否则会递归）。
    /// 日志绝不能拖垮主流程，所以这里允许失败；但调用方要拿得到「失败了」这件事。
    internal static bool LogQuiet(string line)
    {
        return BoundedLog.TryAppend(Path.Combine(LiveHub.RuntimeDir ?? ".", "logs", LogSessionFile), line);
    }

    private static string LogSessionFile = "danmaku.log";

    public static void EnableDpiAwareness()
    {
        // 实测：不声明 DPI 感知时，GetWindowRect 拿到的是被虚拟化的坐标（200% 缩放下差约 2 倍）。
        // legacy csc 不方便嵌 manifest，直接调 API（Win10 1703+）。
        try { DpiAwareness.Enable(); }
        catch (Exception)
        {
            try { SetProcessDpiAwareness(2); } catch (Exception) { }
        }
    }

    /// 彩色 emoji 精灵图（由 scripts\gen-emoji-sheet.ps1 构建期生成）：
    /// 便携包在 exe 旁边或 data\ 下，开发机在 live-hub 根目录。
    public static string EmojiSheetPath()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string[] candidates = new string[]
        {
            Path.Combine(dir, "emoji-sheet.png"),
            Path.Combine(dir, @"data\emoji-sheet.png"),
            Path.Combine(dir, @"lua\emoji-sheet.png")
        };
        for (int i = 0; i < candidates.Length; i++)
            if (File.Exists(candidates[i])) return candidates[i];
        return candidates[0];
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);

    public static string ConfigPath()
    {
        return DanmakuConfig.DefaultPath();
    }

    public static DanmakuConfig LoadOrCreateConfig()
    {
        string path = ConfigPath();
        if (!File.Exists(path))
        {
            DanmakuConfig fresh = new DanmakuConfig();
            fresh.Save(path);
            return fresh;
        }
        return DanmakuConfig.Load(path);
    }

    /// --danmaku <平台> <房间> <会话号> <播放器pid> [overlay|demo] [存帧路径]
    /// mode=demo 时不连直播，改灌合成数据 —— 用来快速验收观感（颜色 / 顶部底部 / 表情 / SC 各档）。
    public static int Run(string platform, string room, string sessionId, int playerPid, string mode, string shotPath)
    {
        EnableDpiAwareness();
        EmojiSheet.SetPath(EmojiSheetPath());
        DanmakuConfig config = LoadOrCreateConfig();
        string configPath = ConfigPath();
        string title = "弹幕 · " + LiveHub.PlatformName(platform) + " " + room + (mode == "demo" ? "（演示）" : "");

        DanmakuOverlayForm form = new DanmakuOverlayForm(config, configPath, playerPid, title);
        form.ShotPath = shotPath;

        if (mode == "demo")
        {
            form.Start();
            Thread demo = new Thread(delegate () { DemoLoop(form); });
            demo.IsBackground = true;
            demo.Start();
            Application.Run(form);
            return 0;
        }

        if (Regex.IsMatch(sessionId ?? "", "^[a-f0-9]{32}$")) LogSessionFile = "danmaku-" + sessionId + ".log";
        IDanmakuClient client = DanmakuClientFactory.Create(platform, room);
        PlatformHealth.SetRoom(platform, room);
        client.DanmakuReceived += delegate (DanmakuItem item) { PlatformHealth.Record(platform, "danmaku", "ready"); form.PostDanmaku(item); };
        client.SuperChatReceived += delegate (SuperChatItem item) { form.PostSuperChat(item); };
        client.SuperChatDeleted += delegate (long id) { form.PostSuperChatDelete(id); };
        ILiveEventSource eventSource = client as ILiveEventSource;
        if (eventSource != null) eventSource.LiveEventReceived += delegate (LiveEvent item) { form.PostLiveEvent(item); };
        client.StatusChanged += delegate (string text) { form.PostStatus(text); };
        client.RoomInfo += delegate (string text) { form.PostStatus(text); };
        // 平台表情字典（B站 `[大笑]` 这类）。拉好之后要让渲染器换字典并丢掉旧的文本位图缓存，
        // 所以走 PostEmoticons 到 UI 线程 —— 客户端线程不能碰渲染器状态。
        client.EmoticonsLoaded += delegate () { form.PostEmoticons(client.Emoticons); };

        PlatformHealth.Record(platform, "danmaku", "connecting");
        Thread worker = new Thread(delegate ()
        {
            try { client.Run(); PlatformHealth.Record(platform, "danmaku", "stopped"); }
            catch (Exception ex) { PlatformHealth.Record(platform, "danmaku", PlatformHealth.Classify(ex)); Diagnostics.Report("弹幕工作线程", ex); }
        });
        worker.IsBackground = true;
        worker.Name = "danmaku-client";

        form.Start();
        worker.Start();
        try { Application.Run(form); }
        finally { client.Stop(); }
        return 0;
    }

    // ------------------------------------------------------------------ 演示数据
    private static readonly string[] DemoSentences = new string[]
    {
        "哈哈哈哈哈", "这波可以", "主播看看我", "6666666", "批发极品S在哪里批发",
        "帮帮我，史瓦象先生！", "上号上号", "这段太顶了", "签到", "德云色永远的神",
        "这个弹幕有点长啊，测试一下长弹幕的滚动速度和轨道占用情况",
        "笑死我了🤣🤣🤣", "跳舞💃", "爱了爱了❤", "恭喜🎉🎉", "这也太强了吧👍", "🔥🔥🔥",
        "生僻字测试：𠮷㍿①②Ⅲ", "混合测试 abc → ★ ① 中文",
        "新增区段：🀄🎴🃏⏰⌚", "变体选择符：❤️ vs ❤ 和 ☀️ vs ☀", "扑克麻将：🀄🎴🃏🀅", "ZWJ组合：👨‍👩‍👧 👩‍💻 🧑‍🚀 ❤️‍🔥 🏳️‍🌈", "键帽：1️⃣2️⃣3️⃣#️⃣", "肤色：👍🏽👌🏻",
        "？", "啊？", "标记了一处地点", "1234567890"
    };

    private static readonly int[] DemoColors = new int[]
    {
        16777215, 16777215, 16777215, 16711680, 16776960, 65280, 65535, 16711935, 14893055, 16744192
    };

    private static void DemoLoop(DanmakuOverlayForm form)
    {
        Random random = new Random(20260922);
        int tick = 0;
        try
        {
            while (true)
            {
                tick++;
                int count = 1 + random.Next(3);
                for (int i = 0; i < count; i++)
                {
                    DanmakuItem item = new DanmakuItem();
                    item.Text = DemoSentences[random.Next(DemoSentences.Length)];
                    item.Color = DemoColors[random.Next(DemoColors.Length)];
                    item.FontSize = 25;
                    item.Mode = 1;
                    if (tick % 23 == 0) item.Mode = 5;                 // 顶部弹幕
                    if (tick % 31 == 0) item.Mode = 4;                 // 底部弹幕
                    if (tick % 17 == 0) item.Mode = 6;                 // 逆向弹幕
                    item.UserName = "观众" + random.Next(1000, 9999);
                    item.UserLevel = random.Next(0, 40);
                    item.MedalLevel = random.Next(0, 35);
                    item.MedalName = "德云色";
                    item.TimestampMs = BiliDanmakuClient.ToUnixSeconds(DateTime.UtcNow) * 1000L;
                    form.PostDanmaku(item);
                }

                // 第 2 秒：三档 SC 各来一条（颜色全部由服务端下发的那套字段驱动）
                // 配色沿用 B站 的服务端规则：background_color 是卡片上半部、background_bottom_color 是下半部，
                // message_font_color 是正文色（要和下半部有对比度）。
                if (tick == 6)
                {
                    form.PostSuperChat(DemoSuperChat(1, 30, 60, "明明就是颜值哥", "今天有没有水滴石穿环节？", "#EDF5FF", "#2A60B2", "#7497CD", "#A3F6FF", "#666666"));
                    form.PostSuperChat(DemoSuperChat(2, 100, 120, "卜一杉嗯", "帮帮我，史瓦象先生！大象：我没招啊", "#FFF6E0", "#C8871A", "#E8B44A", "#FFFDF6", "#8A5A00"));
                    form.PostSuperChat(DemoSuperChat(3, 2000, 300, "土豪本豪", "这个月的生活费就靠这一条了，主播加油！这是一条很长的醒目留言，用来测试自动换行与卡片高度。", "#FFE9E6", "#A81E1E", "#D4574F", "#FFEDEA", "#8C1A1A"));
                }
                // 第 9 秒：一条表情弹幕（用 B站 官方表情图，验证内联图片）
                if (tick == 27)
                {
                    DanmakuItem emote = new DanmakuItem();
                    emote.Text = "你知道吗";
                    emote.IsEmoticon = true;
                    emote.EmoticonUrl = "http://i0.hdslb.com/bfs/live/bf42aa51842a7a0005545de05d0b59081b8ce4ff.png";
                    emote.FontSize = 25;
                    emote.Mode = 1;
                    emote.Color = 16777215;
                    emote.LargeEmoticon = true;
                    form.PostDanmaku(emote);
                }
                // 第 12 秒：带勋章 / 舰队 / 用户名的弹幕（验证前缀拼接）
                if (tick == 36)
                {
                    DanmakuItem fancy = new DanmakuItem();
                    fancy.Text = "带勋章和舰长标记的弹幕";
                    fancy.Color = 16776960;
                    fancy.FontSize = 25;
                    fancy.Mode = 1;
                    fancy.UserName = "舰长大人";
                    fancy.UserLevel = 42;
                    fancy.MedalLevel = 30;
                    fancy.MedalName = "德云色";
                    fancy.GuardLevel = 3;
                    form.PostDanmaku(fancy);
                }
                Thread.Sleep(320);
            }
        }
        catch (Exception) { }
    }

    private static SuperChatItem DemoSuperChat(long id, int price, int seconds, string name, string message,
        string background, string bottom, string priceColor, string font, string nameColor)
    {
        SuperChatItem item = new SuperChatItem();
        item.Id = id;
        item.Price = price;
        item.DurationSec = seconds;
        item.Message = message;
        item.UserName = name;
        item.UserLevel = 17;
        item.MedalLevel = 30;
        item.MedalName = "德云色";
        item.NameColor = BiliDanmakuClient.HexColor(nameColor, item.NameColor);
        item.BackgroundColor = BiliDanmakuClient.HexColor(background, item.BackgroundColor);
        item.BackgroundBottomColor = BiliDanmakuClient.HexColor(bottom, item.BackgroundBottomColor);
        item.BackgroundPriceColor = BiliDanmakuClient.HexColor(priceColor, item.BackgroundPriceColor);
        item.MessageFontColor = BiliDanmakuClient.HexColor(font, item.MessageFontColor);
        item.ExpiresAt = DateTime.UtcNow.AddSeconds(seconds);
        // 演示用头像：B站 站内一张公开头像，验证圆角裁切与下载占位
        item.Face = "https://i2.hdslb.com/bfs/face/f68991aafc03f94d32eb550c6f2f74053a26bd8a.jpg";
        return item;
    }

    /// --danmaku-probe 平台:房间 输出文件 [秒数]
    /// 无界面：把「能不能连、能不能解压、能不能解析」证死，并落一份报告。
    public static int Probe(string spec, string outFile, int seconds)
    {
        List<string> report = new List<string>();
        int exitCode = 0;
        string platform = "bili";
        string room = spec;
        int colon = spec.IndexOf(':');
        if (colon > 0) { platform = spec.Substring(0, colon); room = spec.Substring(colon + 1); }

        report.Add("PLATFORM=" + platform);
        report.Add("ROOM=" + room);
        if (!DanmakuClientFactory.IsSupported(platform))
        {
            report.Add("UNSUPPORTED=这个平台还没接入弹幕（" + platform + "），当前支持："
                + string.Join("/", DanmakuClientFactory.SupportedPlatforms()));
            File.WriteAllLines(outFile, report.ToArray(), new UTF8Encoding(false));
            return 2;
        }

        IDanmakuClient client = DanmakuClientFactory.Create(platform, room);
        int danmakuCount = 0, scCount = 0, total = 0;
        int eventCount = 0;
        var kinds = new Dictionary<string, int>();
        string sampleDanmaku = null, sampleSuperChat = null;
        // 含「方括号代码」的弹幕（B站 `[大笑]` 这类）。用来判断**这个平台到底有没有**
        // 表情代码机制 —— 有才需要做字典，没有就别白费劲。
        // 只看形态，不判断是否真能查到图：`[1楼]` 这种普通文本也会被算进来，
        // 所以它是**上界**，不是精确数。
        int bracketCount = 0;
        string sampleBracket = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 20);
        object gate = new object();

        client.StatusChanged += delegate (string text)
        { lock (gate) { report.Add("STATUS=" + text); } };
        client.RoomInfo += delegate (string text)
        { lock (gate) { report.Add("ROOMINFO=" + text); } };
        client.DanmakuReceived += delegate (DanmakuItem item)
        {
            lock (gate)
            {
                danmakuCount++; total++;
                if (sampleDanmaku == null) sampleDanmaku = DescribeDanmaku(item);
                if (item.Text != null && HasBracketCode(item.Text))
                {
                    bracketCount++;
                    if (sampleBracket == null) sampleBracket = item.Text;
                }
            }
        };
        client.SuperChatReceived += delegate (SuperChatItem item)
        {
            lock (gate)
            {
                scCount++; total++;
                if (sampleSuperChat == null) sampleSuperChat = DescribeSuperChat(item);
            }
        };

        ILiveEventSource eventSource = client as ILiveEventSource;
        if (eventSource != null) eventSource.LiveEventReceived += delegate (LiveEvent item) {
            lock (gate) { eventCount++; total++; string name = item.Kind.ToString(); int count; kinds.TryGetValue(name, out count); kinds[name] = count + 1; }
        };
        Thread worker = new Thread(delegate () { client.Run(); });
        worker.IsBackground = true;
        worker.Start();
        while (DateTime.UtcNow < deadline) Thread.Sleep(200);
        client.Stop();
        Thread.Sleep(600);

        lock (gate)
        {
            report.Add("DANMU=" + danmakuCount);
            report.Add("SUPERCHAT=" + scCount);
            report.Add("EVENTS=" + eventCount);
            foreach (KeyValuePair<string, int> kind in kinds) report.Add("EVENT-" + kind.Key.ToUpperInvariant() + "=" + kind.Value);
            report.Add("TOTAL=" + total);
            report.Add("SAMPLE-DANMU=" + (sampleDanmaku == null ? "(无)" : sampleDanmaku));
            report.Add("BRACKET=" + bracketCount + "/" + danmakuCount
                + (sampleBracket == null ? "" : "  例：" + sampleBracket));
            report.Add("EMOTICONS=" + client.Emoticons.Count);
            report.Add("SAMPLE-SC=" + (sampleSuperChat == null ? "(无)" : sampleSuperChat));
            report.Add("AUTH-OK=" + (danmakuCount > 0 || scCount > 0 || total > 0));
        }
        if (danmakuCount == 0) exitCode = 1;
        report.Add("RESULT=" + (exitCode == 0 ? "OK" : "NO-DANMAKU"));
        try { File.WriteAllLines(outFile, report.ToArray(), new UTF8Encoding(false)); }
        catch (Exception) { return 3; }
        return exitCode;
    }

    /// 文本里有没有 `[xxx]` 形态的片段（不看能否查到图 —— 那是字典的事）。
    /// 长度上限 24 与 EmoticonDictionary.MaxCodeLength 一致：`[这是一句很长的话]` 不算代码。
    private static bool HasBracketCode(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '[') continue;
            int close = text.IndexOf(']', i + 1);
            if (close < 0) return false;
            if (close - i <= 24) return true;
        }
        return false;
    }

    private static string DescribeDanmaku(DanmakuItem item)
    {
        return "\"" + item.Text + "\" mode=" + item.Mode + " color=#" + item.Color.ToString("X6")
            + " size=" + item.FontSize + " user=" + item.UserName + " lv=" + item.UserLevel
            + " medal=" + item.MedalName + item.MedalLevel
            + (item.IsEmoticon ? " emote=" + item.EmoticonUrl : "");
    }

    private static string DescribeSuperChat(SuperChatItem item)
    {
        return "¥" + item.Price + " " + item.DurationSec + "s user=" + item.UserName
            + " face=" + (item.Face == null ? "" : "有")
            + " bg=" + ColorTranslator.ToHtml(item.BackgroundColor)
            + " bottom=" + ColorTranslator.ToHtml(item.BackgroundBottomColor)
            + " \"" + item.Message + "\"";
    }
}
