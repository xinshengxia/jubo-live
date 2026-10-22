using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// An ordinary page inside the launcher, with the same config and controls as the
// existing dialog. It deliberately does not create or reparent another DpiForm.
internal static class WorkbenchDanmakuUi
{
    internal static Control Build(Form owner, Action saved)
    {
        return new SettingsPage(owner, saved);
    }

    internal static void Refresh(Control view)
    {
        SettingsPage page = view as SettingsPage;
        if (page != null) { page.RefreshFromConfig(false); return; }
        if (view != null) foreach (Control child in view.Controls) Refresh(child);
    }

    private sealed class Binding
    {
        internal string Name;
        internal bool Dirty;
        internal Control Control;
        internal Action<DanmakuConfig> Load, Write;
    }

    private sealed class SettingsPage : Panel
    {
        private readonly Form owner;
        private readonly Action saved;
        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Dictionary<string, object> extra = new Dictionary<string, object>();
        private readonly DanmakuSettingsPages pages = new DanmakuSettingsPages();
        private readonly ModernLabel status = new ModernLabel();
        private readonly ToolTip tips = new ToolTip();
        private readonly List<FlowLayoutPanel> grids = new List<FlowLayoutPanel>();
        private bool loading, arranging;
        private DanmakuConfig loadedConfig;
        private DanmakuConfig moreWorking, moreBaseline;
        private Action moreApply;
        private Action<DanmakuConfig> moreReload;

