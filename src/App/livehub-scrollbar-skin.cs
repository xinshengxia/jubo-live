using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Keep native scrolling, wheel, keyboard and drag behavior; only paint its non-client skin.
internal sealed class ModernScrollbarSkin : NativeWindow
{
    private static readonly ConditionalWeakTable<Control, ModernScrollbarSkin> Skins
        = new ConditionalWeakTable<Control, ModernScrollbarSkin>();
    private readonly Control owner;
    private bool painting;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScrollbarInfo
    {
        public int Size; public Rect Bounds; public int ArrowSize, ThumbTop, ThumbBottom, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] State;
    }
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc, int state);
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr hwnd, int id, ref ScrollbarInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    internal static void Apply(Control control)
    {
        if (control == null || !control.IsHandleCreated) return;
        ModernScrollbarSkin skin = Skins.GetValue(control, delegate(Control c) { return new ModernScrollbarSkin(c); });
        if (skin.Handle != control.Handle)
        {
            if (skin.Handle != IntPtr.Zero) skin.ReleaseHandle();
            skin.AssignHandle(control.Handle);
        }
        RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, 0x0401); // RDW_FRAME | RDW_INVALIDATE
    }
    private ModernScrollbarSkin(Control control)
    {
        owner = control;
        owner.HandleDestroyed += delegate { if (Handle != IntPtr.Zero) ReleaseHandle(); };
        owner.Disposed += delegate { if (Handle != IntPtr.Zero) ReleaseHandle(); };
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (painting || Handle == IntPtr.Zero || owner.IsDisposed) return;
        if (m.Msg == 0x0085 || m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318
            || m.Msg == 0x0114 || m.Msg == 0x0115 || m.Msg == 0x00A0)
        {
            painting = true;
            IntPtr dc = IntPtr.Zero;
            bool ownsDc = false; int savedDc = 0;
            try
            {
                bool supplied = (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero;
                ownsDc = !supplied;
                dc = supplied ? m.WParam : GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                savedDc = SaveDC(dc);
                using (Graphics graphics = Graphics.FromHdc(dc))
                {
                    int styles = GetWindowLong(Handle, -16);
                    if ((styles & 0x00200000) != 0) PaintScrollbar(graphics, true);
                    if ((styles & 0x00100000) != 0) PaintScrollbar(graphics, false);
                }

            }
            catch (Exception ex) { Diagnostics.Report("ui-scrollbar-paint", ex); }
            finally
            {
                if (savedDc != 0) RestoreDC(dc, savedDc);
                if (ownsDc && dc != IntPtr.Zero) ReleaseDC(Handle, dc);
                painting = false;
            }
        }
    }
    private void PaintScrollbar(Graphics graphics, bool vertical)
    {
        Rect window;
        ScrollbarInfo info = new ScrollbarInfo(); info.Size = Marshal.SizeOf(typeof(ScrollbarInfo)); info.State = new uint[6];
        if (!GetWindowRect(Handle, out window) || !GetScrollBarInfo(Handle, vertical ? -5 : -6, ref info)) return;
        Rectangle bounds = Rectangle.FromLTRB(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top,
            info.Bounds.Right - window.Left, info.Bounds.Bottom - window.Top);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Color surface = owner.BackColor;
        using (SolidBrush background = new SolidBrush(surface)) graphics.FillRectangle(background, bounds);
        int thickness = Math.Min(vertical ? bounds.Width : bounds.Height, Math.Max(3, DpiLayout.S(owner, 4)));
        Rectangle thumb = vertical
            ? new Rectangle(bounds.Left + (bounds.Width - thickness) / 2, bounds.Top + info.ThumbTop, thickness, info.ThumbBottom - info.ThumbTop)
            : new Rectangle(bounds.Left + info.ThumbTop, bounds.Top + (bounds.Height - thickness) / 2, info.ThumbBottom - info.ThumbTop, thickness);
        if (thumb.Width > 0 && thumb.Height > 0)
        {
            Color muted = Color.FromArgb((surface.R * 2 + ModernTheme.TextSecondary.R) / 3,
                (surface.G * 2 + ModernTheme.TextSecondary.G) / 3, (surface.B * 2 + ModernTheme.TextSecondary.B) / 3);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var shape = ModernTheme.RoundRect(thumb, Math.Max(1, thickness / 2)))
            using (SolidBrush brush = new SolidBrush(muted)) graphics.FillPath(brush, shape);
        }
    }
}
