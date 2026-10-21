using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// Workbench controls use the project's existing palette and ordinary GDI+
// geometry. No external UI library or third-party icon paths are embedded here.
internal sealed class WorkbenchPanel : Panel
{
    private bool card;

    public WorkbenchPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ModernTheme.Changed += UpdateTheme;
        UpdateTheme();
    }

    public bool IsCard
    {
        get { return card; }
        set { if (card != value) { card = value; UpdateTheme(); } }
    }

    private void UpdateTheme()
    {
        if (IsDisposed) return;
        BackColor = card ? ModernTheme.Card : ModernTheme.Surface;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!card) { base.OnPaintBackground(e); return; }
        e.Graphics.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), DpiLayout.S(this, 10)))
        using (SolidBrush fill = new SolidBrush(ModernTheme.Card)) e.Graphics.FillPath(fill, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (card && Width > 1 && Height > 1)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), DpiLayout.S(this, 10)))
            using (Pen border = new Pen(ModernTheme.Border, Math.Max(1f, DpiLayout.For(this)))) e.Graphics.DrawPath(border, path);
        }
        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= UpdateTheme;
        base.Dispose(disposing);
    }
}

internal sealed class WorkbenchCell : Control
{
    internal WorkbenchCell()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        DpiLayout.SetDesignFont(this, WorkbenchFonts.Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color back = Parent == null ? ModernTheme.Card : Parent.BackColor;
        if (back.A == 0) back = ModernTheme.Card;
        g.Clear(back);
        if (Width < 1 || Height < 1) return;
        TextRenderer.DrawText(g, Text ?? "", Font, ClientRectangle, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

internal sealed class WorkbenchBrandMark : Control
{
    public WorkbenchBrandMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        int radius = Math.Min(DpiLayout.S(this, 9), Math.Max(DpiLayout.S(this, 4), Height / 4));
        using (GraphicsPath path = ModernTheme.RoundRect(box, radius))
        using (SolidBrush brush = new SolidBrush(ModernTheme.Accent))
            g.FillPath(brush, path);
        float scale = DpiLayout.For(this);
        int pad = Math.Max(5, (int)(Width * .22f));
        Rectangle monitor = new Rectangle(pad, pad + Math.Max(1, Height / 16), Math.Max(8, Width - pad * 2), Math.Max(7, (int)(Height * .46f)));
        using (Pen pen = new Pen(Color.White, Math.Max(1.4f, 1.5f * scale)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawRectangle(pen, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            int center = monitor.Left + monitor.Width / 2;
            g.DrawLine(pen, center, monitor.Bottom + 1, center, monitor.Bottom + Math.Max(2, Height / 8));
            g.DrawLine(pen, center - Math.Max(3, Width / 7), monitor.Bottom + Math.Max(2, Height / 8),
                center + Math.Max(3, Width / 7), monitor.Bottom + Math.Max(2, Height / 8));
        }
    }
}

internal sealed class WorkbenchNavigationButton : Button
{
    private bool selected, hover, pressed;
    private string glyph = "";
    private Font emphasisFont;
    private string emphasisKey;

    public WorkbenchNavigationButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
        DpiLayout.SetDesignFont(this, WorkbenchFonts.Nav);
        ModernTheme.Changed += UpdateTheme;
        UpdateTheme();
    }

    public bool Selected
    {
        get { return selected; }
        set
        {
            if (selected == value) return;
            selected = value;
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    public string Glyph
    {
        get { return glyph; }
        set { glyph = value ?? ""; Invalidate(); }
    }

    private void UpdateTheme()
    {
        if (IsDisposed) return;
        BackColor = ModernTheme.Surface;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    private void ReleaseEmphasisFont()
    {
        if (emphasisFont != null) { emphasisFont.Dispose(); emphasisFont = null; }
        emphasisKey = null;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        ReleaseEmphasisFont();
        base.OnFontChanged(e);
    }

    // Selected rows carry the draft's 600 weight. The workbench typography is
    // Microsoft YaHei UI (not HarmonyOS Medium), so the emphasis font follows
    // the control font family and only swaps the weight, scaled with the DPI
    // font that DpiForm installed.
    private Font EmphasisFont()
    {
        float pixels = Font.Unit == GraphicsUnit.Pixel ? Font.Size : Font.SizeInPoints * 96f / 72f;
        string key = Font.FontFamily.Name + "|" + FontStyle.Bold + "|"
            + pixels.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (emphasisFont != null && key == emphasisKey) return emphasisFont;
        ReleaseEmphasisFont();
        emphasisKey = key;
        emphasisFont = new Font(Font.FontFamily, pixels, FontStyle.Bold, GraphicsUnit.Pixel);
        return emphasisFont;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor);
        if (Width < 2 || Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        // Draft (workbench-v4): the selected row keeps the ink text colour and
        // only the glyph turns accent; the row itself is the soft accent fill.
        Color textColor = Enabled ? ModernTheme.Text : ModernTheme.TextDisabled;
        Color iconColor = Enabled ? (selected ? ModernTheme.Accent : ModernTheme.Text) : ModernTheme.TextDisabled;
        Color fill = selected ? ModernTheme.Selection : pressed ? ModernTheme.Selection : hover ? ModernTheme.Hover : Color.Transparent;
        using (GraphicsPath path = ModernTheme.RoundRect(rect, DpiLayout.S(this, 7)))
        {
            if (fill.A != 0) using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
        }
        Rectangle icon = new Rectangle(DpiLayout.S(this, 11), (Height - DpiLayout.S(this, 15)) / 2,
            DpiLayout.S(this, 15), DpiLayout.S(this, 15));
        DrawGlyph(g, icon, iconColor, glyph);
        Rectangle text = new Rectangle(DpiLayout.S(this, 31), DpiLayout.S(this, 2),
            Math.Max(0, Width - DpiLayout.S(this, 37)), Math.Max(0, Height - DpiLayout.S(this, 4)));
        TextRenderer.DrawText(g, Text, selected && Enabled ? EmphasisFont() : Font, text, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -DpiLayout.S(this, 4), -DpiLayout.S(this, 4)), textColor, fill);
    }

    private void DrawGlyph(Graphics g, Rectangle box, Color color, string name)
    {
        float scale = DpiLayout.For(this);
        float x = box.Left, y = box.Top, size = box.Width;
        float center = size / 2f;
        using (Pen pen = new Pen(color, Math.Max(1f, 1.25f * scale)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            string key = (name ?? "").ToLowerInvariant();
            if (key == "favorites" || key == "star")
            {
                PointF[] points = new PointF[10];
                for (int i = 0; i < points.Length; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5;
                    float radius = size * (i % 2 == 0 ? 0.43f : 0.20f);
                    points[i] = new PointF(x + center + (float)Math.Cos(angle) * radius,
                        y + center + (float)Math.Sin(angle) * radius);
                }
                g.DrawPolygon(pen, points);
            }
            else if (key == "history" || key == "recent")
            {
                g.DrawArc(pen, x + size * .15f, y + size * .15f, size * .72f, size * .72f, 205, 310);
                g.DrawLines(pen, new PointF[] { new PointF(x + size * .09f, y + size * .15f), new PointF(x + size * .10f, y + size * .40f), new PointF(x + size * .33f, y + size * .39f) });
                g.DrawLines(pen, new PointF[] { new PointF(x + center, y + size * .29f), new PointF(x + center, y + center), new PointF(x + size * .67f, y + size * .60f) });
            }
            else if (key == "search" || key == "lookup")
            {
                g.DrawEllipse(pen, x + size * .10f, y + size * .10f, size * .60f, size * .60f);
                g.DrawLine(pen, x + size * .61f, y + size * .61f, x + size * .91f, y + size * .91f);
            }
            else if (key == "sessions" || key == "players" || key == "monitor" || key == "play")
            {
                g.DrawRectangle(pen, x + size * .08f, y + size * .12f, size * .84f, size * .60f);
                g.DrawLine(pen, x + center, y + size * .73f, x + center, y + size * .89f);
                g.DrawLine(pen, x + size * .30f, y + size * .90f, x + size * .70f, y + size * .90f);
                if (key == "sessions" || key == "play")
                    g.DrawPolygon(pen, new PointF[] { new PointF(x + size * .41f, y + size * .27f), new PointF(x + size * .41f, y + size * .58f), new PointF(x + size * .66f, y + size * .42f) });
            }
            else if (key == "accounts" || key == "account" || key == "user")
            {
                g.DrawEllipse(pen, x + size * .32f, y + size * .05f, size * .36f, size * .36f);
                g.DrawArc(pen, x + size * .12f, y + size * .50f, size * .76f, size * .80f, 180, 180);
            }
            else if (key == "danmaku" || key == "chat" || key == "messages")
            {
                g.DrawRectangle(pen, x + size * .10f, y + size * .10f, size * .80f, size * .60f);
                g.DrawLines(pen, new PointF[] { new PointF(x + size * .25f, y + size * .71f), new PointF(x + size * .25f, y + size * .91f), new PointF(x + size * .47f, y + size * .71f) });
                g.DrawLine(pen, x + size * .26f, y + size * .31f, x + size * .74f, y + size * .31f);
                g.DrawLine(pen, x + size * .26f, y + size * .49f, x + size * .59f, y + size * .49f);
            }
            else
            {
                // Simple stacked data trays also serve as a neutral fallback.
                for (int i = 0; i < 3; i++)
                {
                    float top = y + size * (.09f + i * .29f);
                    g.DrawRectangle(pen, x + size * .11f, top, size * .78f, size * .20f);
                    g.DrawLine(pen, x + size * .23f, top + size * .10f, x + size * .30f, top + size * .10f);
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ModernTheme.Changed -= UpdateTheme; ReleaseEmphasisFont(); }
        base.Dispose(disposing);
    }
}

internal sealed class WorkbenchRoomItem
{
    internal string Name, Room, Meta, State;
    internal bool Live;
    internal Color Accent;
}

internal sealed class WorkbenchRoomBrowser : ScrollableControl, IDpiScaled
{
    private sealed class ItemLayout
    {
        internal Rectangle Bounds, Mpv, PotPlayer, Select;
    }

    private readonly List<WorkbenchRoomItem> items = new List<WorkbenchRoomItem>();
    private readonly List<ItemLayout> layouts = new List<ItemLayout>();
    private bool cards = true, arranging;
    private int selectedIndex = -1, columns = 1, hoverItem = -1, hoverAction, pressedItem = -1, pressedAction;
    private Font boldFont, smallFont;
    private string fontKey;
    // Draft metrics: 3 columns of room cards, 92px cover, 198px card height.
    private const int CardHeight = 198, CardCover = 92;

    public WorkbenchRoomBrowser()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        AutoScroll = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "直播房间";
        DpiLayout.SetDesignFont(this, WorkbenchFonts.Body);
        ModernTheme.Changed += UpdateTheme;
        UpdateTheme();
    }

    public bool Cards
    {
        get { return cards; }
        set { if (cards != value) { cards = value; ArrangeItems(); } }
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set { SelectIndex(value, true); }
    }

    public List<WorkbenchRoomItem> Items { get { return items; } }

    /// <summary>Height a single row of room cards needs, including the 2px inset.</summary>
    internal int CardsRowHeight { get { return Px(2) * 2 + Px(CardHeight); } }
    public event Action<int> SelectionChanged;
    public event Action<int, string> OpenRequested;
    public event Action<int> ChooseRequested;
    public event Action DeleteRequested;

    public void SetItems(List<WorkbenchRoomItem> values)
    {
        int previous = selectedIndex;
        if (ReferenceEquals(values, items)) values = new List<WorkbenchRoomItem>(values);
        items.Clear();
        if (values != null) foreach (WorkbenchRoomItem item in values) if (item != null) items.Add(item);
        selectedIndex = previous >= 0 && previous < items.Count ? previous : -1;
        hoverItem = pressedItem = -1;
        hoverAction = pressedAction = 0;
        ArrangeItems();
        UpdateAccessibleDescription();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
    }

    public void OnDpiScaled(float scale)
    {
        ReleaseFonts();
        ArrangeItems();
        if (IsHandleCreated) ModernTheme.ApplyScrollbarTheme(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ModernTheme.ApplyScrollbarTheme(this);
        ArrangeItems();
    }

    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ReleaseFonts(); ArrangeItems(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); ArrangeItems(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ReleaseFonts(); Invalidate(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); ArrangeItems(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); hoverItem = pressedItem = -1; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressedItem = -1; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    private int Px(int value) { return DpiLayout.S(this, value); }

    private void ArrangeItems()
    {
        if (arranging || IsDisposed) return;
        arranging = true;
        try
        {
            // A vertical scrollbar can change ClientSize while the minimum is
            // assigned. Recompute from that resulting width before painting.
            for (int pass = 0; pass < 3; pass++)
            {
                int clientWidth = ClientSize.Width;
                int inset = Px(2), gap = Px(12);
                int available = Math.Max(1, clientWidth - inset * 2);
                // Three cards per row on wide layouts (the draft's fixed grid);
                // only collapse when the content column genuinely runs out.
                columns = cards ? (available >= Px(660) ? 3 : available >= Px(420) ? 2 : 1) : 1;
                int width = Math.Max(1, (available - (columns - 1) * gap) / columns);
                bool narrow = !cards && width < Px(540);
                int height = Px(cards ? CardHeight : narrow ? 122 : 90);
                layouts.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    ItemLayout layout = new ItemLayout();
                    layout.Bounds = new Rectangle(inset + (i % columns) * (width + gap), inset + (i / columns) * (height + gap), width, height);
                    int inner = Px(12), buttonGap = Px(8);
                    if (cards)
                    {
                        int buttonWidth = Math.Max(1, (width - inner * 2 - buttonGap) / 2);
                        int top = layout.Bounds.Bottom - inner - Px(30);
                        layout.Mpv = new Rectangle(layout.Bounds.Left + inner, top, buttonWidth, Px(30));
                        layout.PotPlayer = new Rectangle(layout.Mpv.Right + buttonGap, top, buttonWidth, Px(30));
                        // The select pill sits at the bottom-right of the cover,
                        // 12px in from both edges (draft .wb-cover).
                        layout.Select = new Rectangle(layout.Bounds.Right - inner - Px(52),
                            layout.Bounds.Top + Px(CardCover) - Px(12) - Px(26), Px(52), Px(26));
                    }
                    else if (narrow)
                    {
                        int buttonHeight = Px(29);
                        int buttonWidth = Math.Max(1, (width - inner * 2 - buttonGap) / 2);
                        int top = layout.Bounds.Bottom - inner - buttonHeight;
                        layout.Mpv = new Rectangle(layout.Bounds.Left + inner, top, buttonWidth, buttonHeight);
                        layout.PotPlayer = new Rectangle(layout.Mpv.Right + buttonGap, top, buttonWidth, buttonHeight);
                    }
                    else
                    {
                        int buttonHeight = Px(29), buttonWidth = Px(102);
                        int top = layout.Bounds.Top + (height - buttonHeight) / 2;
                        layout.PotPlayer = new Rectangle(layout.Bounds.Right - inner - buttonWidth, top, buttonWidth, buttonHeight);
                        layout.Mpv = new Rectangle(layout.PotPlayer.Left - buttonGap - buttonWidth, top, buttonWidth, buttonHeight);
                    }
                    layouts.Add(layout);
                }
                int rows = (items.Count + columns - 1) / columns;
                int contentHeight = rows == 0 ? 0 : inset * 2 + rows * height + (rows - 1) * gap;
                Size minimum = new Size(0, contentHeight);
                if (AutoScrollMinSize != minimum) AutoScrollMinSize = minimum;
                if (ClientSize.Width == clientWidth) break;
            }
            Invalidate();
        }
        finally { arranging = false; }
    }

    private void EnsureFonts()
    {
        float pixels = Font.Unit == GraphicsUnit.Pixel ? Font.Size : Font.SizeInPoints * 96f * DpiLayout.For(this) / 72f;
        string key = Font.FontFamily.Name + "|" + Font.Style + "|" + pixels.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (key == fontKey && boldFont != null && smallFont != null) return;
        ReleaseFonts();
        fontKey = key;
        boldFont = new Font(Font.FontFamily, pixels + Px(1), FontStyle.Bold, GraphicsUnit.Pixel);
        smallFont = new Font(Font.FontFamily, Math.Max(Px(10), pixels - Px(1)), Font.Style, GraphicsUnit.Pixel);
    }

    private void ReleaseFonts()
    {
        if (boldFont != null) { boldFont.Dispose(); boldFont = null; }
        if (smallFont != null) { smallFont.Dispose(); smallFont = null; }
        fontKey = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(ModernTheme.Surface);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        EnsureFonts();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Point scroll = AutoScrollPosition;
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, cards ? "还没有收藏，输入房间号后可加入收藏。" : "还没有最近播放记录。", Font,
                new Rectangle(Px(12), Px(18), Math.Max(0, ClientSize.Width - Px(24)), Px(60)), ModernTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        for (int index = 0; index < layouts.Count && index < items.Count; index++)
        {
            ItemLayout layout = layouts[index];
            Rectangle box = layout.Bounds; box.Offset(scroll);
            if (!box.IntersectsWith(e.ClipRectangle)) continue;
            DrawItem(g, index, layout, box, scroll);
        }
    }

    private void DrawItem(Graphics g, int index, ItemLayout layout, Rectangle box, Point scroll)
    {
        WorkbenchRoomItem item = items[index];
        bool chosen = selectedIndex == index;
        Rectangle cardBox = new Rectangle(box.Left, box.Top, Math.Max(1, box.Width - 1), Math.Max(1, box.Height - 1));
        using (GraphicsPath path = ModernTheme.RoundRect(cardBox, Px(11)))
        using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
        using (Pen border = new Pen(chosen ? ModernTheme.Accent : hoverItem == index ? ModernTheme.BorderHover : ModernTheme.Border,
            Math.Max(1f, DpiLayout.For(this))))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        int inner = Px(12);
        Rectangle mpv = layout.Mpv; mpv.Offset(scroll);
        Rectangle pot = layout.PotPlayer; pot.Offset(scroll);
        if (cards)
        {
            Color accent = item.Accent.A == 0 ? ModernTheme.Accent : item.Accent;
            int coverHeight = Px(CardCover);
            Rectangle cover = new Rectangle(box.Left + Px(1), box.Top + Px(1), Math.Max(0, box.Width - Px(2)), coverHeight);
            using (GraphicsPath clip = ModernTheme.RoundRect(cardBox, Px(11)))
            {
                GraphicsState saved = g.Save();
                try
                {
                    g.SetClip(clip, CombineMode.Intersect);
                    using (LinearGradientBrush gradient = new LinearGradientBrush(cover,
                        WorkbenchPalette.Blend(accent, ModernTheme.Card, .55f),
                        WorkbenchPalette.Blend(accent, ModernTheme.Card, .88f), 22f))
                        g.FillRectangle(gradient, cover);
                }
                finally { g.Restore(saved); }
            }
            DrawGamepad(g, new Rectangle(box.Left + Px(15), box.Top + coverHeight - Px(13) - Px(20), Px(26), Px(20)),
                Color.FromArgb(205, ModernTheme.TextSecondary));
            DrawStatePill(g, item, box, inner);
            Rectangle choose = layout.Select; choose.Offset(scroll);
            using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(choose.Left, choose.Top, Math.Max(1, choose.Width - 1), Math.Max(1, choose.Height - 1)), Px(6)))
            using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
            using (Pen border = new Pen(ModernTheme.Border, Math.Max(1f, DpiLayout.For(this))))
            {
                g.FillPath(fill, path); g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, "选择", smallFont, choose, ModernTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int bodyTop = box.Top + coverHeight;
            DrawText(g, item.Name, boldFont, new Rectangle(box.Left + inner, bodyTop + Px(11), Math.Max(0, box.Width - inner * 2), Px(20)), ModernTheme.Text);
            DrawText(g, item.Meta, smallFont, new Rectangle(box.Left + inner, bodyTop + Px(32), Math.Max(0, box.Width - inner * 2), Px(17)), ModernTheme.TextSecondary);
        }
        else
        {
            bool narrow = box.Width < Px(540);
            int right = narrow ? box.Right - inner : mpv.Left - Px(14);
            int textWidth = Math.Max(0, right - box.Left - inner);
            int stateWidth = Math.Min(Px(100), Math.Max(0, textWidth / 3));
            DrawText(g, item.Name, boldFont, new Rectangle(box.Left + inner, box.Top + Px(10), Math.Max(0, textWidth - stateWidth - Px(6)), Px(23)), ModernTheme.Text);
            DrawState(g, item, new Rectangle(right - stateWidth, box.Top + Math.Max(0, (box.Height - Px(20)) / 2), stateWidth, Px(20)), false);
            DrawText(g, item.Meta, smallFont, new Rectangle(box.Left + inner, box.Top + Px(36), textWidth, Px(18)), ModernTheme.TextSecondary);
            DrawText(g, "房间 " + (item.Room ?? ""), smallFont, new Rectangle(box.Left + inner, box.Top + Px(57), textWidth, Px(18)), ModernTheme.TextSecondary);
        }
        DrawOpenButton(g, mpv, "▷  mpv", index, 1);
        DrawOpenButton(g, pot, "PotPlayer", index, 2);
        if (chosen && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(cardBox, -Px(4), -Px(4)), ModernTheme.Text, ModernTheme.Card);
    }

    private static Color Blend(Color first, Color second, float weight)
    {
        return WorkbenchPalette.Blend(first, second, weight);
    }

    private void DrawGamepad(Graphics g, Rectangle rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1.4f, 1.6f * DpiLayout.For(this))))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawArc(pen, rect.Left, rect.Top + rect.Height / 4, rect.Width, rect.Height, 20, 320);
            g.DrawLine(pen, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, rect.Left + rect.Width / 2 + 1, rect.Top + rect.Height / 2);
            using (SolidBrush dot = new SolidBrush(color))
            {
                g.FillEllipse(dot, rect.Left + rect.Width / 4 - Px(1), rect.Top + rect.Height / 2 - Px(1), Px(3), Px(3));
                g.FillEllipse(dot, rect.Right - rect.Width / 4 - Px(2), rect.Top + rect.Height / 2 - Px(2), Px(3), Px(3));
            }
        }
    }

    private void DrawStatePill(Graphics g, WorkbenchRoomItem item, Rectangle box, int inner)
    {
        if (string.IsNullOrEmpty(item.State)) return;
        TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int text = TextRenderer.MeasureText(g, item.State, smallFont, Size.Empty, flags).Width;
        int width = Math.Min(Math.Max(Px(52), text + Px(24)), Math.Max(Px(40), box.Width - inner * 2 - Px(52)));
        Rectangle pill = new Rectangle(box.Right - Px(9) - width, box.Top + Px(9), width, Px(22));
        using (GraphicsPath path = ModernTheme.RoundRect(pill, Px(5)))
        using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
            g.FillPath(fill, path);
        Color color = Enabled ? item.Live ? ModernTheme.Success : ModernTheme.TextSecondary : ModernTheme.TextDisabled;
        using (SolidBrush dot = new SolidBrush(color))
            g.FillEllipse(dot, pill.Left + Px(8), pill.Top + (pill.Height - Px(5)) / 2, Px(5), Px(5));
        DrawText(g, item.State, smallFont, new Rectangle(pill.Left + Px(16), pill.Top, pill.Width - Px(18), pill.Height), color);
    }

    private void DrawScreen(Graphics g, Rectangle rect, Color color)
    {
        using (Pen pen = new Pen(color, Math.Max(1f, 1.25f * DpiLayout.For(this))))
        {
            g.DrawRectangle(pen, rect.Left, rect.Top, rect.Width, rect.Height - Px(5));
            g.DrawLine(pen, rect.Left + rect.Width / 2, rect.Bottom - Px(4), rect.Left + rect.Width / 2, rect.Bottom);
            g.DrawLine(pen, rect.Left + Px(6), rect.Bottom, rect.Right - Px(6), rect.Bottom);
        }
    }

    private void DrawState(Graphics g, WorkbenchRoomItem item, Rectangle rect, bool rightAligned)
    {
        if (rect.Width <= Px(8) || string.IsNullOrEmpty(item.State)) return;
        Color color = Enabled ? item.Live ? ModernTheme.Success : ModernTheme.TextSecondary : ModernTheme.TextDisabled;
        TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        int measured = TextRenderer.MeasureText(g, item.State, smallFont, Size.Empty, flags).Width;
        int textWidth = Math.Min(measured, Math.Max(0, rect.Width - Px(11)));
        int start = rightAligned ? rect.Right - textWidth - Px(11) : rect.Left;
        using (SolidBrush brush = new SolidBrush(color)) g.FillEllipse(brush, start, rect.Top + (rect.Height - Px(5)) / 2, Px(5), Px(5));
        DrawText(g, item.State, smallFont, new Rectangle(start + Px(10), rect.Top, textWidth, rect.Height), color);
    }

    private static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        TextRenderer.DrawText(g, text ?? "", font, rect, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private void DrawOpenButton(Graphics g, Rectangle rect, string text, int index, int action)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        bool hover = hoverItem == index && hoverAction == action;
        bool pressed = pressedItem == index && pressedAction == action;
        using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(rect.Left, rect.Top, Math.Max(1, rect.Width - 1), Math.Max(1, rect.Height - 1)), Px(6)))
        using (SolidBrush fill = new SolidBrush(pressed ? ModernTheme.Selection : hover ? ModernTheme.Hover : ModernTheme.Card))
        using (Pen border = new Pen(hover ? ModernTheme.BorderHover : ModernTheme.Border, Math.Max(1f, DpiLayout.For(this))))
        {
            g.FillPath(fill, path); g.DrawPath(border, path);
        }
        TextRenderer.DrawText(g, text, smallFont, rect, Enabled ? ModernTheme.Text : ModernTheme.TextDisabled,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private int HitTest(Point point, out int action)
    {
        action = 0;
        Point content = new Point(point.X - AutoScrollPosition.X, point.Y - AutoScrollPosition.Y);
        for (int i = 0; i < layouts.Count; i++) if (layouts[i].Bounds.Contains(content))
        {
            if (layouts[i].Mpv.Contains(content)) action = 1;
            else if (layouts[i].PotPlayer.Contains(content)) action = 2;
            else if (layouts[i].Select.Contains(content)) action = 3;
            return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int action, index = HitTest(e.Location, out action);
        if (hoverItem != index || hoverAction != action) { hoverItem = index; hoverAction = action; Invalidate(); }
        Cursor = index >= 0 && Enabled ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverItem = pressedItem = -1; hoverAction = pressedAction = 0; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Focus();
            int action, index = HitTest(e.Location, out action);
            pressedItem = index; pressedAction = action;
            if (index >= 0) SelectIndex(index, false);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int action, index = HitTest(e.Location, out action);
        bool open = Enabled && e.Button == MouseButtons.Left && index >= 0
            && index == pressedItem && action == pressedAction && action > 0;
        pressedItem = -1; pressedAction = 0; Invalidate();
        if (open && action == 3) RequestChoose(index);
        else if (open) RequestOpen(index, action == 2 ? "potplayer" : "mpv");
        base.OnMouseUp(e);
    }

    private void RequestChoose(int index)
    {
        if (index < 0 || index >= items.Count) return;
        SelectIndex(index, false);
        Action<int> choose = ChooseRequested;
        if (choose != null) choose(index);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            int action, index = HitTest(e.Location, out action);
            if (index >= 0 && action == 0) RequestOpen(index, "mpv");
        }
        base.OnMouseDoubleClick(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right
            || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown
            || key == Keys.Enter || key == Keys.Delete) return true;
        return base.IsInputKey(keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Enter && Enabled && selectedIndex >= 0)
        {
            RequestOpen(selectedIndex, (keyData & Keys.Shift) != 0 ? "potplayer" : "mpv");
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled) { base.OnKeyDown(e); return; }
        int next = selectedIndex, step = cards ? columns : 1;
        bool handled = true;
        if (e.KeyCode == Keys.Enter) RequestOpen(selectedIndex, e.Shift ? "potplayer" : "mpv");
        else if (e.KeyCode == Keys.Delete)
        {
            if (selectedIndex >= 0 && selectedIndex < items.Count) { Action handler = DeleteRequested; if (handler != null) handler(); }
        }
        else if (e.KeyCode == Keys.Home) next = 0;
        else if (e.KeyCode == Keys.End) next = items.Count - 1;
        else if (e.KeyCode == Keys.Up) next = selectedIndex < 0 ? 0 : selectedIndex - step;
        else if (e.KeyCode == Keys.Down) next = selectedIndex < 0 ? 0 : selectedIndex + step;
        else if (e.KeyCode == Keys.Left) next = selectedIndex < 0 ? 0 : selectedIndex - 1;
        else if (e.KeyCode == Keys.Right) next = selectedIndex < 0 ? 0 : selectedIndex + 1;
        else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown)
        {
            int height = layouts.Count == 0 ? Px(90) : layouts[0].Bounds.Height + Px(12);
            int page = Math.Max(1, ClientSize.Height / Math.Max(1, height)) * step;
            next = selectedIndex < 0 ? 0 : selectedIndex + (e.KeyCode == Keys.PageUp ? -page : page);
        }
        else handled = false;
        if (handled)
        {
            if (items.Count > 0 && next != selectedIndex) SelectIndex(Math.Max(0, Math.Min(items.Count - 1, next)), true);
            e.Handled = e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    private void SelectIndex(int index, bool scroll)
    {
        if (index < -1 || index >= items.Count) index = -1;
        bool changed = index != selectedIndex;
        selectedIndex = index;
        if (scroll && index >= 0 && index < layouts.Count)
        {
            Rectangle bounds = layouts[index].Bounds;
            int top = -AutoScrollPosition.Y, next = top;
            if (bounds.Top < top) next = bounds.Top;
            else if (bounds.Bottom > top + ClientSize.Height) next = Math.Max(0, bounds.Bottom - ClientSize.Height);
            if (next != top) AutoScrollPosition = new Point(0, next);
        }
        UpdateAccessibleDescription();
        Invalidate();
        if (changed)
        {
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Selection, index + 1);
            Action<int> handler = SelectionChanged; if (handler != null) handler(index);
        }
    }

    private void RequestOpen(int index, string player)
    {
        if (!Enabled || index < 0 || index >= items.Count) return;
        SelectIndex(index, true);
        Action<int, string> handler = OpenRequested;
        if (handler != null) handler(index, player);
    }

    private void UpdateAccessibleDescription()
    {
        if (selectedIndex >= 0 && selectedIndex < items.Count)
        {
            WorkbenchRoomItem item = items[selectedIndex];
            AccessibleDescription = (item.Name ?? "") + "，房间 " + (item.Room ?? "") + "，" + (item.State ?? "")
                + "。Enter 用 mpv 打开，Shift+Enter 用 PotPlayer 打开" + (cards ? "，Delete 删除收藏。" : "。");
        }
        else AccessibleDescription = "共 " + items.Count + " 个房间。方向键选择，Enter 用 mpv 打开，Shift+Enter 用 PotPlayer 打开。";
    }

    private void UpdateTheme()
    {
        if (IsDisposed) return;
        BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Text;
        if (IsHandleCreated) ModernTheme.ApplyScrollbarTheme(this);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() { return new BrowserAccessibleObject(this); }

    private Rectangle AccessibleBounds(int index, int action)
    {
        if (!IsHandleCreated || index < 0 || index >= layouts.Count) return Rectangle.Empty;
        Rectangle rect = action == 1 ? layouts[index].Mpv : action == 2 ? layouts[index].PotPlayer : layouts[index].Bounds;
        rect.Offset(AutoScrollPosition);
        return RectangleToScreen(rect);
    }

    private sealed class BrowserAccessibleObject : ControlAccessibleObject
    {
        private readonly WorkbenchRoomBrowser browser;
        internal BrowserAccessibleObject(WorkbenchRoomBrowser owner) : base(owner) { browser = owner; }
        public override int GetChildCount() { return browser.items.Count; }
        public override AccessibleObject GetChild(int index)
        { return index >= 0 && index < browser.items.Count ? new ItemAccessibleObject(browser, index, 0) : null; }
        public override AccessibleObject GetSelected() { return GetChild(browser.selectedIndex); }
        public override AccessibleObject GetFocused() { return browser.Focused ? GetSelected() : null; }
        public override AccessibleObject HitTest(int x, int y)
        {
            if (!browser.IsHandleCreated) return null;
            int action, index = browser.HitTest(browser.PointToClient(new Point(x, y)), out action);
            return index >= 0 ? new ItemAccessibleObject(browser, index, action) : base.HitTest(x, y);
        }
    }

    private sealed class ItemAccessibleObject : AccessibleObject
    {
        private readonly WorkbenchRoomBrowser browser;
        private readonly int index, action;
        internal ItemAccessibleObject(WorkbenchRoomBrowser owner, int item, int command) { browser = owner; index = item; action = command; }
        private WorkbenchRoomItem Item { get { return index >= 0 && index < browser.items.Count ? browser.items[index] : null; } }
        public override string Name
        {
            get { WorkbenchRoomItem item = Item; return item == null ? "" : action == 0 ? (item.Name ?? "") + "，房间 " + (item.Room ?? "") + "，" + (item.State ?? "") : (action == 1 ? "用 mpv 打开 " : "用 PotPlayer 打开 ") + (item.Name ?? ""); }
            set { }
        }
        public override string Value { get { WorkbenchRoomItem item = Item; return item == null ? "" : item.Meta ?? ""; } set { } }
        public override AccessibleRole Role { get { return action == 0 ? AccessibleRole.ListItem : AccessibleRole.PushButton; } }
        public override Rectangle Bounds { get { return browser.AccessibleBounds(index, action); } }
        public override AccessibleObject Parent { get { return action == 0 ? browser.AccessibilityObject : new ItemAccessibleObject(browser, index, 0); } }
        public override string DefaultAction { get { return action == 2 ? "用 PotPlayer 打开" : "用 mpv 打开"; } }
        public override AccessibleStates State
        {
            get
            {
                AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (!browser.Enabled) state |= AccessibleStates.Unavailable;
                if (browser.selectedIndex == index) state |= AccessibleStates.Selected;
                if (browser.Focused && browser.selectedIndex == index) state |= AccessibleStates.Focused;
                if (!browser.Visible) state |= AccessibleStates.Invisible;
                if (!browser.IsHandleCreated || !browser.RectangleToScreen(browser.ClientRectangle).IntersectsWith(Bounds)) state |= AccessibleStates.Offscreen;
                return state;
            }
        }
        public override int GetChildCount() { return action == 0 ? 2 : 0; }
        public override AccessibleObject GetChild(int child)
        { return action == 0 && child >= 0 && child < 2 ? new ItemAccessibleObject(browser, index, child + 1) : null; }
        public override void Select(AccessibleSelection flags)
        { if (Item != null) { if ((flags & AccessibleSelection.TakeFocus) != 0) browser.Focus(); browser.SelectIndex(index, true); } }
        public override void DoDefaultAction() { browser.RequestOpen(index, action == 2 ? "potplayer" : "mpv"); }
        public override AccessibleObject Navigate(AccessibleNavigation direction)
        {
            if (action == 0)
            {
                if (direction == AccessibleNavigation.FirstChild) return GetChild(0);
                if (direction == AccessibleNavigation.LastChild) return GetChild(1);
                int next = direction == AccessibleNavigation.Next || direction == AccessibleNavigation.Down ? index + 1
                    : direction == AccessibleNavigation.Previous || direction == AccessibleNavigation.Up ? index - 1 : -1;
                if (next >= 0 && next < browser.items.Count) return new ItemAccessibleObject(browser, next, 0);
            }
            else if ((direction == AccessibleNavigation.Next || direction == AccessibleNavigation.Right) && action == 1)
                return new ItemAccessibleObject(browser, index, 2);
            else if ((direction == AccessibleNavigation.Previous || direction == AccessibleNavigation.Left) && action == 2)
                return new ItemAccessibleObject(browser, index, 1);
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ModernTheme.Changed -= UpdateTheme; ReleaseFonts(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Workbench palette helpers. The draft uses a dedicated sidebar surface
/// (#171920 dark / #fbfbfd light) that sits between Surface and Card, plus the
/// generic fill tone for inset strips.
/// </summary>
internal static class WorkbenchPalette
{
    internal static Color Side { get { return Blend(ModernTheme.Surface, ModernTheme.Card, .65f); } }

    internal static Color Blend(Color first, Color second, float weight)
    {
        return Color.FromArgb((int)Math.Round(first.R * (1 - weight) + second.R * weight),
            (int)Math.Round(first.G * (1 - weight) + second.G * weight),
            (int)Math.Round(first.B * (1 - weight) + second.B * weight));
    }
}

/// <summary>
/// Workbench typography. The interactive draft renders with the browser's
/// "Segoe UI","Microsoft YaHei UI" stack: body text is a thin YaHei UI regular,
/// not the HarmonyOS Medium used by the danmaku overlay. Workbench chrome, nav,
/// labels and buttons all go through here so a missing YaHei falls back cleanly.
/// </summary>
internal static class WorkbenchFonts
{
    private static readonly string family = PickFamily();

    // Shared design fonts for custom controls built in their constructors.
    internal static readonly Font Body = Create(12f, FontStyle.Regular);
    internal static readonly Font Nav = Create(10.5f, FontStyle.Regular);

    internal static string FamilyName { get { return family; } }

    internal static Font Create(float pixels, FontStyle style)
    {
        return new Font(family, pixels, style, GraphicsUnit.Pixel);
    }

    private static string PickFamily()
    {
        try
        {
            using (InstalledFontCollection installed = new InstalledFontCollection())
            {
                bool yaheiUi = false, yahei = false;
                foreach (FontFamily candidate in installed.Families)
                {
                    if (string.Equals(candidate.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)) yaheiUi = true;
                    else if (string.Equals(candidate.Name, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase)) yahei = true;
                }
                if (yaheiUi) return "Microsoft YaHei UI";
                if (yahei) return "Microsoft YaHei";
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        return FontFamily.GenericSansSerif.Name;
    }
}

/// <summary>
/// Flat shell strip: the draft's sidebar and 34px top chrome share one surface
/// colour with a single hairline edge (right for the sidebar, bottom for chrome).
/// </summary>
internal sealed class WorkbenchStrip : Panel
{
    internal enum EdgeKind { Right, Bottom }

    private EdgeKind edge = EdgeKind.Bottom;

    internal WorkbenchStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ModernTheme.Changed += UpdateTheme;
        UpdateTheme();
    }

    internal EdgeKind Edge
    {
        get { return edge; }
        set { if (edge != value) { edge = value; Invalidate(); } }
    }

    private void UpdateTheme()
    {
        if (IsDisposed) return;
        BackColor = WorkbenchPalette.Side;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(WorkbenchPalette.Side);
        if (Width < 2 || Height < 2) return;
        using (Pen line = new Pen(ModernTheme.Border, Math.Max(1f, DpiLayout.For(this))))
        {
            if (edge == EdgeKind.Right) e.Graphics.DrawLine(line, Width - 1, 0, Width - 1, Height);
            else e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= UpdateTheme;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Static rounded fill used behind grouped pill controls (draft .wb-platforms).
/// Purely decorative: it never takes input and sits behind its sibling buttons.
/// </summary>
internal sealed class WorkbenchFillStrip : Control
{
    internal WorkbenchFillStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        ModernTheme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() { if (!IsDisposed) Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent == null ? ModernTheme.Card : Parent.BackColor);
        if (Width < 2 || Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), DpiLayout.S(this, 9)))
        using (SolidBrush fill = new SolidBrush(ModernTheme.Hover))
            g.FillPath(fill, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Small vector glyph used for the chrome app icon, the latest-session avatar and
/// the sidebar footer. Drawn with the same hairline style as the nav glyphs so the
/// shell does not depend on an icon font or embedded image.
/// </summary>
internal sealed class WorkbenchGlyph : Control
{
    private string glyph = "monitor-play";
    private bool tiled = true;
    private int iconSize;

    internal WorkbenchGlyph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ModernTheme.Changed += OnThemeChanged;
    }

    internal string Glyph
    {
        get { return glyph; }
        set { glyph = value ?? ""; Invalidate(); }
    }

    /// <summary>Paint a soft rounded tile behind the glyph (draft .wb-avatar).</summary>
    internal bool Tiled
    {
        get { return tiled; }
        set { tiled = value; Invalidate(); }
    }

    /// <summary>Glyph edge in design pixels; 0 lets the control size decide.</summary>
    internal int IconSize
    {
        get { return iconSize; }
        set { iconSize = Math.Max(0, value); Invalidate(); }
    }

    private int Px(int value) { return DpiLayout.S(this, value); }

    private void OnThemeChanged() { if (!IsDisposed) Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor);
        if (Width < 2 || Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        if (tiled)
        {
            using (GraphicsPath tile = ModernTheme.RoundRect(box, Px(9)))
            using (SolidBrush fill = new SolidBrush(ModernTheme.Hover))
                g.FillPath(fill, tile);
        }
        int size = iconSize > 0 ? Px(iconSize) : Math.Max(Px(12), (int)Math.Round(Math.Min(Width, Height) * .44f));
        size = Math.Min(size, Math.Min(Width, Height));
        Rectangle icon = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        DrawGlyph(g, icon, tiled ? ModernTheme.TextSecondary : ModernTheme.TextSecondary);
    }

    private void DrawGlyph(Graphics g, Rectangle box, Color color)
    {
        float scale = DpiLayout.For(this);
        float x = box.Left, y = box.Top, w = box.Width, h = box.Height;
        using (Pen pen = new Pen(color, Math.Max(1f, 1.25f * scale)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            string key = (glyph ?? "").ToLowerInvariant();
            if (key == "drive")
            {
                g.DrawRectangle(pen, x + w * .08f, y + h * .22f, w * .84f, h * .56f);
                g.DrawLine(pen, x + w * .22f, y + h * .5f, x + w * .44f, y + h * .5f);
                using (SolidBrush dot = new SolidBrush(color))
                    g.FillEllipse(dot, x + w * .62f, y + h * .44f, w * .12f, h * .12f);
                return;
            }
            // monitor-play: screen, stand and a play triangle inside.
            g.DrawRectangle(pen, x + w * .08f, y + h * .1f, w * .84f, h * .6f);
            g.DrawLine(pen, x + w * .5f, y + h * .71f, x + w * .5f, y + h * .87f);
            g.DrawLine(pen, x + w * .3f, y + h * .9f, x + w * .7f, y + h * .9f);
            g.DrawPolygon(pen, new PointF[]
            {
                new PointF(x + w * .42f, y + h * .24f),
                new PointF(x + w * .42f, y + h * .56f),
                new PointF(x + w * .64f, y + h * .4f)
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

internal static class WorkbenchWindowDrag
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    internal static void Begin(Form form)
    {
        if (form == null || !form.IsHandleCreated) return;
        ReleaseCapture();
        SendMessage(form.Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    internal static void ToggleMaximize(Form form)
    {
        if (form == null) return;
        if (form.WindowState == FormWindowState.Maximized) { form.WindowState = FormWindowState.Normal; return; }
        form.WindowState = FormWindowState.Maximized;
    }
}

internal sealed class TrafficDot : Control
{
    private readonly string kind;
    private bool hot;
    internal TrafficDot(string kind, Action click)
    {
        this.kind = kind ?? "close";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = kind == "close" ? "关闭" : kind == "max" ? "最大化" : "最小化";
        Click += delegate { if (click != null) click(); };
    }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : ModernTheme.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color fill = kind == "close" ? Color.FromArgb(0xFF, 0x5F, 0x57)
            : kind == "max" ? Color.FromArgb(0x28, 0xC8, 0x40)
            : Color.FromArgb(0xFE, 0xBC, 0x2E);
        int size = Math.Min(Width, Height) - DpiLayout.S(this, 2);
        if (size < 4) return;
        Rectangle circle = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        using (SolidBrush brush = new SolidBrush(fill)) g.FillEllipse(brush, circle);
        if (!hot) return;
        using (Pen pen = new Pen(Color.FromArgb(0x4A, 0x24, 0x24), Math.Max(1, DpiLayout.S(this, 2))))
        {
            int c = Math.Max(2, size / 2);
            if (kind == "close") g.DrawLine(pen, circle.Left + c - 2, circle.Top + c - 2, circle.Right - c + 2, circle.Bottom - c + 2);
            else if (kind == "max") g.DrawRectangle(pen, circle.Left + c - 3, circle.Top + c - 3, 6, 6);
            else g.DrawLine(pen, circle.Left + c - 3, circle.Top + c, circle.Right - c + 3, circle.Top + c);
        }
    }
}
