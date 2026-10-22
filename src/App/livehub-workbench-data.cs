using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AButton = AntdUI.Button;
using ASelect = AntdUI.Select;
using ATag = AntdUI.Tag;
using TTypeMini = AntdUI.TTypeMini;

internal static partial class LiveHub
{
    // Native workbench pages. These reuse the existing login and data services;
    // no browser, UI package or network client is introduced by the presentation.
    internal static class WorkbenchDataUi
    {
        internal sealed class DataCategory
        {
            internal string Id, Name, Location, Note;
            internal string[] Headers;
            internal readonly List<string[]> Rows = new List<string[]>();
            internal readonly List<string> Files = new List<string>();
            internal long Bytes;
            internal DateTime ModifiedUtc;
        }

        internal sealed class DataSnapshot
        {
            internal readonly List<DataCategory> Categories = new List<DataCategory>();
            internal string Warning = "";
        }

        private const int MaximumJsonBytes = 2 * 1024 * 1024;
        private const int MaximumFiles = 4000;
        private const int MaximumRows = 500;
        private static readonly string[] CategoryIds = { "favorites", "history", "config", "cache", "logs" };
        private static readonly string[] CategoryNames = { "收藏", "播放历史", "配置", "缓存", "日志" };

        private sealed class Page : Panel
        {
            private readonly Action theme;
            internal Page(string name)
            {
                Name = name; Dock = DockStyle.Fill; AutoScroll = true;
                Padding = new Padding(0); Margin = Padding.Empty;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                theme = delegate { BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Text; Invalidate(true); };
                theme(); ModernTheme.Changed += theme;
                HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(this); };
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) ModernTheme.Changed -= theme;
                base.Dispose(disposing);
            }
        }

        private static ModernLabel Label(string text, bool heading)
        {
            ModernLabel label = new ModernLabel { Text = text, AutoSize = true,
                Role = heading ? ModernLabel.RoleKind.Normal : ModernLabel.RoleKind.Secondary,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 8) };
            if (heading) DpiLayout.SetDesignFont(label, ModernTheme.UiFontBold);
            return label;
        }

        private static ModernButton Button(string text, string name, EventHandler action)
        {
            ModernButton button = new ModernButton { Text = text, Name = name,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(76, 32), Padding = new Padding(9, 3, 9, 3),
                Margin = new Padding(0, 0, 8, 8) };
            if (action != null) button.Click += action;
            return button;
        }

        private static AButton AntdButton(string text, string name, EventHandler action)
        {
            AButton button = new AButton { Name = name, Text = text, AutoSize = true,
                Height = 32, Radius = 7, Margin = new Padding(0, 0, 8, 8),
                Font = new Font(ModernTheme.UiFont.FontFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel) };
            if (action != null) button.Click += action;
            return button;
        }

        private static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, Dock = DockStyle.Top, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        private static TableLayoutPanel Stack()
        {
            TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Top,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 0, Padding = Padding.Empty, Margin = Padding.Empty };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }

        private static void Add(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Top;
            table.Controls.Add(control, 0, row);
            Label label = control as Label;
            if (label != null)
            {
                Action wrap = delegate
                {
                    int width = Math.Max(80, table.ClientSize.Width - table.Padding.Horizontal - label.Margin.Horizontal);
                    if (label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
                };
                table.SizeChanged += delegate { wrap(); }; wrap();
            }
        }

        private static Panel Card(Control content)
        {
            Panel panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 14) };
            Action theme = delegate { panel.BackColor = ModernTheme.Card; panel.ForeColor = ModernTheme.Text; panel.Invalidate(); };
            theme(); ModernTheme.Changed += theme;
            panel.Disposed += delegate { ModernTheme.Changed -= theme; };
            Action round = delegate
            {
                if (panel.Width < 3 || panel.Height < 3) return;
                using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, panel.Width - 1, panel.Height - 1), 10))
                {
                    Region region = new Region(path);
                    Region old = panel.Region; panel.Region = region; if (old != null) old.Dispose();
                }
            };
            panel.SizeChanged += delegate { round(); };
            panel.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Math.Max(1, panel.Width - 1), Math.Max(1, panel.Height - 1)), 10))
                using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
                using (Pen pen = new Pen(ModernTheme.Border))
                {
                    e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
                }
            };
            panel.Controls.Add(content); round(); return panel;
        }

        private static ModernListView List(string name, string[] headers, int height)
        {
            ModernListView list = new ModernListView { Name = name, View = View.Details, FullRowSelect = true,
                MultiSelect = false, HideSelection = false, Dock = DockStyle.Top, Height = height,
                Margin = new Padding(0, 4, 0, 10) };
            foreach (string header in headers) list.Columns.Add(header, 130);
            list.ClientSizeChanged += delegate { FitColumns(list); };
            return list;
        }

        private static void FitColumns(ListView list)
        {
            if (list.Columns.Count == 0 || list.ClientSize.Width <= 0) return;
            int width = Math.Max(1, list.ClientSize.Width - 2);
            int minimum = DpiLayout.S(list, 56), used = 0;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                int value = i == list.Columns.Count - 1 ? width - used : width / list.Columns.Count;
                list.Columns[i].Width = Math.Max(minimum, value); used += list.Columns[i].Width;
            }
        }

        private static void SetRows(ListView list, IList<string[]> rows)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (string[] row in rows)
                {
                    if (row.Length == 0) continue;
                    ListViewItem item = new ListViewItem(row); list.Items.Add(item);
                }
            }
            finally { list.EndUpdate(); }
            FitColumns(list);
        }

        internal static Control BuildAccounts(Form owner)
        {
            Page page = new Page("workbenchAccounts");
            TableLayoutPanel body = Stack(); page.Controls.Add(body);
            FlowLayoutPanel toolbar = Flow();
            Label accountsTitle = Label("四平台账号", true);
            accountsTitle.AutoSize = false;
            accountsTitle.Width = DpiLayout.S(owner, 120);
            accountsTitle.Height = DpiLayout.S(owner, 32);
            accountsTitle.TextAlign = ContentAlignment.MiddleLeft;
            accountsTitle.Margin = new Padding(0, 0, DpiLayout.S(owner, 8), 0);
            toolbar.Controls.Add(accountsTitle);
            toolbar.Controls.Add(Button("检测全部平台", "workbenchCheckAllCookies", delegate
            {
                foreach (string platform in LoginHealth.Platforms) LoginHealth.RequestCheck(platform, true);
            }));
            Add(body, toolbar);
            Dictionary<string, ModernLabel> summaries = new Dictionary<string, ModernLabel>();
            Dictionary<string, ModernLabel> reasons = new Dictionary<string, ModernLabel>();
            Dictionary<string, AButton> checks = new Dictionary<string, AButton>();
            Action refresh = delegate
            {
                foreach (string p in LoginHealth.Platforms)
                {
                    LoginHealth.Result result = LoginHealth.Get(p);
                    summaries[p].Text = PlatformName(p) + " Cookie：" + LoginHealth.Title(result)
                        + "\r\n最近检测：" + LoginHealth.TimeText(result.CheckedUtc);
                    summaries[p].ForeColor = LoginHealth.ColorFor(result);
                    reasons[p].Text = result.Reason ?? "";
                    checks[p].Enabled = result.State != "checking";
                }
            };
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            int slot = 0;
            foreach (string platform in LoginHealth.Platforms)
            {
                string selected = platform;
                TableLayoutPanel group = Stack();
                TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
                top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                ModernLabel title = Label(PlatformName(selected), true);
                DpiLayout.SetDesignFont(title, new Font(ModernTheme.UiFont.FontFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel));
                top.Controls.Add(title, 0, 0);
                ATag tag = new ATag { Text = "Cookie 状态", AutoSize = false, Size = new Size(DpiLayout.S(owner, 84), DpiLayout.S(owner, 24)), Margin = new Padding(0, 2, 0, 0) };
                top.Controls.Add(tag, 1, 0);
                Add(group, top);
                ModernLabel summary = Label("尚未读取登录状态", false);
                summary.Name = "workbenchAccountStatus_" + selected;
                summary.MaximumSize = new Size(DpiLayout.S(owner, 400), 0);
                DpiLayout.SetDesignFont(summary, new Font(ModernTheme.UiFont.FontFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel));
                summaries[selected] = summary; Add(group, summary);
                ModernLabel reason = Label("", false);
                reason.MaximumSize = new Size(DpiLayout.S(owner, 400), 0);
                DpiLayout.SetDesignFont(reason, new Font(ModernTheme.UiFont.FontFamily, 11f, FontStyle.Regular, GraphicsUnit.Pixel));
                reasons[selected] = reason; Add(group, reason);
                TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                    ColumnCount = 4, Margin = new Padding(0, 4, 0, 0) };
                for (int column = 0; column < 4; column++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                Action<AButton> actionButton = delegate(AButton button)
                {
                    button.AutoSize = false; button.Dock = DockStyle.Fill; button.Ghost = true;
                    button.Margin = new Padding(0, 0, 6, 0);
                };
                AButton login = AntdButton(PlatformName(selected) + "扫码", "workbenchLogin_" + selected,
                    delegate { Login(owner, selected); refresh(); });
                actionButton(login);
                checks[selected] = AntdButton("检测 Cookie", "workbenchCheckCookie_" + selected,
                    delegate { LoginHealth.RequestCheck(selected, true); refresh(); });
                actionButton(checks[selected]);
                AButton details = AntdButton("状态详情", "workbenchAccountDetails_" + selected,
                    delegate { ShowAccountDetails(owner, selected); refresh(); });
                actionButton(details);
                AButton logout = AntdButton("退出登录", "workbenchLogout_" + selected,
                    delegate { LogoutAccount(owner, selected); refresh(); });
                actionButton(logout); logout.ForeColor = Color.FromArgb(0xEF, 0x9A, 0xAB);
                actions.Controls.Add(login, 0, 0);
                actions.Controls.Add(checks[selected], 1, 0);
                actions.Controls.Add(details, 2, 0);
                actions.Controls.Add(logout, 3, 0);
                Add(group, actions);
                Panel card = Card(group);
                card.Dock = DockStyle.Fill;
                card.Margin = new Padding(slot % 2 == 0 ? 0 : 6, 0, slot % 2 == 0 ? 6 : 0, 12);
                if (slot % 2 == 0) { grid.RowCount = slot / 2 + 1; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
                grid.Controls.Add(card, slot % 2, slot / 2);
                slot++;
            }
            Add(body, grid);
            Add(body, Label("检测超时、网页核验或格式变化不会直接判定登录失效。", false));
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += delegate { if (!owner.IsDisposed && owner.Visible && page.Visible) refresh(); };
            EventHandler shown = delegate { if (page.Visible) refresh(); timer.Start(); };
            owner.Shown += shown;
            page.VisibleChanged += delegate { if (page.Visible && owner.Visible) refresh(); };
            page.Disposed += delegate { owner.Shown -= shown; timer.Dispose(); };
            return page;
        }

        private static void Login(Form owner, string platform)
        {
            if (platform == "bili") ShowBiliLogin(owner); else PlatformLogin.Show(owner, platform);
            LoginHealth.RequestCheck(platform, true);
        }

        private static void LogoutAccount(Form owner, string platform)
        {
            if (MessageBox.Show(owner, "退出 " + PlatformName(platform)
                + " 并清除该平台 Cookie 和独立登录 profile？请先关闭该平台登录窗口。", "退出登录",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            string error = LocalData.Logout(platform);
            MessageBox.Show(owner, error.Length == 0 ? "已退出；现有播放/弹幕会话请手动关闭。" : "清除未完成：" + error,
                "退出登录", MessageBoxButtons.OK, error.Length == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        internal static Form BuildAccountDetails(string platform)
        {
                Form dialog = Dialog(PlatformName(platform) + " · Cookie 登录状态", 620, 340);
                TableLayoutPanel stack = Stack(); stack.Dock = DockStyle.Fill; dialog.Controls.Add(stack);
                ModernLabel text = Label("", false); text.Name = "workbenchCookieDetails";
                Action refresh = delegate { text.Text = LoginHealth.Details(platform); };
                Add(stack, text);
                FlowLayoutPanel actions = Flow();
                actions.Controls.Add(Button("检测 Cookie", "checkCookie", delegate { LoginHealth.RequestCheck(platform, true); refresh(); }));
                actions.Controls.Add(Button("重新登录", "loginAgain", delegate { Login(dialog, platform); refresh(); }));
                actions.Controls.Add(Button("退出登录", "logout", delegate { LogoutAccount(dialog, platform); refresh(); }));
                actions.Controls.Add(Button("关闭", "close", delegate { dialog.Close(); }));
                Add(stack, actions);
                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += delegate { refresh(); }; dialog.Shown += delegate { refresh(); timer.Start(); };
                dialog.Disposed += delegate { timer.Dispose(); };
                refresh();
                return dialog;
        }

        private static void ShowAccountDetails(Form owner, string platform)
        {
            using (Form dialog = BuildAccountDetails(platform))
            {
                DpiLayout.ScaleTree(dialog); dialog.ShowDialog(owner);
            }
        }

        internal static Control BuildData(Form owner)
        {
            Page page = new Page("workbenchData");
            Panel body = new Panel { Name = "workbenchDataBody", Dock = DockStyle.Top, AutoScroll = false, Height = 700, BackColor = Color.Transparent };
            page.Controls.Add(body);

            FlowLayoutPanel tabs = Flow();
            AButton localTab = AntdButton("本地数据", "workbenchLocalTab", null);
            AButton diagnosticTab = AntdButton("平台运行状态", "workbenchDiagnosticTab", null);
            diagnosticTab.Ghost = true;
            tabs.Dock = DockStyle.None; tabs.AutoSize = false; tabs.Height = DpiLayout.S(owner, 36);
            tabs.Controls.Add(localTab); tabs.Controls.Add(diagnosticTab); body.Controls.Add(tabs);

            TableLayoutPanel localBody = Stack();
            localBody.AutoSize = false; localBody.Dock = DockStyle.Fill;
            Panel localCard = Card(localBody); localCard.Name = "workbenchLocalCard";
            localCard.AutoSize = false; localCard.Dock = DockStyle.None; localCard.Height = DpiLayout.S(owner, 640);
            body.Controls.Add(localCard);
            TableLayoutPanel head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            TableLayoutPanel heading = Stack(); Add(heading, Label("本地数据", true));
            Add(heading, Label("收藏、历史、配置和运行文件", false));
            head.Controls.Add(heading, 0, 0);
            FlowLayoutPanel actions = Flow(); actions.WrapContents = false;
            AButton refreshButton = AntdButton("刷新列表", "workbenchRefreshData", null);
            AButton viewButton = AntdButton("查看本地数据", "workbenchViewData", null);
            viewButton.Type = TTypeMini.Primary;
            actions.Controls.Add(refreshButton); actions.Controls.Add(viewButton);
            head.Controls.Add(actions, 1, 0); Add(localBody, head);

            string[] glyphs = { "★", "↺", "≡", "▤", "⌘" };
            TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 5, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 0) };
            int[] tableWidths = { 19, 31, 12, 21, 17 };
            for (int i = 0; i < tableWidths.Length; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, tableWidths[i]));
            table.RowCount = 6; table.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            for (int i = 0; i < 5; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 37,
                ColumnCount = 5, BackColor = ModernTheme.Hover, Margin = new Padding(0, 0, 0, 1) };
            for (int i = 0; i < tableWidths.Length; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, tableWidths[i]));
            string[] headers = { "数据类别", "相对位置", "占用", "最近更新", "操作" };
            for (int i = 0; i < headers.Length; i++) { Label headCell = TableCell(headers[i], false); header.Controls.Add(headCell, i, 0); }
            table.Controls.Add(header, 0, 0); table.SetColumnSpan(header, 5);

            Label[] counts = new Label[5];
            Label[] paths = new Label[5];
            Label[] sizes = new Label[5];
            Label[] updated = new Label[5];
            for (int i = 0; i < 5; i++)
            {
                TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 52, AutoSize = false,
                    ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 0, 0, 0), BackColor = Color.Transparent };
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int c = 0; c < tableWidths.Length; c++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, tableWidths[c]));
                TableLayoutPanel name = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = false, BackColor = Color.Transparent };
                name.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
                name.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                Label title = TableCell(glyphs[i] + "  " + CategoryNames[i], true);
                counts[i] = TableCell("读取中…", false); counts[i].Name = "workbenchDataCount_" + CategoryIds[i];
                name.Controls.Add(title, 0, 0); name.Controls.Add(counts[i], 0, 1);
                row.Controls.Add(name, 0, 0);
                paths[i] = TableCell("—", false); paths[i].Name = "workbenchDataPath_" + CategoryIds[i]; row.Controls.Add(paths[i], 1, 0);
                sizes[i] = TableCell("—", false); row.Controls.Add(sizes[i], 2, 0);
                updated[i] = TableCell("—", false); row.Controls.Add(updated[i], 3, 0);
                string category = CategoryIds[i];
                AButton detail = AntdButton("查看明细", "workbenchDataRow_" + category, delegate { ShowDataDetails(owner, category); });
                detail.AutoSize = false; detail.Width = DpiLayout.S(owner, 92); detail.Height = DpiLayout.S(owner, 30);
                detail.Ghost = false; detail.Dock = DockStyle.None; detail.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                detail.Margin = new Padding(0, DpiLayout.S(owner, 11), DpiLayout.S(owner, 10), 0);
                row.Controls.Add(detail, 4, 0);
                table.Controls.Add(row, 0, i + 1); table.SetColumnSpan(row, 5);
            }
            table.Width = DpiLayout.S(owner, 850);
            Panel tableHost = new Panel { Name = "workbenchDataTableHost", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
            table.Dock = DockStyle.Top; tableHost.Controls.Add(table);
            Add(localBody, tableHost);
            ModernLabel note = Label("正在读取本地数据…", false); note.Name = "workbenchDataNote"; Add(localBody, note);

            TableLayoutPanel maintenance = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 2, BackColor = ModernTheme.Hover, Margin = new Padding(0, 14, 0, 10), Padding = new Padding(12, 10, 12, 10) };
            maintenance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            maintenance.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            TableLayoutPanel maintainText = Stack();
            Add(maintainText, Label("缓存与日志维护", true));
            Add(maintainText, Label("清理运行缓存和日志，保留收藏、历史、配置和登录资料。", false));
            maintenance.Controls.Add(maintainText, 0, 0);
            AButton clean = AntdButton("清理缓存和日志", "workbenchCleanCache", delegate
            {
                if (MessageBox.Show(owner, "清理运行缓存和日志？收藏、历史、配置和登录资料会保留。请先关闭播放会话。",
                    "清理缓存", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                string error = LocalData.CleanCache();
                MessageBox.Show(owner, error.Length == 0 ? "缓存和日志已清理。" : "清理未完成：" + error,
                    "清理缓存", MessageBoxButtons.OK, error.Length == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                refreshButton.PerformClick();
            });
            maintenance.Controls.Add(clean, 1, 0);
            Add(localBody, maintenance);

            TableLayoutPanel build = Stack(); build.Name = "workbenchBuildInfo";
            Add(build, Label("构建信息", true));
            ModernLabel buildVersion = Label("Build 版本：" + BuildVersion(BuildInfo.Text), false);
            buildVersion.Name = "workbenchBuildVersion"; Add(build, buildVersion);
            ModernLabel buildTime = Label("构建来源时间（UTC）：" + BuildTime(BuildInfo.Text), false);
            buildTime.Name = "workbenchBuildTime"; Add(build, buildTime);
            Add(localBody, build);
            ModernLabel ready = Label("✓ 就绪 · 本地数据只读预览，未修改任何文件。", false);
            ready.Name = "workbenchDataReady"; Add(localBody, ready);

            TableLayoutPanel diagnosticBody = Stack();
            diagnosticBody.AutoSize = false; diagnosticBody.Dock = DockStyle.Fill;
            Panel diagnosticCard = Card(diagnosticBody); diagnosticCard.Name = "workbenchDiagnosticCard";
            diagnosticCard.AutoSize = false; diagnosticCard.Dock = DockStyle.None; diagnosticCard.Height = DpiLayout.S(owner, 360);
            diagnosticCard.Visible = false; body.Controls.Add(diagnosticCard);
            TableLayoutPanel diagnosticHead = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
            diagnosticHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            diagnosticHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            diagnosticHead.Controls.Add(Label("平台运行状态", true), 0, 0);
            FlowLayoutPanel diagnosticActions = Flow(); diagnosticActions.WrapContents = false;
            ASelect platformBox = new ASelect { Name = "workbenchDiagnosticPlatform", Width = DpiLayout.S(owner, 112),
                Height = DpiLayout.S(owner, 32), Margin = new Padding(0, 0, 8, 8),
                Font = new Font(ModernTheme.UiFont.FontFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel) };
            platformBox.Items.AddRange(new object[] { "B站", "虎牙", "斗鱼", "抖音" }); platformBox.SelectedIndex = 0;
            diagnosticActions.Controls.Add(platformBox);
            AButton refreshDiagnostic = AntdButton("刷新运行状态", "workbenchRefreshDiagnostics", null);
            AButton logs = AntdButton("查看诊断日志", "workbenchDiagnosticLogs", delegate { ShowDataDetails(owner, "logs"); });
            diagnosticHead.Controls.Add(diagnosticActions, 1, 0); Add(diagnosticBody, diagnosticHead);

            TableLayoutPanel observationTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, BackColor = Color.Transparent };
            int[] observationWidths = { 26, 54, 20 };
            for (int i = 0; i < observationWidths.Length; i++) observationTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, observationWidths[i]));
            observationTable.RowCount = 4; observationTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            for (int i = 0; i < 3; i++) observationTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            TableLayoutPanel observationHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32,
                ColumnCount = 3, BackColor = ModernTheme.Hover, Margin = new Padding(0, 0, 0, 1) };
            for (int i = 0; i < observationWidths.Length; i++) observationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, observationWidths[i]));
            string[] observationHeaders = { "环节", "最近结果", "更新时间" };
            for (int i = 0; i < observationHeaders.Length; i++) { Label headCell = TableCell(observationHeaders[i], false); observationHeader.Controls.Add(headCell, i, 0); }
            observationTable.Controls.Add(observationHeader, 0, 0); observationTable.SetColumnSpan(observationHeader, 3);
            Label[] stages = new Label[3], results = new Label[3], times = new Label[3];
            for (int i = 0; i < 3; i++)
            {
                TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 42, AutoSize = false, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int c = 0; c < observationWidths.Length; c++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, observationWidths[c]));
                stages[i] = TableCell("—", false); results[i] = TableCell("—", false); times[i] = TableCell("—", false);
                row.Controls.Add(stages[i], 0, 0); row.Controls.Add(results[i], 1, 0); row.Controls.Add(times[i], 2, 0);
                observationTable.Controls.Add(row, 0, i + 1); observationTable.SetColumnSpan(row, 3);
            }
            observationTable.Width = DpiLayout.S(owner, 850);
            Panel observationHost = new Panel { Name = "workbenchDataObservationHost", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
            observationTable.Dock = DockStyle.Top; observationHost.Controls.Add(observationTable);
            Add(diagnosticBody, observationHost);
            FlowLayoutPanel diagnosticFooter = Flow(); diagnosticFooter.WrapContents = false;
            diagnosticFooter.Name = "workbenchDiagnosticActions";
            diagnosticFooter.Controls.Add(refreshDiagnostic); diagnosticFooter.Controls.Add(logs);
            Add(diagnosticBody, diagnosticFooter);
            Add(diagnosticBody, Label("这里展示本地最近观测，不发起平台请求；历史结果不能代表当前状态。", false));

            Action layoutPage = delegate
            {
                int width = Math.Max(DpiLayout.S(owner, 320), body.ClientSize.Width);
                int contentTop = DpiLayout.S(owner, 44);
                tabs.SetBounds(0, 0, width, DpiLayout.S(owner, 36));
                localCard.SetBounds(0, contentTop, width, DpiLayout.S(owner, 640));
                diagnosticCard.SetBounds(0, contentTop, width, DpiLayout.S(owner, 360));
                body.Height = contentTop + (diagnosticCard.Visible ? DpiLayout.S(owner, 360) : DpiLayout.S(owner, 640));
                ScrollableControl scrollPage = page as ScrollableControl;
            if (scrollPage != null) scrollPage.AutoScrollMinSize = new Size(0, body.Height + DpiLayout.S(owner, 10));
            };
            body.SizeChanged += delegate { layoutPage(); };
            DataSnapshot snapshot = null; int generation = 0; bool refreshing = false;
            Action apply = delegate
            {
                if (snapshot == null) return;
                for (int i = 0; i < snapshot.Categories.Count && i < 5; i++)
                {
                    DataCategory category = snapshot.Categories[i];
                    counts[i].Text = category.Rows.Count + (category.Id == "config" ? " 项" : category.Id == "cache" || category.Id == "logs" ? " 个文件" : " 条记录");
                    paths[i].Text = PathText(category.Location);
                    sizes[i].Text = SizeText(category.Bytes);
                    updated[i].Text = category.ModifiedUtc == DateTime.MinValue ? "尚无文件" : category.ModifiedUtc.ToLocalTime().ToString("MM-dd HH:mm");
                }
                note.Text = snapshot.Warning.Length == 0 ? "本地数据清单 · 点击“查看明细”打开只读数据窗口" : snapshot.Warning;
            };
            Action refresh = delegate
            {
                if (refreshing || page.IsDisposed) return;
                refreshing = true; int version = ++generation; refreshButton.Enabled = false;
                note.Text = "正在读取本地数据…";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    DataSnapshot value = ReadSnapshot();
                    try
                    {
                        if (!page.IsDisposed && page.IsHandleCreated) page.BeginInvoke((MethodInvoker)delegate
                        {
                            if (page.IsDisposed || version != generation) return;
                            snapshot = value; apply();
                            refreshing = false; refreshButton.Enabled = true;
                        });
                        else refreshing = false;
                    }
                    catch (InvalidOperationException) { refreshing = false; }
                });
            };
            Action drawDiagnostics = delegate
            {
                int index = Math.Max(0, Math.Min(3, platformBox.SelectedIndex));
                string selected = LoginHealth.Platforms[index];
                List<string[]> rows = DiagnosticRows(selected);
                for (int i = 0; i < 3 && i < rows.Count; i++)
                {
                    stages[i].Text = rows[i][0]; results[i].Text = rows[i][1]; times[i].Text = rows[i][2];
                }
            };
            refreshButton.Click += delegate { refresh(); };
            viewButton.Click += delegate { ShowDataDetails(owner, "favorites"); };
            platformBox.SelectedIndexChanged += delegate { drawDiagnostics(); };
            refreshDiagnostic.Click += delegate { drawDiagnostics(); };
            Action switchTab = delegate
            {
                layoutPage();
                if (page is ScrollableControl) ((ScrollableControl)page).AutoScrollPosition = new Point(0, 0);
                Relayout(page, owner);
            };
            localTab.Click += delegate
            {
                localTab.Ghost = false; diagnosticTab.Ghost = true;
                localCard.Visible = true; diagnosticCard.Visible = false; refresh(); switchTab();
            };
            diagnosticTab.Click += delegate
            {
                diagnosticTab.Ghost = false; localTab.Ghost = true;
                localCard.Visible = false; diagnosticCard.Visible = true; drawDiagnostics(); switchTab();
            };
            EventHandler shown = delegate { if (page.Visible) { refresh(); drawDiagnostics(); } };
            owner.Shown += shown;
            page.VisibleChanged += delegate { if (page.Visible && owner.Visible) { refresh(); drawDiagnostics(); } };
            Action applyTheme = delegate
            {
                header.BackColor = ModernTheme.Hover;
                observationHeader.BackColor = ModernTheme.Hover;
                maintenance.BackColor = ModernTheme.Hover;
            };
            ModernTheme.Changed += applyTheme;
            page.Disposed += delegate { ++generation; owner.Shown -= shown; ModernTheme.Changed -= applyTheme; };
            applyTheme();
            layoutPage();
            return page;
        }

        private static Label TableCell(string text, bool strong)
        {
            Label cell = new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Margin = Padding.Empty, Padding = new Padding(6, 0, 4, 0),
                BackColor = Color.Transparent,
                ForeColor = strong ? ModernTheme.Text : ModernTheme.TextSecondary };
            return cell;
        }

        private static string PathText(string path)
        {
            if (string.IsNullOrEmpty(path)) return "—";
            try
            {
                string normalized = path.Replace('\\', '/');
                int data = normalized.IndexOf("/data/", StringComparison.OrdinalIgnoreCase);
                if (data >= 0) return "data/" + normalized.Substring(data + 6);
                int runtime = normalized.IndexOf("/runtime", StringComparison.OrdinalIgnoreCase);
                if (runtime >= 0) return normalized.Substring(runtime + 1);
                string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return path.Substring(root.Length).TrimStart('\\').Replace('\\', '/');
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                    return "%USERPROFILE%" + path.Substring(home.Length).Replace('\\', '/');
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            return path.Replace('\\', '/');
        }

        internal static void Relayout(Control page, Form owner)
        {
            if (page == null || page.IsDisposed) return;
            Panel body = null; foreach (Control c in page.Controls) if (c.Name == "workbenchDataBody") body = c as Panel;
            if (body == null) return;
            Panel tableHost = FindControl(body, "workbenchDataTableHost");
            if (tableHost != null && tableHost.Controls.Count > 0)
            {
                TableLayoutPanel table = tableHost.Controls[0] as TableLayoutPanel;
                if (table != null)
                {
                    int total = 0; foreach (RowStyle style in table.RowStyles) total += (int)Math.Round(style.Height);
                    if (total < 10) total = table.PreferredSize.Height;
                    tableHost.Height = Math.Max(DpiLayout.S(owner, 80), total + 2);
                }
            }
            for (int i = 0; i < CategoryIds.Length; i++)
            {
                Control[] buttons = body.Controls.Find("workbenchDataRow_" + CategoryIds[i], true);
                if (buttons.Length == 0) continue;
                Control button = buttons[0];
                button.Width = DpiLayout.S(owner, 92);
                button.Height = DpiLayout.S(owner, 30);
                button.Margin = new Padding(DpiLayout.S(owner, 10), DpiLayout.S(owner, 11), 0, 0);
            }
            Panel observationHost = FindControl(body, "workbenchDataObservationHost");
            if (observationHost != null && observationHost.Controls.Count > 0)
            {
                TableLayoutPanel table = observationHost.Controls[0] as TableLayoutPanel;
                if (table != null)
                {
                    int total = 0; foreach (RowStyle style in table.RowStyles) total += (int)Math.Round(style.Height);
                    if (total < 10) total = table.PreferredSize.Height;
                    observationHost.Height = Math.Max(DpiLayout.S(owner, 80), total + 2);
                }
            }
            int height = 0;
            foreach (Control c in body.Controls) if (c.Visible) height = Math.Max(height, c.Bottom);
            body.Height = Math.Max(DpiLayout.S(owner, 300), height + DpiLayout.S(owner, 12));
            ScrollableControl scrollPage = page as ScrollableControl;
            if (scrollPage != null) scrollPage.AutoScrollMinSize = new Size(0, body.Height + DpiLayout.S(owner, 10));
        }

        private static Panel FindControl(Control root, string name)
        {
            if (root.Name == name && root is Panel) return (Panel)root;
            foreach (Control c in root.Controls)
            {
                Panel found = FindControl(c, name);
                if (found != null) return found;
            }
            return null;
        }

        internal static string BuildVersion(string text)
        {
            int space = (text ?? "").IndexOf(" (");
            return space < 0 ? text ?? "未知版本" : text.Substring(0, space);
        }

        internal static string BuildTime(string text)
        {
            Match match = Regex.Match(text ?? "", @"\(([^)]+)\)");
            DateTimeOffset time;
            if (!match.Success || !DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out time)) return "未知时间";
            // The deterministic build stamp is the source snapshot time injected by both build paths.
            return time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }

        private static Form Dialog(string title, int width, int height)
        {
            Form dialog = new DpiForm { Text = title, StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(width, height), MinimumSize = new Size(460, 320), Padding = new Padding(16),
                AutoScroll = true, AutoScaleMode = AutoScaleMode.None };
            DpiLayout.SetDesignFont(dialog, ModernTheme.UiFont);
            Action theme = delegate { dialog.BackColor = ModernTheme.Surface; dialog.ForeColor = ModernTheme.Text; if (dialog.IsHandleCreated) ModernTheme.ApplyWindowChrome(dialog); };
            theme(); ModernTheme.Changed += theme;
            dialog.Disposed += delegate { ModernTheme.Changed -= theme; };
            dialog.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(dialog); };
            return dialog;
        }

        internal static Form BuildDataDetails(string initialCategory)
        {
            Form dialog = Dialog("本地数据", 900, 650); dialog.Name = "workbenchDataDetails";
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
                Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 68)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dialog.Controls.Add(layout);
            FlowLayoutPanel tabs = Flow(); layout.Controls.Add(tabs, 0, 0);
            ModernLabel location = Label("", false); location.Name = "workbenchDetailLocation"; layout.Controls.Add(location, 0, 1);
            ModernListView list = List("workbenchDetailList", new[] { "内容" }, 300); list.Dock = DockStyle.Fill; layout.Controls.Add(list, 0, 2);
            TextBox preview = new TextBox { Name = "workbenchLogPreview", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Text = "选择日志文件可在这里查看经过凭据过滤的末尾内容。" };
            DpiLayout.SetDesignFont(preview, ModernTheme.UiFontSmall); layout.Controls.Add(preview, 0, 3);
            ModernLabel note = Label("只读数据窗口；配置仅显示允许公开查看的显示和播放器选项。", false); layout.Controls.Add(note, 0, 4);
            FlowLayoutPanel actions = Flow(); actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Controls.Add(Button("关闭窗口", "close", delegate { dialog.Close(); })); layout.Controls.Add(actions, 0, 5);
            DataSnapshot snapshot = null; string current = Array.IndexOf(CategoryIds, initialCategory) < 0 ? "favorites" : initialCategory;
            Dictionary<string, ModernButton> tabButtons = new Dictionary<string, ModernButton>();
            Action render = delegate
            {
                if (snapshot == null) return;
                DataCategory category = snapshot.Categories[Array.IndexOf(CategoryIds, current)];
                foreach (var pair in tabButtons) pair.Value.Selected = pair.Key == current;
                list.BeginUpdate();
                try { list.Columns.Clear(); foreach (string header in category.Headers) list.Columns.Add(header, 120); }
                finally { list.EndUpdate(); }
                SetRows(list, category.Rows); location.Text = category.Name + " · " + category.Rows.Count + " 项　" + category.Location;
                preview.Visible = current == "logs";
                layout.RowStyles[2].Height = current == "logs" ? 68 : 100;
                layout.RowStyles[3].Height = current == "logs" ? 32 : 0;
                preview.Text = current == "logs" ? "选择日志文件查看末尾内容。" : "";
                note.Text = category.Note + (snapshot.Warning.Length == 0 ? "" : "　" + snapshot.Warning);
            };
            foreach (string categoryId in CategoryIds)
            {
                string selected = categoryId;
                ModernButton tab = Button(CategoryNames[Array.IndexOf(CategoryIds, selected)], "workbenchDataTab_" + selected,
                    delegate { current = selected; render(); });
                tabButtons[selected] = tab; tabs.Controls.Add(tab);
            }
            list.SelectedIndexChanged += delegate
            {
                if (snapshot == null || current != "logs" || list.SelectedIndices.Count == 0) return;
                DataCategory category = snapshot.Categories[4]; int index = list.SelectedIndices[0];
                if (index < category.Files.Count) preview.Text = ReadLogPreview(category.Files[index]);
            };
            Action theme = delegate { preview.BackColor = ModernTheme.Card; preview.ForeColor = ModernTheme.Text; ModernTheme.ApplyScrollbarTheme(preview); };
            theme(); ModernTheme.Changed += theme; dialog.Disposed += delegate { ModernTheme.Changed -= theme; };
            dialog.Shown += delegate { snapshot = ReadSnapshot(); render(); };
            return dialog;
        }

        private static void ShowDataDetails(Form owner, string category)
        {
            using (Form dialog = BuildDataDetails(category)) { DpiLayout.ScaleTree(dialog); dialog.ShowDialog(owner); }
        }

        private static string SafeText(object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            text = Regex.Replace(text, @"[\x00-\x1F\x7F]", " ");
            return text.Length > 512 ? text.Substring(0, 512) + "…" : text;
        }

        private static string ReadJson(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            LocalData.RequireOrdinaryPath(path);
            FileInfo info = new FileInfo(path);
            if (info.Length > MaximumJsonBytes) throw new InvalidDataException("数据文件超过只读窗口的大小限额");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                char[] buffer = new char[MaximumJsonBytes + 1]; int total = 0, read;
                while (total < buffer.Length && (read = reader.Read(buffer, total, buffer.Length - total)) > 0) total += read;
                if (total > MaximumJsonBytes) throw new InvalidDataException("数据文件读取超过大小限额");
                return new string(buffer, 0, total);
            }
        }

        private static object Field(IDictionary<string, object> map, string key)
        {
            object value; return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        internal static List<string[]> ReadFavoriteRows(string path)
        {
            List<string[]> rows = new List<string[]>(); string json = ReadJson(path); if (json == null) return rows;
            object value = new JavaScriptSerializer().DeserializeObject(json);
            IDictionary<string, object> legacy = value as IDictionary<string, object>;
            if (legacy != null)
            {
                foreach (var pair in legacy)
                {
                    Target target = Parse(SafeText(pair.Value), "bili");
                    if (target != null) rows.Add(new[] { SafeText(pair.Key), PlatformName(target.Platform), target.Room });
                    if (rows.Count >= MaximumRows) break;
                }
            }
            else
            {
                IEnumerable entries = value as IList;
                if (entries == null) throw new InvalidDataException("收藏数据格式无法识别");
                foreach (object entry in entries)
                {
                    IDictionary<string, object> item = entry as IDictionary<string, object>;
                    Target target = Parse(SafeText(Field(item, "Value") ?? Field(item, "value")), "bili");
                    if (target == null) target = Parse(SafeText(Field(item, "room")), "bili");
                    if (target != null) rows.Add(new[] { SafeText(Field(item, "Key") ?? Field(item, "key") ?? Field(item, "name")), PlatformName(target.Platform), target.Room });
                    if (rows.Count >= MaximumRows) break;
                }
            }
            return rows;
        }

        internal static List<string[]> ReadHistoryRows(string path)
        {
            List<string[]> rows = new List<string[]>(); string json = ReadJson(path); if (json == null) return rows;
            IDictionary<string, object> data = new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
            if (data == null) throw new InvalidDataException("历史数据格式无法识别");
            foreach (var pair in data)
            {
                if (Platforms.Get(pair.Key) == null) continue;
                if (pair.Value == null) continue;
                IEnumerable entries = pair.Value as IList;
                if (entries == null) throw new InvalidDataException("历史平台记录不是列表");
                foreach (object value in entries)
                {
                    IDictionary<string, object> item = value as IDictionary<string, object>;
                    Target target = Parse(SafeText(Field(item, "Room")), pair.Key);
                    if (target != null) rows.Add(new[] { SafeText(Field(item, "At")), SafeText(Field(item, "Name")), PlatformName(target.Platform), target.Room });
                    if (rows.Count >= MaximumRows) return rows;
                }
            }
            return rows;
        }

        internal static List<string[]> ReadDisplayConfigRows(string path)
        {
            List<string[]> rows = new List<string[]>(); string json = ReadJson(path); if (json == null) return rows;
            IDictionary<string, object> map = new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
            if (map == null) throw new InvalidDataException("配置数据格式无法识别");
            string[] booleans = { "Enabled", "AutoStart", "DarkMode", "StrokeEnabled", "ScaleWithWindow", "AntiBlockSubtitle", "SolidText", "ShowUserName", "ShowMedal" };
            foreach (string key in booleans)
            {
                object value = Field(map, key); if (value is bool) rows.Add(new[] { key, (bool)value ? "开启" : "关闭" });
            }
            string[] numbers = { "FontScalePercent", "SpeedPercent", "OpacityPercent", "DisplayAreaPercent", "DanmakuDelayMs", "MaxOnScreen", "DuplicateWindowMs" };
            foreach (string key in numbers)
            {
                object value = Field(map, key); int number;
                if (value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number)) rows.Add(new[] { key, number.ToString(CultureInfo.InvariantCulture) });
            }
            foreach (string key in new[] { "ScMode", "MpvQuality", "FontName" })
            {
                string value = SafeText(Field(map, key));
                string[] allowed = key == "ScMode" ? new[] { "full", "compact", "badge", "off" }
                    : key == "MpvQuality" ? new[] { "off", "light", "high" } : new[] { "鸿蒙", "微软雅黑", "黑体", "宋体" };
                if (Array.IndexOf(allowed, value) >= 0) rows.Add(new[] { key, value });
            }
            return rows;
        }

        internal static DataSnapshot ReadSnapshot()
        {
            return ReadSnapshot(Favorites, History, DanmakuHost.ConfigPath(), RuntimeDir, CurrentToolPath("mpv"), CurrentToolPath("potplayer"));
        }

        internal static DataSnapshot ReadSnapshot(string favoritePath, string historyPath, string configPath, string runtime, string mpv, string pot)
        {
            DataSnapshot snapshot = new DataSnapshot();
            string[][] headers = { new[] { "收藏名称", "平台", "房间号" }, new[] { "播放时间", "主播名称", "平台", "房间号" },
                new[] { "配置项", "当前值" }, new[] { "文件", "大小", "最近更新" }, new[] { "日志文件", "大小", "最近更新" } };
            string[] locations = { favoritePath, historyPath, configPath, runtime, string.IsNullOrEmpty(runtime) ? "" : Path.Combine(runtime, "logs") };
            for (int i = 0; i < CategoryIds.Length; i++) snapshot.Categories.Add(new DataCategory { Id = CategoryIds[i], Name = CategoryNames[i], Headers = headers[i], Location = locations[i] ?? "", Note = "只读查看；文件未被修改。" });
            for (int index = 0; index < 3; index++)
            {
                try
                {
                    DataCategory category = snapshot.Categories[index];
                    category.Rows.AddRange(index == 0 ? ReadFavoriteRows(favoritePath) : index == 1 ? ReadHistoryRows(historyPath) : ReadDisplayConfigRows(configPath));
                    AddMetadata(category, locations[index]);
                }
                catch (Exception ex) { snapshot.Categories[index].Note = "读取未完成：" + ex.GetType().Name; snapshot.Warning = "部分数据无法读取，未作修改。"; }
            }
            DataCategory config = snapshot.Categories[2];
            config.Rows.Insert(0, new[] { "PotPlayer 程序路径", string.IsNullOrEmpty(pot) ? "未设置" : pot });
            config.Rows.Insert(0, new[] { "mpv 程序路径", string.IsNullOrEmpty(mpv) ? "未设置" : mpv });
            config.Note = "仅投影显示/播放器选项；不展示 Cookie、API Key、任意原始配置或凭据。";
            try
            {
                if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime))
                {
                    LocalData.RequireOrdinaryPath(runtime);
                    Stopwatch watch = Stopwatch.StartNew(); int visited = 0;
                    foreach (string path in Directory.EnumerateFiles(runtime))
                    {
                        if (++visited > MaximumFiles || watch.ElapsedMilliseconds > 1500) { snapshot.Warning = "文件列表达到统计限额；显示部分结果。"; break; }
                        if (RuntimeCacheName(Path.GetFileName(path)) && !ProtectedName(Path.GetFileName(path)) && !IsPersistentDataPath(path)) AddFile(snapshot.Categories[3], path);
                    }
                    string logs = Path.Combine(runtime, "logs");
                    if (Directory.Exists(logs))
                    {
                        LocalData.RequireOrdinaryPath(logs);
                        foreach (string path in Directory.EnumerateFiles(logs))
                        {
                            if (++visited > MaximumFiles || watch.ElapsedMilliseconds > 1500) { snapshot.Warning = "文件列表达到统计限额；显示部分结果。"; break; }
                            if (BoundedLog.OwnedName(Path.GetFileName(path)) && !ProtectedName(Path.GetFileName(path)) && !IsPersistentDataPath(path)) AddFile(snapshot.Categories[4], path);
                        }
                    }
                }
            }
            catch (Exception ex) { snapshot.Warning = "部分运行文件不可读：" + ex.GetType().Name; }
            snapshot.Categories[3].Note = "仅列本程序已知临时会话/状态文件；不遍历 profile、凭据或任意子目录。";
            snapshot.Categories[4].Note = "只读查看本程序日志；预览过滤凭据字段和 URL，最多显示末尾 128 KiB。";
            return snapshot;
        }

        private static bool ProtectedName(string name)
        {
            return Regex.IsMatch(name ?? "", "cookie|credential|token|password|profile|sessionid|api[-_]?key|health-login", RegexOptions.IgnoreCase);
        }

        private static void AddMetadata(DataCategory category, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            LocalData.RequireOrdinaryPath(path); FileInfo info = new FileInfo(path);
            category.Bytes += info.Length;
            if (info.LastWriteTimeUtc > category.ModifiedUtc) category.ModifiedUtc = info.LastWriteTimeUtc;
        }

        private static void AddFile(DataCategory category, string path)
        {
            LocalData.RequireOrdinaryPath(path); FileInfo info = new FileInfo(path);
            category.Files.Add(path); category.Rows.Add(new[] { info.Name, SizeText(info.Length), info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") });
            category.Bytes += info.Length;
            if (info.LastWriteTimeUtc > category.ModifiedUtc) category.ModifiedUtc = info.LastWriteTimeUtc;
        }

        private static List<string[]> OverviewRows(DataSnapshot snapshot)
        {
            List<string[]> rows = new List<string[]>();
            foreach (DataCategory category in snapshot.Categories)
                rows.Add(new[] { category.Name, category.Rows.Count + (category.Id == "config" ? " 项" : category.Id == "cache" || category.Id == "logs" ? " 个文件" : " 条记录"),
                    category.Location, SizeText(category.Bytes), category.ModifiedUtc == DateTime.MinValue ? "尚无文件" : category.ModifiedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") });
            return rows;
        }

        private static string SizeText(long value)
        {
            return value >= 1048576 ? (value / 1048576.0).ToString("0.##") + " MiB" : value >= 1024 ? (value / 1024.0).ToString("0.##") + " KiB" : value + " B";
        }

        internal static string ReadLogPreview(string path)
        {
            try
            {
                if (ProtectedName(Path.GetFileName(path)) || !BoundedLog.OwnedName(Path.GetFileName(path))) return "该文件不属于可预览日志。";
                LocalData.RequireOrdinaryPath(path);
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - 128 * 1024); stream.Seek(start, SeekOrigin.Begin);
                    byte[] buffer = new byte[128 * 1024]; int total = 0, count;
                    while (total < buffer.Length && (count = stream.Read(buffer, total, buffer.Length - total)) > 0) total += count;
                    string text = Encoding.UTF8.GetString(buffer, 0, total);
                    if (start > 0) { int newline = text.IndexOf('\n'); text = newline < 0 ? "" : text.Substring(newline + 1); }
                    StringBuilder safe = new StringBuilder(start > 0 ? "（仅显示日志末尾）\r\n" : "");
                    foreach (string line in text.Split('\n'))
                    {
                        if (Regex.IsMatch(line, "cookie|authorization|bearer|api[-_ ]?key|password|private[-_ ]?key|sessionid|sessdata|dy_auth|udb_", RegexOptions.IgnoreCase)) safe.AppendLine("[已隐藏包含凭据字段的日志行]");
                        else safe.AppendLine(Regex.Replace(line.TrimEnd('\r'), @"https?://[^\s\""<>]+", "[URL]", RegexOptions.IgnoreCase));
                    }
                    return safe.Length == 0 ? "日志为空。" : safe.ToString();
                }
            }
            catch (Exception ex) { return "日志暂不可读：" + ex.GetType().Name; }
        }

        internal static List<string[]> DiagnosticRows(string platform)
        {
            List<string[]> rows = new List<string[]>(); string[] stages = { "room", "playback", "danmaku" };
            string[] labels = { "房间解析 / 状态", "取流 / 本地中转", "弹幕连接" };
            for (int i = 0; i < stages.Length; i++)
            {
                string result = "尚无观测", time = "--", room = "--";
                try
                {
                    string path = Path.Combine(RuntimeDir ?? "", "health-" + platform + "-" + stages[i] + ".state.txt");
                    if (File.Exists(path))
                    {
                        string text = ReadJson(path); string[] parts = text.Split('|'); long ticks;
                        if (parts.Length >= 2 && parts.Length <= 3 && long.TryParse(parts[0], out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                        {
                            result = ObservationText(parts[1]); DateTime at = new DateTime(ticks, DateTimeKind.Utc);
                            time = at.ToLocalTime().ToString("MM-dd HH:mm:ss");
                            if (DateTime.UtcNow - at > TimeSpan.FromMinutes(5)) result += "（历史观测）";
                            if (parts.Length == 3 && Regex.IsMatch(parts[2], "^[A-Za-z0-9_-]{1,128}$")) room = parts[2];
                        }
                        else result = "观测格式无法识别";
                    }
                }
                catch (Exception) { result = "观测暂不可读"; }
                rows.Add(new[] { labels[i], result, time, room });
            }
            return rows;
        }

        private static string ObservationText(string value)
        {
            switch (value)
            {
                case "live": return "正在直播";
                case "replay": return "重播 / 录播";
                case "ready": return "曾就绪 / 取得结果";
                case "connecting": return "解析 / 连接中";
                case "retrying": return "正在重试";
                case "offline": return "未开播";
                case "stopped": return "连接已停止";
                case "auth-required": return "访问受阻，可能需平台核验";
                case "timeout": return "请求超时";
                case "network-error": return "网络异常";
                case "parse-error": return "响应解析失败";
                case "unknown": return "状态未确认";
                default: return "其他运行异常";
            }
        }
    }
}
