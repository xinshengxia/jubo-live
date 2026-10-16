// --------------------------------------------------------------------------------------------
// 静默失败的登记处（2026-09-25，评估报告 §5.9）
//
// 报告的原话：空 catch 对**清理和 UI 收尾**是合理的，但在**配置保存、Cookie 保存、
// 网络解析、日志写入**处会把真实失败伪装成成功。应建立错误分类。
//
// 所以本项目的约定是三类，别再一律 `catch (Exception) { }`：
//
//   ① 清理/收尾类（Dispose / Kill / Delete / Close / Unregister）
//        → **允许静默**。失败没有信息量，也没人能处理（进程都要退了）。
//          写法就用 `catch (Exception) { }`，不需要来这里登记。
//
//   ② 日志类（写日志文件本身）
//        → **不能抛**（日志绝不能拖垮主流程），但首次失败必须**记一笔**，
//          否则「出问题时日志里什么都没有」这件事本身没有任何线索。
//          写法：`catch (Exception ex) { Diagnostics.Report("日志", ex); }`
//
//   ③ 数据持久化类（配置 / 凭据 / 会话状态）
//        → **必须让调用方知道**：返回 bool，或者抛出去。
//          绝不能吞掉之后还让上层显示「已保存」—— 那是在骗用户。
//
// 登记处本身也遵守 ②：它写日志失败时只能认了（否则递归）。
// --------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

/// 构建信息（由 Build-Hardened.ps1 / Build-Portable.ps1 注入 build-info.cs 生成的 AssemblyInformationalVersion）。
/// **界面上要能看到** —— 只写进文件属性的话，用户报问题时还得去右键属性里翻。
internal static class BuildInfo
{
    private static string _text;

    /// 形如 `0.0.0+1c3ff28 (2026-09-25 21:38)`；没注入信息时返回 `未知版本`。
    public static string Text
    {
        get
        {
            if (_text != null) return _text;
            try
            {
                object[] attrs = typeof(BuildInfo).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs != null && attrs.Length > 0)
                    _text = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(_text)) _text = "未知版本";
            return _text;
        }
    }

    /// 短形式：只取 commit 那段（`1c3ff28`），给标题栏用。
    public static string Short
    {
        get
        {
            string full = Text;
            int plus = full.IndexOf('+');
            if (plus < 0) return full;
            string rest = full.Substring(plus + 1);
            int space = rest.IndexOf(' ');
            return space < 0 ? rest : rest.Substring(0, space);
        }
    }
}

internal static class SilenceWatch
{
    /// 连续多久没收到任何帧就值得记一笔。
    ///
    /// ⚠️ 这个数字**只用来打日志**，不参与任何行为决策 —— 绝不因为「静默」就去重连。
    ///    「安静房间」和「断线」在数据层面无法区分，这正是原来 3 秒超时那个设计的错误所在。
    ///    已连接的虎牙房间即使没人说话，6111/8006 这类帧也在持续到，
    ///    所以「90 秒一帧都没有」本身就是足够强的异常信号。
    public const int ThresholdSeconds = 90;

    /// 同一段静默里最多每 5 分钟记一次，别刷屏（完全静默的连接 = 每小时 12 行）。
    public const int RepeatSeconds = 300;

    /// 该不该打这行日志。
    ///
    /// 抽成纯函数是因为这里**踩过一次坑**：`lastWarn` 一开始初始化成「现在」，
    /// 把**第一次**告警推到了 RepeatSeconds 之后（实测 200 秒的静默夹具一条日志都没有）。
    ///
    /// `lastWarn` 用 `DateTime?`、**`null` = 从没报过** —— 把「第一次该立刻报」写进类型里，
    /// 而不是靠调用方记得初始化成 `DateTime.MinValue`（那个坑就是这么来的）。
    public static bool ShouldWarn(int silentMs, DateTime? lastWarn, DateTime now)
    {
        if (silentMs < ThresholdSeconds * 1000) return false;
        if (lastWarn == null) return true;                 // 从没报过 → 第一次就该报
        return (now - lastWarn.Value).TotalSeconds >= RepeatSeconds;
    }

    /// 观测文案。明确写「仅观测，不改变行为」—— 免得以后有人把它当成检测器。
    public static string Describe(string platform, int silentMs, int frames)
    {
        return platform + "：已 " + (silentMs / 1000) + " 秒没有收到任何帧"
            + "（连接仍为 Open；本次连接累计 " + frames + " 帧）—— 仅观测，不改变行为";
    }
}

internal static class Diagnostics
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

    /// 日志文件写不进去。界面该把这件事说出来 —— 否则用户看到的「没有日志」会被当成「没出问题」。
    private static bool _logBroken;

    public static bool LogBroken { get { lock (Gate) { return _logBroken; } } }

    /// 记一次被吞掉的失败。
    /// **只记第一次的异常文本**，后续同类别只累加计数 —— 否则反复失败会把日志刷爆
    /// （而日志本身可能正好就是坏的那个）。
    public static void Report(string area, Exception ex)
    {
        if (area == null) area = "其他";
        bool first = false;
        lock (Gate)
        {
            int n;
            Counts.TryGetValue(area, out n);
            Counts[area] = n + 1;
            first = (n == 0);
        }
        if (first)
        {
            string detail = ex == null ? "(无异常对象)" : (ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("x"));
            DanmakuHost.LogQuiet("【静默失败·" + area + "】" + detail);
        }
    }

    public static void ReportLogBroken(Exception ex)
    {
        lock (Gate) { _logBroken = true; }
    }

    /// 给界面/托盘用的一句话摘要；没有失败时返回 null。
    public static string Summary()
    {
        lock (Gate)
        {
            if (_logBroken && Counts.Count == 0) return "日志写不进去（诊断信息会丢失）";
            if (Counts.Count == 0 && !_logBroken) return null;
            var parts = new List<string>();
            if (_logBroken) parts.Add("日志不可写");
            foreach (KeyValuePair<string, int> pair in Counts)
                parts.Add(pair.Key + "×" + pair.Value);
            return "静默失败：" + string.Join("、", parts.ToArray());
        }
    }

    /// 供测试用：清空计数
    internal static void Reset()
    {
        lock (Gate) { Counts.Clear(); _logBroken = false; }
    }

    internal static int CountOf(string area)
    {
        lock (Gate) { int n; Counts.TryGetValue(area, out n); return n; }
    }
}
