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

    // ---------------- 限流空壳页 / 请求闸门（2026-10-26） ----------------
    //
    // 背景：抖音在「同一个客户端短时间抓太多房间页」时会返回**空壳页**——RSC 框架和
    // roomStore 都在，但 roomStore.roomInfo 里没有 room（和「房间不存在」的页面同模板）。
    // ParseDouyinRoom 只能返回 null，旧逻辑把它当成「抓不到房间页（网络或页面改版）」直接
    // 报给用户，而且开播标记要等 2 分钟才重查 —— 用户看到的就是一条误导的错误。
    // 实测（2026-10-26，12 路并发走应用同一条 .NET 请求链）：约 1/18 命中空壳页；收藏页一次要查
    // 15 个抖音房间（4 路并行）再叠加头像/预览查询就会触发。空壳页不带验证码，降速/等待即可恢复。
    //
    // 现在的处理：
    //   ① 识别空壳页 → 记 rate-limited 观测，短暂等待后重试一次（重试成功就照常给状态）；
    //   ② 抖音房间页请求统一闸门：最多 2 个在飞、相邻请求开始至少间隔 250 ms；
    //   ③ 同一房间 4 秒内的结果短时复用 + 单飞，合并「开播标记 / 头条 / 头像 / 预览」的重复请求；
    //   ④ TUN 假 IP（198.18.0.0/15）不再连 IP 字面量 —— 没有 SNI，TLS 会被对端掐断；
    //      真 IP 环境仍保留「IPv4 字面量优先」那条老路（绕开 IPv6「验证码中间页」）。
    internal enum DouyinFailureKind { None, Network, Captcha, Shell, Parse }

    internal static DouyinFailureKind DouyinLastFailure = DouyinFailureKind.None;
    private static DateTime DouyinShellSeenAt = DateTime.MinValue;

    /// <summary>撞上空壳页后多久内还按「临时限流」提示（秒）。</summary>
    private const int DouyinShellHintSeconds = 30;

    private static readonly object DouyinGate = new object();
    private static int DouyinInFlight;
    private static DateTime DouyinLastStart = DateTime.MinValue;
    private static readonly object DouyinRoomLockGate = new object();
    private static readonly Dictionary<string, object> DouyinRoomLocks = new Dictionary<string, object>();
    private static readonly object DouyinCacheGate = new object();
    private static readonly Dictionary<string, DouyinRoom> DouyinCachedRooms = new Dictionary<string, DouyinRoom>();
    private static readonly Dictionary<string, DateTime> DouyinCachedAt = new Dictionary<string, DateTime>();
    private static bool DouyinLiteralBroken;
    private static DateTime DouyinLiteralBrokenAt = DateTime.MinValue;
    private static int DouyinLiteralFailures;

    // 调优值（离线回归会改小；生产默认值见下）
    internal static int DouyinMaxInFlight = 2;
    internal static int DouyinFetchSpacingMs = 250;
    internal static int DouyinShellRetryDelayMs = 800;
    internal static int DouyinRoomCacheSeconds = 4;
    private const int DouyinLiteralBrokenMinutes = 10;

    // 连续撞上空壳页后的「冷却」：中转每 2 秒重连一次、界面每 2 秒补标记，
    // 风控窗口里每秒都去抓一次只会把限制拖长 —— 冷却期内直接返回「限流」，不再发请求。
    private static DateTime DouyinCooldownUntil = DateTime.MinValue;
    private static int DouyinShellStreak;
    internal static int DouyinCooldownMs = 6000;                 // 首档冷却（测试可改小）
    private const int DouyinCooldownMaxMs = 18000;

    /// <summary>按 198.18.0.0/15 识别 TUN / 透明代理给的假 IP：连字面量没有 SNI，TLS 会被掐断。</summary>
    internal static bool DouyinIsFakeAddress(IPAddress address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    private static bool DouyinLiteralUsable()
    {
        if (!DouyinLiteralBroken) return true;
        if ((DateTime.UtcNow - DouyinLiteralBrokenAt).TotalMinutes >= DouyinLiteralBrokenMinutes)
        {
            lock (DouyinGate) { DouyinLiteralBroken = false; DouyinLiteralFailures = 0; }
            return true;
        }
        return false;
    }

    /// <summary>IPv4 字面量连接连续失败两次：当前网络下这条路不可用（不只假 IP 段会这样），先熔断 10 分钟。</summary>
    private static void DouyinNoteLiteralFailure()
    {
        lock (DouyinGate)
        {
            if (++DouyinLiteralFailures < 2) return;
            DouyinLiteralBroken = true;
            DouyinLiteralBrokenAt = DateTime.UtcNow;
            DouyinLiteralFailures = 0;
        }
    }

    private static void DouyinNoteLiteralSuccess()
    {
        lock (DouyinGate) { DouyinLiteralFailures = 0; DouyinLiteralBroken = false; }
    }

    /// <summary>抖音房间页请求闸门：限制在飞数与请求开始频率（风控是「速率型」的）。</summary>
    internal static void DouyinEnterFetch()
    {
        lock (DouyinGate)
        {
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                double since = (now - DouyinLastStart).TotalMilliseconds;
                if (DouyinInFlight < DouyinMaxInFlight && since >= DouyinFetchSpacingMs) break;
                int wait = DouyinInFlight >= DouyinMaxInFlight
                    ? 60 : Math.Max(10, DouyinFetchSpacingMs - (int)since);
                Monitor.Wait(DouyinGate, Math.Min(wait, 200));
            }
            DouyinInFlight++;
            DouyinLastStart = DateTime.UtcNow;
        }
    }

    internal static void DouyinLeaveFetch()
    {
        lock (DouyinGate)
        {
            if (DouyinInFlight > 0) DouyinInFlight--;
            Monitor.PulseAll(DouyinGate);
        }
    }

    private static bool DouyinCached(string room, out DouyinRoom info)
    {
        info = null;
        lock (DouyinCacheGate)
        {
            DateTime at;
            if (!DouyinCachedAt.TryGetValue(room, out at)) return false;
            if ((DateTime.UtcNow - at).TotalSeconds > DouyinRoomCacheSeconds)
            {
                DouyinCachedAt.Remove(room);
                DouyinCachedRooms.Remove(room);
                return false;
            }
            return DouyinCachedRooms.TryGetValue(room, out info) && info != null;
        }
    }

    private static void DouyinCache(string room, DouyinRoom info)
    {
        lock (DouyinCacheGate)
        {
            DouyinCachedRooms[room] = info;
            DouyinCachedAt[room] = DateTime.UtcNow;
        }
    }

    private static object DouyinRoomLock(string room)
    {
        lock (DouyinRoomLockGate)
        {
            object gate;
            if (!DouyinRoomLocks.TryGetValue(room, out gate))
            {
                gate = new object();
                DouyinRoomLocks[room] = gate;
            }
            return gate;
        }
    }

    /// <summary>离线回归用：清掉限流/缓存/闸门状态（生产路径不会调）。</summary>
    internal static void ResetDouyinFetchStateForTest()
    {
        lock (DouyinCacheGate) { DouyinCachedRooms.Clear(); DouyinCachedAt.Clear(); }
        lock (DouyinRoomLockGate) DouyinRoomLocks.Clear();
        lock (DouyinGate) { DouyinInFlight = 0; DouyinLastStart = DateTime.MinValue; DouyinLiteralFailures = 0; DouyinLiteralBroken = false; }
        DouyinLastFailure = DouyinFailureKind.None;
        DouyinShellSeenAt = DateTime.MinValue;
        DouyinCooldownUntil = DateTime.MinValue;
        DouyinShellStreak = 0;
        DouyinNeedsProxy = false;
        DouyinCaptchaBlocked = false;
    }

    /// <summary>给用户看的一句失败原因：临时限流 / 需要网页验证 / 网络或页面改版，三者别混。</summary>
    internal static string DouyinFailureNote()
    {
        if (DouyinLastFailure == DouyinFailureKind.Shell
            && DouyinShellSeenAt != DateTime.MinValue
            && (DateTime.UtcNow - DouyinShellSeenAt).TotalSeconds <= DouyinShellHintSeconds)
            return "抖音临时限流（风控），稍后自动重试";
        if (DouyinLastFailure == DouyinFailureKind.Captcha)
            return "抖音要求网页验证（可点播放触发验证）";
        return "抓不到房间页（网络或页面改版）";
    }

    private static DouyinRoom FetchDouyinRoom(string room)
    {
        return FetchDouyinRoom(room, true);
    }

    /// <param name="allowCache">开播标记 / 标题 / 头像这类「4 秒前的同一份页面也算数」的调用传 true；
    /// 真要起播（直链必须当下有效）传 false。</param>
    private static DouyinRoom FetchDouyinRoom(string room, bool allowCache)
    {
        if (string.IsNullOrEmpty(room)) return null;
        // 冷却期内不再发请求（界面/中转都调用这里）：空壳页不是网络坏了，越重试限制越久。
        if (DateTime.UtcNow < DouyinCooldownUntil)
        {
            DouyinLastFailure = DouyinFailureKind.Shell;
            return null;
        }
        // 4 秒缓存只服务「同一时刻的开播标记 / 标题 / 预览」；离线回归可以把 TTL 设为 0 彻底关掉。
        bool useCache = allowCache && DouyinRoomCacheSeconds > 0;
        DouyinRoom cached;
        if (useCache && DouyinCached(room, out cached)) return cached;
        lock (DouyinRoomLock(room))
        {
            if (useCache && DouyinCached(room, out cached)) return cached;

            // 先正常试一次；撞上风控就**静默**用常驻 profile 重取 cookie 再来一次。
            DouyinRoom first = FetchDouyinRoomOnce(room);
            if (first == null && DouyinCaptchaBlocked && DouyinGuard.TryRefreshSilently(room))
            {
                DouyinCaptchaBlocked = false;
                first = FetchDouyinRoomOnce(room);
            }
            // 空壳页是临时限流，不是「房间不存在」也不是改版：第一次歇一下再取一次，别把房间状态直接判死；
            // 连续撞上就进入冷却，让中转/界面的自动重试别再往风控窗口里补请求。
            if (first == null && DouyinLastFailure == DouyinFailureKind.Shell && DouyinShellStreak == 0)
            {
                if (DouyinShellRetryDelayMs > 0)
                    Thread.Sleep(DouyinShellRetryDelayMs + ((Environment.TickCount ^ room.GetHashCode()) & 0x7FFFFFFF) % 200);
                DouyinLastFailure = DouyinFailureKind.None;
                first = FetchDouyinRoomOnce(room);
            }
            if (first != null)
            {
                DouyinLastFailure = DouyinFailureKind.None;
                DouyinShellSeenAt = DateTime.MinValue;
                DouyinCooldownUntil = DateTime.MinValue;
                DouyinShellStreak = 0;
                DouyinCache(room, first);
            }
            else if (DouyinLastFailure == DouyinFailureKind.Shell)
            {
                DouyinShellStreak = Math.Min(DouyinShellStreak + 1, 3);
                DouyinCooldownUntil = DateTime.UtcNow.AddMilliseconds(
                    Math.Min(DouyinCooldownMaxMs, DouyinCooldownMs * DouyinShellStreak));
                DouyinShellSeenAt = DateTime.UtcNow;
            }
            return first;
        }
    }

    private static DouyinRoom FetchDouyinRoomOnce(string room)
    {
        return FetchDouyinRoomOnce(room, "playback");
    }

    // 与播放共用 IPv4 优先、域名及代理回退；弹幕重试不启动登录浏览器。
    internal static DouyinRoom FetchDouyinDanmakuRoom(string room)
    {
        if (DateTime.UtcNow < DouyinCooldownUntil)
        {
            DouyinLastFailure = DouyinFailureKind.Shell;
            return null;
        }
        DouyinRoom first = FetchDouyinRoomOnce(room, "danmaku");
        // 弹幕侧同样会遇到限流空壳页：歇一下重试一次，别让弹幕白等一轮重连。
        if (first == null && DouyinLastFailure == DouyinFailureKind.Shell)
        {
            if (DouyinShellRetryDelayMs > 0) Thread.Sleep(DouyinShellRetryDelayMs);
            DouyinLastFailure = DouyinFailureKind.None;
            first = FetchDouyinRoomOnce(room, "danmaku");
        }
        return first;
    }

    private static DouyinRoom FetchDouyinRoomOnce(string room, string channel)
    {
        bool triedProxy = DouyinNeedsProxy;
        if (triedProxy)
        {
            DouyinRoom proxied = FetchDouyinRoomUrl(DouyinPagePrefix + room, true, channel);
            if (proxied != null) return proxied;
        }
        if (DouyinLiteralUsable())
        {
            foreach (string host in DouyinIpv4Hosts())
            {
                DouyinRoom viaIpv4 = FetchDouyinRoomUrl("https://" + host + "/" + room, false, channel);
                if (viaIpv4 != null) return viaIpv4;
                // 已经被限流时空壳页会连着来，别再换一个 IP 硬撞（那只会延长限流）。
                if (DouyinLastFailure == DouyinFailureKind.Shell) break;
            }
        }
        DouyinRoom viaHostname = FetchDouyinRoomUrl(DouyinPagePrefix + room, false, channel);
        if (viaHostname != null) return viaHostname;
        return triedProxy ? null : FetchDouyinRoomUrl(DouyinPagePrefix + room, true, channel);
    }

    // 解析 A 记录并缓存 5 分钟（CDN 地址本来就会轮换）；取前两个就够，同一个池子。
    // TUN / 透明代理给的假 IP 直接跳过：连字面量没有 SNI，抖音会掐断 TLS（实测 2026-10-26）。
    private static string[] DouyinIpv4Hosts()
    {
        if (!DouyinLiteralUsable()) return new string[0];
        if (DouyinIpv4Cache != null && (DateTime.UtcNow - DouyinIpv4CacheAt).TotalMinutes < 5) return DouyinIpv4Cache;
        var hosts = new List<string>();
        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses("live.douyin.com"))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (DouyinIsFakeAddress(address)) continue;
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
        bool viaIpv4Literal = false;
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
            viaIpv4Literal = Uri.CheckHostName(new Uri(url).Host) == UriHostNameType.IPv4;
            string html = null;
            string redirectLocation = null;
            string setCookieHeader = null;
            DouyinEnterFetch();
            try
            {
                using (HttpWebResponse response = ScopedGetTransportForTest == null
                    ? (HttpWebResponse)request.GetResponse()
                    : ScopedGetTransportForTest(logical, delegate(HttpWebRequest fixture)
                        { fixture.Proxy = request.Proxy; fixture.Headers["Cookie"] = request.Headers["Cookie"]; }))
                {
                    setCookieHeader = response.Headers["Set-Cookie"];
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        redirectLocation = response.Headers["Location"];
                    }
                    else
                    {
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
                            html = Encoding.UTF8.GetString(memory.ToArray());
                        }
                    }
                }
            }
            finally { DouyinLeaveFetch(); }
            if (redirectLocation != null)
            {
                logical = ScopedRedirectTarget(logical, redirectLocation);
                url = logical.AbsoluteUri;
                continue;
            }
            DouyinRoom parsed = ParseDouyinRoom(html);
            if (parsed != null)
            {
                Match user = Regex.Match(html, @"user_unique_id\\?"":\s*\\?""?(\d{6,})");
                parsed.UserUniqueId = user.Success ? user.Groups[1].Value : "";
                CookieContainer cookies = new CookieContainer();
                if (!string.IsNullOrEmpty(setCookieHeader))
                {
                    try { cookies.SetCookies(logical, setCookieHeader); }
                    catch (CookieException ex) { Diagnostics.Report("抖音响应 Cookie", ex); }
                }
                Cookie ttwid = cookies.GetCookies(new Uri(DouyinPagePrefix))["ttwid"];
                parsed.Ttwid = ttwid == null ? "" : ttwid.Value;
                DouyinNoteLiteralSuccess();
                PlatformHealth.Record("douyin", channel, channel == "danmaku" ? "connecting" : "ready");
            }
            else
            {
                bool captcha = DouyinGuard.IsCaptchaPage(html);
                bool shell = !captcha && DouyinPageMissingRoom(html);
                DouyinLastFailure = shell ? DouyinFailureKind.Shell
                    : captcha ? DouyinFailureKind.Captcha : DouyinFailureKind.Parse;
                if (shell) DouyinShellSeenAt = DateTime.UtcNow;
                PlatformHealth.Record("douyin", channel, shell ? "rate-limited" : captcha ? "auth-required" : "parse-error");
                if (!BoundedLog.TryAppend(Path.Combine(RuntimeDir, "logs", "relay.log"), PlatformHealth.ResponseSummary(html))) Diagnostics.ReportLogBroken(null);
            }
            // 只有确认拿到的是验证码页才改走代理；普通网络抖动不改，免得代理没开时更糟
            // 风控拦了：标志置上，上层会弹「去过验证」
            if (channel == "playback" && parsed == null && DouyinGuard.IsCaptchaPage(html)) DouyinCaptchaBlocked = true;
            if (parsed == null && !viaSystemProxy && DouyinGuard.IsCaptchaPage(html)) DouyinNeedsProxy = true;
            return parsed;
            }
            throw new InvalidDataException("重定向次数超限");
        }
        catch (Exception ex)
        {
            DouyinLastFailure = DouyinFailureKind.Network;
            PlatformHealth.Record("douyin", channel, PlatformHealth.Classify(ex));
            Diagnostics.Report("抖音房间获取", ex);
            if (viaIpv4Literal) DouyinNoteLiteralFailure();
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
        DouyinRoom info = FetchDouyinRoom(room, false);
        if (info == null) return null;                       // 页面没抓到：临时故障
        if (info.Status == 4) { offline = true; return null; }   // 只有已知状态 4 才明确下播
        if (info.Status != 2)
        {
            PlatformHealth.Record("douyin", "playback", "parse-error");
            return null;
        }
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
        DouyinRoom info = FetchDouyinRoom(target.Room, false);
        if (info == null && DouyinCaptchaBlocked)
        {
            // 风控拦了 —— 给用户一次过验证的机会（开 Edge 窗口，过了自动取 cookie），然后再取一次。
            // 不去掉标志的话每次播放都会重弹，很烦；所以无论成败都清掉，让用户主动再点播放才重试。
            DouyinCaptchaBlocked = false;
            if (DouyinGuard.Verify(null, target.Room))
                info = FetchDouyinRoom(target.Room, false);
        }
        if (info == null)
            throw new InvalidOperationException("没能从抖音解析出房间信息（" + target.Room + "）：" + DouyinFailureNote());
        title = info.Owner != null && info.Title != null ? info.Owner + " - " + info.Title
            : info.Owner != null ? info.Owner
            : info.Title != null ? info.Title
            : "抖音直播 " + target.Room;
        roomTitle = CreatePlaybackTitleInfo(title, info.Title).RoomTitle;
        if (info.Status == 4) throw new InvalidOperationException("抖音 " + target.Room + " 现在没有开播");
        if (info.Status != 2)
        {
            PlatformHealth.Record("douyin", "playback", "parse-error");
            throw new InvalidOperationException("抖音 " + target.Room + " 房间状态无法确认（status=" + info.Status + "），请稍后重试");
        }
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
