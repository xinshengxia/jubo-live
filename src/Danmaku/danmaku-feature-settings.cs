using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal sealed class DanmakuFeatureSettings
{
    internal static Form Build(DanmakuConfig config)
    { return BuildCore(config, false); }
    internal static Form BuildPreview(DanmakuConfig config)
    { return BuildCore(config, true); }
    private static Form BuildCore(DanmakuConfig config, bool preview)
    {
        DpiForm form = preview ? (DpiForm)new DanmakuSettingsPreviewForm() : new DpiForm();
        form.Text = "更多弹幕设置"; form.StartPosition = FormStartPosition.CenterParent;
        DpiLayout.SetDesignFont(form, ModernTheme.UiFont);
        form.BackColor = ModernTheme.Surface; form.ForeColor = ModernTheme.Text;
        form.HandleCreated += delegate { ModernTheme.ApplyWindowChrome(form); };
        form.ClientSize = new Size(740, 620); form.MinimumSize = new Size(540, 440);
        TabControl tabs = new TabControl(); tabs.Dock = DockStyle.Fill; form.Controls.Add(tabs);
        List<Action> apply = new List<Action>();
        Func<string, TableLayoutPanel> page = delegate (string name)
        {
            TabPage tab = new TabPage(name); tab.BackColor = ModernTheme.Surface; tabs.TabPages.Add(tab);
            Panel scroll = new Panel(); scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true; tab.Controls.Add(scroll);
            TableLayoutPanel table = new TableLayoutPanel(); table.Dock = DockStyle.Top; table.AutoSize = true;
            table.Padding = new Padding(14); table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            scroll.Controls.Add(table); return table;
        };
        Action<TableLayoutPanel, string, Control, int> row = delegate (TableLayoutPanel table, string caption, Control control, int height)
        {
            int index = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Label label = new ModernLabel(); label.Text = caption; label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft;
            control.Dock = DockStyle.Fill; control.Margin = new Padding(5, 4, 5, 4);
            table.Controls.Add(label, 0, index); table.Controls.Add(control, 1, index);
        };
        Action<TableLayoutPanel, string, bool, Action<bool>> check = delegate (TableLayoutPanel table, string caption, bool value, Action<bool> save)
        {
            CheckBox box = new ModernCheckBox(); box.Checked = value; box.Text = "启用";
            row(table, caption, box, 34); apply.Add(delegate { save(box.Checked); });
        };
        Action<TableLayoutPanel, string, int, int, int, Action<int>> number = delegate (TableLayoutPanel table, string caption, int value, int min, int max, Action<int> save)
        {
            NumericUpDown box = new NumericUpDown(); box.Minimum = min; box.Maximum = max; box.Value = Math.Max(min, Math.Min(max, value));
            box.BackColor = ModernTheme.Surface; box.ForeColor = ModernTheme.Text;
            row(table, caption, box, 34); apply.Add(delegate { save((int)box.Value); });
        };
        Action<TableLayoutPanel, string, string[], string[], string, Action<string>> choice = delegate (TableLayoutPanel table, string caption, string[] ids, string[] labels, string value, Action<string> save)
        {
            ComboBox box = new ModernComboBox(); box.Items.AddRange(labels); box.SelectedIndex = Math.Max(0, Array.IndexOf(ids, value));
            row(table, caption, box, 36); apply.Add(delegate { save(ids[box.SelectedIndex]); });
        };
        Action<TableLayoutPanel, string, string[], Action<string[]>> rules = delegate (TableLayoutPanel table, string caption, string[] values, Action<string[]> save)
        {
            TextBox box = new TextBox(); box.Multiline = true; box.ScrollBars = ScrollBars.Vertical;
            box.BackColor = ModernTheme.Surface; box.ForeColor = ModernTheme.Text;
            box.Text = string.Join(Environment.NewLine, values ?? new string[0]);
            row(table, caption, box, 104);
            apply.Add(delegate { save(box.Text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)); });
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
        TabPage safeTab = new TabPage("画面安全区"); tabs.TabPages.Add(safeTab);
        DanmakuSafeAreaCanvas canvas = new DanmakuSafeAreaCanvas(config.ReservedAreas); canvas.Dock = DockStyle.Fill;
        FlowLayoutPanel presets = new FlowLayoutPanel(); presets.Dock = DockStyle.Bottom; presets.Height = 76;
        Label hint = new ModernLabel(); hint.Text = "在示意画面拖动添加矩形；右键矩形删除。区域按画面百分比保存，缩放后自动跟随。";
        hint.Dock = DockStyle.Top; hint.Height = 48;
        safeTab.Controls.Add(canvas); safeTab.Controls.Add(presets); safeTab.Controls.Add(hint);
        foreach (string preset in new string[] { "顶部标题", "底部字幕", "中央主体", "清空" })
        {
            string name = preset; Button button = new ModernButton(); button.Text = name; button.AutoSize = true;
            button.Click += delegate {
                if (name == "清空") canvas.ClearAreas();
                else canvas.AddArea(name == "顶部标题" ? new RectangleF(0, 0, 100, 8)
                    : name == "底部字幕" ? new RectangleF(0, 85, 100, 15) : new RectangleF(35, 20, 30, 60));
            }; presets.Controls.Add(button);
        }
        apply.Add(delegate { config.ReservedAreas = canvas.GetAreas(); });
        Panel footer = new Panel(); footer.Dock = DockStyle.Bottom; footer.Height = 48; form.Controls.Add(footer);
        Button ok = new ModernButton(); ok.Text = "应用"; ok.DialogResult = DialogResult.OK; ok.SetBounds(510, 8, 94, 32);
        Button cancel = new ModernButton(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(620, 8, 94, 32);
        footer.Controls.Add(ok); footer.Controls.Add(cancel);
        bool layingOut = false;
        Action reflow = delegate {
            if (layingOut) return; layingOut = true;
            try {
                footer.Height = DpiLayout.S(form, 48); presets.Height = DpiLayout.S(form, 76); hint.Height = DpiLayout.S(form, 48);
                cancel.SetBounds(Math.Max(4, footer.Width - DpiLayout.S(form, 108)), DpiLayout.S(form, 8), DpiLayout.S(form, 94), DpiLayout.S(form, 32));
                ok.SetBounds(Math.Max(4, cancel.Left - DpiLayout.S(form, 106)), DpiLayout.S(form, 8), DpiLayout.S(form, 94), DpiLayout.S(form, 32));
            } finally { layingOut = false; }
        };
        footer.Resize += delegate { reflow(); }; form.DpiUpdated += delegate { reflow(); };
        ok.Click += delegate { foreach (Action save in apply) save(); config.Normalize(); };
        form.AcceptButton = ok; form.CancelButton = cancel;
        DpiLayout.ScaleTree(form);
        reflow();
        return form;
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
        DoubleBuffered = true; BackColor = Color.FromArgb(24, 35, 51);
        foreach (string area in areas ?? new string[0]) { RectangleF rect; if (DanmakuRegions.TryParse(area, out rect)) _areas.Add(rect); }
    }
    internal void ClearAreas() { _areas.Clear(); Invalidate(); }
    internal void AddArea(RectangleF area) { if (_areas.Count < 12) _areas.Add(area); Invalidate(); }
    internal string[] GetAreas() { List<string> values = new List<string>(); foreach (RectangleF area in _areas) values.Add(DanmakuRegions.Encode(area)); return values.ToArray(); }
    private RectangleF Frame
    {
        get { float width = Math.Max(16, Width - 24), height = width * 9f / 16f; if (height > Height - 24) { height = Math.Max(9, Height - 24); width = height * 16f / 9f; } return new RectangleF((Width - width) / 2f, (Height - height) / 2f, width, height); }
    }
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
        using (Pen border = new Pen(Color.SlateGray)) e.Graphics.DrawRectangle(border, frame.X, frame.Y, frame.Width, frame.Height);
        List<RectangleF> visible = new List<RectangleF>(_areas);
        if (_start.HasValue) visible.Add(new RectangleF(Math.Min(_start.Value.X, _end.X), Math.Min(_start.Value.Y, _end.Y), Math.Abs(_start.Value.X - _end.X), Math.Abs(_start.Value.Y - _end.Y)));
        using (SolidBrush fill = new SolidBrush(Color.FromArgb(100, 240, 130, 70)))
        using (Pen line = new Pen(Color.Coral, 2f))
        foreach (RectangleF percent in visible)
        {
            RectangleF rect = new RectangleF(frame.X + percent.X * frame.Width / 100f, frame.Y + percent.Y * frame.Height / 100f, percent.Width * frame.Width / 100f, percent.Height * frame.Height / 100f);
            e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(line, rect.X, rect.Y, rect.Width, rect.Height);
        }
        using (SolidBrush brush = new SolidBrush(Color.White)) e.Graphics.DrawString("播放画面示意（16:9）", Font, brush, frame.X + 8, frame.Y + 8);
    }
}
