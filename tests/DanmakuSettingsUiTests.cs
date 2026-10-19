using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static Control SettingsPage(Control root, string text)
    {
        foreach (Control child in root.Controls)
        {
            if (!(child is Button) && child.Text == text) return child;
            Control found = SettingsPage(child, text); if (found != null) return found;
        }
        return null;
    }
    private static void SelectSettingsPage(Form form, string text)
    {
        Control page = SettingsPage(form, text);
        TabControl native = page.Parent as TabControl;
        if (native != null) native.SelectedTab = (TabPage)page;
        else ClickSettingsPage(form, text);
        form.PerformLayout(); Application.DoEvents();
    }
    private static bool ClickSettingsPage(Control root, string text)
    {
        foreach (Control child in root.Controls)
        {
            Button button = child as Button;
            if (button != null && button.Text == text) { button.PerformClick(); return true; }
            if (ClickSettingsPage(child, text)) return true;
        }
        return false;
    }
    private static void SettingsShot(Form form, string name)
    {
        using (Bitmap image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), name + ".png"), ImageFormat.Png);
        }
    }
    private static void TestSettingsAdaptation()
    {
        Type theme = DanmakuType("ModernTheme");
        bool original = (bool)theme.GetProperty("IsDark").GetValue(null, null);
        try
        {
            CallStatic(theme, "Apply", true);
            object config = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
            using (Form form = (Form)CallStatic(DanmakuType("DanmakuFeatureSettings"), "BuildPreview", config))
            {
                DanmakuScene.Set(form, "FitWorkingArea", false);
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
                form.Show(); Application.DoEvents(); SelectSettingsPage(form, "画面安全区");
                SettingsShot(form, "settings-safe-dark");
                Color surface = (Color)theme.GetProperty("Surface").GetValue(null, null);
                Check(SettingsPage(form, "画面安全区").BackColor.ToArgb() == surface.ToArgb(), "深色安全区页面没有系统白底");
                foreach (bool dark in new bool[] { true, false })
                foreach (int dpi in new int[] { 96, 144, 192, 96 })
                {
                    CallStatic(theme, "Apply", dark);
                    DanmakuType("DpiForm").GetMethod("ApplyDpi", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { dpi, false, null });
                    foreach (Size logical in new Size[] { new Size(740, 620), new Size(540, 440) })
                    {
                        form.ClientSize = new Size(logical.Width * dpi / 96, logical.Height * dpi / 96);
                        foreach (string title in new string[] { "显示与外观", "过滤与重点", "拥挤与事件", "画面安全区" })
                        {
                            SelectSettingsPage(form, title);
                            Control page = SettingsPage(form, title);
                            surface = (Color)theme.GetProperty("Surface").GetValue(null, null);
                            if (page.BackColor.ToArgb() != surface.ToArgb()) throw new Exception("页底色未跟随主题：" + title);
                            foreach (Control control in form.Controls)
                                if (control.Dock == DockStyle.Bottom)
                                {
                                    if (control.Bottom > form.ClientSize.Height) throw new Exception("按钮条越界");
                                    foreach (Control button in control.Controls)
                                        if (!button.Visible || button.Bottom > control.Height || button.Right > control.Width) throw new Exception("底部按钮越界");
                                    foreach (Control sibling in form.Controls)
                                        if (sibling != control && sibling.Bounds.IntersectsWith(control.Bounds)) throw new Exception("页面覆盖按钮条");
                                }
                            if (title == "画面安全区")
                            {
                                Control canvas = FindSettingsControl(page, "DanmakuSafeAreaCanvas");
                                RectangleF frame = (RectangleF)canvas.GetType().GetProperty("Frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas, null);
                                if (Math.Abs(frame.Width / frame.Height - 16f / 9f) > 0.005f || frame.Left < 0 || frame.Bottom > canvas.Height + 0.1f)
                                    throw new Exception("安全区示意画面比例或边界不正确");
                                Point point = new Point((int)(frame.Left + frame.Width * 0.25f), (int)(frame.Top + frame.Height * 0.75f));
                                Point percent = (Point)canvas.GetType().GetMethod("Percent", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, new object[] { point });
                                if (Math.Abs(percent.X - 25) > 1 || Math.Abs(percent.Y - 75) > 1) throw new Exception("安全区坐标映射未跟随缩放");
                                FlowLayoutPanel presets = FindSettingsControl(page, "FlowLayoutPanel") as FlowLayoutPanel;
                                foreach (Control button in presets.Controls)
                                    if (button.Right > presets.ClientSize.Width || button.Bottom > presets.ClientSize.Height) throw new Exception("安全区预设按钮越界");
                                if (dark && dpi == 192 && logical.Width == 740) SettingsShot(form, "settings-safe-200");
                            }
                            if (dpi == 192 && logical.Width == 740 && title == "显示与外观") SettingsShot(form, dark ? "settings-display-dark-200" : "settings-display-light-200");
                            if (dark && dpi == 192 && logical.Width == 540 && title == "显示与外观") SettingsShot(form, "settings-small-dark-200");
                        }
                    }
                }
                Check(true, "四个页面在深浅主题、100/150/200% 和默认/最小窗口下布局可用");
                SelectSettingsPage(form, "显示与外观");
                ScrollableControl scroll = FindSettingsControl(SettingsPage(form, "显示与外观"), "Panel") as ScrollableControl;
                Check(scroll != null && scroll.AutoScroll && scroll.AutoScrollMinSize.Height > 0,
                    "小窗口可滚动访问所有显示设置");
                Control last = SettingsField(form, "弹幕自身不透明度 %");
                scroll.ScrollControlIntoView(last); Application.DoEvents();
                Rectangle visible = scroll.RectangleToClient(last.RectangleToScreen(last.ClientRectangle));
                Check(visible.Bottom <= scroll.ClientSize.Height && visible.Top >= 0, "最小窗口能滚动到最后一个设置");
            }
        }
        finally { CallStatic(theme, "Apply", original); }
    }
    private static Control SettingsField(Control root, string caption)
    {
        foreach (Control child in root.Controls)
        {
            if (child.AccessibleName == caption) return child;
            Control found = SettingsField(child, caption); if (found != null) return found;
        }
        return null;
    }
    private static void TestSettingsEditing()
    {
        object config = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
        using (Form form = (Form)CallStatic(DanmakuType("DanmakuFeatureSettings"), "BuildPreview", config))
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
            form.Show(); Application.DoEvents();
            Control field = SettingsField(form, "顶部固定可用区域 %");
            TextBox input = (TextBox)FindSettingsControl(field, "TextBox");
            PropertyInfo value = field.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic);
            input.Text = "41"; Check((int)value.GetValue(field, null) == 41, "数字支持直接输入");
            ((Button)SettingsField(field, "增加")).PerformClick(); Check((int)value.GetValue(field, null) == 42, "增加按钮读取当前输入");
            ((Button)SettingsField(field, "减少")).PerformClick(); Check((int)value.GetValue(field, null) == 41, "减少按钮有效");
            input.Text = "9999"; Check((int)value.GetValue(field, null) == 100 && !SettingsField(field, "增加").Enabled, "输入超上限时收敛且禁用增加");
            input.Text = "0"; Check((int)value.GetValue(field, null) == 10 && !SettingsField(field, "减少").Enabled, "输入低于下限时收敛且禁用减少");
            input.Text = ""; Check((int)value.GetValue(field, null) == 10, "空输入保持有效值");
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, new object[] { new KeyEventArgs(Keys.Up) });
            Check((int)value.GetValue(field, null) == 11, "方向上键调整数值，无系统键盘输入");
            Check((int)DanmakuScene.Get(config, "TopFixedZonePercent") == 40, "未应用时不改变配置");
            Control tabs = FindSettingsControl(form, "DanmakuSettingsPages");
            MethodInfo key = tabs.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            key.Invoke(tabs, new object[] { new Message(), Keys.Control | Keys.Tab });
            Check((int)tabs.GetType().GetProperty("SelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tabs, null) == 1, "Ctrl+Tab 切换页签");
            key.Invoke(tabs, new object[] { new Message(), Keys.Control | Keys.Shift | Keys.Tab });
            Check((int)tabs.GetType().GetProperty("SelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tabs, null) == 0, "Ctrl+Shift+Tab 返回页签");
            input.Text = "55"; ((Button)form.AcceptButton).PerformClick();
            Check(form.DialogResult == DialogResult.OK && (int)DanmakuScene.Get(config, "TopFixedZonePercent") == 55, "应用按钮保存编辑后的值");
        }
        using (Form form = (Form)CallStatic(DanmakuType("DanmakuFeatureSettings"), "BuildPreview", config))
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
            form.Show(); Application.DoEvents();
            Control field = SettingsField(form, "顶部固定可用区域 %");
            ((TextBox)FindSettingsControl(field, "TextBox")).Text = "70";
            ((Button)form.CancelButton).PerformClick();
            Check(form.DialogResult == DialogResult.Cancel && (int)DanmakuScene.Get(config, "TopFixedZonePercent") == 55, "取消不保存编辑内容");
        }
    }
    private static Control FindSettingsControl(Control root, string type)
    {
        foreach (Control child in root.Controls)
        {
            if (child.GetType().Name == type) return child;
            Control found = FindSettingsControl(child, type); if (found != null) return found;
        }
        return null;
    }
}
