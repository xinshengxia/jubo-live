using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterDpiCases()
    {
        TestRunner.AddDesktop("高分屏", "多倍率、窄窗口、换屏往返和独立窗口", TestAdaptiveDpi);
        TestRunner.Add("高分屏", "重复DPI更新与滚动条不会失效或递归重建", TestDpiFontAndScrollbarLifetime);
        TestRunner.Add("高分屏", "文字像素原样贴图与实心字芯", TestDpiTextPixels);
        TestRunner.Add("高分屏", "在飞弹幕换屏后重建位图并释放旧租约", TestDpiFlyingCache);
    }
    private static void SetDpi(Form form, int dpi)
    {
        form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { dpi });
    }
    private static void TestAdaptiveDpi()
    {
        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            try
            {
                DanmakuType("UiFonts").GetMethod("Register", ReviewStatic).Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "fonts") });
                string output = Path.Combine(SourceRoot, "artifacts", "dpi-preview"); Directory.CreateDirectory(output);
                MethodInfo build = hub.GetMethod("BuildLauncherForm", Flags, null, new Type[] { typeof(int) }, null);
                using (Form form = (Form)build.Invoke(null, new object[] { -1 }))
                using (Form other = (Form)build.Invoke(null, new object[] { -1 }))
                {
                    SetDpi(form, 96); SetDpi(other, 144);
                    Size original = form.ClientSize;
                    foreach (int dpi in new int[] { 96, 120, 144, 168, 192, 240, 288, 96 })
                    {
                        SetDpi(form, dpi);
                        CheckLauncherAlignment(form, dpi);
                        foreach (Control c in form.Controls)
                        {
                            Check(c.Left >= 0 && c.Right <= form.ClientSize.Width + 1,
                                "DPI=" + dpi + " 控件横向可达：" + c.GetType().Name + " " + c.Text + " bounds=" + c.Bounds + " client=" + form.ClientSize);
                            Check(c.Bottom <= Math.Max(form.ClientSize.Height, form.AutoScrollMinSize.Height) + 2,
                                "DPI=" + dpi + " 控件可见或可纵向滚动到达");
                        }
                        for (int i = 0; i < form.Controls.Count; i++)
                            for (int j = i + 1; j < form.Controls.Count; j++)
                            {
                                Control a = form.Controls[i], b = form.Controls[j];
                                Check(!a.Bounds.IntersectsWith(b.Bounds), "DPI=" + dpi + " 顶层控件不重叠 " + i + "/" + j);
                            }
                        Control button = null;
                        foreach (Control c in form.Controls) if (c.Text == "加入收藏") button = c;
                        Check(button != null && button.Font.Unit == GraphicsUnit.Pixel
                            && Math.Abs(button.Font.Size - 9f * dpi / 72f) < 0.01f,
                            "字体直接按目标显示器像素尺寸渲染，DPI=" + dpi);
                        Check(Math.Abs((float)other.GetType().GetField("RenderScale", ReviewInstance).GetValue(other) - 1.5f) < 0.001f,
                            "另一窗口的 DPI 不被当前窗口改变");
                        if (dpi == 144 || dpi == 192)
                            SaveDpiForm(form, Path.Combine(output, "launcher-" + dpi + ".png"));
                    }
                    Check(form.ClientSize == original, "多次倍率往返后客户端尺寸无累计误差");
                    SetDpi(form, 192); form.ClientSize = new Size(1240, 1000); form.PerformLayout();
                    CheckLauncherAlignment(form, 192);
                    ListBox recent = null, favorite = null;
                    foreach (Control c in form.Controls)
                    {
                        if (c is ListBox) { if (recent == null) recent = (ListBox)c; else favorite = (ListBox)c; }
                        Check(c.Left >= 0 && c.Right <= form.ClientSize.Width + 1, "窄窗口所有操作均横向可达");
                    }
                    Check(recent != null && favorite != null && recent.Left == favorite.Left && favorite.Top > recent.Bottom,
                        "窄窗口最近播放与收藏切换为上下排列");
                    Check(form.AutoScrollMinSize.Height > form.ClientSize.Height, "内容过高时保留纵向滚动入口");
                    SaveDpiForm(form, Path.Combine(output, "launcher-narrow-192.png"));
                }
                object parts = DanmakuType("DanmakuSettingsDialog").GetMethod("Build", ReviewStatic)
                    .Invoke(null, new object[] { Activator.CreateInstance(DanmakuType("DanmakuConfig")) });
                using (Form settings = (Form)parts.GetType().GetField("Dialog").GetValue(parts))
                {
                    SetDpi(settings, 192);
                    settings.ClientSize = new Size(1000, 800); settings.PerformLayout(); SetDpi(settings, 192);
                    foreach (Control c in ((Control)parts.GetType().GetField("Body").GetValue(parts)).Controls)
                        if (c is ComboBox) Check(c.Font.Unit == GraphicsUnit.Pixel && Math.Abs(c.Font.Size - 24f) < 0.01f,
                            "设置下拉框的字号只放大一次，不因句柄提前创建而翻倍");
                    Control ok = (Control)parts.GetType().GetField("Ok").GetValue(parts);
                    Control cancel = (Control)parts.GetType().GetField("Cancel").GetValue(parts);
                    Check(settings.ClientRectangle.Contains(ok.Bounds) && settings.ClientRectangle.Contains(cancel.Bounds),
                        "高 DPI 小工作区下保存和取消按钮保持可达");
                    SaveDpiForm(settings, Path.Combine(output, "settings-192-small.png"));
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(20000)) throw new Exception("高 DPI 矩阵超时");
        if (failure != null) throw failure;
    }
    private static void CheckLauncherAlignment(Form form, int dpi)
    {
        var settings = new List<Control>();
        Control platform = null, input = null, inputLabel = null, quality = null, enhance = null;
        Control sessionLabel = null, reopen = null, lastPlayback = null, sessions = null;
        foreach (Control control in form.Controls)
        {
            Label label = control as Label;
            if (label != null)
                Check(label.TextAlign == ContentAlignment.MiddleLeft, "启动器标签垂直居中，DPI=" + dpi + " " + label.Text);
            if (control.Text == "B站") platform = control;
            if (control.GetType().Name == "ModernInput") input = control;
            if (control.Text == "房间号 / 链接：") inputLabel = control;
            if (control.Text == "正在观看 / 最近打开") sessionLabel = control;
            if (control.Text == "重新打开选中房间") reopen = control;
            if (control.Text == "用 mpv 打开（回车）") lastPlayback = control;
            if (control is ListView) sessions = control;
            ComboBox combo = control as ComboBox;
            if (combo != null)
            {
                settings.Add(combo);
                if (combo.Items.Contains("原画优先")) quality = combo;
                if (combo.Items.Contains("轻度")) enhance = combo;
            }
            if (control.Text == "取消启动" || control.Text == "弹幕设置…" || control.Text == "查找主播"
                || control.Text == "画质：" || control.Text == "mpv 缓冲：" || control.Text == "弹幕："
                || control.Text == "SC：" || control.Text == "画质增强") settings.Add(control);
        }
        Check(input != null && inputLabel != null && input.Top == inputLabel.Top && input.Height == inputLabel.Height,
            "房间输入框与标签等高对齐，DPI=" + dpi);
        if (platform != null && input != null && input.Left > platform.Right && input.Top < platform.Bottom)
            Check(input.Top == platform.Top && input.Height == platform.Height, "顶部输入框与平台按钮上下对齐，DPI=" + dpi);
        for (int i = 0; i < settings.Count; i++)
            for (int j = i + 1; j < settings.Count; j++)
            {
                Control a = settings[i], b = settings[j];
                if (a.Top >= b.Bottom || b.Top >= a.Bottom) continue;
                Check(Math.Abs((2 * a.Top + a.Height) - (2 * b.Top + b.Height)) <= 1,
                    "设置同行控件共用中心线，DPI=" + dpi + " " + a.Text + "/" + b.Text);
            }
        Check(quality != null && enhance != null && quality.Left == enhance.Left, "画质与画质增强下拉框左边缘对齐，DPI=" + dpi);
        Check(sessionLabel != null && reopen != null && sessionLabel.Top == reopen.Top && sessionLabel.Height == reopen.Height,
            "观看列表标题与重新打开按钮居中对齐，DPI=" + dpi);
        Check(lastPlayback != null && sessions != null && lastPlayback.Right == sessions.Right,
            "底部播放按钮与列表右边缘对齐，DPI=" + dpi);
    }

    private static Bitmap RenderDpiControl(Control control)
    {
        Bitmap image = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height), PixelFormat.Format32bppArgb);
        // WM_PRINT does not use the same path as WM_PAINT for native owner-drawn
        // controls. Invoke their production painter to avoid a stale HWND font in previews.
        using (Graphics paint = Graphics.FromImage(image))
        {
            if (control.GetType().Name == "ModernButton")
                control.GetType().GetMethod("OnPaint", ReviewInstance).Invoke(control,
                    new object[] { new PaintEventArgs(paint, new Rectangle(Point.Empty, image.Size)) });
            else if (control is ComboBox && ((ComboBox)control).DrawMode != DrawMode.Normal)
            {
                ComboBox combo = (ComboBox)control;
                control.GetType().GetMethod("OnDrawItem", ReviewInstance).Invoke(control,
                    new object[] { new DrawItemEventArgs(paint, combo.Font, new Rectangle(Point.Empty, image.Size),
                        combo.SelectedIndex, DrawItemState.ComboBoxEdit) });
            }
            else control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        }
        using (Graphics g = Graphics.FromImage(image))
        {
            g.SetClip(control.ClientRectangle);
            foreach (Control child in control.Controls)
                using (Bitmap part = RenderDpiControl(child)) g.DrawImageUnscaled(part, child.Left, child.Top);
        }
        return image;
    }
    private static void SaveDpiForm(Form form, string path)
    {
        using (Bitmap image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(form.BackColor);
            foreach (Control c in form.Controls)
                using (Bitmap part = RenderDpiControl(c)) graphics.DrawImageUnscaled(part, c.Left, c.Top);
            image.Save(path, ImageFormat.Png);
        }
    }
    private static void TestDpiFontAndScrollbarLifetime()
    {
        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            try
            {
                using (Form form = (Form)Activator.CreateInstance(DanmakuType("DpiForm"), ReviewInstance, null, new object[0], null))
                {
                    form.ClientSize = new Size(400, 260); form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-10000, -10000);
                    Control input = (Control)Activator.CreateInstance(DanmakuType("ModernInput"), new object[] { true });
                    input.Dock = DockStyle.Fill; form.Controls.Add(input);
                    TextBox inner = (TextBox)input.GetType().GetField("Inner").GetValue(input);
                    inner.Text = new string('中', 1500); int recreates = 0;
                    inner.HandleCreated += delegate { recreates++; };
                    SetDpi(form, 144); form.Show(); Application.DoEvents();
                    for (int i = 0; i < 30; i++)
                    {
                        SetDpi(form, i % 2 == 0 ? 144 : 192);
                        SetDpi(form, i % 2 == 0 ? 144 : 192);
                        Application.DoEvents();
                        using (Bitmap bitmap = new Bitmap(40, 40))
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                            Check(graphics.MeasureString("字体 Aa", inner.Font).Width > 0, "重复相同DPI后字体仍有效 " + i);
                    }
                    Check(recreates <= 8, "滚动条切换不会反复递归重建窗口句柄（次数=" + recreates + "）");
                    form.Close();
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        if (!thread.Join(15000)) throw new Exception("字体/滚动条寿命验收超时");
        if (failure != null) throw failure;
    }
    private static void TestDpiTextPixels()
    {
        Directory.CreateDirectory(Path.Combine(SourceRoot, "artifacts", "dpi-preview"));
        using (Bitmap source = new Bitmap(3, 3, PixelFormat.Format32bppPArgb))
        using (Bitmap output = new Bitmap(10, 10, PixelFormat.Format32bppPArgb))
        using (Graphics graphics = Graphics.FromImage(output))
        {
            source.SetPixel(1, 1, Color.White);
            DanmakuType("TextBlit").GetMethod("Draw", ReviewStatic).Invoke(null, new object[] { graphics, source, 2.4f, 3.6f });
            Check(output.GetPixel(3, 5).A == 255 && output.GetPixel(3, 5).R == 255,
                "浮点运动坐标取整后字芯像素原样保留");
            int touched = 0; for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) if (output.GetPixel(x, y).A != 0) touched++;
            Check(touched == 1, "文字不会因 bicubic 二次采样扩散到相邻像素");
        }
        object cache = Activator.CreateInstance(DanmakuType("DanmakuTextCache"));
        object glyph = cache.GetType().GetMethod("Get").Invoke(cache,
            new object[] { "高分屏中文字 Aa 123", Color.White, 25f, "Microsoft YaHei UI", true, 1 });
        try
        {
            Bitmap bitmap = BitmapOf(glyph); int opaqueWhite = 0;
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 255 && pixel.R == 255 && pixel.G == 255 && pixel.B == 255) opaqueWhite++;
            }
            Check(opaqueWhite > 100, "生产文字渲染的字芯含足够的不透明白色像素");
            using (Bitmap sample = new Bitmap(bitmap.Width + 40, bitmap.Height + 40))
            using (Graphics graphics = Graphics.FromImage(sample))
            {
                graphics.Clear(Color.FromArgb(40, 44, 52));
                DanmakuType("TextBlit").GetMethod("Draw", ReviewStatic).Invoke(null, new object[] { graphics, bitmap, 20.4f, 20.4f });
                sample.Save(Path.Combine(SourceRoot, "artifacts", "dpi-preview", "danmaku-solid.png"), ImageFormat.Png);
            }
        }
        finally
        {
            glyph.GetType().GetMethod("Release", ReviewInstance).Invoke(glyph, null);
            cache.GetType().GetMethod("Clear").Invoke(cache, null);
        }
        object config = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
        DanmakuType("DanmakuConfig").GetField("OpacityPercent").SetValue(config, 60);
        MethodInfo layer = DanmakuType("DanmakuOverlayForm").GetMethod("LayerAlpha", ReviewStatic);
        Check((int)layer.Invoke(null, new object[] { config }) == 255, "实心字芯模式不会再叠加整体透明度");
        DanmakuType("DanmakuConfig").GetField("SolidText").SetValue(config, false);
        Check((int)layer.Invoke(null, new object[] { config }) == 153, "关闭实心字芯后保留旧透明度行为");
    }
    private static void TestDpiFlyingCache()
    {
        Type configType = DanmakuType("DanmakuConfig"), rendererType = DanmakuType("DanmakuRenderer");
        object config = Activator.CreateInstance(configType);
        configType.GetField("ScaleWithWindow").SetValue(config, false);
        object renderer = Activator.CreateInstance(rendererType, new object[] { config });
        try
        {
            rendererType.GetMethod("Resize").Invoke(renderer, new object[] { 1280, 720 });
            object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
            DanmakuType("DanmakuItem").GetField("Text").SetValue(item, "换屏中的中文字体 Aa 123");
            rendererType.GetMethod("AddDanmaku").Invoke(renderer, new object[] { item });
            IList flying = (IList)rendererType.GetField("_flying", ReviewInstance).GetValue(renderer);
            Check(flying.Count == 1, "测试弹幕确实进入渲染队列");
            object first = flying[0], old = first.GetType().GetField("Handle").GetValue(first);
            float oldSize = (float)first.GetType().GetField("FontSize").GetValue(first);
            rendererType.GetMethod("SetDisplayDpi", ReviewInstance).Invoke(renderer, new object[] { 192 });
            rendererType.GetMethod("Resize").Invoke(renderer, new object[] { 1280, 720 });
            object fresh = first.GetType().GetField("Handle").GetValue(first);
            Check(!ReferenceEquals(old, fresh) && Math.Abs((float)first.GetType().GetField("FontSize").GetValue(first) - oldSize * 2) < 0.01f,
                "窗口像素尺寸相同但 DPI 改变时，在飞文字仍重新渲染");
            Check(IsFreed(old), "旧位图缓存与飞行租约都已释放");
        }
        finally
        {
            IList flying = (IList)rendererType.GetField("_flying", ReviewInstance).GetValue(renderer);
            MethodInfo forget = rendererType.GetMethod("ForgetFlying", ReviewInstance, null, new Type[] { typeof(int) }, null);
            while (flying.Count > 0) forget.Invoke(renderer, new object[] { 0 });
            object cache = rendererType.GetField("_textCache", ReviewInstance).GetValue(renderer);
            cache.GetType().GetMethod("Clear").Invoke(cache, null);
        }
    }
}
