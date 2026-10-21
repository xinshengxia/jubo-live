// --------------------------------------------------------------------------------------------
// 2026-09-26：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// --------------------------------------------------------------------------------------------
using System;
using System.Drawing;
using System.Reflection;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// ============================================================================================
// 现代风格的 WinForms 控件（自绘，零依赖）
//
// 为什么自己画：项目约束是 C# 5 + .NET Framework 4.8 + 零 NuGet，装不了任何 UI 库；
// 而 WinForms 默认控件是 2006 年的观感（3D 边框、方角、系统灰）。
// 弹幕覆盖层本来就是全自绘的（弹幕、SC 卡片），这里保持一致。
//
// ⚠️ **只改外观，不改布局度量。** danmaku-5-settings.cs 里的行列位置一个字没动 ——
//    因为离线测试断言「内容恰好放得下（needed == 可视高度）」，实测两者都是 530，
//    **没有余量**。想加分组标题就得先解决高度，那是另一件事。
// ============================================================================================

internal static class ModernTheme
{
    // ---- 深色 / 浅色两套调色板 ----
    //
    // 为什么用「两套常量 + 属性转发」而不是「运行时算颜色」：
    // 深色不是把浅色取反（那样会得到一堆脏灰），是**另一组独立挑过的值**。
    // 浅色取 Windows 11 的中性灰阶，深色取 Win11 深色主题的那几个档，不自创色值。
    private static bool _dark;

    // 浅色
    private static readonly Color LSurface = Color.FromArgb(0xF5, 0xF6, 0xFA);
    private static readonly Color LCard = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color LBorder = Color.FromArgb(0xE4, 0xE6, 0xEF);
    private static readonly Color LBorderHover = Color.FromArgb(0xB0, 0xB0, 0xB0);
    private static readonly Color LAccent = Color.FromArgb(0x79, 0x62, 0xDF);
    private static readonly Color LAccentHover = Color.FromArgb(0x87, 0x72, 0xE5);
    private static readonly Color LAccentPressed = Color.FromArgb(0x66, 0x50, 0xCA);
    private static readonly Color LOnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color LText = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color LTextSecondary = Color.FromArgb(0x66, 0x66, 0x66);
    private static readonly Color LTextDisabled = Color.FromArgb(0xA0, 0xA0, 0xA0);
    private static readonly Color LHover = Color.FromArgb(0xF0, 0xF0, 0xF0);
    private static readonly Color LSelection = Color.FromArgb(0xEE, 0xEB, 0xFB);
    private static readonly Color LDisabledFill = Color.FromArgb(0xC8, 0xD6, 0xE6);
    private static readonly Color LSuccess = Color.FromArgb(0x10, 0x7C, 0x10);
    private static readonly Color LDanger = Color.FromArgb(0xC4, 0x2B, 0x1C);

    // 深色
    private static readonly Color DSurface = Color.FromArgb(0x10, 0x12, 0x17);
    private static readonly Color DCard = Color.FromArgb(0x1B, 0x1E, 0x27);
    private static readonly Color DBorder = Color.FromArgb(0x2C, 0x31, 0x40);
    private static readonly Color DBorderHover = Color.FromArgb(0x5A, 0x5A, 0x5A);
    private static readonly Color DAccent = Color.FromArgb(0x79, 0x62, 0xDF);
    private static readonly Color DAccentHover = Color.FromArgb(0x87, 0x72, 0xE5);
    private static readonly Color DAccentPressed = Color.FromArgb(0x66, 0x50, 0xCA);
    private static readonly Color DOnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DText = Color.FromArgb(0xF2, 0xF2, 0xF2);
    private static readonly Color DTextSecondary = Color.FromArgb(0xA8, 0xB0, 0xC4);
    private static readonly Color DTextDisabled = Color.FromArgb(0x66, 0x66, 0x66);
    private static readonly Color DHover = Color.FromArgb(0x24, 0x28, 0x35);
    private static readonly Color DSelection = Color.FromArgb(0x30, 0x2A, 0x48);
    private static readonly Color DDisabledFill = Color.FromArgb(0x3A, 0x3A, 0x3A);
    // 语义色也要分深浅两套：SeaGreen / Firebrick 这种在深底上根本读不清
    private static readonly Color DSuccess = Color.FromArgb(0x6C, 0xCB, 0x5F);
    private static readonly Color DDanger = Color.FromArgb(0xFF, 0x99, 0xA4);

    public static bool IsDark { get { return _dark; } }

    public static Color Surface { get { return _dark ? DSurface : LSurface; } }
    public static Color Card { get { return _dark ? DCard : LCard; } }
    public static Color Border { get { return _dark ? DBorder : LBorder; } }
    public static Color BorderHover { get { return _dark ? DBorderHover : LBorderHover; } }
    public static Color Accent { get { return _dark ? DAccent : LAccent; } }
    public static Color AccentHover { get { return _dark ? DAccentHover : LAccentHover; } }
    public static Color AccentPressed { get { return _dark ? DAccentPressed : LAccentPressed; } }
    /// 压在强调色上面的前景色（主按钮的文字/对勾）
    public static Color OnAccent { get { return _dark ? DOnAccent : LOnAccent; } }
    public static Color Text { get { return _dark ? DText : LText; } }
    public static Color TextSecondary { get { return _dark ? DTextSecondary : LTextSecondary; } }
    public static Color TextDisabled { get { return _dark ? DTextDisabled : LTextDisabled; } }
    public static Color Hover { get { return _dark ? DHover : LHover; } }
    public static Color Selection { get { return _dark ? DSelection : LSelection; } }
    public static Color DisabledFill { get { return _dark ? DDisabledFill : LDisabledFill; } }
    /// 成功提示（登录成功 / 打开成功）。**不要用 Color.SeaGreen** —— 深底上读不清。
    public static Color Success { get { return _dark ? DSuccess : LSuccess; } }
    /// 错误提示。**不要用 Color.Firebrick** —— 同上。
    public static Color Danger { get { return _dark ? DDanger : LDanger; } }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>把**窗口标题栏**也切到深色（Win10 1809+ 的 DWMWA_USE_IMMERSIVE_DARK_MODE）。</summary>
    ///
    /// ⚠️ 不设的话深色模式下窗体是深的、标题栏是刺眼的白 —— 用户反馈的「深色模式显示不正常」
    ///    里就有它。属性号在 Win11/新 Win10 上是 20，1809~1909 上是 19，两个都试一遍即可
    ///    （老系统返回非 0，忽略）。
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

    /// 最近一次设置标题栏失败的原因（空串 = 成功）。给自检 / 诊断用。
    public static string ChromeError = "";
    /// 最近一次设置滚动条主题失败的原因（空串 = 成功）。
    public static string ScrollbarError = "";

