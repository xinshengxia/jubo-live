using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private static void RegisterLogicAuditPlaybackCases()
    {
        TestRunner.Add("逻辑回归-播放", "抖音缺失与未知状态不冒充下播", TestLogicDouyinUnknownState);
        TestRunner.Add("逻辑回归-播放", "中转短续接不闪状态且持续失败和真实恢复可见", TestLogicRelayRecoveryState);
    }

    // Runs the production HTTP and parsing paths using only a local synthetic response.
    private sealed class LogicPlaybackServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Thread worker;
        private volatile bool stopping;
        internal Exception Failure;
        internal int Requests;
        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        internal LogicPlaybackServer(Action<int, NetworkStream> reply)
        {
            listener.Start();
            worker = new Thread(delegate()
            {
                try
                {
                    while (!stopping)
                        using (TcpClient client = listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            client.ReceiveTimeout = client.SendTimeout = 5000;
                            ReadRequest(stream);
                            reply(Interlocked.Increment(ref Requests), stream);
                        }
                }
                catch (Exception ex) { if (!stopping) Failure = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
        }
        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            worker.Join(6000);
        }
    }

    private static void LogicPlaybackReply(NetworkStream stream, int status, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Forbidden")
            + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
    }

    private static void TestLogicDouyinUnknownState()
    {
        string dir = ReviewDirectory();
        string[] fields = { "RuntimeDir", "ScopedGetTransportForTest", "DouyinIpv4Cache", "DouyinIpv4CacheAt", "DouyinNeedsProxy", "DouyinCaptchaBlocked", "RelayQuality" };
        var saved = new Dictionary<string, object>();
        foreach (string field in fields) saved[field] = hub.GetField(field, ReviewStatic).GetValue(null);
        const string flv = "\"FULL_HD1\":\"https://pull-flv.example/synthetic.flv\"";
        string page = "";
        using (var server = new LogicPlaybackServer(delegate(int request, NetworkStream stream)
            { LogicPlaybackReply(stream, 200, Encoding.UTF8.GetBytes(page)); }))
        try
        {
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
            hub.GetField("DouyinIpv4Cache", ReviewStatic).SetValue(null, new string[0]);
            hub.GetField("DouyinIpv4CacheAt", ReviewStatic).SetValue(null, DateTime.UtcNow);
            hub.GetField("DouyinNeedsProxy", ReviewStatic).SetValue(null, false);
            hub.GetField("DouyinCaptchaBlocked", ReviewStatic).SetValue(null, false);
            hub.GetField("RelayQuality", ReviewStatic).SetValue(null, "best");
            hub.GetField("ScopedGetTransportForTest", ReviewStatic).SetValue(null,
                new Func<Uri, Action<HttpWebRequest>, HttpWebResponse>(delegate(Uri logical, Action<HttpWebRequest> configure)
                {
                    var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + server.Port + "/fixture");
                    request.Timeout = request.ReadWriteTimeout = 4000;
                    configure(request); request.Proxy = null;
                    return (HttpWebResponse)request.GetResponse();
                }));
            Type adapterType = hub.GetNestedType("DouyinAdapter", BindingFlags.NonPublic);
            object adapter = Activator.CreateInstance(adapterType, true);
            MethodInfo resolve = hub.GetMethod("ResolveDouyinUrl", ReviewStatic, null,
                new[] { typeof(string), typeof(bool).MakeByRefType() }, null);
            foreach (int status in new[] { -1, 0, 5, 4, 2 })
            {
                string json = DouyinRoomJson(status, "合成直播", "合成主播", flv);
                if (status == -1) json = json.Replace("\"status\":-1,", "");
                page = DouyinFlight(json, "])");
                object[] state = { "7777", null };
                string kind = Convert.ToString(adapterType.GetMethod("GetRoomStateKind").Invoke(adapter, state));
                object[] args = { "7777", false };
                string url = (string)resolve.Invoke(null, args);
                Check((bool)args[1] == (status == 4), "抖音仅已知状态4标记下播：" + status);
                Check(kind == (status == 2 ? "Live" : status == 4 ? "Offline" : "Unknown"), "抖音预览保持准确状态：" + status);
                Check(status == 2 ? url == "https://pull-flv.example/synthetic.flv" : url == null,
                    "抖音仅确认直播时返回播放直链：" + status);
                // A successful launch also probes the stream for metadata; keep this case entirely offline.
                if (status == 2) continue;
                string error = null;
                try { Call("PrepareDouyinWithMediaTitle", Call("Parse", "douyin:7777", "bili"), null, null); }
                catch (TargetInvocationException ex) { error = ex.InnerException.Message; }
                Check(error != null && error.Contains(status == 4 ? "现在没有开播" : "房间状态无法确认"),
                    "抖音启动区分未知状态、明确下播与直播：" + status);
                if (status != 2 && status != 4)
                    Check(File.ReadAllText(Path.Combine(dir, "health-douyin-playback.state.txt")).Split('|')[1] == "parse-error",
                        "抖音未知状态归类解析失败：" + status);
            }
            Check(server.Failure == null, "抖音状态合成HTTP服务器正常结束");
        }
        finally
        {
            foreach (string field in fields) hub.GetField(field, ReviewStatic).SetValue(null, saved[field]);
            Directory.Delete(dir, true);
        }
    }

    private static string LogicRelayState(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (IOException) { return ""; }
    }

    private static bool LogicAwaitRelayState(string path, string state, int timeoutMs)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (LogicRelayState(path) == state) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static void TestLogicRelayRecoveryState()
    {
        string dir = ReviewDirectory(), path = Path.Combine(dir, "douyu-status-fixture.relay.txt");
        string[] fields = { "RuntimeDir", "RelayLogFile", "RelaySessionId" };
        var saved = new Dictionary<string, object>();
        foreach (string field in fields) saved[field] = hub.GetField(field, ReviewStatic).GetValue(null);
        var downstream = new TcpListener(IPAddress.Loopback, 0); downstream.Start();
        using (var recover = new ManualResetEvent(false))
        using (var headerSent = new ManualResetEvent(false))
        using (var mediaRelease = new ManualResetEvent(false))
        using (var recoveryHold = new ManualResetEvent(false))
        {
            Thread pump = null;
            string fastReconnectState = null;
            Exception pumpFailure = null;
            var server = new LogicPlaybackServer(delegate(int number, NetworkStream stream)
            {
                if (number <= 2)
                {
                    if (number == 2) fastReconnectState = LogicRelayState(path);
                    LogicPlaybackReply(stream, 200, Join(FlvHeader, VideoTag(number * 1000, (byte)number)));
                }
                else if (!recover.WaitOne(0)) LogicPlaybackReply(stream, 403, new byte[0]);
                else
                {
                    byte[] tag = VideoTag(3000, 3);
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + (FlvHeader.Length + tag.Length) + "\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length); stream.Write(FlvHeader, 0, FlvHeader.Length);
                    stream.Write(tag, 0, 11); headerSent.Set();
                    if (!mediaRelease.WaitOne(5000)) throw new TimeoutException("合成媒体未释放");
                    stream.Write(tag, 11, tag.Length - 11);
                    recoveryHold.WaitOne(5000);
                }
            });
            try
            {
                hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
                hub.GetField("RelayLogFile", ReviewStatic).SetValue(null, Path.Combine(dir, "relay.log"));
                hub.GetField("RelaySessionId", ReviewStatic).SetValue(null, null);
                using (var player = new TcpClient("127.0.0.1", ((IPEndPoint)downstream.LocalEndpoint).Port))
                using (TcpClient client = downstream.AcceptTcpClient())
                {
                    Delegate resolver = RelayResolverFor(new FakeResolver { Port = server.Port });
                    pump = new Thread(delegate() { try { Call("Pump", client, "douyu", "status-fixture", resolver); } catch (Exception ex) { pumpFailure = ex; } });
                    pump.IsBackground = true; pump.Start();
                    NetworkStream stream = player.GetStream(); stream.ReadTimeout = 7000;
                    byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n");
                    stream.Write(request, 0, request.Length); ReadRequest(stream);
                    ReadExactly(stream, FlvHeader.Length + VideoTag(1000, 1).Length + VideoTag(2000, 2).Length);
                    Check(fastReconnectState == "ONLINE", "CDN短续接保留ONLINE，避免反复闪重连提示");
                    Check(LogicAwaitRelayState(path, "RECONNECTING", 7000), "持续403且解析仍有URL时发布重连状态");
                    recover.Set(); Check(headerSent.WaitOne(4000), "恢复连接已收到FLV头和tag头");
                    Thread.Sleep(150);
                    Check(LogicRelayState(path) == "RECONNECTING", "只有FLV头和不完整媒体tag不能冒充恢复");
                    mediaRelease.Set(); ReadExactly(stream, VideoTag(3000, 3).Length);
                    Check(LogicAwaitRelayState(path, "ONLINE", 2000), "完整媒体tag转发后恢复ONLINE");
                    player.Close(); recoveryHold.Set();
                    Check(pump.Join(4000) && pumpFailure == null, "下游断开后中转正常退出，无遗留工作线程");
                    Check(server.Failure == null, "中转状态合成HTTP服务器正常结束");
                }
            }
            finally
            {
                recover.Set(); mediaRelease.Set(); recoveryHold.Set();
                downstream.Stop(); server.Dispose();
                if (pump != null) pump.Join(6000);
                foreach (string field in fields) hub.GetField(field, ReviewStatic).SetValue(null, saved[field]);
                Directory.Delete(dir, true);
            }
        }
    }
}
