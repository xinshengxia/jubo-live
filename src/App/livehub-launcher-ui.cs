using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 从主 partial 原样搬运；仍在同一 net48 编译单元。
internal static partial class LiveHub
{
    // ---------------- 界面 ----------------

    private static string FavoriteLabel(KeyValuePair<string, string> favorite)
    {
        Target target = Parse(favorite.Value, "bili");
        return favorite.Key + "   —   " + target.Room;
    }

    private static string HistoryLabel(HistoryEntry entry)
    {
        string name = entry.Name == null ? "" : entry.Name;
        return entry.Room + "   " + name + "   " + (entry.At == null ? "" : entry.At);
    }

    private static string NameForHistory(Target target, List<KeyValuePair<string, string>> favorites)
    {
        if (target.Platform == "bili")
        {
            try
            {
                string path = Path.Combine(RuntimeDir, "bili-" + target.Room + ".anchor.txt");
                if (File.Exists(path))
                {
                    string name = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (name.Length > 0) return name;
                }
            }
            catch (Exception) { }
        }
        foreach (var favorite in favorites)
        {
            Target t = Parse(favorite.Value, "bili");
            if (t != null && t.Platform == target.Platform && t.Room == target.Room) return favorite.Key;
        }
        return "";
    }

    /// <summary>启动器主窗口：建窗 → 进消息循环。</summary>
    private static void ShowDialog()
    {
        Application.Run(BuildLauncherForm());
    }

    /// <summary>建好启动器窗体（全部控件 + 事件接线 + 首次布局），**不进消息循环**。</summary>
    /// <remarks>
    /// 2026-09-27 从 ShowDialog 里抽出来（评估报告 §7「ShowDialog 824 行」整改的第一步）：
    /// 有了「只建窗、不跑消息循环」这条入口，`--ui-probe` 才能把**真实控件树**导出来做结构回归。
    /// 拆这种靠闭包耦合几百行 UI 的代码，没有「拆分前后控件树完全一致」这类等价性证据就是盲改。
    /// </remarks>
    /// <summary>建好启动器窗体（控件 + 事件接线 + 首次布局），**不进消息循环**。</summary>
    /// <remarks>
    /// 2026-09-27：控件与状态搬进 <see cref="LauncherUi"/>，这里只剩三行 ——
    /// 原来这一个是 824 行的单方法（评估报告 §7）。
    /// </remarks>
    private static Form BuildLauncherForm()
    {
        return BuildLauncherForm(0);
    }

    /// <summary>建窗。<paramref name="themeOverride"/>：0=按配置，1=强制深色，-1=强制浅色（测试用）。</summary>
    private static Form BuildLauncherForm(int themeOverride)
    {
        // ⚠️ 主题必须在 **new LauncherUi() 之前**定下来：LauncherUi 的控件是**字段初始化器**建的，
        //    而它们的构造函数会读一遍调色板（只有自绘控件在 OnPaint 里现读）。
        //    2026-09-27 修：原来 Apply 写在 BuildFormShell() 里，那时控件早就建好了 ——
        //    深色模式下 ModernInput 内层那个标准 TextBox 因此一直是白底黑字。
        LauncherUi.ApplyTheme(themeOverride);
        LauncherUi ui = new LauncherUi();
        ui.Build();
        return ui.form;
    }

    /// <summary>启动器主窗口的**控件与状态**（2026-09-27 从 ShowDialog 拆出来，评估报告 §7）。</summary>
    ///
    /// 原来这些是 ShowDialog 里的局部变量 + 一串闭包委托（顺序耦合、无法单测、改一行得读 800 行）。
    /// 现在：状态与控件是字段，几段逻辑是方法，`Build()` 只负责拼装。
    ///
    /// ⚠️ 字段名**故意沿用原来的局部变量名**（form/input/status/…）：这次是纯机械搬运，
    /// 方法体一个字没改 —— 半路改名只会让 diff 变噪音。要改名另开一次提交。
    ///
    /// 等价性证据：`--ui-probe` 导出的控件树在拆分前后**逐字段一致**（tests\LauncherUiTests.cs），
    /// 外加 GUI 冒烟与离线断言。
    private sealed class LauncherUi
    {
        // ==== 状态与控件（原来是 BuildLauncherForm 里的局部变量）====
        internal List<KeyValuePair<string, string>> favorites = LoadFavorites();
        internal Dictionary<string, List<HistoryEntry>> history = LoadHistory();
        internal string platform = "bili";
        internal Form form = new DpiForm();
        internal bool darkMode = false;
        internal Dictionary<string, ModernButton> platformButtons = new Dictionary<string, ModernButton>();
        internal string[] platforms = { "bili", "huya", "douyu", "douyin" };
        internal int buttonX = 18;
        internal Label inputLabel = new ModernLabel();
        internal ModernInput input = new ModernInput(false);
        internal Button inputHelp = new ModernButton();
        internal Label status = new ModernLabel();
        internal Label qualityLabel = new ModernLabel();
        internal ComboBox qualityBox = new ModernComboBox();
        internal Label bufferLabel = new ModernLabel();
        internal ComboBox bufferBox = new ModernComboBox();
        internal Button cancelLaunch = new ModernButton();
        internal Label danmakuLabel = new ModernLabel();
        internal ComboBox danmakuBox = new ModernComboBox();
        internal Label scLabel = new ModernLabel();
        internal ComboBox scBox = new ModernComboBox();
        internal string[] scIds = new string[] { "full", "compact", "badge", "off" };
        internal Label enhanceLabel = new ModernLabel();
        internal ComboBox enhanceBox = new ModernComboBox();
        internal string[] pqIds = new string[] { "off", "light", "high" };
        internal ToolTip enhanceTip = new ToolTip();
        internal Button danmakuSettings = new ModernButton();
        internal Label recentLabel = new ModernLabel();
        internal Label favLabel = new ModernLabel();
        internal ModernListBox recentList = new ModernListBox();
        internal ModernListBox favList = new ModernListBox();
        internal Button addFav = new ModernButton();
        internal Button delFav = new ModernButton();
        internal Button lookup = new ModernButton();
        internal Button localData = new ModernButton();
        internal Dictionary<string, Label> loginLabels = new Dictionary<string, Label>();
        internal Button biliLogin = new ModernButton();
        internal Button douyuLogin = new ModernButton();
        internal Button huyaLogin = new ModernButton();
        internal Button douyinLogin = new ModernButton();
        internal Button openPot = new ModernButton();
        internal Button openMpv = new ModernButton();
        internal Label sessionsLabel = new ModernLabel();
        internal Button reopen = new ModernButton();
        // 播放器路径（2026-10-16 搬到主界面）：绿色包不自带播放器，用户在这里指定自己的 mpv / PotPlayer
        internal Label playerLabel = new ModernLabel();
        internal ComboBox playerBox = new ModernComboBox();
        internal Button pickPlayer = new ModernButton();
        internal Label playerHint = new ModernLabel();
        internal ModernListView sessionsList = new ModernListView();
        internal ToolTip listTips = new ToolTip();
        internal List<KeyValuePair<string, string>> currentFavorites = new List<KeyValuePair<string, string>>();
        internal List<HistoryEntry> currentHistory = new List<HistoryEntry>();
        internal Dictionary<string, RoomStateKind> roomBadges = new Dictionary<string, RoomStateKind>();
        internal Dictionary<string, DateTime> badgeTimes = new Dictionary<string, DateTime>();
        internal HashSet<string> badgePending = new HashSet<string>();
        internal List<PlaybackSession> sessions = new List<PlaybackSession>();
        internal string lastSessionSignature = null;
        internal System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer();
        internal System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer();
        internal int previewToken = 0;
        internal CancellationTokenSource currentLaunch = null;

