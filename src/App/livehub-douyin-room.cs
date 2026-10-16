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
    private static bool DouyinNeedsProxy;
    private static string[] DouyinIpv4Cache;
    private static DateTime DouyinIpv4CacheAt;

    /// 这次抓取是不是撞上抖音风控了（用来决定要不要请用户过验证）
    private static bool DouyinCaptchaBlocked;

    private static DouyinRoom FetchDouyinRoom(string room)
    {
        // 先正常试一次；撞上风控就**静默**用常驻 profile 重取 cookie 再来一次。
        // 登录过的话这一步连验证码都不会出现、用户完全无感 —— 这是 cookie 到期后的自动续命。
        DouyinRoom first = FetchDouyinRoomOnce(room);
        if (first != null) return first;
        if (DouyinCaptchaBlocked && DouyinGuard.TryRefreshSilently(room))
        {
            DouyinCaptchaBlocked = false;
            return FetchDouyinRoomOnce(room);
        }
        return null;
    }

    private static DouyinRoom FetchDouyinRoomOnce(string room)
    {
        return FetchDouyinRoomOnce(room, "playback");
    }

    // 与播放共用 IPv4 优先、域名及代理回退；弹幕重试不启动登录浏览器。
    internal static DouyinRoom FetchDouyinDanmakuRoom(string room)
    {
        return FetchDouyinRoomOnce(room, "danmaku");
    }

    private static DouyinRoom FetchDouyinRoomOnce(string room, string channel)
    {
        bool triedProxy = DouyinNeedsProxy;
        if (triedProxy)
        {
            DouyinRoom proxied = FetchDouyinRoomUrl(DouyinPagePrefix + room, true, channel);
            if (proxied != null) return proxied;
        }
        foreach (string host in DouyinIpv4Hosts())
        {
            DouyinRoom viaIpv4 = FetchDouyinRoomUrl("https://" + host + "/" + room, false, channel);
            if (viaIpv4 != null) return viaIpv4;
        }
        DouyinRoom viaHostname = FetchDouyinRoomUrl(DouyinPagePrefix + room, false, channel);
        if (viaHostname != null) return viaHostname;
        return triedProxy ? null : FetchDouyinRoomUrl(DouyinPagePrefix + room, true, channel);
    }

    // 解析 A 记录并缓存 5 分钟（CDN 地址本来就会轮换）；取前两个就够，同一个池子
    private static string[] DouyinIpv4Hosts()
    {
        if (DouyinIpv4Cache != null && (DateTime.UtcNow - DouyinIpv4CacheAt).TotalMinutes < 5) return DouyinIpv4Cache;
        var hosts = new List<string>();
        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses("live.douyin.com"))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                hosts.Add(address.ToString());
                if (hosts.Count >= 2) break;
            }
        }
        catch (Exception) { }
        DouyinIpv4Cache = hosts.ToArray();
        DouyinIpv4CacheAt = DateTime.UtcNow;
        return DouyinIpv4Cache;
    }

    private static DouyinRoom FetchDouyinRoomUrl(string url, bool viaSystemProxy, string channel)
    {
        Uri logical = new Uri("https://live.douyin.com" + new Uri(url).PathAndQuery);
        try
        {
            for (int redirects = 0; redirects <= 5; redirects++)
            {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Host = logical.Host;     // 连 IP 字面量时 Host 头也必须是域名
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Proxy = viaSystemProxy ? WebRequest.GetSystemWebProxy() : null;
            request.UserAgent = DouyinUserAgent;
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
            // 先带上用户过验证后存下来的 cookie；没有就只用临时 nonce（很可能撞风控）
            string savedCookies = PlatformLogin.CookieHeaderForUri(logical);
            request.AllowAutoRedirect = false;
            request.Headers["Cookie"] = savedCookies.Length > 0
                ? "__ac_nonce=" + DouyinNonce() + "; " + savedCookies
                : "__ac_nonce=" + DouyinNonce();
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse response = ScopedGetTransportForTest == null
                ? (HttpWebResponse)request.GetResponse()
                : ScopedGetTransportForTest(logical, delegate(HttpWebRequest fixture)
                    { fixture.Proxy = request.Proxy; fixture.Headers["Cookie"] = request.Headers["Cookie"]; }))
            {
                int status = (int)response.StatusCode;
                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                {
                    logical = ScopedRedirectTarget(logical, response.Headers["Location"]);
                    url = logical.AbsoluteUri;
                    continue;
                }
            using (Stream stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                int total = 0, read;
                while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, DouyinMaxPageBytes - total + 1))) > 0)
                {
                    if (read > DouyinMaxPageBytes - total) throw new InvalidDataException("抖音房间页超过 4 MiB");
                    memory.Write(buffer, 0, read);
                    total += read;
                }
                string html = Encoding.UTF8.GetString(memory.ToArray());
                DouyinRoom parsed = ParseDouyinRoom(html);
                if (parsed != null)
                {
                    Match user = Regex.Match(html, @"user_unique_id\\?"":\s*\\?""?(\d{6,})");
                    parsed.UserUniqueId = user.Success ? user.Groups[1].Value : "";
                    CookieContainer cookies = new CookieContainer();
                    string setCookies = response.Headers["Set-Cookie"];
                    if (!string.IsNullOrEmpty(setCookies))
                    {
                        try { cookies.SetCookies(logical, setCookies); }
                        catch (CookieException ex) { Diagnostics.Report("抖音响应 Cookie", ex); }
                    }
                    Cookie ttwid = cookies.GetCookies(new Uri(DouyinPagePrefix))["ttwid"];
                    parsed.Ttwid = ttwid == null ? "" : ttwid.Value;
                    PlatformHealth.Record("douyin", channel, channel == "danmaku" ? "connecting" : "ready");
                }
                if (parsed == null)
                {
                    PlatformHealth.Record("douyin", channel, DouyinGuard.IsCaptchaPage(html) ? "auth-required" : "parse-error");
                    if (!BoundedLog.TryAppend(Path.Combine(RuntimeDir, "logs", "relay.log"), PlatformHealth.ResponseSummary(html))) Diagnostics.ReportLogBroken(null);
                }
                // 只有确认拿到的是验证码页才改走代理；普通网络抖动不改，免得代理没开时更糟
                // 风控拦了：标志置上，上层会弹「去过验证」
                if (channel == "playback" && parsed == null && DouyinGuard.IsCaptchaPage(html)) DouyinCaptchaBlocked = true;
                if (parsed == null && !viaSystemProxy && DouyinGuard.IsCaptchaPage(html)) DouyinNeedsProxy = true;
                return parsed;
            }
            }
            }
            throw new InvalidDataException("重定向次数超限");
        }
        catch (Exception ex)
        {
            PlatformHealth.Record("douyin", channel, PlatformHealth.Classify(ex));
            Diagnostics.Report("抖音房间获取", ex);
            return null;
        }
    }

    // 中转每次连接都调它，拿到的必须是"当下有效"的直链，所以这里不缓存
    private static string ResolveDouyinUrl(string room)
    {
        bool offline;
        return ResolveDouyinUrl(room, out offline);
    }

    private static string ResolveDouyinUrl(string room, out bool offline)
    {
        offline = false;
        DouyinRoom info = FetchDouyinRoom(room);
        if (info == null) return null;                       // 页面没抓到：临时故障
        if (info.Status != 2) { offline = true; return null; }   // 明确下播
        return PickDouyinFlvUrlForQuality(info.FlvUrls, RelayQuality);
    }

    // 一次抓取同时得到标题和直链（房间页 1.1 MB，别为标题、取流各抓一遍）
    private static string PrepareDouyin(Target target, out string title)
    {
        string roomTitle;
        return PrepareDouyinWithMediaTitle(target, out title, out roomTitle);
    }

    private static string PrepareDouyinWithMediaTitle(Target target, out string title, out string roomTitle)
    {
        DouyinRoom info = FetchDouyinRoom(target.Room);
        if (info == null && DouyinCaptchaBlocked)
        {
            // 风控拦了 —— 给用户一次过验证的机会（开 Edge 窗口，过了自动取 cookie），然后再取一次。
            // 不去掉标志的话每次播放都会重弹，很烦；所以无论成败都清掉，让用户主动再点播放才重试。
            DouyinCaptchaBlocked = false;
            if (DouyinGuard.Verify(null, target.Room))
                info = FetchDouyinRoom(target.Room);
        }
        if (info == null)
            throw new InvalidOperationException("没能从抖音解析出房间信息（" + target.Room + "）：页面改版、网络不通，或没过人机验证");
        title = info.Owner != null && info.Title != null ? info.Owner + " - " + info.Title
            : info.Owner != null ? info.Owner
            : info.Title != null ? info.Title
            : "抖音直播 " + target.Room;
        roomTitle = CreatePlaybackTitleInfo(title, info.Title).RoomTitle;
        if (info.Status != 2) throw new InvalidOperationException("抖音 " + target.Room + " 现在没有开播");
        string url = PickDouyinFlvUrlForQuality(info.FlvUrls, target.Quality);
        if (url == null) throw new InvalidOperationException("抖音 " + target.Room + " 没解析出可用的直播地址");
        WriteBitrateHint(target, url);
        return url;
    }

    private static string FetchDouyinTitle(string room)
    {
        DouyinRoom info = FetchDouyinRoom(room);
        if (info != null)
        {
            if (info.Owner != null && info.Title != null) return info.Owner + " - " + info.Title;
            if (info.Owner != null) return info.Owner;
            if (info.Title != null) return info.Title;
        }
        return "抖音直播 " + room;
    }

}
