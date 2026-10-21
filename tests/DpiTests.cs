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
        TestRunner.Add("高分屏", "原生DPI消息同步框架状态且不重复缩放字体", TestFrameworkDpiTransition);
        TestRunner.Add("高分屏", "客户区尺寸协商和用户调整后的倍率往返稳定", TestDpiClientSizeRoundTrip);
        TestRunner.Add("高分屏", "DPI后原生滚动条与实际布局同步且保留必要滚动", TestDpiNativeScrollbarReconcile);
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
                    form.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(form, false);
                    other.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(other, false);
                    SetDpi(form, 96); SetDpi(other, 144);
                    Size original = form.ClientSize;
                    foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 288, 96 })
                    {
                        SetDpi(form, dpi);
                        foreach (string key in WorkbenchPageKeys)
                        {
                            WorkbenchShowPage(form, key);
                            WorkbenchCheckViewport(form, dpi);
                        }
                        WorkbenchShowPage(form, "favorites");
                        CheckLauncherAlignment(form, dpi);
                        Control button = WorkbenchField(form, "addFav");
                        Check(button.Font.Unit == GraphicsUnit.Pixel && Math.Abs(button.Font.Size - 9f * dpi / 72f) < 0.01f,
                            "字体直接按目标显示器像素尺寸渲染，DPI=" + dpi);
                        Check(Math.Abs((float)other.GetType().GetField("RenderScale", ReviewInstance).GetValue(other) - 1.5f) < 0.001f,
                            "另一窗口的 DPI 不被当前窗口改变");
                        if (dpi == 144 || dpi == 192) SaveDpiForm(form, Path.Combine(output, "launcher-" + dpi + ".png"));
                    }
                    Check(form.ClientSize == original, "多次倍率往返后客户端尺寸无累计误差");
                    SetDpi(form, 192); form.ClientSize = new Size(1240, 1000); form.PerformLayout();
                    foreach (string key in WorkbenchPageKeys)
                    {
                        WorkbenchShowPage(form, key); WorkbenchCheckViewport(form, 192);
                    }
                    WorkbenchShowPage(form, "favorites");
                    Check(WorkbenchActive(WorkbenchFind(form, "favoritesBrowser")), "窄窗口可进入完整收藏视口");
                    Check(!WorkbenchActive(WorkbenchFind(form, "recentBrowser")), "窄窗口不把最近播放叠在收藏前面");
                    WorkbenchShowPage(form, "recent");
                    Check(WorkbenchActive(WorkbenchFind(form, "recentBrowser")), "窄窗口可独立进入最近播放视口");
                    Check(WorkbenchActive(WorkbenchFind(form, "recentOpenPot")) && WorkbenchActive(WorkbenchFind(form, "recentOpenMpv")),
                        "窄窗口保留两个明确播放器动作");
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
        if (!thread.Join(60000)) throw new Exception("高 DPI 八页矩阵超时");
        if (failure != null) throw failure;
    }
    private static void CheckLauncherAlignment(Form form, int dpi)
    {
        Control input = WorkbenchField(form, "input"), inputLabel = WorkbenchField(form, "inputLabel");
        Check(WorkbenchActive(input), "收藏页房间输入框可见，DPI=" + dpi);
        if (WorkbenchActive(inputLabel))
            Check(Math.Abs((2 * input.Top + input.Height) - (2 * inputLabel.Top + inputLabel.Height)) <= 1,
                "房间输入与可见标签垂直居中，DPI=" + dpi);
        var settings = new List<Control>();
        foreach (string name in new[] { "qualityBox", "bufferBox", "danmakuBox", "scBox", "enhanceBox" })
        {
            Control combo = WorkbenchField(form, name);
            Check(WorkbenchActive(combo), "收藏快捷设置不丢失，DPI=" + dpi + " " + name);
            settings.Add(combo);
        }
        for (int i = 0; i < settings.Count; i++)
        for (int j = i + 1; j < settings.Count; j++)
        {
            Control a = settings[i], b = settings[j];
            if (a.Top >= b.Bottom || b.Top >= a.Bottom) continue;
            Check(Math.Abs((2 * a.Top + a.Height) - (2 * b.Top + b.Height)) <= 1,
                "快捷下拉同一行保持垂直居中，DPI=" + dpi + " " + a.Name + "/" + b.Name);
            Check(!a.Bounds.IntersectsWith(b.Bounds), "快捷下拉互不覆盖，DPI=" + dpi);
        }
        WorkbenchShowPage(form, "sessions");
        ListView sessions = (ListView)WorkbenchFind(form, "workbenchSessions");
        Check(sessions != null && sessions.Columns.Count == 5, "会话表在独立页面保留五列，DPI=" + dpi);
        int used = 0; foreach (ColumnHeader column in sessions.Columns) used += column.Width;
        Check(Math.Abs(used - sessions.ClientSize.Width) <= 6, "会话列宽适应当前页面视口，DPI=" + dpi);
        WorkbenchShowPage(form, "favorites");
    }

    private static Bitmap RenderDpiControl(Control control)
    {
        Bitmap image = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height), PixelFormat.Format32bppArgb);
        // WM_PRINT does not use the same path as WM_PAINT for native owner-drawn
        // controls. Invoke their production painter to avoid a stale HWND font in previews.
        using (Graphics paint = Graphics.FromImage(image))
        {
            // ModernButton / 自绘 ComboBox 直接调生产绘制方法；AntdUI 的自绘控件
            //（Button/Tag/Select）走下面的默认 DrawToBitmap —— 实测 2.4.15 能画出内容
            // 且不抛异常，所以预览渲染不需要为它们特判。
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
                if (WorkbenchLocallyVisible(child))
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
                if (WorkbenchLocallyVisible(c))
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
