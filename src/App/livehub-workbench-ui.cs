using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // The workbench reuses the original launcher state and playback handlers.
    // Hidden list models retain selection/persistence semantics; the new browsers
    // provide the card/row presentation without adding a rendering dependency.
    private sealed partial class LauncherUi
    {
        internal readonly Dictionary<string, Panel> workbenchPages = new Dictionary<string, Panel>();
        private readonly Dictionary<string, WorkbenchNavigationButton> workbenchNavigation = new Dictionary<string, WorkbenchNavigationButton>();
        private readonly Dictionary<string, ModernButton> recentPlatformButtons = new Dictionary<string, ModernButton>();
        private readonly List<Font> workbenchFonts = new List<Font>();
        private bool workbenchReady, workbenchLayoutBusy, syncingWorkbench;
        internal string workbenchPage = "favorites";
        private string workbenchIdleHint;
        private WorkbenchPanel workbenchContent, workbenchLaunch, workbenchFooter;
        private Panel workbenchHost, workbenchModels;
        private WorkbenchStrip workbenchChrome, workbenchSidebar;
        private Label workbenchTitle, workbenchSubtitle, workbenchBrand;
        private WorkbenchBrandMark workbenchBrandMark, workbenchChromeIcon;
        private Label workbenchChromeBrand, workbenchChromeHint;
        private TrafficDot workbenchChromeMin, workbenchChromeMax, workbenchChromeClose;
        private Label workbenchWatchGroup, workbenchSettingsGroup, workbenchFooterHint, workbenchSessionsEmpty;
        private WorkbenchGlyph workbenchFooterIcon, workbenchHomeIcon, workbenchHomeAvatar;
        private WorkbenchFillStrip workbenchPlatformStrip, recentPlatformStrip;
        private readonly List<Label> workbenchScopeLabels = new List<Label>();
        private Label workbenchLaunchHint, workbenchFavoritesHint, workbenchRecentHint, workbenchSessionHint;
        private Label workbenchPlayerHelp;
        private WorkbenchRoomBrowser favoritesBrowser, recentBrowser;
        private ModernButton recentOpenMpv, recentOpenPot, workbenchStatusCancel;
        private ModernButton workbenchHomeSessions;
        private WorkbenchPanel workbenchHomeCard;
        private Label workbenchHomeHeader, workbenchHomeTitle, workbenchHomeMeta, workbenchHomeState;
        private AntdUI.Button workbenchHomeReopen;
        private Panel workbenchHomeList;
        private sealed class HomeSessionRow
        {
            internal WorkbenchPanel Card;
            internal WorkbenchGlyph Avatar;
            internal Label Title, Meta, State;
            internal AntdUI.Button Reopen;
            internal PlaybackSession Session;
            internal Target Target;
            internal bool Active = true;
        }
        private readonly List<HomeSessionRow> workbenchHomeRows = new List<HomeSessionRow>();
        private WorkbenchPanel workbenchPlayerCard, workbenchPlayerPreferences;
        private ComboBox workbenchQuality, workbenchBuffer, workbenchDanmaku, workbenchEnhance;
        private readonly List<Label> workbenchPlayerCaptions = new List<Label>();
        private LookupUi workbenchLookup;
        private Control workbenchDanmakuPage;

        private Label WorkbenchLabel(string name, string text, bool secondary)
        {
            ModernLabel label = new ModernLabel(); label.Name = name; label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft; label.AutoEllipsis = true;
            label.Role = secondary ? ModernLabel.RoleKind.Secondary : ModernLabel.RoleKind.Normal;
            WorkbenchFont(label, 12, FontStyle.Regular);
            return label;
        }

        private void WorkbenchFont(Control control, float pixels, FontStyle style)
        {
            // The interactive draft renders with the "Segoe UI","Microsoft YaHei UI"
            // stack; the workbench chrome deliberately does not use HarmonyOS Medium.
            Font font = WorkbenchFonts.Create(pixels, style);
            workbenchFonts.Add(font); DpiLayout.SetDesignFont(control, font);
        }

        private ModernButton WorkbenchButton(string name, string text, Action click)
        {
            ModernButton button = new ModernButton(); button.Name = name; button.Text = text;
            if (click != null) button.Click += delegate { click(); };
            return button;
        }

        private Label ChromeButton(string name, string text, Action click)
        {
            Label button = WorkbenchLabel(name, text, true);
            button.AutoSize = false; button.TextAlign = ContentAlignment.MiddleCenter;
            button.Cursor = Cursors.Hand;
            button.Click += delegate { if (click != null) click(); };
            return button;
        }

        private AntdUI.Button WorkbenchAntdButton(string name, string text, Action click)
        {
            Font font = WorkbenchFonts.Create(12f, FontStyle.Regular);
            workbenchFonts.Add(font);
            AntdUI.Button button = new AntdUI.Button { Name = name, Text = text, AutoSize = false,
                Height = Px(30), Radius = 7, Font = font };
            DpiLayout.SetDesignFont(button, font);
            if (click != null) button.Click += delegate { click(); };
            return button;
        }

        private void BuildWorkbench()
        {
            AntdUiBridge.Initialize();
            List<Control> oldControls = new List<Control>();
            foreach (Control child in form.Controls) oldControls.Add(child);
            form.SuspendLayout();
            try
            {
                form.Controls.Clear(); form.Tag = this; form.AutoScroll = false; form.AutoScrollMinSize = Size.Empty;
                // ⚠️ 原来是 Px(1024) × Px(866)：1.18 的竖长比例，正是用户说的「上下太长、
                //    左右宽度不够」的结构性根因（200% 下 = 2048×1732，比 2560×1504 的工作区
                //    还高，底部被任务栏裁掉）。现在用宽扁的设计基准 1240×720；真实启动时
                //    Load 里再按当前显示器工作区自适应一次（ApplyWorkbenchDefaultBounds）。
                form.ClientSize = new Size(Px(1240), Px(720));
                form.MinimumSize = new Size(Px(760), Px(520));
                form.Load += delegate { ApplyWorkbenchDefaultBounds(); };
                // Draft shell: a 34px chrome strip across the top, then the flat
                // sidebar (own surface tone, hairline right border) and content.
                workbenchChrome = new WorkbenchStrip { Name = "workbenchChrome", Edge = WorkbenchStrip.EdgeKind.Bottom };
                workbenchSidebar = new WorkbenchStrip { Name = "workbenchSidebar", Edge = WorkbenchStrip.EdgeKind.Right, AutoScroll = true };
                workbenchContent = new WorkbenchPanel { Name = "workbenchContent" };
                workbenchHost = new WorkbenchPanel { Name = "workbenchPageHost" };
                workbenchFooter = new WorkbenchPanel { Name = "workbenchFooter" };
                workbenchModels = new WorkbenchPanel { Name = "workbenchModels", Visible = false };
                form.Controls.Add(workbenchChrome); form.Controls.Add(workbenchSidebar);
                form.Controls.Add(workbenchContent); form.Controls.Add(workbenchModels);
                workbenchTitle = WorkbenchLabel("workbenchTitle", "我的收藏", false);
                WorkbenchFont(workbenchTitle, 23, FontStyle.Bold);
                workbenchSubtitle = WorkbenchLabel("workbenchSubtitle", "喜欢的主播，打开就能看。", true);
                workbenchContent.Controls.Add(workbenchTitle); workbenchContent.Controls.Add(workbenchSubtitle);
                workbenchContent.Controls.Add(workbenchHost); workbenchContent.Controls.Add(workbenchFooter);
                workbenchBrandMark = new WorkbenchBrandMark { Name = "workbenchBrandMark" };
                workbenchBrand = WorkbenchLabel("workbenchBrand", "看直播", false); WorkbenchFont(workbenchBrand, 19, FontStyle.Bold);
                workbenchWatchGroup = WorkbenchLabel("workbenchWatchGroup", "观看", true);
                workbenchSettingsGroup = WorkbenchLabel("workbenchSettingsGroup", "设置与账号", true);
                workbenchFooterHint = WorkbenchLabel("workbenchFooterHint", "本地播放器 · 四平台", true);
                workbenchChromeIcon = new WorkbenchBrandMark { Name = "workbenchChromeIcon" };
                workbenchChromeBrand = WorkbenchLabel("workbenchChromeBrand", "看直播", true);
                workbenchChromeHint = WorkbenchLabel("workbenchChromeHint", "本地播放器 · 四平台", true);
                workbenchChromeHint.TextAlign = ContentAlignment.MiddleRight;
                workbenchChromeClose = new TrafficDot("close", delegate { form.Close(); }) { Name = "workbenchChromeClose" };
                workbenchChromeMin = new TrafficDot("min", delegate { form.WindowState = FormWindowState.Minimized; }) { Name = "workbenchChromeMin" };
                workbenchChromeMax = new TrafficDot("max", delegate { WorkbenchWindowDrag.ToggleMaximize(form); }) { Name = "workbenchChromeMax" };
                workbenchFooterIcon = new WorkbenchGlyph { Name = "workbenchFooterIcon", Tiled = false, IconSize = 16, Glyph = "drive" };
                WorkbenchFont(workbenchWatchGroup, 10, FontStyle.Regular);
                WorkbenchFont(workbenchSettingsGroup, 10, FontStyle.Regular);
                WorkbenchFont(workbenchFooterHint, 11, FontStyle.Regular);
                WorkbenchFont(workbenchChromeBrand, 11, FontStyle.Regular);
                WorkbenchFont(workbenchChromeHint, 11, FontStyle.Regular);
                workbenchChrome.Controls.AddRange(new Control[] { workbenchChromeIcon, workbenchChromeBrand, workbenchChromeHint, workbenchChromeMin, workbenchChromeMax, workbenchChromeClose });
                workbenchChrome.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) WorkbenchWindowDrag.Begin(form); };
                workbenchChrome.DoubleClick += delegate { WorkbenchWindowDrag.ToggleMaximize(form); };
                foreach (Control drag in new Control[] { workbenchChromeIcon, workbenchChromeBrand, workbenchChromeHint })
                {
                    drag.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) WorkbenchWindowDrag.Begin(form); };
                    drag.DoubleClick += delegate { WorkbenchWindowDrag.ToggleMaximize(form); };
                }
                workbenchSidebar.Controls.AddRange(new Control[] { workbenchBrandMark, workbenchBrand,
                    workbenchWatchGroup, workbenchSettingsGroup, workbenchFooterIcon, workbenchFooterHint });
                string[] keys = { "favorites", "recent", "lookup", "sessions", "players", "danmaku", "accounts", "data" };
                string[] titles = { "我的收藏", "最近播放", "查找主播", "观看会话", "播放器与画质", "弹幕与 SC 设置", "账号与登录", "本地数据 / 平台状态" };
                string[] glyphs = { "star", "history", "search", "monitor", "settings", "chat", "user", "folder" };
                for (int i = 0; i < keys.Length; i++)
                {
                    string key = keys[i];
                    WorkbenchNavigationButton nav = new WorkbenchNavigationButton { Name = "workbenchNav_" + key, Text = titles[i], Glyph = glyphs[i] };
                    nav.Click += delegate { SelectWorkbenchPage(key); };
                    WorkbenchFont(nav, 10.5f, FontStyle.Regular);
                    workbenchNavigation.Add(key, nav); workbenchSidebar.Controls.Add(nav);
                    WorkbenchPanel page = new WorkbenchPanel { Name = "workbenchPage_" + key, AutoScroll = true, Visible = false };
                    page.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(page); };
                    workbenchPages.Add(key, page); workbenchHost.Controls.Add(page);
                }
                status.Parent = workbenchFooter; status.AutoEllipsis = true;
                WorkbenchFont(status, 11, FontStyle.Regular);
                workbenchFooter.Paint += delegate(object sender, PaintEventArgs e)
                {
                    using (Pen line = new Pen(ModernTheme.Border))
                        e.Graphics.DrawLine(line, 0, 0, workbenchFooter.Width, 0);
                    int size = Px(13), top = Math.Max(0, (workbenchFooter.Height - size) / 2);
                    using (Pen pen = new Pen(ModernTheme.TextSecondary, Math.Max(1f, DpiLayout.For(workbenchFooter))))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.DrawEllipse(pen, 0, top, size, size);
                        e.Graphics.DrawLines(pen, new[] { new Point(Px(3), top + size / 2), new Point(Px(5), top + size - Px(4)), new Point(Px(10), top + Px(4)) });
                    }
                };
                workbenchStatusCancel = WorkbenchButton("workbenchStatusCancel", "取消启动", CancelWorkbenchLaunch);
                workbenchFooter.Controls.Add(workbenchStatusCancel);
                BuildWorkbenchFavorites(); BuildWorkbenchRecent(); BuildWorkbenchSessions();
                BuildWorkbenchPlayers(); BuildWorkbenchLookup();
                workbenchDanmakuPage = WorkbenchDanmakuUi.Build(form, RefreshWorkbenchSavedPreferences);
                workbenchDanmakuPage.Dock = DockStyle.Fill; workbenchPages["danmaku"].Controls.Add(workbenchDanmakuPage);
                Control accounts = WorkbenchDataUi.BuildAccounts(form); accounts.Dock = DockStyle.Fill; workbenchPages["accounts"].Controls.Add(accounts);
                Control data = WorkbenchDataUi.BuildData(form); data.Dock = DockStyle.Fill; workbenchPages["data"].Controls.Add(data);
                foreach (Control child in oldControls)
                    if (child.Parent == null) { workbenchModels.Controls.Add(child); child.Visible = false; }
                // Keep the original lists as the selection model for persistence and badge updates.
                favList.Parent = workbenchModels; recentList.Parent = workbenchModels;
                foreach (Control child in workbenchModels.Controls) child.Visible = false;
                ((ModernButton)openMpv).Style = ModernButton.Kind.Primary;
                ((ModernButton)inputHelp).Style = ModernButton.Kind.Subtle;
                ((ModernButton)danmakuSettings).Style = ModernButton.Kind.Subtle;
                foreach (ModernButton button in platformButtons.Values) { WorkbenchFont(button, 12, FontStyle.Regular); button.Tab = true; }
                WorkbenchFont(inputHelp, 11, FontStyle.Regular);
                WorkbenchFont(openMpv, 13, FontStyle.Regular);
                WorkbenchFont(openPot, 13, FontStyle.Regular);
                WorkbenchFont(cancelLaunch, 13, FontStyle.Regular);
                WorkbenchFont(addFav, 12, FontStyle.Regular);
                WorkbenchFont(delFav, 12, FontStyle.Regular);
                WorkbenchFont(danmakuSettings, 11, FontStyle.Regular);
                WorkbenchFont(workbenchStatusCancel, 12, FontStyle.Regular);
                WorkbenchFont(qualityBox, 12, FontStyle.Regular);
                WorkbenchFont(bufferBox, 12, FontStyle.Regular);
                WorkbenchFont(danmakuBox, 12, FontStyle.Regular);
                WorkbenchFont(scBox, 12, FontStyle.Regular);
                WorkbenchFont(enhanceBox, 12, FontStyle.Regular);
                cancelLaunch.EnabledChanged += delegate { SyncWorkbenchActions(); };
                openMpv.EnabledChanged += delegate { SyncWorkbenchActions(); };
                recentList.SelectedIndexChanged += delegate { if (!syncingWorkbench) recentBrowser.SelectedIndex = recentList.SelectedIndex; SyncWorkbenchActions(); };
                favList.SelectedIndexChanged += delegate { if (!syncingWorkbench) favoritesBrowser.SelectedIndex = favList.SelectedIndex; SyncWorkbenchActions(); };
                form.Disposed += delegate
                {
                    if (workbenchLookup != null) workbenchLookup.Dispose();
                    foreach (Font font in workbenchFonts) font.Dispose();
                };
                workbenchReady = true;
                SelectWorkbenchPage("favorites");
            }
            finally { form.ResumeLayout(false); }
        }

        private void BuildWorkbenchFavorites()
        {
            Panel page = workbenchPages["favorites"];
            workbenchLaunch = new WorkbenchPanel { Name = "workbenchLaunch", IsCard = true };
            page.Controls.Add(workbenchLaunch);
            foreach (ModernButton button in platformButtons.Values) button.Parent = workbenchLaunch;
            // Grouped platform pills sit on a soft fill strip (draft .wb-platforms).
            workbenchPlatformStrip = new WorkbenchFillStrip { Name = "workbenchPlatformStrip", Visible = false };
            workbenchLaunch.Controls.Add(workbenchPlatformStrip);
            foreach (Control child in new Control[] { input, inputHelp, openMpv, openPot, cancelLaunch,
                qualityLabel, qualityBox, bufferLabel, bufferBox, danmakuLabel, danmakuBox,
                scLabel, scBox, enhanceLabel, enhanceBox, danmakuSettings }) child.Parent = workbenchLaunch;
            workbenchLaunchHint = WorkbenchLabel("workbenchLaunchHint", "画质、缓冲、弹幕用于本次播放；SC 与画质增强保存为偏好。", true);
            WorkbenchFont(workbenchLaunchHint, 11, FontStyle.Regular);
            workbenchLaunch.Controls.Add(workbenchLaunchHint);
            // Draft quick settings: an 11px label with a 10px scope hint on the
            // right ("本次" / "保存 / 实时" / "保存 / mpv") and a 12px select.
            Control[] launchLabels = { qualityLabel, bufferLabel, danmakuLabel, scLabel, enhanceLabel };
            string[] scopes = { "本次", "本次", "本次", "保存 / 实时", "保存 / mpv" };
            for (int i = 0; i < launchLabels.Length; i++)
            {
                WorkbenchFont(launchLabels[i], 11, FontStyle.Regular);
                Label scope = WorkbenchLabel("workbenchScope" + i, scopes[i], true);
                scope.TextAlign = ContentAlignment.MiddleRight;
                WorkbenchFont(scope, 10, FontStyle.Regular);
                workbenchScopeLabels.Add(scope);
                workbenchLaunch.Controls.Add(scope);
            }
            favoritesBrowser = new WorkbenchRoomBrowser { Name = "favoritesBrowser", Cards = true, AccessibleName = "收藏房间" };
            favoritesBrowser.SelectionChanged += delegate(int index) { if (index >= 0 && index < favList.Items.Count) favList.SelectedIndex = index; };
            favoritesBrowser.OpenRequested += delegate(int index, string player) { LaunchWorkbenchFavorite(index, player); };
            favoritesBrowser.ChooseRequested += delegate(int index)
            {
                if (index < 0 || index >= currentFavorites.Count) return;
                Target target = Parse(currentFavorites[index].Value, "bili");
                if (target != null) input.Text = target.Platform + ":" + target.Room;
            };
            favoritesBrowser.DeleteRequested += delegate { delFav.PerformClick(); };
            page.Controls.Add(favoritesBrowser);
            favLabel.Parent = page; addFav.Parent = page; delFav.Parent = page;
            // Draft section head: 14px semibold ink title, quiet secondary count.
            ModernLabel favoritesHeading = favLabel as ModernLabel;
            if (favoritesHeading != null) favoritesHeading.Role = ModernLabel.RoleKind.Normal;
            WorkbenchFont(favLabel, 14, FontStyle.Bold);
            workbenchFavoritesHint = WorkbenchLabel("workbenchFavoritesHint", "选择卡片回填房间；双击或回车用 mpv，Shift+回车用 PotPlayer。", true);
            WorkbenchFont(workbenchFavoritesHint, 12, FontStyle.Regular);
            page.Controls.Add(workbenchFavoritesHint);
            workbenchHomeIcon = new WorkbenchGlyph { Name = "workbenchHomeIcon", Tiled = false, IconSize = 16, Glyph = "monitor-play" };
            page.Controls.Add(workbenchHomeIcon);
            workbenchHomeHeader = WorkbenchLabel("workbenchHomeHeader", "正在观看 / 最近打开", false);
            WorkbenchFont(workbenchHomeHeader, 14, FontStyle.Bold);
            page.Controls.Add(workbenchHomeHeader);
            workbenchHomeSessions = WorkbenchButton("workbenchHomeSessions", "全部会话 →", delegate { SelectWorkbenchPage("sessions"); });
            workbenchHomeSessions.Style = ModernButton.Kind.Subtle;
            WorkbenchFont(workbenchHomeSessions, 12, FontStyle.Regular);
            page.Controls.Add(workbenchHomeSessions);
            workbenchHomeList = new Panel { Name = "workbenchHomeList", AutoScroll = true };
            workbenchHomeList.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(workbenchHomeList); };
            page.Controls.Add(workbenchHomeList);
            HomeSessionRow first = CreateHomeSessionRow();
            workbenchHomeCard = first.Card;
            workbenchHomeAvatar = first.Avatar;
            workbenchHomeTitle = first.Title;
            workbenchHomeMeta = first.Meta;
            workbenchHomeState = first.State;
            workbenchHomeReopen = first.Reopen;
            // DpiForm captures the control tree once. Reserve every row before
            // that capture so sessions opened later inherit the same scaling.
            while (workbenchHomeRows.Count < 24) CreateHomeSessionRow();
        }

        private HomeSessionRow CreateHomeSessionRow()
        {
            int index = workbenchHomeRows.Count;
            string suffix = index == 0 ? "" : index.ToString();
            HomeSessionRow row = new HomeSessionRow();
            row.Active = index == 0;
            row.Card = new WorkbenchPanel { Name = "workbenchHomeSession" + suffix, IsCard = true, Visible = row.Active };
            row.Avatar = new WorkbenchGlyph { Name = "workbenchHomeAvatar" + suffix, Tiled = true, IconSize = 15, Glyph = "monitor-play" };
            row.Title = WorkbenchLabel("workbenchHomeTitle" + suffix, "暂无播放记录", false);
            WorkbenchFont(row.Title, 13, FontStyle.Bold);
            row.Meta = WorkbenchLabel("workbenchHomeMeta" + suffix, "打开房间后，这里保留最近播放。", true);
            WorkbenchFont(row.Meta, 11, FontStyle.Regular);
            row.State = WorkbenchLabel("workbenchHomeState" + suffix, "", true);
            WorkbenchFont(row.State, 11, FontStyle.Regular);
            row.Reopen = WorkbenchAntdButton("workbenchHomeReopen" + suffix, "重新打开", delegate
            {
                if (row.Target == null || currentLaunch != null) return;
                input.Text = row.Target.Platform + ":" + row.Target.Room;
                if (row.Session != null)
                {
                    qualityBox.SelectedIndex = row.Target.Quality == "smooth" ? 2 : row.Target.Quality == "high" ? 1 : 0;
                    bufferBox.SelectedIndex = row.Target.BufferMode == "low" ? 1 : 0;
                    danmakuBox.SelectedIndex = row.Target.DanmakuMode == "on" ? 1 : row.Target.DanmakuMode == "off" ? 2 : 0;
                }
                Launch(row.Session == null ? "mpv" : row.Session.Player);
            });
            row.Card.Controls.AddRange(new Control[] { row.Avatar, row.Title, row.Meta, row.State, row.Reopen });
            workbenchHomeRows.Add(row);
            workbenchHomeList.Controls.Add(row.Card);
            return row;
        }

        private void BuildWorkbenchRecent()
        {
            Panel page = workbenchPages["recent"];
            foreach (string p in platforms)
            {
                string chosen = p;
                ModernButton button = WorkbenchButton("recentPlatform_" + p, PlatformName(p), delegate { SetWorkbenchPlatform(chosen); });
                button.Tab = true;
                recentPlatformButtons.Add(p, button); page.Controls.Add(button);
            }
            recentPlatformStrip = new WorkbenchFillStrip { Name = "recentPlatformStrip", Visible = false };
            page.Controls.Add(recentPlatformStrip);
            recentLabel.Parent = page;
            ModernLabel recentHeading = recentLabel as ModernLabel;
            if (recentHeading != null) recentHeading.Role = ModernLabel.RoleKind.Normal;
            WorkbenchFont(recentLabel, 14, FontStyle.Bold);
            workbenchRecentHint = WorkbenchLabel("workbenchRecentHint", "每条记录可选择播放器；双击默认用 mpv 打开。", true);
            WorkbenchFont(workbenchRecentHint, 12, FontStyle.Regular);
            recentOpenMpv = WorkbenchButton("recentOpenMpv", "用 mpv 打开选中房间", delegate { LaunchWorkbenchRecent(recentList.SelectedIndex, "mpv"); });
            recentOpenPot = WorkbenchButton("recentOpenPot", "用 PotPlayer 打开选中房间", delegate { LaunchWorkbenchRecent(recentList.SelectedIndex, "potplayer"); });
            WorkbenchFont(recentOpenMpv, 12, FontStyle.Regular); WorkbenchFont(recentOpenPot, 12, FontStyle.Regular);
            recentBrowser = new WorkbenchRoomBrowser { Name = "recentBrowser", Cards = false, AccessibleName = "最近播放记录" };
            recentBrowser.SelectionChanged += delegate(int index) { if (index >= 0 && index < recentList.Items.Count) recentList.SelectedIndex = index; };
            recentBrowser.OpenRequested += delegate(int index, string player) { LaunchWorkbenchRecent(index, player); };
            page.Controls.AddRange(new Control[] { workbenchRecentHint, recentOpenMpv, recentOpenPot, recentBrowser });
        }

        private void BuildWorkbenchSessions()
        {
            Panel page = workbenchPages["sessions"];
            sessionsLabel.Parent = page; reopen.Parent = page; sessionsList.Parent = page;
            sessionsList.Name = "workbenchSessions";
            ModernLabel sessionsHeading = sessionsLabel as ModernLabel;
            if (sessionsHeading != null) sessionsHeading.Role = ModernLabel.RoleKind.Normal;
            WorkbenchFont(sessionsLabel, 14, FontStyle.Bold);
            WorkbenchFont(reopen, 12, FontStyle.Regular);
            workbenchSessionHint = WorkbenchLabel("workbenchSessionHint", "重新打开将恢复该会话的播放器、画质、缓冲和弹幕选项。", true);
            WorkbenchFont(workbenchSessionHint, 12, FontStyle.Regular);
            page.Controls.Add(workbenchSessionHint);
            workbenchSessionsEmpty = WorkbenchLabel("workbenchSessionsEmpty", "暂无观看会话\r\n打开一个房间后，这里会显示会话记录。", true);
            workbenchSessionsEmpty.AutoSize = false; workbenchSessionsEmpty.TextAlign = ContentAlignment.MiddleCenter;
            WorkbenchFont(workbenchSessionsEmpty, 13, FontStyle.Regular);
            page.Controls.Add(workbenchSessionsEmpty);
        }

        private ComboBox WorkbenchPreference(string name, ComboBox source, Action changed)
        {
            ModernComboBox box = new ModernComboBox { Name = name };
            foreach (object item in source.Items) box.Items.Add(item);
            box.SelectedIndex = source.SelectedIndex;
            box.SelectedIndexChanged += delegate { if (!syncingWorkbench && box.SelectedIndex >= 0) { source.SelectedIndex = box.SelectedIndex; if (changed != null) changed(); } };
            source.SelectedIndexChanged += delegate
            {
                if (box.SelectedIndex != source.SelectedIndex)
                { bool previous = syncingWorkbench; syncingWorkbench = true; box.SelectedIndex = source.SelectedIndex; syncingWorkbench = previous; }
            };
            return box;
        }

        private void BuildWorkbenchPlayers()
        {
            Panel page = workbenchPages["players"];
            workbenchPlayerCard = new WorkbenchPanel { Name = "workbenchPlayerCard", IsCard = true };
            workbenchPlayerPreferences = new WorkbenchPanel { Name = "workbenchPlayerPreferences", IsCard = true };
            page.Controls.Add(workbenchPlayerCard); page.Controls.Add(workbenchPlayerPreferences);
            foreach (Control child in new Control[] { playerLabel, playerBox, pickPlayer, playerHint }) child.Parent = workbenchPlayerCard;
            playerHint.AutoEllipsis = true;
            workbenchPlayerHelp = WorkbenchLabel("workbenchPlayerHelp", "这里选择要配置路径的程序；播放时可在收藏或最近播放中选择 mpv / PotPlayer。", true);
            WorkbenchFont(workbenchPlayerHelp, 12, FontStyle.Regular);
            WorkbenchFont(playerLabel, 14, FontStyle.Bold);
            WorkbenchFont(playerHint, 11, FontStyle.Regular);
            WorkbenchFont(pickPlayer, 12, FontStyle.Regular);
            WorkbenchFont(playerBox, 12, FontStyle.Regular);
            workbenchPlayerCard.Controls.Add(workbenchPlayerHelp);
            workbenchQuality = WorkbenchPreference("workbenchPlayerQuality", qualityBox, null);
            workbenchBuffer = WorkbenchPreference("workbenchPlayerBuffer", bufferBox, null);
            workbenchDanmaku = WorkbenchPreference("workbenchPlayerDanmaku", danmakuBox, null);
            workbenchEnhance = WorkbenchPreference("workbenchPlayerEnhance", enhanceBox, null);
            string[] captions = { "画质 · 本次播放", "mpv 缓冲 · 本次播放", "弹幕 · 本次播放", "mpv 画质增强 · 保存偏好" };
            foreach (string caption in captions)
            {
                Label label = WorkbenchLabel("workbenchPlayerCaption" + workbenchPlayerCaptions.Count, caption, true);
                WorkbenchFont(label, 11, FontStyle.Regular);
                workbenchPlayerCaptions.Add(label); workbenchPlayerPreferences.Controls.Add(label);
            }
            workbenchPlayerPreferences.Controls.AddRange(new Control[] { workbenchQuality, workbenchBuffer, workbenchDanmaku, workbenchEnhance });
        }

        private void BuildWorkbenchLookup()
        {
            workbenchLookup = new LookupUi("all", "", delegate(LookupCandidate candidate, string name)
            {
                bool added = SaveLookupFavorite(candidate, name, favorites); RefreshLists();
                status.ForeColor = added ? ModernTheme.Success : ModernTheme.TextSecondary;
                status.Text = added ? "已收藏 " + name : "该房间已经在收藏中";
            }, SharedLookupService, SharedLookupVerifier, form);
            workbenchLookup.View.Dock = DockStyle.Fill;
            workbenchPages["lookup"].Controls.Add(workbenchLookup.View);
            workbenchPages["lookup"].AutoScroll = false;
            workbenchPages["lookup"].AutoScrollMinSize = Size.Empty;
        }

        internal void SelectWorkbenchPage(string key)
        {
            if (!workbenchPages.ContainsKey(key)) return;
            workbenchPage = key;
            foreach (KeyValuePair<string, Panel> pair in workbenchPages) pair.Value.Visible = pair.Key == key;
            foreach (KeyValuePair<string, WorkbenchNavigationButton> pair in workbenchNavigation) pair.Value.Selected = pair.Key == key;
            workbenchTitle.Text = workbenchNavigation[key].Text;
            string subtitle = key == "favorites" ? "喜欢的主播，打开就能看。"
                : key == "recent" ? "选择熟悉的播放器，从最近的房间继续。"
                : key == "lookup" ? "查询、核验、收藏，找到喜欢的主播。"
                : key == "sessions" ? "查看正在观看的房间，按上次选项重新打开。"
                : key == "players" ? "选择程序路径，管理画质和播放偏好。"
                : key == "danmaku" ? "显示、过滤、SC 与更多覆盖层设置。"
                : key == "accounts" ? "四个平台的登录与 Cookie 状态，一处管理。"
                : "浏览本地数据，查看解析、取流和弹幕运行状态。";
            workbenchSubtitle.Text = subtitle;
            form.AcceptButton = key == "favorites" ? openMpv : key == "recent" ? recentOpenMpv : key == "lookup" ? workbenchLookup.Search : null;
            form.CancelButton = key == "lookup" ? workbenchLookup.Cancel : null;
            if (key == "danmaku" && workbenchDanmakuPage != null) WorkbenchDanmakuUi.Refresh(workbenchDanmakuPage);
            if (currentLaunch == null && (string.IsNullOrEmpty(status.Text)
                || status.Text.StartsWith("选房间", StringComparison.Ordinal) || status.Text == workbenchIdleHint))
            {
                workbenchIdleHint = key == "favorites" ? "输入房间或选择收藏，回车用 mpv 打开。"
                    : key == "recent" ? "选择一条记录，可用 mpv 或 PotPlayer 打开。"
                    : key == "lookup" ? "输入昵称、房间链接或抖音号，回车查询。"
                    : key == "sessions" ? "选择会话后可按原播放器和参数重新打开。"
                    : key == "players" ? "两种播放器分别指定路径；画质增强仅对 mpv 生效。"
                    : key == "danmaku" ? "编辑后保存，运行中的覆盖层会读取更新。"
                    : key == "accounts" ? "登录与 Cookie 检测按平台独立管理。"
                    : "只读查看本地数据；清理仅作用于缓存和日志。";
                status.ForeColor = ModernTheme.TextSecondary; status.Text = workbenchIdleHint;
            }
            SyncWorkbenchActions(); LayoutWorkbench();
        }

        private static Color PlatformAccent(string platform)
        {
            switch (platform)
            {
                case "bili": return Color.FromArgb(0x7A, 0x5C, 0xFF);
                case "huya": return Color.FromArgb(0xF5, 0xA6, 0x23);
                case "douyu": return Color.FromArgb(0xFF, 0x6B, 0x35);
                case "douyin": return Color.FromArgb(0x22, 0xD3, 0xEE);
                default: return ModernTheme.Accent;
            }
        }

        private void SetWorkbenchPlatform(string chosen)
        {
            platform = chosen; RefreshAll(); SelectWorkbenchPage(workbenchPage);
        }

        private void LaunchWorkbenchFavorite(int index, string player)
        {
            if (index < 0 || index >= currentFavorites.Count) return;
            favList.SelectedIndex = index;
            Target target = Parse(currentFavorites[index].Value, "bili");
            if (target != null) { input.Text = target.Platform + ":" + target.Room; Launch(player); }
        }

        private void LaunchWorkbenchRecent(int index, string player)
        {
            if (index < 0 || index >= currentHistory.Count) return;
            recentList.SelectedIndex = index; input.Text = platform + ":" + currentHistory[index].Room; Launch(player);
        }

        private void CancelWorkbenchLaunch()
        {
            if (currentLaunch == null) return;
            currentLaunch.Cancel(); cancelLaunch.Enabled = false; status.Text = "正在取消启动…"; SyncWorkbenchActions();
        }

        private void SyncWorkbenchActions()
        {
            if (!workbenchReady) return;
            bool launching = currentLaunch != null;
            workbenchStatusCancel.Visible = launching && workbenchPage != "favorites";
            workbenchStatusCancel.Enabled = cancelLaunch.Enabled;
            recentOpenMpv.Enabled = recentOpenPot.Enabled = !launching && recentList.SelectedIndex >= 0;
            favoritesBrowser.Enabled = recentBrowser.Enabled = !launching;
            foreach (HomeSessionRow row in workbenchHomeRows) row.Reopen.Enabled = !launching && row.Target != null;
            foreach (KeyValuePair<string, ModernButton> pair in recentPlatformButtons) pair.Value.Selected = pair.Key == platform;
        }

        private void RefreshWorkbenchSavedPreferences()
        {
            bool previous = syncingWorkbench; syncingWorkbench = true;
            try
            {
                DanmakuConfig config = DanmakuHost.LoadOrCreateConfig();
                int sc = Math.Max(0, Array.IndexOf(scIds, config.ScMode));
                int enhance = Math.Max(0, Array.IndexOf(pqIds, config.MpvQuality));
                // Source handlers save these preferences, but only when the value changes.
                if (scBox.SelectedIndex != sc) scBox.SelectedIndex = sc;
                if (enhanceBox.SelectedIndex != enhance) enhanceBox.SelectedIndex = enhance;
            }
            finally { syncingWorkbench = previous; }
        }

        /// <summary>
        /// 最新会话在前，并补充跨平台历史；每个房间只显示一条快捷入口。
        /// </summary>
        private void RefreshWorkbenchHomeSession()
        {
            if (!workbenchReady) return;
            List<PlaybackSession> recentSessions = new List<PlaybackSession>();
            List<KeyValuePair<string, HistoryEntry>> recentHistory = new List<KeyValuePair<string, HistoryEntry>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlaybackSession session in sessions)
            {
                if (session.Target != null && seen.Add(session.Target.Platform + ":" + session.Target.Room)) recentSessions.Add(session);
            }
            foreach (var pair in history)
                foreach (HistoryEntry entry in pair.Value)
                    recentHistory.Add(new KeyValuePair<string, HistoryEntry>(pair.Key, entry));
            recentHistory.Sort(delegate(KeyValuePair<string, HistoryEntry> a, KeyValuePair<string, HistoryEntry> b)
                { return string.Compare(b.Value.At, a.Value.At, StringComparison.Ordinal); });
            recentHistory.RemoveAll(delegate(KeyValuePair<string, HistoryEntry> entry)
                { return !seen.Add(entry.Key + ":" + entry.Value.Room); });
            int count = Math.Min(24, recentSessions.Count + recentHistory.Count);
            while (workbenchHomeRows.Count < Math.Max(1, count)) CreateHomeSessionRow();
            for (int i = 0; i < workbenchHomeRows.Count; i++)
            {
                HomeSessionRow row = workbenchHomeRows[i];
                row.Active = i < Math.Max(1, count);
                row.Card.Visible = row.Active;
                row.Session = i < recentSessions.Count ? recentSessions[i] : null;
                row.Target = row.Session == null ? null : row.Session.Target;
                row.State.ForeColor = ModernTheme.TextSecondary;
                if (count == 0)
                {
                    row.Title.Text = "暂无播放记录";
                    row.Meta.Text = "打开房间后，这里保留最近播放。";
                    row.State.Text = "";
                }
                else if (row.Session != null)
                {
                    PlaybackSession session = row.Session;
                    row.Title.Text = PlatformName(session.Target.Platform) + " " + session.Target.Room;
                    row.Meta.Text = (session.Player == "potplayer" ? "PotPlayer" : "mpv") + " · "
                        + (session.Target.Quality == "smooth" ? "流畅" : session.Target.Quality == "high" ? "高清" : "原画")
                        + " · " + (session.Player == "potplayer" ? "播放器默认" : session.Target.BufferMode == "low" ? "延迟更低" : "播放更稳定");
                    row.State.Text = "● " + (session.Ended ? "播放器已退出" : RoomStateLabel(session.Target.Platform, session.LiveState));
                    if (!session.Ended && session.LiveState == RoomStateKind.Live) row.State.ForeColor = ModernTheme.Success;
                }
                else if (i < count)
                {
                    var entry = recentHistory[i - recentSessions.Count];
                    row.Target = Parse(entry.Key + ":" + entry.Value.Room, entry.Key);
                    row.Title.Text = string.IsNullOrEmpty(entry.Value.Name) ? PlatformName(entry.Key) + " " + entry.Value.Room : entry.Value.Name;
                    row.Meta.Text = PlatformName(entry.Key) + " · " + entry.Value.Room;
                    row.State.Text = "最近 · " + (entry.Value.At ?? "");
                }
                row.Reopen.Enabled = row.Target != null && currentLaunch == null;
            }
            if (!workbenchLayoutBusy) LayoutWorkbenchFavorites();
        }

        /// <summary>
        /// 启动时按当前显示器工作区把窗口调成宽扁比例（第一次显示前执行，不会闪）。
        /// 尺寸规则在 <see cref="LiveHub.WorkbenchDesignSize"/>；这里只负责换算物理像素、
        /// 居中，并夹在工作区内（DpiForm 的 FitWorkingArea 仍是兜底）。
        /// 这个 Load 处理器在 DpiForm.OnLoad → InitializeDpi 之后才跑（base.OnLoad 才触发
        /// Load 事件），所以 RenderScale 已经是当前显示器的真实倍率。
        /// </summary>
        private void ApplyWorkbenchDefaultBounds()
        {
            if (form == null || form.IsDisposed) return;
            DpiForm dpiForm = form as DpiForm;
            float scale = dpiForm != null && dpiForm.RenderScale > 0.01f ? dpiForm.RenderScale : 1f;
            Rectangle work;
            try { work = Screen.FromControl(form).WorkingArea; }
            catch (Exception) { work = Screen.PrimaryScreen.WorkingArea; }
            Size design = LiveHub.WorkbenchDesignSize(
                (int)Math.Round(work.Width / scale), (int)Math.Round(work.Height / scale));
            form.ClientSize = new Size(DpiForm.Scale(design.Width, scale), DpiForm.Scale(design.Height, scale));
            int windowWidth = form.Width, windowHeight = form.Height;
            int left = work.Left + Math.Max(0, (work.Width - windowWidth) / 2);
            int top = work.Top + Math.Max(0, (work.Height - windowHeight) / 2);
            form.Location = new Point(
                Math.Max(work.Left, Math.Min(left, Math.Max(work.Left, work.Right - windowWidth))),
                Math.Max(work.Top, Math.Min(top, Math.Max(work.Top, work.Bottom - windowHeight))));
        }

        private void RefreshWorkbenchRooms()
        {
            if (!workbenchReady) return;
            List<WorkbenchRoomItem> favoriteItems = new List<WorkbenchRoomItem>();
            foreach (KeyValuePair<string, string> entry in currentFavorites)
            {
                Target target = Parse(entry.Value, "bili");
                if (target == null) continue;
                RoomStateKind kind; bool known = roomBadges.TryGetValue(target.Platform + ":" + target.Room, out kind);
                favoriteItems.Add(new WorkbenchRoomItem { Name = entry.Key, Room = target.Room,
                    Meta = PlatformName(target.Platform) + " · " + target.Room,
                    State = known ? RoomStateLabel(target.Platform, kind) : "待检测", Live = known && kind == RoomStateKind.Live,
                    Accent = PlatformAccent(target.Platform) });
            }
            List<WorkbenchRoomItem> recentItems = new List<WorkbenchRoomItem>();
            foreach (HistoryEntry entry in currentHistory)
            {
                RoomStateKind kind; bool known = roomBadges.TryGetValue(platform + ":" + entry.Room, out kind);
                recentItems.Add(new WorkbenchRoomItem { Name = string.IsNullOrEmpty(entry.Name) ? entry.Room : entry.Name,
                    Room = entry.Room, Meta = PlatformName(platform) + " · " + entry.Room + " · " + (entry.At ?? ""),
                    State = known ? RoomStateLabel(platform, kind) : "待检测", Live = known && kind == RoomStateKind.Live,
                    Accent = PlatformAccent(platform) });
            }
            bool previous = syncingWorkbench; syncingWorkbench = true;
            try
            {
                favoritesBrowser.SetItems(favoriteItems); recentBrowser.SetItems(recentItems);
                favoritesBrowser.SelectedIndex = favList.SelectedIndex; recentBrowser.SelectedIndex = recentList.SelectedIndex;
            }
            finally { syncingWorkbench = previous; }
            SyncWorkbenchActions();
        }

        private void FitWorkbenchPageScroll(Panel page)
        {
            if (page == null || page.IsDisposed || !page.AutoScroll) return;
            page.AutoScrollPosition = new Point(0, 0);
            int bottom = 0;
            foreach (Control child in page.Controls)
            {
                if (!child.Visible || child.Dock == DockStyle.Fill) continue;
                bottom = Math.Max(bottom, child.Bottom);
            }
            page.AutoScrollMinSize = bottom > page.ClientSize.Height + Px(2)
                ? new Size(0, bottom + Px(8)) : Size.Empty;
        }

        private void LayoutWorkbench()
        {
            if (!workbenchReady || workbenchLayoutBusy || ((DpiForm)form).ApplyingDpi) return;
            workbenchLayoutBusy = true; form.SuspendLayout();
            try
            {
                int width = form.ClientSize.Width, height = form.ClientSize.Height;
                int chromeHeight = Px(34); // custom title strip (the native title bar is hidden)
                // 侧栏按宽度三档收窄：窄窗把像素让给内容，宽窗保留可读的导航文字。
                int sidebar = Px(width < Px(760) ? 124 : width < Px(1000) ? 144 : 164);
                int pad = Px(width < Px(900) ? 20 : 24);
                workbenchChrome.SetBounds(0, 0, width, chromeHeight);
                int shellHeight = Math.Max(Px(160), height - chromeHeight);
                workbenchSidebar.SetBounds(0, chromeHeight, sidebar, shellHeight);
                workbenchContent.SetBounds(sidebar, chromeHeight, Math.Max(Px(300), width - sidebar), shellHeight);
                int dot = Px(18), dotGap = Px(10);
                int dotY = Math.Max(0, (chromeHeight - dot) / 2);
                workbenchChromeClose.SetBounds(Px(14), dotY, dot, dot);
                workbenchChromeMin.SetBounds(Px(14) + dot + dotGap, dotY, dot, dot);
                workbenchChromeMax.SetBounds(Px(14) + (dot + dotGap) * 2, dotY, dot, dot);
                workbenchChromeIcon.SetBounds(Px(14) + (dot + dotGap) * 3 + Px(8), Math.Max(0, (chromeHeight - Px(18)) / 2), Px(18), Px(18));
                workbenchChromeBrand.SetBounds(workbenchChromeIcon.Right + Px(8), 0, Px(80), chromeHeight);
                workbenchChromeHint.SetBounds(Math.Max(Px(60), width - Px(260)), 0, Px(240), chromeHeight);
                int sidebarWidth = workbenchSidebar.ClientSize.Width;
                // 侧栏也压扁一档：品牌区从 26→22，导航从 121→100 起排，底部提示贴底。
                workbenchBrandMark.SetBounds(Px(12), Px(22), Px(30), Px(30));
                workbenchBrand.SetBounds(Px(50), Px(24), Math.Max(Px(52), sidebarWidth - Px(58)), Px(26));
                workbenchWatchGroup.SetBounds(Px(12), Px(76), Math.Max(Px(56), sidebarWidth - Px(24)), Px(16));
                int navY = Px(100), index = 0;
                foreach (string key in new[] { "favorites", "recent", "lookup", "sessions", "players", "danmaku", "accounts", "data" })
                {
                    if (index == 4) { workbenchSettingsGroup.SetBounds(Px(12), navY + Px(10), Math.Max(Px(56), sidebarWidth - Px(24)), Px(16)); navY += Px(29); }
                    int navHeight = key == "data" ? Px(52) : Px(36);
                    workbenchNavigation[key].SetBounds(Px(8), navY, Math.Max(Px(80), sidebarWidth - Px(16)), navHeight);
                    navY += navHeight + Px(4); index++;
                }
                int footerTop = Math.Max(navY + Px(14), shellHeight - Px(56));
                workbenchFooterIcon.SetBounds(Px(12), footerTop + Px(3), Px(16), Px(16));
                workbenchFooterHint.SetBounds(Px(34), footerTop, Math.Max(Px(54), sidebarWidth - Px(42)), Px(20));
                workbenchSidebar.AutoScrollMinSize = new Size(0,
                    Math.Max(workbenchFooterHint.Bottom, workbenchFooterIcon.Bottom) + Px(10));
                int inner = Math.Max(Px(240), workbenchContent.ClientSize.Width - pad * 2);
                int contentHeight = workbenchContent.ClientSize.Height;
                // 标题与副标题原来是竖排（host 从 y=99 起）；改成同一行横排后页面从 50 起，
                // 纵向多出约 50px 给页面内容 —— 这是「上下太长」的又一处来源。
                int titleWidth = TextRenderer.MeasureText(workbenchTitle.Text, workbenchTitle.Font,
                    Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Px(6);
                titleWidth = Math.Max(Px(90), Math.Min(inner, titleWidth));
                workbenchTitle.SetBounds(pad, Px(8), titleWidth, Px(32));
                int subtitleLeft = pad + titleWidth + Px(14);
                workbenchSubtitle.SetBounds(subtitleLeft, Px(15), Math.Max(Px(60), pad + inner - subtitleLeft), Px(18));
                int statusWidth = inner - (workbenchStatusCancel.Visible ? Px(126) : 0);
                int statusText = TextRenderer.MeasureText(status.Text, status.Font,
                    new Size(Math.Max(Px(100), statusWidth), int.MaxValue), TextFormatFlags.WordBreak).Height;
                int footerHeight = Math.Max(Px(32), Math.Min(Px(54), statusText + Px(8)));
                workbenchFooter.SetBounds(pad, Math.Max(Px(180), contentHeight - footerHeight - Px(8)), inner, footerHeight);
                status.SetBounds(Px(16), 0, statusWidth - Px(16), footerHeight);
                workbenchStatusCancel.SetBounds(inner - Px(118), Math.Max(0, (footerHeight - Px(29)) / 2), Px(118), Px(29));
                int hostTop = Px(50);
                workbenchHost.SetBounds(pad, hostTop, inner, Math.Max(Px(120), workbenchFooter.Top - hostTop - Px(10)));
                foreach (Panel page in workbenchPages.Values) page.SetBounds(0, 0, workbenchHost.ClientSize.Width, workbenchHost.ClientSize.Height);
                LayoutWorkbenchFavorites(); LayoutWorkbenchRecent(); LayoutWorkbenchSessions(); LayoutWorkbenchPlayers();
                foreach (Panel page in workbenchPages.Values) page.PerformLayout();
                LayoutWorkbenchFavorites(); LayoutWorkbenchRecent(); LayoutWorkbenchSessions(); LayoutWorkbenchPlayers();
                Panel dataPage;
                if (workbenchPages.TryGetValue("data", out dataPage)) WorkbenchDataUi.Relayout(dataPage, form);
                foreach (Panel workbenchPage in workbenchPages.Values) FitWorkbenchPageScroll(workbenchPage);
                SyncWorkbenchActions();
                form.AutoScroll = false;
                form.AutoScrollMinSize = Size.Empty;
            }
            finally { form.ResumeLayout(true); workbenchLayoutBusy = false; }
        }

        private void LayoutWorkbenchFavorites()
        {
            Panel page = workbenchPages["favorites"];
            int width = Math.Max(Px(260), page.ClientSize.Width);
            int pageHeight = Math.Max(Px(220), page.ClientSize.Height);
            int pad = Px(16), gap = Px(8);
            int inner = width - pad * 2;
            int y = Px(8), x = pad;
            // Platform bar inside the launch card (draft: 30px pills, quiet help link).
            int tabGap = Px(6);
            int tabWidth = Math.Min(Px(72), Math.Max(Px(46), (inner - Px(120)) / 4));
            foreach (string p in platforms)
            {
                platformButtons[p].SetBounds(x, y, tabWidth, Px(30)); x += tabWidth + tabGap;
            }
            inputHelp.SetBounds(width - pad - Px(88), y, Px(88), Px(30));
            workbenchPlatformStrip.SetBounds(pad - Px(3), y - Px(3),
                4 * tabWidth + 3 * tabGap + Px(6), Px(36));
            y += Px(30) + Px(8);
            bool compact = inner < Px(700);
            if (!compact)
            {
                int cancelWidth = Px(72), potWidth = Px(160), mpvWidth = Px(155);
                input.SetBounds(pad, y, Math.Max(Px(120), inner - mpvWidth - potWidth - cancelWidth - gap * 3), Px(38));
                openMpv.SetBounds(input.Right + gap, y, mpvWidth, Px(38));
                openPot.SetBounds(openMpv.Right + gap, y, potWidth, Px(38));
                cancelLaunch.SetBounds(openPot.Right + gap, y, Math.Max(Px(56), width - pad - openPot.Right - gap), Px(38));
                y += Px(38) + Px(10);
            }
            else
            {
                input.SetBounds(pad, y, inner, Px(36)); y += Px(36) + Px(6);
                int cancelWidth = Px(56);
                int playWidth = Math.Max(Px(110), (inner - gap * 2 - cancelWidth) / 2);
                openMpv.SetBounds(pad, y, playWidth, Px(32)); openPot.SetBounds(openMpv.Right + gap, y, playWidth, Px(32));
                cancelLaunch.SetBounds(openPot.Right + gap, y, Math.Max(Px(48), width - pad - openPot.Right - gap), Px(32));
                y += Px(32) + Px(10);
            }
            Control[] labels = { qualityLabel, bufferLabel, danmakuLabel, scLabel, enhanceLabel };
            ComboBox[] boxes = { qualityBox, bufferBox, danmakuBox, scBox, enhanceBox };
            int columns = inner >= Px(680) ? 5 : inner >= Px(420) ? 3 : 2;
            int columnGap = Px(10), cell = (inner - columnGap * (columns - 1)) / columns;
            int labelHeight = Px(14), comboHeight = Px(27);
            for (int i = 0; i < boxes.Length; i++)
            {
                int left = pad + (i % columns) * (cell + columnGap);
                int top = y + (i / columns) * (labelHeight + Px(3) + comboHeight + Px(8));
                ((Label)labels[i]).TextAlign = ContentAlignment.MiddleLeft;
                int scopeWidth = 0;
                if (i < workbenchScopeLabels.Count)
                {
                    Label scope = workbenchScopeLabels[i];
                    scopeWidth = Math.Min(cell / 2, TextRenderer.MeasureText(scope.Text, scope.Font).Width + Px(4));
                    scope.SetBounds(left + cell - scopeWidth, top, scopeWidth, labelHeight);
                }
                labels[i].SetBounds(left, top, Math.Max(Px(40), cell - scopeWidth - Px(4)), labelHeight);
                boxes[i].SetBounds(left, top + labelHeight + Px(3), cell, comboHeight);
            }
            int rows = (boxes.Length + columns - 1) / columns;
            y += rows * (labelHeight + Px(3) + comboHeight) + (rows - 1) * Px(8) + Px(6);
            int hintWidth = Math.Max(Px(100), inner - Px(140));
            int hintHeight = Math.Max(Px(18), Math.Min(Px(34), TextRenderer.MeasureText(workbenchLaunchHint.Text,
                workbenchLaunchHint.Font, new Size(hintWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + Px(1)));
            workbenchLaunchHint.SetBounds(pad, y, hintWidth, hintHeight);
            int noteHeight = Math.Max(hintHeight, Px(24));
            danmakuSettings.SetBounds(width - pad - Px(104), y + Math.Max(0, (noteHeight - Px(24)) / 2), Px(104), Px(24));
            int launchBottom = y + noteHeight + Px(8);
            workbenchLaunch.SetBounds(0, 0, width, launchBottom);
            y = launchBottom + Px(12);
            // 收藏区标题 + 增删按钮（按钮 32→30，行距收紧）。
            favLabel.SetBounds(0, y + Px(5), Math.Max(Px(80), width - Px(228)), Px(20));
            addFav.SetBounds(width - Px(216), y, Px(104), Px(30));
            delFav.SetBounds(width - Px(104), y, Px(104), Px(30));
            y += Px(30) + Px(6);
            // 宽窗口把「正在观看 / 最近打开」放到收藏卡右侧：横向用满宽度、纵向省掉一整段
            // （用户原话「上下太长、左右宽度不够」）。窄窗口保持原来的上下堆叠。
            bool wideTail = inner >= Px(960);
            int homeWidth = wideTail ? Math.Min(Px(320), Math.Max(Px(280), inner / 4)) : width;
            int browserWidth = wideTail ? width - homeWidth - Px(16) : width;
            int hintLine = Px(18);
            int browserHeight = favoritesBrowser.CardsRowHeight;
            int availableHeight = pageHeight - y - Px(4) - hintLine - Px(4) - (wideTail ? 0 : Px(200));
            if (availableHeight >= favoritesBrowser.CardsTwoRowsHeight)
                browserHeight = availableHeight;
            favoritesBrowser.SetBounds(0, y, browserWidth, browserHeight);
            workbenchFavoritesHint.SetBounds(0, favoritesBrowser.Bottom + Px(4), browserWidth, hintLine);
            int homeBottom;
            if (wideTail) homeBottom = LayoutWorkbenchHomeBand(width - homeWidth, y, homeWidth, browserHeight);
            else homeBottom = LayoutWorkbenchHomeBand(0, workbenchFavoritesHint.Bottom + Px(10), width, Px(190));
            page.AutoScrollMinSize = new Size(0, Math.Max(workbenchFavoritesHint.Bottom, homeBottom) + Px(4));
        }

        /// <summary>「正在观看 / 最近打开」小节：标题行 + 会话卡片；返回卡片底边。窄宽度时卡片内部改成两行排布。</summary>
        private int LayoutWorkbenchHomeBand(int left, int top, int width, int height)
        {
            int sessionsWidth = Math.Min(Px(100), Math.Max(Px(76), width / 3));
            workbenchHomeIcon.SetBounds(left, top + Px(3), Px(16), Px(16));
            workbenchHomeHeader.SetBounds(left + Px(22), top, Math.Max(Px(80), width - sessionsWidth - Px(26)), Px(20));
            workbenchHomeSessions.SetBounds(left + width - sessionsWidth, top - Px(2), sessionsWidth, Px(24));
            int cardTop = top + Px(20) + Px(5);
            bool compactCard = width < Px(420);
            int cardHeight = compactCard ? Px(94) : Px(64);
            workbenchHomeList.SetBounds(left, cardTop, width, Math.Max(cardHeight, height - Px(25)));
            int visibleCount = 0;
            foreach (HomeSessionRow row in workbenchHomeRows) if (row.Active) visibleCount++;
            int contentHeight = visibleCount * (cardHeight + Px(8)) - Px(8);
            workbenchHomeList.AutoScrollMinSize = new Size(0, contentHeight);
            int rowWidth = workbenchHomeList.ClientSize.Width;
            int rowIndex = 0;
            foreach (HomeSessionRow row in workbenchHomeRows)
            {
                if (!row.Active) continue;
                width = rowWidth;
                row.Card.SetBounds(workbenchHomeList.AutoScrollPosition.X,
                    workbenchHomeList.AutoScrollPosition.Y + rowIndex++ * (cardHeight + Px(8)), width, cardHeight);
                int cardPad = Px(12), avatarSize = Px(32);
                int textLeft = cardPad + avatarSize + Px(10);
                int titleHeight = Px(19), metaHeight = Px(17);
                int reopenWidth = compactCard ? Math.Min(Px(84), width / 3) : Math.Min(Px(92), width / 4);
                if (compactCard)
                {
                    row.Avatar.SetBounds(cardPad, Px(8), avatarSize, avatarSize);
                    row.Title.SetBounds(textLeft, Px(7), Math.Max(Px(60), width - textLeft - Px(10)), titleHeight);
                    row.Meta.SetBounds(textLeft, Px(26), Math.Max(Px(60), width - textLeft - Px(10)), metaHeight);
                    row.State.SetBounds(cardPad, Px(46), Math.Max(Px(70), width - cardPad * 2), Px(18));
                    row.Reopen.SetBounds(width - cardPad - reopenWidth, Px(66), reopenWidth, Px(24));
                }
                else
                {
                    int stateWidth = Math.Min(Px(150), Math.Max(Px(96), width / 4));
                    int textWidth = Math.Max(Px(80), width - textLeft - reopenWidth - stateWidth - Px(26));
                    row.Avatar.SetBounds(cardPad, (cardHeight - avatarSize) / 2, avatarSize, avatarSize);
                    row.Title.SetBounds(textLeft, Px(9), textWidth, titleHeight);
                    row.Meta.SetBounds(textLeft, Px(29), textWidth, metaHeight);
                    row.State.SetBounds(width - cardPad - reopenWidth - Px(10) - stateWidth, Px(21), stateWidth, Px(20));
                    row.Reopen.SetBounds(width - cardPad - reopenWidth, Px(17), reopenWidth, Px(28));
                }
            }
            return workbenchHomeList.Bottom;
        }

        private void LayoutWorkbenchRecent()
        {
            Panel page = workbenchPages["recent"];
            int width = Math.Max(Px(240), page.ClientSize.Width);
            int height = Math.Max(Px(140), page.ClientSize.Height);
            int y = Px(6);
            int tabGap = Px(8), tabWidth = Math.Min(Px(76), Math.Max(Px(58), width / 6));
            int tabsWidth = platforms.Length * tabWidth + (platforms.Length - 1) * tabGap;
            int actionWidth = Math.Max(Px(120), Math.Min(Px(210), (width - Px(10)) / 2));
            bool singleRow = width >= tabsWidth + Px(24) + actionWidth * 2 + Px(10);
            int x = 0;
            foreach (string p in platforms) { recentPlatformButtons[p].SetBounds(x, y, tabWidth, Px(32)); x += tabWidth + tabGap; }
            recentPlatformStrip.SetBounds(-Px(2), y - Px(2), tabsWidth + Px(4), Px(36));
            if (singleRow)
            {
                recentOpenMpv.SetBounds(width - actionWidth * 2 - Px(10), y, actionWidth, Px(32));
                recentOpenPot.SetBounds(width - actionWidth, y, actionWidth, Px(32));
                y += Px(32) + Px(8);
            }
            else
            {
                y += Px(32) + Px(8);
                recentOpenMpv.SetBounds(0, y, actionWidth, Px(32));
                recentOpenPot.SetBounds(actionWidth + Px(10), y, actionWidth, Px(32));
                y += Px(32) + Px(8);
            }
            // 标题行与说明并排，省掉一整行；窄窗自动省略后半句（AutoEllipsis）。
            int labelWidth = Math.Min(Px(430), Math.Max(Px(160), width / 2));
            recentLabel.SetBounds(0, y, labelWidth, Px(24));
            workbenchRecentHint.SetBounds(labelWidth + Px(12), y, Math.Max(Px(80), width - labelWidth - Px(12)), Px(24));
            y += Px(24) + Px(8);
            // The record list owns its own scrollbar; the page itself must not
            // also grow, otherwise wheel input at the list end scrolls the whole
            // page down into empty space.
            page.AutoScroll = false;
            page.AutoScrollMinSize = Size.Empty;
            recentBrowser.SetBounds(0, y, width, Math.Max(Px(150), height - y - Px(2)));
        }

        private void LayoutWorkbenchSessions()
        {
            Panel page = workbenchPages["sessions"];
            int width = Math.Max(Px(240), page.ClientSize.Width);
            int height = Math.Max(Px(140), page.ClientSize.Height);
            int y = Px(2);
            int reopenWidth = Math.Min(Px(190), Math.Max(Px(130), width / 4));
            sessionsLabel.SetBounds(0, y, Math.Max(Px(90), width - reopenWidth - Px(12)), Px(30));
            reopen.SetBounds(width - reopenWidth, y, reopenWidth, Px(30));
            y += Px(30) + Px(4);
            int hintWidth = Math.Max(Px(80), width - Px(12));
            int hintHeight = Math.Max(Px(18), Math.Min(Px(44), TextRenderer.MeasureText(workbenchSessionHint.Text,
                workbenchSessionHint.Font, new Size(hintWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + Px(2)));
            workbenchSessionHint.SetBounds(0, y, hintWidth, hintHeight);
            y += hintHeight + Px(8);
            sessionsList.SetBounds(0, y, width, Math.Max(Px(150), height - y - Px(2)));
            if (sessionsList.Width > 2 && sessionsList.Height > 2)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = ModernTheme.RoundRect(
                    new Rectangle(0, 0, sessionsList.Width, sessionsList.Height), DpiLayout.S(sessionsList, 8)))
                {
                    Region previous = sessionsList.Region;
                    sessionsList.Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            }
            if (workbenchSessionsEmpty != null)
            {
                workbenchSessionsEmpty.SetBounds(Px(8), sessionsList.Top + Px(80), Math.Max(Px(120), width - Px(16)), Px(64));
                workbenchSessionsEmpty.Visible = sessionsList.Items.Count == 0;
                workbenchSessionsEmpty.BringToFront();
            }
            LayoutSessionColumns(); page.AutoScrollMinSize = new Size(0, sessionsList.Bottom + Px(4));
        }

        private void LayoutWorkbenchPlayers()
        {
            Panel page = workbenchPages["players"];
            int width = Math.Max(Px(240), page.ClientSize.Width);
            int pad = Px(16);
            int inner = Math.Max(Px(200), width - pad * 2);
            playerLabel.SetBounds(pad, Px(12), inner, Px(26));
            playerBox.SetBounds(pad, Px(46), Math.Min(Px(120), Math.Max(Px(90), inner / 3)), Px(28));
            pickPlayer.SetBounds(playerBox.Right + Px(10), Px(44), Math.Min(Px(140), Math.Max(Px(90), inner / 3)), Px(32));
            int pathHeight = Math.Max(Px(40), TextRenderer.MeasureText(playerHint.Text, playerHint.Font,
                new Size(Math.Max(Px(100), inner), int.MaxValue), TextFormatFlags.WordBreak).Height + Px(8));
            pathHeight = Math.Min(Px(88), pathHeight);
            playerHint.SetBounds(pad, Px(84), inner, pathHeight);
            int helpHeight = Math.Max(Px(26), Math.Min(Px(46), TextRenderer.MeasureText(workbenchPlayerHelp.Text,
                workbenchPlayerHelp.Font, new Size(Math.Max(Px(100), inner), int.MaxValue), TextFormatFlags.WordBreak).Height + Px(4)));
            workbenchPlayerHelp.SetBounds(pad, playerHint.Bottom + Px(8), inner, helpHeight);
            workbenchPlayerCard.SetBounds(0, 0, width, workbenchPlayerHelp.Bottom + Px(12));
            int columns = inner >= Px(760) ? 4 : inner >= Px(600) ? 3 : 2;
            int gap = Px(16), cell = (inner - gap * (columns - 1)) / columns;
            ComboBox[] boxes = { workbenchQuality, workbenchBuffer, workbenchDanmaku, workbenchEnhance };
            int prefTop = workbenchPlayerCard.Bottom + Px(14);
            int prefHeight = Px(16) + ((boxes.Length + columns - 1) / columns) * Px(76);
            workbenchPlayerPreferences.SetBounds(0, prefTop, width, prefHeight);
            for (int i = 0; i < boxes.Length; i++)
            {
                // 说明和下拉都是 workbenchPlayerPreferences 的子控件，坐标相对它（不是页面）。
                int left = pad + (i % columns) * (cell + gap);
                int top = Px(12) + (i / columns) * Px(76);
                workbenchPlayerCaptions[i].SetBounds(left, top, cell, Px(22));
                boxes[i].SetBounds(left, top + Px(26), cell, Px(30));
            }
            page.AutoScrollMinSize = new Size(0, workbenchPlayerPreferences.Bottom + Px(8));
        }
    }

    /// <summary>
    /// 工作台默认窗口尺寸（96 DPI 逻辑单位）的纯函数：按可用工作区取「宽扁」，
    /// 左右各留 20、上下各留 16，宽上限 1400、高上限 880，且高不超过宽的 72%。
    /// 这样 1280×752 的逻辑工作区 → 1240×720（1.72），不会再出现 1024×866（1.18）
    /// 那种底部压到任务栏下面的竖长窗口。真实启动时由 ApplyWorkbenchDefaultBounds
    /// 乘 RenderScale 落成物理尺寸；测试用合成工作区直接断言规则。
    /// </summary>
    internal static Size WorkbenchDesignSize(int workWidth, int workHeight)
    {
        const int minWidth = 760, minHeight = 520, maxWidth = 1400, maxHeight = 880;
        int width = Math.Min(maxWidth, workWidth - 40);
        int height = Math.Min(maxHeight, workHeight - 32);
        if (width < minWidth) width = Math.Min(workWidth, minWidth);
        if (height < minHeight) height = Math.Min(workHeight, minHeight);
        int ratioCap = width * 72 / 100;
        if (height > ratioCap) height = Math.Max(Math.Min(workHeight, minHeight), ratioCap);
        return new Size(Math.Max(320, width), Math.Max(240, height));
    }
}
