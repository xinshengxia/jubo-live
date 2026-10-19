using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

// Settings use the same painted controls as the launcher, including the tab strip.
internal sealed class DanmakuSettingsPages : UserControl, IDpiScaled
{
    private readonly TableLayoutPanel header = new TableLayoutPanel();
    private readonly Panel body = new Panel();
    private readonly List<Panel> pages = new List<Panel>();
    private readonly List<ModernButton> buttons = new List<ModernButton>();
    private int selected = -1;
    internal DanmakuSettingsPages()
    {
        Name = "SettingsPages"; AccessibleRole = AccessibleRole.PageTabList;
        header.Dock = DockStyle.Top; header.Height = 44; header.RowCount = 1;
        header.Padding = new Padding(14, 6, 14, 6);
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
        ModernButton button = new ModernButton(); button.Text = title; button.Dock = DockStyle.Fill;
        button.TabStop = false;
        button.AccessibleRole = AccessibleRole.PageTab; button.Margin = new Padding(0, 0, 6, 0);
        button.Click += delegate { SelectedIndex = index; };
        header.ColumnCount = pages.Count;
        header.ColumnStyles.Clear();
        for (int i = 0; i < pages.Count; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / pages.Count));
        header.Controls.Add(button, index, 0); buttons.Add(button);
        if (selected < 0) SelectedIndex = 0;
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
    public void OnDpiScaled(float scale) { header.Height = Math.Max(1, (int)Math.Round(44f * scale)); }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
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
    internal DanmakuSettingsNumber()
    {
        Size = new Size(160, 28); TabStop = false;
        input.Dock = DockStyle.Fill; input.Inner.Text = "0";
        stepper.Dock = DockStyle.Right; stepper.Width = 64;
        minus.Text = "−"; minus.AccessibleName = "减少"; minus.Dock = DockStyle.Left; minus.Width = 28;
        plus.Text = "+"; plus.AccessibleName = "增加"; plus.Dock = DockStyle.Right; plus.Width = 28;
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
        stepper.Width = (int)Math.Round(64f * scale);
        minus.Width = plus.Width = (int)Math.Round(28f * scale);
    }
    private void ApplyTheme() { BackColor = stepper.BackColor = ModernTheme.Surface; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}
