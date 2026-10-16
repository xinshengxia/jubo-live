using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

// 观测而非联网探测。磁盘中固定 4×3 个位置，使子进程的结果可供启动器读取。
internal static class PlatformHealth
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, string> Last = new Dictionary<string, string>();
    private static readonly Dictionary<string, DateTime> Written = new Dictionary<string, DateTime>();
    private static readonly Dictionary<string, string> Rooms = new Dictionary<string, string>();
    internal static void SetRoom(string platform, string room)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(room ?? "", @"^[A-Za-z0-9_-]{1,128}$"))
            lock (Gate) { Rooms[Path.Combine(LiveHub.RuntimeDir ?? ".", platform)] = room; }
    }
    internal static void RecordRoomState(string platform, string room, RoomStateKind state)
    {
        SetRoom(platform, room);
        Record(platform, "room", state == RoomStateKind.Live ? "live"
            : state == RoomStateKind.Offline ? "offline"
            : state == RoomStateKind.Replay ? "replay" : "unknown");
    }
    internal static void Record(string platform, string stage, string state)
    {
        if (LiveHub.Platforms.Get(platform) == null || (stage != "room" && stage != "playback" && stage != "danmaku" && stage != "login")) return;
        if (Array.IndexOf(new string[] { "live", "replay", "unknown", "ready", "connecting", "retrying", "offline", "parse-error", "timeout", "network-error", "auth-required", "saved-unverified", "save-failed", "logged-out", "cleanup-failed", "stopped", "error" }, state) < 0) return;
        lock (Gate)
        {
            string key = Path.Combine(LiveHub.RuntimeDir ?? ".", "health-" + platform + "-" + stage + ".state.txt");
            string old, room; DateTime previous;
            Rooms.TryGetValue(Path.Combine(LiveHub.RuntimeDir ?? ".", platform), out room);
            string signature = state + "|" + room;
            if (Last.TryGetValue(key, out old) && old == signature && Written.TryGetValue(key, out previous)
                && (DateTime.UtcNow - previous).TotalSeconds < 30) return;
            try
            {
                LiveHub.AtomicWriteAllText(key, DateTime.UtcNow.Ticks + "|" + state + (string.IsNullOrEmpty(room) ? "" : "|" + room));
                Last[key] = signature; Written[key] = DateTime.UtcNow;
            }
            catch (Exception ex) { Diagnostics.Report("平台状态保存", ex); }
        }
    }

    internal static string Classify(Exception exception)
    {
        while (exception is AggregateException && exception.InnerException != null) exception = exception.InnerException;
        WebException web = exception as WebException;
        if (web != null)
        {
            HttpWebResponse response = web.Response as HttpWebResponse;
            if (response != null && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)) return "auth-required";
            return web.Status == WebExceptionStatus.Timeout ? "timeout" : "network-error";
        }
        if (exception is System.Security.Authentication.AuthenticationException) return "auth-required";
        if (exception is TimeoutException) return "timeout";
        if (exception is InvalidDataException || exception is FormatException) return "parse-error";
        if (exception is System.Net.Sockets.SocketException || exception is System.Net.WebSockets.WebSocketException) return "network-error";
        return "error";
    }

    internal static void ResolutionResult(string platform, string url, bool offline)
    {
        if (url != null || offline) { Record(platform, "playback", url != null ? "ready" : "offline"); return; }
        lock (Gate)
        {
            string key = Path.Combine(LiveHub.RuntimeDir ?? ".", "health-" + platform + "-playback.state.txt");
            string previous;
            if (!Last.TryGetValue(key, out previous) || previous.StartsWith("connecting|")) Record(platform, "playback", "parse-error");
        }
    }

    // 不保留正文前缀、URL、标头或任意远端字段，避免把登录态写进日志。
    internal static string ResponseSummary(string response)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(response ?? "");
        using (SHA256 hash = SHA256.Create())
            return "bytes=" + bytes.Length + " sha256=" + BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static string Describe()
    {
        StringBuilder text = new StringBuilder("房间状态及播放 / 弹幕的最近事件（各平台保留最近一条，不能代替 Cookie 登录检查）：\r\n");
        foreach (LiveHub.IPlatformAdapter platform in LiveHub.Platforms.All)
        {
            text.Append(platform.DisplayName);
            foreach (string stage in new string[] { "room", "playback", "danmaku", "login" })
            {
                string state = "尚无记录";
                try
                {
                    string file = Path.Combine(LiveHub.RuntimeDir, "health-" + platform.Id + "-" + stage + ".state.txt");
                    if (File.Exists(file))
                    {
                        string[] parts = File.ReadAllText(file).Split('|'); long ticks;
                        if (parts.Length >= 2 && parts.Length <= 3 && long.TryParse(parts[0], out ticks))
                        {
                            DateTime time = new DateTime(ticks, DateTimeKind.Utc);
                            state = StateText(parts[1], stage, platform.Id) + " · " + time.ToLocalTime().ToString("MM-dd HH:mm:ss");
                            if (parts.Length == 3 && parts[2].Length > 0) state += " · 房间 " + parts[2];
                            if (DateTime.UtcNow - time > TimeSpan.FromMinutes(5) && (parts[1] == "live" || parts[1] == "replay" || parts[1] == "ready" || parts[1] == "connecting" || parts[1] == "retrying"))
                                state += "（记录超过 5 分钟，待重新观测；不表示 Cookie 过期）";
                        }
                    }
                }
                catch (Exception) { state = "观测记录不可读"; }
                text.Append("\r\n  ").Append(stage == "room" ? "房间状态" : stage == "playback" ? "播放解析" : stage == "danmaku" ? "弹幕连接" : "历史登录事件").Append(": ").Append(state);
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string StateText(string state, string stage, string platform)
    {
        switch (state)
        {
            case "live": return "正在直播";
            case "replay": return LiveHub.RoomStateLabel(platform, RoomStateKind.Replay);
            case "unknown": return "本次未查到状态";
            case "ready": return stage == "playback" ? "曾取得播放地址" : stage == "danmaku" ? "曾连接 / 收到弹幕" : "曾确认登录";
            case "connecting": return "解析 / 连接中";
            case "retrying": return "正在重试";
            case "offline": return stage == "room" ? "房间未开播" : "房间未开播";
            case "stopped": return "连接已停止";
            case "saved-unverified": return "凭据已保存，待验证";
            case "save-failed": return "凭据保存失败";
            case "logged-out": return "已执行退出登录";
            case "cleanup-failed": return "凭据清理未完成";
            case "auth-required": return "认证 / 权限受阻，可能需网页验证";
            case "timeout": return "请求超时";
            case "network-error": return "网络异常";
            case "parse-error": return "响应解析失败";
            default: return "其他异常";
        }
    }
}

internal static partial class LiveHub
{
    private static string ParserFailure(string platform, string response)
    {
        PlatformHealth.Record(platform, "playback", "parse-error");
        if (!BoundedLog.TryAppend(Path.Combine(RuntimeDir, "logs", platform + "-parser.log"),
            PlatformHealth.ResponseSummary(response))) Diagnostics.ReportLogBroken(null);
        return null;
    }
}
