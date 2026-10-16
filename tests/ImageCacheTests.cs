// --------------------------------------------------------------------------------------------
// 表情图片下载与位图生命周期的回归（评估报告 §4）。
//
// 报告点出的四件事，每条都在这里钉死：
//   ① 响应体无上限读进内存  → ReadCapped 超过 4 MiB 直接丢；
//   ② 所有平台统一发 B站 Referer + 强制 Proxy=null → 按域给 Referer、不再绕过系统代理；
//   ③ 失败后立刻允许重下（同屏几十条弹幕反复重试）→ 30s/2m/10m 退避；
//   ④ 位图缓存淘汰时一个不释放、一个当场释放 → 统一的延迟释放队列（安全期内仍可用，过期真释放）。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    /// <summary>一个只服务固定响应的小 HTTP 服务器（够 HttpWebRequest 用）。</summary>
    private sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<string, byte[]> _routes = new Dictionary<string, byte[]>();
        private readonly Dictionary<string, string> _redirects = new Dictionary<string, string>();
        private volatile bool _stop;
        private int _hits;

        public TinyHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Thread worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public int Port { get; private set; }
        public int Hits { get { return _hits; } }

        public void Add(string path, byte[] body) { lock (_routes) _routes[path] = body; }
        public void AddRedirect(string path, string location) { lock (_redirects) _redirects[path] = location; }

        private void Loop()
        {
            while (!_stop)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                try { Serve(client); }
                catch (Exception) { }
                finally { try { client.Close(); } catch (Exception) { } }
            }
        }

        private void Serve(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            StringBuilder head = new StringBuilder();
            byte[] one = new byte[1];
            while (true)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0) break;
                head.Append((char)one[0]);
                if (head.Length >= 4 && head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                if (head.Length > 8192) break;
            }
            string request = head.ToString();
            int firstSpace = request.IndexOf(' ');
            int secondSpace = firstSpace < 0 ? -1 : request.IndexOf(' ', firstSpace + 1);
            string path = (firstSpace > 0 && secondSpace > firstSpace)
                ? request.Substring(firstSpace + 1, secondSpace - firstSpace - 1) : "/";
            Interlocked.Increment(ref _hits);

            byte[] body = null;
            string redirect = null;
            lock (_routes) _routes.TryGetValue(path, out body);
            lock (_redirects) _redirects.TryGetValue(path, out redirect);
            if (redirect != null)
            {
                byte[] moved = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + redirect
                    + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(moved, 0, moved.Length);
                return;
            }
            if (body == null)
            {
                byte[] notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(notFound, 0, notFound.Length);
                return;
            }
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch (Exception) { }
        }
    }

    private static byte[] MakePng(int width, int height)
    {
        using (Bitmap bitmap = new Bitmap(width, height))
        using (MemoryStream memory = new MemoryStream())
        {
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
    }

    private static void TestImageCacheHardening(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type cacheType = assembly.GetType("DanmakuImageCache", true);
        Type sharedType = assembly.GetType("SharedBitmap", true);

        // ---- ① 纯策略：退避梯度与 Referer ----
        MethodInfo backoff = cacheType.GetMethod("BackoffFor", flags);
        Check(backoff != null, "图片下载：BackoffFor 存在");
        Check((int)backoff.Invoke(null, new object[] { 1 }) == 30, "图片下载：第 1 次失败退避 30 秒");
        Check((int)backoff.Invoke(null, new object[] { 2 }) == 120, "图片下载：第 2 次失败退避 2 分钟");
        Check((int)backoff.Invoke(null, new object[] { 3 }) == 600, "图片下载：第 3 次失败退避 10 分钟");
        Check((int)backoff.Invoke(null, new object[] { 99 }) == 600, "图片下载：退避有上限，不会无限增长");

        MethodInfo referer = cacheType.GetMethod("RefererFor", flags);
        Check((string)referer.Invoke(null, new object[] { "https://p3-pc-sign.douyinpic.com/obj/x" }) == "https://live.douyin.com/",
            "图片下载：抖音图床用抖音的 Referer（原来错发 B站 的，会被防盗链 403）");
        Check((string)referer.Invoke(null, new object[] { "https://fileserver.cdn.huya.com/a.png" }) == "https://www.huya.com/",
            "图片下载：虎牙图床用虎牙的 Referer");
        Check((string)referer.Invoke(null, new object[] { "https://shark2.douyucdn.cn/a.png" }) == "https://www.douyu.com/",
            "图片下载：斗鱼图床用斗鱼的 Referer");
        Check((string)referer.Invoke(null, new object[] { "https://i0.hdslb.com/a.png" }) == "https://live.bilibili.com/",
            "图片下载：B站 图床用 B站 的 Referer");
        Check((string)referer.Invoke(null, new object[] { "https://evildouyin.com/a.png" }) == "https://evildouyin.com/",
            "图片下载：伪装域不会拿到抖音 Referer（域匹配是相等或子域）");
        Check((string)referer.Invoke(null, new object[] { "https://img.example.com/a.png" }) == "https://img.example.com/",
            "图片下载：陌生域给自己的 origin，不冒充平台");

        MethodInfo safeRemote = assembly.GetType("LiveHub", true).GetMethod("IsSafeRemoteHttpUrl", flags);
        Check(safeRemote != null, "远端 URL：统一安全校验存在");
        Check((bool)safeRemote.Invoke(null, new object[] { "https://cdn.example.com/a.png" }), "远端 URL：公网 HTTPS 放行");
        Check(!(bool)safeRemote.Invoke(null, new object[] { "file:///C:/Windows/win.ini" }), "远端 URL：拒绝 file 协议");
        Check(!(bool)safeRemote.Invoke(null, new object[] { "http://127.0.0.1/admin" }), "远端 URL：拒绝 IPv4 回环 SSRF");
        Check(!(bool)safeRemote.Invoke(null, new object[] { "http://192.168.1.1/admin" }), "远端 URL：拒绝 IPv4 私网 SSRF");
        Check(!(bool)safeRemote.Invoke(null, new object[] { "http://[::1]/admin" }), "远端 URL：拒绝 IPv6 回环 SSRF");
        Check(!(bool)safeRemote.Invoke(null, new object[] { "http://printer.local/admin" }), "远端 URL：拒绝本地域名 SSRF");

        // 2026-09-28：只查字面挡不住「攻击者域名指向 127.0.0.1」——解析结果也要复核
        MethodInfo resolvedGuard = assembly.GetType("LiveHub", true).GetMethod("IsSafeRemoteHttpUrlResolved", flags);
        Check(resolvedGuard != null, "远端 URL：解析复核入口存在");
        if (resolvedGuard != null)
        {
            Func<string, IPAddress[]> loopbackDns = delegate(string host) { return new IPAddress[] { IPAddress.Loopback }; };
            Func<string, IPAddress[]> publicDns = delegate(string host) { return new IPAddress[] { IPAddress.Parse("104.16.2.34") }; };
            Func<string, IPAddress[]> mixedDns = delegate(string host)
            {
                return new IPAddress[] { IPAddress.Parse("104.16.2.34"), IPAddress.Parse("192.168.1.10") };
            };
            Check(!(bool)resolvedGuard.Invoke(null, new object[] { "http://evil.example.com/x", loopbackDns }),
                "远端 URL：域名解析到回环 → 拒绝（DNS 型 SSRF）");
            Check(!(bool)resolvedGuard.Invoke(null, new object[] { "http://evil.example.com/x", mixedDns }),
                "远端 URL：解析结果里只要有一个私网地址就拒绝");
            Check((bool)resolvedGuard.Invoke(null, new object[] { "http://evil.example.com/x", publicDns }),
                "远端 URL：解析到公网地址才放行");
            Check(!(bool)safeRemote.Invoke(null, new object[] { "http://0.1.2.3/x" }), "远端 URL：拒绝 0.0.0.0/8");
            Check(!(bool)safeRemote.Invoke(null, new object[] { "http://100.64.0.1/x" }), "远端 URL：拒绝 CGNAT 100.64/10");
            Check(!(bool)safeRemote.Invoke(null, new object[] { "http://192.0.2.1/x" }), "远端 URL：拒绝 TEST-NET-1");
            Check(!(bool)safeRemote.Invoke(null, new object[] { "http://203.0.113.7/x" }), "远端 URL：拒绝 TEST-NET-3");
            Check(!(bool)safeRemote.Invoke(null, new object[] { "http://169.254.169.254/latest/meta-data/" }),
                "远端 URL：拒绝云元数据地址 169.254.169.254");
            Check((bool)safeRemote.Invoke(null, new object[] { "http://198.18.0.1/x" }),
                "远端 URL：刻意放行 198.18/15（Clash TUN fake-ip，拦了会误杀全部表情图）");
        }

        // 2026-09-28：中转的**初始**上游地址也要过闸（产品出口；离线测试注入 RelayResolver 不经过这里）
        MethodInfo guardRelay = assembly.GetType("LiveHub", true).GetMethod("GuardRelayUpstream", flags);
        Check(guardRelay != null, "中转上游：产品出口安全闸存在");
        if (guardRelay != null)
        {
            Check((string)guardRelay.Invoke(null, new object[] { "http://127.0.0.1:9988/live.flv" }) == null,
                "中转上游：回环地址被拒");
            Check((string)guardRelay.Invoke(null, new object[] { "http://10.1.2.3/live.flv" }) == null,
                "中转上游：私网地址被拒");
            Check((string)guardRelay.Invoke(null, new object[] { "http://8.8.8.8/live.flv" }) == "http://8.8.8.8/live.flv",
                "中转上游：公网地址放行");
        }

        // 2026-09-28：TOCTOU 封口 —— 让「校验用的地址」就是「连接用的地址」
        MethodInfo pinned = assembly.GetType("LiveHub", true).GetMethod("PinnedCandidatesFor", flags);
        Check(pinned != null, "钉扎：候选地址入口存在");
        if (pinned != null)
        {
            Func<string, IPAddress[]> dnsOne = delegate(string host) { return new IPAddress[] { IPAddress.Parse("104.16.2.34") }; };
            Func<string, IPAddress[]> dnsPrivate = delegate(string host) { return new IPAddress[] { IPAddress.Parse("192.168.1.5") }; };
            Func<string, IPAddress[]> dnsMixed = delegate(string host)
            {
                return new IPAddress[] { IPAddress.Parse("192.168.1.5"), IPAddress.Parse("2606:4700::1111"), IPAddress.Parse("104.16.2.34") };
            };
            object[] call;
            string[] cand;

            call = new object[] { "https://evil.example.com/x?a=1", dnsOne, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 1 && cand[0] == "https://104.16.2.34/x?a=1" && (string)call[3] == "evil.example.com",
                "钉扎：连接目标换成校验过的 IP，路径/查询保留，Host 仍是原域名（实得 " + (cand.Length > 0 ? cand[0] : "(空)") + "）");

            call = new object[] { "https://evil.example.com:8443/x", dnsOne, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 1 && cand[0] == "https://104.16.2.34:8443/x", "钉扎：非默认端口保留");

            call = new object[] { "https://evil.example.com/x", dnsMixed, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 2 && cand[0].IndexOf("104.16.2.34", StringComparison.Ordinal) > 0
                && cand[1].IndexOf("[2606:4700::1111]", StringComparison.Ordinal) > 0,
                "钉扎：私网地址被剔掉、IPv4 优先（候选 " + cand.Length + " 个）");

            call = new object[] { "https://evil.example.com/x", dnsPrivate, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 0, "钉扎：域名只解析到私网 → 没有候选，调用方必须拒绝连接");

            call = new object[] { "http://127.0.0.1:9988/x", dnsOne, true, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 1 && cand[0] == "http://127.0.0.1:9988/x" && call[3] == null,
                "钉扎：信任的字面地址原样连（离线测试走本地服务器的那条路）");

            call = new object[] { "http://127.0.0.1:9988/x", dnsOne, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 0, "钉扎：不信任的字面回环地址被拒");

            call = new object[] { "http://104.16.2.34/x", dnsOne, false, null };
            cand = (string[])pinned.Invoke(null, call);
            Check(cand.Length == 1 && cand[0] == "http://104.16.2.34/x" && call[3] == null,
                "钉扎：公网字面地址原样连（本想连它，不需要 Host 覆盖）");
        }

        MethodInfo buildPinned = assembly.GetType("LiveHub", true).GetMethod("BuildPinnedUrl", flags);
        Check(buildPinned != null, "钉扎：连接 URL 拼装入口存在");
        if (buildPinned != null)
        {
            string v6 = (string)buildPinned.Invoke(null, new object[] { new Uri("https://h/p"), IPAddress.Parse("2606:4700::1111") });
            Check(v6 == "https://[2606:4700::1111]/p", "钉扎：IPv6 地址加方括号（实得 " + v6 + "）");
            string withPort = (string)buildPinned.Invoke(null, new object[] { new Uri("https://h:8443/p"), IPAddress.Parse("104.16.2.34") });
            Check(withPort == "https://104.16.2.34:8443/p", "钉扎：非默认端口原样带过去（实得 " + withPort + "）");
            string noPath = (string)buildPinned.Invoke(null, new object[] { new Uri("https://h"), IPAddress.Parse("104.16.2.34") });
            Check(noPath == "https://104.16.2.34/", "钉扎：没有路径时补 /（实得 " + noPath + "）");
        }

        // ---- ② 带上限的读取 ----
        MethodInfo readCapped = cacheType.GetMethod("ReadCapped", flags);
        object kept = readCapped.Invoke(null, new object[] { new MemoryStream(new byte[3]), 4 });
        Check(kept is byte[] && ((byte[])kept).Length == 3, "图片下载：上限内正常读完");
        object overflow = readCapped.Invoke(null, new object[] { new MemoryStream(new byte[10]), 4 });
        Check(overflow == null, "图片下载：超过上限返回 null（不再无限制读进内存）");

        // ---- ③ 端到端：本地 HTTP 服务器 ----
        using (TinyHttpServer server = new TinyHttpServer())
        {
            server.Add("/small.png", MakePng(4, 4));
            server.Add("/huge.png", MakePng(2100, 2));
            server.Add("/big.bin", new byte[5 * 1024 * 1024]);
            object cache = Activator.CreateInstance(cacheType);
            // FetchNow 是**实例**方法：flags 里带 Static 是找不到它的（返回 null → NRE）
            MethodInfo fetch = cacheType.GetMethod("FetchNow", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo tryGet = cacheType.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Instance);
            // 同样：RequestCount / BackoffCount 是**实例**属性（flags 里带 Static 会返回 null）
            PropertyInfo requests = cacheType.GetProperty("RequestCount", BindingFlags.NonPublic | BindingFlags.Instance);
            string root = "http://127.0.0.1:" + server.Port;

            server.AddRedirect("/redirect", root + "/small.png");
            MethodInfo openRelay = assembly.GetType("LiveHub", true).GetMethod("OpenRelayUpstream", flags);
            Check(openRelay != null, "中转下载：逐跳重定向校验入口存在");
            bool redirectBlocked = false;
            try { openRelay.Invoke(null, new object[] { root + "/redirect", "douyu" }); }
            catch (TargetInvocationException ex) { redirectBlocked = ex.InnerException is InvalidDataException; }
            Check(redirectBlocked && server.Hits == 1,
                "中转下载：重定向到回环地址在第二跳前被拒绝（没有访问目标）");

            // 2026-09-28：表情图那条路原来直接 AllowAutoRedirect=false（挡住 302 回环，但合法
            // CDN 的 302 也一起失效）。现在改成逐跳校验：守卫还在，合法重定向照样跟随。
            server.AddRedirect("/img-redirect", root + "/small.png");
            MethodInfo openImage = cacheType.GetMethod("OpenImageUpstream", flags);
            Check(openImage != null, "图片下载：逐跳重定向校验入口存在");
            int beforeImageRedirect = server.Hits;
            bool imageRedirectBlocked = false;
            try { openImage.Invoke(null, new object[] { root + "/img-redirect" }); }
            catch (TargetInvocationException ex) { imageRedirectBlocked = ex.InnerException is InvalidDataException; }
            Check(imageRedirectBlocked && server.Hits == beforeImageRedirect + 1,
                "图片下载：重定向到回环地址在第二跳前被拒绝（没有访问目标）");

            int beforeBlocked = server.Hits;
            object blocked = tryGet.Invoke(cache, new object[] { root + "/small.png" });
            Thread.Sleep(100);
            Check(blocked == null && server.Hits == beforeBlocked, "图片下载：产品入口不会请求回环地址（SSRF 被拦）");

            object ok = fetch.Invoke(cache, new object[] { root + "/small.png" });
            Check(ok != null && ok.GetType() == sharedType, "图片下载：本地服务器上的正常 PNG 能下到");
            if (ok != null)
            {
                Check(BitmapOf(ok).Width == 4, "图片下载：下到的图尺寸正确");
                object again2 = tryGet.Invoke(cache, new object[] { root + "/small.png" });
                Check(object.ReferenceEquals(again2, ok),
                    "图片下载：第二次取走缓存，不重复下载（拿到同一个共享位图）");
                ReleaseShared(again2);   // 借出来的要还
            }

            object huge = fetch.Invoke(cache, new object[] { root + "/huge.png" });
            Check(huge == null, "图片下载：解码后尺寸超过 2048 的图被丢弃（解码炸弹）");

            long before = (long)requests.GetValue(cache, null);
            object big = fetch.Invoke(cache, new object[] { root + "/big.bin" });
            Check(big == null, "图片下载：5 MiB 响应体被丢弃（上限 4 MiB）");
            long afterFirst = (long)requests.GetValue(cache, null);
            Check(afterFirst == before + 1, "图片下载：这次失败确实发过一次请求");
            object again = fetch.Invoke(cache, new object[] { root + "/big.bin" });
            long afterSecond = (long)requests.GetValue(cache, null);
            Check(again == null && afterSecond == afterFirst,
                "图片下载：退避期内不再发起请求（原来是同屏几十条弹幕反复重试）");
            Check((int)cacheType.GetProperty("BackoffCount", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cache, null) >= 1,
                "图片下载：失败 URL 进了退避表");
        }

        // ---- ④ 所有权：淘汰只是「缓存放手」，借出去的那份照样有效 ----
        // （原来是「延迟释放队列 + 安全期」，见 docs\decisions\009-位图生命周期.md —— 安全期是猜的，
        //   热门房间一撞上硬上限就把还在飞的位图放掉，于是有了那次 Image.get_Width 崩溃。）
        // 构造函数是 internal → 必须显式给 NonPublic（只给类型数组是找不到的）
        ConstructorInfo sharedCtor = sharedType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Bitmap) }, null);
        MethodInfo acquire = sharedType.GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo release = sharedType.GetMethod("Release", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo dropFromCache = sharedType.GetMethod("DropFromCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(sharedCtor != null && acquire != null && release != null && dropFromCache != null,
            "位图所有权：SharedBitmap 暴露 Acquire/Release/DropFromCache（否则这条是空转）");
        if (sharedCtor != null && acquire != null && release != null && dropFromCache != null)
        {
            object shared = sharedCtor.Invoke(new object[] { new Bitmap(8, 8) });
            acquire.Invoke(shared, null);                      // 使用者先借一份
            dropFromCache.Invoke(shared, null);                // 缓存再淘汰（顺序反了就没意义了）
            Check(!IsFreed(shared), "位图所有权：缓存淘汰后，借出去的那份仍然可用（正在飞的弹幕还在画它）");
            release.Invoke(shared, null);                      // 使用者放手
            Check(IsFreed(shared), "位图所有权：最后一份还回来之后立刻释放（不需要等安全期）");

            object unused = sharedCtor.Invoke(new object[] { new Bitmap(8, 8) });
            dropFromCache.Invoke(unused, null);
            Check(IsFreed(unused), "位图所有权：没借出去过的一淘汰就释放（不留孤儿）");
        }

        // ---- ⑤ 图片缓存本身也要有界：灌 1200 张（容量 200）不许无界增长 ----
        // 走 private Complete(url, bitmap)：它就是下载成功后的入库路径（含淘汰），
        // 这样不用真发 1200 次请求就能压容量上限这条线。
        object cache2 = Activator.CreateInstance(cacheType);
        MethodInfo complete = cacheType.GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(complete != null, "图片缓存：Complete 入库路径可见（否则这条是空转）");
        if (complete != null)
        {
            for (int i = 0; i < 1200; i++)
                complete.Invoke(cache2, new object[] { "http://example.invalid/pic" + i + ".png", new Bitmap(2, 2) });
            int items = (int)cacheType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance).GetValue(cache2, null);
            Check(items <= 200, "图片缓存：容量上限生效（1200 张灌进去，缓存里实得 " + items + " ≤ 200）");
        }
    }
}
