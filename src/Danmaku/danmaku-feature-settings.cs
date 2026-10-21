using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal sealed class DanmakuFeatureSettings
{
    internal static Form Build(DanmakuConfig config)
    { Action ignored; return BuildCore(config, false, out ignored, false); }
    internal static Form BuildPreview(DanmakuConfig config)
    { Action ignored; return BuildCore(config, true, out ignored, false); }
    internal static Form Build(DanmakuConfig config, out Action applyAll)
    { return BuildCore(config, false, out applyAll, false); }
    internal static Form BuildFlat(DanmakuConfig config, out Action applyAll)
    { return BuildCore(config, false, out applyAll, true); }
    private static Form BuildCore(DanmakuConfig config, bool preview, out Action applyAll, bool flat)
    {
        Control ignored; Action<DanmakuConfig> ignoredReload;
        return BuildCore(config, preview, out applyAll, flat, false, out ignored, out ignoredReload);
    }
    internal static Control BuildEmbedded(DanmakuConfig config, out Action applyAll, out Action<DanmakuConfig> reload)
    {
        Control content;
        BuildCore(config, false, out applyAll, true, true, out content, out reload);
        return content;
    }
    private static Form BuildCore(DanmakuConfig config, bool preview, out Action applyAll, bool flat,
        bool embedded, out Control content, out Action<DanmakuConfig> reload)
    {
        DpiForm form = embedded ? null : preview ? (DpiForm)new DanmakuSettingsPreviewForm() : new DpiForm();
        Control root = form != null ? (Control)form : new Panel { Name = "workbenchEmbeddedMore", Dock = DockStyle.Fill };
        content = root;
        DpiLayout.SetDesignFont(root, ModernTheme.UiFont);
        root.BackColor = ModernTheme.Surface; root.ForeColor = ModernTheme.Text;
        if (form != null)
        {
            form.Text = "更多弹幕设置"; form.StartPosition = FormStartPosition.CenterParent;
            form.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(form); };
            form.ClientSize = new Size(740, 620); form.MinimumSize = new Size(540, 440);
        }
        DanmakuSettingsPages tabs = null;
        Panel flatScroll = null; TableLayoutPanel flatStack = null;
        if (flat)
        {
            flatScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            flatScroll.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(flatScroll); };
            root.Controls.Add(flatScroll);
            flatStack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
                Padding = new Padding(0, 0, 0, 12), Margin = Padding.Empty };
            flatStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            flatScroll.Controls.Add(flatStack);
        }
        else { tabs = new DanmakuSettingsPages(); tabs.Dock = DockStyle.Fill; root.Controls.Add(tabs); }
        List<Action> apply = new List<Action>();
        List<Action<DanmakuConfig>> load = new List<Action<DanmakuConfig>>();
        Func<string, TableLayoutPanel> page = delegate (string name)
        {
            if (flat)
            {
                DanmakuCard card = new DanmakuCard { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 14),
                    Padding = new Padding(16) };
                TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
                ModernLabel heading = new ModernLabel { Text = name, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
                DpiLayout.SetDesignFont(heading, new Font(ModernTheme.UiFont.FontFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel));
                grid.Controls.Add(heading, 0, 0); grid.SetColumnSpan(heading, 2); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowCount = 1;
                grid.Margin = Padding.Empty; card.Controls.Add(grid);
                flatStack.Controls.Add(card); flatStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                return grid;
            }
            Panel tab = tabs.AddPage(name);
            Panel scroll = new Panel(); scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true; scroll.BackColor = ModernTheme.Surface; tab.Controls.Add(scroll);
            scroll.HandleCreated += delegate { ModernTheme.ApplyScrollbarTheme(scroll); };
            TableLayoutPanel table = new TableLayoutPanel(); table.Dock = DockStyle.Top; table.AutoSize = true;
            table.Padding = new Padding(14); table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            scroll.Controls.Add(table);
            table.SizeChanged += delegate { scroll.AutoScrollMinSize = new Size(0, table.Height); };
            return table;
        };
        Action<TableLayoutPanel, string, Control, int> row = delegate (TableLayoutPanel table, string caption, Control control, int height)
        {
            int index = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Label label = new ModernLabel(); label.Text = caption; label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft;
            control.Dock = DockStyle.Fill; control.Margin = new Padding(5, 4, 5, 4);
            control.AccessibleName = caption;
            string fieldName;
            if (FeatureFields.TryGetValue(caption, out fieldName)) control.Name = "featureSetting_" + fieldName;
            table.Controls.Add(label, 0, index); table.Controls.Add(control, 1, index);
        };
        Action<TableLayoutPanel, string, bool, Action<bool>> check = delegate (TableLayoutPanel table, string caption, bool value, Action<bool> save)
        {
            CheckBox box = new ModernCheckBox(); box.Checked = value; box.Text = "启用";
            row(table, caption, box, 34); apply.Add(delegate { save(box.Checked); });
            FieldInfo field = FeatureField(caption); load.Add(delegate(DanmakuConfig latest) { box.Checked = (bool)field.GetValue(latest); });
        };
        Action<TableLayoutPanel, string, int, int, int, Action<int>> number = delegate (TableLayoutPanel table, string caption, int value, int min, int max, Action<int> save)
        {
            DanmakuSettingsNumber box = new DanmakuSettingsNumber(); box.Maximum = max; box.Minimum = min; box.Value = value;
            row(table, caption, box, 38); box.Dock = DockStyle.None; box.Anchor = AnchorStyles.Left;
            apply.Add(delegate { save(box.Value); });
            FieldInfo field = FeatureField(caption); load.Add(delegate(DanmakuConfig latest) { box.Value = (int)field.GetValue(latest); });
        };
        Action<TableLayoutPanel, string, string[], string[], string, Action<string>> choice = delegate (TableLayoutPanel table, string caption, string[] ids, string[] labels, string value, Action<string> save)
        {
            ComboBox box = new ModernComboBox(); box.Items.AddRange(labels); box.SelectedIndex = Math.Max(0, Array.IndexOf(ids, value));
            row(table, caption, box, 36); apply.Add(delegate { if (box.SelectedIndex >= 0) save(ids[box.SelectedIndex]); });
            FieldInfo field = FeatureField(caption); load.Add(delegate(DanmakuConfig latest) { box.SelectedIndex = Math.Max(0, Array.IndexOf(ids, (string)field.GetValue(latest))); });
        };
        Action<TableLayoutPanel, string, string[], Action<string[]>> rules = delegate (TableLayoutPanel table, string caption, string[] values, Action<string[]> save)
        {
            ModernInput box = new ModernInput(true);
            box.Inner.Text = string.Join(Environment.NewLine, values ?? new string[0]);
            row(table, caption, box, 104);
            apply.Add(delegate { save(box.Inner.Text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)); });
            FieldInfo field = FeatureField(caption); load.Add(delegate(DanmakuConfig latest) { box.Inner.Text = string.Join(Environment.NewLine, (string[])field.GetValue(latest) ?? new string[0]); });
        };
        TableLayoutPanel display = page("显示与外观");
        check(display, "滚动弹幕", config.ShowScrolling, delegate(bool v) { config.ShowScrolling = v; });
        check(display, "顶部固定", config.ShowTop, delegate(bool v) { config.ShowTop = v; });
        check(display, "底部固定", config.ShowBottom, delegate(bool v) { config.ShowBottom = v; });
        check(display, "逆向弹幕", config.ShowReverse, delegate(bool v) { config.ShowReverse = v; });
        check(display, "彩色弹幕", config.ShowColored, delegate(bool v) { config.ShowColored = v; });
        check(display, "高级弹幕的纯文字", config.ShowAdvancedText, delegate(bool v) { config.ShowAdvancedText = v; });
        check(display, "过长固定弹幕改为滚动", config.FixedOverflowToScroll, delegate(bool v) { config.FixedOverflowToScroll = v; });
        number(display, "顶部固定可用区域 %", config.TopFixedZonePercent, 10, 100, delegate(int v) { config.TopFixedZonePercent = v; });
        number(display, "底部固定可用区域 %", config.BottomFixedZonePercent, 10, 100, delegate(int v) { config.BottomFixedZonePercent = v; });
        number(display, "每类固定条数（0 不限）", config.MaxFixedPerMode, 0, 60, delegate(int v) { config.MaxFixedPerMode = v; });
        choice(display, "表情大小", new string[] { "small", "platform", "large" },
            new string[] { "普通大小", "跟随平台的大表情标记", "大表情（可占多行）" }, config.EmoteSizeMode, delegate(string v) { config.EmoteSizeMode = v; });
        choice(display, "文字边缘", new string[] { "outline", "heavy", "shadow" }, new string[] { "细描边", "重墨描边", "右下投影" }, config.BorderStyle, delegate(string v) { config.BorderStyle = v; });
        number(display, "弹幕自身不透明度 %", config.TextOpacityPercent, 10, 100, delegate(int v) { config.TextOpacityPercent = v; });
        TableLayoutPanel filter = page("过滤与重点");
        rules(filter, "屏蔽用户（每行一条）", config.BlockUsers, delegate(string[] v) { config.BlockUsers = v; });
        rules(filter, "关键词高亮", config.HighlightKeywords, delegate(string[] v) { config.HighlightKeywords = v; });
        rules(filter, "关键词置顶", config.PinKeywords, delegate(string[] v) { config.PinKeywords = v; });
        Label help = new ModernLabel(); help.Text = "用户规则支持 uid:数字、name:完整昵称、re:正则；旧的无前缀昵称保留包含匹配。\r\n关键词每行一个，支持 re:。超时或无效正则会暂时停用，修改设置后重试。";
        row(filter, "规则说明", help, 100);
        check(filter, "合并时归一空白/全半角", config.NormalizeDuplicateText, delegate(bool v) { config.NormalizeDuplicateText = v; });
        number(filter, "合并放大上限 %", config.MergedMaxScalePercent, 100, 180, delegate(int v) { config.MergedMaxScalePercent = v; });
        TableLayoutPanel traffic = page("拥挤与事件");
        check(traffic, "平滑弹幕准入", config.SmoothDanmaku, delegate(bool v) { config.SmoothDanmaku = v; });
        number(traffic, "每秒上限（0 为自动）", config.AdmissionPerSecond, 0, 120, delegate(int v) { config.AdmissionPerSecond = v; });
        number(traffic, "短队列等待毫秒", config.QueueWaitMs, 0, 1000, delegate(int v) { config.QueueWaitMs = v; });
        number(traffic, "短队列容量", config.QueueCapacity, 1, 200, delegate(int v) { config.QueueCapacity = v; });
        check(traffic, "拥挤时缩小普通弹幕", config.ShrinkWhenCrowded, delegate(bool v) { config.ShrinkWhenCrowded = v; });
        number(traffic, "拥挤字号 %", config.CrowdedScalePercent, 70, 100, delegate(int v) { config.CrowdedScalePercent = v; });
        check(traffic, "送礼提示", config.ShowGift, delegate(bool v) { config.ShowGift = v; });
        check(traffic, "进场/关注提示", config.ShowEnterMessage, delegate(bool v) { config.ShowEnterMessage = v; });
        check(traffic, "上舰提示", config.ShowGuardEvents, delegate(bool v) { config.ShowGuardEvents = v; });
        check(traffic, "平台公告", config.ShowNotice, delegate(bool v) { config.ShowNotice = v; });
        check(traffic, "看过 / 点赞 / 高能榜人数", config.ShowLiveStats, delegate(bool v) { config.ShowLiveStats = v; });
        Label eventsHelp = new ModernLabel(); eventsHelp.Text = "当前接入 B站礼物、上舰、进场/关注、公告和人数统计。独立提示条参与避让；默认关闭以保留画面。";
        row(traffic, "事件说明", eventsHelp, 68);
        Panel safeTab = flat ? new DanmakuCard { Dock = DockStyle.Top, Height = 400,
            Margin = new Padding(0, 0, 0, 14), Padding = new Padding(16) } : tabs.AddPage("画面安全区");
        if (flat)
        {
            flatStack.Controls.Add(safeTab); flatStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        TableLayoutPanel safeLayout = new TableLayoutPanel(); safeLayout.Dock = DockStyle.Fill;
        safeLayout.ColumnCount = 1; safeLayout.RowCount = flat ? 4 : 3; safeLayout.Padding = flat ? Padding.Empty : new Padding(14, 8, 14, 8);
        safeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (flat)
        {
            safeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ModernLabel safeHeading = new ModernLabel { Text = "画面安全区", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            DpiLayout.SetDesignFont(safeHeading, new Font(ModernTheme.UiFont.FontFamily, 15f, FontStyle.Bold, GraphicsUnit.Pixel));
            safeLayout.Controls.Add(safeHeading, 0, 0);
        }
        safeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        safeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        safeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); safeTab.Controls.Add(safeLayout);
        DanmakuSafeAreaCanvas canvas = new DanmakuSafeAreaCanvas(config.ReservedAreas); canvas.Dock = DockStyle.Fill;
        canvas.Margin = new Padding(0, 0, 0, 8);
        FlowLayoutPanel presets = new FlowLayoutPanel(); presets.Dock = DockStyle.Fill;
        presets.AutoSize = true; presets.AutoSizeMode = AutoSizeMode.GrowAndShrink; presets.Margin = Padding.Empty;
        Label hint = new ModernLabel(); hint.Text = "在示意画面拖动添加矩形；右键矩形删除。区域按画面百分比保存，缩放后自动跟随。";
        hint.Dock = DockStyle.Fill; hint.AutoSize = true; hint.Margin = new Padding(0, 0, 0, 10);
        int safeOffset = flat ? 1 : 0;
        safeLayout.Controls.Add(hint, 0, safeOffset); safeLayout.Controls.Add(canvas, 0, safeOffset + 1); safeLayout.Controls.Add(presets, 0, safeOffset + 2);
        foreach (string preset in new string[] { "顶部标题", "底部字幕", "中央主体", "清空" })
        {
            string name = preset; Button button = new ModernButton(); button.Text = name; button.AutoSize = true;
            button.MinimumSize = new Size(96, 30); button.Padding = new Padding(10, 4, 10, 4); button.Margin = new Padding(0, 0, 6, 0);
            button.Click += delegate {
                if (name == "清空") canvas.ClearAreas();
                else canvas.AddArea(name == "顶部标题" ? new RectangleF(0, 0, 100, 8)
                    : name == "底部字幕" ? new RectangleF(0, 85, 100, 15) : new RectangleF(35, 20, 30, 60));
            }; presets.Controls.Add(button);
        }
        apply.Add(delegate { config.ReservedAreas = canvas.GetAreas(); });
        load.Add(delegate(DanmakuConfig latest) { canvas.SetAreas(latest.ReservedAreas); });
        Panel footer = new Panel(); footer.Dock = DockStyle.Bottom; footer.Height = 48; root.Controls.Add(footer);
        footer.Visible = !embedded;
        Button ok = new ModernButton(); ((ModernButton)ok).Style = ModernButton.Kind.Primary; ok.Text = "应用"; ok.DialogResult = DialogResult.OK; ok.SetBounds(510, 8, 94, 32);
        Button cancel = new ModernButton(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(620, 8, 94, 32);
        footer.Controls.Add(ok); footer.Controls.Add(cancel);
        bool layingOut = false;
        Action reflow = delegate {
            if (layingOut) return; layingOut = true;
            try {
                footer.Height = DpiLayout.S(root, 48);
                cancel.SetBounds(Math.Max(4, footer.Width - DpiLayout.S(root, 108)), DpiLayout.S(root, 8), DpiLayout.S(root, 94), DpiLayout.S(root, 32));
                ok.SetBounds(Math.Max(4, cancel.Left - DpiLayout.S(root, 106)), DpiLayout.S(root, 8), DpiLayout.S(root, 94), DpiLayout.S(root, 32));
            } finally { layingOut = false; }
        };
        footer.Resize += delegate { reflow(); }; if (form != null) form.DpiUpdated += delegate { reflow(); };
        ok.Click += delegate { foreach (Action save in apply) save(); config.Normalize(); };
        if (form != null) { form.AcceptButton = ok; form.CancelButton = cancel; }
        Action theme = delegate { ApplyTheme(root); if (form != null) ModernTheme.ApplyWindowChrome(form); };
        ModernTheme.Changed += theme; root.Disposed += delegate { ModernTheme.Changed -= theme; };
        ApplyTheme(root);
        if (form != null) DpiLayout.ScaleTree(form);
        reflow();
        applyAll = delegate { foreach (Action save in apply) save(); config.Normalize(); };
        reload = delegate(DanmakuConfig latest) { foreach (Action<DanmakuConfig> restore in load) restore(latest); };
        return form;
    }
    private static readonly Dictionary<string, string> FeatureFields = new Dictionary<string, string>
    {
        { "滚动弹幕", "ShowScrolling" }, { "顶部固定", "ShowTop" }, { "底部固定", "ShowBottom" },
        { "逆向弹幕", "ShowReverse" }, { "彩色弹幕", "ShowColored" }, { "高级弹幕的纯文字", "ShowAdvancedText" },
        { "过长固定弹幕改为滚动", "FixedOverflowToScroll" }, { "顶部固定可用区域 %", "TopFixedZonePercent" },
        { "底部固定可用区域 %", "BottomFixedZonePercent" }, { "每类固定条数（0 不限）", "MaxFixedPerMode" },
        { "表情大小", "EmoteSizeMode" }, { "文字边缘", "BorderStyle" }, { "弹幕自身不透明度 %", "TextOpacityPercent" },
        { "屏蔽用户（每行一条）", "BlockUsers" }, { "关键词高亮", "HighlightKeywords" }, { "关键词置顶", "PinKeywords" },
        { "合并时归一空白/全半角", "NormalizeDuplicateText" }, { "合并放大上限 %", "MergedMaxScalePercent" },
        { "平滑弹幕准入", "SmoothDanmaku" }, { "每秒上限（0 为自动）", "AdmissionPerSecond" },
        { "短队列等待毫秒", "QueueWaitMs" }, { "短队列容量", "QueueCapacity" },
        { "拥挤时缩小普通弹幕", "ShrinkWhenCrowded" }, { "拥挤字号 %", "CrowdedScalePercent" },
        { "送礼提示", "ShowGift" }, { "进场/关注提示", "ShowEnterMessage" }, { "上舰提示", "ShowGuardEvents" },
        { "平台公告", "ShowNotice" }, { "看过 / 点赞 / 高能榜人数", "ShowLiveStats" }
    };
    private static FieldInfo FeatureField(string caption)
    { return typeof(DanmakuConfig).GetField(FeatureFields[caption], BindingFlags.Instance | BindingFlags.Public); }
    private static void ApplyTheme(Control root)
    {
        Color surface = ModernTheme.Surface;
        for (Control parent = root.Parent; parent != null; parent = parent.Parent)
            if (parent is DanmakuCard) { surface = ModernTheme.Card; break; }
        if (root is Form || root.GetType() == typeof(Panel) || root is TableLayoutPanel || root is FlowLayoutPanel)
        { root.BackColor = surface; root.ForeColor = ModernTheme.Text; }
        Panel scroll = root as Panel;
        if (scroll != null && scroll.AutoScroll) ModernTheme.ApplyScrollbarTheme(scroll);
        foreach (Control child in root.Controls) ApplyTheme(child);
    }
    internal static void Show(IWin32Window owner, DanmakuConfig config)
    {
        DanmakuConfig working = config.Clone();
        using (Form dialog = Build(working))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            foreach (FieldInfo field in typeof(DanmakuConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(config, field.GetValue(working)); // 保留主对话框原有的保存快照
        }
    }
}

// 仅供离屏布局预览：零不透明度、工具窗口和 NOACTIVATE，避免测试抢占用户焦点。
internal sealed class DanmakuSettingsPreviewForm : DpiForm
{
    internal DanmakuSettingsPreviewForm() { Opacity = 0; ShowInTaskbar = false; }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000080; return value; }
    }
}

