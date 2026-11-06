// --------------------------------------------------------------------------------------------
// 本地中转 + 房间状态 + FLV 工具（partial 的另一半）。
//
// 2026-09-27 从 live-hub-v2.cs 搬出来（评估报告 §7）。这一块是**整条中转链**：
//   · StartRelay / RunRelay：起子进程中转、绑 0 端口回报、闲置退出策略；
//   · Pump：把上游 FLV 裸字节流按 B站 CDN 的形状喂给播放器（Connection: close + 无 chunked）；
//   · FLV 头/Tag 解析：跳过 prelude、按时间戳去重（首屏重复播放靠它）；
//   · 房间状态与状态文件（下播/恢复的判定与发布）；
//   · ResolveRelayUrl*：直链解析与 20 秒复用窗口（PotPlayer 首包耐心只有 ~1.05 秒，见 tests\README.md）；
//   · HttpGetText / Field2：这一块与平台解析共用的两个小工具。
// 代码原样搬运，只补 using。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // ---------------- 斗鱼专用本地中转 ----------------
    //
    // 为什么需要（2026-09-18 实测结论）：
    //  1) 斗鱼 CDN 的响应是 Connection: keep-alive 且既没有 Content-Length 也没有 chunked，
    //     HTTP/1.1 下 body 没有边界，PotPlayer 会永远停在「即将播放」
    //     （虎牙是 chunked、B站 是 Connection: close，所以它们直接给直链就行；mpv/ffmpeg 不在乎）。
    //  2) 给 PotPlayer 的响应要照抄 B站 CDN 的形状：Connection: close + 裸字节流。
    //     实测给它 chunked 反而会被卡住（同样的流，chunked 卡、裸流播）。
    //  3) PotPlayer 会先开一条连接探测、再开第二条正式播；斗鱼一个直链基本只够喂一条连接，
    //     所以每条连接都要重新解析一次直链（复用旧链接第二条只能收到 ~31KB 就 EOF）。
    // 曾经有个 PickFreePort()（先绑 0 拿端口→立刻释放→把端口交给子进程去绑）。2026-09-20 删掉：
    // 那中间有窗口会被别的进程抢走，而且「端口可用」这件事本来就不该在父进程里猜——
    // 现在由中转自己绑 0 并回报实际端口（见 StartRelay）。

    // 起中转并等它就绪，返回**中转实际绑定到的端口**。
    // 2026-09-20：端口不再由启动器 PickFreePort() 先占后放（探测→释放→子进程再绑，中间有窗口
    // 会被别的进程抢走，多开时偶发；也是起播前要 Thread.Sleep(700) 的原因之一）。现在传 0，
    // 由中转自己绑（系统分配）并把端口写进 READY 状态文件回报。
    private static int StartRelay(Target target)
    {
        string exe = Application.ExecutablePath;
        string token = target.SessionId ?? NewSessionId();
        string statusFile = Path.Combine(RuntimeDir, "relay-" + token + ".status");
        try { if (File.Exists(statusFile)) File.Delete(statusFile); } catch (Exception) { }
        ProcessStartInfo psi = new ProcessStartInfo(exe, "--relay " + target.Platform + " " + target.Room + " 0 " + token
            + " " + NormalizeQuality(target.Quality) + ChildProcessPathArguments());
        psi.UseShellExecute = true;
        psi.WorkingDirectory = Path.GetDirectoryName(exe);
        using (Process relay = Process.Start(psi))
        {
            target.RelayProcessId = relay.Id;
            DateTime deadline = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < deadline)
            {
                if (LaunchCancellation.IsCancellationRequested)
                {
                    try { relay.Kill(); } catch (Exception) { }
                    throw new OperationCanceledException();
                }
                if (File.Exists(statusFile))
                {
                    string result = File.ReadAllText(statusFile, Encoding.UTF8).Trim();
                    try { File.Delete(statusFile); } catch (Exception) { }
                    // READY <端口>：端口由中转回报，启动器不再自己猜
                    int readyPort;
                    if (result.StartsWith("READY", StringComparison.Ordinal)
                        && int.TryParse(result.Substring("READY".Length).Trim(), out readyPort) && readyPort > 0)
                        return readyPort;
                    break;
                }
                if (relay.HasExited) break;
                Thread.Sleep(50);
            }
            if (!relay.HasExited) { try { relay.Kill(); } catch (Exception) { } }
        }
        // 起不来时先问房间状态：只有真 OFF 才说未开播；重播要明确说明是重播流取不到。
        string reason;
        RoomStateKind state = CheckRoomState(target.Platform, target.Room, out reason);
        if (state == RoomStateKind.Offline)
            throw new InvalidOperationException("主播没在播" + (reason == null ? "" : "（" + reason + "）")
                + "，没有可播放的流：" + target.Platform + ":" + target.Room);
        if (state == RoomStateKind.Replay)
            throw new InvalidOperationException(ReplayUnavailableMessage(reason ?? "中转未能就绪")
                + "：" + target.Platform + ":" + target.Room);
        throw new InvalidOperationException("直播中转未能就绪；请检查 "
            + Path.Combine(RuntimeDir, target.SessionId == null
                ? "relay-" + target.Platform + "-" + target.Room + ".log"
                : "session-" + target.SessionId + ".log"));
    }

    // 中转状态文件：mpv 的 live-title.lua 和启动器界面都读它。
    //   ONLINE / RECONNECTING / OFFLINE / REPLAY-UNAVAILABLE|原因（空或不存在 = 正常运行/已结束）
    private static int RelayRetrySeconds = 20;   // 下播后每隔多久看一眼是否重新开播（测试可调）

    // 房间是否在播：由启动器写（它能查四家的房间状态接口，B站 这种不走中转的也有提示）
    private static string RoomStatePath(string platform, string room)
    {
        return Path.Combine(RuntimeDir, platform + "-" + room + ".state.txt");
    }

    private static string SessionRoomStatePath(Target target)
    {
        return target.SessionId == null ? RoomStatePath(target.Platform, target.Room)
            : Path.Combine(RuntimeDir, "session-" + target.SessionId + ".state.txt");
    }

    private static void RunRoomWatcher(string platform, string room, string sessionId, int playerId)
    {
        Target target = Parse(platform + ":" + room, "bili");
        if (target == null) return;
        target.SessionId = sessionId;
        while (true)
        {
            try { using (Process process = Process.GetProcessById(playerId)) if (process.HasExited) break; }
            catch (Exception) { break; }
            RoomStateKind state = RoomState(platform, room);
            try { AtomicWriteAllText(SessionRoomStatePath(target), RoomStateFileText(platform, state)); }
            catch (Exception) { }
            Thread.Sleep(10000);
        }
        try { AtomicWriteAllText(SessionRoomStatePath(target), ""); } catch (Exception) { }
    }

    private static void StartRoomWatcher(Target target, Process player)
    {
        if (target.Platform != "bili" || target.SessionId == null) return;
        try
        {
            var start = new ProcessStartInfo(Application.ExecutablePath,
                "--watch " + target.Platform + " " + target.Room + " " + target.SessionId + " " + player.Id + ChildProcessPathArguments());
            start.UseShellExecute = true;
            start.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            using (Process watcher = Process.Start(start)) { }
        }
        catch (Exception) { }
    }

    // 弹幕覆盖层子进程：只在 B站 且需要时拉起；与 --watch 同构 —— 播放器退出它就自己收工，
    // 关掉启动器窗口也不影响它（用户的实际习惯就是开着播放器把启动器关掉）。
    private static void StartDanmakuHost(Target target, Process player)
    {
        // ⚠️ 这里原来写的是 `target.Platform != "bili"` —— 只给 B站 拉覆盖层。
        //    加虎牙/斗鱼/抖音时漏改了，于是**探针能跑通、实际用却一条弹幕都没有**：
        //    探针是直接调 `--danmaku`，绕过了启动器这条路径。
        //    现在改成问工厂「这个平台接进来了吗」，以后加平台不会再漏。
        if (target.SessionId == null || player == null) return;
        if (!DanmakuClientFactory.IsSupported(target.Platform)) return;
        if (target.DanmakuMode == "off") return;
        try
        {
            if (target.DanmakuMode != "on")
            {
                DanmakuConfig config = DanmakuConfig.Load(DanmakuHost.ConfigPath());
                if (!config.AutoStart) return;
            }
            var start = new ProcessStartInfo(Application.ExecutablePath,
                "--danmaku " + target.Platform + " " + target.Room + " " + target.SessionId
                + " " + player.Id + " overlay" + ChildProcessPathArguments());
            start.UseShellExecute = true;
            start.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            using (Process danmaku = Process.Start(start))
            if (danmaku != null)
                DanmakuHost.Log("会话 " + target.SessionId + " 拉起弹幕进程 pid=" + danmaku.Id
                    + "（跟随播放器 pid=" + player.Id + "）");
        }
        catch (Exception ex) { DanmakuHost.Log("拉起弹幕进程失败: " + ex.Message); }
    }

    // 中转连接状态：由中转进程写
    private static string RelayStatePath(string platform, string room)
    {
        return Path.Combine(RuntimeDir, platform + "-" + room + ".relay.txt");
    }

    private static string SessionRelayStatePath(Target target)
    {
        return target.SessionId == null ? RelayStatePath(target.Platform, target.Room)
            : Path.Combine(RuntimeDir, "session-" + target.SessionId + ".relay.txt");
    }

    private static string ActiveRelayStatePath(string platform, string room)
    {
        return RelaySessionId == null ? RelayStatePath(platform, room)
            : Path.Combine(RuntimeDir, "session-" + RelaySessionId + ".relay.txt");
    }

    // 旧三态兼容入口；新代码一律走 RoomState，避免把重播压成 false/下播。
    private static bool? RoomIsLive(string platform, string room)
    {
        return LegacyRoomState(RoomState(platform, room));
    }

    private static RoomStateKind RoomState(string platform, string room)
    {
        string note;
        return CheckRoomState(platform, room, out note);
    }

    // 四家的房间状态：Live / Offline / Replay / Unknown。
    // note 是给用户看的原因（房间不存在 / 在放重播 / 接口没响应…），免得把"房间没了"说成"网络问题"。
    //
    // 2026-09-27：四段 if (platform == ...) 搬进各自的适配器（src\App\livehub-platforms.cs），
    // 这里只剩派发 —— 「哪家怎么查」从此只有一处定义。
    internal static RoomStateKind CheckRoomState(string platform, string room, out string note)
    {
        IPlatformAdapter adapter = Platforms.Get(platform);
        if (adapter == null) { note = "不认识的平台：" + platform; return RoomStateKind.Unknown; }
        RoomStateKind state = adapter.GetRoomStateKind(room, out note);
        PlatformHealth.RecordRoomState(platform, room, state);
        return state;
    }


    private static Dictionary<string, object> Field2(Dictionary<string, object> data, string name)
    {
        return NestedDict(data, name);
    }

    internal static string HttpGetText(string url, string referer)
    {
        using (HttpWebResponse response = OpenScopedGet(url, 6000,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0", referer, null, false))
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
    }

    internal static HttpWebResponse OpenScopedGet(string url, int timeout, string userAgent, string referer,
        Action<HttpWebRequest, Uri> configure, bool useSystemProxy = true)
    {
        Uri logical = new Uri(url);
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            Uri destination = logical;
            // 使用现有公网地址钉扎，防止重定向检查与连接之间再次 DNS 解析到私网。
            Action<HttpWebRequest> configureRequest = delegate(HttpWebRequest request)
                {
                    request.Proxy = useSystemProxy ? WebRequest.DefaultWebProxy : null;
                    request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    string cookies = PlatformLogin.CookieHeaderForUri(destination);
                    if (cookies.Length > 0) request.Headers["Cookie"] = cookies;
                    if (configure != null) configure(request, destination);
                };
            HttpWebResponse response;
            if (ScopedGetTransportForTest != null) response = ScopedGetTransportForTest(destination, configureRequest);
            else if (!useSystemProxy) response = OpenRemoteResponse(logical.AbsoluteUri, timeout, userAgent, referer, configureRequest, false);
            else
            {
                // 抖音客户端原本使用系统代理；代理负责解析目标，保留该路径的网络行为。
                if (!IsSafeRemoteHttpUrlResolved(logical.AbsoluteUri, null)) throw new InvalidDataException("代理请求目标被拒绝");
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(logical);
                request.AllowAutoRedirect = false; request.Timeout = request.ReadWriteTimeout = timeout;
                if (userAgent != null) request.UserAgent = userAgent;
                if (referer != null) request.Referer = referer;
                configureRequest(request);
                response = (HttpWebResponse)request.GetResponse();
            }
            int status = (int)response.StatusCode;
            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
            {
                try { logical = ScopedRedirectTarget(logical, response.Headers["Location"]); }
                finally { response.Dispose(); }
                continue;
            }
            return response;  // 最终响应所有权交给调用方的 using。
        }
        throw new WebException("重定向次数超限");
    }

    // 仅离线回归设置：保持逻辑 URL，实际连接定向到合成 loopback 服务。
    internal static Func<Uri, Action<HttpWebRequest>, HttpWebResponse> ScopedGetTransportForTest = null;
    internal static Func<string, IPAddress[]> ScopedRedirectResolverForTest = null;

    internal static Uri ScopedRedirectTarget(Uri original, string location)
    {
        Uri next;
        if (string.IsNullOrEmpty(location) || !Uri.TryCreate(original, location, out next)
            || (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            || next.UserInfo.Length > 0 || (original.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
            || !IsSafeRemoteHttpUrlResolved(next.AbsoluteUri, ScopedRedirectResolverForTest)) throw new InvalidDataException("重定向目标被拒绝");
        return next;
    }

    private static void PublishRelayState(string platform, string room, string state)
    {
        try { AtomicWriteAllText(ActiveRelayStatePath(platform, room), state); } catch (Exception) { }
    }

    private static string ReadActiveRelayState(string platform, string room)
    {
        try
        {
            string path = ActiveRelayStatePath(platform, room);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
        }
        catch (Exception) { return ""; }
    }

    // 播放器是不是已经走了（对方关闭连接时 Poll 返回可读且没有数据）
    private static bool ClientGone(TcpClient client)
    {
        try
        {
            Socket socket = client.Client;
            if (socket == null) return true;
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (Exception) { return true; }
    }

    private static void PublishRelayStatus(string statusFile, string status)
    {
        if (statusFile == null) return;
        try
        {
            File.WriteAllText(statusFile + ".tmp", status, new UTF8Encoding(false));
            File.Move(statusFile + ".tmp", statusFile);
        }
        catch (Exception) { }
    }

    // 中转的并发上限与存活规则（2026-09-20 抽成常量/纯函数，离线回归直接钉）：
    //   · 并发上限 8：播放器探测 + 正式 + 重连也就 2~3 条，8 是防「本地无关进程把连接占满」；
    //   · 有连接被服务过之后，最后一个连接走了要再等 180 秒才退 —— PotPlayer 缓冲/重连的间隔可能很长，
    //     退出太早它就连不上了；
    //   · 一条连接都没来过时最多等 120 秒，免得留下一个永远没人连的空中转。
    private const int RelayMaxConnections = 8;
    private const int RelayIdleExitSeconds = 180;
    private const int RelayNoClientExitSeconds = 120;
    // 连续这么多次「一个字节都没转发出去」之后：清掉直链复用窗口，强制重新解析一次。
    // 2026-10-16 虎牙 910323：主播下播转重播后直链照样解析得出来，但每次都 403/挂住，
    // 中转拿着同一条地址 0 MB 空转 20 分钟（现在下播判定已修，这条是「地址过期」的兜底）。
    private const int RelayStalledAttemptsBeforeRefresh = 3;

    private static bool RelayAcceptsConnection(int active)
    {
        return active < RelayMaxConnections;
    }

    private static bool RelayShouldExit(bool served, int active, DateTime started, DateTime lastClient, DateTime now)
    {
        if (served) return active == 0 && (now - lastClient).TotalSeconds > RelayIdleExitSeconds;
        return (now - started).TotalSeconds > RelayNoClientExitSeconds;
    }

    // 中转进程主体：先监听（PotPlayer 可以立刻连上排队），再解析上游地址，然后逐个连接转发
    private static void RunRelay(string platform, string room, int port, string statusFile)
    {
        RelayLogFile = Path.Combine(RuntimeDir, RelaySessionId == null
            ? "relay-" + platform + "-" + room + ".log"
            : "session-" + RelaySessionId + ".log");
        try { Directory.CreateDirectory(RuntimeDir); File.WriteAllText(RelayLogFile, "", Encoding.UTF8); } catch (Exception) { }
        PublishRelayState(platform, room, "");
        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;   // 传 0 时这里是系统分配的端口
        string upstream = ResolveRelayUrl(platform, room, false);
        RelayLog("上游地址=" + (upstream == null ? "(解析失败)" : "解析成功") + "，监听 127.0.0.1:" + boundPort);
        if (upstream == null && !ReadActiveRelayState(platform, room).StartsWith("REPLAY-UNAVAILABLE|", StringComparison.Ordinal))
        { PublishRelayStatus(statusFile, "ERROR"); listener.Stop(); return; }
        // 重播暂时取不到流也让播放器连进来：live-title.lua 会显示准确原因，Pump 按 20 秒节流重试。
        // 真 OFF 已在 ResolveRelayUrlFresh 里置 offline，仍走 ERROR，不会打开一个假的播放窗口。
        PublishRelayStatus(statusFile, "READY " + boundPort);
        DateTime started = DateTime.UtcNow;
        DateTime lastClient = DateTime.UtcNow;
        int[] active = new int[1];
        bool served = false;
        while (true)
        {
            if (RelayShouldExit(served, active[0], started, lastClient, DateTime.UtcNow)) break;
            if (!listener.Pending()) { Thread.Sleep(60); continue; }
            // PotPlayer 会先开一个连接探测、再开一个正式播放；必须并发服务，否则第二个连接被堵住（表现为有码率但黑屏）
            TcpClient client = listener.AcceptTcpClient();
            if (!RelayAcceptsConnection(Interlocked.CompareExchange(ref active[0], 0, 0)))
            {
                try { client.Close(); } catch (Exception) { }
                RelayLog("连接数达到上限，拒绝新连接");
                continue;
            }
            client.ReceiveTimeout = 5000;  // 空连接不能永久占用工作线程
            client.SendTimeout = 15000;
            served = true;
            lastClient = DateTime.UtcNow;
            Interlocked.Increment(ref active[0]);
            Thread worker = new Thread(delegate()
            {
                try
                {
                    // 每条连接都在 Pump 里各自解析（斗鱼一个直链基本只喂一条连接），
                    // 并且上游断了会在同一条连接里自动重连。
                    Pump(client, platform, room,
                        // forceRefresh=false：20 秒内复用刚解析过的地址（启动时那次解析就能直接给第一个连接用），
                        // 否则每个连接都要重抓一次整页，播放器等首包会等到超时。
                        delegate(out bool offline) { return ResolveRelayUrl(platform, room, false, out offline); });
                }
                catch (Exception ex) { RelayLog("连接异常: " + ex.GetType().Name + " " + ex.Message); }
                finally
                {
                    try { client.Close(); } catch (Exception) { }
                    Interlocked.Decrement(ref active[0]);
                    lastClient = DateTime.UtcNow;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }
        listener.Stop();
        PublishRelayState(platform, room, "");
    }

    private static string RelayLogFile;

    private static void RelayLog(string line)
    {
        if (RelayLogFile == null) RelayLogFile = Path.Combine(RuntimeDir, "relay.log");
        if (!BoundedLog.TryAppend(RelayLogFile, line)) Diagnostics.ReportLogBroken(null);
    }

    // 中转主体：把上游字节流喂给播放器；**上游断了就重新解析地址、重连，接着喂同一条播放器连接**，
    // 这样 CDN 定期掐连接（实测斗鱼就是这种情况：看一两分钟断一次）在播放器看来是无感的。
    private delegate string RelayResolver(out bool offline);

    private static void Pump(TcpClient client, string platform, string room, RelayResolver resolveUpstream)
    {
        NetworkStream output = client.GetStream();
        string firstLine;
        if (!TryReadRelayRequest(output, out firstLine))
        {
            RejectRelayRequest(output, "400 Bad Request");
            return;
        }
        RelayLog("连接: " + firstLine + " 平台=" + platform);
        if (!RelayRequestAllowed(firstLine))
        {
            RelayLog("令牌不匹配，拒绝（中转只服务本进程拉起的播放器）");
            RejectRelayRequest(output, "403 Forbidden");
            return;
        }
        // 立刻回响应头（探测连接也要马上有响应），再去连上游
        // 形状照抄 B站 CDN（实测 PotPlayer 认）：无长度、无分块、Connection: close，裸字节流
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"
            + "Server: live-hub\r\n"
            + "Content-Type: video/x-flv\r\n"
            + "Connection: close\r\n\r\n");
        output.Write(head, 0, head.Length);

        bool firstUpstream = true;
        long totalBytes = 0;
        long reportBytes = 0;
        DateTime lastReport = DateTime.UtcNow;
        int reconnects = 0;
        // One shared affine clock, with per-track epoch confirmation. An old
        // audio tail must never inherit the new video's clock offset.
        RelayTimeline timeline = new RelayTimeline();
        // 连续「没有有效媒体进展」预算：CDN 正常分段续接后有效音视频 tag 会复位，
        // 只有反复连上却拿不到可播放音视频才累计失败（替代旧的累计 500 次重连上限）。
        RelayProgressBudget budget = new RelayProgressBudget();
        int droppedTags = 0;
        bool exhausted = false;
        bool forwardedMedia = false;
        string publishedState = null;   // 只在状态变化时写文件，别每轮都写盘
        // CDN 正常切断短连接后通常数百毫秒内续上，不为这些无感续接反复闪重连提示。
        // 以真正转发的音视频 tag 为进展；只有 FLV 头、元数据或重叠片段不能冒充恢复。
        Stopwatch mediaProgress = Stopwatch.StartNew();
        int stalled = 0;                // 连续「一个字节都没转发出去」的上游尝试次数
        int resolveFailures = 0;        // 连续「解析不到上游地址」次数：用于逐步放缓重试
        // 走到循环外时的收尾状态：null = 什么都不写（下播等待超时那条路，保留已写好的 OFFLINE）
        string finalState = "RECONNECTING";
        while (!budget.Exhausted)
        {
            if (ClientGone(client)) { RelayLog("播放器断开，结束"); return; }
            bool offline;
            string upstream = resolveUpstream(out offline);
            if (ClientGone(client)) { RelayLog("播放器断开，结束"); return; }
            if (upstream == null && offline)
            {
                // 主播下播：**不要**断掉播放器连接——挂在这里等重开，播放器那边由状态文件显示「已下播」
                if (publishedState != "OFFLINE")
                {
                    publishedState = "OFFLINE";
                    PublishRelayState(platform, room, "OFFLINE");
                    if (forwardedMedia)
                        PublishRelayDiag(platform, room, "OFFLINE", timeline, reconnects, droppedTags,
                            totalBytes, false);
                    RelayLog("主播已下播，挂起等待重新开播（播放器已提示）");
                }
                while (upstream == null && offline)
                {
                    if (ClientGone(client)) { RelayLog("播放器断开，结束"); return; }
                    Thread.Sleep(Math.Max(200, RelayRetrySeconds * 1000));
                    upstream = resolveUpstream(out offline);
                }
                if (upstream == null) continue;
                RelayLog("主播重新开播，继续接流");
            }
            else if (upstream == null)
            {
                string resolverState = ReadActiveRelayState(platform, room);
                if (resolverState.StartsWith("REPLAY-UNAVAILABLE|", StringComparison.Ordinal))
                {
                    publishedState = "REPLAY-UNAVAILABLE";
                    string reason = resolverState.Substring("REPLAY-UNAVAILABLE|".Length);
                    RelayLog(ReplayUnavailableMessage(reason) + "，" + RelayRetrySeconds + " 秒后再试");
                    Thread.Sleep(Math.Max(2000, RelayRetrySeconds * 1000));
                    continue;
                }
                if (publishedState != "RECONNECTING")
                {
                    publishedState = "RECONNECTING";
                    PublishRelayState(platform, room, "RECONNECTING");
                }
                RelayLog("上游地址解析失败，稍后重试");
                reconnects++;
                budget.FailedAttempt();
                // 解析失败多半是平台限流/风控：固定 2 秒重试只会把限制拖长。逐次放缓（2/4/8/16 秒），
                // 解析成功立刻复位；抖音侧另有自己的冷却，这里再兜一层。
                resolveFailures = Math.Min(resolveFailures + 1, 4);
                Thread.Sleep(Math.Min(16000, 2000 << (resolveFailures - 1)));
                continue;
            }
            resolveFailures = 0;
            // 「ONLINE」只表示**真的在转发**（下面拿到上游数据后才发布）：
            // 解析成功、连上但一个字节都取不到（2026-10-16 虎牙下播后的 403/挂起）不算在线，
            // 否则状态文件会一直骗播放器和界面说「正常」。
            long attemptStart = totalBytes;
            RelayMediaProgress segmentProgress = new RelayMediaProgress();
            try
            {
                using (HttpWebResponse response = OpenRelayUpstream(upstream, platform))
                using (Stream input = response.GetResponseStream())
                {
                    RelayLog((firstUpstream ? "上游 " : "重连 ") + (int)response.StatusCode + " " + response.ContentType
                        + " 主机=" + RelayHostForLog(upstream));
                    byte[] firstTagHeader = null;
                    if (!firstUpstream)
                    {
                        if (!TrySkipFlvPreamble(input, out firstTagHeader))
                            throw new InvalidDataException("重连后的 FLV 头不完整或无效");
                    }
                    else
                    {
                        byte[] flvHeader = new byte[13];
                        if (!ReadExactly(input, flvHeader, flvHeader.Length)
                            || flvHeader[0] != 'F' || flvHeader[1] != 'L' || flvHeader[2] != 'V'
                            || flvHeader[3] != 1 || flvHeader[8] != 9)
                            throw new InvalidDataException("上游没有有效的 FLV 文件头");
                        // 播放器可能在等首包时就断开（实测 PotPlayer 约 1 秒没数据就重连）：
                        // 这里和下面转发 tag 一样按「播放器断开」处理，直接结束这条连接，
                        // 别掉进 catch 被当成上游异常，白解析一次地址再重连。
                        try { output.Write(flvHeader, 0, flvHeader.Length); }
                        catch (Exception) { RelayLog("播放器断开，结束"); return; }
                        totalBytes += flvHeader.Length;
                    }
                    firstUpstream = false;
                    timeline.BeginReconnect();
                    while (true)
                    {
                        // 整个 FLV tag 收齐并校验 PreviousTagSize 后才发给播放器。
                        // 若 CDN 在 tag 中间断开，残片留在本进程，不会污染下一条上游流。
                        byte[] tag = ReadCompleteFlvTag(input, firstTagHeader);
                        firstTagHeader = null;
                        if (tag == null) break;
                        int slot = tag[0] == 8 ? 0 : (tag[0] == 9 ? 1 : -1);
                        long correctionsBefore = timeline.Corrections;
                        long epochTailsBefore = timeline.EpochTailEpisodes;
                        if (!timeline.Accept(tag))
                        {
                            if (timeline.Exhausted) { exhausted = true; break; }
                            if (timeline.EpochTailEpisodes != epochTailsBefore)
                                RelayLog("时间线代际[" + RelayHostForLog(upstream) + "] 丢弃旧代际尾包（累计已丢 "
                                    + timeline.DiscardedEpochTail + " 个，代际次数 " + timeline.EpochTailEpisodes + "）");
                            droppedTags++;
                            continue;
                        }
                        if (timeline.Corrections != correctionsBefore)
                            RelayLog("时间线修正[" + RelayHostForLog(upstream) + "] 原因=" + timeline.LastReason
                                + " 原始跳变=" + timeline.LastJumpMs + "ms 累计修正=" + timeline.Corrections
                                + " 跨代际跳变=" + timeline.Jumps);
                        if (segmentProgress.Observe(tag)) budget.Recover();
                        try { output.Write(tag, 0, tag.Length); }
                        catch (Exception) { RelayLog("播放器断开，结束"); return; }
                        totalBytes += tag.Length;
                        if (slot >= 0)
                        {
                            forwardedMedia = true;
                            mediaProgress.Restart();
                            if (publishedState != "ONLINE")
                            {
                                publishedState = "ONLINE";
                                PublishRelayState(platform, room, "ONLINE");
                                PublishRelayDiag(platform, room, "ONLINE", timeline, reconnects, droppedTags,
                                    totalBytes, segmentProgress.Stable);
                            }
                        }
                        if ((DateTime.UtcNow - lastReport).TotalSeconds >= 30)
                        {
                            RelayLog("已转发 " + (totalBytes / 1024 / 1024) + " MB（最近 30 秒 "
                                + ((totalBytes - reportBytes) / 1024) + " KB）"
                                + "｜丢弃重连重叠 " + droppedTags + " 个 tag");
                            lastReport = DateTime.UtcNow;
                            reportBytes = totalBytes;
                            if (forwardedMedia)
                                PublishRelayDiag(platform, room, publishedState == null ? "UNKNOWN" : publishedState,
                                    timeline, reconnects, droppedTags, totalBytes, segmentProgress.Stable);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RelayLog("上游异常: " + ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("x")
                    + " 主机=" + RelayHostForLog(upstream));
            }
            if (exhausted)
            {
                // 4 位时间戳能表示的上限到了：继续重连也只会反复超界，明确结束让用户重开会话。
                finalState = "RECONNECTING";
                RelayLog("时间线已超出 FLV 可表示范围，中转结束（需要重新打开会话）");
                break;
            }
            // 连着几次一个字节都没取到 → 直链多半已经作废（CDN 掐流、签名过期），
            // 清掉 20 秒复用窗口，下一轮强制重新解析。
            // （2026-10-16 之前只会拿着同一条地址一直重连：日志里「准备重连 #N（已转发 0 MB）」）
            if (totalBytes == attemptStart) stalled++; else stalled = 0;
            if (stalled >= RelayStalledAttemptsBeforeRefresh)
            {
                stalled = 0;
                RelayLog("连续 " + RelayStalledAttemptsBeforeRefresh + " 次没取到数据，强制重新解析上游地址");
                RelayUrlCachedUrl = null;
                RelayUrlCachedKey = null;
            }
            // 播放器已经走了就别再解析上游地址：解析一次就是一次整页抓取
            // （实测死循环时 25 秒能抓 30 多次抖音房间页），只等新连接进来。
            if (ClientGone(client)) { RelayLog("播放器断开，结束"); return; }
            if (publishedState != "RECONNECTING"
                && (publishedState != "ONLINE" || mediaProgress.ElapsedMilliseconds >= 1500))
            {
                publishedState = "RECONNECTING";
                PublishRelayState(platform, room, "RECONNECTING");
                if (forwardedMedia)
                    PublishRelayDiag(platform, room, "RECONNECTING", timeline, reconnects, droppedTags,
                        totalBytes, segmentProgress.Stable);
            }
            reconnects++;
            if (segmentProgress.Stable) budget.Recover(); else budget.FailedAttempt();
            RelayLog("准备重连 #" + reconnects + "（已转发 " + (totalBytes / 1024 / 1024) + " MB）");
            Thread.Sleep(segmentProgress.Stable ? 150 : 1000);
        }
        // 以前这里无条件写空串（= 一切正常）。但能走到这一行，要么重连次数打满、要么重开等待超时，
        // 都是「中转要结束了」——写空串等于骗播放器说一切正常：画面停住却没有任何提示。
        // 正常收尾（播放器断开）走的是上面的 return，不会到这里。
        if (finalState != null) PublishRelayState(platform, room, finalState);
        if (forwardedMedia && finalState != null)
            PublishRelayDiag(platform, room, finalState, timeline, reconnects, droppedTags, totalBytes, false);
        if (budget.Exhausted)
            RelayLog("连续 " + RelayProgressBudget.Limit + " 次没有有效媒体进展，中转结束");
        RelayLog("中转结束，共转发 " + (totalBytes / 1024 / 1024) + " MB");
    }

    private static void RejectRelayRequest(Stream output, string status)
    {
        try
        {
            byte[] deny = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            output.Write(deny, 0, deny.Length);
        }
        catch (IOException) { } // 对端已断开。
    }

    private static bool TryReadRelayRequest(NetworkStream stream, out string firstLine)
    {
        firstLine = "";
        const int limit = 8192;
        byte[] bytes = new byte[limit];
        int count = 0;
        int originalTimeout = stream.ReadTimeout;
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            while (count < limit && timer.ElapsedMilliseconds < 5000)
            {
                int remainingMs = (int)(5000 - timer.ElapsedMilliseconds);
                stream.ReadTimeout = Math.Max(1, originalTimeout > 0 ? Math.Min(originalTimeout, remainingMs) : remainingMs);
                int read = stream.Read(bytes, count, Math.Min(512, limit - count));
                if (read == 0) return false;
                int end = count + read;
                for (int at = count; at < end; at++)
                {
                    if (bytes[at] != '\n') continue;
                    bool finished = (at >= 3 && bytes[at - 1] == '\r' && bytes[at - 2] == '\n' && bytes[at - 3] == '\r')
                        || (at >= 1 && bytes[at - 1] == '\n');
                    if (!finished) continue;
                    string text = Encoding.ASCII.GetString(bytes, 0, at + 1);
                    int lineEnd = text.IndexOf('\n');
                    firstLine = text.Substring(0, lineEnd).TrimEnd('\r');
                    return firstLine.Length > 0;
                }
                count = end;
            }
            return false;
        }
        catch (IOException) { return false; }
        finally { try { stream.ReadTimeout = originalTimeout; } catch (ObjectDisposedException) { } }
    }

    /// <summary>产品路径的上游地址出口：回环/私网/本地域名一律不接受（返回 null）。
    /// 与 OpenRelayUpstream 的逐跳校验共用 IsSafeRemoteHttpUrl 这一份判据。</summary>
    internal static string GuardRelayUpstream(string url)
    {
        if (url == null) return null;
        return IsSafeRemoteHttpUrl(url) ? url : null;
    }

    /// <summary>逐跳校验 CDN 重定向，防止公开 URL 用 302 把中转引向本机/私网。</summary>
    private static HttpWebResponse OpenRelayUpstream(string url, string platform)
    {
        string current = url;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            // 初始地址已经在各平台解析器里校验；离线中转测试会故意给回环地址。
            // 重定向是新的远端输入，必须逐跳复核，不能 302 回本机/私网。
            if (redirect > 0 && !IsSafeRemoteHttpUrl(current))
            {
                // 拒绝也留脱敏原因：只记主机名，不记完整签名 URL/查询参数。
                RelayLog("上游重定向被拒绝[" + RelayHostForLog(current) + "] 原因=目标不是安全公网地址");
                throw new InvalidDataException("上游返回了不安全的地址，已拒绝连接");
            }
            // 连接目标钉在已校验的公网地址上，Host 头/SNI/证书名校验仍是原域名（TOCTOU 封口）。
            // 初始地址（redirect==0）已由产品出口 GuardRelayUpstream 校验；离线中转测试用注入的
            // RelayResolver 直接给本地服务器地址，所以这里信任字面地址。
            HttpWebResponse response = OpenRemoteResponse(current, 15000, null, null,
                delegate(HttpWebRequest request)
                {
                    request.Proxy = null;   // 上游 CDN 使用直连路径，避免系统代理改变取流连接
                    // 把「实际连到哪个地址、Host 用谁」留在中转日志里：钉扎生效时这里能看到
                    // IP 字面量 + 原域名，出问题时不用猜（离线测试这里会是注入的本地地址）。
                    RelayLog("上游连接目标=" + request.Address.Host
                        + (request.Host == null ? "（未钉扎）" : " Host=" + request.Host));
                    if (platform == "huya")
                    {
                        request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0";
                        request.Referer = "https://www.huya.com/";
                        request.Headers["Origin"] = "https://www.huya.com";
                    }
                    else if (platform == "douyin")
                    {
                        request.UserAgent = DouyinUserAgent;
                        request.Referer = DouyinReferer;
                    }
                    else request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                },
                redirect == 0);
            int status = (int)response.StatusCode;
            if (status < 300 || status >= 400) return response;
            string location = response.Headers["Location"];
            response.Close();
            Uri next;
            if (location == null || !Uri.TryCreate(new Uri(current), location, out next))
            {
                RelayLog("上游重定向被拒绝[" + RelayHostForLog(current) + "] 原因=无效重定向地址");
                throw new InvalidDataException("上游返回了无效重定向");
            }
            current = next.AbsoluteUri;
        }
        RelayLog("上游重定向被拒绝[" + RelayHostForLog(current) + "] 原因=重定向次数过多");
        throw new InvalidDataException("上游重定向次数过多");
    }

    /// Continuous output time with one shared offset and a cross-track epoch
    /// guard. Only four timestamp bytes are changed; encoded payload/CTS stays.
    ///
    /// 与 Rust src/flv.rs 的 ClockEpoch 等价：一次时钟代际切换只改一次共享偏移；
    /// 两轨都确认新代际之前，另一轨的旧尾部按原始时间戳窗口识别并丢弃，
    /// 避免旧音频套上新偏移后冒充「已追上」，把音画相对时间改坏。
    private sealed class RelayTimeline
    {
        private const long JumpLimit = 2000;
        private readonly long[] last = { -1, -1 };
        private readonly long[] rawLast = { -1, -1 };
        private readonly bool[] catching = { false, false };
        private long shift;
        private bool epoch;
        private long epochOrigin, epochLatest;
        private long epochStarted;
        private static readonly long EpochGuardTicks = Stopwatch.Frequency * 5;
        private readonly bool[] confirmed = { false, false };
        private bool epochTailDropping;
        public bool Exhausted;
        public long Jumps;                 // 非传输层归零的跳变修正次数（与 Rust jumps 同义）
        public long Corrections;           // 所有代际切换次数，含重连归零
        public long LastJumpMs;            // 最近一次修正的原始跳变幅度（毫秒）
        public string LastReason;          // 最近一次修正的原因分类（只进日志，不含 URL）
        public long DiscardedEpochTail;    // 丢弃的旧代际尾包数
        public long EpochTailEpisodes;     // 发生过旧代际尾包丢弃的代际次数

        public void BeginReconnect()
        {
            catching[0] = last[0] >= 0; catching[1] = last[1] >= 0;
            // 未完成的代际保护跨 HTTP 边界保留：旧尾巴不能因为换了连接就套上新偏移。
        }
        private void BeginEpoch(int slot, long raw, long target, bool transportReset, long jumpMs, string reason)
        {
            // 用原始时钟算偏移，绝不从 clamp 后的中间值反推。
            shift = target - raw;
            epoch = true; epochOrigin = epochLatest = raw; epochTailDropping = false;
            epochStarted = Stopwatch.GetTimestamp();
            confirmed[0] = last[0] < 0; confirmed[1] = last[1] < 0;
            confirmed[slot] = true; catching[slot] = false;
            Corrections++; LastJumpMs = jumpMs; LastReason = reason;
            if (!transportReset) Jumps++;
        }
        public bool Accept(byte[] tag)
        {
            long boundary = Math.Max(0, Math.Max(last[0], last[1]));
            if (FlvTagIsCodecHeader(tag)) { SetFlvTagTimestamp(tag, boundary); return true; }
            int slot = tag[0] == 8 ? 0 : (tag[0] == 9 ? 1 : -1);
            if (slot < 0) return true;
            long raw = (long)(uint)FlvTagTimestamp(tag);
            // A missing track must not keep the old-generation filter alive forever.
            // After the same five-second guard as Rust, ordinary jump detection can
            // rebase a new response instead of discarding its every media tag.
            if (epoch && Stopwatch.GetTimestamp() - epochStarted >= EpochGuardTicks)
                epoch = false;
            if (epoch)
            {
                // 允许另一轨首包略早于本轨（正常交错），但拒绝旧代际数秒外的尾部；
                // 上沿随已确认的最新原始时间戳前移，暂时缺席的轨之后仍能加入新时钟。
                long lower = Math.Max(0, epochOrigin - JumpLimit), upper = epochLatest + JumpLimit;
                if (raw < lower || raw > upper)
                {
                    if (!epochTailDropping) { epochTailDropping = true; EpochTailEpisodes++; }
                    DiscardedEpochTail++; return false;
                }
                confirmed[slot] = true; epochLatest = Math.Max(epochLatest, raw);
            }
            long adjusted = raw + shift;
            bool wasCatching = catching[slot];
            if (!epoch && last[slot] >= 0)
            {
                long forward = adjusted - boundary, backward = last[slot] - adjusted;
                bool clockReset = wasCatching && raw < 1000 && rawLast[slot] - raw > JumpLimit;
                if (clockReset || forward > JumpLimit || (!wasCatching && backward > JumpLimit))
                {
                    string reason = clockReset ? "时钟归零" : forward > JumpLimit ? "前向跳变" : "后向跳变";
                    long jumpMs = forward > JumpLimit ? forward : -backward;
                    BeginEpoch(slot, raw, boundary + (clockReset ? 1 : 20), clockReset, jumpMs, reason);
                    adjusted = raw + shift;
                }
            }
            if (wasCatching && !epoch)
            {
                if (adjusted <= last[slot]) return false;
                catching[slot] = false;
            }
            else if (epoch)
            {
                // 已转发的轨不能因旧代际尾包而倒退，即使它落在代际交错窗口内。
                if (adjusted < last[slot] || (wasCatching && adjusted == last[slot])) return false;
                catching[slot] = false;
            }
            if (adjusted < 0 || adjusted > uint.MaxValue) { Exhausted = true; return false; }
            last[slot] = Math.Max(last[slot], adjusted); rawLast[slot] = raw;
            SetFlvTagTimestamp(tag, adjusted);
            if (epoch && confirmed[0] && confirmed[1]) epoch = false;
            return true;
        }
    }

    // Header bytes and HTTP 200 are not evidence that playback recovered.
    // 与 Rust relay.rs 的 MediaProgress 对齐：需要足够 tag 数、足够音视频字节，
    // 且至少一条轨的时间戳真实推进 ≥1s；FLV 头、初始化包和静止时间戳都不算恢复。
    private sealed class RelayMediaProgress
    {
        private long count, bytes;
        private readonly long[] first = { -1, -1 };
        private readonly long[] last = { -1, -1 };
        public bool Stable;
        public bool Observe(byte[] tag)
        {
            if (Stable || tag == null || tag.Length < 11) return false;
            if (tag[0] != 8 && tag[0] != 9) return false;
            int payload = (tag[1] << 16) | (tag[2] << 8) | tag[3];
            if (payload <= 0 || FlvTagIsCodecHeader(tag)) return false;
            long timestamp = (long)(uint)FlvTagTimestamp(tag);
            int slot = tag[0] == 8 ? 0 : 1;
            if (first[slot] < 0) first[slot] = timestamp;
            last[slot] = Math.Max(last[slot], timestamp); count++; bytes += tag.Length;
            // Cross-track offset is not playback progress: at least one track's
            // own timestamps must advance, even when audio and video start apart.
            bool advanced = (first[0] >= 0 && last[0] - first[0] >= 1000)
                || (first[1] >= 0 && last[1] - first[1] >= 1000);
            Stable = count >= 24 && bytes >= 8192 && advanced;
            return Stable;
        }
    }

    /// 连续无有效媒体进展预算（与 Rust relay.rs 的 consecutive_failures 同义）：
    /// 有效音视频转发会 Recover()，只有「连上却拿不到可播放音视频」才累计失败。
    internal sealed class RelayProgressBudget
    {
        internal const int Limit = 500;
        private int failures;
        internal int ConsecutiveFailures { get { return failures; } }
        internal bool Exhausted { get { return failures >= Limit; } }
        internal void Recover() { failures = 0; }
        internal void FailedAttempt() { if (failures < Limit) failures++; }
    }

    /// 日志里的上游标识：只留主机名，去掉查询参数/路径/令牌，避免签名直链进日志。
    internal static string RelayHostForLog(string url)
    {
        if (string.IsNullOrEmpty(url)) return "(无上游地址)";
        Uri uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
            return "(无法解析的主机)";
        return uri.Host;
    }

    // 给启动器/工作台只读展示的中继计数。不写 URL、Cookie 或请求头。
    private static string RelayDiagPath(string platform, string room)
    {
        return Path.Combine(RuntimeDir, platform + "-" + room + ".relaydiag");
    }

    private static void PublishRelayDiag(string platform, string room, string state, RelayTimeline timeline,
        int reconnects, int droppedTags, long bytes, bool stable)
    {
        try
        {
            if (RuntimeDir == null) return;
            StringBuilder text = new StringBuilder();
            text.Append("version=1\n");
            text.Append("state=").Append(state == null ? "UNKNOWN" : state).Append('\n');
            text.Append("updated=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("reconnects=").Append(reconnects.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("jumps=").Append(timeline == null ? "0" : timeline.Jumps.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("corrections=").Append(timeline == null ? "0" : timeline.Corrections.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("discarded_epoch_tail=").Append(timeline == null ? "0" : timeline.DiscardedEpochTail.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("dropped_tags=").Append(droppedTags.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("bytes=").Append(bytes.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("stable=").Append(stable ? "1" : "0").Append('\n');
            string path = RelaySessionId == null ? RelayDiagPath(platform, room)
                : Path.Combine(RuntimeDir, "session-" + RelaySessionId + ".relaydiag");
            AtomicWriteAllText(path, text.ToString());
        }
        catch (Exception ex) { Diagnostics.Report("中继诊断写入", ex); }
    }

    /// FLV tag 的时间戳（毫秒）。布局：tag 头第 4~6 字节是低 24 位、第 7 字节是高 8 位
    /// （大端在前、扩展字节在后）。不是 11 字节的 tag 头就返回 -1。
    private static int FlvTagTimestamp(byte[] tag)
    {
        if (tag == null || tag.Length < 11) return -1;
        return (tag[7] << 24) | (tag[4] << 16) | (tag[5] << 8) | tag[6];
    }

    private static bool FlvTagIsCodecHeader(byte[] tag)
    {
        if (tag == null || tag.Length < 17) return false;
        int payload = (tag[1] << 16) | (tag[2] << 8) | tag[3];
        return (tag[0] == 8 && payload >= 2 && (tag[11] >> 4) == 10 && tag[12] == 0)
            || (tag[0] == 9 && ((payload >= 2 && ((tag[11] & 15) == 7 || (tag[11] & 15) == 12) && tag[12] == 0)
                || (payload >= 5 && (tag[11] & 0x80) != 0 && (tag[11] & 15) == 0)));
    }

    /// 原地改写 FLV tag 的时间戳（低 24 位 + 扩展字节）。只动这 4 个字节，
    /// 所以 `PreviousTagSize` 仍然有效，不用重算。
    private static void SetFlvTagTimestamp(byte[] tag, long timestamp)
    {
        if (tag == null || tag.Length < 11) return;
        tag[4] = (byte)((timestamp >> 16) & 0xFF);
        tag[5] = (byte)((timestamp >> 8) & 0xFF);
        tag[6] = (byte)(timestamp & 0xFF);
        tag[7] = (byte)((timestamp >> 24) & 0xFF);
    }

    private static byte[] ReadCompleteFlvTag(Stream input, byte[] header)
    {
        try
        {
            if (header == null)
            {
                header = new byte[11];
                if (!ReadExactly(input, header, header.Length)) return null;
            }
            if (header.Length != 11 || (header[0] != 8 && header[0] != 9 && header[0] != 18)) return null;
            int payloadSize = (header[1] << 16) | (header[2] << 8) | header[3];
            if (payloadSize > 8 * 1024 * 1024) return null;
            byte[] tag = new byte[11 + payloadSize + 4];
            Buffer.BlockCopy(header, 0, tag, 0, 11);
            if (!ReadExactly(input, tag, 11, payloadSize + 4)) return null;
            int at = 11 + payloadSize;
            int previousSize = (tag[at] << 24) | (tag[at + 1] << 16) | (tag[at + 2] << 8) | tag[at + 3];
            return previousSize == payloadSize + 11 ? tag : null;
        }
        catch (Exception) { return null; }
    }

    private static bool ReadExactly(Stream input, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = input.Read(buffer, offset, count);
            if (read <= 0) return false;
            offset += read;
            count -= read;
        }
        return true;
    }

    // 重连时只丢弃 FLV 文件头与可选的首个 script tag。若读取不完整则重试上游，
    // 不把残缺 tag 接入已在播放的流。
    private static bool TrySkipFlvPreamble(Stream input, out byte[] firstTagHeader)
    {
        firstTagHeader = null;
        try
        {
            byte[] header = new byte[13];      // FLV header(9) + PreviousTagSize(4)
            if (!ReadExactly(input, header, 13)) return false;
            if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V' || header[3] != 1
                || header[5] != 0 || header[6] != 0 || header[7] != 0 || header[8] != 9)
                return false;
            for (int tagIndex = 0; tagIndex < 8; tagIndex++)
            {
                byte[] tagHeader = new byte[11];
                if (!ReadExactly(input, tagHeader, 11)) return false;
                if (tagHeader[0] == 8 || tagHeader[0] == 9)
                {
                    firstTagHeader = tagHeader;
                    return true;
                }
                if (tagHeader[0] != 18) return false;
                int size = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
                if (size > 8 * 1024 * 1024) return false;
                byte[] skip = new byte[8192];
                int remaining = size;
                while (remaining > 0)
                {
                    int read = input.Read(skip, 0, Math.Min(skip.Length, remaining));
                    if (read <= 0) return false;
                    remaining -= read;
                }
                byte[] previousSize = new byte[4];
                if (!ReadExactly(input, previousSize, 4)) return false;
                int statedSize = (previousSize[0] << 24) | (previousSize[1] << 16)
                    | (previousSize[2] << 8) | previousSize[3];
                if (statedSize != size + 11) return false;
            }
            return false; // 防止异常流无限插入 script tag
        }
        catch (Exception) { return false; }
    }

    private static bool ReadExactly(Stream input, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = input.Read(buffer, offset, count - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    // 直链解析：斗鱼自己调公开接口算（2 次 HTTPS，约 0.3~0.6 秒，比拉 streamlink 快 3 秒以上）；
    // 任何一步不对就退回 streamlink。
    // 注意：不能缓存旧链接——实测过期的斗鱼链接照样返回 200，但码率掉到 1Mbps 左右，播放器会一直缓冲。
    private static string ResolveRelayUrl(string platform, string room, bool forceRefresh)
    {
        bool offline;
        return ResolveRelayUrl(platform, room, forceRefresh, out offline);
    }

    // 解析结果短时复用：抖音/虎牙解析一次要抓整页（实测抖音房间页约 1 秒），
    // 而播放器等首包的耐心只有 1 秒左右（实测 PotPlayer 约 1.05 秒没数据就断开重连），
    // 每个连接都重抓一次整页会让它永远拿不到首包。
    // 斗鱼不参与复用：实测过期直链照样返回 200，但码率掉到 1Mbps 左右。
    // 2026-09-20 实测记录（斗鱼这三个数字别随手改）：
    //   ① 中继启动时解析出的那条斗鱼直链，给**第一条连接**用是好的（curl 3 秒收到 7140178 B）；
    //      同一条链接再给第二条连接用，上游几十毫秒就掐断：日志「上游 200」→ 51ms 后
    //      「准备重连 #1（已转发 0 MB）」→「重连 200」才接上——即老文档说的「一个直链基本只喂一条连接」。
    //   ② 所以斗鱼窗口钉死 0（每条连接重新解析，沿用原设计）。
    //   ③ 纠正一次本轮翻车：曾把这里设成 8 秒，看到斗鱼 0 字节就归咎于复用；事后定位真凶是同批改的
    //      SecurityProtocol=SystemDefault（见 InstallSafetyNets），复用被冤枉了。上面的 ① 才是真证据。
    //   ④ 已知未解：PotPlayer 等首包的耐心约 1.05 秒，而斗鱼每条连接重解析要 1~2 秒。要修得走
    //      「启动时预解析 N 条、每条只发一次」的池子，**不是**复用同一条。
    internal const double RelayUrlReuseSeconds = 20;
    internal const double DouyuRelayUrlReuseSeconds = 0;
    private static string RelayUrlCachedKey;
    private static string RelayUrlCachedUrl;
    private static DateTime RelayUrlCachedAt;
    private static string RelayReplayUnavailableKey;
    private static DateTime RelayReplayUnavailableAt;

    /// 复用窗口由平台适配器给（斗鱼是 0：它的直链只够喂一条连接，复用等于第二条连接收 31KB 就 EOF）。
    private static double RelayUrlReuseWindow(string platform)
    {
        IPlatformAdapter adapter = Platforms.Get(platform);
        return adapter == null ? RelayUrlReuseSeconds : adapter.RelayUrlReuseSeconds;
    }

    // offline = 上游明确"没在播"（虎牙/抖音可明确判定）；false 时可能是重播或临时解析失败。
    private static string ResolveRelayUrl(string platform, string room, bool forceRefresh, out bool offline)
    {
        string cacheKey = platform + ":" + room + ":" + RelayQuality;
        double window = RelayUrlReuseWindow(platform);
        if (!forceRefresh && RelayReplayUnavailableKey == cacheKey
            && (DateTime.UtcNow - RelayReplayUnavailableAt).TotalSeconds < RelayRetrySeconds)
        {
            offline = false;
            return null;
        }
        if (!forceRefresh && window > 0 && RelayUrlCachedUrl != null && RelayUrlCachedKey == cacheKey
            && (DateTime.UtcNow - RelayUrlCachedAt).TotalSeconds < window)
        {
            offline = false;
            return RelayUrlCachedUrl;
        }
        string resolved = ResolveRelayUrlFresh(platform, room, out offline);
        if (resolved != null && GuardRelayUpstream(resolved) == null)
        {
            // 各平台解析器自己也校验过，但「安全靠每个调用点记得校验」迟早会漏 —— 产品出口再兜一次。
            // 离线测试注入的是 RelayResolver（Pump 的参数），不经过这里，本地测试服务器照常能用。
            RelayLog("上游地址未通过安全校验（回环/私网/本地域名），已拒绝本次解析");
            return null;
        }
        if (resolved != null && RelayUrlReuseWindow(platform) > 0)
        {
            RelayReplayUnavailableKey = null;
            RelayUrlCachedKey = cacheKey;
            RelayUrlCachedUrl = resolved;
            RelayUrlCachedAt = DateTime.UtcNow;
        }
        return resolved;
    }

    /// 解析上游直链。2026-09-27 起「哪家怎么解析、能不能判下播」交给适配器（评估报告 §7），
    /// 这里只保留**共用**的编排：解析 → 判下播 → 退 streamlink。
    private static string ResolveRelayUrlFresh(string platform, string room, out bool offline)
    {
        offline = false;
        IPlatformAdapter adapter = Platforms.Get(platform);
        if (adapter == null) return null;
        Target target = Parse(platform + ":" + room, "bili");
        if (target == null) return null;
        target.Quality = RelayQuality;

        PlatformHealth.SetRoom(platform, room);
        PlatformHealth.Record(platform, "playback", "connecting");
        RoomStateKind checkedState = RoomStateKind.Unknown;
        string roomNote = null;
        // 虎牙必须在取流前分清 OFF 与 REPLAY：只有 OFF 拒播，REPLAY 继续尝试三条 CDN。
        // 抖音解析器本身会随同一次房间页解析给出 offline，避免在这里重复抓页。
        if (platform == "huya")
        {
            checkedState = CheckRoomState(platform, room, out roomNote);
            if (checkedState == RoomStateKind.Offline)
            {
                offline = true;
                PlatformHealth.ResolutionResult(platform, null, true);
                return null;
            }
        }
        string failureReason;
        string direct = adapter.ResolveStreamUrl(target, out offline, out failureReason);
        PlatformHealth.ResolutionResult(platform, direct, offline);
        if (direct != null) return direct;
        if (offline) return null;      // 上游明确没在播（能判的平台自己会置位）

        if (adapter.NeedsRelay && !adapter.CanTellOffline)
        {
            // 解析不出直链时问一句「到底在不在播」：明确没播 → 让中转挂起等下播重开，而不是反复重试
            checkedState = CheckRoomState(platform, room, out roomNote);
            if (checkedState == RoomStateKind.Offline)
            { offline = true; PlatformHealth.ResolutionResult(platform, null, true); return null; }
        }
        // 抖音的「没有第二条本地路径、失败才退 streamlink」已经由适配器那条解析覆盖（它自带 offline 判定）
        string url = null;
        try
        {
            if (File.Exists(StreamlinkConsole))
                url = FirstUrl(RunCapture(StreamlinkConsole, "--stream-url \"" + target.Url + "\" best", 30000));
        }
        catch (Exception) { }
        PlatformHealth.ResolutionResult(platform, url, offline);
        if (url == null && checkedState == RoomStateKind.Replay)
        {
            string reason = failureReason ?? roomNote ?? "未解析到可用直链";
            // 状态文件只写人话原因，不写 URL / 响应正文 / 标头；Pump 据此延长重试间隔并让 mpv 显示 OSD。
            reason = reason.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
            PublishRelayState(platform, room, "REPLAY-UNAVAILABLE|" + reason);
            RelayReplayUnavailableKey = platform + ":" + room + ":" + RelayQuality;
            RelayReplayUnavailableAt = DateTime.UtcNow;
        }
        return url;
    }
}
