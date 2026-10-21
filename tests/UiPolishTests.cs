using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterUiPolishCases()
    {
        TestRunner.AddDesktop("界面修复", "会话表无虚构列且换尺寸列宽稳定", TestNoFillerColumns);
        TestRunner.AddDesktop("界面修复", "设置滚动不横向溢出、末尾可达且深色无白轨道", TestSettingsScrollPolish);
    }
    private static void RunUiPolish(Action action)
    {
        Exception failure = null;
        Thread worker = new Thread(delegate() { try { action(); } catch (Exception ex) { failure = ex; } });
        worker.SetApartmentState(ApartmentState.STA); worker.IsBackground = true; worker.Start();
        // 24 组 DPI/宽度/主题组合及位图渲染在负载下可能超过 15 秒；仍保留硬超时。
        if (!worker.Join(60000)) throw new Exception("界面滚动验收超时");
        if (failure != null) throw failure;
    }
    private static void TestNoFillerColumns()
    {
        RunUiPolish(delegate
        {
            MethodInfo build = hub.GetMethod("BuildLauncherForm", Flags, null, new Type[] { typeof(int) }, null);
            using (Form form = (Form)build.Invoke(null, new object[] { 1 }))
            {
                WorkbenchShowPage(form, "sessions");
                foreach (int dpi in new[] { 96, 120, 144, 192, 288 })
                {
                    SetDpi(form, dpi);
                    foreach (int width in new[] { 700, 1120, 1300 })
                    {
                        form.ClientSize = new Size((int)Math.Round(width * dpi / 96.0), (int)Math.Round(600 * dpi / 96.0));
                        form.PerformLayout();
                        int sessionTables = 0;
                        foreach (Control control in WorkbenchDescendants(form))
                        {
                            ListView list = control as ListView;
                            if (list == null || list.Name != "workbenchSessions") continue;
                            sessionTables++;
                            Check(list.Columns.Count == 5, "会话表只有五个有效字段，不产生窄空列");
                            int total = 0;
                            foreach (ColumnHeader column in list.Columns)
                            { Check(!string.IsNullOrEmpty(column.Text), "所有列均为真实字段"); total += column.Width; }
                            Check(Math.Abs(total - list.ClientSize.Width) <= 1, "列宽覆盖实际客户区，尾部不留多重分隔线");
                        }
                        Check(sessionTables == 1, "明确检查唯一会话表，不因控件被移入容器而空转");
                    }
                }
                SaveDpiForm(form, Path.Combine(SourceRoot, "artifacts", "dpi-preview", "launcher-no-filler-dark.png"));
            }
        });
    }
    private static void TestSettingsScrollPolish()
    {
        RunUiPolish(delegate
        {
            foreach (bool dark in new[] { true, false })
            {
                DanmakuType("ModernTheme").GetMethod("Apply", ReviewStatic).Invoke(null, new object[] { dark });
                object parts = DanmakuType("DanmakuSettingsDialog").GetMethod("Build", ReviewStatic)
                    .Invoke(null, new object[] { Activator.CreateInstance(DanmakuType("DanmakuConfig")) });
                using (Form form = (Form)parts.GetType().GetField("Dialog").GetValue(parts))
                {
                    Panel body = (Panel)parts.GetType().GetField("Body").GetValue(parts);
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
                    SetDpi(form, 96); form.Show(); Application.DoEvents();
                    foreach (int dpi in new[] { 96, 120, 144, 168, 192, 288 })
                    {
                        foreach (int logicalWidth in new[] { 700, 500 })
                        {
                            SetDpi(form, dpi);
                            form.ClientSize = new Size((int)Math.Round(logicalWidth * dpi / 96.0), (int)Math.Round(440 * dpi / 96.0));
                            SetDpi(form, dpi); form.PerformLayout(); body.PerformLayout();
                            Check(!body.HorizontalScroll.Visible, "设置页无横向滚动条 dark=" + dark + " DPI=" + dpi + " width=" + logicalWidth);
                            int right = body.ClientSize.Width;
                            foreach (Control child in body.Controls)
                                Check(child.Left >= 0 && child.Right <= right, "设置控件不被纵向滚动条覆盖：" + child.GetType().Name);
                            body.AutoScrollPosition = new Point(0, int.MaxValue / 2);
                            Control last = null;
                            foreach (Control child in body.Controls) if (last == null || child.Bottom > last.Bottom) last = child;
                            Check(last != null && last.Bottom <= body.ClientSize.Height, "滚动到底可完整看到最后一项说明：last=" + (last == null ? "null" : last.Bounds.ToString()) + " client=" + body.ClientSize + " scroll=" + body.AutoScrollPosition + " min=" + body.AutoScrollMinSize);
                            Control ok = (Control)parts.GetType().GetField("Ok").GetValue(parts);
                            Check(form.ClientRectangle.Contains(ok.Bounds), "滚动内容不覆盖保存按钮");
                            body.AutoScrollPosition = Point.Empty;
                        }
                    }
                    SetDpi(form, 192); form.ClientSize = new Size(1400, 1000); SetDpi(form, 192);
                    using (Bitmap image = RenderDpiControl(body))
                    {
                        if (dark)
                        {
                            int bright = 0;
                            int edge = image.Width - 1;
                            for (int y = 3; y < image.Height - 3; y++)
                            { Color c = image.GetPixel(edge, y); if (c.R > 180 && c.G > 180 && c.B > 180) bright++; }
                            Check(bright <= 2, "深色滚动轨道外沿没有长条白底（亮像素=" + bright + "）");
                        }
                    }
                    SaveDpiForm(form, Path.Combine(SourceRoot, "artifacts", "dpi-preview", dark ? "settings-scroll-dark.png" : "settings-scroll-light.png"));
                }
            }
        });
    }
}