    /// <summary>把控件自带的**滚动条**也切到深色（Win10 1809+ 的 `DarkMode_Explorer` 主题）。</summary>
    ///
    /// ⚠️ 深色模式下最扎眼的残留就是它：列表整体是深的，右边挂着一条亮白滚动条
    ///    （截图里会话表右侧那条）。滚动条是系统画的，不听 BackColor，只能改窗口主题。
    ///    浅色时显式设回 `Explorer`，否则切回浅色会留着深色滚动条。
    public static void ApplyScrollbarTheme(Control control)
    {
        if (control == null || !control.IsHandleCreated) return;
        try
        {
            // subIdList is a part mapping, not a filter: mapping an Edit/Header to
            // ScrollBar paints scrollbar parts inside it (black strips and grips).
            SetWindowTheme(control.Handle, _dark ? "DarkMode_Explorer" : "Explorer", null);
            ModernScrollbarSkin.Apply(control);
            ScrollbarError = "";
        }
        catch (Exception ex)
        {
            ScrollbarError = ex.GetType().Name;
        }
    }

    public static void ApplyInputTheme(Control control)
    {
        if (control == null || !control.IsHandleCreated) return;
        // A single-line borderless edit has no scrollbar. Disable native edit
        // theming so its entire background follows the surrounding card.
        SetWindowTheme(control.Handle, "", "");
    }

    public static void ApplyWindowChrome(Form form)
    {
        if (form == null) return;
        try
        {
            if (!form.IsHandleCreated) { IntPtr force = form.Handle; }   // 触发句柄创建
            int value = _dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
            int corner = 2; // DWMWA_WINDOW_CORNER_PREFERENCE: round
            DwmSetWindowAttribute(form.Handle, 33, ref corner, sizeof(int));
        }
        catch (Exception ex)
        {
            // 老系统没有 dwmapi / 不支持这个属性：标题栏保持系统默认，不影响功能。
            // 但**不静默** —— 记下来，排查「深色标题栏没生效」时能直接看到原因。
            ChromeError = ex.GetType().Name;
        }
    }

    /// 主题变化通知。**自绘控件必须订阅它**，否则切主题时只有窗体变色、控件还是旧的。
    /// ⚠️ 控件要在 Dispose 里退订 —— 静态事件挂着不放手会把已经关掉的窗口一直留在内存里
    /// （设置对话框会反复开关，泄漏会累积）。
    public static event Action Changed;

    /// 切换主题。返回是否真的变了（没变就不必让控件重绘）。
    public static bool Apply(bool dark)
    {
        if (_dark == dark) return false;
        _dark = dark;
        Action handler = Changed;
        if (handler != null) handler();
        return true;
    }

    /// 界面字体。Microsoft YaHei UI 是 Win10+ 的中文 UI 字面（比「微软雅黑」更贴 UI），
    /// 老系统没有就退回「微软雅黑」—— 两者度量一致，布局不受影响。
    private static readonly bool HasYaheiUi = FamilyExists("Microsoft YaHei UI");
    private static readonly bool HasYahei = FamilyExists("Microsoft YaHei");

    public static readonly Font UiFont = PickFont(9F);
    public static readonly Font UiFontSmall = PickFont(8.25F);
    public static readonly Font UiFontBold = PickFont(9F, FontStyle.Bold);


