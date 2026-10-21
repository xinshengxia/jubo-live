// --------------------------------------------------------------------------------------------
// 启动器界面的**结构回归**（`--ui-probe` 导出真实控件树，离线可跑）。
//
// 为什么需要它（评估报告 §7 那 824 行 ShowDialog 的前置条件）：
//   · `tests\Test-LauncherGui.ps1` 走 UIA，验的是**渲染出来**的窗口 —— 只能在有桌面会话时跑，
//     而且断言偏「控件在不在」；
//   · 要动 ShowDialog 的代码，需要一条**离线、可重复**的等价性判据：拆分前后控件树必须逐字段一致。
//     这条就是那个判据（同时锁住「同行控件不重叠」「最小尺寸放得下底排按钮」两类真实踩过的坑）。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private sealed class UiControl
    {
        public string Type, Parent, Text;
        public int X, Y, W, H, Depth;
        public bool Enabled;
    }

    /// <summary>跑一次 --ui-probe，把控件树读回来（失败返回 null）。</summary>
    private static List<UiControl> ProbeLauncherUi(string launcher, out string rawJson, out int clientWidth, out int clientHeight, out int minWidth, string fixture)
    {
        rawJson = null; clientWidth = 0; clientHeight = 0; minWidth = 0;
        string json = Path.Combine(Path.GetTempPath(), "livehub-ui-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            int code = RunLauncherExitCode(launcher, "--ui-probe \"" + json + "\"" + WorkbenchProbePathArguments(fixture));
            if (code != 0 || !File.Exists(json)) return null;
            rawJson = File.ReadAllText(json, Encoding.UTF8);
            var root = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(rawJson);
            if (root == null) return null;
            clientWidth = Convert.ToInt32(root["clientWidth"]);
            clientHeight = Convert.ToInt32(root["clientHeight"]);
            minWidth = Convert.ToInt32(root["minimumWidth"]);
            var list = root["controls"] as ArrayList;
            if (list == null) return null;
            var result = new List<UiControl>();
            foreach (object entry in list)
            {
                var map = entry as Dictionary<string, object>;
                if (map == null) continue;
                var control = new UiControl();
                control.Type = Convert.ToString(map["type"]);
                control.Parent = Convert.ToString(map["parent"]);
                control.Text = Convert.ToString(map["text"]);
                control.X = Convert.ToInt32(map["x"]);
                control.Y = Convert.ToInt32(map["y"]);
                control.W = Convert.ToInt32(map["w"]);
                control.H = Convert.ToInt32(map["h"]);
                control.Enabled = Convert.ToBoolean(map["enabled"]);
                control.Depth = Convert.ToInt32(map["depth"]);
                result.Add(control);
            }
            return result;
        }
        finally { try { File.Delete(json); } catch (Exception) { } }
    }

    private static string[] RowTexts(List<UiControl> controls)
    {
        var texts = new List<string>();
        foreach (UiControl control in controls) texts.Add(control.Text);
        return texts.ToArray();
    }

    private static bool HasText(List<UiControl> controls, string needle)
    {
        foreach (UiControl control in controls)
            if (control.Text != null && control.Text.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static void TestLauncherUiStructure(string launcher)
    {
        string directory = Path.Combine(Path.GetTempPath(), "livehub-workbench-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "path-config.json"), "{}");
        try
        {
            string json; int clientWidth, clientHeight, minWidth;
            List<UiControl> controls = ProbeLauncherUi(launcher, out json, out clientWidth, out clientHeight, out minWidth, directory);
            Check(controls != null, "启动器界面：--ui-probe 在不弹窗的情况下导出控件树");
            if (controls == null) return;
            Check(controls.Count >= 25, "启动器界面：真实控件树规模正常，非空转");
            string[] required = {
                "B站", "虎牙", "斗鱼", "抖音", "画质：", "mpv 缓冲：", "弹幕：", "SC：",
                "取消启动", "最近播放", "收藏", "正在观看", "用 mpv 打开", "用 PotPlayer 打开",
                "重新打开选中房间", "弹幕设置…", "查找主播", "格式说明", "本地数据", "播放器路径", "选择程序…"
            };
            var missing = new List<string>();
            foreach (string need in required) if (!HasText(controls, need)) missing.Add(need);
            Check(missing.Count == 0, "启动器界面：原有操作仍有对应控件" + (missing.Count == 0 ? "" : " 缺失 " + string.Join("、", missing.ToArray())));
            Check(minWidth > 0 && minWidth <= clientWidth, "启动器界面：最小宽度不超过默认宽度");
            var payload = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            int sessions = 0;
            foreach (Dictionary<string, object> row in (ArrayList)payload["controls"])
                if (row.ContainsKey("name") && Convert.ToString(row["name"]) == "workbenchSessions")
                {
                    sessions++;
                    Check(row.ContainsKey("columnWidths") && ((ArrayList)row["columnWidths"]).Count == 5,
                        "观看列表：只锁定会话表的五个真实字段，不混淆本地数据列表");
                }
            Check(sessions == 1, "启动器界面：探针识别唯一会话表");

            // A multi-page interface must be checked one active page at a time;
            // hidden pages intentionally share the same content rectangle.
            WithWorkbenchForm(-1, delegate(System.Windows.Forms.Form form)
            {
                foreach (string key in WorkbenchPageKeys)
                {
                    WorkbenchShowPage(form, key);
                    WorkbenchCheckViewport(form, 96);
                }
            });
            string again; int width2, height2, minimum2;
            List<UiControl> second = ProbeLauncherUi(launcher, out again, out width2, out height2, out minimum2, directory);
            Check(second != null && again == json, "启动器界面：同一合成数据夹具的控件树可重复");
        }
        finally
        {
            string absolute = Path.GetFullPath(directory), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(absolute).StartsWith("livehub-workbench-probe-", StringComparison.Ordinal)
                && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
