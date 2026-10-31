using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    // 抖音限流空壳页（2026-10-26）：
    // 抖音在短时间被大量抓房间页时，会返回「RSC 框架 + roomStore 都在、但 roomInfo 里没有 room」
    // 的空壳页（和「房间不存在」同模板）。旧逻辑把它显示成「抓不到房间页（网络或页面改版）」，
    // 这里钉住三件事：① 能识别空壳页；② 空壳页后重试一次能恢复、连续空壳页给「限流」提示；
    // ③ TUN 假 IP 不再去连 IPv4 字面量（没有 SNI，TLS 会被掐断）。
    private static void RegisterDouyinRateLimitCases()
    {
        TestRunner.Add("抖音限流", "空壳页识别：有 roomStore 但没 room", TestDouyinShellDetection);
        TestRunner.Add("抖音限流", "空壳页重试与限流提示", TestDouyinShellRetry);
        TestRunner.Add("抖音限流", "连续限流进入冷却：冷却期内不再发请求", TestDouyinShellCooldown);
        TestRunner.Add("抖音限流", "TUN 假 IP 跳过 IPv4 字面量", TestDouyinFakeAddressFilter);
    }

    private static void TestDouyinShellDetection()
    {
        string shell = DouyinFlight("7:{\"state\":{\"roomStore\":{\"roomInfo\":{"
            + "\"web_rid\":\"123456789012\",\"web_stream_url\":\"\"}}}}", "])");
        Check(Call("ParseDouyinRoom", shell) == null, "空壳页不产出房间信息");
        Check((bool)Call("DouyinPageMissingRoom", shell), "识别空壳页：roomStore 有、room 缺失");

        string valid = DouyinFlight(DouyinRoomJson(4, "没开播", "合成主播", ""), "])");
        Check(Call("ParseDouyinRoom", valid) != null, "正常页面仍能解析");
        Check(!(bool)Call("DouyinPageMissingRoom", valid), "正常页面不会被误判成空壳页");
        Check(!(bool)Call("DouyinPageMissingRoom", "<html><body>nothing</body></html>"), "垃圾页不算空壳页");
        Check(!(bool)Call("DouyinPageMissingRoom", ""), "空页面不算空壳页");

        // RSC 数组（model）形态：state 挂在数组第 4 项里，解析器同样要看得到
        string model = DouyinFlight("7:[\"$\",\"$L3\",null,{\"state\":{\"roomStore\":"
            + "{\"roomInfo\":{\"web_rid\":\"1\"}}}}]", "])");
        Check((bool)Call("DouyinPageMissingRoom", model), "RSC 数组形态的空壳页也能识别");
    }

    private static void TestDouyinShellRetry()
    {
        string dir = ReviewDirectory();
        string[] names = { "RuntimeDir", "ScopedGetTransportForTest", "DouyinIpv4Cache", "DouyinIpv4CacheAt",
            "DouyinNeedsProxy", "DouyinCaptchaBlocked", "DouyinLastFailure", "DouyinShellSeenAt",
            "DouyinShellRetryDelayMs", "DouyinMaxInFlight", "DouyinFetchSpacingMs", "DouyinRoomCacheSeconds",
            "DouyinLiteralBroken", "DouyinLiteralFailures", "DouyinCooldownMs", "DouyinCooldownUntil", "DouyinShellStreak" };
        Dictionary<string, object> saved = new Dictionary<string, object>();
        foreach (string name in names) saved[name] = hub.GetField(name, ReviewStatic).GetValue(null);

        string shellHtml = DouyinFlight("7:{\"state\":{\"roomStore\":{\"roomInfo\":"
            + "{\"web_rid\":\"123456789012\"}}}}", "])");
        string normalHtml = DouyinFlight(DouyinRoomJson(4, "没开播", "合成主播", ""), "])");
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stopping = false; Exception serverFailure = null;
        int calls = 0, mode = 0;   // mode 0 = 第 3 次起给正常页；mode 1 = 一直给空壳页
        object serverGate = new object();
        Thread server = null;
        server = new Thread(delegate()
        {
            try
            {
                while (!stopping) using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = client.SendTimeout = 4000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        int previous = -1, breaks = 0, count = 0;
                        while (count++ < 8192)
                        {
                            int value = stream.ReadByte(); if (value < 0) throw new IOException("请求截断");
                            if (value == 10 && previous == 13) breaks++; else if (value != 13) breaks = 0;
                            previous = value; if (breaks == 2) break;
                        }
                        string body;
                        lock (serverGate) { calls++; body = (mode == 0 && calls >= 3) ? normalHtml : shellHtml; }
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length
                            + "\r\nSet-Cookie: ttwid=synthetic-response; Domain=.douyin.com; Path=/; Secure\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) { if (!stopping) serverFailure = ex; }
        }); server.IsBackground = true; server.Start();

        try
        {
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
            hub.GetField("DouyinIpv4Cache", ReviewStatic).SetValue(null, new string[0]);   // 只走域名/代理两跳
            hub.GetField("DouyinIpv4CacheAt", ReviewStatic).SetValue(null, DateTime.UtcNow);
            hub.GetField("DouyinShellRetryDelayMs", ReviewStatic).SetValue(null, 5);
            hub.GetField("DouyinMaxInFlight", ReviewStatic).SetValue(null, 2);
            hub.GetField("DouyinFetchSpacingMs", ReviewStatic).SetValue(null, 0);
            hub.GetField("DouyinRoomCacheSeconds", ReviewStatic).SetValue(null, 4);
            hub.GetField("DouyinLiteralBroken", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinLiteralFailures", ReviewStatic).SetValue(null, 0);
            hub.GetField("ScopedGetTransportForTest", ReviewStatic).SetValue(null,
                new Func<Uri, Action<HttpWebRequest>, HttpWebResponse>(delegate(Uri logical, Action<HttpWebRequest> configure)
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/fixture");
                    request.Timeout = request.ReadWriteTimeout = 4000; request.AllowAutoRedirect = false;
                    configure(request);
                    request.Proxy = null;
                    return (HttpWebResponse)request.GetResponse();
                }));
            MethodInfo fetch = hub.GetMethod("FetchDouyinRoom", ReviewStatic, null, new Type[] { typeof(string) }, null);

            mode = 0; calls = 0;
            Call("ResetDouyinFetchStateForTest");
            object room = fetch.Invoke(null, new object[] { "123456789012" });
            Check(room != null && Equals(DouyinField(room, "Status"), 4), "空壳页后重试一次即拿到房间状态");
            Check(calls == 3, "空壳页重试只多抓一轮（实得请求数 " + calls + "）");
            Check((string)Call("DouyinFailureNote") == "抓不到房间页（网络或页面改版）",
                "重试成功后不再提示限流（实得 " + Call("DouyinFailureNote") + "）");

            mode = 1; calls = 0;
            Call("ResetDouyinFetchStateForTest");
            object failed = fetch.Invoke(null, new object[] { "123456789012" });
            Check(failed == null, "连续空壳页时不产出房间信息（不猜状态）");
            string note = (string)Call("DouyinFailureNote");
            Check(note != null && note.Contains("限流"), "连续空壳页的提示是临时限流（实得：" + note + "）");
            Check(File.ReadAllText(Path.Combine(dir, "health-douyin-playback.state.txt")).Split('|')[1] == "rate-limited",
                "空壳页的观测状态标记为 rate-limited");
            Check(serverFailure == null, "合成 HTTP 服务器没有失败");
        }
        finally
        {
            foreach (string name in names) hub.GetField(name, ReviewStatic).SetValue(null, saved[name]);
            stopping = true; listener.Stop(); server.Join(4000); Directory.Delete(dir, true);
        }
    }

    private static void TestDouyinShellCooldown()
    {
        string dir = ReviewDirectory();
        string[] names = { "RuntimeDir", "ScopedGetTransportForTest", "DouyinIpv4Cache", "DouyinIpv4CacheAt",
            "DouyinNeedsProxy", "DouyinCaptchaBlocked", "DouyinLastFailure", "DouyinShellSeenAt",
            "DouyinShellRetryDelayMs", "DouyinMaxInFlight", "DouyinFetchSpacingMs", "DouyinRoomCacheSeconds",
            "DouyinLiteralBroken", "DouyinLiteralFailures", "DouyinCooldownMs" };
        Dictionary<string, object> saved = new Dictionary<string, object>();
        foreach (string name in names) saved[name] = hub.GetField(name, ReviewStatic).GetValue(null);

        string shellHtml = DouyinFlight("7:{\"state\":{\"roomStore\":{\"roomInfo\":"
            + "{\"web_rid\":\"123456789012\"}}}}", "])");
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stopping = false; Exception serverFailure = null;
        int calls = 0;
        object serverGate = new object();
        Thread server = null;
        server = new Thread(delegate()
        {
            try
            {
                while (!stopping) using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = client.SendTimeout = 4000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        int previous = -1, breaks = 0, count = 0;
                        while (count++ < 8192)
                        {
                            int value = stream.ReadByte(); if (value < 0) throw new IOException("请求截断");
                            if (value == 10 && previous == 13) breaks++; else if (value != 13) breaks = 0;
                            previous = value; if (breaks == 2) break;
                        }
                        lock (serverGate) calls++;
                        byte[] bytes = Encoding.UTF8.GetBytes(shellHtml);
                        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length
                            + "\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) { if (!stopping) serverFailure = ex; }
        }); server.IsBackground = true; server.Start();

        try
        {
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
            hub.GetField("DouyinIpv4Cache", ReviewStatic).SetValue(null, new string[0]);
            hub.GetField("DouyinIpv4CacheAt", ReviewStatic).SetValue(null, DateTime.UtcNow);
            hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinCaptchaBlocked", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinShellRetryDelayMs", ReviewStatic).SetValue(null, 5);
            hub.GetField("DouyinMaxInFlight", ReviewStatic).SetValue(null, 2);
            hub.GetField("DouyinFetchSpacingMs", ReviewStatic).SetValue(null, 0);
            hub.GetField("DouyinRoomCacheSeconds", ReviewStatic).SetValue(null, 0);
            hub.GetField("DouyinCooldownMs", ReviewStatic).SetValue(null, 150);
            hub.GetField("DouyinLiteralBroken", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinLiteralFailures", ReviewStatic).SetValue(null, 0);
            hub.GetField("ScopedGetTransportForTest", ReviewStatic).SetValue(null,
                new Func<Uri, Action<HttpWebRequest>, HttpWebResponse>(delegate(Uri logical, Action<HttpWebRequest> configure)
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/fixture");
                    request.Timeout = request.ReadWriteTimeout = 4000; request.AllowAutoRedirect = false;
                    configure(request);
                    request.Proxy = null;
                    return (HttpWebResponse)request.GetResponse();
                }));
            MethodInfo fetch = hub.GetMethod("FetchDouyinRoom", ReviewStatic, null, new Type[] { typeof(string) }, null);

            Call("ResetDouyinFetchStateForTest");
            object first = fetch.Invoke(null, new object[] { "123456789012" });
            Check(first == null, "连续空壳页不产出房间信息");
            Check(calls == 4, "首次空壳页：先取一次 + 重试一次（实得请求数 " + calls + "）");
            object second = fetch.Invoke(null, new object[] { "123456789012" });
            Check(second == null && calls == 4, "冷却期内不再发请求（实得请求数 " + calls + "）");
            Thread.Sleep(220);
            object third = fetch.Invoke(null, new object[] { "123456789012" });
            Check(third == null && calls == 6, "冷却结束后只再取一轮、不再做立即重试（实得请求数 " + calls + "）");
            string note = (string)Call("DouyinFailureNote");
            Check(note != null && note.Contains("限流"), "冷却期间提示仍是临时限流（实得：" + note + "）");
            Check(serverFailure == null, "合成 HTTP 服务器没有失败");
        }
        finally
        {
            foreach (string name in names) hub.GetField(name, ReviewStatic).SetValue(null, saved[name]);
            stopping = true; listener.Stop(); server.Join(4000); Directory.Delete(dir, true);
        }
    }

    private static void TestDouyinFakeAddressFilter()
    {
        Check((bool)Call("DouyinIsFakeAddress", IPAddress.Parse("198.18.0.208")), "198.18/16 识别为 TUN 假 IP");
        Check((bool)Call("DouyinIsFakeAddress", IPAddress.Parse("198.19.255.1")), "198.19/16 识别为 TUN 假 IP");
        Check(!(bool)Call("DouyinIsFakeAddress", IPAddress.Parse("36.156.33.220")), "公网 CDN IPv4 不算假 IP");
        Check(!(bool)Call("DouyinIsFakeAddress", IPAddress.Parse("2001:2::b9")), "IPv6 假地址不属于 IPv4 判断");
    }
}