        internal SettingsPage(Form owner, Action saved)
        {
            this.owner = owner; this.saved = saved;
            Name = "workbenchDanmakuSettings"; Dock = DockStyle.Fill;
            Margin = Padding.Empty; Padding = Padding.Empty;
            DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
            pages.Dock = DockStyle.Fill; pages.Name = "workbenchDanmakuCategories";
            TableLayoutPanel footer = new TableLayoutPanel { Name = "workbenchDanmakuActions", Dock = DockStyle.Bottom,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1,
                Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ModernButton save = Button("保存弹幕设置", "workbenchSaveDanmaku"); save.Style = ModernButton.Kind.Primary;
            ModernButton reload = Button("重新读取", "workbenchReloadDanmaku");
            ModernButton more = Button("更多弹幕设置…", "workbenchMoreDanmaku");
            more.Visible = false;
            save.Click += delegate { SaveChanges(true); };
            reload.Click += delegate { extra.Clear(); foreach (Binding item in bindings) item.Dirty = false; RefreshFromConfig(true); ModernTheme.Apply(DanmakuHost.LoadOrCreateConfig().DarkMode); status.Text = "已读取已保存设置。"; };
            more.Click += delegate { EditMore(); };
            status.Name = "workbenchDanmakuStatus"; status.AutoSize = true; status.TextAlign = ContentAlignment.MiddleLeft;
            status.Margin = new Padding(0, 8, 8, 8); status.Text = "保存后，正在运行的覆盖层会自动读取更新。";
            status.MaximumSize = new Size(430, 0); status.Dock = DockStyle.Fill;
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(reload, 1, 0);
            footer.Controls.Add(more, 2, 0);
            footer.Controls.Add(save, 3, 0);
            Controls.Add(pages); Controls.Add(footer);

            FlowLayoutPanel display = Page("显示与内容", "workbenchDanmakuDisplay");
            FlowLayoutPanel sc = Page("SC 醒目留言", "workbenchDanmakuSc");
            FlowLayoutPanel filter = Page("过滤与开关", "workbenchDanmakuFilter");
            Panel morePage = pages.AddPage("更多弹幕设置"); morePage.Name = "workbenchDanmakuMore";
            FlowLayoutPanel ui = Page("界面与热键", "workbenchDanmakuInterface");

            AddCombo(display, "FontScalePercent", "弹幕字号", Objects(60, 80, 100, 130, 170),
                new[] { "超小", "小", "标准（同 B站）", "大", "超大" },
                delegate(DanmakuConfig c) { return c.FontScalePercent; }, delegate(DanmakuConfig c, object v) { c.FontScalePercent = Int(v); });
            AddCombo(display, "SpeedPercent", "弹幕速度", Objects(60, 80, 100, 130, 170),
                new[] { "很慢（13 秒）", "慢（10 秒）", "标准（8 秒）", "快（6 秒）", "很快（4.7 秒）" },
                delegate(DanmakuConfig c) { return c.SpeedPercent; }, delegate(DanmakuConfig c, object v) { c.SpeedPercent = Int(v); });
            AddCombo(display, "OpacityPercent", "不透明度", Objects(100, 75, 50, 25), new[] { "100%", "75%", "50%", "25%" },
                delegate(DanmakuConfig c) { return c.OpacityPercent; }, delegate(DanmakuConfig c, object v) { c.OpacityPercent = Int(v); });
            AddCombo(display, "DisplayAreaPercent", "显示区域", Objects(100, 75, 50, 25), new[] { "全屏", "3/4 屏", "半屏", "1/4 屏" },
                delegate(DanmakuConfig c) { return c.DisplayAreaPercent; }, delegate(DanmakuConfig c, object v) { c.DisplayAreaPercent = Int(v); });
            AddCombo(display, "DanmakuDelayMs", "弹幕延迟补偿", Objects(0, 2000, 2500, 3000, 3500, 4000, 5000),
                new[] { "关闭（不补偿）", "2.0 秒", "2.5 秒", "3.0 秒", "3.5 秒", "4.0 秒", "5.0 秒" },
                delegate(DanmakuConfig c) { return c.DanmakuDelayMs; }, delegate(DanmakuConfig c, object v) { c.DanmakuDelayMs = Int(v); });
            AddCombo(display, "MaxOnScreen", "弹幕密度", Objects(400, 150, 80, 40), new[] { "不限", "正常", "较少", "最少" },
                delegate(DanmakuConfig c) { return c.MaxOnScreen; }, delegate(DanmakuConfig c, object v) { c.MaxOnScreen = Int(v); });
            AddCombo(display, "DuplicateMode", "重复弹幕", Objects(0, 1, 2), new[] { "逐条显示", "仅合并计数", "合并并适度放大" },
                delegate(DanmakuConfig c) { return !c.BlockDuplicate ? 0 : c.EnlargeMergedDanmaku ? 2 : 1; },
                delegate(DanmakuConfig c, object v) { c.BlockDuplicate = Int(v) != 0; c.EnlargeMergedDanmaku = Int(v) == 2; });
            AddCombo(display, "DuplicateWindowMs", "合并时间", Objects(3000, 5000, 8000, 10000), new[] { "3 秒", "5 秒", "8 秒", "10 秒" },
                delegate(DanmakuConfig c) { return c.DuplicateWindowMs; }, delegate(DanmakuConfig c, object v) { c.DuplicateWindowMs = Int(v); });
            string[] fontNames = UiFonts.Available ? new[] { "鸿蒙", "微软雅黑", "黑体", "宋体" } : new[] { "微软雅黑", "黑体", "宋体" };
            AddCombo(display, "FontName", "字体", fontNames, fontNames,
                delegate(DanmakuConfig c) { return c.FontName; }, delegate(DanmakuConfig c, object v) { c.FontName = (string)v; });

            AddCombo(sc, "ScMode", "SC 醒目留言", new object[] { "full", "compact", "badge", "off" },
                new[] { "完整卡片", "折叠成一行", "只留角标", "不显示" },
                delegate(DanmakuConfig c) { return c.ScMode; }, delegate(DanmakuConfig c, object v) { c.ScMode = (string)v; });
            AddCombo(sc, "ScMaxStack", "超过几张启用滚动", Objects(1, 2, 3, 4, 6, 12, 0),
                new[] { "超过 1 张", "超过 2 张", "超过 3 张", "超过 4 张", "超过 6 张", "超过 12 张", "不限（放不下才滚）" },
                delegate(DanmakuConfig c) { return c.ScMaxStack; }, delegate(DanmakuConfig c, object v) { c.ScMaxStack = Int(v); });
            AddCombo(sc, "ScCorner", "SC 贴哪个角", new object[] { "tr", "br", "tl", "bl" },
                new[] { "右上（同 B站）", "右下", "左上", "左下" },
                delegate(DanmakuConfig c) { return c.ScCorner; }, delegate(DanmakuConfig c, object v) { c.ScCorner = (string)v; });
            AddCheck(sc, "ScClickable", "点 SC 面板切换形态", delegate(DanmakuConfig c) { return c.ScClickable; }, delegate(DanmakuConfig c, bool v) { c.ScClickable = v; });
            AddNote(sc, "超过指定张数会启用滚动，不会据此丢弃旧卡片。SC 显示模式与我的收藏页同步。", "workbenchScHint");

            AddCheck(filter, "Enabled", "弹幕总开关", delegate(DanmakuConfig c) { return c.Enabled; }, delegate(DanmakuConfig c, bool v) { c.Enabled = v; });
            AddCheck(filter, "StrokeEnabled", "黑色描边", delegate(DanmakuConfig c) { return c.StrokeEnabled; }, delegate(DanmakuConfig c, bool v) { c.StrokeEnabled = v; });
            AddCheck(filter, "ShowMedal", "弹幕前显示粉丝勋章", delegate(DanmakuConfig c) { return c.ShowMedal; }, delegate(DanmakuConfig c, bool v) { c.ShowMedal = v; });
            AddCheck(filter, "ShowUserName", "弹幕前显示用户名", delegate(DanmakuConfig c) { return c.ShowUserName; }, delegate(DanmakuConfig c, bool v) { c.ShowUserName = v; });
            AddCheck(filter, "ShowUserLevel", "显示用户等级 / 舰长", delegate(DanmakuConfig c) { return c.ShowUserLevel; }, delegate(DanmakuConfig c, bool v) { c.ShowUserLevel = v; });
            AddCheck(filter, "ShowEmoticon", "显示表情图片", delegate(DanmakuConfig c) { return c.ShowEmoticon; }, delegate(DanmakuConfig c, bool v) { c.ShowEmoticon = v; });
            AddCheck(filter, "AutoStart", "打开直播自动带起弹幕", delegate(DanmakuConfig c) { return c.AutoStart; }, delegate(DanmakuConfig c, bool v) { c.AutoStart = v; });
            AddCheck(filter, "HideWhenNotForeground", "「始终最前」时非前台隐藏", delegate(DanmakuConfig c) { return c.HideWhenNotForeground; }, delegate(DanmakuConfig c, bool v) { c.HideWhenNotForeground = v; });
            AddCheck(filter, "AlwaysOnTop", "始终压在所有窗口之上", delegate(DanmakuConfig c) { return c.AlwaysOnTop; }, delegate(DanmakuConfig c, bool v) { c.AlwaysOnTop = v; });
            AddCheck(filter, "AntiBlockSubtitle", "防挡字幕（底部留 20%）", delegate(DanmakuConfig c) { return c.AntiBlockSubtitle; }, delegate(DanmakuConfig c, bool v) { c.AntiBlockSubtitle = v; });
            AddCheck(filter, "ScaleWithWindow", "字号随画面大小缩放", delegate(DanmakuConfig c) { return c.ScaleWithWindow; }, delegate(DanmakuConfig c, bool v) { c.ScaleWithWindow = v; });
            AddCheck(filter, "SolidText", "实心字芯（透明度仅影响 SC 背景）", delegate(DanmakuConfig c) { return c.SolidText; }, delegate(DanmakuConfig c, bool v) { c.SolidText = v; });
            AddKeywords(filter);

            AddCombo(ui, "UiFontKey", "界面字体", new object[] { "auto", "harmony", "yahei-ui", "yahei", "system" },
                new[] { "自动（鸿蒙优先）", "鸿蒙 HarmonyOS", "微软雅黑 UI", "微软雅黑", "系统默认" },
                delegate(DanmakuConfig c) { return c.UiFontKey; }, delegate(DanmakuConfig c, object v) { c.UiFontKey = Convert.ToString(v, CultureInfo.InvariantCulture); });
            AddCombo(ui, "UiFontPercent", "界面字体大小", Objects(85, 100, 115, 130),
                new[] { "小（85%）", "标准（100%）", "大（115%）", "超大（130%）" },
                delegate(DanmakuConfig c) { return c.UiFontPercent; }, delegate(DanmakuConfig c, object v) { c.UiFontPercent = Int(v); });
            AddCombo(ui, "MpvQuality", "mpv 画质档位", new object[] { "off", "light", "high" },
                new[] { "无（不增强）", "轻度（抗振铃）", "高质量" },
                delegate(DanmakuConfig c) { return c.MpvQuality; }, delegate(DanmakuConfig c, object v) { c.MpvQuality = (string)v; c.MpvEnhance = false; });
            AddCheck(ui, "DarkMode", "深色模式（启动器与设置页）", delegate(DanmakuConfig c) { return c.DarkMode; }, delegate(DanmakuConfig c, bool v) { c.DarkMode = v; });
            ModernButton restart = Button("重启启动器", "workbenchRestartLauncher");
            restart.Style = ModernButton.Kind.Secondary;
            restart.Margin = new Padding(0, DpiLayout.S(this, 4), DpiLayout.S(this, 8), DpiLayout.S(this, 8));
            restart.Click += delegate
            {
                if (!SaveChanges(false)) return;
                try
                {
                    System.Diagnostics.ProcessStartInfo info = LiveHub.CreateLauncherRestartInfo(Environment.GetCommandLineArgs());
                    System.Diagnostics.Process.Start(info);
                    owner.BeginInvoke((MethodInvoker)delegate { owner.Close(); });
                }
                catch (Exception ex) { status.Text = "重启失败：" + ex.Message; }
            };
            ui.Controls.Add(restart);
            AddNote(ui, "修改界面字体或字号后，点“重启启动器”即可生效；画质增强只影响 mpv，PotPlayer 使用播放器自身的渲染设置。", "workbenchMpvQualityHint");
            AddNote(ui, "热键：Ctrl+Alt+D 开关弹幕；Ctrl+Alt+S 切换 SC；Ctrl+Alt+O 隐藏覆盖层。\r\n"
                + "Ctrl+Alt+↑ / ↓ 调字号；← / → 调显示区域；PgUp / PgDn 调速度；[ / ] 调延迟补偿。\r\n"
                + "界面字体：HarmonyOS Sans SC（许可随 fonts/ 目录携带）。", "workbenchDanmakuHotkeys");
            ModernTheme.Changed += ApplyTheme; Disposed += delegate { ModernTheme.Changed -= ApplyTheme; tips.Dispose(); };
            SizeChanged += delegate { Arrange(); };
            VisibleChanged += delegate { if (Visible) RefreshFromConfig(false); };
            ApplyTheme(); RefreshFromConfig(true);
            EmbedMoreSettings();
        }

        private void EmbedMoreSettings()
        {
            try
            {
                moreWorking = (loadedConfig ?? DanmakuHost.LoadOrCreateConfig()).Clone();
                moreBaseline = moreWorking.Clone();
                Control host = DanmakuFeatureSettings.BuildEmbedded(moreWorking, out moreApply, out moreReload);
                host.Dock = DockStyle.Fill;
                Control[] pagesFound = Controls.Find("workbenchDanmakuMore", true);
                Panel page = pagesFound.Length > 0 ? pagesFound[0] as Panel : null;
                if (page == null) { host.Dispose(); return; }
                page.Controls.Add(host); host.Visible = true; BindMoreDirty(host);
            }
            catch (Exception ex) { Diagnostics.Report("内嵌更多弹幕设置", ex); moreApply = null; moreReload = null; }
        }

        private void BindMoreDirty(Control parent)
        {
            EventHandler changed = delegate { if (!loading) status.Text = "有未保存的更改。"; };
            CheckBox check = parent as CheckBox; if (check != null) check.CheckedChanged += changed;
            ComboBox choice = parent as ComboBox; if (choice != null) choice.SelectedIndexChanged += changed;
            TextBox text = parent as TextBox; if (text != null) text.TextChanged += changed;
            Button button = parent as Button; if (button != null) button.Click += changed;
            if (parent is DanmakuSafeAreaCanvas) parent.MouseUp += delegate { if (!loading) status.Text = "有未保存的更改。"; };
            foreach (Control child in parent.Controls) BindMoreDirty(child);
        }

        private Dictionary<string, object> MoreChanges()
        {
            Dictionary<string, object> changed = new Dictionary<string, object>();
            if (moreApply == null || moreWorking == null || moreBaseline == null) return changed;
            moreApply();
            JavaScriptSerializer json = new JavaScriptSerializer();
            foreach (FieldInfo field in typeof(DanmakuConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object value = field.GetValue(moreWorking);
                if (json.Serialize(value) != json.Serialize(field.GetValue(moreBaseline))) changed[field.Name] = value;
            }
            return changed;
        }

        private void RefreshMore(DanmakuConfig latest, bool force)
        {
            if (moreReload == null || moreWorking == null) return;
            Dictionary<string, object> pending = force ? new Dictionary<string, object>() : MoreChanges();
            moreBaseline = latest.Clone();
            foreach (FieldInfo field in typeof(DanmakuConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(moreWorking, field.GetValue(moreBaseline));
            foreach (KeyValuePair<string, object> pair in pending)
                typeof(DanmakuConfig).GetField(pair.Key).SetValue(moreWorking, pair.Value);
            bool previous = loading; loading = true;
            try { moreReload(moreWorking); }
            finally { loading = previous; }
        }

        private static object[] Objects(params int[] values)
        {
            object[] result = new object[values.Length]; for (int i = 0; i < values.Length; i++) result[i] = values[i]; return result;
        }
        private static int Int(object value) { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }

        private ModernButton Button(string text, string name)
        {
            return new ModernButton { Text = text, Name = name, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(94, 32),
                Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 8) };
        }

        private FlowLayoutPanel Page(string title, string name)
        {
            Panel page = pages.AddPage(title); page.Name = name;
            Panel scroll = new Panel { Name = name + "Scroll", Dock = DockStyle.Fill, AutoScroll = true,
                Padding = Padding.Empty }; page.Controls.Add(scroll);
            DanmakuCard card = new DanmakuCard { Name = name + "Card", Dock = DockStyle.Top, AutoSize = false,
                Margin = Padding.Empty, Padding = new Padding(DpiLayout.S(this, 16), DpiLayout.S(this, 12), DpiLayout.S(this, 16), DpiLayout.S(this, 14)) };
            scroll.Controls.Add(card);
            ModernLabel heading = new ModernLabel { Text = title, Dock = DockStyle.None, AutoSize = true };
            DpiLayout.SetDesignFont(heading, new Font(ModernTheme.UiFont.FontFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel));
            FlowLayoutPanel grid = new FlowLayoutPanel { Name = name + "Grid", Dock = DockStyle.None,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
                Margin = Padding.Empty, Padding = Padding.Empty };
            card.Controls.Add(heading); card.Controls.Add(grid);
            grids.Add(grid);
            Action layout = delegate
            {
                int pad = DpiLayout.S(this, 16);
                heading.Location = new Point(pad, DpiLayout.S(this, 12));
                int width = Math.Max(DpiLayout.S(this, 160), card.ClientSize.Width - pad * 2);
                grid.MaximumSize = new Size(width, 0);
                grid.Width = width;
                grid.Location = new Point(pad, heading.Bottom + DpiLayout.S(this, 10));
                card.Height = grid.Bottom + DpiLayout.S(this, 14);
                scroll.AutoScrollMinSize = new Size(0, card.Height + DpiLayout.S(this, 12));
            };
            scroll.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(scroll); };
            scroll.SizeChanged += delegate { Arrange(); layout(); };
            grid.SizeChanged += delegate { layout(); };
            heading.SizeChanged += delegate { layout(); };
            layout();
            return grid;
        }

        private void AddCombo(FlowLayoutPanel grid, string name, string caption, object[] values, string[] labels,
            Func<DanmakuConfig, object> read, Action<DanmakuConfig, object> write)
        {
            Panel field = new Panel { Name = "workbenchField_" + name, Size = new Size(260, 62), Margin = new Padding(0, 0, 14, 10) };
            ModernLabel label = new ModernLabel { Text = caption, AutoSize = true, Dock = DockStyle.Top,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft };
            ModernComboBox box = new ModernComboBox { Name = "workbenchSetting_" + name,
                AccessibleName = caption, Dock = DockStyle.Bottom };
            List<object> choices = new List<object>(values); box.Items.AddRange(labels);
            field.Controls.Add(box); field.Controls.Add(label); grid.Controls.Add(field);
            Binding binding = new Binding { Name = name, Control = box };
            binding.Load = delegate(DanmakuConfig config)
            {
                object value = read(config); int index = choices.IndexOf(value);
                if (index < 0)
                {
                    choices.Add(value); box.Items.Add(Convert.ToString(value, CultureInfo.InvariantCulture) + "（当前自定义值）"); index = choices.Count - 1;
                }
                box.SelectedIndex = index;
            };
            binding.Write = delegate(DanmakuConfig config) { if (box.SelectedIndex >= 0) write(config, choices[box.SelectedIndex]); };
            box.SelectedIndexChanged += delegate { if (!loading) Changed(binding); };
            tips.SetToolTip(box, caption); bindings.Add(binding);
        }

        private void AddCheck(FlowLayoutPanel grid, string name, string caption, Func<DanmakuConfig, bool> read, Action<DanmakuConfig, bool> write)
        {
            ModernCheckBox box = new ModernCheckBox { Name = "workbenchSetting_" + name, Text = caption,
                AccessibleName = caption, AutoSize = false, Size = new Size(260, 30), Margin = new Padding(0, 0, 14, 10) };
            Binding binding = new Binding { Name = name, Control = box };
            binding.Load = delegate(DanmakuConfig config) { box.Checked = read(config); };
            binding.Write = delegate(DanmakuConfig config) { write(config, box.Checked); };
            box.CheckedChanged += delegate
            {
                if (!loading)
                {
                    Changed(binding);
                    if (name == "DarkMode") ModernTheme.Apply(box.Checked);
                }
            };
            tips.SetToolTip(box, caption); bindings.Add(binding); grid.Controls.Add(box);
        }

        private void AddKeywords(FlowLayoutPanel grid)
        {
            Panel field = new Panel { Name = "workbenchKeywordField", Size = new Size(540, 120), Margin = new Padding(0, 8, 0, 10), Tag = "wide" };
            ModernLabel label = new ModernLabel { Text = "屏蔽词（每行一个；re: 开头为正则）", AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
            ModernInput input = new ModernInput(true) { Name = "workbenchSetting_BlockKeywords", Dock = DockStyle.Fill };
            input.Inner.ScrollBars = ScrollBars.Vertical; input.Inner.AcceptsReturn = true;
            field.Controls.Add(input); field.Controls.Add(label); grid.Controls.Add(field);
            Binding binding = new Binding { Name = "BlockKeywords", Control = input };
            binding.Load = delegate(DanmakuConfig config) { input.Text = string.Join(Environment.NewLine, config.BlockKeywords ?? new string[0]); };
            binding.Write = delegate(DanmakuConfig config) { config.BlockKeywords = input.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries); };
            input.Inner.TextChanged += delegate { if (!loading) Changed(binding); };
            bindings.Add(binding);
        }

        private void AddNote(FlowLayoutPanel grid, string text, string name)
        {
            ModernLabel label = new ModernLabel { Name = name, Text = text, AutoSize = true, Tag = "wide",
                Margin = new Padding(0, 9, 0, 12), MaximumSize = new Size(540, 0), TextAlign = ContentAlignment.MiddleLeft };
            grid.Controls.Add(label);
        }

        private void Changed(Binding binding)
        {
            binding.Dirty = true; status.Text = "有未保存的更改。";
        }

        private void Arrange()
        {
            if (arranging || IsDisposed) return;
            arranging = true;
            try
            {
                foreach (FlowLayoutPanel grid in grids)
                {
                    int available = Math.Max(DpiLayout.S(this, 180), grid.ClientSize.Width);
                    int gap = DpiLayout.S(this, 14);
                    int columns = available >= DpiLayout.S(this, 600) ? 2 : 1;
                    int width = Math.Max(DpiLayout.S(this, 150), (available - gap * columns) / columns);
                    foreach (Control control in grid.Controls)
                    {
                        bool wide = object.Equals(control.Tag, "wide");
                        int target = Math.Max(1, wide ? available - gap : width);
                        Label label = control as Label;
                        if (label != null) label.MaximumSize = new Size(target, 0);
                        else control.Width = target;
                    }
                }
            }
            finally { arranging = false; }
        }

        private void ApplyTheme()
        {
            BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Text;
            foreach (FlowLayoutPanel grid in grids)
            {
                grid.BackColor = ModernTheme.Card;
                if (grid.Parent != null) grid.Parent.BackColor = ModernTheme.Card;
            }
            Invalidate(true);
        }

        internal void RefreshFromConfig(bool force)
        {
            if (IsDisposed) return;
            DanmakuConfig config = DanmakuHost.LoadOrCreateConfig(); loadedConfig = config; loading = true;
            try
            {
                foreach (Binding item in bindings)
                    if (force || !item.Dirty) { item.Load(config); if (force) item.Dirty = false; }
            }
            finally { loading = false; }
            RefreshMore(config, force);
            Arrange();
        }

        private void ApplyPending(DanmakuConfig config)
        {
            foreach (var pair in extra)
            {
                FieldInfo field = typeof(DanmakuConfig).GetField(pair.Key, BindingFlags.Instance | BindingFlags.Public);
                if (field != null) field.SetValue(config, pair.Value);
            }
            foreach (Binding binding in bindings) if (binding.Dirty) binding.Write(config);
        }

        internal bool SaveChanges(bool showMessages)
        {
            try
            {
                DanmakuConfig config = DanmakuHost.LoadOrCreateConfig();
                ApplyPending(config);
                foreach (KeyValuePair<string, object> pair in MoreChanges())
                    typeof(DanmakuConfig).GetField(pair.Key).SetValue(config, pair.Value);
                config.Normalize();
                if (!config.Save(DanmakuHost.ConfigPath()))
                {
                    status.Text = "设置未保存，请检查文件权限或日志。";
                    if (showMessages) MessageBox.Show(owner, status.Text, "弹幕与 SC 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                extra.Clear(); foreach (Binding binding in bindings) binding.Dirty = false;
                RefreshFromConfig(true); ModernTheme.Apply(config.DarkMode); status.Text = "设置已保存，运行中的覆盖层将自动更新。";
                if (saved != null) saved(); return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Report("工作台弹幕设置保存", ex); status.Text = "设置未保存：" + ex.Message;
                if (showMessages) MessageBox.Show(owner, status.Text, "弹幕与 SC 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void EditMore()
        {
            DanmakuConfig working = DanmakuHost.LoadOrCreateConfig(); ApplyPending(working);
            DanmakuConfig before = working.Clone(); DanmakuFeatureSettings.Show(owner, working);
            JavaScriptSerializer json = new JavaScriptSerializer(); bool changed = false;
            foreach (FieldInfo field in typeof(DanmakuConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object value = field.GetValue(working);
                if (json.Serialize(value) == json.Serialize(field.GetValue(before))) continue;
                extra[field.Name] = value; changed = true;
            }
            if (changed) status.Text = "更多弹幕选项已调整，点击“保存弹幕设置”后生效。";
        }
    }
}

internal sealed class DanmakuCard : Panel
{
    internal DanmakuCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ModernTheme.Changed += RefreshTheme;
    }

    private void RefreshTheme() { if (!IsDisposed) Invalidate(); }
    protected override void Dispose(bool disposing)
    { if (disposing) ModernTheme.Changed -= RefreshTheme; base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : ModernTheme.Surface);
        if (Width < 2 || Height < 2) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (System.Drawing.Drawing2D.GraphicsPath path = ModernTheme.RoundRect(box, DpiLayout.S(this, 10)))
        using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
        using (Pen pen = new Pen(ModernTheme.Border))
        {
            g.FillPath(fill, path); g.DrawPath(pen, path);
        }
    }
}
