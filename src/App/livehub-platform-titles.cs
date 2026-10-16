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
    private sealed class PlaybackTitleInfo
    {
        public string FullTitle;
        public string RoomTitle;
    }

    private static PlaybackTitleInfo CreatePlaybackTitleInfo(string fullTitle, string roomTitle)
    {
        return new PlaybackTitleInfo { FullTitle = fullTitle, RoomTitle = string.IsNullOrWhiteSpace(roomTitle) ? fullTitle : roomTitle };
    }
    // ---------------- 播放计划 ----------------
    // 播放计划与启动三层（ResolvePlan / Launch / BuildStart）以及 mpv/PotPlayer 参数拼装、
    // FLV 元数据探测：2026-09-27 搬到 src\App\livehub-playback.cs（设计说明也搬过去了）。


    // 斗鱼：betard 接口给房间名和主播名（streamlink 插件同款）
    private static string FetchDouyuTitle(string room)
    {
        return FetchDouyuPlaybackTitle(room).FullTitle;
    }

    private static PlaybackTitleInfo FetchDouyuPlaybackTitle(string room)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://www.douyu.com/betard/" + room);
            request.Timeout = 4000;
            request.ReadWriteTimeout = 4000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return ParseDouyuPlaybackTitle(reader.ReadToEnd(), "斗鱼直播 " + room);
            }
        }
        catch (Exception) { }
        return CreatePlaybackTitleInfo("斗鱼直播 " + room, null);
    }

    private static PlaybackTitleInfo ParseDouyuPlaybackTitle(string json, string fallback)
    {
        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        object roomValue;
        var roomData = root != null && root.TryGetValue("room", out roomValue) ? roomValue as Dictionary<string, object> : null;
        if (roomData == null) return CreatePlaybackTitleInfo(fallback, null);
        string nick = Field(roomData, "nickname"), name = Field(roomData, "room_name");
        string full = nick != null && name != null ? nick + " - " + name : nick ?? name ?? fallback;
        return CreatePlaybackTitleInfo(full, name);
    }

    // B站：房间标题（主播名由 FetchBiliAnchor 提供）
    private static string FetchBiliRoomTitle(string room)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + room);
            request.Timeout = 4000;
            request.ReadWriteTimeout = 4000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                object dataValue;
                var data = root != null && root.TryGetValue("data", out dataValue) ? dataValue as Dictionary<string, object> : null;
                return Field(data, "title");
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- 斗鱼专用本地中转 ----------------
    // 中转本体（StartRelay / RunRelay / Pump / FLV 工具 / 房间状态 / 直链解析）
    // 2026-09-27 搬到 src\App\livehub-relay.cs；设计说明也一并搬过去了，见那个文件开头。


}
