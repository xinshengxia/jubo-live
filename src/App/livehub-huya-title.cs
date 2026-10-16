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
    private static string FetchHuyaTitle(string room)
    {
        return FetchHuyaPlaybackTitle(room).FullTitle;
    }

    private static PlaybackTitleInfo FetchHuyaPlaybackTitle(string room)
    {
        string fallback = "虎牙直播 " + room;
        string pageRoomTitle = null;
        PlaybackTitleInfo info = ResolveHuyaPlaybackTitle(
            delegate { return FetchHuyaProfileJson(room); },
            delegate {
                PlaybackTitleInfo page = HuyaPlaybackTitleFromPage(room, fallback);
                pageRoomTitle = page.RoomTitle;
                return page.FullTitle;
            }, fallback);
        if (pageRoomTitle != null) info.RoomTitle = pageRoomTitle;
        return info;
    }

    private static string FetchHuyaProfileJson(string room)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
            "https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid=" + room);
        request.Timeout = 6000;
        request.ReadWriteTimeout = 6000;
        request.Proxy = null;
        request.UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X)";
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static string ResolveHuyaTitle(Func<string> fetchProfileJson, Func<string> fetchPageTitle, string fallback)
    {
        return ResolveHuyaPlaybackTitle(fetchProfileJson, fetchPageTitle, fallback).FullTitle;
    }

    private static PlaybackTitleInfo ResolveHuyaPlaybackTitle(Func<string> fetchProfileJson, Func<string> fetchPageTitle, string fallback)
    {
        try
        {
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(fetchProfileJson());
            string nick = FindString(root, "nick", 0);
            string name = FindString(root, "roomName", 0);
            if (string.IsNullOrEmpty(name)) name = FindString(root, "introduction", 0);
            string title = JoinTitle(nick, name, null);
            if (!string.IsNullOrEmpty(title)) return CreatePlaybackTitleInfo(title, name);
        }
        catch (Exception ex)
        {
            Diagnostics.Report("虎牙标题接口", ex);
        }
        // 短号会收到 HTTP 200 + JSON status=422；没有有效标题同样需要网页兜底。
        try
        {
            string title = fetchPageTitle();
            return CreatePlaybackTitleInfo(string.IsNullOrWhiteSpace(title) ? fallback : title, null);
        }
        catch (Exception)
        {
            return CreatePlaybackTitleInfo(fallback, null);
        }
    }

    // 兜底：短号房间（如 a16789）在手机端接口上会回 422，改从网页里取昵称/房间名（同样有）
    private static string HuyaTitleFromPage(string room, string fallback)
    {
        return HuyaPlaybackTitleFromPage(room, fallback).FullTitle;
    }

    private static PlaybackTitleInfo HuyaPlaybackTitleFromPage(string room, string fallback)
    {
        try
        {
            string html = HttpGetText("https://www.huya.com/" + room, "https://www.huya.com/");
            return ParseHuyaPlaybackTitlePage(html, fallback);
        }
        catch (Exception)
        {
            return CreatePlaybackTitleInfo(fallback, null);
        }
    }

    private static PlaybackTitleInfo ParseHuyaPlaybackTitlePage(string html, string fallback)
    {
        if (html == null) return CreatePlaybackTitleInfo(fallback, null);
        string nick = Regex.Match(html, "\"nick\":\"([^\"]{1,40})\"").Groups[1].Value;
        string name = Regex.Match(html, "\"roomName\":\"([^\"]{1,80})\"").Groups[1].Value;
        if (name.Length == 0) name = Regex.Match(html, "\"introduction\":\"([^\"]{1,80})\"").Groups[1].Value;
        return CreatePlaybackTitleInfo(JoinTitle(nick.Length > 0 ? nick : null, name.Length > 0 ? name : null, fallback), name);
    }

    // 在反序列化出来的 JSON 树里递归找某个字符串字段（接口结构变过几次，递归找最稳）
    private static string FindString(object node, string key, int depth)
    {
        if (node == null || depth > 6) return null;
        var map = node as Dictionary<string, object>;
        if (map != null)
        {
            object value;
            if (map.TryGetValue(key, out value) && value is string)
            {
                string text = ((string)value).Trim();
                if (text.Length > 0) return text;
            }
            foreach (KeyValuePair<string, object> pair in map)
            {
                string found = FindString(pair.Value, key, depth + 1);
                if (found != null) return found;
            }
            return null;
        }
        object[] array = AsArray(node);
        if (array != null)
        {
            foreach (object item in array)
            {
                string found = FindString(item, key, depth + 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    // B站扫码登录 + cookie 落盘 + ACL 收紧 + zxing 校验：2026-09-27 搬到 src\App\livehub-credentials.cs


    /// 平台显示名。2026-09-27 起从这里查适配器表（评估报告 §7），不再是一串三元表达式 ——
    /// 而且**不认识的 id 不会被猜成 B站**（原实现最后那个 else 就是这个毛病）。
    /// <summary>记事本的可执行文件绝对路径；本机没有就返回 null。</summary>
    /// 用绝对路径取，**不查系统关联** —— 见 OpenInEditor 的说明。
}
