using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private static void TestDisconnectDuringResolveFailure()
    {
        string dir = ReviewDirectory(); object oldRuntime = hub.GetField("RuntimeDir", Flags).GetValue(null);
        object oldLog = hub.GetField("RelayLogFile", Flags).GetValue(null);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, dir);
            string log = Path.Combine(dir, "relay.log"); hub.GetField("RelayLogFile", Flags).SetValue(null, log);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var player = new TcpClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port))
            using (TcpClient relay = listener.AcceptTcpClient())
            {
                var resolver = new FakeResolver { FailWithoutOffline = true, ResolveEntered = entered, ResolveRelease = release };
                Exception failure = null;
                var worker = new Thread(delegate() { try { Call("Pump", relay, "douyin", "7777", RelayResolverFor(resolver)); } catch (Exception ex) { failure = ex; } });
                worker.IsBackground = true; worker.Start();
                try
                {
                    byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n");
                    player.GetStream().Write(request, 0, request.Length);
                    Check(entered.WaitOne(3000), "播放器连接保持到上游解析开始");
                    player.Close(); Stopwatch clock = Stopwatch.StartNew();
                    while (!(relay.Client.Poll(1000, SelectMode.SelectRead) && relay.Client.Available == 0) && clock.ElapsedMilliseconds < 1000) Thread.Sleep(10);
                    Check(relay.Client.Poll(0, SelectMode.SelectRead) && relay.Client.Available == 0, "解析返回前已确认播放器 TCP 断开");
                    release.Set();
                    Check(worker.Join(1500), "解析失败且播放器断开时不进入两秒重试等待");
                    Check(failure == null && resolver.Phase == 1, "失败解析只调用一次且中转正常结束");
                    Check(File.ReadAllText(log).Contains("播放器断开"), "失败解析分支如实记录播放器断开");
                }
                finally { release.Set(); }
            }
        }
        finally
        {
            listener.Stop(); hub.GetField("RuntimeDir", Flags).SetValue(null, oldRuntime);
            hub.GetField("RelayLogFile", Flags).SetValue(null, oldLog); Directory.Delete(dir, true);
        }
    }
}