        /// <summary>建窗 + 装控件 + 接线 + 首次刷新 + DPI 缩放 + 布局。</summary>
        /// <summary>建窗 + 装控件 + 接线 + 首次刷新 + DPI 缩放 + 布局。</summary>
        /// <remarks>
        /// 2026-09-27：原来这里是 824 行（评估报告 §7）。现在按「窗体 / 三行控件 / 两个列表 /
        /// 底排按钮 / 会话表 / 计时器 / 其余接线」拆成 8 段，每段只碰自己那几个控件 ——
        /// 状态在字段上，段与段之间不传参。拆分前后 `--ui-probe` 的控件树逐字符一致。
        /// </remarks>
        /// <summary>按配置（或测试指定的覆盖值）把主题定下来。**必须在构造 LauncherUi 之前调用**。</summary>
        internal static void ApplyTheme(int themeOverride)
        {
            bool dark;
            if (themeOverride > 0) dark = true;
            else if (themeOverride < 0) dark = false;
            else
            {
                dark = false;
                try { dark = DanmakuHost.LoadOrCreateConfig().DarkMode; }
                catch (Exception) { /* 配置读不出来就用浅色，别让外观问题挡住启动 */ }
            }
            ModernTheme.Apply(dark);
        }

        internal void Build()
        {
            BuildFormShell();
            BuildTopRow();
            BuildSettingsRow();
            BuildLists();
            BuildBottomButtons();
            BuildSessionTable();
            BuildTimers();
            WireEvents();
        RefreshAll();
        // ⚠️ DPI 缩放的**唯一入口** —— 必须在所有控件建完之后。
        //    ScaleTree 只缩放 **静态 SetBounds** 那一批；动态的 Layout() 由它自己按
        //    物理坐标算（见上面那段注释），所以这里**不要**再调 Layout() ——
        //    ClientSize 早就是缩放过的值，Layout() 会在合适的时候自己被触发。
        //    只缩 Bounds、**不动 Font**（字号是点值，本来就按设备 DPI 渲染）。
        DpiLayout.ScaleTree(form);
        // 再跑一次动态布局 —— ScaleTree 会把 Layout() 管的那批控件也缩放一遍，
        // 而 Layout() 是**绝对赋值**（不是乘算），所以重跑一次正好把那些覆盖成正确的
        // 物理坐标。放在 ScaleTree 之前跑的那次是给 RefreshAll() 用的中间状态。
        Layout();
        }

        /// <summary>窗体本身：标题（带 commit 短号）、DPI 尺寸、最小尺寸、主题。</summary>
        private void BuildFormShell()
        {

        // 下面的事件接线会调用 Layout / RefreshAll / Launch / Badge / UpdateBadge —— 都是 LauncherUi 的方法，

        // 标题带 build 短号：用户报问题时一眼能看到是哪份构建，不用去右键属性里翻。
        form.Text = "看直播  " + BuildInfo.Short;
        // ⚠️ 这两个尺寸要乘 DPI：下面所有 SetBounds 都是 96 DPI 的逻辑值，
        //    而客户端区必须先按 DPI 放大，否则控件会超出窗口。
        //    缩放在最后统一做（见 Application.Run 之前的 DpiLayout.ScaleTree）。
        // ⚠️ 2026-10-16：高度 560 装不下内容，用户看到的是「默认打开的框不够大、显示不全」。
        //    实测（96 DPI 基准下建窗，见提交说明）：可压缩的两张表（最近播放/收藏、正在观看）
        //    早已压到各自最小值，**内容刚性需要 605**（底排按钮下缘 595 + 底部留白 10），
        //    而 560 的客户区把底排那 8 个按钮（加入收藏…用 mpv 打开）整排推到窗外。
        //    ⚠️ 还不够高会**连锁恶化**：AutoScroll 出竖滚动条 → 客户区窄 34px → 底排换行 →
        //    内容反而涨到 651 → 更放不下。所以这里必须一次给够：605 → 620（15 单位余量）。
        //    回归断言见 tests\LauncherUiTests.cs 的「默认尺寸下内容纵向完整可见」。
        form.ClientSize = new Size(DpiLayout.S(1120), DpiLayout.S(620));
        // 底部那一排按钮的最小可用宽度是 1098，推导：
        //   左侧最后一颗（抖音扫码）右边缘 = 586 + 100 = 686
        //   右侧第一颗（用 PotPlayer 打开）左边缘 = 宽度 - 400
        //   要不相交：宽度 - 400 >= 686 + 12  →  宽度 >= 1098
        // 850 远远不够，实测能缩到 850×600，此时「斗鱼扫码」压住「PotPlayer」、
        // 「抖音扫码」同时压住两个播放按钮。1136 是含边框的保守值。
        //
        // ⚠️ 2026-09-25：这条改动此前**从未真正进过仓库** —— 它写在已废弃生成器的
        //    补丁块里，而那个块改的是 $text、脚本最后写盘的却是更早快照的 $written，
        //    于是「补丁报告成功、产物没变」。生成器已废弃，现在直接改源码。
        form.MinimumSize = new Size(600, 440);
        form.AutoScroll = true;
        ((DpiForm)form).DpiUpdated += delegate { Layout(); };
        // 窗口图标用 exe 自带的那枚（构建期 /win32icon:app.ico 嵌进去的）。不设的话
        // WinForms 会用默认的红黄蓝图标，任务栏/标题栏与 exe 文件图标对不上。
        // 取不到就安静退回默认 —— 不影响启动。
        try { form.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        form.StartPosition = FormStartPosition.CenterScreen;
        form.FormBorderStyle = FormBorderStyle.Sizable;
        // 现代化外观：和「弹幕与 SC 设置」同一套自绘控件 + 同一份调色板（danmaku-15-ui.cs）。
        // 主题已经由 ApplyTheme() 在**构造本类之前**定好了（见 BuildLauncherForm 的注释），
        // 这里只记录状态、并按当前配色刷窗体自身 + 标题栏。
        darkMode = ModernTheme.IsDark;
        form.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(form); };
        // 窗体自身的底色也要跟着主题走：用户在「弹幕设置…」里勾上深色时，启动器面板
        // 是**另一个窗口**，它只订阅了 Changed 才会当场变（否则要重启才生效）。
        // 自绘控件各自订阅了，这里补的是窗体自己 + 记录状态。
        Action followTheme = delegate
        {
            darkMode = ModernTheme.IsDark;
            form.BackColor = ModernTheme.Surface;
            form.ForeColor = ModernTheme.Text;
            ModernTheme.ApplyWindowChrome(form);
        };
        ModernTheme.Changed += followTheme;
        form.Disposed += delegate { ModernTheme.Changed -= followTheme; };
        DpiLayout.SetDesignFont(form, ModernTheme.UiFont);
        form.BackColor = ModernTheme.Surface;
        form.ForeColor = ModernTheme.Text;
        // ⚠️ 这里**故意不设 AutoScaleMode** —— 见 DpiAwareness.Enable() 里的说明：
        //    本窗体是「硬编码像素 + 运行时按 ClientSize 动态算」的混合布局，
        //    AutoScaleMode 缩放和它天生不兼容（实测底部按钮跑到 y=2218、列表全部看不见）。
        //    DpiForm is the only scaling owner and rebuilds target-pixel fonts on each monitor.

        }

