// --------------------------------------------------------------------------------------------
// 主题（深色 / 浅色）回归：**每个控件的实际背景色都必须在正确的一侧**。
//
// 为什么要有这条：2026-09-27 的深色模式坏得很难看，而当时的控件树结构回归**完全看不出来** ——
// 位置一个不差，颜色是错的。真实原因是控件在 ModernTheme.Apply() 之前就被建出来了
// （LauncherUi 的控件是字段初始化器建的），标准 TextBox 这种「只在构造函数里取一次色」的
// 控件于是永远停在浅色。
//
// 判据直接量化：不透明控件的背景亮度。深色下不许有亮底（> 0.5），浅色下不许有深底（< 0.3）。
// 透明控件（Label 的 Color.Transparent，alpha=0）跳过 —— 它们本来就该透出父容器底色。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

internal static partial class HardeningTests
{
    /// <summary>跑一次探针（启动器窗体或设置对话框），返回原始 JSON。</summary>
    private static string RunUiProbe(string launcher, string mode, string theme)
    {
        string json = Path.Combine(Path.GetTempPath(), "livehub-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string arguments = mode + " \"" + json + "\"" + (theme == null ? "" : " " + theme);
            int code = RunLauncherExitCode(launcher, arguments);
            if (code != 0 || !File.Exists(json)) return null;
            return File.ReadAllText(json, Encoding.UTF8);
        }
        finally { try { File.Delete(json); } catch (Exception) { } }
    }

    /// <summary>#RRGGBB / #AARRGGBB → 相对亮度（0=黑 1=白）；解析失败返回 -1。</summary>
    private static double Luminance(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return -1;
        string v = hex.Substring(1);
        if (v.Length == 8)
        {
            // 全透明 = 透出父容器，不参与判断
            if (v.Substring(0, 2) == "00") return -1;
            v = v.Substring(2);
        }
        if (v.Length != 6) return -1;
        int r = int.Parse(v.Substring(0, 2), NumberStyles.HexNumber);
        int g = int.Parse(v.Substring(2, 2), NumberStyles.HexNumber);
        int b = int.Parse(v.Substring(4, 2), NumberStyles.HexNumber);
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
    }

    /// <summary>收集「背景色跑到错误一侧」的控件，返回可读清单。</summary>
    private static List<string> WrongSideBackgrounds(string json, bool dark)
    {
        var bad = new List<string>();
        var root = new System.Web.Script.Serialization.JavaScriptSerializer()
            .Deserialize<Dictionary<string, object>>(json);
        var list = root == null ? null : root["controls"] as ArrayList;
        if (list == null) return bad;
        foreach (object entry in list)
        {
            var map = entry as Dictionary<string, object>;
            if (map == null) continue;
            string back = Convert.ToString(map["back"]);
            double lum = Luminance(back);
            if (lum < 0) continue;              // 透明 / 解析不出 → 跳过
            bool wrong = dark ? lum > 0.5 : lum < 0.3;
            if (!wrong) continue;
            string text = Convert.ToString(map["text"]);
            if (text.Length > 18) text = text.Substring(0, 18);
            bad.Add(Convert.ToString(map["type"]) + "[" + text + "]=" + back);
        }
        return bad;
    }

