// --------------------------------------------------------------------------------------------
// 音画同步基线 + 中继时间线计数的只读展示（2026-11-06，双版本同步路线）
//
// 为什么单独一块：
//   · 同步参数由 livehub-playback.cs 拼进 mpv 命令行；界面若自己再抄一份就会漂移，
//     所以展示文案直接调用同一处 PlaybackSyncSummary()。
//   · 中继是独立子进程，时间线计数（跳变/丢弃代际尾包/重连）在启动器内存里看不到；
//     中继在 RuntimeDir 另写一份 .relaydiag 文本，这里只读解析。状态文件 .relay.txt
//     的格式不变（live-title.lua 仍按原协议读）。
//   · 「标记音画异常」只写最小本地诊断：时间、会话、播放器、同步基线、计数。
//     绝不写签名直链/查询参数/Cookie/请求头。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

internal static partial class LiveHub
{
    /// 会话语义的中继诊断文件路径（与中转进程 PublishRelayDiag 的落盘规则一致）。
    private static string SessionRelayDiagPath(Target target)
    {
        return target.SessionId == null
            ? Path.Combine(RuntimeDir, target.Platform + "-" + target.Room + ".relaydiag")
            : Path.Combine(RuntimeDir, "session-" + target.SessionId + ".relaydiag");
    }

    private static string ReadRelayDiagText(Target target)
    {
        try
        {
            if (RuntimeDir == null) return null;
            string path = SessionRelayDiagPath(target);
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) { Diagnostics.Report("读取中继诊断", ex); return null; }
    }

    /// 纯解析：key=value 文本 → 字典。坏行/空行跳过，不做任何外推。
    internal static Dictionary<string, string> ParseRelayDiag(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return values;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line.Substring(0, equals).Trim();
            if (key.Length == 0 || key.Length > 64) continue;
            values[key] = line.Substring(equals + 1).Trim();
        }
        return values;
    }

    private static long DiagLong(Dictionary<string, string> values, string key)
    {
        string text;
        long number;
        if (values == null || !values.TryGetValue(key, out text)) return 0;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static string DiagText(Dictionary<string, string> values, string key)
    {
        string text;
        return values != null && values.TryGetValue(key, out text) && text.Length > 0 ? text : "unknown";
    }

    /// 给界面的一行摘要；没有诊断文件/坏文件时给出明确文案，而不是假装计数为 0。
    internal static string RelayDiagSummary(string text)
    {
        Dictionary<string, string> values = ParseRelayDiag(text);
        if (values.Count == 0) return "暂无计数（会话未使用本地中继，或中继尚未产生媒体数据）";
        string state = DiagText(values, "state");
        if (state == "OFFLINE") state = "已下播等待";
        else if (state == "RECONNECTING") state = "重连中";
        else if (state == "ONLINE") state = "转发中";
        else if (state == "REPLAY-UNAVAILABLE") state = "重播流暂不可用";
        string updated;
        string time = "时间未知";
        DateTime parsed;
        if (values.TryGetValue("updated", out updated) && !string.IsNullOrEmpty(updated)
            && DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            time = parsed.ToLocalTime().ToString("HH:mm:ss");
        long bytes = DiagLong(values, "bytes");
        return state + " · 重连 " + DiagLong(values, "reconnects") + " 次"
            + " · 时间线修正 " + DiagLong(values, "corrections") + " 次"
            + "（跨代际跳变 " + DiagLong(values, "jumps") + " 次）"
            + " · 丢弃旧代际尾包 " + DiagLong(values, "discarded_epoch_tail") + " 个"
            + " · 丢弃重连重叠 " + DiagLong(values, "dropped_tags") + " 个"
            + " · 已转发 " + (bytes / 1024 / 1024) + " MB（更新于 " + time + "）";
    }

    // ---------------------------------------------------------------- 音画异常标记
    private static string SyncMarkLogPath()
    {
        return Path.Combine(LogsDirectory(), "avsync-mark.log");
    }

    /// 「标记音画异常」：写最小本地诊断，返回是否写入成功；文案与日志都不含 URL/Cookie。
    private static bool TryMarkAvSyncIssue(PlaybackSession session, out string message)
    {
        message = null;
        if (session == null || session.Target == null) { message = "没有选中会话。"; return false; }
        try
        {
            Dictionary<string, string> diag = ParseRelayDiag(ReadRelayDiagText(session.Target));
            StringBuilder line = new StringBuilder();
            line.Append("[音画异常标记] session=").Append(session.Target.SessionId == null ? "(未记录)" : session.Target.SessionId);
            line.Append(" platform=").Append(session.Target.Platform);
            line.Append(" room=").Append(session.Target.Room);
            line.Append(" player=").Append(session.Player == "potplayer" ? "potplayer" : "mpv");
            line.Append(" sync=").Append(SafeLogField(PlaybackSyncSummary(session.Player, session.Target)));
            line.Append(" relay=");
            if (diag.Count == 0) line.Append("none");
            else
            {
                line.Append("state:").Append(DiagText(diag, "state"));
                line.Append(",reconnects:").Append(DiagLong(diag, "reconnects"));
                line.Append(",corrections:").Append(DiagLong(diag, "corrections"));
                line.Append(",jumps:").Append(DiagLong(diag, "jumps"));
                line.Append(",discarded_epoch_tail:").Append(DiagLong(diag, "discarded_epoch_tail"));
                line.Append(",dropped_tags:").Append(DiagLong(diag, "dropped_tags"));
                line.Append(",bytes:").Append(DiagLong(diag, "bytes"));
            }
            bool written = BoundedLog.TryAppend(SyncMarkLogPath(), line.ToString());
            if (!written)
            {
                Diagnostics.ReportLogBroken(null);
                message = "音画异常标记写入失败：本地日志不可写。";
                return false;
            }
            message = "已记录音画异常标记（时间、会话、同步基线与中继计数）。";
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Report("音画异常标记", ex);
            message = "音画异常标记失败：" + ex.GetType().Name;
            return false;
        }
    }

    // 日志单行化：换行/制表符不能把一条诊断拆成多行。
    private static string SafeLogField(string text)
    {
        if (text == null) return "";
        StringBuilder safe = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            safe.Append(c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
        }
        return safe.ToString();
    }

    private sealed partial class LauncherUi
    {
        /// 选中会话的同步基线 + 中继计数（只读）。每 2 秒的 RefreshSessions 会顺带刷新。
        private void UpdateSessionSyncDiagnostics()
        {
            if (workbenchSessionSync == null) return;
            PlaybackSession session = SelectedPlaybackSession();
            if (avSyncMark != null) avSyncMark.Enabled = !closingSession && session != null && !session.Ended;
            if (session == null)
            {
                workbenchSessionSync.Text = "选择会话后显示：mpv 同步参数（video-sync/framedrop/autosync/audio-delay/缓存档）与中继时间线计数。";
                return;
            }
            string sync = PlaybackSyncSummary(session.Player, session.Target);
            string relay = RelayDiagSummary(ReadRelayDiagText(session.Target));
            workbenchSessionSync.Text = "同步基线：" + sync + "\r\n中继：" + relay;
        }

        private void MarkSelectedAvSyncIssue()
        {
            PlaybackSession session = SelectedPlaybackSession();
            string message;
            bool okay = TryMarkAvSyncIssue(session, out message);
            status.ForeColor = okay ? ModernTheme.Success : ModernTheme.Danger;
            status.Text = message;
        }
    }
}