    /// 字体族是否真的装了。
    ///
    /// ⚠️ 为什么不用 try/catch 试构造：`new Font("不存在的字体", ...)` **不会抛异常**
    /// （靠 catch 判不出来；而且那种写法要吞掉一个异常，会被「空catch棘轮」判成新增，
    /// 实测抓到过一次）。先查一遍安装列表最干净：只在静态初始化时跑一次。
    ///
    /// ⚠️ 也**不能靠回读 `Font.Name`** —— 2026-09-26 实测：字体根本没注册时，
    /// `new Font("HarmonyOS Sans SC", 20).Name` **照样原样返回** "HarmonyOS Sans SC"，
    /// 完全不反映真实回退。判据只能是 `InstalledFontCollection`（或用
    /// `MeasureString` 比对宽度，但汉字全是全角 1em，两个字体的中文宽度**本来就相同**，
    /// 拿纯中文量会得出「一样宽」的假结论 —— 必须掺几个 ASCII 字符才区分得出来）。
    private static bool FamilyExists(string name)
    {
        try
        {
            using (InstalledFontCollection installed = new InstalledFontCollection())
            {
                foreach (FontFamily family in installed.Families)
                    if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception)
        {
            // 枚举字体失败（GDI+ 异常）时按「没有」处理 —— 下面还有两级兜底，
            // 最差也只是观感退一档，不是需要打扰用户的事。
            return false;
        }
        return false;
    }

    private static Font PickFont(float size)
    {
        return PickFont(size, FontStyle.Regular);
    }

    private static Font PickFont(float size, FontStyle style)
    {
        // 首选随包携带的 HarmonyOS Sans SC（fonts\ 目录，AddFontResourceEx 注册进本进程）。
        // ⚠️ 粗体走 **Medium 字重**，不用 FontStyle.Bold —— 那是**合成粗体**，
        //    中文笔画会被描粗糊在一起；HarmonyOS Sans 自带 Medium，直接用它更干净。
        if (UiFonts.Available)
        {
            try
            {
                if (style == FontStyle.Bold)
                    return new Font(UiFonts.MediumFamily, size * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                return new Font(UiFonts.Family, size * 96f / 72f, style, GraphicsUnit.Pixel);
            }
            catch (Exception ex) { Diagnostics.Report("ui-font-pick", ex); }
        }
        // 兜底：系统自带的雅黑（老系统没有 YaHei UI 就用普通雅黑）
        if (HasYaheiUi) return new Font("Microsoft YaHei UI", size * 96f / 72f, style, GraphicsUnit.Pixel);
        if (HasYahei) return new Font("Microsoft YaHei", size * 96f / 72f, style, GraphicsUnit.Pixel);
        return new Font(FontFamily.GenericSansSerif, size * 96f / 72f, style, GraphicsUnit.Pixel);
    }

    /// 圆角矩形路径。radius <= 0 时退化成直角，调用方不用分支。
    public static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        int d = radius * 2;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// 画一条向下的 V 形箭头（下拉框用）。自己画比贴图片省事，也不会因 DPI 糊。
    public static void DrawChevron(Graphics g, Rectangle box, Color color)
    {
        using (Pen pen = new Pen(color, 1.6f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            int cx = box.X + box.Width / 2;
            int cy = box.Y + box.Height / 2;
            g.DrawLines(pen, new Point[]
            {
                new Point(cx - 4, cy - 2),
                new Point(cx, cy + 2),
                new Point(cx + 4, cy - 2)
            });
        }
    }

    /// 画一个对勾（复选框用）
    public static void DrawCheck(Graphics g, Rectangle box, Color color)
    {
        using (Pen pen = new Pen(color, 1.9f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new Point[]
            {
                new Point(box.X + 3, box.Y + box.Height / 2),
                new Point(box.X + box.Width / 2 - 1, box.Bottom - 4),
                new Point(box.Right - 3, box.Y + 3)
            });
        }
    }

    /// 分组标题。用加粗正文色 —— 简洁够用。
    public static ModernLabel SectionHeader(string text)
    {
        ModernLabel label = new ModernLabel();
        label.Text = text;
        DpiLayout.SetDesignFont(label, UiFontBold);
        label.Role = ModernLabel.RoleKind.Normal;
        label.AutoSize = false;
        return label;
    }
}

/// <summary>
/// 扁平下拉框。ComboBox 没法靠 UserPaint 完全自绘（句柄是系统画的），
/// 所以走「OwnerDrawFixed 画条目 + WndProc 里补描边」这条路 —— 这是 WinForms 里
/// 不重新实现整个控件的前提下最干净的做法。
/// </summary>
internal sealed class ModernComboBox : ComboBox, IDpiScaled
{
    private bool _hover;

    /// 下拉列表的行高与最大高度也要跟着 DPI（都是独立字段，不随 Bounds 缩放）
    public void OnDpiScaled(float scale)
    {
        ItemHeight = (int)System.Math.Round(22 * scale);
        DropDownHeight = (int)System.Math.Round(260 * scale);
    }

    public ModernComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        ModernTheme.Changed += OnThemeChanged;
        ItemHeight = 22;                       // 下拉列表行高（比默认松一点，好点）
        IntegralHeight = false;                // 否则会被行高向上取整到很矮
        DropDownHeight = 260;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true; Invalidate(); base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // WM_PAINT 之后**整块重画**。为什么不能只补描边：
        // `FlatStyle.Flat` 压不住 Aero 主题的 ComboBox —— 右侧那个带渐变的按钮是系统画的，
        // 只描一圈边它还在（第一版就是这么干的，截图上那块蓝灰渐变很扎眼）。
        // 这里把客户区整个盖掉再自己画文字 + 箭头 + 描边，才真正是平的。
        //
        // 0x0317 = WM_PRINT / 0x0318 = WM_PRINTCLIENT：`Control.DrawToBitmap` 走的是这两条，
        // 不是 WM_PAINT。**不处理的话离屏渲染出来的还是系统样式** —— 测试恰恰是用
        // DrawToBitmap 截图的，只处理 WM_PAINT 会让「测试里好看、真机上难看」。
        if ((m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318)
            && IsHandleCreated && Width > 0 && Height > 0)
        {
            using (Graphics g = (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero
                ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent != null ? Parent.BackColor : ModernTheme.Surface);
                int radius = DpiLayout.S(this, 8);
                using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), radius))
                using (SolidBrush back = new SolidBrush(ModernTheme.Card))
                    g.FillPath(back, path);

                if (SelectedIndex >= 0 && SelectedIndex < Items.Count)
                {
                    Rectangle text = new Rectangle(7, 0, Width - 30, Height);
                    TextRenderer.DrawText(g, Convert.ToString(Items[SelectedIndex]), Font, text,
                        Enabled ? ModernTheme.Text : ModernTheme.TextDisabled,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                ModernTheme.DrawChevron(g, new Rectangle(Width - 22, 0, 22, Height),
                    Enabled ? ModernTheme.TextSecondary : ModernTheme.TextDisabled);
                using (GraphicsPath path = ModernTheme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), radius))
                using (Pen pen = new Pen(_hover ? ModernTheme.BorderHover : ModernTheme.Border))
                    g.DrawPath(pen, path);
            }
        }
    }

    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        Graphics g = e.Graphics;
        bool isList = (e.State & DrawItemState.ComboBoxEdit) == 0;

        // 闭合状态的那块「编辑区」：画底色 + 选中项文字 + 右侧箭头
        if (!isList)
        {
            using (SolidBrush back = new SolidBrush(ModernTheme.Card))
                g.FillRectangle(back, e.Bounds);
            if (e.Index >= 0 && e.Index < Items.Count)
            {
                Rectangle text = new Rectangle(e.Bounds.X + 7, e.Bounds.Y,
                    e.Bounds.Width - 28, e.Bounds.Height);
                TextRenderer.DrawText(g, Convert.ToString(Items[e.Index]), Font, text,
                    Enabled ? ModernTheme.Text : ModernTheme.TextDisabled,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            ModernTheme.DrawChevron(g, new Rectangle(Width - 22, 0, 22, Height),
                Enabled ? ModernTheme.TextSecondary : ModernTheme.TextDisabled);
            return;
        }

        // 下拉列表里的行
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush back = new SolidBrush(selected ? ModernTheme.Selection : ModernTheme.Card))
            g.FillRectangle(back, e.Bounds);
        if (e.Index >= 0 && e.Index < Items.Count)
        {
            Rectangle text = new Rectangle(e.Bounds.X + 7, e.Bounds.Y,
                e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(g, Convert.ToString(Items[e.Index]), Font, text,
                ModernTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>扁平复选框：圆角方块 + 强调色对勾，取代系统那种带 3D 感的方框。</summary>
internal sealed class ModernCheckBox : CheckBox, IDpiScaled
{
    private bool _hover;
    private float _scale = 1;
    public void OnDpiScaled(float scale) { _scale = scale; Invalidate(); }

    public ModernCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        ForeColor = ModernTheme.Text;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        ModernTheme.Changed += OnThemeChanged;
        Height = 24;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : ModernTheme.Surface);

        int boxSize = (int)Math.Round(16 * _scale);
        int boxTop = (Height - boxSize) / 2;
        Rectangle box = new Rectangle(0, boxTop, boxSize, boxSize);

        if (Checked)
        {
            using (GraphicsPath path = ModernTheme.RoundRect(box, 4))
            using (SolidBrush fill = new SolidBrush(Enabled ? ModernTheme.Accent : ModernTheme.TextDisabled))
                g.FillPath(fill, path);
            ModernTheme.DrawCheck(g, box, ModernTheme.OnAccent);
        }
        else
        {
            using (GraphicsPath path = ModernTheme.RoundRect(box, 4))
            {
                using (SolidBrush fill = new SolidBrush(ModernTheme.Card))
                    g.FillPath(fill, path);
                Color border = !Enabled ? ModernTheme.Border
                    : (_hover ? ModernTheme.BorderHover : ModernTheme.TextDisabled);
                using (Pen pen = new Pen(border, 1.2f))
                    g.DrawPath(pen, path);
            }
        }

        int gap = (int)Math.Round(8 * _scale);
        Rectangle text = new Rectangle(boxSize + gap, 0, Width - boxSize - gap, Height);
        TextRenderer.DrawText(g, Text, Font, text,
            Enabled ? ModernTheme.Text : ModernTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// 扁平按钮。主按钮（保存）用强调色实心，次按钮（取消）用白底描边，还有第三种
/// 「链接感」的弱按钮（重置/打开目录之类）—— 靠 Style 区分，调用方只改一个属性。
/// </summary>
internal sealed class ModernButton : Button
{
    public enum Kind { Primary, Secondary, Subtle }

    private bool _hover;
    private bool _pressed;
    private bool _selected, _tab;
    private Kind _kind = Kind.Secondary;

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        ModernTheme.Changed += OnThemeChanged;
    }

    public Kind Style
    {
        get { return _kind; }
        set { _kind = value; Invalidate(); }
    }

    /// 选中态（平台切换按钮用）。**不能用 BackColor 表达** —— 自绘按钮根本不看 BackColor，
    /// 只有这个属性会真的改变填充色。启动器原来写的是
    /// `button.BackColor = selected ? 蓝 : SystemColors.Control`，换自绘后必须改走这里。
    public bool Selected
    {
        get { return _selected; }
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    /// <summary>Text-style tab: no border or card fill until selected.</summary>
    public bool Tab
    {
        get { return _tab; }
        set { if (_tab != value) { _tab = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : ModernTheme.Surface);

        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = ModernTheme.RoundRect(rect, DpiLayout.S(this, 8)))
        {
            Color fill, border, fore;
            if (!Enabled)
            {
                fill = _kind == Kind.Primary ? ModernTheme.DisabledFill : ModernTheme.Hover;
                border = Color.Transparent;
                fore = ModernTheme.TextDisabled;
            }
            else if (_tab)
            {
                fill = _selected || _hover ? ModernTheme.Hover : Color.Transparent;
                border = Color.Transparent;
                fore = _selected || _hover ? ModernTheme.Text : ModernTheme.TextSecondary;
            }
            else if (_selected)
            {
                // 选中平台要有明显轮廓：软紫底 + 强调色描边 + 白字。
                fill = _pressed ? ModernTheme.AccentPressed : (_hover ? ModernTheme.AccentHover : ModernTheme.Selection);
                border = ModernTheme.Accent;
                fore = _pressed || _hover ? ModernTheme.OnAccent : ModernTheme.Text;
            }
            else if (_kind == Kind.Primary)
            {
                fill = _pressed ? ModernTheme.AccentPressed : (_hover ? ModernTheme.AccentHover : ModernTheme.Accent);
                border = Color.Transparent;
                fore = ModernTheme.OnAccent;
            }
            else if (_kind == Kind.Subtle)
            {
                fill = _hover ? ModernTheme.Hover : Color.Transparent;
                border = Color.Transparent;
                fore = ModernTheme.Accent;
            }
            else
            {
                fill = _pressed ? ModernTheme.Border : (_hover ? ModernTheme.Hover : ModernTheme.Card);
                border = _hover ? ModernTheme.BorderHover : ModernTheme.Border;
                fore = ModernTheme.Text;
            }

            if (fill.A > 0)
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
            }
            if (border.A > 0)
            {
                using (Pen pen = new Pen(border)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// 带圆角描边的文本框宿主。WinForms 的 TextBox 只能是 FixedSingle/Fixed3D/None，
/// 想要圆角就得「无边框 TextBox 塞进自绘 Panel」—— 这里包一层省得调用方重复写。
/// 暴露 Inner 给调用方设置 Multiline / ScrollBars。
/// </summary>
internal sealed class ModernInput : Panel, IDpiScaled
{
    public readonly TextBox Inner;
    private bool _focused;
    private int cornerRadius = 5;
    private bool drawBorder = true;

    internal int CornerRadius
    {
        get { return cornerRadius; }
        set { cornerRadius = Math.Max(0, value); Invalidate(); }
    }

    internal bool DrawBorder
    {
        get { return drawBorder; }
        set { drawBorder = value; Invalidate(); }
    }

    public ModernInput(bool multiline)
    {
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ModernTheme.Changed += OnThemeChanged;
        BackColor = ModernTheme.Card;
        Padding = new Padding(8, multiline ? 6 : 0, 8, multiline ? 6 : 0);

        Inner = new TextBox();
        StyleInner();
        Inner.Multiline = multiline;
        if (multiline)
        {
            // ⚠️ WinForms 的 ScrollBars.Vertical 是**无条件显示**的 —— 内容只有一行也挂着
            // 一条灰滚动条，是整块界面上最后一个 2006 年观感的东西。
            // 所以默认不显示，靠 SyncScrollbar 在内容真的溢出时才挂上。
            Inner.ScrollBars = ScrollBars.None;
            Inner.TextChanged += delegate { QueueScrollbarSync(); };
            Inner.Resize += delegate { QueueScrollbarSync(); };
        }
        Inner.Dock = multiline ? DockStyle.Fill : DockStyle.None;
        // 多行输入框的滚动条同样是系统画的，也要跟着主题走
        Inner.HandleCreated += delegate { ApplyInnerTheme(); QueueScrollbarSync(); };
        Inner.VisibleChanged += delegate { QueueScrollbarSync(); };
        Inner.GotFocus += delegate { _focused = true; Invalidate(); };
        Inner.LostFocus += delegate { _focused = false; Invalidate(); };
        // 把内层事件转出来 —— 调用方拿到的是这个 Panel，不是里面的 TextBox
        Inner.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
        // 单行时垂直居中：PlaceholderText 那种效果靠 Padding 算不准，直接按字号偏移
        if (!multiline) Inner.Location = new Point(8, (Height - Inner.Height) / 2);
        Controls.Add(Inner);
        OnDpiScaled(DpiLayout.Scale);
    }

    public void OnDpiScaled(float scale)
    {
        Padding = new Padding((int)Math.Round(8 * scale), Inner.Multiline ? (int)Math.Round(6 * scale) : 0,
            (int)Math.Round(8 * scale), Inner.Multiline ? (int)Math.Round(6 * scale) : 0);
        PerformLayout();
    }

    private void ApplyInnerTheme()
    {
        if (Inner.Multiline) ModernTheme.ApplyScrollbarTheme(Inner);
        else ModernTheme.ApplyInputTheme(Inner);
    }

    /// 内容行数超过可视行数时才挂滚动条。行数用 GetLineFromCharIndex 数
    /// （它按**自动换行后**的实际行算，比按 \r\n 数准）。
    private bool scrollbarQueued, changingScrollbar;
    private void QueueScrollbarSync()
    {
        if (!Inner.Multiline || !Inner.IsHandleCreated || Inner.IsDisposed || scrollbarQueued || changingScrollbar) return;
        scrollbarQueued = true;
        try
        {
            Inner.BeginInvoke((MethodInvoker)delegate
            {
                scrollbarQueued = false;
                if (Inner.IsDisposed || !Inner.IsHandleCreated || !Inner.Visible) return;
                SyncScrollbar();
            });
        }
        catch (InvalidOperationException ex) { scrollbarQueued = false; System.Diagnostics.Debug.WriteLine(ex.Message); }
    }
    private void SyncScrollbar()
    {
        if (changingScrollbar || !Inner.IsHandleCreated || Inner.ClientSize.Height <= 0) return;
        changingScrollbar = true;
        try
        {
            int lines = Inner.GetLineFromCharIndex(Inner.TextLength) + 1;
            int visible = Math.Max(1, Inner.ClientSize.Height / Math.Max(1, Inner.Font.Height));
            ScrollBars want = lines > visible ? ScrollBars.Vertical : ScrollBars.None;
            // ScrollBars recreates the HWND and sends Resize/HandleCreated again.
            if (Inner.ScrollBars != want) Inner.ScrollBars = want;
        }
        finally { changingScrollbar = false; }
    }
    public override string Text
    {
        get { return Inner.Text; }
        set { Inner.Text = value; }
    }

    public bool ReadOnly
    {
        get { return Inner.ReadOnly; }
        set { Inner.ReadOnly = value; }
    }

    /// 外层是 Panel、里面才是 TextBox —— 字号必须**转下去**，
    /// 否则 `input.Font = ...` 只改了 Panel，真正显示文字的那层没变。
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (Inner != null) { Inner.Font = Font; PerformLayout(); }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Inner != null && !Inner.Multiline)
        {
            int innerHeight = Inner.PreferredHeight;
            Inner.SetBounds(Padding.Left, Math.Max(0, (Height - innerHeight) / 2),
                Math.Max(10, Width - Padding.Left - Padding.Right), innerHeight);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? ModernTheme.Surface : Parent.BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = ModernTheme.RoundRect(rect, DpiLayout.S(this, cornerRadius)))
        {
            using (SolidBrush fill = new SolidBrush(ModernTheme.Card)) e.Graphics.FillPath(fill, path);
            if (drawBorder)
                using (Pen pen = new Pen(_focused ? ModernTheme.Accent : ModernTheme.Border, _focused ? 1.6f : 1f))
                    e.Graphics.DrawPath(pen, path);
        }
    }

    /// 主题变了要连**内层 TextBox** 一起刷色。
    ///
    /// ⚠️ 2026-09-27 修：原来这里只改了外层 Panel 的 BackColor/ForeColor，
    ///    `Inner` 是标准 TextBox，颜色**只在构造函数里设过一次** —— 于是深色模式下
    ///    输入框永远是白底黑字（截图里那块最扎眼的白）。
    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        ApplyInnerColors();
        Invalidate();
    }

    private void ApplyInnerColors()
    {
        if (Inner == null) return;
        Inner.BackColor = ModernTheme.Card;
        Inner.ForeColor = ModernTheme.Text;
        ApplyInnerTheme();
    }

    /// 内层 TextBox 的颜色统一走这里 —— 构造函数与主题切换两个入口都调它，
    /// 免得以后再加一个入口又漏掉一处。
    private void StyleInner()
    {
        Inner.BorderStyle = BorderStyle.None;
        DpiLayout.SetDesignFont(Inner, ModernTheme.UiFont);
        ApplyInnerColors();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// 扁平列表框。ListBox 的 3D 描边是系统画的（BorderStyle.FixedSingle 那种深灰方框），
/// 所以走 BorderStyle.None + 自己在 WM_PAINT 里补一圈主题色描边。
/// </summary>
internal sealed class ModernListBox : ListBox, IDpiScaled
{
    internal bool DrawBorder = true;
    public ModernListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        ItemHeight = 20;
        IntegralHeight = false;
        ModernTheme.Changed += OnThemeChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ModernTheme.ApplyScrollbarTheme(this);
    }

    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        ModernTheme.ApplyScrollbarTheme(this);
        Invalidate();
    }

    /// 行高也要跟着 DPI —— 它是独立字段，不会随 Bounds 自动缩放。
    /// 不缩的话字号已经 2 倍、行高还是 20px，每一行只显示上半截（实测截图）。
    public void OnDpiScaled(float scale)
    {
        ItemHeight = (int)System.Math.Round(20 * scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Graphics g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush back = new SolidBrush(selected ? ModernTheme.Selection : ModernTheme.Card))
            g.FillRectangle(back, e.Bounds);
        // 选中时用强调色文字 —— 浅底选中块上再压深色字会糊
        Color fore = selected ? ModernTheme.Accent : ModernTheme.Text;
        Rectangle text = new Rectangle(e.Bounds.X + 5, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(g, Convert.ToString(Items[e.Index]), Font, text, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (DrawBorder && (m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318)
            && IsHandleCreated && Width > 2 && Height > 2)
        {
            using (Graphics g = (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero
                ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
            using (Pen pen = new Pen(ModernTheme.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>
/// 扁平 ListView（Details 视图）。表头、行底、行文字、网格线全部自绘 ——
/// 默认表头是那种带渐变和分隔线的 Aero 样式，跟这套配色放一起很违和。
/// </summary>
internal sealed class ModernListView : ListView
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int LVM_GETHEADER = 0x101F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_PRINT = 0x0317;
    private const int WM_PRINTCLIENT = 0x0318;

    private HeaderBackdrop _headerBackdrop;

    /// <summary>
    /// 表头是个**独立的子窗口**（SysHeader32），父窗口的 WM_PAINT 盖不住它 ——
    /// 子窗口在父窗口之后绘制。所以只能子类化它，拦它自己的 WM_ERASEBKGND。
    ///
    /// 不这么做的话，表头右侧那块「填充区」（不是列，OnDrawColumnHeader 不触发）
    /// 会一直用系统浅色 —— 深色模式下就是一块刺眼的白。
    /// </summary>
    private sealed class HeaderBackdrop : NativeWindow
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Rectangle rect);

        protected override void WndProc(ref Message m)
        {
            // Let the header render its owner-drawn columns before filling the
            // unused area. Suppressing WM_PRINT also suppressed all header text.
            base.WndProc(ref m);
            // 0x000F = WM_PAINT 触发前的擦除、0x0317/0x0318 = WM_PRINT/WM_PRINTCLIENT。
            // **三条都要接** —— 不接 WM_PRINT 的话，`PrintWindow` / `DrawToBitmap`
            // 截出来的还是系统色，会出现「截图里看着修好了、真机上不一样」。
            if (m.Msg == WM_ERASEBKGND || m.Msg == 0x000F || m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT)
            {
                try
                {
                    using (Graphics g = m.Msg != 0x000F && m.WParam != IntPtr.Zero
                        ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
                    using (SolidBrush brush = new SolidBrush(ModernTheme.Surface))
                    {
                        // 只铺「最后一列右边」那一段，别把列头自己的地盘也盖了
                        Rectangle client;
                        if (GetClientRect(Handle, out client))
                            g.FillRectangle(brush, _fillerLeft, 0, Math.Max(0, client.Width - _fillerLeft), client.Height);
                    }
                }
                catch (Exception ex)
                {
                    // 画不出来就退回系统绘制，不值得为它崩 —— 但必须留痕，
                    // 否则「表头偶尔还是白的」会变成一条查不到原因的幽灵 bug。
                    Diagnostics.Report("ui-header-paint", ex);
                }
            }
        }

        private ModernListView _owner;
        private int _fillerLeft;

        public void Attach(ModernListView owner, IntPtr header)
        {
            _owner = owner;
            AssignHandle(header);
            UpdateFiller();
        }

        public void UpdateFiller()
        {
            if (_owner == null) return;
            int used = 0;
            foreach (ColumnHeader column in _owner.Columns) used += column.Width;
            _fillerLeft = used;
        }

        /// NativeWindow 没有 Invalidate，只能自己发。
        public void Refresh()
        {
            if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, true);
        }
    }

    public ModernListView()
    {
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        FullRowSelect = true;
        HideSelection = false;
        ModernTheme.Changed += OnThemeChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ModernTheme.ApplyScrollbarTheme(this);   // 右侧那条滚动条也要深色（见 ModernTheme 里的说明）
        IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero && _headerBackdrop == null)
        {
            _headerBackdrop = new HeaderBackdrop();
            _headerBackdrop.Attach(this, header);
        }
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (_headerBackdrop != null)
        {
            _headerBackdrop.UpdateFiller();
            _headerBackdrop.Refresh();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_headerBackdrop != null) _headerBackdrop.UpdateFiller();
        Invalidate();
    }

    private void OnThemeChanged()
    {
        BackColor = ModernTheme.Card;
        ForeColor = ModernTheme.Text;
        ModernTheme.ApplyScrollbarTheme(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        Graphics g = e.Graphics;
        using (SolidBrush back = new SolidBrush(ModernTheme.Surface))
            g.FillRectangle(back, e.Bounds);
        Rectangle text = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(g, e.Header.Text, Font, text, ModernTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using (Pen pen = new Pen(ModernTheme.Border))
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // Details 视图下背景由这里铺满整行，文字交给 OnDrawSubItem。
        // 其他视图（列表/图标）没有子项回调，所以这里也把首列文字画了。
        using (SolidBrush back = new SolidBrush(RowBack(e.Item.Selected)))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (View != View.Details)
            TextRenderer.DrawText(e.Graphics, e.Item.Text, Font, e.Bounds, ModernTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle bounds = e.Bounds;
        if (bounds.Width <= 0) return;
        using (SolidBrush back = new SolidBrush(RowBack(e.Item.Selected)))
            g.FillRectangle(back, bounds);
        bool first = e.ColumnIndex == 0;
        Rectangle text = new Rectangle(bounds.X + 8, bounds.Y, bounds.Width - 12, bounds.Height);
        TextRenderer.DrawText(g, e.SubItem.Text, Font, text,
            first ? ModernTheme.Text : ModernTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (GridLines)
        {
            using (Pen pen = new Pen(ModernTheme.Border))
            {
                g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                g.DrawLine(pen, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);
            }
        }
    }

    /// 用 item.Selected 而不是事件参数上的 State —— DrawListViewSubItemEventArgs 没有
    /// State（只有 DrawListViewItemEventArgs 有），两处统一走 Selected 少一个坑。
    private static Color RowBack(bool selected)
    {
        return selected ? ModernTheme.Selection : ModernTheme.Card;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if ((m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318)
            && IsHandleCreated && Width > 2 && Height > 2)
        {
            using (Graphics g = (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero
                ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
            {
                // 表头右侧的填充区**不在这里画** —— 表头是独立子窗口，父窗口盖不住它，
                // 已改由 HeaderBackdrop 拦它自己的 WM_ERASEBKGND（见上面那个嵌套类）。
                using (Pen pen = new Pen(ModernTheme.Border))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
/// <summary>
/// 分组标题左边那条强调色竖条。自绘而不是「建个 Panel 设 BackColor」——
/// 后者在对话框里切深色模式时不会跟着变色（BackColor 只在建的时候取了一次）。
/// </summary>
internal sealed class ModernSectionBar : Panel
{
    public ModernSectionBar()
    {
        Width = 3;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        ModernTheme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() { Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(ModernTheme.Accent))
            e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
/// <summary>
/// 会跟着主题变色的标签。默认 Secondary（说明性文字），设 Role = Normal 走正文色。
///
/// 为什么要专门一个类：Label 的颜色是**建的时候设一次**的，切深色模式时不会跟着变 ——
/// 浅色下写的 DimGray 到了深色底上几乎看不见。让标签自己订阅主题最省心。
/// </summary>
internal sealed class ModernLabel : Label
{
    public enum RoleKind { Normal, Secondary }

    private RoleKind _role = RoleKind.Secondary;

    public ModernLabel()
    {
        DpiLayout.SetDesignFont(this, ModernTheme.UiFont);
        BackColor = Color.Transparent;
        ForeColor = ModernTheme.TextSecondary;
        ModernTheme.Changed += OnThemeChanged;
    }

    public RoleKind Role
    {
        get { return _role; }
        set { _role = value; ApplyColor(); }
    }

    private void ApplyColor()
    {
        ForeColor = _role == RoleKind.Normal ? ModernTheme.Text : ModernTheme.TextSecondary;
    }

    private void OnThemeChanged() { ApplyColor(); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ModernTheme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
/// <summary>Declare DPI awareness before any HWND is created; actual mode is diagnosable.</summary>
internal static class DpiAwareness
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);
    internal static string ActualMode = "unknown";
    internal static int LastError;
    public static void Enable()
    {
        bool enabled = false;
        try
        {
            enabled = SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (!enabled) LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            IntPtr actual = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(actual, new IntPtr(-4))) { ActualMode = "PerMonitorV2"; return; }
        }
        catch (Exception ex) { Diagnostics.Report("dpi-pmv2", ex); }
        if (!enabled)
        {
            try { enabled = SetProcessDpiAwareness(2) == 0; if (enabled) ActualMode = "PerMonitor"; }
            catch (Exception ex) { Diagnostics.Report("dpi-pm", ex); }
        }
        if (!enabled)
        {
            try { enabled = SetProcessDPIAware(); if (enabled) ActualMode = "SystemAware"; }
            catch (Exception ex) { Diagnostics.Report("dpi-system", ex); }
        }
        try
        {
            IntPtr actual = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(actual, new IntPtr(-4))) ActualMode = "PerMonitorV2";
            else if (AreDpiAwarenessContextsEqual(actual, new IntPtr(-3))) ActualMode = "PerMonitor";
            else if (AreDpiAwarenessContextsEqual(actual, new IntPtr(-2))) ActualMode = "SystemAware";
            else ActualMode = "Unaware";
        }
        catch (Exception ex) { Diagnostics.Report("dpi-query", ex); }
    }
}
/// <summary>
/// 界面字体：HarmonyOS Sans SC（华为免费字体，随包携带）。
///
/// **为什么必须走 AddFontResourceEx 而不是 PrivateFontCollection**：
/// 本项目的自绘控件全用 `TextRenderer.DrawText`（GDI），而 **GDI 看不到
/// PrivateFontCollection 里私载的字体** —— 那样写的话字体加载「成功」但渲染时
/// 会静默退回系统字体，看着像没生效。
/// `AddFontResourceEx(..., FR_PRIVATE, 0)` 是把字体注册进**本进程**的 GDI 字体表：
/// 免安装、不影响系统、便携包换台机器照样能用。
///
/// 许可（fonts\LICENSE.txt，华为 HarmonyOS Sans Fonts License Agreement）：
/// 允许 royalty-free 地 embed / bundle / redistribute，条件是
///   ① 软件里要有显著声明（已写在设置对话框底部）
///   ② 不得修改字体
///   ③ 不得单独分发（随软件打包没问题）
///   ④ 随附版权声明与本协议（fonts\LICENSE.txt 已在包内）
/// </summary>
internal static class UiFonts
{
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    /// FR_PRIVATE：只对本进程可见 —— 这正是「便携」需要的语义。
    private const uint FR_PRIVATE = 0x10;

    public const string Family = "HarmonyOS Sans SC";
    public const string MediumFamily = "HarmonyOS Sans SC Medium";

    /// 字体有没有真的注册上。注册失败就退回系统字体，不至于界面崩掉。
    public static bool Available;

    /// 在**建任何控件之前**调用一次。
    public static void Register(string fontDir)
    {
        string[] files = new string[] { "HarmonyOS_Sans_SC_Regular.ttf", "HarmonyOS_Sans_SC_Medium.ttf" };
        int loaded = 0;
        for (int i = 0; i < files.Length; i++)
        {
            try
            {
                string path = System.IO.Path.Combine(fontDir, files[i]);
                if (!System.IO.File.Exists(path)) continue;
                if (AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero) > 0) loaded++;
            }
            catch (Exception ex) { Diagnostics.Report("ui-font", ex); }
        }
        Available = loaded == files.Length;
        if (!Available) Diagnostics.Report("ui-font-missing", new InvalidOperationException("fonts 目录没找到可用的 HarmonyOS Sans"));
    }
}
/// <summary>
/// 自己做的 DPI 布局缩放。
///
/// **为什么不用 WinForms 的 AutoScaleMode**：实测过两次都失败 ——
/// 本项目窗体是「硬编码像素 + 运行时按 form.ClientSize 动态算一部分」的混合布局，
/// 而 `AutoScaleMode` 一赋值就触发 `PerformAutoScale`，时机和次数都不受我们控制，
/// 结果是**一部分控件缩了一次、另一部分缩了两次**（实测底部按钮跑到 rel y=2218，
/// 客户区才 1120；两个列表和 ListView 直接跑到可视区外）。
///
/// 这里换成显式的一次性缩放，规则简单到不会出错：
///   · **只缩放尺寸和位置**（Bounds / ClientSize / MinimumSize）
///   · **绝不动 Font** —— 字号是点值，GDI/GDI+ 本来就按设备 DPI 渲染，
///     再乘一次就会得到 4 倍大的字
///   · 在**所有控件建完之后**调用一次
///
/// 对动态布局是安全的：那些代码都是**赋值**（`list.Width = form.ClientSize.Width / 2`）
/// 而不是乘算，所以缩放后 ClientSize 变大，它们算出来的就是物理尺寸 ✓ 重跑也不会累积。
/// </summary>
/// <summary>
/// 需要跟着 DPI 缩放的**非 Bounds 属性**（行高、下拉高度…）。
///
/// 为什么单列一个接口：DpiLayout.ScaleTree 只缩放 Bounds，而这些属性是**独立字段**
/// —— ListBox.ItemHeight 是构造函数里按逻辑值设的，不跟着缩放就会出现
/// 「字号已经 2 倍、行高还是 20px」，列表每一行只显示上半截（实测截图就是这样）。
/// </summary>
internal interface IDpiScaled
{
    void OnDpiScaled(float scale);
}

internal static class DpiLayout
{
    // Construction always uses 96-DPI logical coordinates. Ordinary windows apply
    // their own DPI once the tree exists, and again on WM_DPICHANGED.
    internal sealed class FontSpec
    {
        internal float Points; internal string Family; internal FontStyle Style;
        internal FontSpec(Font font)
        {
            Points = font.Unit == GraphicsUnit.Pixel ? font.Size * 72f / 96f : font.SizeInPoints;
            Family = font.FontFamily.Name; Style = font.Style;
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, FontSpec> Fonts
        = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, FontSpec>();
    public static void SetDesignFont(Control control, Font font)
    {
        lock (Fonts) { Fonts.Remove(control); Fonts.Add(control, new FontSpec(font)); }
        ModernInput input = control as ModernInput;
        if (input != null && input.Inner != null)
            lock (Fonts) { Fonts.Remove(input.Inner); Fonts.Add(input.Inner, new FontSpec(font)); }
        control.Font = font;
    }
    internal static FontSpec DesignFont(Control control)
    {
        lock (Fonts)
        {
            FontSpec spec;
            for (Control current = control; current != null; current = current.Parent)
                if (Fonts.TryGetValue(current, out spec)) return spec;
        }
        return new FontSpec(control.Font);
    }
    public static float Scale = 1f;
    public static void Measure() { Scale = 1f; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    public static int ScrollbarWidth(Control control)
    {
        try
        {
            uint dpi = (uint)Math.Max(DpiForm.WindowDpi(control), Math.Round(For(control) * 96));
            return GetSystemMetricsForDpi(2, dpi);
        }
        catch (EntryPointNotFoundException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); return S(control, 17); }
    }
    public static int S(int logical) { return logical; }
    public static float For(Control control)
    {
        DpiForm form = control as DpiForm;
        if (form == null && control != null) form = control.FindForm() as DpiForm;
        return form == null ? 1f : form.RenderScale;
    }
    public static int S(Control control, int logical) { return DpiForm.Scale(logical, For(control)); }
    public static void ScaleTree(Control root)
    {
        // Retained for old builders; they now produce a logical tree only.
        if (root == null) return;
        foreach (Control child in root.Controls)
        {
            IDpiScaled scaled = child as IDpiScaled;
            if (scaled != null) scaled.OnDpiScaled(1f);
            if (child.HasChildren && !(child is ModernInput)) ScaleTree(child);
        }
    }
}
/// <summary>
/// WebP 解码（抖音的表情图**全是 WebP**）。
///
/// **为什么需要它**：.NET Framework 的 `System.Drawing.Image.FromStream` **不支持 WebP**，
/// 拿到 WebP 字节会抛 `ArgumentException: 参数无效`。抖音的 `emoji/list` 返回的
/// `url_list[0]` 实测 `Content-Type: image/webp`、前 4 字节 `RIFF`，
/// 于是那一整条下载路径**每次都在这里静默失败**（原来那处 catch 是空的），
/// 表现出来就是「抖音的弹幕表情一直是文字」而 B站 正常（B站 的图是真 PNG）。
///
/// **怎么解**（2026-09-27 改，评估报告 §1）：分两层，见 `Decode` 的注释 ——
///   ① 内置 VP8L 无损解码器（`danmaku-16-webp-lossless.cs`，纯托管、零依赖）：
///      抖音的表情图实测就是 VP8L，所以**不装任何系统组件也能显示**；
///   ② 系统 WIC（`Microsoft.WebpImageExtension`）：有损 VP8 只能靠它。
/// ⚠️ 原来这里写着「Windows 10 1809+ 自带 Microsoft Webp Decoder」——**这句话是错的**：
///    那个解码器可能由可选的商店扩展提供；未安装时可能返回
///    `NotSupportedException: 未找到适用于完成此操作的图像处理组件 (0x88982F8B)`，
///    而当时的代码只在日志里静默失败、测试还因此把整条流水线判红。
///    附带一个坑：部分系统 WebP 解码器 把带 alpha 的 VP8L 帧声明成 Bgr32，
///    按声明格式转像素会丢掉透明（内置解码器没这个问题，所以 VP8L 优先走它）。
///
/// 换 PNG/JPEG 的路试过了都走不通：给 CDN 加 `&format=png` / `&x-format=png` 原样返回 WebP；
/// 改路径（加后缀、换主机）一律 403 —— 签名只覆盖原始路径。
/// </summary>
internal static class WebpImage
{
    // 后端标识（诊断 / 自检 / 测试用）
    internal const string BackendNone = "none";
    internal const string BackendLossless = "vp8l-managed";
    internal const string BackendWic = "wic";

    /// 最近一次成功解码用的后端
    internal static string ActiveBackend = BackendNone;
    /// 最后一次失败的可读原因（界面/诊断可以直接说）
    internal static string LastFailureReason;
    /// 测试注入：模拟「这台机器什么 WebP 解码器都没有」
#pragma warning disable 649   // 只由测试通过反射赋值；产品代码只读它
    internal static bool DisableAllBackendsForTest;
#pragma warning restore 649
    private static bool _reportedUnavailable;

    /// 「这台机器解不了 WebP」只提醒一次 —— 弹幕一秒几十条，每次都报会把诊断摘要刷爆。
    private static void ReportUnavailable(string reason)
    {
        LastFailureReason = reason;
        if (_reportedUnavailable) return;
        _reportedUnavailable = true;
        // 登记进诊断摘要（托盘里能看到），不再只写日志了事 —— 评估报告 §1 的原话：
        // 「必须明确提示缺少 WebP 组件，不能只在日志中静默失败」。
        Diagnostics.Report("webp-unavailable", new NotSupportedException(
            "WebP 解码不可用：" + reason
            + " —— 有损 WebP 表情会退化成文字（装 Microsoft WebP 图像扩展可恢复）；"
            + "无损 WebP / 抖音表情走内置解码器，不受影响。"));
    }

    /// 这台机器现在能不能解 WebP（只探测，不做实际解码）。
    public static bool Available()
    {
        if (DisableAllBackendsForTest) return false;
        // 内置 VP8L 解码器随程序走，永远可用；WIC 只影响有损 VP8 那条支线。
        return true;
    }

    /// 按 RIFF 容器头判断是不是 WebP（第 8~11 字节是 "WEBP"）。
    public static bool LooksLikeWebp(byte[] data)
    {
        return data != null && data.Length > 12
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';
    }

    /// 加载 WPF 程序集。
    ///
    /// ⚠️ **必须用完整名**：`Assembly.Load("PresentationCore")`（简单名）在
    ///    .NET Framework 下**只从应用目录探测、不会去 GAC**，实测抛
    ///    `FileNotFoundException: 未能加载文件或程序集"PresentationCore"`。
    ///    带上 Version/Culture/PublicKeyToken 才会走 GAC 解析。
    /// 兜底：直接按 GAC 目录取（.NET 4.x 的子目录名是固定的）。
    private static Assembly LoadWpf(string simpleName)
    {
        const string token = "31bf3856ad364e35";
        try
        {
            return Assembly.Load(simpleName + ", Version=4.0.0.0, Culture=neutral, PublicKeyToken=" + token);
        }
        catch (Exception ex) { Diagnostics.Report("wpf-load", ex); }

        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "assembly", "GAC_MSIL", simpleName,
                "v4.0_4.0.0.0__" + token, simpleName + ".dll");
            if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path);
        }
        catch (Exception ex) { Diagnostics.Report("wpf-load-path", ex); }
        return null;
    }
    /// 解码入口：**分层选后端**（2026-09-27 整改，评估报告 §1）。
    ///
    ///   ① **内置 VP8L 无损解码器**（danmaku-16-webp-lossless.cs，纯托管零依赖）：
    ///      抖音的表情图实测就是 VP8L，所以这条路径让「**没装** WebP 图像扩展的 Windows」
    ///      也能显示表情 —— 原来那版把「Windows 10 1809+ 一定自带 WebP 解码器」当硬前提，
    ///      前提不成立时整条路可能静默失败。
    ///      顺带修掉一个 WIC 的坑：部分系统 WebP 解码器 把带 alpha 的 VP8L 帧
    ///      声明成 Bgr32（按声明格式取像素会得到 alpha 全 255），内置解码器直接给正确的 32bppArgb。
    ///   ② 系统 WIC（反射加载 `PresentationCore`）：**有损 VP8** 与其它变体只能靠它；
    ///      装了 `Microsoft.WebpImageExtension` 才有。
    ///   ③ 都失败 → 明确告知（诊断摘要 + 日志），不再只是写日志。
    public static Bitmap Decode(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        if (DisableAllBackendsForTest)
        {
            ReportUnavailable("测试注入：所有 WebP 后端已禁用");
            return null;
        }

        // ① 内置无损解码（VP8L）
        if (WebpLossless.LooksLikeLossless(data))
        {
            Bitmap managed = WebpLossless.Decode(data);
            if (managed != null) { ActiveBackend = BackendLossless; return managed; }
            LastFailureReason = "内置 VP8L 解码失败（数据可能被截断或损坏）";
            Diagnostics.Report("webp-lossless", new System.IO.InvalidDataException(LastFailureReason));
        }

        // ② 系统 WIC
        Bitmap viaWic = DecodeWithWic(data);
        if (viaWic != null) { ActiveBackend = BackendWic; return viaWic; }

        ReportUnavailable(LastFailureReason != null
            ? LastFailureReason
            : "系统没有可用的 WebP 编解码器（WIC）");
        return null;
    }

    /// 走系统 WIC 解（有损 VP8 / VP8X 等）。失败返回 null。
    ///
    /// ⚠️ **全部走反射**，不引用 WPF 程序集。原因：`PresentationCore` / `WindowsBase`
    ///    只在本机 **GAC** 里，不在 .NET Framework 目录 —— 而 `csc`（legacy 构建）
    ///    和 `dotnet build`（csproj，用 FrameworkPathOverride 指向框架目录）
    ///    都只从框架目录解析引用，加 `/r:` 会**同时打破两条构建路径**。
    ///    反射加载 `PresentationCore` 则两条路径都不需要改。
    ///
    /// 线程安全性：实测在 MTA 线程池线程上可用（PowerShell 默认就是 MTA，能解出来）。
    private static Bitmap DecodeWithWic(byte[] data)
    {
        try
        {
            Assembly core = LoadWpf("PresentationCore");
            if (core == null) return null;
            Type decoderType = core.GetType("System.Windows.Media.Imaging.BitmapDecoder", true);
            Type frameType = core.GetType("System.Windows.Media.Imaging.BitmapFrame", true);
            Type convertedType = core.GetType("System.Windows.Media.Imaging.FormatConvertedBitmap", true);
            Type optionsType = core.GetType("System.Windows.Media.Imaging.BitmapCreateOptions", true);
            Type cacheType = core.GetType("System.Windows.Media.Imaging.BitmapCacheOption", true);
            Type pixelFormatsType = core.GetType("System.Windows.Media.PixelFormats", true);

            object preserve = Enum.Parse(optionsType, "PreservePixelFormat");
            object onLoad = Enum.Parse(cacheType, "OnLoad");
            object bgra32 = pixelFormatsType.GetProperty("Bgra32",
                BindingFlags.Public | BindingFlags.Static).GetValue(null, null);

            using (System.IO.MemoryStream stream = new System.IO.MemoryStream(data))
            {
                object decoder = decoderType.GetMethod("Create", new Type[]
                {
                    typeof(System.IO.Stream), optionsType, cacheType
                }).Invoke(null, new object[] { stream, preserve, onLoad });

                object frames = decoderType.GetProperty("Frames").GetValue(decoder, null);
                object frame = frames.GetType().GetProperty("Item").GetValue(frames, new object[] { 0 });

                // 统一转成 Bgra32 —— 它的内存布局正好等于 GDI+ 的 Format32bppArgb，
                // 所以下面能整块拷，不用逐像素换通道。
                object converted = Activator.CreateInstance(convertedType,
                    new object[] { frame, bgra32, null, 0 });
                int width = (int)convertedType.GetProperty("PixelWidth").GetValue(converted, null);
                int height = (int)convertedType.GetProperty("PixelHeight").GetValue(converted, null);
                if (width <= 0 || height <= 0) return null;

                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                convertedType.GetMethod("CopyPixels", new Type[] { typeof(Array), typeof(int), typeof(int) })
                    .Invoke(converted, new object[] { pixels, stride, 0 });

                Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Drawing.Imaging.BitmapData locked = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length); }
                finally { bitmap.UnlockBits(locked); }
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Report("webp-decode", ex);
            return null;
        }
    }
}
