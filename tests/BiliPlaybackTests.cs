using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private static Dictionary<string, object> BiliFixtureFormat(string format, string host)
    {
        var route = new Dictionary<string, object> { { "host", host }, { "extra", "?fixture=1" } };
        var codec = new Dictionary<string, object> { { "codec_name", "avc" }, { "base_url", "/live" },
            { "url_info", new[] { route } } };
        return new Dictionary<string, object> { { "format_name", format }, { "codec", new[] { codec } } };
    }

    private static void TestBiliPlaybackUrl()
    {
        var formats = new[] { BiliFixtureFormat("ts", "https://198.18.0.2"),
            BiliFixtureFormat("flv", "http://127.0.0.1"), BiliFixtureFormat("flv", "https://198.18.0.1") };
        var stream = new Dictionary<string, object> { { "format", formats } };
        var play = new Dictionary<string, object> { { "stream", new[] { stream } } };
        var info = new Dictionary<string, object> { { "playurl", play } };
        var data = new Dictionary<string, object> { { "playurl_info", info } };
        var root = new Dictionary<string, object> { { "data", data }, { "code", 0 } };
        Check((string)Call("BiliPlaybackUrl", root, false) == "https://198.18.0.1/live?fixture=1",
            "优先选择公网 FLV/AVC，保留签名参数");
        stream["format"] = new[] { formats[0] };
        Check(Call("BiliPlaybackUrl", root, true) == null && Call("BiliPlaybackUrl", root, false) != null,
            "分片流走直连，不交给 FLV 中转");
        root["code"] = -1;
        Check(Call("BiliPlaybackUrl", root, false) == null, "平台拒绝时交给备用解析器");
    }

    private static string ConfirmOffline(out bool offline) { offline = true; return null; }

    private static void TestStopAfterOffline()
    {
        string directory = ReviewDirectory();
        var terminal = hub.GetField("relayTerminalOffline", Flags);
        var wait = hub.GetField("relayWaitAfterOffline", Flags);
        var runtime = hub.GetField("RuntimeDir", Flags);
        var session = hub.GetField("RelaySessionId", Flags);
        object oldWait = wait.GetValue(null), oldTerminal = terminal.GetValue(null), oldRuntime = runtime.GetValue(null), oldSession = session.GetValue(null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            runtime.SetValue(null, directory); session.SetValue(null, null);
            wait.SetValue(null, false); terminal.SetValue(null, false);
            var resolver = Delegate.CreateDelegate(hub.GetNestedType("RelayResolver", BindingFlags.NonPublic),
                typeof(HardeningTests).GetMethod("ConfirmOffline", Flags));
            listener.Start();
            foreach (string status in new[] { "200 OK", "410 Gone" })
            using (var player = new TcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port))
            {
                player.ReceiveTimeout = 3000;
                byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\n\r\n");
                player.GetStream().Write(request, 0, request.Length);
                using (var server = listener.AcceptTcpClient()) Call("Pump", server, "bili", "fixture", resolver);
                using (var reader = new StreamReader(player.GetStream())) Check(reader.ReadLine() == "HTTP/1.1 " + status, "下播后会话响应 " + status);
            }
        }
        finally
        {
            listener.Stop(); terminal.SetValue(null, oldTerminal); wait.SetValue(null, oldWait);
            runtime.SetValue(null, oldRuntime); session.SetValue(null, oldSession);
            Directory.Delete(directory, true);
        }
    }
}
