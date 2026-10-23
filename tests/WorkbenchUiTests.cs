using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static readonly string[] WorkbenchPageKeys = {
        "favorites", "recent", "lookup", "sessions", "players", "danmaku", "accounts", "data"
    };

    private static void RegisterWorkbenchUiCases()
    {
        TestRunner.Add("工作台", "侧栏逐页切换且播放栏只属于收藏", TestWorkbenchNavigation);
        TestRunner.Add("工作台", "完整快捷选项与最近双播放器入口", TestWorkbenchControls);
        TestRunner.Add("工作台", "账号与本地数据职责分离且构建信息可见", TestWorkbenchDataOwnership);
        TestRunner.Add("工作台", "最近打开取最新会话且同房间旧会话不重复", TestWorkbenchLatestSessionCard);
        TestRunner.Add("布局", "默认窗口宽扁且常用尺寸下收藏页无纵向长滚", TestWorkbenchDefaultWindowShape);
        TestRunner.Add("工作台", "收藏两排完整显示且右栏多条记录可独立滚动", TestWorkbenchFavoritesDensity);
    }

    private static string WorkbenchProbePathArguments(string directory)
    {
        return " --runtime=\"" + Path.Combine(directory, "runtime") + "\""
            + " --favorites=\"" + Path.Combine(directory, "favorites.json") + "\""
            + " --history=\"" + Path.Combine(directory, "history.json") + "\""
            + " --cookie=\"" + Path.Combine(directory, "bili-cookies.txt") + "\""
            + " --config=\"" + Path.Combine(directory, "path-config.json") + "\"";
    }

    // Control.Visible includes the invisible Form ancestor. Inspect only the local
    // Visible bit so the production tree can be tested without ever showing it.
    private static bool WorkbenchLocallyVisible(Control control)
    {
        MethodInfo state = typeof(Control).GetMethod("GetState", ReviewInstance, null, new[] { typeof(int) }, null);
        if (state == null) throw new InvalidOperationException("WinForms local visibility inspection unavailable");
        return (bool)state.Invoke(control, new object[] { 2 });
    }

    private static bool WorkbenchActive(Control control)
    {
        if (control == null) return false;
        for (Control current = control; current != null && !(current is Form); current = current.Parent)
            if (!WorkbenchLocallyVisible(current)) return false;
        return true;
    }

    private static List<Control> WorkbenchDescendants(Control root)
    {
        var controls = new List<Control>();
        foreach (Control child in root.Controls)
        {
            controls.Add(child);
            controls.AddRange(WorkbenchDescendants(child));
        }
        return controls;
    }

    private static Control WorkbenchFind(Control root, string name)
    {
        if (root.Name == name) return root;
        foreach (Control control in WorkbenchDescendants(root)) if (control.Name == name) return control;
        return null;
    }

    // 新版工作台的一部分按钮换成了 AntdUI.Button —— 它是自绘控件，**不是**
    // System.Windows.Forms.Button（但实现了 IButtonControl）。测试程序由 csc 编译时
    // **不引用 AntdUI.dll**（见 Build-Hardened.ps1 / tools/portable-src-build.ps1 的
    // 测试编译行），所以测试里既不能写 AntdUI 类型名，也不能用 `is Button`
    // 去卡具体类型：按"按钮语义"（IButtonControl）和控件名断言，AntdUI.Button 与
    // ModernButton（WinForms Button 子类）都能同时满足。
    private static bool WorkbenchIsButton(Control control)
    {
        return control != null && control is IButtonControl;
    }

    // 快捷选项可能是 ComboBox，也可能被换成 AntdUI.Select；两者都通过 Items
    // 暴露 IList 集合，所以这里只按集合契约读取，不绑定具体控件类型。
    private static IList WorkbenchOptions(Control control, string name)
    {
        Check(control != null, "工作台：保留快捷选项控件 " + name);
        PropertyInfo items = control.GetType().GetProperty("Items", ReviewInstance);
        Check(items != null, "工作台：快捷选项控件公开选项集合 " + name);
        IList options = items.GetValue(control, null) as IList;
        Check(options != null, "工作台：快捷选项集合可按列表读取 " + name);
        return options;
    }

    private static bool WorkbenchInside(Control control, string ancestorName)
    {
        for (Control current = control; current != null; current = current.Parent)
            if (current.Name == ancestorName) return true;
        return false;
    }

    private static Control WorkbenchField(Form form, string name)
    {
        Check(form.Tag != null, "工作台：窗体保留控件状态对象");
        FieldInfo field = form.Tag.GetType().GetField(name, ReviewInstance);
        Check(field != null, "工作台：原有状态字段仍可访问 " + name);
        return (Control)field.GetValue(form.Tag);
    }

    private static void WorkbenchShowPage(Form form, string key)
    {
        Check(form.Tag != null, "工作台：窗体绑定页面控制器");
        MethodInfo select = form.Tag.GetType().GetMethod("SelectWorkbenchPage", ReviewInstance);
        Check(select != null, "工作台：具有可重复的页面切换入口");
        select.Invoke(form.Tag, new object[] { key });
        form.PerformLayout();
    }

    private static void WithWorkbenchForm(int theme, Action<Form> body)
    {
        Exception failure = null;
        var worker = new Thread(delegate()
        {
            string directory = Path.Combine(Path.GetTempPath(), "livehub-workbench-" + Guid.NewGuid().ToString("N"));
            var fields = new Dictionary<FieldInfo, object>();
            try
            {
                Directory.CreateDirectory(directory);
                string runtime = Path.Combine(directory, "runtime"), data = Path.Combine(directory, "data");
                Directory.CreateDirectory(runtime); Directory.CreateDirectory(data);
                foreach (string name in new[] { "RuntimeDir", "Favorites", "History", "Cookie", "PathConfigFile", "Mpv", "PotPlayer" })
                {
                    FieldInfo field = hub.GetField(name, ReviewStatic);
                    if (field == null) continue;
                    fields.Add(field, field.GetValue(null));
                    string value = name == "RuntimeDir" ? runtime
                        : name == "Favorites" ? Path.Combine(data, "favorites.json")
                        : name == "History" ? Path.Combine(data, "history.json")
                        : name == "Cookie" ? Path.Combine(data, "bili-cookies.txt")
                        : name == "PathConfigFile" ? Path.Combine(data, "path-config.json")
                        : Path.Combine(directory, name + "-absent.exe");
                    field.SetValue(null, value);
                }
                File.WriteAllText(Path.Combine(data, "favorites.json"),
                    "[{\"name\":\"合成主播\",\"room\":\"123456\"},{\"name\":\"合成跨平台主播\",\"room\":\"huya:456789\"}]", Encoding.UTF8);
                File.WriteAllText(Path.Combine(data, "history.json"),
                    "{\"bili\":[{\"Room\":\"123456\",\"Name\":\"合成主播\",\"At\":\"2000-01-01 12:00\"}]}", Encoding.UTF8);
                File.WriteAllText(Path.Combine(data, "path-config.json"), "{}", Encoding.UTF8);
                MethodInfo build = hub.GetMethod("BuildLauncherForm", ReviewStatic, null, new[] { typeof(int) }, null);
                using (Form form = (Form)build.Invoke(null, new object[] { theme }))
                {
                    FieldInfo fit = form.GetType().GetField("FitWorkingArea", ReviewInstance);
                    if (fit != null) fit.SetValue(form, false);
                    body(form);
                    Check(!form.Visible, "工作台夹具没有显示窗口或占用前台");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var pair in fields) pair.Key.SetValue(null, pair.Value);
                string absolute = Path.GetFullPath(directory), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(absolute).StartsWith("livehub-workbench-", StringComparison.Ordinal)
                    && Directory.Exists(absolute))
                    try { Directory.Delete(absolute, true); } catch (IOException) { }
            }
        });
        worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!worker.Join(45000)) throw new Exception("工作台隐藏控件夹具超时");
        if (failure != null) throw failure;
    }

    // 把布局强制跑到底：自定义分页容器（DanmakuSettingsPages）里的滚动区/卡片要等一次
    // 深层 PerformLayout 才有尺寸，未落座的容器 ClientSize=0 会被误判成越界。
    private static void WorkbenchForceLayout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) WorkbenchForceLayout(child);
    }

    private static void WorkbenchCheckViewport(Control root, int dpi)
    {
        if (root is Form) WorkbenchForceLayout(root);   // 每次顶层检查先把整棵树排到稳定
        ScrollableControl scroll = root as ScrollableControl;
        if (scroll != null && scroll.AutoScroll) scroll.AutoScrollPosition = Point.Empty;
        foreach (Control child in root.Controls)
        {
            if (!WorkbenchActive(child)) continue;
            int maxHeight = scroll == null ? root.ClientSize.Height
                : Math.Max(root.ClientSize.Height, Math.Max(scroll.AutoScrollMinSize.Height, scroll.DisplayRectangle.Height));
            Check(child.Left >= -1 && child.Right <= root.ClientSize.Width + 2,
                "工作台横向可达 DPI=" + dpi + " " + child.Name + " " + child.Bounds + " parent=" + root.Name + " client=" + root.ClientSize);
            Check(child.Top >= -1 && child.Bottom <= maxHeight + 2,
                "工作台纵向可见或可滚动到达 DPI=" + dpi + " " + child.Name + " " + child.Bounds + " max=" + maxHeight);
            WorkbenchCheckViewport(child, dpi);
        }
    }

    private static void TestWorkbenchNavigation()
    {
        WithWorkbenchForm(-1, delegate(Form form)
        {
            Control launch = WorkbenchFind(form, "workbenchLaunch");
            Check(launch != null, "工作台：保留独立播放区");
            int navigationEntries = 0;
            foreach (Control control in WorkbenchDescendants(form))
                if (control.Name != null && control.Name.StartsWith("workbenchNav_", StringComparison.Ordinal)) navigationEntries++;
            Check(navigationEntries == WorkbenchPageKeys.Length,
                "工作台：侧栏入口恰好八个（实际 " + navigationEntries + "）");
            foreach (string key in WorkbenchPageKeys)
            {
                Check(WorkbenchFind(form, "workbenchNav_" + key) != null, "工作台：侧栏入口齐全 " + key);
                WorkbenchShowPage(form, key);
                foreach (string page in WorkbenchPageKeys)
                {
                    Control panel = WorkbenchFind(form, "workbenchPage_" + page);
                    Check(panel != null, "工作台：页面控件存在 " + page);
                    Check(WorkbenchActive(panel) == (page == key), "工作台：仅所选页面显示 " + key + "/" + page);
                }
                Check(WorkbenchActive(launch) == (key == "favorites"), "工作台：播放区仅收藏页显示 " + key);
                Control expected = key == "favorites" ? WorkbenchField(form, "openMpv")
                    : key == "recent" ? WorkbenchFind(form, "recentOpenMpv")
                    : key == "lookup" ? WorkbenchFind(form, "lookupSearch") : null;
                if (expected == null) Check(form.AcceptButton == null,
                    "工作台：回车只执行当前页明确的播放或查询操作 " + key);
                else
                {
                    Check(WorkbenchIsButton(expected), "工作台：回车落地在按钮控件上 " + key);
                    Check(ReferenceEquals(form.AcceptButton, expected),
                        "工作台：回车只执行当前页明确的播放或查询操作 " + key);
                }
            }
            Check(WorkbenchFind(form, "workbenchNav_map") == null, "工作台：不包含已取消的迁移说明入口");
        });
    }

    private static void TestWorkbenchControls()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            foreach (var pair in new Dictionary<string, int> { { "qualityBox", 3 }, { "bufferBox", 2 }, { "danmakuBox", 3 }, { "scBox", 4 }, { "enhanceBox", 3 } })
            {
                IList options = WorkbenchOptions(WorkbenchField(form, pair.Key), pair.Key);
                Check(options.Count == pair.Value, "工作台：原设置选项完整 " + pair.Key);
            }
            bool badgeMode = false;
            foreach (object option in WorkbenchOptions(WorkbenchField(form, "scBox"), "scBox"))
                if (Convert.ToString(option) == "只留角标") badgeMode = true;
            Check(badgeMode, "工作台：SC 角标模式仍可选择");
            WorkbenchShowPage(form, "recent");
            foreach (var pair in new Dictionary<string, string> { { "recentOpenMpv", "mpv" }, { "recentOpenPot", "PotPlayer" } })
            {
                Control button = WorkbenchFind(form, pair.Key);
                Check(WorkbenchIsButton(button) && WorkbenchActive(button)
                    && button.Text.IndexOf(pair.Value, StringComparison.Ordinal) >= 0,
                    "工作台：最近播放有明确播放器入口 " + pair.Key);
            }
            Control statusCancel = WorkbenchFind(form, "workbenchStatusCancel");
            Check(WorkbenchIsButton(statusCancel) && statusCancel.Text == "取消启动"
                && WorkbenchInside(statusCancel, "workbenchFooter"),
                "工作台：非收藏页也保留页内取消启动入口");
            WorkbenchShowPage(form, "sessions");
            Control sessionControl = WorkbenchFind(form, "workbenchSessions");
            Check(sessionControl != null && WorkbenchInside(sessionControl, "workbenchPage_sessions"),
                "工作台：观看会话页承载唯一会话表");
            ListView sessions = sessionControl as ListView;
            Check(sessions != null && sessions.Columns.Count == 5, "工作台：会话表仍有五个真实字段");
            int used = 0; foreach (ColumnHeader column in sessions.Columns) { Check(column.Text.Length > 0, "会话字段有标题"); used += column.Width; }
            Check(Math.Abs(used - sessions.ClientSize.Width) <= 6, "工作台：会话列填满可用视口，无虚构空列");
        });
    }

    private static void TestWorkbenchDataOwnership()
    {
        WithWorkbenchForm(-1, delegate(Form form)
        {
            WorkbenchShowPage(form, "data");
            Control data = WorkbenchFind(form, "workbenchPage_data");
            string text = "";
            foreach (Control control in WorkbenchDescendants(data)) text += "\n" + control.Text;
            Check(text.IndexOf("Cookie", StringComparison.OrdinalIgnoreCase) < 0,
                "工作台：本地数据和运行诊断不重复展示 Cookie 内容");
            Check(WorkbenchFind(data, "workbenchCheckAllCookies") == null
                && WorkbenchFind(data, "workbenchLogin_bili") == null,
                "工作台：本地数据页不重复账号登录或检测入口");
            string build = (string)DanmakuType("BuildInfo").GetProperty("Text", ReviewStatic).GetValue(null, null);
            // 期望值直接由生产格式化器（WorkbenchDataUi.BuildVersion/BuildTime）算出来，
            // 避免测试和界面各写一套解析 —— 否则界面显示错值也可能通过。
            Type dataUi = WorkbenchDataType();
            string expectedVersion = (string)ReviewCall(dataUi, "BuildVersion", build);
            string expectedTime = (string)ReviewCall(dataUi, "BuildTime", build);
            Control buildGroup = WorkbenchFind(data, "workbenchBuildInfo");
            Control buildVersion = WorkbenchFind(data, "workbenchBuildVersion");
            Control buildTime = WorkbenchFind(data, "workbenchBuildTime");
            Check(buildGroup != null && buildVersion != null && buildTime != null,
                "工作台：本地数据提供版本和时间展示区");
            Check(expectedVersion.Length > 0 && buildVersion.Text.Contains(expectedVersion),
                "工作台：版本值来自实际构建标识");
            Check(expectedTime.Length > 0 && buildTime.Text == "构建来源时间（UTC）：" + expectedTime,
                "工作台：构建时间按 UTC 来自实际构建标识（" + expectedTime + "）");
            WorkbenchShowPage(form, "accounts");
            Control accounts = WorkbenchFind(form, "workbenchPage_accounts");
            string accountText = "";
            foreach (Control control in WorkbenchDescendants(accounts)) accountText += "\n" + control.Text;
            Check(accountText.Contains("Cookie") && accountText.Contains("检测"), "工作台：账号页集中登录状态检测");
            // 账号页的登录/检测按钮现在是 AntdUI.Button（不是 WinForms Button），
            // 但按控件名与按钮语义断言，换成任何实现 IButtonControl 的按钮都仍被覆盖。
            foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
            {
                Control login = WorkbenchFind(accounts, "workbenchLogin_" + platform);
                Check(WorkbenchIsButton(login) && login.Text.IndexOf("扫码", StringComparison.Ordinal) >= 0,
                    "工作台：账号页保留平台登录按钮 " + platform);
                Check(WorkbenchIsButton(WorkbenchFind(accounts, "workbenchCheckCookie_" + platform)),
                    "工作台：账号页保留平台检测按钮 " + platform);
            }
            Check(WorkbenchIsButton(WorkbenchFind(accounts, "workbenchCheckAllCookies")),
                "工作台：账号页保留全部平台检测入口");
        });
    }

    private static object NewWorkbenchSession(Type sessionType, object target, string player, bool ended)
    {
        object session = Activator.CreateInstance(sessionType, true);
        sessionType.GetField("Target").SetValue(session, target);
        sessionType.GetField("Player").SetValue(session, player);
        sessionType.GetField("Ended").SetValue(session, ended);
        return session;
    }

    /// <summary>
    /// 首页「正在观看 / 最近打开」必须显示**最近一次**会话（sessions[0]），并且同一
    /// 房间先后用不同播放器打开时不能一直堆旧行：已结束的旧会话行在新会话出现后清掉，
    /// 旧播放器还在跑的会话保留。用合成会话对象离线断言，不启动任何播放器。
    /// </summary>
    private static void TestWorkbenchLatestSessionCard()
    {
        WithWorkbenchForm(-1, delegate(Form form)
        {
            Type launcherUi = form.Tag.GetType();
            IList sessions = (IList)launcherUi.GetField("sessions", ReviewInstance).GetValue(form.Tag);
            Type sessionType = hub.GetNestedType("PlaybackSession", BindingFlags.Public | BindingFlags.NonPublic);
            Check(sessionType != null, "工作台：会话行类型可用反射构造合成夹具");
            object bili700001 = Call("Parse", "bili:700001", "bili");
            object bili700002 = Call("Parse", "bili:700002", "bili");
            object bili700003 = Call("Parse", "bili:700003", "bili");
            Check(bili700001 != null && bili700002 != null && bili700003 != null, "工作台：合成会话目标可解析");
            MethodInfo refresh = launcherUi.GetMethod("RefreshWorkbenchHomeSession", ReviewInstance);
            Check(refresh != null, "工作台：首页会话卡片有可重复刷新入口");
            Control title = (Control)launcherUi.GetField("workbenchHomeTitle", ReviewInstance).GetValue(form.Tag);
            Control meta = (Control)launcherUi.GetField("workbenchHomeMeta", ReviewInstance).GetValue(form.Tag);

            // 合成夹具：同一房间先用 mpv、再用 PotPlayer 打开，应显示最新会话。
            sessions.Clear();
            sessions.Add(NewWorkbenchSession(sessionType, bili700001, "mpv", true));
            sessions.Insert(0, NewWorkbenchSession(sessionType, bili700001, "potplayer", false));
            refresh.Invoke(form.Tag, null);
            Check(title.Text == "B站 700001", "工作台：首页显示最近一次会话（" + title.Text + "）");
            Check(meta.Text.StartsWith("PotPlayer", StringComparison.Ordinal),
                "工作台：首页显示最近一次会话的播放器（" + meta.Text + "）");

            MethodInfo prune = hub.GetMethod("PruneEndedDuplicateSessions", Flags);
            Check(prune != null, "工作台：同房间旧会话清理是可单独断言的纯函数");
            sessions.Clear();
            sessions.Add(NewWorkbenchSession(sessionType, bili700001, "potplayer", false));   // 活跃，保留
            sessions.Add(NewWorkbenchSession(sessionType, bili700001, "mpv", true));          // 旧的已结束，清掉
            sessions.Add(NewWorkbenchSession(sessionType, bili700002, "mpv", false));        // 活跃，保留
            sessions.Add(NewWorkbenchSession(sessionType, bili700002, "mpv", true));         // 旧的已结束，清掉
            sessions.Add(NewWorkbenchSession(sessionType, bili700003, "mpv", true));         // 唯一一条，保留
            int removed = (int)prune.Invoke(null, new object[] { sessions });
            Check(removed == 2 && sessions.Count == 3, "工作台：同房间已结束的旧会话行被清理（移除 " + removed + " 条）");
            Check((string)sessionType.GetField("Player").GetValue(sessions[0]) == "potplayer"
                && (string)sessionType.GetField("Player").GetValue(sessions[2]) == "mpv",
                "工作台：清理后保留活跃会话与没有更新记录的结束会话");
            refresh.Invoke(form.Tag, null);
            Check(title.Text == "B站 700001", "工作台：清理后首页仍指向最近一次会话");
            sessions.Clear();
        });
    }

    /// <summary>
    /// 两排收藏与右栏使用合成数据、隐藏窗口验证，不触碰个人数据或前台。
    /// </summary>
    private static void TestWorkbenchFavoritesDensity()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            Type ui = form.Tag.GetType();
            Control browser = WorkbenchFind(form, "favoritesBrowser");
            IList items = (IList)browser.GetType().GetProperty("Items").GetValue(browser, null);
            Type roomType = hub.Assembly.GetType("WorkbenchRoomItem");
            items.Clear();
            for (int i = 0; i < 6; i++)
            {
                object item = Activator.CreateInstance(roomType, true);
                roomType.GetField("Name", ReviewInstance).SetValue(item, "合成主播 " + (i + 1));
                roomType.GetField("Meta", ReviewInstance).SetValue(item, "B站 · " + (100001 + i));
                roomType.GetField("State", ReviewInstance).SetValue(item, "直播中");
                roomType.GetField("Live", ReviewInstance).SetValue(item, true);
                items.Add(item);
            }
            IList sessions = (IList)ui.GetField("sessions", ReviewInstance).GetValue(form.Tag);
            Type sessionType = hub.GetNestedType("PlaybackSession", ReviewStatic);
            for (int i = 0; i < 8; i++)
                sessions.Add(NewWorkbenchSession(sessionType, Call("Parse", "bili:" + (200001 + i), "bili"), i % 2 == 0 ? "mpv" : "potplayer", i > 1));
            ui.GetMethod("RefreshWorkbenchHomeSession", ReviewInstance).Invoke(form.Tag, null);
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { dpi });
                form.ClientSize = new Size(1240 * dpi / 96, 720 * dpi / 96);
                WorkbenchShowPage(form, "favorites"); WorkbenchForceLayout(form);
                ScrollableControl cards = (ScrollableControl)browser;
                Control hint = WorkbenchFind(form, "workbenchFavoritesHint");
                Control page = WorkbenchFind(form, "workbenchPage_favorites");
                Check(cards.AutoScrollMinSize.Height <= cards.ClientSize.Height,
                    "收藏六张卡片不裁切且无内部滚动 DPI=" + dpi + " content=" + cards.AutoScrollMinSize + " viewport=" + cards.ClientSize + " page=" + page.ClientSize + " top=" + cards.Top);
                Check(hint.Top >= cards.Bottom && hint.Bottom <= page.ClientSize.Height,
                    "收藏下方操作说明完整可见 DPI=" + dpi);
                ScrollableControl list = (ScrollableControl)WorkbenchFind(form, "workbenchHomeList");
                Check(list.Left > cards.Left && list.Height >= cards.Height - 30 * dpi / 96,
                    "右栏使用收藏区整列高度 DPI=" + dpi);
                Check(list.AutoScrollMinSize.Height > list.ClientSize.Height && list.Controls.Count >= 8,
                    "多条会话在右栏独立滚动 DPI=" + dpi);
                Control first = WorkbenchFind(form, "workbenchHomeSession");
                Control second = WorkbenchFind(form, "workbenchHomeSession1");
                Check(second.Top >= first.Bottom && WorkbenchFind(form, "workbenchHomeReopen1").Enabled,
                    "右栏会话不重叠且各自可重开 DPI=" + dpi);
                string preview = Environment.GetEnvironmentVariable("LIVEHUB_LAYOUT_PREVIEW");
                if (!string.IsNullOrEmpty(preview) && dpi == 96)
                {
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(preview);
                    }
                }
            }
            sessions.Clear();
            ui.GetMethod("RefreshWorkbenchHomeSession", ReviewInstance).Invoke(form.Tag, null);
            Check(WorkbenchFind(form, "workbenchHomeTitle").Text == "合成主播"
                && WorkbenchFind(form, "workbenchHomeReopen").Enabled,
                "重启后无会话时右栏仍显示持久化最近播放");
        });
    }

    /// <summary>默认宽窗口和窄窗口均保持内容可达。</summary>
    private static void TestWorkbenchDefaultWindowShape()
    {
        Size design = (Size)Call("WorkbenchDesignSize", 1280, 752);
        Check(design.Width == 1240 && design.Height == 720,
            "布局：1280×752 工作区得到 1240×720 的宽扁默认尺寸（" + design.Width + "×" + design.Height + "）");
        Check(design.Width * 100 >= design.Height * 150,
            "布局：默认窗口至少 3:2，不再回到 4:3 竖长（" + design.Width + "×" + design.Height + "）");
        Size small = (Size)Call("WorkbenchDesignSize", 1024, 560);
        Check(small.Width <= 1024 && small.Height <= 560 && small.Height >= 520,
            "布局：小工作区默认窗口装得下（" + small.Width + "×" + small.Height + "）");
        Size big = (Size)Call("WorkbenchDesignSize", 2560, 1400);
        Check(big.Width == 1400 && big.Height <= 880,
            "布局：大工作区默认窗口有上限（" + big.Width + "×" + big.Height + "）");

        WithWorkbenchForm(-1, delegate(Form form)
        {
            Check(form.ClientSize.Width > form.ClientSize.Height,
                "布局：默认客户区是宽扁的（" + form.ClientSize + "）");
            Check(form.MinimumSize.Width <= form.ClientSize.Width && form.MinimumSize.Height <= form.ClientSize.Height,
                "布局：最小尺寸不超过默认尺寸");
            foreach (Size logical in new Size[] { new Size(1240, 720), new Size(1024, 800) })
            {
                form.ClientSize = logical; form.PerformLayout();
                foreach (string key in WorkbenchPageKeys)
                {
                    WorkbenchShowPage(form, key);
                    WorkbenchCheckViewport(form, 96);
                }
                WorkbenchShowPage(form, "favorites");
                ScrollableControl page = WorkbenchFind(form, "workbenchPage_favorites") as ScrollableControl;
                Check(page != null && page.AutoScrollMinSize.Height <= page.ClientSize.Height,
                    "布局：收藏页在 " + logical.Width + "×" + logical.Height + " 下不用纵向长滚（min="
                    + (page == null ? "null" : page.AutoScrollMinSize.ToString())
                    + " client=" + (page == null ? "null" : page.ClientSize.ToString()) + "）");
            }
        });
    }
}
