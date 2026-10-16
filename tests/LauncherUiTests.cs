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
    private static List<UiControl> ProbeLauncherUi(string launcher, out string rawJson, out int clientWidth, out int clientHeight, out int minWidth)
    {
        rawJson = null; clientWidth = 0; clientHeight = 0; minWidth = 0;
        string json = Path.Combine(Path.GetTempPath(), "livehub-ui-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            int code = RunLauncherExitCode(launcher, "--ui-probe \"" + json + "\"");
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
        string json;
        int clientWidth, clientHeight, minWidth;
        List<UiControl> controls = ProbeLauncherUi(launcher, out json, out clientWidth, out clientHeight, out minWidth);
        Check(controls != null, "启动器界面：--ui-probe 能在不弹窗的情况下导出控件树");
        if (controls == null) return;

        Check(controls.Count >= 25, "启动器界面：控件树规模正常（实得 " + controls.Count + " 个控件，<25 说明这条是空转）");

        var probe = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        foreach (Dictionary<string, object> row in (ArrayList)probe["controls"])
        {
            if (!row.ContainsKey("columnWidths")) continue;
            var widths = (ArrayList)row["columnWidths"]; int used = 0;
            for (int i = 0; i < widths.Count; i++) used += Convert.ToInt32(widths[i]);
            Check(widths.Count == 5, "观看列表：有效列占用整行，尾部不留下大片空列");
            Check(Math.Abs(used - Convert.ToInt32(row["clientWidth"])) <= 6, "观看列表：列宽总和适配当前客户区，无横向溢出");
            Check(Convert.ToInt32(widths[0]) > Convert.ToInt32(widths[2])
                && Convert.ToInt32(widths[1]) >= clientWidth / 10, "观看列表：房间列留出更多空间，状态字段随窗口和DPI展开");
        }

        // ---- ① 关键控件都在（按文案认，和 GUI 冒烟同一套判据）----
        string[] required = new string[] {
            "B站", "虎牙", "斗鱼", "抖音", "房间号 / 链接：", "画质：", "mpv 缓冲：", "弹幕：", "SC：",
            "取消启动", "最近播放", "收藏", "正在观看", "用 mpv 打开", "用 PotPlayer 打开",
            "重新打开选中房间", "B站扫码", "斗鱼扫码", "虎牙扫码", "抖音扫码", "弹幕设置…", "查找主播", "格式说明", "本地数据 / 平台状态", "B站 Cookie", "虎牙 Cookie", "斗鱼 Cookie", "抖音 Cookie",
            "播放器路径", "选择程序…"
        };
        var missing = new List<string>();
        foreach (string need in required) if (!HasText(controls, need)) missing.Add(need);
        Check(missing.Count == 0, "启动器界面：关键控件齐全" + (missing.Count > 0 ? "（缺：" + string.Join("、", missing.ToArray()) + "）" : ""));

        // ---- ② 第二行（画质/缓冲/取消启动/弹幕/SC…）同行不许重叠 ----
        // 这一行全是绝对坐标 SetBounds，2026-09-25 真的压过一次（增强档位压住 SC 下拉），
        // GUI 冒烟是靠屏幕坐标查的；这里用控件树自己的相对坐标，离线也能查。
        // 行聚类：±12px 视为同一行，取「上部区域里控件最多的那一行」——
        // 不写死 y 范围（DPI/尺寸一变就白测；第一版写死 clientHeight/10 只捞到 3 个，假失败）。
        var lines = new List<List<UiControl>>();
        foreach (UiControl control in controls)
        {
            if (control.Parent != "(form)" || control.Depth != 0) continue;
            List<UiControl> bucket = null;
            foreach (List<UiControl> candidate in lines)
                if (Math.Abs(candidate[0].Y - control.Y) <= 12) { bucket = candidate; break; }
            if (bucket == null) { bucket = new List<UiControl>(); bucket.Add(control); lines.Add(bucket); }
            else bucket.Add(control);
        }
        List<UiControl> row2 = null;
        foreach (List<UiControl> bucket in lines)
        {
            if (bucket[0].Y <= clientHeight / 20 || bucket[0].Y >= clientHeight / 3) continue;   // 只要上部那几行
            if (row2 == null || bucket.Count > row2.Count) row2 = bucket;
        }
        if (row2 == null) row2 = new List<UiControl>();
        Check(row2.Count >= 6, "启动器界面：第二行（设置行）找得到 ≥6 个控件（实得 " + row2.Count + "："
            + string.Join("、", RowTexts(row2)) + "）");
        var clashes = new List<string>();
        for (int i = 0; i < row2.Count; i++)
        {
            for (int j = i + 1; j < row2.Count; j++)
            {
                UiControl a = row2[i], b = row2[j];
                int overlap = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                if (overlap > 2) clashes.Add("[" + a.Text + "]×[" + b.Text + "] 重叠 " + overlap + "px");
            }
        }
        Check(clashes.Count == 0, "启动器界面：第二行控件不重叠" + (clashes.Count > 0 ? "（" + string.Join("；", clashes.ToArray()) + "）" : ""));

        // ---- ③ 底排按钮：都在客户区内，且互相不重叠 ----
        var bottom = new List<UiControl>();
        foreach (UiControl control in controls)
        {
            if (control.Parent != "(form)" || control.Depth != 0) continue;
            if (control.Y >= clientHeight - clientHeight / 8) bottom.Add(control);   // 底部那条（写反过一次：会把上半屏全收进来）
        }
        Check(bottom.Count >= 6, "启动器界面：底排找得到 ≥6 个按钮（实得 " + bottom.Count + "）");
        var outOfBox = new List<string>();
        var bottomClash = new List<string>();
        for (int i = 0; i < bottom.Count; i++)
        {
            UiControl a = bottom[i];
            if (a.X < 0 || a.X + a.W > clientWidth) outOfBox.Add(a.Text + "(" + a.X + ".." + (a.X + a.W) + " 超出 " + clientWidth + ")");
            for (int j = i + 1; j < bottom.Count; j++)
            {
                UiControl b = bottom[j];
                int overlap = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                if (overlap > 2 && Math.Min(a.Y + a.H, b.Y + b.H) > Math.Max(a.Y, b.Y)) bottomClash.Add("[" + a.Text + "]×[" + b.Text + "] 重叠 " + overlap + "px");
            }
        }
        Check(outOfBox.Count == 0, "启动器界面：底排按钮都在客户区内" + (outOfBox.Count > 0 ? "（" + string.Join("；", outOfBox.ToArray()) + "）" : ""));
        Check(bottomClash.Count == 0, "启动器界面：底排按钮不重叠" + (bottomClash.Count > 0 ? "（" + string.Join("；", bottomClash.ToArray()) + "）" : ""));

        // ---- ③b 任何顶层控件都不许伸出客户区（2026-09-27 实测：状态标签在 2x DPI 下右边缘超了 444px）----
        var outside = new List<string>();
        foreach (UiControl control in controls)
        {
            if (control.Parent != "(form)" || control.Depth != 0) continue;
            if (control.X < 0 || control.X + control.W > clientWidth + 2)
                outside.Add(control.Text + "(" + control.X + ".." + (control.X + control.W) + ")");
        }
        Check(outside.Count == 0, "启动器界面：控件都不伸出客户区（宽 " + clientWidth + "）"
            + (outside.Count > 0 ? "（越界：" + string.Join("；", outside.ToArray()) + "）" : ""));

        // ---- ③c 默认尺寸下**纵向**也必须放得下（2026-10-16 实测踩到）----
        // 上面 ③/③b 只查了横向（X/W），纵向从没断言过 —— 于是「底排 8 个按钮下缘在 y=1174、
        // 客户区只有 1120，整排被 AutoScroll 挡在窗外」这件事一直是绿的，直到用户报「显示不全」。
        // 这条就是那个缺口的护栏：探针跑在**默认尺寸**下，所以内容必须整体落在客户区内。
        int lowest = 0; string lowestText = "";
        var vertical = new List<string>();
        foreach (UiControl control in controls)
        {
            if (control.Parent != "(form)" || control.Depth != 0) continue;
            if (control.Y + control.H > lowest) { lowest = control.Y + control.H; lowestText = control.Text; }
            if (control.Y < 0 || control.Y + control.H > clientHeight + 2)
                vertical.Add(control.Text + "(y " + control.Y + ".." + (control.Y + control.H) + ")");
        }
        Check(vertical.Count == 0, "启动器界面：默认尺寸下内容纵向完整可见（客户区高 " + clientHeight + "，最下沿 "
            + lowest + " [" + lowestText + "]"
            + (vertical.Count > 0 ? "；越界：" + string.Join("；", vertical.ToArray()) : "") + "）");

        // ---- ④ 最小宽度必须容得下底排（评估报告实测过 850 宽时按钮互相压）----
        int widest = 0;
        foreach (UiControl control in bottom) widest = Math.Max(widest, control.X + control.W);
        Check(clientWidth >= widest, "启动器界面：当前宽度容得下底排（客户区 " + clientWidth + " ≥ 最右 " + widest + "）");
        Check(minWidth > 0 && minWidth <= clientWidth, "启动器界面：最小宽度也容得下底排（minWidth " + minWidth + " ≥ 最右 " + widest + "）");

        // ---- ⑤ 探测本身要可重复（同一个 exe 连跑两次，控件树必须一模一样）----
        string json2;
        int w2, h2, m2;
        List<UiControl> again = ProbeLauncherUi(launcher, out json2, out w2, out h2, out m2);
        Check(again != null && json2 == json, "启动器界面：控件树可重复（连跑两次完全一致，否则这条回归不稳）");
    }
}
