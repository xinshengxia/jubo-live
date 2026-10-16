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
    // ---------------- 抖音 ----------------
    //
    // 取流方式（2026-09-19 实测）：房间页把房间数据内嵌在 RSC 负载里——
    //   self.__pace_f.push([1,"7:{...roomStore...streamStore...}"])</script>
    // 只要一个随机的 __ac_nonce cookie 就能拿到，**不需要 a_bogus/X-Bogus 签名、不需要登录 cookie**。
    // 加固点：
    //  1) 页面结尾有 "])</script> 和 ")]</script> 两种变体（跟请求头/灰度有关），所以不照抄任何单一正则，
    //     改为线性扫描所有 push 负载再按 JSON 解析——顺带避免正则回溯；
    //  2) 响应限时（8s，读也是 8s）+ 限量（4 MB，实测页面约 1.1 MB），不让远端把内存拉爆；
    //  3) __ac_nonce 用加密随机数生成，不用可预测的 Random；
    //  4) 解析出来的直链只接受 http/https 普通 URL，别让中转被页面内容牵着去连任意目标；
    //  5) 任何字段缺失/类型不符都返回 null，由上层给出人话错误，不抛到 UI 之外。
    private const string DouyinPagePrefix = "https://live.douyin.com/";
    private const string DouyinReferer = "https://live.douyin.com/";
    private const int DouyinMaxPageBytes = 4 * 1024 * 1024;
    private const string DouyinUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0";

    internal sealed class DouyinRoom
    {
        public int Status;                 // 2 = 直播中
        public string RoomId;
        public string Title;
        public string Owner;
        public string OwnerId;
        public string OwnerSecUid;
        public string OwnerWebRid;
        // 仅供本次弹幕握手使用，不落盘。
        public string UserUniqueId;
        public string Ttwid;
        public Dictionary<string, string> FlvUrls = new Dictionary<string, string>();
    }

    private static string DouyinNonce()
    {
        byte[] bytes = new byte[11];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        StringBuilder sb = new StringBuilder(22);
        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString().Substring(0, 21);
    }

    // 线性扫描 self.__pace_f.push([<数字>,"<JSON 字符串>"，返回字符串字面量（含引号）。
    // 不依赖结尾是 "]) 还是 ")]，也不怕负载里有转义引号。
    private static List<string> ExtractPacePayloads(string html)
    {
        var payloads = new List<string>();
        if (string.IsNullOrEmpty(html)) return payloads;
        const string marker = "self.__pace_f.push([";
        int index = 0;
        while (index < html.Length)
        {
            index = html.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0) break;
            int cursor = index + marker.Length;
            int quote = html.IndexOf('"', cursor);
            if (quote < 0) break;
            string head = html.Substring(cursor, quote - cursor).Trim();
            bool isNumber = head.Length >= 2 && head[head.Length - 1] == ',';
            if (isNumber)
                for (int i = 0; i < head.Length - 1; i++)
                    if (!char.IsDigit(head[i])) { isNumber = false; break; }
            // 形如 push([0]) 的负载没有字符串参数：跳过这个 marker 本身即可，
            // 不能跳到 quote 之后——那个引号属于下一个负载。
            if (!isNumber) { index = index + marker.Length; continue; }
            int end = FindJsonStringEnd(html, quote);
            if (end < 0) break;
            payloads.Add(html.Substring(quote, end - quote + 1));
            index = end + 1;
        }
        return payloads;
    }

    private static int FindJsonStringEnd(string text, int quote)
    {
        for (int i = quote + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '"') return i;
        }
        return -1;
    }

    private static Dictionary<string, object> NestedDict(Dictionary<string, object> data, string name)
    {
        if (data == null) return null;
        object value;
        return data.TryGetValue(name, out value) ? value as Dictionary<string, object> : null;
    }

    // 纯函数：给页面 HTML 返回房间信息（离线测试直接调它）
    internal static DouyinRoom ParseDouyinRoom(string html)
    {
        var serializer = new JavaScriptSerializer();
        foreach (string literal in ExtractPacePayloads(html))
        {
            string payload;
            try { payload = serializer.Deserialize(literal, typeof(string)) as string; }
            catch (Exception) { continue; }
            if (payload == null) continue;
            // 只认 roomStore 这一个关键标记：streamStore 在部分页面变体里并不存在，
            // 多要求一个键只会在抖音改版时更早失效（加固：少假设、多容错）。
            if (payload.IndexOf("roomStore", StringComparison.Ordinal) < 0) continue;
            string json = Regex.Replace(payload, @"^\w+:", "");      // 去掉 flight 前缀，如 "7:"
            object parsed;
            try { parsed = serializer.DeserializeObject(json); }
            catch (Exception) { continue; }
            // 负载形态实测有两种：[{...}] 或单个 {...}，都要吃。
            // 注意不能直接用 AsArray：Dictionary 也实现 IEnumerable，会被拆成 KeyValuePair。
            var single = parsed as Dictionary<string, object>;
            object[] items = single != null ? new object[] { single } : AsArray(parsed);
            if (items == null) continue;
            foreach (object item in items)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) continue;
                var room = NestedDict(NestedDict(NestedDict(entry, "state"), "roomStore"), "roomInfo");
                room = NestedDict(room, "room");
                if (room == null) continue;
                var info = new DouyinRoom();
                int status;
                info.Status = int.TryParse(Field(room, "status"), out status) ? status : 0;
                info.RoomId = Field(room, "id_str");
                info.Title = Field(room, "title");
                info.Owner = Field(NestedDict(room, "owner"), "nickname");
                var owner = NestedDict(room, "owner");
                info.OwnerId = Field(owner, "id_str") ?? Field(owner, "id");
                info.OwnerSecUid = Field(owner, "sec_uid");
                info.OwnerWebRid = Field(owner, "web_rid");
                var flv = NestedDict(NestedDict(room, "stream_url"), "flv_pull_url");
                if (flv != null)
                    foreach (var pair in flv)
                        if (pair.Value != null) info.FlvUrls[pair.Key.ToUpperInvariant()] = pair.Value.ToString();
                return info;
            }
        }
        return null;
    }

    // 画质优先级：FULL_HD1（原画）→ HD1 → SD1 → SD2
    private static string PickDouyinFlvUrl(Dictionary<string, string> urls)
    {
        return PickDouyinFlvUrlForQuality(urls, "best");
    }

    private static string PickDouyinFlvUrlForQuality(Dictionary<string, string> urls, string quality)
    {
        if (urls == null) return null;
        string[] order = NormalizeQuality(quality) == "smooth"
            ? new[] { "SD1", "SD2", "HD1", "FULL_HD1" }
            : NormalizeQuality(quality) == "high"
                ? new[] { "HD1", "SD1", "SD2", "FULL_HD1" }
                : new[] { "FULL_HD1", "HD1", "SD1", "SD2" };
        foreach (string name in order)
        {
            string value;
            if (urls.TryGetValue(name, out value) && IsSafeStreamUrl(value)) return value;
        }
        return null;
    }

}
