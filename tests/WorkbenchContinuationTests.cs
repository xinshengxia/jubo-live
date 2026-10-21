using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterWorkbenchContinuationCases()
    {
        TestRunner.Add("工作台续作", "更多设置嵌入控件与传统弹窗接口兼容", TestContinuationEmbeddedMore);
        TestRunner.Add("工作台续作", "更多设置跨页合并与重新读取不丢修改", TestContinuationMoreMerge);
        TestRunner.Add("工作台续作", "更多设置随父窗口DPI缩放且标签字段同列对齐", TestContinuationMoreDpi);
        TestRunner.Add("工作台续作", "圆角分组关闭后退订主题事件", TestContinuationCardLifetime);
        TestRunner.Add("工作台续作", "窄窗口分类头自适应换行且所有分类可达", TestContinuationCompactHeader);
    }

    private static void ContinuationWithSettings(Action<Form, Control, Type, string> body)
    {
        RunUiPolish(delegate
        {
            string directory = ReviewDirectory();
            FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), favorites = hub.GetField("Favorites", ReviewStatic);
            object previousRuntime = runtime.GetValue(null), previousFavorites = favorites.GetValue(null);
            Type config = DanmakuType("DanmakuConfig"), ui = DanmakuType("WorkbenchDanmakuUi");
            try
            {
                runtime.SetValue(null, directory); favorites.SetValue(null, Path.Combine(directory, "favorites.json"));
                string path = Path.Combine(directory, "danmaku.json");
                File.WriteAllText(path, "{\"FontScalePercent\":100,\"ScMode\":\"full\",\"MpvQuality\":\"off\",\"ShowScrolling\":true,\"QueueCapacity\":30,\"FontName\":\"微软雅黑\"}");
                using (Form owner = (Form)Activator.CreateInstance(DanmakuType("DpiForm"), ReviewInstance, null, new object[0], null))
                using (Control page = (Control)ui.GetMethod("Build", ReviewStatic).Invoke(null, new object[] { owner, null }))
                {
                    owner.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(owner, false);
                    owner.ClientSize = new Size(850, 700); owner.Controls.Add(page); owner.PerformLayout();
                    object tabs = page.GetType().GetField("pages", ReviewInstance).GetValue(page);
                    tabs.GetType().GetProperty("SelectedIndex", ReviewInstance).SetValue(tabs, 3, null);
                    body(owner, page, config, path);
                    Check(!owner.Visible, "续作夹具没有显示窗口或占用前台");
                }
            }
            finally
            {
                runtime.SetValue(null, previousRuntime); favorites.SetValue(null, previousFavorites);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
    }

    private static Control ContinuationSetting(Control root, string field)
    {
        Control[] controls = root.Controls.Find("featureSetting_" + field, true);
        Check(controls.Length == 1, "更多设置字段存在且唯一：" + field);
        return controls[0];
    }

    private static object ContinuationLoad(Type config, string path)
    { return config.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { path }); }

    private static void ContinuationSave(Type config, object value, string path)
    { Check((bool)config.GetMethod("Save", ReviewInstance).Invoke(value, new object[] { path }), "合成配置保存成功"); }

    private static void TestContinuationEmbeddedMore()
    {
        ContinuationWithSettings(delegate(Form owner, Control page, Type config, string path)
        {
            Control more = page.Controls.Find("workbenchDanmakuMore", true)[0];
            foreach (Control child in WorkbenchDescendants(more))
                Check(!(child is Form), "更多页只有普通控件，不建立第二个DPI窗体");
            foreach (string field in new[] { "ShowScrolling", "BlockUsers", "QueueCapacity", "ShowGift", "TextOpacityPercent" })
                ContinuationSetting(more, field);
            Type features = DanmakuType("DanmakuFeatureSettings");
            object settings = ContinuationLoad(config, path);
            using (Form legacy = (Form)features.GetMethod("Build", ReviewStatic, null, new[] { config }, null).Invoke(null, new[] { settings }))
                Check(legacy.Controls.Find("SettingsPages", true).Length == 1 && !legacy.Visible,
                    "原更多弹窗Build接口仍保留完整分类且未显示");
        });
    }

    private static void TestContinuationMoreMerge()
    {
        ContinuationWithSettings(delegate(Form owner, Control page, Type config, string path)
        {
            Control scrolling = ContinuationSetting(page, "ShowScrolling");
            scrolling.GetType().GetProperty("Checked").SetValue(scrolling, false, null);
            ComboBox font = (ComboBox)page.Controls.Find("workbenchSetting_FontScalePercent", true)[0]; font.SelectedIndex = 3;
            object newest = ContinuationLoad(config, path);
            config.GetField("ScMode").SetValue(newest, "badge"); config.GetField("MpvQuality").SetValue(newest, "high");
            config.GetField("QueueCapacity").SetValue(newest, 55); ContinuationSave(config, newest, path);
            page.GetType().GetMethod("RefreshFromConfig", ReviewInstance).Invoke(page, new object[] { false });
            Check(!(bool)scrolling.GetType().GetProperty("Checked").GetValue(scrolling, null), "切页刷新保留未保存的更多设置修改");
            Control capacity = ContinuationSetting(page, "QueueCapacity");
            Check((int)capacity.GetType().GetProperty("Value", ReviewInstance).GetValue(capacity, null) == 55,
                "未编辑的更多设置同步其他入口的最新值");
            Check((bool)page.GetType().GetMethod("SaveChanges", ReviewInstance).Invoke(page, new object[] { false }), "嵌入更多设置保存成功");
            object result = ContinuationLoad(config, path);
            Check(!(bool)config.GetField("ShowScrolling").GetValue(result), "更多页实际修改被持久保存");
            Check((int)config.GetField("FontScalePercent").GetValue(result) == 130, "同次保存包含基本页实际修改");
            Check((string)config.GetField("ScMode").GetValue(result) == "badge"
                && (string)config.GetField("MpvQuality").GetValue(result) == "high", "保存更多设置不覆盖首页最新SC与画质增强");
            Check((int)config.GetField("QueueCapacity").GetValue(result) == 55, "保存不回退其他入口的更多设置值");
            scrolling.GetType().GetProperty("Checked").SetValue(scrolling, true, null);
            page.GetType().GetMethod("RefreshFromConfig", ReviewInstance).Invoke(page, new object[] { true });
            Check(!(bool)scrolling.GetType().GetProperty("Checked").GetValue(scrolling, null), "重新读取明确丢弃未保存修改并恢复已保存更多设置");
            Check(!(bool)config.GetField("ShowScrolling").GetValue(ContinuationLoad(config, path)), "重新读取不会改写配置文件");
        });
    }

    private static void TestContinuationMoreDpi()
    {
        ContinuationWithSettings(delegate(Form owner, Control page, Type config, string path)
        {
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                SetDpi(owner, dpi); owner.PerformLayout(); page.PerformLayout();
                owner.ClientSize = new Size(500 * dpi / 96, 650 * dpi / 96); owner.PerformLayout(); page.PerformLayout();
                foreach (string field in new[] { "ShowScrolling", "TopFixedZonePercent", "EmoteSizeMode", "BlockUsers", "QueueCapacity" })
                {
                    Control editor = ContinuationSetting(page, field); TableLayoutPanel row = editor.Parent as TableLayoutPanel;
                    Check(row != null, "更多设置使用一致的标签字段表格：" + field);
                    TableLayoutPanelCellPosition at = row.GetPositionFromControl(editor);
                    Control label = row.GetControlFromPosition(0, at.Row);
                    Check(label != null && label.Bottom > editor.Top && editor.Bottom > label.Top,
                        "标签与字段纵向相交对齐 DPI=" + dpi + " " + field);
                    Check(editor.Left >= 0 && editor.Right <= row.ClientSize.Width + 1,
                        "更多设置字段横向可达 DPI=" + dpi + " " + field);
                    if (editor.GetType().Name == "DanmakuSettingsNumber")
                    {
                        Control numberInput = (Control)editor.GetType().GetField("input", ReviewInstance).GetValue(editor);
                        Check(numberInput.Width >= (int)Math.Round(48 * dpi / 96.0),
                            "数字输入保留可读宽度而不被步进按钮挤空 DPI=" + dpi + " " + field);
                        Check(numberInput.Left >= 0 && numberInput.Right <= editor.ClientSize.Width,
                            "数字输入完整位于数值控件内 DPI=" + dpi + " " + field);
                    }
                }
                Control more = page.Controls.Find("workbenchDanmakuMore", true)[0];
                foreach (Control child in WorkbenchDescendants(more)) Check(!(child is Form), "DPI变化后仍只有父窗口负责缩放");
            }
        });
    }

    private static void TestContinuationCardLifetime()
    {
        RunUiPolish(delegate
        {
            Type theme = DanmakuType("ModernTheme"); FieldInfo field = theme.GetField("Changed", ReviewStatic);
            Delegate before = (Delegate)field.GetValue(null); int count = before == null ? 0 : before.GetInvocationList().Length;
            Control card = (Control)Activator.CreateInstance(DanmakuType("DanmakuCard"), ReviewInstance, null, new object[0], null);
            card.Dispose();
            Delegate after = (Delegate)field.GetValue(null); int final = after == null ? 0 : after.GetInvocationList().Length;
            Check(final == count, "圆角卡片Dispose退订主题事件，不保留已释放分组");
        });
    }

    private static void TestContinuationCompactHeader()
    {
        RunUiPolish(delegate
        {
            using (Form owner = (Form)Activator.CreateInstance(DanmakuType("DpiForm"), ReviewInstance, null, new object[0], null))
            using (Control tabs = (Control)Activator.CreateInstance(DanmakuType("DanmakuSettingsPages"), ReviewInstance, null, new object[0], null))
            {
                owner.GetType().GetField("FitWorkingArea", ReviewInstance).SetValue(owner, false);
                owner.ClientSize = new Size(520, 500); owner.Controls.Add(tabs); tabs.Dock = DockStyle.Fill;
                foreach (string title in new[] { "显示与内容", "SC 醒目留言", "过滤与开关", "更多弹幕设置", "界面与热键" })
                    tabs.GetType().GetMethod("AddPage", ReviewInstance).Invoke(tabs, new object[] { title });
                Control header = (Control)tabs.GetType().GetField("header", ReviewInstance).GetValue(tabs);
                foreach (int dpi in new[] { 96, 144, 192 })
                foreach (int width in new[] { 520, 380 })
                {
                    SetDpi(owner, dpi); owner.ClientSize = new Size(width * dpi / 96, 500 * dpi / 96);
                    owner.PerformLayout(); tabs.PerformLayout();
                    Check(header.Controls.Count == 5, "窄窗口不删除设置分类 DPI=" + dpi);
                    foreach (Control button in header.Controls)
                    {
                        Check(button.Left >= 0 && button.Right <= header.ClientSize.Width
                            && button.Top >= 0 && button.Bottom <= header.ClientSize.Height,
                            "分类按钮始终位于分类头内 DPI=" + dpi + " width=" + width + " " + button.Text);
                        int text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                        Check(text <= button.Width, "分类文字没有被裁切 DPI=" + dpi + " " + button.Text);
                    }
                    if (width == 380) Check(header.Height > 42 * dpi / 96, "过窄时换成多行而不把末尾分类推出窗口 DPI=" + dpi);
                }
                Check(!owner.Visible, "分类头夹具不显示窗口");
            }
        });
    }
}