    private static void TestThemeColors(string launcher)
    {
        // ---- ① 启动器窗体 ----
        string darkJson = RunUiProbe(launcher, "--ui-probe", "--dark");
        string lightJson = RunUiProbe(launcher, "--ui-probe", "--light");
        Check(darkJson != null && lightJson != null, "主题：--ui-probe 支持 --dark / --light（不依赖用户配置）");
        if (darkJson == null || lightJson == null) return;

        var darkRoot = new System.Web.Script.Serialization.JavaScriptSerializer()
            .Deserialize<Dictionary<string, object>>(darkJson);
        Check(Convert.ToBoolean(darkRoot["dark"]), "主题：--dark 时 ModernTheme.IsDark == true");
        var lightRoot = new System.Web.Script.Serialization.JavaScriptSerializer()
            .Deserialize<Dictionary<string, object>>(lightJson);
        Check(!Convert.ToBoolean(lightRoot["dark"]), "主题：--light 时 ModernTheme.IsDark == false");

        var darkPalette = darkRoot["palette"] as Dictionary<string, object>;
        var lightPalette = lightRoot["palette"] as Dictionary<string, object>;
        Check(Convert.ToString(darkPalette["surface"]) != Convert.ToString(lightPalette["surface"]),
            "主题：深浅两套调色板确实不同（" + darkPalette["surface"] + " vs " + lightPalette["surface"] + "）");
        Check(Luminance(Convert.ToString(darkPalette["surface"])) < 0.3, "主题：深色底本身是深的");
        Check(Luminance(Convert.ToString(lightPalette["surface"])) > 0.7, "主题：浅色底本身是亮的");

        List<string> darkBad = WrongSideBackgrounds(darkJson, true);
        Check(darkBad.Count == 0, "主题：深色下没有亮底控件"
            + (darkBad.Count > 0 ? "（" + string.Join("、", darkBad.ToArray()) + "）" : ""));
        List<string> lightBad = WrongSideBackgrounds(lightJson, false);
        Check(lightBad.Count == 0, "主题：浅色下没有深底控件"
            + (lightBad.Count > 0 ? "（" + string.Join("、", lightBad.ToArray()) + "）" : ""));

        // 结构不许随主题变化（位置/数量都得一样）—— 只换颜色
        int darkCount = ((ArrayList)darkRoot["controls"]).Count;
        int lightCount = ((ArrayList)lightRoot["controls"]).Count;
        Check(darkCount == lightCount, "主题：控件数量与主题无关（" + darkCount + " vs " + lightCount + "）");

        // ---- ①b 运行时切换：建完控件再 Apply 一次（真实路径是靠 ModernTheme.Changed 通知）----
        // 这一条能抓住「只在构造函数里取色、不订阅主题」的控件 —— 2026-09-27 的 ModernInput
        // 内层 TextBox 就是这么坏的：结构探针全绿，画面上一块白。
        string toDark = RunUiProbe(launcher, "--ui-probe", "--light --then-dark");
        Check(toDark != null, "主题：--ui-probe 支持建完再切（--light --then-dark）");
        if (toDark != null)
        {
            var toDarkRoot = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(toDark);
            Check(Convert.ToBoolean(toDarkRoot["dark"]), "主题：切到深色后 IsDark == true");
            List<string> stayLight = WrongSideBackgrounds(toDark, true);
            Check(stayLight.Count == 0, "主题：运行时切到深色后没有控件留在浅色"
                + (stayLight.Count > 0 ? "（" + string.Join("、", stayLight.ToArray()) + "）" : ""));
            Check(Luminance(Convert.ToString(toDarkRoot["formBack"])) < 0.3,
                "主题：运行时切到深色后窗体自身也变深（" + toDarkRoot["formBack"] + "）");
        }
        string toLight = RunUiProbe(launcher, "--ui-probe", "--dark --then-light");
        if (toLight != null)
        {
            var toLightRoot = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(toLight);
            Check(!Convert.ToBoolean(toLightRoot["dark"]), "主题：切回浅色后 IsDark == false");
            List<string> stayDark = WrongSideBackgrounds(toLight, false);
            Check(stayDark.Count == 0, "主题：运行时切回浅色后没有控件留在深色"
                + (stayDark.Count > 0 ? "（" + string.Join("、", stayDark.ToArray()) + "）" : ""));
            Check(Luminance(Convert.ToString(toLightRoot["formBack"])) > 0.7,
                "主题：运行时切回浅色后窗体自身也变浅（" + toLightRoot["formBack"] + "）");
        }

        // ---- ② 设置对话框（用户就是在那里切深色）----
        string setDark = RunUiProbe(launcher, "--settings-probe", "--dark");
        string setLight = RunUiProbe(launcher, "--settings-probe", "--light");
        Check(setDark != null && setLight != null, "主题：--settings-probe 能导出「弹幕与 SC 设置」的控件树");
        if (setDark == null || setLight == null) return;
        var setDarkRoot = new System.Web.Script.Serialization.JavaScriptSerializer()
            .Deserialize<Dictionary<string, object>>(setDark);
        Check(Convert.ToBoolean(setDarkRoot["dark"]), "主题：设置对话框 --dark 生效");
        List<string> setBad = WrongSideBackgrounds(setDark, true);
        Check(setBad.Count == 0, "主题：设置对话框在深色下也没有亮底控件"
            + (setBad.Count > 0 ? "（" + string.Join("、", setBad.ToArray()) + "）" : ""));
        List<string> setLightBad = WrongSideBackgrounds(setLight, false);
        Check(setLightBad.Count == 0, "主题：设置对话框在浅色下也没有深底控件"
            + (setLightBad.Count > 0 ? "（" + string.Join("、", setLightBad.ToArray()) + "）" : ""));
        // 对话框里勾上「深色模式」= 建完再 Apply（同一个 Changed 事件）
        string setToggle = RunUiProbe(launcher, "--settings-probe", "--light --then-dark");
        Check(setToggle != null, "主题：设置对话框支持运行时切换探针");
        if (setToggle != null)
        {
            List<string> setStayLight = WrongSideBackgrounds(setToggle, true);
            Check(setStayLight.Count == 0, "主题：设置对话框里勾上深色后当场全部变深"
                + (setStayLight.Count > 0 ? "（" + string.Join("、", setStayLight.ToArray()) + "）" : ""));
        }
    }
}