using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private static string ReadCdpFixtureHeaders(Stream stream)
    {
        StringBuilder text = new StringBuilder();
        while (text.Length < 8192)
        {
            int next = stream.ReadByte(); if (next < 0) throw new IOException("HTTP 请求截断"); text.Append((char)next);
            if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") return text.ToString();
        }
        throw new IOException("HTTP 请求超限");
    }

    private static byte[] ReadCdpFixtureBytes(Stream stream, int count)
    {
        byte[] bytes = new byte[count]; int at = 0;
        while (at < count) { int read = stream.Read(bytes, at, count - at); if (read == 0) throw new IOException("WebSocket 帧截断"); at += read; }
        return bytes;
    }

    private static void TestLocalCdpCookies()
    {
        Type guard = DanmakuType("DouyinGuard"), login = DanmakuType("PlatformLogin");
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic); object oldRuntime = runtime.GetValue(null);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int mode = 0, discoveries = 0, commands = 0; bool stopping = false, leakedCookie = false;
        Exception serverFailure = null;
        string json = "{\"id\":1,\"result\":{\"cookies\":["
            + "{\"name\":\"sessionid\",\"value\":\"synthetic-douyin\",\"domain\":\".douyin.com\",\"path\":\"/\",\"secure\":true,\"expires\":4102444800},"
            + "{\"name\":\"udb_uid\",\"value\":\"synthetic-huya\",\"domain\":\".huya.com\",\"path\":\"/\",\"secure\":true,\"expires\":4102444800},"
            + "{\"name\":\"dy_auth\",\"value\":\"synthetic-douyu\",\"domain\":\".douyu.com\",\"path\":\"/\",\"secure\":true,\"expires\":4102444800}]}}";
        Thread server = new Thread(delegate()
        {
            try
            {
                while (!stopping) using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = client.SendTimeout = 4000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadCdpFixtureHeaders(stream);
                        leakedCookie |= request.IndexOf("\r\nCookie:", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (request.StartsWith("GET /json/list "))
                        {
                            discoveries++;
                            string socketUrl = mode == 2 ? "ws://www.example.com/devtools/page/remote" : "ws://127.0.0.1:" + port + "/devtools/page/synthetic";
                            string body = mode == 3 ? new string('x', 1024 * 1024 + 1)
                                : "[{\"type\":\"page\",\"url\":\"https://live.douyin.com/\",\"webSocketDebuggerUrl\":\"" + socketUrl + "\"}]";
                            byte[] bytes = Encoding.UTF8.GetBytes(body);
                            string head = mode == 1 ? "HTTP/1.1 302 Found\r\nLocation: https://www.example.com/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                : "HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
                            byte[] header = Encoding.ASCII.GetBytes(head); stream.Write(header, 0, header.Length);
                            if (mode != 1) stream.Write(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            string key = null;
                            foreach (string line in request.Split(new string[] { "\r\n" }, StringSplitOptions.None))
                                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
                            if (key == null) throw new IOException("缺少 WebSocket key");
                            string accept;
                            using (SHA1 hash = SHA1.Create()) accept = Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                            stream.Write(head, 0, head.Length);
                            byte[] frame = ReadCdpFixtureBytes(stream, 2); int length = frame[1] & 127;
                            if (length == 126) { byte[] extended = ReadCdpFixtureBytes(stream, 2); length = extended[0] * 256 + extended[1]; }
                            if (length > 8192 || (frame[1] & 128) == 0) throw new IOException("客户端帧无效");
                            byte[] mask = ReadCdpFixtureBytes(stream, 4), payload = ReadCdpFixtureBytes(stream, length);
                            for (int i = 0; i < length; i++) payload[i] ^= mask[i % 4];
                            if (!Encoding.UTF8.GetString(payload).Contains("Storage.getCookies")) throw new IOException("必须读取完整浏览器上下文，不能只读取当前页面Cookie");
                            commands++;
                            byte[] response = Encoding.UTF8.GetBytes(json);
                            byte[] frameHead = { 0x81, 126, (byte)(response.Length >> 8), (byte)response.Length };
                            stream.Write(frameHead, 0, frameHead.Length); stream.Write(response, 0, response.Length);
                        }
                    }
                }
            }
            catch (Exception ex) { if (!stopping) serverFailure = ex; }
        }); server.IsBackground = true; server.Start();
        try
        {
            runtime.SetValue(null, dir);
            foreach (string platformId in new string[] { "douyin", "huya", "douyu" })
            {
                string cookieJson = (string)ReviewCall(guard, "CdpCallCookiesJson", port);
                Check(cookieJson == json, platformId + "：通过本机 HTTP 发现和真实 WebSocket 读到 CDP 响应");
                object platform = ReviewCall(login, "Get", platformId);
                string raw = (string)ReviewCall(guard, "ExtractPlatformCookieHeader", cookieJson, platformId);
                MethodInfo save = login.GetMethod("Save", ReviewStatic, null, new Type[] { platform.GetType(), typeof(string), typeof(string) }, null);
                Check((bool)save.Invoke(null, new object[] { platform, raw, cookieJson }), platformId + "：保存实际 CDP 返回的合成凭据");
                string path = Path.Combine(dir, platformId + "-cookies.txt");
                Check(File.Exists(path + ".json"), platformId + "：生成作用域元数据");
                string header = (string)ReviewCall(login, "CookieHeaderForUri", new Uri("https://live." + platformId + ".com/"));
                Check(header == raw && !string.IsNullOrEmpty(header), platformId + "：后续平台请求可选中登录 cookie");
                Check((string)ReviewCall(login, "CookieHeaderForUri", new Uri("http://live." + platformId + ".com/")) == "", platformId + "：Secure 约束仍生效");
            }
            Check(commands == 3 && discoveries == 3 && !leakedCookie, "三平台完整读取链路完成，CDP 请求不带平台 Cookie");
            mode = 1; Check(ReviewCall(guard, "CdpCallCookiesJson", port) == null, "本机 CDP 重定向被拒绝，不跳转公网");
            mode = 2; Check(ReviewCall(guard, "CdpCallCookiesJson", port) == null && commands == 3, "拒绝连接远端 debugger WebSocket");
            mode = 0;
            Check(ReviewCall(guard, "CdpCall", port, "https://other.example.com/", "{}") == null && commands == 3, "指定页面域名未命中时不退到无关页面");
            mode = 3; Check(ReviewCall(guard, "CdpCallCookiesJson", port) == null, "本机列表超过 1 MiB 时拒绝");
            Check(serverFailure == null, "合成 CDP 服务没有失败");
            listener.Stop(); server.Join(4000);
            Check(ReviewCall(guard, "CdpCallCookiesJson", port) == null, "Edge 尚未就绪/已退出时返回空，登录轮询可继续");
            bool rejected = false;
            try { Call("HttpGetText", "http://127.0.0.1:" + port + "/json/list", null); }
            catch (TargetInvocationException) { rejected = true; }
            Check(rejected, "公网 HTTP 出口仍拒绝回环，不扩大安全例外");
        }
        finally
        {
            runtime.SetValue(null, oldRuntime); stopping = true; listener.Stop(); server.Join(4000); Directory.Delete(dir, true);
        }
    }
}
