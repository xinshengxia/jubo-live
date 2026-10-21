using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

// Settings use the same painted controls as the launcher, including the tab strip.
internal sealed class DanmakuSettingsPages : UserControl, IDpiScaled
{
    private readonly Panel header = new Panel();
    private readonly Panel body = new Panel();
    private readonly List<Panel> pages = new List<Panel>();
    private readonly List<ModernButton> buttons = new List<ModernButton>();
    private int selected = -1;
    internal DanmakuSettingsPages()
    {
        Name = "SettingsPages"; AccessibleRole = AccessibleRole.PageTabList;
        header.Dock = DockStyle.Top; header.Height = 42; header.AutoSize = false;
        header.Padding = new Padding(14, 4, 14, 4);
        body.Dock = DockStyle.Fill;
        Controls.Add(body); Controls.Add(header);
        ModernTheme.Changed += ApplyTheme; ApplyTheme();
    }
    internal Panel AddPage(string title)
    {
        Panel page = new Panel(); page.Text = title; page.AccessibleName = title;
        page.Dock = DockStyle.Fill; page.BackColor = ModernTheme.Surface; page.Visible = false;
        body.Controls.Add(page); pages.Add(page);
        int index = pages.Count - 1;
        ModernButton button = new ModernButton(); button.Text = title; button.Dock = DockStyle.None;
        button.AutoSize = false; button.MinimumSize = Size.Empty; button.Padding = new Padding(12, 2, 12, 2); button.Tab = true;
        button.TabStop = false;
        button.AccessibleRole = AccessibleRole.PageTab; button.Margin = new Padding(0, 0, 6, 0);
        button.Click += delegate { SelectedIndex = index; };
        header.Controls.Add(button); buttons.Add(button);
        if (selected < 0) SelectedIndex = 0;
        ArrangeHeader();
        return page;
    }
    internal int SelectedIndex
    {
        get { return selected; }
        set
        {
            if (value < 0 || value >= pages.Count) return;
            selected = value;
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == value; buttons[i].Selected = i == value;
                buttons[i].TabStop = i == value;
                buttons[i].AccessibleDescription = i == value ? "当前选项页" : "";
            }
            pages[value].BringToFront();
        }
    }
    private bool syncingHeader;
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        ArrangeHeader();
    }
    private void ArrangeHeader()
    {
        if (syncingHeader || IsDisposed) return;
        syncingHeader = true;
        try
        {
            int pad = DpiLayout.S(this, 14), vertical = DpiLayout.S(this, 4), gap = DpiLayout.S(this, 6);
            int available = Math.Max(1, ClientSize.Width - pad * 2), x = pad, y = vertical;
            int rowHeight = DpiLayout.S(this, 34);
            foreach (ModernButton button in buttons)
            {
                int desired = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + DpiLayout.S(this, 24);
                int width = Math.Min(available, Math.Max(DpiLayout.S(this, 72), desired));
                if (x > pad && x + width > pad + available) { x = pad; y += rowHeight + gap; }
                button.SetBounds(x, y, width, rowHeight); x += width + gap;
            }
            header.Height = y + rowHeight + vertical;
        }
        finally { syncingHeader = false; }
    }
    public void OnDpiScaled(float scale) { ArrangeHeader(); }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (pages.Count == 0) return base.ProcessCmdKey(ref message, keys);
        int next = selected;
        if ((keys & (Keys.Control | Keys.KeyCode)) == (Keys.Control | Keys.Tab))
            next += (keys & Keys.Shift) != 0 ? -1 : 1;
        else if (header.ContainsFocus && keys == Keys.Right) next++;
        else if (header.ContainsFocus && keys == Keys.Left) next--;
        else return base.ProcessCmdKey(ref message, keys);
        SelectedIndex = (next + pages.Count) % pages.Count; buttons[selected].Focus(); return true;
    }
    private void ApplyTheme()
    {
        BackColor = header.BackColor = body.BackColor = ModernTheme.Surface;
        ForeColor = ModernTheme.Text;
        foreach (Panel page in pages) page.BackColor = ModernTheme.Surface;
        Invalidate(true);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}

internal sealed class DanmakuSettingsNumber : UserControl, IDpiScaled
{
    private readonly ModernInput input = new ModernInput(false);
    private readonly Panel stepper = new Panel();
    private readonly ModernButton minus = new ModernButton(), plus = new ModernButton();
    private int minimum, maximum = 100, value;
    private bool arranging;
    internal DanmakuSettingsNumber()
    {
        Size = new Size(160, 28); MinimumSize = new Size(132, 28); TabStop = false;
        input.Dock = DockStyle.None; input.Inner.Text = "0";
        stepper.Dock = DockStyle.None; stepper.Width = 64;
        minus.Text = "−"; minus.AccessibleName = "减少"; minus.Dock = DockStyle.None; minus.Width = 28;
        plus.Text = "+"; plus.AccessibleName = "增加"; plus.Dock = DockStyle.None; plus.Width = 28;
        stepper.Controls.Add(minus); stepper.Controls.Add(plus);
        Controls.Add(input); Controls.Add(stepper);
        minus.Click += delegate { Value = Value - 1; }; plus.Click += delegate { Value = Value + 1; };
        input.Inner.Leave += delegate { Commit(); };
        input.Inner.KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            { Value = Value + (e.KeyCode == Keys.Up ? 1 : -1); e.Handled = true; e.SuppressKeyPress = true; }
        };
        input.Inner.KeyPress += delegate(object sender, KeyPressEventArgs e) {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && !(e.KeyChar == '-' && minimum < 0 && input.Inner.SelectionStart == 0)) e.Handled = true;
        };
        ModernTheme.Changed += ApplyTheme; ApplyTheme();
        ArrangeParts();
    }
    internal int Minimum { get { return minimum; } set { minimum = value; Value = this.value; } }
    internal int Maximum { get { return maximum; } set { maximum = value; Value = this.value; } }
    internal int Value
    {
        get { Commit(); return value; }
        set
        {
            this.value = Math.Max(minimum, Math.Min(maximum, value));
            input.Inner.Text = this.value.ToString(CultureInfo.InvariantCulture);
            minus.Enabled = this.value > minimum; plus.Enabled = this.value < maximum;
        }
    }
    private void Commit()
    {
        int parsed;
        Value = int.TryParse(input.Inner.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : value;
    }
    public void OnDpiScaled(float scale)
    {
        MinimumSize = new Size((int)Math.Round(132f * scale), (int)Math.Round(28f * scale));
        ArrangeParts();
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); ArrangeParts(); }
    private void ArrangeParts()
    {
        if (arranging || IsDisposed || input == null || stepper == null) return;
        arranging = true;
        try
        {
            int button = DpiLayout.S(this, 28), gap = 0;
            int controls = Math.Min(DpiLayout.S(this, 60), Math.Max(DpiLayout.S(this, 56), ClientSize.Width - DpiLayout.S(this, 48)));
            int editor = Math.Max(1, ClientSize.Width - controls - gap);
            input.SetBounds(0, 0, editor, ClientSize.Height);
            stepper.SetBounds(editor + gap, 0, Math.Max(1, controls), ClientSize.Height);
            int each = Math.Min(button, Math.Max(1, (stepper.Width - DpiLayout.S(this, 6)) / 2));
            minus.SetBounds(0, 0, each, stepper.Height);
            plus.SetBounds(stepper.Width - each, 0, each, stepper.Height);
        }
        finally { arranging = false; }
    }
    private void ApplyTheme() { BackColor = stepper.BackColor = ModernTheme.Card; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}
