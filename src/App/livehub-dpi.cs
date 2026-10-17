using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// All ordinary windows are constructed at 96 DPI. This is the sole scaling owner;
// WinForms automatic scaling must not also scale the same controls.
internal class DpiForm : Form
{
    private const int WmDpiChanged = 0x02E0;
    private const int WmGetDpiScaledSize = 0x02E4;
    private readonly List<Metric> metrics = new List<Metric>();
    private readonly List<Font> ownedFonts = new List<Font>();
    private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
    private Size designMinimum;
    private bool initialized, applying, preparing;
    private bool updatingDpi, processingDpiMessage;
    private SizeF logicalClientSize;
    internal float RenderScale = 1f;
    internal event EventHandler DpiUpdated;
    internal bool ApplyingDpi { get { return applying || preparing; } }
    internal bool FitWorkingArea = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref NativeRect rect, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);

    private sealed class Metric
    {
        internal Control Control;
        internal Rectangle Bounds;
        internal Padding Padding, Margin;
        internal Size Minimum, Maximum, ScrollMinimum;
        internal float Points;
        internal string Family;
        internal FontStyle Style;
        internal readonly List<float> Rows = new List<float>();
        internal readonly List<float> Columns = new List<float>();
        internal int ItemHeight, DropDownHeight;
    }

    internal DpiForm()
    {
        AutoScaleMode = AutoScaleMode.None; DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        ModernTheme.Changed += RefreshScrollbarTheme;
    }

    internal static int WindowDpi(Control control)
    {
        try
        {
            if (control != null && control.IsHandleCreated)
            {
                uint dpi = GetDpiForWindow(control.Handle);
                if (dpi >= 48 && dpi <= 960) return (int)dpi;
            }
        }
        catch (EntryPointNotFoundException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        catch (DllNotFoundException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        // This fallback is only for Windows versions without GetDpiForWindow.
        using (Graphics g = control != null && control.IsHandleCreated
            ? control.CreateGraphics() : Graphics.FromHwnd(IntPtr.Zero))
            return Math.Max(96, (int)Math.Round(g.DpiX));
    }

    private void RefreshScrollbarTheme() { if (!IsDisposed) ModernTheme.ApplyScrollbarTheme(this); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); ModernTheme.ApplyScrollbarTheme(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        InitializeDpi();
        base.OnLoad(e);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        // Retain the user's logical size, not intermediate DPI/scrollbar sizes.
        // A work-area clamp must not become the basis for the return transition.
        if (initialized && !updatingDpi && !processingDpiMessage && !applying && !preparing
            && WindowState == FormWindowState.Normal && RenderScale > 0
            && (syntheticDpi || WindowDpi(this) == (int)Math.Round(RenderScale * 96)))
            logicalClientSize = new SizeF(ClientSize.Width / RenderScale, ClientSize.Height / RenderScale);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Form.WndProc has already synchronized its DeviceDpi. Keep its event,
        // but cancel the framework's geometry/font scaling: ApplyDpi owns it.
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    internal void InitializeDpi()
    {
        if (initialized) return;
        IntPtr hwnd = Handle;
        ApplyDpi(WindowDpi(this), FitWorkingArea, null);
    }

    private static void CreateChildHandles(Control root)
    {
        foreach (Control child in root.Controls)
        {
            IntPtr hwnd = child.Handle; CreateChildHandles(child);
        }
    }

    private void CaptureMetrics(Control control)
    {
        Metric m = new Metric();
        m.Control = control; m.Bounds = control.Bounds;
        m.Padding = control.Padding; m.Margin = control.Margin;
        m.Minimum = control.MinimumSize; m.Maximum = control.MaximumSize;
        DpiLayout.FontSpec designFont = DpiLayout.DesignFont(control);
        m.Points = designFont.Points; m.Family = designFont.Family; m.Style = designFont.Style;
        ScrollableControl scroll = control as ScrollableControl;
        if (scroll != null) m.ScrollMinimum = scroll.AutoScrollMinSize;
        TableLayoutPanel table = control as TableLayoutPanel;
        if (table != null)
        {
            foreach (RowStyle row in table.RowStyles) m.Rows.Add(row.Height);
            foreach (ColumnStyle column in table.ColumnStyles) m.Columns.Add(column.Width);
        }
        ListBox list = control as ListBox;
        if (list != null) m.ItemHeight = list.ItemHeight;
        ComboBox combo = control as ComboBox;
        if (combo != null) { m.ItemHeight = combo.ItemHeight; m.DropDownHeight = combo.DropDownHeight; }
        metrics.Add(m);
        foreach (Control child in control.Controls) CaptureMetrics(child);
    }

    internal void ApplyDpi(int dpi, bool fit, Rectangle? suggested)
    {
        if (applying || IsDisposed) return;
        bool wasUpdating = updatingDpi;
        updatingDpi = true;
        try { ApplyDpiCore(dpi, fit, suggested); }
        finally { updatingDpi = wasUpdating; }
    }

    private void ApplyDpiCore(int dpi, bool fit, Rectangle? suggested)
    {
        if (!initialized)
        {
            logicalClientSize = new SizeF(ClientSize.Width / RenderScale, ClientSize.Height / RenderScale);
            preparing = true; SuspendLayout();
            try
            {
                CreateChildHandles(this);
                PerformLayout(); designMinimum = MinimumSize; CaptureMetrics(this); initialized = true;
            }
            finally { ResumeLayout(false); preparing = false; }
        }
        dpi = Math.Max(48, Math.Min(960, dpi));
        float previousScale = RenderScale;
        Dictionary<ScrollableControl, Point> scrollPositions = new Dictionary<ScrollableControl, Point>();
        foreach (Metric metric in metrics)
        {
            ScrollableControl scroll = metric.Control as ScrollableControl;
            if (scroll != null && !scroll.IsDisposed && scroll.AutoScroll)
                scrollPositions[scroll] = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y);
        }
        float next = Math.Max(0.5f, Math.Min(10f, dpi / 96f));
        applying = true; SuspendLayout();
        try
        {
            foreach (ScrollableControl scroll in scrollPositions.Keys) scroll.AutoScrollPosition = Point.Empty;
            RenderScale = next;
            foreach (Metric m in metrics)
            {
                Control c = m.Control;
                if (c.IsDisposed) continue;
                c.SuspendLayout();
                try
                {
                    // Explicit target pixel fonts avoid GDI's system-DPI point conversion
                    // on a second monitor. Each application happens from original metrics.
                    float pixels = m.Points * dpi / 72f;
                    string fontKey = m.Family + "|" + m.Style + "|" + pixels.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    Font font;
                    if (!fontCache.TryGetValue(fontKey, out font))
                    {
                        font = new Font(m.Family, pixels, m.Style, GraphicsUnit.Pixel);
                        fontCache[fontKey] = font; ownedFonts.Add(font);
                    }
                    // Control.Font ignores an equal replacement; retaining canonical
                    // instances until the controls are disposed prevents use-after-free.
                    c.Font = font;
                    c.Padding = ScalePadding(m.Padding, next); c.Margin = ScalePadding(m.Margin, next);
                    if (c != this)
                    {
                        c.MinimumSize = ScaleSize(m.Minimum, next); c.MaximumSize = ScaleSize(m.Maximum, next);
                        if (c.Dock == DockStyle.None && !c.AutoSize)
                            c.Bounds = ScaleRect(m.Bounds, next);
                    }
                    ScrollableControl scroll = c as ScrollableControl;
                    if (scroll != null && !m.ScrollMinimum.IsEmpty)
                        scroll.AutoScrollMinSize = ScaleSize(m.ScrollMinimum, next);
                    TableLayoutPanel table = c as TableLayoutPanel;
                    if (table != null)
                    {
                        for (int i = 0; i < m.Rows.Count; i++)
                            if (table.RowStyles[i].SizeType == SizeType.Absolute)
                                table.RowStyles[i].Height = m.Rows[i] * next;
                        for (int i = 0; i < m.Columns.Count; i++)
                            if (table.ColumnStyles[i].SizeType == SizeType.Absolute)
                                table.ColumnStyles[i].Width = m.Columns[i] * next;
                    }
                    IDpiScaled custom = c as IDpiScaled;
                    if (custom != null) custom.OnDpiScaled(next);
                    else
                    {
                        ListBox list = c as ListBox;
                        if (list != null && list.DrawMode == DrawMode.OwnerDrawFixed)
                            list.ItemHeight = Math.Max(1, Scale(m.ItemHeight, next));
                        ComboBox combo = c as ComboBox;
                        if (combo != null)
                        {
                            if (combo.DrawMode != DrawMode.Normal) combo.ItemHeight = Math.Max(1, Scale(m.ItemHeight, next));
                            combo.DropDownHeight = Math.Max(1, Scale(m.DropDownHeight, next));
                        }
                    }
                    c.Invalidate();
                }
                finally { c.ResumeLayout(false); }
            }
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size minimum = ScaleSize(designMinimum, next);
            if (fit) minimum = new Size(Math.Min(minimum.Width, work.Width), Math.Min(minimum.Height, work.Height));
            MinimumSize = minimum;
            if (suggested.HasValue) Bounds = suggested.Value;
            else ClientSize = ClientSizeAtDpi(dpi);
            if (fit)
            {
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)),
                    Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
            }
        }
        finally
        {
            ResumeLayout(true); applying = false;
        }
        EventHandler updated = DpiUpdated;
        if (updated != null) updated(this, EventArgs.Empty);
        PerformLayout();
        foreach (KeyValuePair<ScrollableControl, Point> entry in scrollPositions)
            if (!entry.Key.IsDisposed && entry.Value != Point.Empty)
                entry.Key.AutoScrollPosition = new Point(Scale(entry.Value.X, next / previousScale), Scale(entry.Value.Y, next / previousScale));
        Invalidate(true);
    }

    private bool syntheticDpi;
    internal void SetTestDpi(int dpi)
    {
        syntheticDpi = true; FitWorkingArea = false; IntPtr hwnd = Handle;
        ApplyDpi(dpi, false, null);
    }

    private Size ClientSizeAtDpi(int dpi)
    {
        return new Size(Math.Max(1, (int)Math.Round(logicalClientSize.Width * dpi / 96d)),
            Math.Max(1, (int)Math.Round(logicalClientSize.Height * dpi / 96d)));
    }

    private Size WindowSizeAtDpi(int dpi)
    {
        Size client = ClientSizeAtDpi(dpi);
        CreateParams cp = CreateParams;
        var rect = new NativeRect { Right = client.Width, Bottom = client.Height };
        try
        {
            if (AdjustWindowRectExForDpi(ref rect, cp.Style, GetMenu(Handle) != IntPtr.Zero, cp.ExStyle, (uint)dpi))
            {
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                // AdjustWindowRectExForDpi excludes standard scrollbars.
                if ((cp.Style & 0x00200000) != 0) width += GetSystemMetricsForDpi(2, (uint)dpi);
                if ((cp.Style & 0x00100000) != 0) height += GetSystemMetricsForDpi(3, (uint)dpi);
                return new Size(width, height);
            }
        }
        catch (EntryPointNotFoundException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        catch (DllNotFoundException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        return SizeFromClientSize(client);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x24 && syntheticDpi)
        {
            base.WndProc(ref m);
            MinMaxInfo limits = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
            limits.MaxTrackSize.X = limits.MaxTrackSize.Y = 32767;
            Marshal.StructureToPtr(limits, m.LParam, false); return;
        }
        if (m.Msg == WmGetDpiScaledSize && initialized && !syntheticDpi && WindowState == FormWindowState.Normal)
        {
            int dpi = Math.Max(48, Math.Min(960, (int)m.WParam.ToInt64()));
            Size size = WindowSizeAtDpi(dpi);
            Marshal.StructureToPtr(new NativePoint { X = size.Width, Y = size.Height }, m.LParam, false);
            m.Result = new IntPtr(1); return;
        }
        if (m.Msg == WmDpiChanged && syntheticDpi) { m.Result = IntPtr.Zero; return; }
        if (m.Msg == WmDpiChanged && initialized && !applying)
        {
            int dpi = (int)(m.WParam.ToInt64() & 0xffff);
            NativeRect r = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
            processingDpiMessage = true;
            try
            {
                // Synchronize framework DPI state even though our override cancels
                // its resizing. Older framework/OS paths still use the explicit work.
                base.WndProc(ref m);
                ApplyDpi(dpi, FitWorkingArea, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                QueueDpiReconcile();
            }
            finally { processingDpiMessage = false; }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private bool reconcileQueued;
    private void QueueDpiReconcile()
    {
        if (reconcileQueued || !IsHandleCreated || IsDisposed) return;
        reconcileQueued = true;
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                reconcileQueued = false;
                if (!IsDisposed && IsHandleCreated)
                {
                    int dpi = WindowDpi(this);
                    if (dpi != (int)Math.Round(RenderScale * 96)) ApplyDpi(dpi, FitWorkingArea, null);
                    else { PerformLayout(); Invalidate(true); }
                }
            });
        }
        catch (InvalidOperationException ex) { reconcileQueued = false; System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    internal static int Scale(int value, float scale) { return (int)Math.Round(value * scale); }
    private static Size ScaleSize(Size value, float scale) { return new Size(Scale(value.Width, scale), Scale(value.Height, scale)); }
    private static Rectangle ScaleRect(Rectangle r, float scale)
    {
        // Round edges, rather than rounding position and width separately.
        int left = Scale(r.Left, scale), top = Scale(r.Top, scale);
        return Rectangle.FromLTRB(left, top, Scale(r.Right, scale), Scale(r.Bottom, scale));
    }
    private static Padding ScalePadding(Padding p, float scale)
    {
        return new Padding(Scale(p.Left, scale), Scale(p.Top, scale), Scale(p.Right, scale), Scale(p.Bottom, scale));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= RefreshScrollbarTheme;
        base.Dispose(disposing);
        if (disposing) { foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); fontCache.Clear(); metrics.Clear(); }
    }
}
