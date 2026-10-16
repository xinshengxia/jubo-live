using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterAuditPendingCases()
    {
        TestRunner.Add("审计待确认", "FLV 两次时钟归零及中间重叠保持音视频续接", TestAuditRepeatedReset);
        TestRunner.Add("审计待确认", "压缩弹幕展开超过限额时拒绝", TestAuditInflateLimit);
        TestRunner.Add("审计待确认", "配置热重载损坏和占用保留最后有效值并恢复", TestAuditConfigReload);
        TestRunner.Add("审计待确认", "大尺寸压缩图片在分配像素前拒绝", TestAuditImageDimensions);
        TestRunner.Add("审计待确认", "图片字节预算、替换和清理保留租约", TestAuditImageBudget);
        TestRunner.Add("审计待确认", "图片全局并发预算及退出后完成释放", TestAuditImageConcurrency);
        TestRunner.Add("审计待确认", "本地 WSS 分片上限、超限断开和新连接恢复", TestAuditWssLimit);
        TestRunner.Add("审计待确认", "B站嵌套压缩和批量展开共享预算", TestAuditBiliBudget);
        TestRunner.Add("审计待确认", "原生 WM_DPICHANGED 建议矩形和倍率往返", TestAuditDpiMessage);
        if (AuditDesktopEnabled) TestRunner.Add("审计桌面", "真实显示器移动、分层透明像素和资源回收", TestAuditDesktop);
    }

    private static byte[] AuditTag(int type, int stamp, byte id)
    {
        byte[] tag = VideoTag(stamp, id); tag[0] = (byte)type; return tag;
    }

    private static void TestAuditRepeatedReset()
    {
        int[][] clocks = { new int[] { 6000, 6100 }, new int[] { 0, 100, 6000 },
            new int[] { 5900, 6000, 6100 }, new int[] { 0, 100 } };
        byte[][] segments = new byte[clocks.Length][];
        byte id = 0;
        for (int n = 0; n < clocks.Length; n++)
        {
            var tags = new List<byte[]>(); tags.Add(FlvHeader);
            foreach (int ts in clocks[n])
            { tags.Add(AuditTag(8, ts, ++id)); tags.Add(AuditTag(9, ts, ++id)); }
            segments[n] = Join(tags.ToArray());
        }
        var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var downstream = new TcpListener(IPAddress.Loopback, 0); downstream.Start();
        Exception serverFailure = null, pumpFailure = null;
        var server = new Thread(delegate() {
            try {
                foreach (byte[] segment in segments)
                    using (TcpClient c = upstream.AcceptTcpClient())
                    using (NetworkStream s = c.GetStream())
                    { ReadRequest(s); byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + segment.Length + "\r\nConnection: close\r\n\r\n"); s.Write(h,0,h.Length); s.Write(segment,0,segment.Length); }
            } catch (Exception ex) { serverFailure = ex; }
        }); server.IsBackground = true; server.Start();
        Thread pump = null;
        try {
            using (var player = new TcpClient("127.0.0.1", ((IPEndPoint)downstream.LocalEndpoint).Port))
            using (var client = downstream.AcceptTcpClient())
            {
                var resolver = RelayResolverFor(new FakeResolver { Port = ((IPEndPoint)upstream.LocalEndpoint).Port });
                pump = new Thread(delegate() { try { Call("Pump", client, "douyu", "clock-fixture", resolver); } catch (Exception ex) { pumpFailure = ex; } });
                pump.IsBackground = true; pump.Start();
                NetworkStream stream = player.GetStream(); stream.ReadTimeout = 5000;
                byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n"); stream.Write(request,0,request.Length);
                ReadRequest(stream); ReadExactly(stream, FlvHeader.Length);
                int[] expected = { 6000,6100,6101,6201,12101,12201,12202,12302 };
                for (int i = 0; i < expected.Length; i++)
                    for (int track = 0; track < 2; track++)
                    {
                        byte[] tag = ReadExactly(stream, VideoTag(0,0).Length);
                        Check(tag[0] == (track == 0 ? 8 : 9) && (int)Call("FlvTagTimestamp", tag) == expected[i],
                            "多段实际 Pump 音视频时间戳 " + i + "/" + track + " = " + expected[i]);
                    }
                player.Close();
            }
        } finally { upstream.Stop(); downstream.Stop(); server.Join(2000); if (pump != null) pump.Join(4000); }
        Check(serverFailure == null && pumpFailure == null, "多段夹具和中转正常收尾");
    }

    private static byte[] AuditCompressed(bool gzip, int length)
    {
        using (var output = new MemoryStream())
        {
            if (!gzip) { output.WriteByte(0x78); output.WriteByte(0x9c); }
            using (Stream zip = gzip ? (Stream)new GZipStream(output, CompressionMode.Compress, true) : new DeflateStream(output, CompressionMode.Compress, true))
            {
                byte[] block = new byte[8192]; for (int i = 0; i < block.Length; i++) block[i] = 32;
                while (length > 0) { int count = Math.Min(length, block.Length); zip.Write(block, 0, count); length -= count; }
            }
            return output.ToArray();
        }
    }

    private static void TestAuditInflateLimit()
    {
        foreach (bool gzip in new bool[] { false, true })
        {
            Type type = DanmakuType(gzip ? "DouyinDanmakuClient" : "BiliDanmakuClient");
            MethodInfo method = type.GetMethod(gzip ? "Gunzip" : "Inflate", ReviewStatic);
            bool rejected = false;
            try { method.Invoke(null, new object[] { AuditCompressed(gzip, 8 * 1024 * 1024 + 1) }); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
            Check(rejected, (gzip ? "抖音 gzip" : "B站 zlib") + " 拒绝超过 8MiB 的展开内容");
            byte[] normal = (byte[])method.Invoke(null, new object[] { AuditCompressed(gzip, 100) });
            Check(normal.Length == 100, "超限后正常压缩消息仍可解析");
        }
    }

    private static void TestAuditConfigReload()
    {
        Exception failure = null;
        var thread = new Thread(delegate() {
            string dir = Path.Combine(Path.GetTempPath(), "audit-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir,"danmaku.json"); Type configType = DanmakuType("DanmakuConfig");
            File.WriteAllText(path,"{\"OpacityPercent\":57}");
            object config = configType.GetMethod("Load").Invoke(null,new object[] { path });
            try {
                using (Form overlay = (Form)Activator.CreateInstance(DanmakuType("DanmakuOverlayForm"), new object[] { config,path,0,"audit" }))
                {
                    FieldInfo last = overlay.GetType().GetField("_lastConfigCheck",ReviewInstance);
                    MethodInfo reload = overlay.GetType().GetMethod("ReloadConfigIfChanged",ReviewInstance);
                    File.WriteAllText(path,"{\"OpacityPercent\":");
                    reload.Invoke(overlay,null);
                    Check((int)configType.GetField("OpacityPercent").GetValue(config) == 57,"半写 JSON 不切回默认配置");
                    File.WriteAllText(path,"{\"OpacityPercent\":63}");
                    using (var locked = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    { last.SetValue(overlay,DateTime.MinValue); reload.Invoke(overlay,null); }
                    Check((int)configType.GetField("OpacityPercent").GetValue(config) == 57,"锁定文件保持最后有效配置");
                    last.SetValue(overlay,DateTime.MinValue); reload.Invoke(overlay,null);
                    Check((int)configType.GetField("OpacityPercent").GetValue(config) == 63,"解锁后无需再次修改文件即可恢复热重载");
                    File.WriteAllText(path,"null"); File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(2));
                    last.SetValue(overlay,DateTime.MinValue); reload.Invoke(overlay,null);
                    Check((int)configType.GetField("OpacityPercent").GetValue(config) == 63,"null 配置保留最后有效值");
                }
            } catch (Exception ex) { failure = ex; }
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(15000),"配置实验线程结束"); if (failure != null) throw failure;
    }

    private static void TestAuditImageDimensions()
    {
        Type type = DanmakuType("DanmakuImageCache"); MethodInfo decode = type.GetMethod("DecodeImage",ReviewStatic);
        foreach (Size size in new Size[] { new Size(2049,1),new Size(1,2049),new Size(4096,4096) })
        {
            byte[] data = MakePng(size.Width,size.Height);
            using (Bitmap result = (Bitmap)decode.Invoke(null,new object[] { data }))
                Check(result == null,"小压缩体积的大尺寸 PNG 被拒绝 " + size);
        }
        using (Bitmap normal = (Bitmap)decode.Invoke(null,new object[] { MakePng(32,32) }))
            Check(normal != null && normal.Width == 32,"正常图片仍可解码");
        foreach (string name in new string[] { "oversized-vp8x", "oversized-vp8l", "oversized-vp8", "truncated" })
        {
            byte[] webp = new byte[30]; Encoding.ASCII.GetBytes("RIFF").CopyTo(webp,0);
            BitConverter.GetBytes(22).CopyTo(webp,4); Encoding.ASCII.GetBytes("WEBP").CopyTo(webp,8);
            string chunk = name == "oversized-vp8x" ? "VP8X" : name == "oversized-vp8" ? "VP8 " : "VP8L";
            Encoding.ASCII.GetBytes(chunk).CopyTo(webp,12); BitConverter.GetBytes(10).CopyTo(webp,16);
            if (chunk == "VP8X") { webp[24]=0; webp[25]=16; }
            else if (chunk == "VP8L") { webp[20]=0x2f; webp[22]=16; }
            else { webp[23]=0x9d; webp[24]=1; webp[25]=0x2a; webp[27]=16; webp[28]=1; }
            if (name == "truncated") Array.Resize(ref webp,23);
            using (Bitmap image = (Bitmap)decode.Invoke(null,new object[] { webp })) Check(image == null,"解码前拒绝 WebP " + name);
        }
        foreach (System.Drawing.Imaging.ImageFormat format in new System.Drawing.Imaging.ImageFormat[] {
            System.Drawing.Imaging.ImageFormat.Jpeg,System.Drawing.Imaging.ImageFormat.Gif,System.Drawing.Imaging.ImageFormat.Bmp })
            using (var source = new Bitmap(16,16)) using (var bytes = new MemoryStream())
            {
                source.Save(bytes,format);
                using (Bitmap image = (Bitmap)decode.Invoke(null,new object[] { bytes.ToArray() }))
                    Check(image != null && image.Height==16,"常规图片格式兼容 " + format);
            }
    }

    private static void TestAuditImageBudget()
    {
        Type type = DanmakuType("DanmakuImageCache"), shared = DanmakuType("SharedBitmap");
        int baseline = (int)shared.GetProperty("LiveCount",ReviewStatic).GetValue(null,null);
        object cache = Activator.CreateInstance(type); MethodInfo complete=type.GetMethod("Complete",ReviewInstance);
        MethodInfo acquire=shared.GetMethod("Acquire",ReviewInstance), release=shared.GetMethod("Release",ReviewInstance);
        object lease = null;
        try
        {
            lease = complete.Invoke(cache,new object[] { "first",new Bitmap(1024,1024) }); acquire.Invoke(lease,null);
            for (int cycle=0;cycle<4;cycle++)
            {
                for (int i=0;i<24;i++) complete.Invoke(cache,new object[] { "large-"+i,new Bitmap(1024,1024) });
                long bytes = (long)type.GetProperty("CacheBytes",ReviewInstance).GetValue(cache,null);
                Check(bytes<=64L*1024*1024 && (int)type.GetProperty("Count").GetValue(cache,null)<=16,"大图片缓存稳定在 64MiB 内，第 "+cycle+" 轮");
            }
            complete.Invoke(cache,new object[] {"large-23",new Bitmap(512,512)});
            Check((long)type.GetProperty("CacheBytes",ReviewInstance).GetValue(cache,null)==61L*1024*1024
                && (int)type.GetProperty("Count").GetValue(cache,null)==16,"同 URL 替换扣除旧位图预算且不重复计入淘汰队列");
            Check(!IsFreed(lease),"字节预算淘汰后在用租约仍有效");
            ((IDisposable)cache).Dispose(); Check(!IsFreed(lease),"缓存销毁不释放仍在用图片");
            release.Invoke(lease,null); lease=null;
            Check((int)shared.GetProperty("LiveCount",ReviewStatic).GetValue(null,null)==baseline,"清理后位图存活数回到基线");
            Bitmap late=new Bitmap(8,8); complete.Invoke(cache,new object[] {"late",late}); bool freed=false;
            try { late.GetPixel(0,0); } catch(ArgumentException) {freed=true;}
            Check(freed,"缓存销毁后晚到的成功下载立即释放位图"); late.Dispose();
        }
        finally { if (lease!=null) release.Invoke(lease,null); ((IDisposable)cache).Dispose(); }
    }

    private static void TestAuditImageConcurrency()
    {
        Type type=DanmakuType("DanmakuImageCache"); object first=Activator.CreateInstance(type), second=Activator.CreateInstance(type);
        int active=0,maximum=0,finished=0;
        using (var release=new ManualResetEvent(false))
        {
            Func<string,bool> validate=delegate {
                int now=Interlocked.Increment(ref active); lock(type) maximum=Math.Max(maximum,now);
                release.WaitOne(5000); Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); return false;
            };
            type.GetField("ValidateUrl",ReviewInstance).SetValue(first,validate); type.GetField("ValidateUrl",ReviewInstance).SetValue(second,validate);
            try
            {
                for(int i=0;i<40;i++) type.GetMethod("TryGet").Invoke(i%2==0?first:second,new object[] { "https://cdn.example.com/"+i });
                var watch=System.Diagnostics.Stopwatch.StartNew(); while(active<4 && watch.ElapsedMilliseconds<3000) Thread.Sleep(10);
                Check(active==4 && maximum==4,"跨两个缓存的慢 DNS/下载同时最多四个");
                ((IDisposable)first).Dispose(); ((IDisposable)second).Dispose(); release.Set();
                watch.Restart(); while(finished<4 && watch.ElapsedMilliseconds<3000) Thread.Sleep(10);
                Check(finished==4,"退出后的预约全部完成");
                Check(type.GetMethod("TryGet").Invoke(first,new object[] { "https://cdn.example.com/after" })==null,"销毁后不再调度图片");
            }
            finally { release.Set(); ((IDisposable)first).Dispose(); ((IDisposable)second).Dispose(); }
        }
    }
}
