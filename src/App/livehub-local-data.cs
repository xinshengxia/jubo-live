using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

internal static class BoundedLog
{
    internal const int FileLimit = 1024 * 1024;
    internal const int ArchiveCount = 4;
    internal const long DirectoryLimit = 32L * 1024 * 1024;
    private static readonly object MaintenanceGate = new object();
    private static readonly Dictionary<string, DateTime> LastMaintenance = new Dictionary<string, DateTime>();

    internal static bool TryAppend(string path, string line)
    {
        try
        {
            path = Path.GetFullPath(path);
            for (int i = 0; i <= ArchiveCount; i++)
                if (LiveHub.IsPersistentDataPath(path + (i == 0 ? "" : "." + i))) return false;
            string key;
            using (SHA256 hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
            using (Mutex mutex = new Mutex(false, "Local\\LiveHubLog-" + key))
            {
                bool entered;
                try { entered = mutex.WaitOne(1000); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) return false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    LocalData.RequireOrdinaryPath(path);
                    string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine;
                    if (text.Length > 16000) text = text.Substring(0, 16000) + " [truncated]" + Environment.NewLine;
                    if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > FileLimit)
                    {
                        for (int i = ArchiveCount; i >= 1; i--)
                        {
                            string target = path + "." + i;
                            string source = i == 1 ? path : path + "." + (i - 1);
                            LocalData.RequireOrdinaryPath(target);
                            LocalData.RequireOrdinaryPath(source);
                            if (File.Exists(target)) File.Delete(target);
                            if (File.Exists(source)) File.Move(source, target);
                        }
                    }
                    if (!File.Exists(path) || new FileInfo(path).Length == 0) text = "BUILD=" + BuildInfo.Text + Environment.NewLine + text;
                    File.AppendAllText(path, text, Encoding.UTF8);
                    string directory = Path.GetDirectoryName(path);
                    lock (MaintenanceGate)
                    {
                        DateTime last;
                        if (!LastMaintenance.TryGetValue(directory, out last) || (DateTime.UtcNow - last).TotalMinutes >= 1)
                        {
                            Maintain(directory);
                            LastMaintenance[directory] = DateTime.UtcNow;
                        }
                    }
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception) { return false; }
    }

    // 只清理本程序拥有的日志，不把目录中的任意文件当成缓存。
    internal static bool OwnedName(string name)
    {
        int dot = name.LastIndexOf('.');
        int number;
        if (dot >= 0 && int.TryParse(name.Substring(dot + 1), out number) && number >= 1 && number <= ArchiveCount)
            name = name.Substring(0, dot);
        return name == "danmaku.log" || name == "janitor.log" || name == "relay.log"
            || name == "bili-parser.log" || name == "huya-parser.log" || name == "douyu-parser.log" || name == "douyin-parser.log"
            || name == "huya-check.log" || System.Text.RegularExpressions.Regex.IsMatch(name,
                @"^(crash-\d{8}|danmaku-[a-f0-9]{32}|session-[a-f0-9]{32}|offline-tests-\d{8}-\d{6})\.log$");
    }

    internal static void Maintain(string directory)
    {
        if (!Directory.Exists(directory)) return;
        LocalData.RequireOrdinaryPath(directory);
        FileInfo[] files = new DirectoryInfo(directory).GetFiles().Where(f => OwnedName(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long total = 0;
        foreach (FileInfo file in files)
        {
            if (LiveHub.IsPersistentDataPath(file.FullName)) continue;
            total += file.Length;
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-14) && total <= DirectoryLimit) continue;
            try { LocalData.RequireOrdinaryPath(file.FullName); file.Delete(); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        }
    }
}

internal static class LocalData
{
    // 不递归跟随 junction/symlink，连任一祖先目录是重解析点也拒绝。
    internal static void RequireOrdinaryPath(string path)
    {
        for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拒绝操作重解析路径：" + current);
    }

    internal static string CleanCache()
    {
        List<string> failures = new List<string>();
        string runtime = LiveHub.RuntimeDir;
        try
        {
            RequireOrdinaryPath(runtime);
            if (Directory.Exists(runtime)) foreach (string path in Directory.GetFiles(runtime))
                if (LiveHub.RuntimeCacheName(Path.GetFileName(path))
                    && !LiveHub.IsPersistentDataPath(path)) DeleteFile(path, failures);
            string logs = Path.Combine(runtime, "logs");
            RequireOrdinaryPath(logs);
            if (Directory.Exists(logs)) foreach (string path in Directory.GetFiles(logs))
                if (BoundedLog.OwnedName(Path.GetFileName(path)) && !LiveHub.IsPersistentDataPath(path)) DeleteFile(path, failures);
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        return string.Join(Environment.NewLine, failures.ToArray());
    }

    internal static string Logout(string platform)
    {
        List<string> failures = new List<string>();
        lock (PlatformLogin.CredentialGate)
        {
            string cookie;
            if (platform == "bili") cookie = LiveHub.Cookie;
            else
            {
                LoginPlatform info = PlatformLogin.Get(platform);
                if (info == null) return "未知平台";
                cookie = PlatformLogin.CookiesPath(info);
            }
            DeleteFile(cookie, failures);
            DeleteFile(cookie + ".json", failures);
            if (platform != "bili")
            {
                DeleteProfile(Path.Combine(LiveHub.RuntimeDir, platform + "-login-profile"), failures);
                if (platform == "douyin") DeleteProfile(Path.Combine(LiveHub.RuntimeDir, "douyin-verify-profile"), failures);
                DeleteProfile(Path.Combine(LiveHub.RuntimeDir, platform + "-webview-login-profile"), failures);
                PlatformLogin.Invalidate(platform);
            }
        }
        PlatformHealth.Record(platform, "login", failures.Count == 0 ? "logged-out" : "cleanup-failed");
        return string.Join(Environment.NewLine, failures.ToArray());
    }

    private static void DeleteFile(string path, List<string> failures)
    {
        try { RequireOrdinaryPath(path); if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { failures.Add(Path.GetFileName(path) + ": " + ex.GetType().Name); }
    }

    private static bool SamePath(string first, string second)
    {
        return !string.IsNullOrEmpty(second) && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteProfile(string path, List<string> failures)
    {
        try
        {
            RequireOrdinaryPath(path);
            if (!Directory.Exists(path)) return;
            // 先检查全树，避免遇到 junction 前已删掉半个 profile。
            ValidateTree(path);
            Directory.Delete(path, true);
        }
        catch (Exception ex) { failures.Add(Path.GetFileName(path) + ": " + ex.GetType().Name + "（请关闭该登录窗口后重试）"); }
    }

    private static void ValidateTree(string path)
    {
        RequireOrdinaryPath(path);
        foreach (string file in Directory.GetFiles(path)) RequireOrdinaryPath(file);
        foreach (string child in Directory.GetDirectories(path)) ValidateTree(child);
    }

    internal static string Usage()
    {
        long[] sizes = new long[5]; int count = 0; bool partial = false;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RequireOrdinaryPath(LiveHub.RuntimeDir);
            Measure(new DirectoryInfo(LiveHub.RuntimeDir), 0, sizes, ref count, ref partial, clock);
        }
        catch (Exception) { partial = true; }
        string[] names = { "其它/状态", "日志", "profile", "凭据", "临时缓存" };
        StringBuilder text = new StringBuilder(); long total = 0;
        foreach (long value in sizes) total += value;
        text.Append("runtime 占用：").Append((total / 1048576.0).ToString("0.00")).Append(" MiB（").Append(count).Append(" 个文件）");
        if (partial) text.Append("；统计不完整，部分路径不可读/是重解析点/达到统计限额");
        for (int i = 0; i < sizes.Length; i++) text.Append("\r\n  ").Append(names[i]).Append(": ").Append((sizes[i] / 1048576.0).ToString("0.00")).Append(" MiB");
        return text.ToString();
    }

    private static void Measure(DirectoryInfo directory, int category, long[] sizes, ref int count,
        ref bool partial, System.Diagnostics.Stopwatch clock)
    {
        if (!directory.Exists) return;
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { partial = true; return; }
        try
        {
            foreach (FileInfo file in directory.GetFiles())
            {
                if (count >= 10000 || clock.ElapsedMilliseconds > 2000) { partial = true; return; }
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                int group = category;
                if (category == 0) group = file.Name.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0 ? 3
                    : LiveHub.RuntimeCacheName(file.Name) ? 4 : 0;
                sizes[group] += file.Length; count++;
            }
            foreach (DirectoryInfo child in directory.GetDirectories())
            {
                if (count >= 10000 || clock.ElapsedMilliseconds > 2000) { partial = true; return; }
                int group = category;
                if (category == 0) group = child.Name == "logs" ? 1
                    : child.Name.EndsWith("-profile", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                Measure(child, group, sizes, ref count, ref partial, clock);
            }
        }
        catch (Exception) { partial = true; }
    }

    // 只创建控件，不统计文件、不绑定清理动作；Show 和离线布局验收共用。
    internal static Form BuildDialog()
    {
        Form dialog = new DpiForm();
        dialog.SuspendLayout();
        dialog.Text = "本地数据与平台状态";
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.AutoScaleMode = AutoScaleMode.None;
        DpiLayout.SetDesignFont(dialog, ModernTheme.UiFont);
        dialog.ClientSize = new Size(DpiLayout.S(700), DpiLayout.S(420));
        Panel layout = new Panel { Name = "layout", Dock = DockStyle.Fill, Padding = new Padding(DpiLayout.S(12)) };
        FlowLayoutPanel actions = new FlowLayoutPanel { Name = "actions", Dock = DockStyle.Top,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, TabIndex = 0 };
        ComboBox platforms = new ModernComboBox { Name = "platforms", DropDownStyle = ComboBoxStyle.DropDownList,
            Width = DpiLayout.S(180), Margin = new Padding(0, DpiLayout.S(4), DpiLayout.S(8), DpiLayout.S(8)) };
        platforms.Items.AddRange(new object[] { "B站", "虎牙", "斗鱼", "抖音" }); platforms.SelectedIndex = 0;
        Button logout = new Button { Name = "logout", Text = "退出登录并清除凭据" };
        Button clean = new Button { Name = "clean", Text = "清理缓存和日志" };
        Button check = new Button { Name = "checkLogin", Text = "检测当前平台" };
        Button checkAll = new Button { Name = "checkAllLogins", Text = "检测全部平台" };
        Button login = new Button { Name = "loginAgain", Text = "重新登录" };
        foreach (Button button in new Button[] { check, checkAll, login, logout, clean })
        {
            button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(0, DpiLayout.S(32));
            button.Padding = new Padding(DpiLayout.S(12), DpiLayout.S(5), DpiLayout.S(12), DpiLayout.S(5));
            button.Margin = new Padding(0, 0, DpiLayout.S(8), DpiLayout.S(8));
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
        }
        TextBox info = new TextBox { Name = "info", Dock = DockStyle.Fill, Margin = Padding.Empty,
            Multiline = true, ReadOnly = true, WordWrap = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, TabIndex = 1 };
        info.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(info); };
        actions.Controls.AddRange(new Control[] { platforms, check, checkAll, login, logout, clean });
        layout.Controls.Add(info); layout.Controls.Add(actions); dialog.Controls.Add(layout);
        // 最窄时操作可能分成三行。按真实字体的首选高度预留阅读区，避免把正文挤成零高度。
        int minWidth = DpiLayout.S(360), operationHeight = 0;
        foreach (Control control in actions.Controls)
        {
            Size preferred = control.GetPreferredSize(Size.Empty);
            minWidth = Math.Max(minWidth, preferred.Width + control.Margin.Horizontal + layout.Padding.Horizontal);
            operationHeight += Math.Max(preferred.Height, control.MinimumSize.Height) + control.Margin.Vertical;
        }
        int minHeight = Math.Max(DpiLayout.S(320), operationHeight + layout.Padding.Vertical + DpiLayout.S(160));
        dialog.MinimumSize = new Size(dialog.Width - dialog.ClientSize.Width + minWidth,
            dialog.Height - dialog.ClientSize.Height + minHeight);
        Action followTheme = delegate
        {
            dialog.BackColor = layout.BackColor = actions.BackColor = ModernTheme.Surface;
            dialog.ForeColor = info.ForeColor = platforms.ForeColor = ModernTheme.Text;
            info.BackColor = platforms.BackColor = ModernTheme.Card;
            foreach (Button button in new[] { check, checkAll, login, logout, clean })
            { button.ForeColor = ModernTheme.Text; button.BackColor = ModernTheme.Card; button.FlatAppearance.BorderColor = ModernTheme.Border; }
            ModernTheme.ApplyScrollbarTheme(info);
            if (dialog.IsHandleCreated) ModernTheme.ApplyWindowChrome(dialog);
        };
        followTheme(); ModernTheme.Changed += followTheme;
        dialog.Disposed += delegate { ModernTheme.Changed -= followTheme; };
        dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); };
        dialog.ResumeLayout(true);
        return dialog;
    }

    internal static void Show(IWin32Window owner)
    {
        Show(owner, "bili");
    }

    internal static void Show(IWin32Window owner, string selectedPlatform)
    {
        using (Form dialog = BuildDialog())
        {
            ComboBox platforms = (ComboBox)dialog.Controls.Find("platforms", true)[0];
            Button logout = (Button)dialog.Controls.Find("logout", true)[0];
            Button clean = (Button)dialog.Controls.Find("clean", true)[0];
            TextBox info = (TextBox)dialog.Controls.Find("info", true)[0];
            platforms.SelectedIndex = Math.Max(0, Array.IndexOf(LoginHealth.Platforms, selectedPlatform));
            Button check = (Button)dialog.Controls.Find("checkLogin", true)[0];
            Button checkAll = (Button)dialog.Controls.Find("checkAllLogins", true)[0];
            Button login = (Button)dialog.Controls.Find("loginAgain", true)[0];
            string usage = "正在统计本地缓存…";

            Action render = delegate
            {
                var text = new StringBuilder("Cookie / 登录状态（绿色仅表示最近 10 分钟内获得平台明确确认）\r\n\r\n");
                for (int i = 0; i < LoginHealth.Platforms.Length; i++)
                    text.Append(LoginHealth.Details(LoginHealth.Platforms[(platforms.SelectedIndex + i) % 4])).AppendLine();
                text.AppendLine("超时、拦截、网页验证或格式变化不会直接判为 Cookie 失效。会话 Cookie 无本地到期时间，须在线确认。")
                    .AppendLine("自动检查：启动后、有新凭据时，以及检测记录超过 10 分钟后；手动重试间隔至少 10 秒。")
                    .AppendLine().Append(PlatformHealth.Describe()).AppendLine()
                    .AppendLine("本地数据管理").AppendLine(usage).AppendLine("BUILD=" + BuildInfo.Text)
                    .AppendLine("日志：单文件 1 MiB，4 份归档；总量 32 MiB，最多保留 14 天。")
                    .AppendLine("清理缓存保留登录 profile、Cookie、配置、收藏和历史。")
                    .AppendLine("播放器：mpv = " + (LiveHub.CurrentToolPath("mpv") ?? "(未设置)") + "；PotPlayer = " + (LiveHub.CurrentToolPath("potplayer") ?? "(未设置)"))
                    .AppendLine("播放器路径改在主界面「播放器路径」那一行（点「选择程序…」，选一次就记住）。");
                if (info.Text != text.ToString())
                { int selection = info.SelectionStart; info.Text = text.ToString(); info.Select(Math.Min(selection, info.TextLength), 0); }
                check.Enabled = LoginHealth.Get(LoginHealth.Platforms[platforms.SelectedIndex]).State != "checking";
            };
            // 播放器路径已经搬到主界面，这里不再放按钮（避免两处入口说法不一致）

            int measurementVersion = 0;
            Action refresh = delegate
            {
                int version = Interlocked.Increment(ref measurementVersion);
                render();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string measured = Usage();
                    try
                    {
                        if (!dialog.IsDisposed && dialog.IsHandleCreated) dialog.BeginInvoke((MethodInvoker)delegate
                        {
                            if (!dialog.IsDisposed && version == Interlocked.CompareExchange(ref measurementVersion, 0, 0))
                            { usage = measured; render(); }
                        });
                    }
                    catch (InvalidOperationException ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
                });
            };
            check.Click += delegate { LoginHealth.RequestCheck(LoginHealth.Platforms[platforms.SelectedIndex], true); render(); };
            checkAll.Click += delegate { foreach (string p in LoginHealth.Platforms) LoginHealth.RequestCheck(p, true); render(); };
            login.Click += delegate
            {
                string p = LoginHealth.Platforms[platforms.SelectedIndex];
                if (p == "bili") LiveHub.ShowBiliLogin(dialog); else PlatformLogin.Show(dialog, p);
                LoginHealth.RequestCheck(p, true); render();
            };
            platforms.SelectedIndexChanged += delegate { render(); };
            logout.Click += delegate
            {
                if (MessageBox.Show(dialog, "将删除所选平台的 Cookie 和独立登录 profile。请先关闭该平台的登录窗口。继续？", "退出登录", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                string error = Logout(new string[] { "bili", "huya", "douyu", "douyin" }[platforms.SelectedIndex]);
                MessageBox.Show(dialog, error.Length == 0 ? "凭据已清除；已连接的播放/弹幕会话请手动关闭。" : "清除未完成：" + error);
                refresh();
            };
            clean.Click += delegate
            {
                if (MessageBox.Show(dialog, "将清除会话缓存和诊断日志；请先关闭播放会话。继续？", "清理缓存", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                string error = CleanCache();
                MessageBox.Show(dialog, error.Length == 0 ? "缓存和日志已清理。" : "清理未完成：" + error);
                refresh();
            };
            using (var timer = new System.Windows.Forms.Timer { Interval = 1500 })
            {
                timer.Tick += delegate { foreach (string p in LoginHealth.Platforms) LoginHealth.RequestCheck(p, false); render(); };
                dialog.Shown += delegate { refresh(); timer.Start(); };
                dialog.ShowDialog(owner); timer.Stop();
            }
        }
    }
}
