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

    private static void WorkbenchCheckViewport(Control root, int dpi)
    {
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
}