internal sealed class DanmakuSafeAreaCanvas : Control
{
    private readonly List<RectangleF> _areas = new List<RectangleF>();
    private Point? _start;
    private Point _end;
    internal DanmakuSafeAreaCanvas(string[] areas)
    {
        DoubleBuffered = true; BackColor = ModernTheme.Surface; ModernTheme.Changed += ApplyTheme;
        foreach (string area in areas ?? new string[0]) { RectangleF rect; if (DanmakuRegions.TryParse(area, out rect)) _areas.Add(rect); }
    }
    internal void ClearAreas() { _areas.Clear(); Invalidate(); }
    internal void SetAreas(string[] areas)
    {
        _areas.Clear();
        foreach (string area in areas ?? new string[0]) { RectangleF rect; if (DanmakuRegions.TryParse(area, out rect)) _areas.Add(rect); }
        Invalidate();
    }
    internal void AddArea(RectangleF area) { if (_areas.Count < 12) _areas.Add(area); Invalidate(); }
    internal string[] GetAreas() { List<string> values = new List<string>(); foreach (RectangleF area in _areas) values.Add(DanmakuRegions.Encode(area)); return values.ToArray(); }
    private RectangleF Frame
    {
        get { float pad = DpiLayout.S(this, 8), width = Math.Max(1, Width - pad * 2), height = width * 9f / 16f; if (height > Height - pad * 2) { height = Math.Max(1, Height - pad * 2); width = height * 16f / 9f; } return new RectangleF((Width - width) / 2f, (Height - height) / 2f, width, height); }
    }
    private void ApplyTheme() { BackColor = ModernTheme.Surface; Invalidate(); }
    protected override void Dispose(bool disposing)
    { if (disposing) ModernTheme.Changed -= ApplyTheme; base.Dispose(disposing); }
    private Point Percent(Point point)
    {
        RectangleF frame = Frame;
        return new Point(Math.Max(0, Math.Min(100, (int)Math.Round((point.X - frame.X) * 100f / frame.Width))),
            Math.Max(0, Math.Min(100, (int)Math.Round((point.Y - frame.Y) * 100f / frame.Height))));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (!Frame.Contains(e.Location)) return;
        Point at = Percent(e.Location);
        if (e.Button == MouseButtons.Right)
        { for (int i = _areas.Count - 1; i >= 0; i--) if (_areas[i].Contains(at)) { _areas.RemoveAt(i); break; } Invalidate(); return; }
        if (e.Button == MouseButtons.Left) { _start = at; _end = at; Capture = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_start.HasValue) { _end = Percent(e.Location); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (!_start.HasValue) return;
        _end = Percent(e.Location); Point first = _start.Value; _start = null; Capture = false;
        RectangleF rect = new RectangleF(Math.Min(first.X, _end.X), Math.Min(first.Y, _end.Y), Math.Abs(first.X - _end.X), Math.Abs(first.Y - _end.Y));
        if (rect.Width >= 2 && rect.Height >= 2) AddArea(rect); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); RectangleF frame = Frame;
        using (SolidBrush background = new SolidBrush(Color.FromArgb(24, 35, 51))) e.Graphics.FillRectangle(background, frame);
        using (Pen border = new Pen(Color.SlateGray)) e.Graphics.DrawRectangle(border, frame.X, frame.Y, frame.Width, frame.Height);
        List<RectangleF> visible = new List<RectangleF>(_areas);
        if (_start.HasValue) visible.Add(new RectangleF(Math.Min(_start.Value.X, _end.X), Math.Min(_start.Value.Y, _end.Y), Math.Abs(_start.Value.X - _end.X), Math.Abs(_start.Value.Y - _end.Y)));
        using (SolidBrush fill = new SolidBrush(Color.FromArgb(100, 240, 130, 70)))
        using (Pen line = new Pen(Color.Coral, DpiLayout.S(this, 2)))
        foreach (RectangleF percent in visible)
        {
            RectangleF rect = new RectangleF(frame.X + percent.X * frame.Width / 100f, frame.Y + percent.Y * frame.Height / 100f, percent.Width * frame.Width / 100f, percent.Height * frame.Height / 100f);
            e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(line, rect.X, rect.Y, rect.Width, rect.Height);
        }
        using (SolidBrush brush = new SolidBrush(Color.White)) e.Graphics.DrawString("播放画面示意（16:9）", Font, brush, frame.X + DpiLayout.S(this, 8), frame.Y + DpiLayout.S(this, 8));
    }
}