        /// <summary>第一行：平台按钮组 + 房间输入框 + 状态标签。</summary>
        private void BuildTopRow()
        {
        foreach (string p in platforms)
        {
            ModernButton button = new ModernButton();
            button.Text = PlatformName(p);
            button.SetBounds(buttonX, 16, 92, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Tag = p;
            button.Click += delegate(object sender, EventArgs e)
            {
                platform = (string)((Button)sender).Tag;
                RefreshAll();
            };
            form.Controls.Add(button);
            platformButtons[p] = button;
            buttonX += 100;
        }

        inputLabel.Text = "房间号 / 链接：";
        inputLabel.SetBounds(buttonX + 4, 24, 104, 20);
        form.Controls.Add(inputLabel);

        DpiLayout.SetDesignFont(input, new Font("Microsoft YaHei", 11F));
        input.SetBounds(buttonX + 108, 20, form.ClientSize.Width - buttonX - 126, 28);
        form.Controls.Add(input);

        inputHelp.Text = "格式说明";
        inputHelp.Name = "roomInputHelp";
        inputHelp.AccessibleDescription = "查看各平台支持的房间号、链接格式和示例";
        inputHelp.SetBounds(1014, 16, 88, 34);
        inputHelp.Click += delegate { ShowRoomInputHelp(form); };
        form.Controls.Add(inputHelp);

        status.Name = "launcherStatus";
        status.AutoEllipsis = true;
        status.ForeColor = ModernTheme.Accent;
        // 第三行从左往右排：画质增强标签 + 下拉（18..244），状态提示跟在右边（258..1102）。
        // ⚠️ 这一行 SetBounds 曾经整个丢失过 —— 上一轮改布局时替换只落下了注释，
        //    于是 status 用默认位置 (0,0)，提示文字跑到客户区最顶上（用户截图能看到）。
        //    GUI 冒烟的防重叠断言没抓到，因为当时把 Label 排除在检查之外了（现已纳入）。
        lookup.Text = "查找主播";
        lookup.Name = "streamerLookupEntry";
        lookup.SetBounds(258, 92, 110, 32);
        form.Controls.Add(lookup);
        localData.Text = "本地数据 / 平台状态";
        localData.Name = "localDataEntry";
        localData.SetBounds(376, 92, 164, 32);
        localData.Click += delegate { LocalData.Show(form); };
        form.Controls.Add(localData);
        foreach (string p in platforms)
        {
            string selectedPlatform = p;
            Label label = new ModernLabel { Name = "loginSummary_" + p, AutoEllipsis = true,
                BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft };
            label.SetBounds(18, 130, 240, 48);
            label.Click += delegate { LocalData.Show(form, selectedPlatform); };
            form.Controls.Add(label); loginLabels[p] = label;
        }
        RefreshLoginSummaries();
        Action loginTheme = delegate { RefreshLoginSummaries(); };
        ModernTheme.Changed += loginTheme;
        form.Disposed += delegate { ModernTheme.Changed -= loginTheme; };
        status.SetBounds(382, 96, 720, 24);
        form.Controls.Add(status);

        }

        /// <summary>第二行：画质 / 缓冲 / 取消启动 / 弹幕 / SC / 画质增强 / 弹幕设置（含各自的事件）。</summary>
        private void BuildSettingsRow()
        {
        qualityLabel.Text = "画质：";
        qualityLabel.SetBounds(18, 64, 50, 24);
        form.Controls.Add(qualityLabel);
        qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        qualityBox.Items.AddRange(new object[] { "原画优先", "高清优先", "流畅优先" });
        qualityBox.SelectedIndex = 0;
        qualityBox.SetBounds(70, 60, 126, 28);
        form.Controls.Add(qualityBox);
        bufferLabel.Text = "mpv 缓冲：";
        bufferLabel.SetBounds(224, 64, 86, 24);
        form.Controls.Add(bufferLabel);
        bufferBox.DropDownStyle = ComboBoxStyle.DropDownList;
        bufferBox.Items.AddRange(new object[] { "播放更稳定", "延迟更低" });
        bufferBox.SelectedIndex = 1;
        enhanceTip.SetToolTip(bufferLabel, "仅对 mpv 生效；PotPlayer 使用播放器自身的缓冲设置。");
        enhanceTip.SetToolTip(bufferBox, "仅对 mpv 生效。默认延迟更低；网络不稳时可切换播放更稳定。");
        bufferBox.SetBounds(310, 60, 134, 28);
        form.Controls.Add(bufferBox);
        cancelLaunch.Text = "取消启动";
        cancelLaunch.Enabled = false;
        cancelLaunch.SetBounds(452, 58, 110, 31);
        form.Controls.Add(cancelLaunch);

        // 弹幕：与「画质 / 缓冲」一样是**本次播放**的选择；默认跟随「弹幕设置」里的总开关
        danmakuLabel.Text = "弹幕：";
        danmakuLabel.SetBounds(570, 64, 44, 24);
        form.Controls.Add(danmakuLabel);
        danmakuBox.DropDownStyle = ComboBoxStyle.DropDownList;
        danmakuBox.Items.AddRange(new object[] { "跟随设置", "本次开启", "本次关闭" });
        danmakuBox.SelectedIndex = 0;
        danmakuBox.SetBounds(614, 60, 106, 28);
        form.Controls.Add(danmakuBox);
        // SC：直接写配置。在跑的覆盖层会热重载，所以改完立刻生效 ——
        // 这就是「SC 关着时也能从界面上把它调出来」的入口。
        scLabel.Text = "SC：";
        scLabel.SetBounds(848, 64, 30, 24);
        form.Controls.Add(scLabel);
        scBox.DropDownStyle = ComboBoxStyle.DropDownList;
        scBox.Items.AddRange(new object[] { "完整卡片", "折叠一行", "只留角标", "关闭" });
        try { scBox.SelectedIndex = Math.Max(0, Array.IndexOf(scIds, DanmakuHost.LoadOrCreateConfig().ScMode)); }
        catch (Exception) { scBox.SelectedIndex = 0; }
        scBox.SetBounds(880, 60, 112, 28);
        form.Controls.Add(scBox);

        // mpv 画质增强开关。位置是量出来的：第二行最右的 SC 下拉到 992 结束，
        // 客户区宽 1120，剩下 128px 正好放一个紧凑复选框 —— 不用再加宽窗体
        // （再加宽就要超过 1280 屏了）。
        //
        // 只对 mpv 有效：PotPlayer 走的是它自己的渲染器，这个开关管不着。
        // 叫「增强」不叫「画质」：左边已经有个「画质：」是流画质，同名会混淆。
        // 位置必须从 scBox 右边缘（970）之后开始 —— 原来写 940 压了 SC 下拉 52px。
        enhanceLabel.Text = "画质增强";
        // 宽度要为汉字标签和高 DPI 布局保留余量，
        // 给 40 会把「强：」截掉（截图才发现）。给 52 留余量。
        enhanceLabel.SetBounds(18, 99, 72, 24);
        form.Controls.Add(enhanceLabel);
        enhanceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        enhanceBox.Items.AddRange(new object[] { "无", "轻度", "高质量" });
        try { enhanceBox.SelectedIndex = Math.Max(0, Array.IndexOf(pqIds, DanmakuHost.LoadOrCreateConfig().MpvQuality)); }
        catch (Exception) { enhanceBox.SelectedIndex = 0; }
        enhanceBox.SetBounds(94, 95, 150, 28);
        enhanceTip.SetToolTip(enhanceBox,
            "mpv 画质档位（**只影响 mpv**，PotPlayer 走它自己的渲染器）。\r\n"
            + "实测三档 GPU 代价（B站 7777，各 20 秒）：\r\n"
            + "  默认   什么都不加                 均 22.3%  峰 25.7%\r\n"
            + "  轻度   --scale-antiring=0.7      均 21.9%  峰 38%   ← 平均几乎不涨，去边缘振铃\r\n"
            + "  高质量 --profile=high-quality     均 46.7%  峰 63.3% ← 缩放器换 ewa_lanczossharp\r\n"
            + "注：这版 mpv 默认渲染器本来就是 gpu-next(libplacebo)，\r\n"
            + "    即 madVR 的等价物；高质量档只是把缩放算法再提一档。");
        enhanceBox.SelectedIndexChanged += delegate
        {
            try
            {
                DanmakuConfig cfg = DanmakuHost.LoadOrCreateConfig();
                cfg.MpvQuality = pqIds[enhanceBox.SelectedIndex];
                cfg.MpvEnhance = false;      // 清掉旧布尔字段，免得迁移逻辑把它又抬回 high
                if (!cfg.Save(DanmakuHost.ConfigPath()))
                    MessageBox.Show(form, "画质档位没能保存（详见 runtime\\logs\\danmaku.log）。",
                        "看直播", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Diagnostics.Report("保存画质档位", ex);
                MessageBox.Show(form, "画质档位没能保存：" + ex.Message,
                    "看直播", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        form.Controls.Add(enhanceBox);
        scBox.SelectedIndexChanged += delegate
        {
            try
            {
                DanmakuConfig live = DanmakuHost.LoadOrCreateConfig();
                live.ScMode = scIds[scBox.SelectedIndex];
                // 写失败必须当场说 —— 否则下拉框显示「完整卡片」、实际没保存，
                // 用户会以为改了没生效（评估报告 §5.9 点名的就是这类伪装成功）。
                if (!live.Save(DanmakuHost.ConfigPath()))
                    MessageBox.Show(form, "SC 设置没能保存（详见 runtime\\logs\\danmaku.log）。",
                        "看直播", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception) { }
        };

        danmakuSettings.Text = "弹幕设置…";
        danmakuSettings.SetBounds(728, 58, 104, 31);
        form.Controls.Add(danmakuSettings);
        danmakuSettings.Click += delegate { DanmakuSettingsDialog.Show(form); };

        }

        /// <summary>最近播放与收藏两个列表（含标题标签）。</summary>
        private void BuildLists()
        {
        recentLabel.SetBounds(18, 126, 430, 20);
        recentLabel.ForeColor = ModernTheme.TextSecondary;
        form.Controls.Add(recentLabel);

        favLabel.SetBounds(462, 126, 420, 20);
        favLabel.ForeColor = ModernTheme.TextSecondary;
        form.Controls.Add(favLabel);

        recentList.SetBounds(18, 150, 420, 222);
        recentList.IntegralHeight = false;
        form.Controls.Add(recentList);

        favList.SetBounds(462, 150, 420, 222);
        favList.IntegralHeight = false;
        form.Controls.Add(favList);

        }

        /// <summary>底排按钮：收藏增删、四家扫码、两个播放按钮。</summary>
        private void BuildBottomButtons()
        {
        addFav.Text = "加入收藏";
        addFav.SetBounds(18, 592, 110, 38);
        form.Controls.Add(addFav);

        delFav.Text = "删除收藏";
        delFav.SetBounds(136, 592, 110, 38);
        form.Controls.Add(delFav);

        biliLogin.Text = "B站扫码";
        biliLogin.SetBounds(254, 592, 108, 38);
        form.Controls.Add(biliLogin);

        // 另外三家走统一的一条路：开独立 Edge 窗口扫码 → CDP 取 cookie（见 PlatformLogin）
        douyuLogin.Text = "斗鱼扫码";
        douyuLogin.SetBounds(370, 592, 100, 38);
        form.Controls.Add(douyuLogin);

        huyaLogin.Text = "虎牙扫码";
        huyaLogin.SetBounds(478, 592, 100, 38);
        form.Controls.Add(huyaLogin);

        douyinLogin.Text = "抖音扫码";
        douyinLogin.SetBounds(586, 592, 100, 38);
        form.Controls.Add(douyinLogin);

        openPot.Text = "用 PotPlayer 打开";
        openPot.SetBounds(500, 592, 176, 38);
        form.Controls.Add(openPot);

        openMpv.Text = "用 mpv 打开（回车）";
        openMpv.SetBounds(684, 592, 198, 38);
        form.Controls.Add(openMpv);

        }

        /// <summary>会话表：标题、重新打开按钮、按可用宽度分配的列、选中联动。</summary>
        private void BuildSessionTable()
        {
        sessionsLabel.Text = "正在观看 / 最近打开";
        // ---- 播放器路径行（主界面可见；绿色包不自带播放器，这一行就是「去哪里指定」）----
        playerLabel.Text = "播放器路径";
        // 本项目的离线用例统一要求：窗体上的标签一律 MiddleLeft（垂直居中）
        playerLabel.TextAlign = ContentAlignment.MiddleLeft;
        playerHint.TextAlign = ContentAlignment.MiddleLeft;
        playerBox.DropDownStyle = ComboBoxStyle.DropDownList;
        playerBox.Items.AddRange(new object[] { "mpv", "PotPlayer" });
        playerBox.SelectedIndex = 0;
        pickPlayer.Text = "选择程序…";
        pickPlayer.FlatStyle = FlatStyle.Flat;
        pickPlayer.Click += delegate { PickPlayerProgram(); };
        playerBox.SelectedIndexChanged += delegate { RefreshPlayerRow(); };
        form.Controls.Add(playerLabel); form.Controls.Add(playerBox);
        form.Controls.Add(pickPlayer); form.Controls.Add(playerHint);

        sessionsLabel.SetBounds(18, 385, 250, 22);
        sessionsLabel.ForeColor = ModernTheme.TextSecondary;
        form.Controls.Add(sessionsLabel);
        reopen.Text = "重新打开选中房间";
        reopen.SetBounds(800, 380, 182, 30);
        form.Controls.Add(reopen);
        sessionsList.View = View.Details;
        sessionsList.FullRowSelect = true;
        sessionsList.HideSelection = false;
        sessionsList.Columns.Add("房间", 250);
        sessionsList.Columns.Add("播放器", 100);
        sessionsList.Columns.Add("直播状态", 130);
        sessionsList.Columns.Add("画质", 110);
        sessionsList.Columns.Add("缓冲", 130);
        // Five real columns share the client width; HeaderBackdrop paints any native remainder.

        sessionsList.SetBounds(18, 416, 964, 160);
        form.Controls.Add(sessionsList);
        sessionsList.ClientSizeChanged += delegate { LayoutSessionColumns(); };

        recentList.SelectedIndexChanged += delegate
        {
            if (recentList.SelectedIndex >= 0)
                listTips.SetToolTip(recentList, recentList.Items[recentList.SelectedIndex].ToString());
        };
        favList.SelectedIndexChanged += delegate
        {
            if (favList.SelectedIndex >= 0)
                listTips.SetToolTip(favList, favList.Items[favList.SelectedIndex].ToString());
        };

        // ⚠️ 这里**全程用物理坐标** —— 它读的 form.ClientSize 已经是乘过 DPI 的，
        //    所以里面的常量也必须乘，否则会出现「一半按物理、一半按逻辑」的错位
        //    （实测症状：底部按钮跑到 rel y=2360，客户区才 1280）。
        //    调用时机也有讲究：必须在 DpiLayout.ScaleTree **之后** ——
        //    ScaleTree 负责缩放静态 SetBounds，这个委托负责动态那部分，
        //    它会**覆盖**自己管的那些控件，所以顺序反了就等于被缩两次。
        form.ClientSizeChanged += delegate { if (!((DpiForm)form).ApplyingDpi) Layout(); };
        Layout();




        }

        /// <summary>两个计时器：2 秒的会话/开播状态轮询、700ms 的输入防抖预览。</summary>
        private void BuildTimers()
        {
        // 这个函数由 2 秒一次的计时器驱动。以前每次进来都 Items.Clear() 整表重建：
        // 选中项/滚动位置/悬停提示每 2 秒被打断一次。现在先算一个「内容签名」，
        // 没变化就直接返回——只有真的换行、换了状态才重建。

        watchTimer.Interval = 2000;
        watchTimer.Tick += delegate
        {
            RefreshLoginSummaries();
            if (Application.MessageLoop) foreach (string p in platforms) LoginHealth.RequestCheck(p, false);
            foreach (PlaybackSession session in sessions)
            {
                if (session.Ended || session.Polling || (DateTime.UtcNow - session.LastPoll).TotalSeconds < 10) continue;
                session.Polling = true;
                session.LastPoll = DateTime.UtcNow;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string note;
                    RoomStateKind live = CheckRoomState(session.Target.Platform, session.Target.Room, out note);
                    try { AtomicWriteAllText(SessionRoomStatePath(session.Target), RoomStateFileText(session.Target.Platform, live)); }
                    catch (Exception) { }
                    try
                    {
                        form.BeginInvoke((MethodInvoker)delegate
                        {
                            session.Polling = false;
                            if (session.Ended) return;
                            session.LiveState = live;
                            UpdateBadge(session.Target, live);
                            RefreshSessions();
                        });
                    }
                    catch (Exception) { }
                });
            }
            // 收藏和最近播放的开播标记：**有上限的并行**，不是一次只查一个。
            // 原来是「每 2 秒的定时器只发一个房间」，14 个房间要串行等 ≈28 秒才把 [播]/[休] 填满，
            // 用户看到的就是一个一个慢慢跳出来。调度策略见 BadgeScheduler（纯函数、有离线回归）。
            PumpBadgeChecks();
            RefreshSessions();
        };
        watchTimer.Start();

        // 每次输入变化立即使旧请求失效；网络响应不会覆盖后来输入的房间状态。
        previewTimer.Interval = 700;          // 打字防抖
        previewTimer.Tick += delegate
        {
            previewTimer.Stop();
            Target target = Parse(input.Text, platform);
            if (target == null) return;
            int token = previewToken;
            if (currentLaunch != null) return;
            status.ForeColor = ModernTheme.TextSecondary;
            status.Text = "正在查询 " + PlatformName(target.Platform) + " " + target.Room + " 的开播状态…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string note;
                RoomStateKind live = CheckRoomState(target.Platform, target.Room, out note);
                try
                {
                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        if (token != previewToken || currentLaunch != null) return;
                        UpdateBadge(target, live);
                        string name = PlatformName(target.Platform) + " " + target.Room;
                        if (live == RoomStateKind.Unknown)
                        {
                            status.ForeColor = ModernTheme.TextSecondary;
                            status.Text = name + "：没查到开播状态" + (note != null ? "（" + note + "）" : "");
                        }
                        else if (live == RoomStateKind.Live)
                        {
                            status.ForeColor = ModernTheme.Success;
                            status.Text = name + "：正在直播";
                        }
                        else if (live == RoomStateKind.Replay)
                        {
                            status.ForeColor = ModernTheme.Accent;
                            status.Text = name + "：" + RoomStateLabel(target.Platform, live)
                                + (note != null ? "（" + note + "）" : "");
                        }
                        else
                        {
                            status.ForeColor = ModernTheme.Danger;
                            status.Text = name + "：当前未开播" + (note != null ? "（" + note + "）" : "");
                        }
                    });
                }
                catch (Exception) { }
            });
        };
        }

