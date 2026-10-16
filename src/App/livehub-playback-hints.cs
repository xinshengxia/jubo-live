using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 从主 partial 原样搬运；仍在同一 net48 编译单元。
internal static partial class LiveHub
{
    private static string FetchBiliAnchor(string room)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(
                "https://api.live.bilibili.com/live_user/v1/UserInfo/get_anchor_in_room?roomid=" + room);
            request.Timeout = 3000;
            request.ReadWriteTimeout = 3000;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                object data, info, uname;
                var dataMap = root != null && root.TryGetValue("data", out data) ? data as Dictionary<string, object> : null;
                var infoMap = dataMap != null && dataMap.TryGetValue("info", out info) ? info as Dictionary<string, object> : null;
                if (infoMap == null || !infoMap.TryGetValue("uname", out uname) || uname == null) return null;
                string name = uname.ToString().Trim();
                return name.Length == 0 ? null : name;
            }
        }
        catch (Exception ex)
        {
            LastAnchorError = ex.Message;
            return null;
        }
    }

    // 主播名写进 runtime 目录由标题脚本读（命令行传中文容易被逗号/转义吃掉）
    private static string WriteBaseHint(Target target, string anchor)
    {
        try
        {
            Directory.CreateDirectory(RuntimeDir);
            string path = Path.Combine(RuntimeDir, target.Platform + "-" + target.Room + ".anchor.txt");
            if (anchor == null) return null;
            AtomicWriteAllText(path, anchor);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BitrateHintPath(Target target)
    {
        return Path.Combine(RuntimeDir, target.Platform + "-" + target.Room + ".bitrate.txt");
    }

    private static void AddMpvMediaTitle(LaunchPlan plan, string title, bool allowSideEffects)
    {
        if (!allowSideEffects) return;
        try
        {
            if (string.IsNullOrWhiteSpace(title)) title = PlatformName(plan.Target.Platform) + "直播 " + plan.Target.Room;
            string path = Path.Combine(RuntimeDir, "session-" + plan.Target.SessionId + ".media-title.txt");
            AtomicWriteAllText(path, Regex.Replace(title, @"[\x00-\x1F\x7F]", " ").Trim());
            plan.Arguments += " " + QuoteWindowsArg("--script-opts-append=live_title_media_file=" + path.Replace('\\', '/'));
        }
        catch (Exception ex) { Diagnostics.Report("直播标题写入", ex); }
    }

    // 斗鱼经本地中转播时 mpv 拿不到 demux-bitrate，这里把流开头的 FLV 元数据里的码率写进提示文件，
    // 给 live-title.lua 拼标题用（虎牙/B站 的流自带 demux-bitrate，不需要）。
    private static void WriteBitrateHint(Target target)
    {
        WriteBitrateHint(target, ResolveDouyuUrl(target.Room, target.Quality));
    }

    private static void WriteBitrateHint(Target target, string probe)
    {
        try
        {
            StreamInfo info = probe == null ? null : ReadFlvInfo(probe);
            if (info == null || info.Kbps <= 0) return;
            AtomicWriteAllText(BitrateHintPath(target), info.Kbps.ToString());
        }
        catch (Exception) { }
    }

}
