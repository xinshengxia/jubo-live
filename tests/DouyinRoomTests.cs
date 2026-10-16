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
    // 真 HTTP 响应，传输仅定向到 loopback；使用产品 FetchRoom 的完整调用链。
    private static void TestDouyinRoomRecovery()
    {
        string dir = ReviewDirectory();
        string[] names = { "RuntimeDir", "ScopedGetTransportForTest", "DouyinIpv4Cache", "DouyinIpv4CacheAt", "DouyinNeedsProxy", "DouyinCaptchaBlocked" };
        Dictionary<string, object> saved = new Dictionary<string, object>();
        foreach (string name in names) saved[name] = hub.GetField(name, ReviewStatic).GetValue(null);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool stopping = false; Exception serverFailure = null;
        string normal = DouyinFlight(DouyinRoomJson(2, "验证码帮助", "合成主播", ""), "])")
            + "<script>var user_unique_id=0; var data={\"user_unique_id\":\"761234567890\"};</script> sec_sdk_build";
        string challenge = "<html><title>验证码中间页</title>sec_sdk_build captcha</html>";
        string responseBody = normal;
        Thread server = new Thread(delegate()
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
                        byte[] body = Encoding.UTF8.GetBytes(responseBody);
                        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length
                            + "\r\nSet-Cookie: ttwid=synthetic-response; Domain=.douyin.com; Path=/; Secure\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
                    }
                }
            }
            catch (Exception ex) { if (!stopping) serverFailure = ex; }
        }); server.IsBackground = true; server.Start();
        List<bool> proxy = new List<bool>(); int mode = 0;
        try
        {
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
            hub.GetField("DouyinIpv4Cache", ReviewStatic).SetValue(null, new string[] { "8.8.8.8" });
            hub.GetField("DouyinIpv4CacheAt", ReviewStatic).SetValue(null, DateTime.UtcNow);
            hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinCaptchaBlocked", ReviewStatic).SetValue(null, false);
            hub.GetField("ScopedGetTransportForTest", ReviewStatic).SetValue(null,
                new Func<Uri, Action<HttpWebRequest>, HttpWebResponse>(delegate(Uri logical, Action<HttpWebRequest> configure)
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/fixture");
                    request.Timeout = request.ReadWriteTimeout = 4000; request.AllowAutoRedirect = false;
                    configure(request); proxy.Add(request.Proxy != null);
                    Check(logical.AbsoluteUri == "https://live.douyin.com/46595453118", "使用逻辑平台 URL 选择凭据");
                    Check(request.Headers["Cookie"].StartsWith("__ac_nonce="), "缺少元数据时只发临时 nonce");
                    responseBody = mode == 1 ? (proxy[proxy.Count - 1] ? normal : challenge)
                        : mode == 2 ? challenge : mode == 3 ? new string('x', 4 * 1024 * 1024 + 1) : normal;
                    request.Proxy = null;
                    return (HttpWebResponse)request.GetResponse();
                }));
            Type type = DanmakuType("DouyinDanmakuClient");
            object client = Activator.CreateInstance(type, ReviewInstance, null, new object[] { "46595453118" }, null);
            MethodInfo fetch = type.GetMethod("FetchRoom", ReviewInstance);
            Check((bool)fetch.Invoke(client, null), "有效房间包含验证码文案/SDK 也能进入握手");
            Check(proxy.Count == 1 && !proxy[0], "首次取得有效 IPv4 响应后不再请求域名/代理");
            Check((string)type.GetField("_roomId", ReviewInstance).GetValue(client) == "723910165684", "实际 roomStore 房间 ID 用于签名");
            Check((string)type.GetField("_userUniqueId", ReviewInstance).GetValue(client) == "761234567890", "保留客户端标识");
            Check((string)type.GetField("_ttwid", ReviewInstance).GetValue(client) == "synthetic-response", "本次 Set-Cookie 的 ttwid 用于握手");
            Check(File.ReadAllText(Path.Combine(dir, "health-douyin-danmaku.state.txt")).Split('|')[1] == "connecting", "取得房间后仍待 WebSocket 连接，不能宣称 ready");
            Check(!File.Exists(Path.Combine(dir, "douyin-cookies.txt")), "握手临时 cookie 不落盘");

            mode = 1; proxy.Clear(); hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            Check((bool)fetch.Invoke(client, null), "IPv4/域名均为验证码页时本轮立即回退系统代理");
            Check(proxy.Count == 3 && !proxy[0] && !proxy[1] && proxy[2], "IPv4 → 域名直连 → 代理次序正确");

            mode = 2; proxy.Clear(); hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            Check(!(bool)fetch.Invoke(client, null), "所有路径均为真实验证码页时不连接 WebSocket");
            Check((string)type.GetField("_roomId", ReviewInstance).GetValue(client) == "", "失败不残留上一轮 roomId");
            Check(!(bool)hub.GetField("DouyinCaptchaBlocked", ReviewStatic).GetValue(null), "弹幕失败不改变播放验证流程标志");
            Check(File.ReadAllText(Path.Combine(dir, "health-douyin-danmaku.state.txt")).Split('|')[1] == "auth-required", "真实验证码保留准确状态");
            Check(!File.Exists(Path.Combine(dir, "health-douyin-playback.state.txt")), "弹幕请求不污染播放观测");
            Check(File.ReadAllText(Path.Combine(dir, "logs", "danmaku.log")).Contains("未取得房间信息"), "失败原因进入会话日志");

            mode = 3; proxy.Clear(); hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            Check(!(bool)fetch.Invoke(client, null), "超过 4 MiB 的页面受限，不解析截断正文");
            Check(File.ReadAllText(Path.Combine(dir, "health-douyin-danmaku.state.txt")).Split('|')[1] == "parse-error", "超限可见且归类解析失败");
            Check(serverFailure == null, "合成 HTTP 服务器没有失败");
        }
        finally
        {
            foreach (string name in names) hub.GetField(name, ReviewStatic).SetValue(null, saved[name]);
            stopping = true; listener.Stop(); server.Join(4000); Directory.Delete(dir, true);
        }
    }
}