        /// <summary>
        /// 收藏 / 最近播放的开播标记候选：**收藏在前、最近播放在后**（保持原来的优先级）。
        /// 返回快照 —— 查询是异步的，不能攥着这两个列表的枚举不放。
        /// </summary>
        private List<Target> BadgeCandidates()
        {
            List<Target> list = new List<Target>();
            foreach (KeyValuePair<string, string> favorite in currentFavorites)
            {
                Target candidate = Parse(favorite.Value, "bili");
                if (candidate != null) list.Add(candidate);
            }
            foreach (HistoryEntry entry in currentHistory)
            {
                Target candidate = Parse(entry.Room, platform);
                if (candidate != null) list.Add(candidate);
            }
            return list;
        }

        /// <summary>
        /// 把开播标记的查询「补满」到 BadgeScheduler.MaxInFlight 个在途。
        ///
        /// 定时器每 2 秒调一次；**每个查询回来时也调一次** —— 空出一个名额就立刻补位，
        /// 不用干等下一次 tick。所以 14 个房间是「几秒内全部到位」，而不是原来的「一个一个慢慢跳」。
        /// </summary>
        private void PumpBadgeChecks()
        {
            List<Target> candidates = BadgeCandidates();
            List<string> keys = new List<string>(candidates.Count);
            Dictionary<string, Target> byKey = new Dictionary<string, Target>();
            foreach (Target candidate in candidates)
            {
                string key = candidate.Platform + ":" + candidate.Room;
                if (byKey.ContainsKey(key)) continue;      // 同一个房间可能同时在收藏和历史里
                byKey[key] = candidate;
                keys.Add(key);
            }
            foreach (string key in BadgeScheduler.Pick(keys, badgePending, badgeTimes,
                DateTime.UtcNow, BadgeScheduler.MaxInFlight))
            {
                Target checking = byKey[key];
                badgePending.Add(key);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string note;
                    RoomStateKind live = CheckRoomState(checking.Platform, checking.Room, out note);
                    try
                    {
                        form.BeginInvoke((MethodInvoker)delegate
                        {
                            badgePending.Remove(key);
                            UpdateBadge(checking, live);
                            PumpBadgeChecks();             // 空出一个名额就立刻补位
                        });
                    }
                    catch (Exception) { }
                });
            }
        }

        /// <summary>其余事件接线：输入回车、取消启动、双击开播、扫码按钮、收藏增删、关闭窗口。</summary>
        private void WireEvents()
        {
        status.TextChanged += delegate { Layout(); };
        input.TextChanged += delegate
        {
            previewToken++;
            previewTimer.Stop();
            if (Parse(input.Text, platform) != null) previewTimer.Start();
        };


        cancelLaunch.Click += delegate
        {
            if (currentLaunch != null)
            {
                currentLaunch.Cancel();
                cancelLaunch.Enabled = false;
                status.Text = "正在取消启动…";
            }
        };
        reopen.Click += delegate
        {
            if (sessionsList.SelectedItems.Count == 0) return;
            PlaybackSession previous = sessionsList.SelectedItems[0].Tag as PlaybackSession;
            if (previous == null) return;
            input.Text = previous.Target.Platform + ":" + previous.Target.Room;
            qualityBox.SelectedIndex = previous.Target.Quality == "smooth" ? 2 : previous.Target.Quality == "high" ? 1 : 0;
            bufferBox.SelectedIndex = previous.Target.BufferMode == "low" ? 1 : 0;
            danmakuBox.SelectedIndex = previous.Target.DanmakuMode == "on" ? 1 : previous.Target.DanmakuMode == "off" ? 2 : 0;
            Launch(previous.Player);
        };

        recentList.SelectedIndexChanged += delegate
        {
            if (recentList.SelectedIndex >= 0 && recentList.SelectedIndex < currentHistory.Count)
                input.Text = currentHistory[recentList.SelectedIndex].Room;
        };
        favList.SelectedIndexChanged += delegate
        {
            if (favList.SelectedIndex >= 0 && favList.SelectedIndex < currentFavorites.Count)
            {
                Target t = Parse(currentFavorites[favList.SelectedIndex].Value, "bili");
                if (t != null) input.Text = t.Platform == "bili" ? t.Room : t.Platform + ":" + t.Room;
            }
        };
        recentList.DoubleClick += delegate { if (recentList.SelectedIndex >= 0) Launch("mpv"); };
        favList.DoubleClick += delegate { if (favList.SelectedIndex >= 0) Launch("mpv"); };

        biliLogin.Click += delegate { StartBiliQrLogin(form); };
        douyuLogin.Click += delegate { PlatformLogin.Show(form, "douyu"); };
        huyaLogin.Click += delegate { PlatformLogin.Show(form, "huya"); };
        douyinLogin.Click += delegate { PlatformLogin.Show(form, "douyin"); };
        openMpv.Click += delegate { Launch("mpv"); };
        openPot.Click += delegate { Launch("potplayer"); };

        lookup.Click += delegate
        {
            ShowLookup(form, delegate(LookupCandidate candidate, string name)
            {
                bool added = SaveLookupFavorite(candidate, name, favorites);
                RefreshLists();
                status.ForeColor = ModernTheme.Success;
                status.Text = added ? "已收藏 " + name : "该房间已经在收藏中";
            });
        };

        addFav.Click += delegate
        {
            Target target = Parse(input.Text, platform);
            if (target == null)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = "请先输入有效房间号";
                return;
            }
            string suggestion = NameForHistory(target, favorites);
            if (suggestion == null || suggestion.Length == 0) suggestion = target.Room;
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "给这个" + PlatformName(target.Platform) + "房间起个收藏名：", "加入收藏", suggestion).Trim();
            if (name.Length == 0) return;
            string value = target.Platform == "bili" ? target.Room : target.Platform + ":" + target.Room;
            var next = CreateFavoritesUpdate(favorites);
            next.RemoveAll(delegate(KeyValuePair<string, string> favorite) {
                Target old = Parse(favorite.Value, "bili");
                return old != null && old.Platform == target.Platform && old.Room == target.Room;
            });
            next.Add(new KeyValuePair<string, string>(name, value));
            MarkFavoriteChanged(next, value);
            try
            {
                SaveFavorites(next);
                favorites = next;
                RefreshLists();
                status.ForeColor = ModernTheme.Success;
                status.Text = "已收藏 " + name;
            }
            catch (Exception ex)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = "收藏失败：" + ex.Message;
            }
        };

        delFav.Click += delegate
        {
            if (favList.SelectedIndex < 0 || favList.SelectedIndex >= currentFavorites.Count)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = "请先在右边列表里选一个收藏";
                return;
            }
            KeyValuePair<string, string> selected = currentFavorites[favList.SelectedIndex];
            string name = selected.Key;
            if (MessageBox.Show("删除收藏「" + name + "」？", "看直播", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var next = CreateFavoritesUpdate(favorites);
            next.RemoveAll(delegate(KeyValuePair<string, string> favorite) {
                return FavoriteRoomKey(favorite.Value) == FavoriteRoomKey(selected.Value);
            });
            try
            {
                SaveFavorites(next);
                favorites = next;
                RefreshLists();
                status.ForeColor = ModernTheme.Success;
                status.Text = "已删除 " + name;
            }
            catch (Exception ex)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = "删除失败：" + ex.Message;
            }
        };

        form.FormClosing += delegate
        {
            watchTimer.Stop();
            previewTimer.Stop();
            if (currentLaunch != null) currentLaunch.Cancel();
        };

        form.AcceptButton = openMpv;
        form.Shown += delegate { input.Focus(); if (Application.MessageLoop) foreach (string p in platforms) LoginHealth.RequestCheck(p, false); };
        }

        private void RefreshLoginSummaries()
        {
            foreach (string p in platforms)
            {
                LoginHealth.Result result = LoginHealth.Get(p);
                Label label = loginLabels[p];
                label.Text = PlatformName(p) + " Cookie：" + LoginHealth.Title(result) + "\r\n"
                    + (result.CheckedUtc > 0 ? "检测 " + LoginHealth.TimeText(result.CheckedUtc) : "点击查看详情 / 检测");
                label.ForeColor = LoginHealth.ColorFor(result);
                label.BackColor = ModernTheme.Card;
                enhanceTip.SetToolTip(label, LoginHealth.Details(p));
            }
        }


        private bool layingOut;
        private int Px(int value) { return DpiLayout.S(form, value); }

        // Keep semantic groups together, but wrap groups as the window narrows.
        private int PlaceGroups(Control[][] groups, int y, int width)
        {
            int left = Px(18), x = left, rowHeight = 0, gap = Px(8);
            var row = new List<Control>();
            foreach (Control[] group in groups)
            {
                int groupWidth = 0, groupHeight = 0;
                foreach (Control c in group) { groupWidth += c.Width + Px(4); groupHeight = Math.Max(groupHeight, c.Height); }
                groupWidth -= Px(4);
                if (x > left && x + groupWidth > width - left)
                {
                    CenterRow(row, y, rowHeight);
                    y += rowHeight + gap; x = left; rowHeight = 0; row.Clear();
                }
                foreach (Control c in group)
                {
                    c.Left = x;
                    row.Add(c);
                    x += c.Width + Px(4);
                }
                x += gap - Px(4); rowHeight = Math.Max(rowHeight, groupHeight);
            }
            CenterRow(row, y, rowHeight);
            return y + rowHeight + gap;
        }

        private static void CenterRow(List<Control> controls, int top, int height)
        {
            // Native combo heights follow their font/DPI, so center the whole row
            // after measuring every group rather than centering each group alone.
            foreach (Control control in controls)
                control.Top = top + (height - control.Height) / 2;
        }

        internal void Layout()
        {
            if (layingOut || ((DpiForm)form).ApplyingDpi) return;
            layingOut = true;
            Point scroll = new Point(-form.AutoScrollPosition.X, -form.AutoScrollPosition.Y);
            form.SuspendLayout();
            try
            {
                form.AutoScrollPosition = Point.Empty;
                int width = Math.Max(Px(460), form.ClientSize.Width);
                int inset = Px(18), available = width - inset * 2;
                int platformWidth = Px(392);
                int y = Px(16);
                int platformX = inset;
                foreach (string p in platforms)
                {
                    Button b = platformButtons[p]; b.SetBounds(platformX, y, Px(92), Px(34));
                    platformX += Px(100);
                }
                foreach (Label label in new Label[] { inputLabel, qualityLabel, bufferLabel, danmakuLabel, scLabel, enhanceLabel,
                    status, recentLabel, favLabel, sessionsLabel })
                    label.TextAlign = ContentAlignment.MiddleLeft;
                foreach (Control label in new Control[] { qualityLabel, bufferLabel, danmakuLabel, scLabel, enhanceLabel })
                    label.Width = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Px(8);
                qualityLabel.Width = enhanceLabel.Width = Math.Max(qualityLabel.Width, enhanceLabel.Width);
                y += Px(42);
                bool joinedInput = available >= platformWidth + Px(300);
                int inputY = joinedInput ? Px(16) : y;
                inputLabel.SetBounds(joinedInput ? inset + platformWidth + Px(8) : inset, inputY, Px(104), Px(34));
                int inputLeft = inputLabel.Right + Px(4);
                inputHelp.SetBounds(width - inset - Px(88), inputY, Px(88), Px(34));
                input.SetBounds(inputLeft, inputY, Math.Max(Px(100), inputHelp.Left - Px(8) - inputLeft), Px(34));
                if (!joinedInput) y += Px(42);
                y = PlaceGroups(new Control[][] {
                    new Control[] { qualityLabel, qualityBox }, new Control[] { bufferLabel, bufferBox },
                    new Control[] { cancelLaunch }, new Control[] { danmakuLabel, danmakuBox },
                    new Control[] { danmakuSettings }, new Control[] { scLabel, scBox }
                }, y, width);
                y = PlaceGroups(new Control[][] { new Control[] { enhanceLabel, enhanceBox }, new Control[] { lookup }, new Control[] { localData } }, y, width);
                int statusLeft = localData.Right + Px(12);
                int inlineWidth = width - inset - statusLeft;
                int statusTextWidth = TextRenderer.MeasureText(status.Text, status.Font, Size.Empty,
                    TextFormatFlags.SingleLine).Width;
                if (inlineWidth >= Px(240) && statusTextWidth <= inlineWidth)
                    status.SetBounds(statusLeft, localData.Top, inlineWidth, localData.Height);
                else
                {
                    int textHeight = TextRenderer.MeasureText(status.Text, status.Font, new Size(available, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                    int statusHeight = Math.Max(Px(24), Math.Min(Px(72), textHeight + Px(4)));
                    status.SetBounds(inset, y, available, statusHeight); y += statusHeight + Px(8);
                }

                int loginColumns = available >= Px(900) ? 4 : 2;
                int loginWidth = (available - Px(8) * (loginColumns - 1)) / loginColumns;
                for (int i = 0; i < platforms.Length; i++)
                    loginLabels[platforms[i]].SetBounds(inset + (i % loginColumns) * (loginWidth + Px(8)),
                        y + (i / loginColumns) * Px(56), loginWidth, Px(48));
                y += (4 / loginColumns) * Px(56);

                // 播放器路径行：说清楚「绿色包不带播放器 + 选一次就记住」，路径直接摆出来
                playerLabel.SetBounds(inset, y + Px(6), Px(80), Px(24));
                playerBox.SetBounds(inset + Px(84), y + Px(2), Px(104), Px(28));
                pickPlayer.SetBounds(playerBox.Right + Px(10), y + Px(2), Px(120), Px(28));
                playerHint.SetBounds(inset, y + Px(33), available, Px(20));
                y += Px(58);
                bool stacked = available < Px(790);
                int listHeight = Math.Max(Px(stacked ? 100 : 120),
                    (form.ClientSize.Height - y - Px(stacked ? 330 : 240)) / (stacked ? 3 : 2));
                recentLabel.SetBounds(inset, y, stacked ? available : (available - Px(18)) / 2, Px(22));
                recentList.SetBounds(inset, y + Px(26), recentLabel.Width, listHeight);
                if (stacked)
                {
                    favLabel.SetBounds(inset, recentList.Bottom + Px(10), available, Px(22));
                    favList.SetBounds(inset, favLabel.Bottom + Px(4), available, listHeight);
                }
                else
                {
                    favLabel.SetBounds(recentList.Right + Px(18), y, available - recentList.Width - Px(18), Px(22));
                    favList.SetBounds(favLabel.Left, recentList.Top, favLabel.Width, listHeight);
                }
                y = Math.Max(recentList.Bottom, favList.Bottom) + Px(12);
                sessionsLabel.SetBounds(inset, y, Math.Max(Px(100), available - Px(196)), Px(30));
                reopen.SetBounds(width - inset - Px(182), y, Px(182), Px(30));
                y += Px(38);
                sessionsList.SetBounds(inset, y, available, Math.Max(Px(90), form.ClientSize.Height - y - Px(76)));
                LayoutSessionColumns(); y = sessionsList.Bottom + Px(12);
                y = PlaceGroups(new Control[][] {
                    new Control[] { addFav }, new Control[] { delFav }, new Control[] { biliLogin },
                    new Control[] { douyuLogin }, new Control[] { huyaLogin }, new Control[] { douyinLogin },
                    new Control[] { openPot }, new Control[] { openMpv }
                }, y, width);
                // Playback actions finish at the same right inset as the lists/input.
                if (openPot.Top == openMpv.Top)
                {
                    openMpv.Left = width - inset - openMpv.Width;
                    openPot.Left = openMpv.Left - Px(8) - openPot.Width;
                }
                form.AutoScrollMinSize = new Size(0, y + Px(10));
                form.AutoScrollPosition = scroll;
            }
            finally { form.ResumeLayout(true); layingOut = false; }
        }

        private bool sizingSessionColumns;
        private void LayoutSessionColumns()
        {
            if (sizingSessionColumns || sessionsList.Columns.Count != 5) return;
            sizingSessionColumns = true;
            try
            {
                // Relative widths also follow DPI and window resizing. Reserve only
                // the native border/filler; a wide empty column must not eat the row.
                int available = Math.Max(5, sessionsList.ClientSize.Width - 1);
                int[] weights = { 38, 14, 17, 13, 18 };
                int used = 0;

                for (int index = 0; index < weights.Length; index++)
                {
                    int width = index == weights.Length - 1 ? available - used : available * weights[index] / 100;
                    sessionsList.Columns[index].Width = width; used += width;
                }
            }
            finally { sizingSessionColumns = false; }
        }

        internal string Badge(Target target)
        {
            RoomStateKind live;
            if (target == null || !roomBadges.TryGetValue(target.Platform + ":" + target.Room, out live)) return "";
            return RoomStateBadge(target.Platform, live);
        }

        internal void UpdateBadge(Target target, RoomStateKind live)
        {
            if (target == null) return;
            string key = target.Platform + ":" + target.Room;
            roomBadges[key] = live;
            badgeTimes[key] = DateTime.UtcNow;
            if (target.Platform != platform) return;
            for (int i = 0; i < currentFavorites.Count; i++)
            {
                Target itemTarget = Parse(currentFavorites[i].Value, "bili");
                if (itemTarget != null && itemTarget.Room == target.Room)
                    favList.Items[i] = Badge(itemTarget) + FavoriteLabel(currentFavorites[i]);
            }
            for (int i = 0; i < currentHistory.Count; i++)
                if (currentHistory[i].Room == target.Room)
                    recentList.Items[i] = Badge(target) + HistoryLabel(currentHistory[i]);
        }

        internal void RefreshLists()
        {
            foreach (KeyValuePair<string, ModernButton> pair in platformButtons)
            {
                // 选中态和文字色都由 ModernButton 自绘决定 —— 原来那两行设的
                // ForeColor / FlatAppearance.BorderColor 对自绘按钮**完全无效**，
                // 留着只会误导后人，删掉。
                pair.Value.Selected = pair.Key == platform;
            }

            currentFavorites.Clear();
            foreach (var favorite in favorites)
            {
                Target t = Parse(favorite.Value, "bili");
                if (t != null && t.Platform == platform) currentFavorites.Add(favorite);
            }
            currentHistory.Clear();
            List<HistoryEntry> entries;
            if (history.TryGetValue(platform, out entries) && entries != null) currentHistory.AddRange(entries);

            favList.Items.Clear();
            foreach (var favorite in currentFavorites)
                favList.Items.Add(Badge(Parse(favorite.Value, "bili")) + FavoriteLabel(favorite));
            recentList.Items.Clear();
            foreach (var entry in currentHistory)
                recentList.Items.Add(Badge(Parse(entry.Room, platform)) + HistoryLabel(entry));

            recentLabel.Text = "最近播放（" + PlatformName(platform) + "，双击用 mpv 打开）";
            favLabel.Text = "收藏（" + PlatformName(platform) + "，" + currentFavorites.Count + " 个）";
        }

        internal void RefreshAll()
        {
            RefreshLists();
            RefreshPlayerRow();
            input.Text = "";
            input.Focus();
            status.ForeColor = ModernTheme.Accent;
            status.Text = platform == "douyu" || platform == "douyin"
                ? "选房间 → 回车用 mpv 打开（窗口不会关）；" + PlatformName(platform) + "会经本地中转（约 2 秒起播）"
                : "选房间 → 回车用 mpv 打开（窗口不会自动关）";
            if (history is UnreadableHistory)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = HistoryReadFailureMessage;
            }
        }

        /// <summary>主界面「播放器路径」行：显示当前播放器、它的路径、以及能不能用。</summary>
        internal void RefreshPlayerRow()
        {
            try
            {
                string tool = playerBox.SelectedIndex == 1 ? "potplayer" : "mpv";
                string path = LiveHub.CurrentToolPath(tool);
                bool usable = PlayerFileLooksValid(path);
                string shown = string.IsNullOrEmpty(path) ? "(未设置)" : path;
                playerHint.Text = usable
                    ? "当前：" + shown + "　·　已就绪，选一次就记住（写在 exe 旁边的 live-hub.json）"
                    : "当前：" + shown + "　·　还没有可用的 " + (tool == "mpv" ? "mpv" : "PotPlayer")
                      + "：点右边「选择程序…」指到你自己的 " + (tool == "mpv" ? "mpv.exe" : "PotPlayerMini64.exe")
                      + "（绿色包不自带播放器）";
                playerHint.ForeColor = usable ? ModernTheme.Text : ModernTheme.Danger;
                listTips.SetToolTip(playerHint, shown);
            }
            catch (Exception ex) { Diagnostics.Report("刷新播放器行", ex); }
        }

        /// <summary>0 字节 / 非 PE（占位或损坏）都算无效 —— 否则点开只会得到一句 Windows 原始错误。</summary>
        private static bool PlayerFileLooksValid(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                if (new FileInfo(path).Length < 1024) return false;
                using (FileStream stream = File.OpenRead(path))
                {
                    int b0 = stream.ReadByte(), b1 = stream.ReadByte();
                    return b0 == 'M' && b1 == 'Z';
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>选播放器程序：写进 exe 旁边的 live-hub.json（选一次就记住）并立刻生效。</summary>
        internal void PickPlayerProgram()
        {
            string tool = playerBox.SelectedIndex == 1 ? "potplayer" : "mpv";
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = tool == "mpv" ? "选择 mpv.exe" : "选择 PotPlayerMini64.exe";
                picker.Filter = tool == "mpv"
                    ? "mpv (mpv.exe)|mpv.exe|可执行文件 (*.exe)|*.exe"
                    : "PotPlayer (PotPlayerMini64.exe)|PotPlayerMini64.exe|可执行文件 (*.exe)|*.exe";
                picker.CheckFileExists = true;
                string current = LiveHub.CurrentToolPath(tool);
                try { if (!string.IsNullOrEmpty(current) && File.Exists(current)) picker.InitialDirectory = Path.GetDirectoryName(current); }
                catch (Exception ex) { Diagnostics.Report("播放器对话框初始目录", ex); }
                if (picker.ShowDialog(form) != DialogResult.OK) return;
                string error;
                if (!LiveHub.SaveToolPath(tool, picker.FileName, out error))
                {
                    status.ForeColor = ModernTheme.Danger;
                    status.Text = "播放器路径没能保存：" + (error ?? "未知原因");
                    return;
                }
                LiveHub.ApplyToolPath(tool, picker.FileName);
                status.ForeColor = ModernTheme.Accent;
                status.Text = "已记住 " + (tool == "mpv" ? "mpv" : "PotPlayer") + " 路径，之后打开房间直接用它：" + picker.FileName;
                RefreshPlayerRow();
            }
        }

        internal void RefreshSessions()
        {
            var rows = new List<string[]>();
            var signatureText = new StringBuilder();
            foreach (PlaybackSession session in sessions)
            {
                // 「已下播」优先于「播放器已退出」：mpv 拉不到流时会自己退出（keep-open 只管
                // 播完之后），原来那一行就只显示「播放器已退出」，看不出真正原因 ——
                // 2026-10-16 虎牙 910323 下播转重播就是这个表现。
                string relayState = "";
                try
                {
                    string relayPath = SessionRelayStatePath(session.Target);
                    relayState = File.Exists(relayPath) ? File.ReadAllText(relayPath, Encoding.UTF8).Trim() : "";
                }
                catch (Exception) { }
                bool down = session.LiveState == RoomStateKind.Offline || relayState == "OFFLINE";
                string state = down ? "已下播"
                    : session.Ended ? "播放器已退出"
                    : RoomStateLabel(session.Target.Platform, session.LiveState);
                if (session.LiveState == RoomStateKind.Replay)
                    state = RoomStateLabel(session.Target.Platform, session.LiveState)
                        + (relayState.StartsWith("REPLAY-UNAVAILABLE|", StringComparison.Ordinal) ? " · 流取不到" : "");
                else if (!down && !session.Ended && relayState == "RECONNECTING") state = "重连中";
                string[] row = new string[] {
                    PlatformName(session.Target.Platform) + " " + session.Target.Room,
                    session.Player == "potplayer" ? "PotPlayer" : "mpv",
                    state,
                    session.Target.Quality == "smooth" ? "流畅" : session.Target.Quality == "high" ? "高清" : "原画",
                    session.Player == "potplayer" ? "播放器默认" : session.Target.BufferMode == "low" ? "延迟更低" : "播放更稳定" };
                rows.Add(row);
                signatureText.Append(string.Join("|", row)).Append('\n');
            }
            string signature = signatureText.ToString();
            if (signature == lastSessionSignature && sessionsList.Items.Count == rows.Count) return;
            lastSessionSignature = signature;

            PlaybackSession selectedSession = sessionsList.SelectedItems.Count > 0
                ? sessionsList.SelectedItems[0].Tag as PlaybackSession : null;
            sessionsList.BeginUpdate();
            sessionsList.Items.Clear();
            for (int index = 0; index < rows.Count; index++)
            {
                var item = new ListViewItem(rows[index][0]);
                for (int column = 1; column < rows[index].Length; column++) item.SubItems.Add(rows[index][column]);
                item.Tag = sessions[index];
                sessionsList.Items.Add(item);
                if (ReferenceEquals(sessions[index], selectedSession)) item.Selected = true;
            }
            sessionsList.EndUpdate();
            LayoutSessionColumns();
        }

        internal void Launch(string player)
        {
            if (currentLaunch != null) return;
            Target target = Parse(input.Text, platform);
            if (target == null)
            {
                status.ForeColor = ModernTheme.Danger;
                status.Text = "房间号或链接无效";
                return;
            }
            target.SessionId = Guid.NewGuid().ToString("N");
            target.Quality = qualityBox.SelectedIndex == 2 ? "smooth" : qualityBox.SelectedIndex == 1 ? "high" : "best";
            target.BufferMode = bufferBox.SelectedIndex == 1 ? "low" : "stable";
            target.DanmakuMode = danmakuBox.SelectedIndex == 1 ? "on" : danmakuBox.SelectedIndex == 2 ? "off" : "auto";
            previewToken++;
            CancellationTokenSource request = new CancellationTokenSource();
            currentLaunch = request;
            openMpv.Enabled = openPot.Enabled = false;
            cancelLaunch.Enabled = true;
            status.ForeColor = ModernTheme.TextSecondary;
            status.Text = "正在解析 " + PlatformName(target.Platform) + " " + target.Room + "…";
            Stopwatch elapsed = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Process process = null;
                string error = null;
                try
                {
                    LaunchCancellation = request.Token;
                    AtomicWriteAllText(SessionRoomStatePath(target), "");
                    ProcessStartInfo start = BuildStart(target, player);
                    if (!request.IsCancellationRequested)
                    {
                        form.BeginInvoke((MethodInvoker)delegate
                        {
                            if (ReferenceEquals(currentLaunch, request))
                                status.Text = "地址已解析，正在打开播放器…";
                        });
                        // 以前这里 Sleep(700) 等中转「起来」。现在 StartRelay 是等中转回报 READY 端口
                        // （此时监听早就 bind+listen 了）才返回，所以这个盲等可以去掉。
                        if (!request.IsCancellationRequested)
                        {
                            process = Process.Start(start);
                            if (process != null && request.IsCancellationRequested)
                            {
                                try { process.Kill(); } catch (Exception) { }
                                process = null;
                            }
                            if (process != null) StartRoomWatcher(target, process);
                            if (process != null) StartDanmakuHost(target, process);
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    LaunchCancellation = default(CancellationToken);
                    if (process == null && target.RelayProcessId != 0)
                        try { using (Process relay = Process.GetProcessById(target.RelayProcessId)) relay.Kill(); }
                        catch (Exception) { }
                }
                try
                {
                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!ReferenceEquals(currentLaunch, request)) return;
                        currentLaunch = null;
                        openMpv.Enabled = openPot.Enabled = true;
                        cancelLaunch.Enabled = false;
                        if (process == null)
                        {
                            status.ForeColor = request.IsCancellationRequested ? ModernTheme.TextSecondary : ModernTheme.Danger;
                            status.Text = request.IsCancellationRequested ? "已取消启动" : "打开失败：" + (error ?? "播放器没有启动");
                            return;
                        }
                        var session = new PlaybackSession { Target = target, Player = player, Process = process,
                            LastPoll = DateTime.MinValue };
                        sessions.Insert(0, session);
                        if (sessions.Count > 20)
                        {
                            PlaybackSession oldest = sessions[sessions.Count - 1];
                            if (oldest.Ended) { sessions.RemoveAt(sessions.Count - 1); oldest.Process.Dispose(); }
                        }
                        process.EnableRaisingEvents = true;
                        process.Exited += delegate
                        {
                            try { form.BeginInvoke((MethodInvoker)delegate
                            {
                                session.Ended = true;
                                try { AtomicWriteAllText(SessionRoomStatePath(target), ""); } catch (Exception) { }
                                RefreshSessions();
                            }); } catch (Exception) { }
                        };
                        if (process.HasExited) session.Ended = true;
                        AddHistory(history, target, NameForHistory(target, favorites));
                        bool historySaved = SaveHistory(history);
                        RefreshLists();
                        RefreshSessions();
                        status.ForeColor = historySaved ? ModernTheme.Success : ModernTheme.Danger;
                        status.Text = "已用 " + (player == "potplayer" ? "PotPlayer" : "mpv") + " 打开 "
                            + PlatformName(target.Platform) + " " + target.Room + "（" + elapsed.Elapsed.TotalSeconds.ToString("0.0") + " 秒）"
                            + (historySaved ? "" : "；" + (history is UnreadableHistory
                                ? HistoryReadFailureMessage : "历史保存失败，请检查目录权限/占用"));
                    });
                }
                catch (Exception) { }
            });
        }

        /// <summary>建窗并进入消息循环（原 ShowDialog 的最后两步）。</summary>
        internal void Run()
        {
            Build();
            Application.Run(form);
        }
    }

    /// 把窗体控件树导成 JSON（`--ui-probe` 用）。
    ///
    /// 记的是「结构」而不是截图：控件类型 / 父容器 / 文本 / 相对父容器的边界 / 可见与可用性。
    /// 用途有两个：① 拆分 ShowDialog 时做**前后等价**对照（diff 必须为空）；
    /// ② 离线回归里断言「该有的控件都在、同一行的控件不重叠、底排按钮放得下」。
}
