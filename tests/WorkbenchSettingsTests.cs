using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterWorkbenchSettingsCases()
    {
        TestRunner.Add("工作台设置", "完整弹幕选项与跨页保存不覆盖最新偏好", TestWorkbenchSettingsMerge);
    }

    private static void TestWorkbenchSettingsMerge()
    {
        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            string directory = ReviewDirectory();
            FieldInfo favoriteField = hub.GetField("Favorites", ReviewStatic), runtimeField = hub.GetField("RuntimeDir", ReviewStatic);
            object oldFavorite = favoriteField.GetValue(null), oldRuntime = runtimeField.GetValue(null);
            Type theme = DanmakuType("ModernTheme");
            bool oldDark = (bool)theme.GetProperty("IsDark", ReviewStatic).GetValue(null, null);
            try
            {
                favoriteField.SetValue(null, Path.Combine(directory, "favorites.json")); runtimeField.SetValue(null, directory);
                string configPath = Path.Combine(directory, "danmaku.json");
                File.WriteAllText(configPath, "{\"FontScalePercent\":100,\"OpacityPercent\":90,\"DuplicateWindowMs\":4200,\"ScMode\":\"full\",\"MpvQuality\":\"off\",\"FontName\":\"微软雅黑\"}");
                Type ui = DanmakuType("WorkbenchDanmakuUi"), configType = DanmakuType("DanmakuConfig");
                using (Form owner = new Form())
                using (Control page = (Control)ui.GetMethod("Build", ReviewStatic).Invoke(null, new object[] { owner, null }))
                {
                    owner.Controls.Add(page);
                    string[] names = { "FontScalePercent", "SpeedPercent", "OpacityPercent", "DisplayAreaPercent", "DanmakuDelayMs", "MaxOnScreen", "DuplicateMode", "DuplicateWindowMs", "FontName", "ScMode", "ScMaxStack", "ScCorner", "MpvQuality" };
                    foreach (string name in names) Check(page.Controls.Find("workbenchSetting_" + name, true).Length == 1, "弹幕设置保留下拉选项：" + name);
                    Check(((ComboBox)page.Controls.Find("workbenchSetting_ScMode", true)[0]).Items.Count == 4, "SC 四种模式全部保留");
                    ComboBox opacity = (ComboBox)page.Controls.Find("workbenchSetting_OpacityPercent", true)[0];
                    Check(opacity.Text.Contains("90"), "非预设不透明度90不伪装成100");
                    ComboBox window = (ComboBox)page.Controls.Find("workbenchSetting_DuplicateWindowMs", true)[0];
                    Check(window.Text.Contains("4200"), "保留当前自定义合并时间");
                    ((ComboBox)page.Controls.Find("workbenchSetting_FontScalePercent", true)[0]).SelectedIndex = 3;
                    object latest = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { configPath });
                    configType.GetField("ScMode").SetValue(latest, "badge"); configType.GetField("MpvQuality").SetValue(latest, "high");
                    Check((bool)configType.GetMethod("Save", ReviewInstance).Invoke(latest, new object[] { configPath }), "合成首页偏好修改已保存");
                    Check((bool)page.GetType().GetMethod("SaveChanges", ReviewInstance).Invoke(page, new object[] { false }), "工作台设置可保存");
                    object result = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { configPath });
                    Check((int)configType.GetField("FontScalePercent").GetValue(result) == 130, "保存本页实际修改的字号");
                    Check((string)configType.GetField("ScMode").GetValue(result) == "badge", "未编辑的SC保留首页最新值");
                    Check((string)configType.GetField("MpvQuality").GetValue(result) == "high", "未编辑的画质增强保留首页最新值");
                    Check((int)configType.GetField("OpacityPercent").GetValue(result) == 90 && (int)configType.GetField("DuplicateWindowMs").GetValue(result) == 4200, "未修改自定义值保持不变");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                favoriteField.SetValue(null, oldFavorite); runtimeField.SetValue(null, oldRuntime);
                theme.GetMethod("Apply", ReviewStatic).Invoke(null, new object[] { oldDark });
                Directory.Delete(directory, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Check(thread.Join(20000), "工作台设置隐藏夹具结束");
        if (failure != null) throw failure;
    }
}
