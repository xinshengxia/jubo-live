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
        RegisterLogicAuditPlaybackCases();
        TestRunner.Add("审计待确认", "FLV 两次时钟归零及中间重叠保持音视频续接", TestAuditRepeatedReset);
        TestRunner.Add("审计待确认", "压缩弹幕展开超过限额时拒绝", TestAuditInflateLimit);
        TestRunner.Add("审计待确认", "配置热重载损坏和占用保留最后有效值并恢复", TestAuditConfigReload);
        TestRunner.Add("审计待确认", "大尺寸压缩图片在分配像素前拒绝", TestAuditImageDimensions);
        TestRunner.Add("审计待确认", "图片字节预算、替换和清理保留租约", TestAuditImageBudget);
        TestRunner.Add("审计待确认", "图片全局并发预算及退出后完成释放", TestAuditImageConcurrency);
        TestRunner.Add("审计待确认", "本地 WSS 分片上限、超限断开和新连接恢复", TestAuditWssLimit);
        TestRunner.Add("审计待确认", "B站嵌套压缩和批量展开共享预算", TestAuditBiliBudget);
        TestRunner.Add("审计待确认", "FLV 时间线代际识别与一致起播", TestAuditTimelineEpochs);
        TestRunner.Add("审计待确认", "中继连续无进展预算与媒体进展复位", TestAuditReconnectBudget);
        TestRunner.Add("审计待确认", "弹幕接收队列有界且高级事件不被挤掉", TestAuditDanmakuQueue);
        TestRunner.Add("审计待确认", "虎牙贵族等级不冒充通用守护头衔", TestAuditHuyaNoble);
        TestRunner.Add("审计待确认", "查找分页参数与候选稳定身份", TestAuditLookupPagination);
        TestRunner.Add("审计待确认", "同步基线与中继计数诊断、音画异常标记", TestAuditSyncDiagnostics);
        TestRunner.Add("审计待确认", "会话停止只关闭本程序拉起的播放器", TestAuditOwnedPlayback);
        TestRunner.Add("审计待确认", "停止或重同步期间阻止另一启动入口抢占", TestAuditSessionLaunchGate);
        TestRunner.AddDesktop("审计待确认", "原生 WM_DPICHANGED 建议矩形和倍率往返", TestAuditDpiMessage);
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
                // The +5900ms gap inside the second HTTP response is now shared-clock
                // rebased; both tracks retain identical relative timing.
                int[] expected = { 6000,6100,6101,6201,6221,6321,6322,6422 };
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

    // ------------------------------------------------------------------------------------
    // 2026-11-06：双版本同步与资源边界的合成回归（不启动真实播放器/网络）。
    // 覆盖：FLV 时间线代际（与 Rust flv.rs ClockEpoch 等价）、中继无进展预算、
    //       弹幕有界队列优先级、虎牙贵族头衔语义、查找分页、同步诊断与标记、会话停止身份校验。
    // ------------------------------------------------------------------------------------
    private static object AuditNewTimeline()
    {
        Type type = hub.GetNestedType("RelayTimeline", BindingFlags.NonPublic);
        if (type == null) throw new Exception("找不到中转时间线类型 RelayTimeline");
        return Activator.CreateInstance(type, true);
    }

    private static bool AuditAccept(object timeline, byte[] tag)
    {
        return (bool)timeline.GetType().GetMethod("Accept", ReviewInstance).Invoke(timeline, new object[] { tag });
    }

    private static long AuditTimelineCounter(object timeline, string field)
    {
        return Convert.ToInt64(timeline.GetType().GetField(field, ReviewInstance).GetValue(timeline));
    }

    private static int AuditTimestamp(byte[] tag)
    {
        return (int)Call("FlvTagTimestamp", tag);
    }

    private static void AuditBeginReconnect(object timeline)
    {
        timeline.GetType().GetMethod("BeginReconnect", ReviewInstance).Invoke(timeline, null);
    }

    private static void TestAuditTimelineEpochs()
    {
        // ① 同一响应内大跳变：改一次共享偏移、压缩到已转发时钟 +20ms，两轨相对时刻不变。
        object timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, AuditTag(9, 1000, 1)), "时间线：视频首包");
        Check(AuditAccept(timeline, AuditTag(8, 1000, 2)), "时间线：音频首包");
        byte[] regular = VideoTag(1016, 3);
        Check(AuditAccept(timeline, regular) && AuditTimestamp(regular) == 1016, "时间线：连续段原样转发");
        Check(AuditTimelineCounter(timeline, "Jumps") == 0, "时间线：连续段不算跳变");
        byte[] jumped = VideoTag(116016, 4);
        Check(AuditAccept(timeline, jumped), "时间线：同响应 +115s 跳变仍接受");
        Check(AuditTimelineCounter(timeline, "Jumps") == 1 && AuditTimelineCounter(timeline, "Corrections") == 1,
            "时间线：跳变只记一次修正");
        Check(AuditTimestamp(jumped) == 1016 + 20, "时间线：跳变压缩到已转发时钟 +20ms");
        Check(AuditTimelineCounter(timeline, "LastJumpMs") == 115000, "时间线：记录原始跳变幅度");
        byte[] nextVideo = VideoTag(116032, 5), nextAudio = AuditTag(8, 116040, 6);
        Check(AuditAccept(timeline, nextVideo) && AuditTimestamp(nextVideo) == 1036 + 16, "时间线：跳变后视频沿用共享偏移");
        Check(AuditAccept(timeline, nextAudio) && AuditTimestamp(nextAudio) == 1036 + 24, "时间线：跳变后音频沿用共享偏移");
        Check(AuditTimelineCounter(timeline, "Jumps") == 1, "时间线：沿用偏移不再重复修正");

        // ② 后向跳变：上游时钟回退时重基到已转发时钟之后，而不是把时间戳倒退发给播放器。
        timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, VideoTag(5000, 7)), "时间线：后向跳变前首包");
        Check(AuditAccept(timeline, VideoTag(5016, 8)), "时间线：后向跳变前连续包");
        byte[] restarted = VideoTag(0, 9);
        Check(AuditAccept(timeline, restarted) && AuditTimestamp(restarted) == 5016 + 20, "时间线：后向跳变重基到已转发时钟之后");
        Check(AuditTimelineCounter(timeline, "Jumps") == 1 && AuditTimelineCounter(timeline, "LastJumpMs") == -5016,
            "时间线：后向跳变幅度按负值记录");
        byte[] afterReset = AuditTag(8, 24, 10);
        Check(AuditAccept(timeline, afterReset) && AuditTimestamp(afterReset) == 5060, "时间线：后向跳变后音频沿用共享偏移");

        // ③ 代际切换：新视频先到、旧音频尾部穿插、对应新音频最后到。
        //    旧尾包必须丢弃，新音视频对必须落在同一输出时刻（不制造额外音画差）。
        timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, AuditTag(8, 100000, 11)) && AuditAccept(timeline, AuditTag(9, 100000, 12)),
            "时间线：代际切换前两轨对齐");
        byte[] newVideo = VideoTag(110000, 13);
        Check(AuditAccept(timeline, newVideo), "时间线：新代际视频先到");
        long pairTimestamp = AuditTimestamp(newVideo);
        bool tailDropped = true;
        int tailCount = 0;
        for (int raw = 100020; raw <= 101500; raw += 20)
        {
            tailCount++;
            if (AuditAccept(timeline, AuditTag(8, raw, 14))) tailDropped = false;
        }
        Check(tailDropped, "时间线：新代际确认前旧音频尾包全部丢弃");
        Check(tailCount == 75, "时间线：旧代际尾包夹具数量");
        Check(AuditTimelineCounter(timeline, "DiscardedEpochTail") == 75, "时间线：记录丢弃的旧代际尾包数");
        Check(AuditTimelineCounter(timeline, "EpochTailEpisodes") == 1, "时间线：旧代际尾包按代际计数");
        byte[] newAudio = AuditTag(8, 110000, 15);
        Check(AuditAccept(timeline, newAudio) && AuditTimestamp(newAudio) == pairTimestamp,
            "时间线：新音频与新视频落在同一输出时刻");
        Check(AuditTimelineCounter(timeline, "Jumps") == 1, "时间线：代际切换只记一次跳变");
        foreach (int raw in new int[] { 110040, 110080 })
        {
            byte[] audio = AuditTag(8, raw, 16), video = VideoTag(raw, 17);
            Check(AuditAccept(timeline, audio) && AuditAccept(timeline, video), "时间线：新代际后续包");
            Check(AuditTimestamp(audio) == AuditTimestamp(video), "时间线：新代际后续音视频相对时刻一致");
        }

        // ④ 重连归零后收到另一轨旧尾部：旧尾部不能套上新偏移冒充「已追上」。
        timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, AuditTag(8, 9000, 21)) && AuditAccept(timeline, AuditTag(9, 9000, 22)),
            "时间线：重连前两轨对齐");
        AuditBeginReconnect(timeline);
        byte[] resetVideo = VideoTag(0, 23);
        Check(AuditAccept(timeline, resetVideo) && AuditTimestamp(resetVideo) == 9001, "时间线：重连归零按已转发时钟续接");
        Check(!AuditAccept(timeline, AuditTag(8, 8900, 24)), "时间线：重连归零后的旧音频尾部被丢弃");
        byte[] resetAudio = AuditTag(8, 0, 25);
        Check(AuditAccept(timeline, resetAudio) && AuditTimestamp(resetAudio) == AuditTimestamp(resetVideo),
            "时间线：重连归零后新音频与视频一致起播");
        AuditBeginReconnect(timeline);
        Check(!AuditAccept(timeline, AuditTag(8, 0, 26)), "时间线：新连接上的同代际重复包被丢弃");

        // ⑤ 连续两次归零各自重基；初始化包只改时间戳、不参与时钟判断。
        timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, AuditTag(8, 9000, 31)) && AuditAccept(timeline, AuditTag(9, 9000, 32)),
            "时间线：两次归零前两轨对齐");
        AuditBeginReconnect(timeline);
        byte[] codec = VideoTag(0, 0);                       // tag[12]==0 → AVC sequence header
        Check(AuditAccept(timeline, codec) && AuditTimestamp(codec) == 9000, "时间线：初始化包时间戳对齐且保留");
        byte[] firstAudio = AuditTag(8, 0, 33);
        Check(AuditAccept(timeline, firstAudio) && AuditTimestamp(firstAudio) == 9001, "时间线：首次归零重基 +1ms");
        byte[] firstVideo = VideoTag(0, 34);
        Check(AuditAccept(timeline, firstVideo) && AuditTimestamp(firstVideo) == 9001, "时间线：首次归零音视频同点");
        byte[] forwardAudio = AuditTag(8, 7000, 35), forwardVideo = VideoTag(7000, 36);
        Check(AuditAccept(timeline, forwardAudio) && AuditTimestamp(forwardAudio) == 9021, "时间线：同响应 +7s 跳变压缩转发");
        Check(AuditAccept(timeline, forwardVideo) && AuditTimestamp(forwardVideo) == 9021, "时间线：跳变后两轨仍同点");
        Check(AuditTimelineCounter(timeline, "Jumps") == 1, "时间线：两次归零里只有一次非传输层跳变");
        AuditBeginReconnect(timeline);
        byte[] secondReset = VideoTag(0, 37);
        Check(AuditAccept(timeline, secondReset) && AuditTimestamp(secondReset) == 9022,
            "时间线：第二次归零继续在已转发时钟之后续接");

        // An unconfirmed generation cannot permanently filter every new response.
        timeline = AuditNewTimeline();
        Check(AuditAccept(timeline, AuditTag(8, 100000, 51)) && AuditAccept(timeline, VideoTag(100000, 52)),
            "时间线：未确认代际前两轨对齐");
        Check(AuditAccept(timeline, VideoTag(200000, 53)), "时间线：视频跳变，音频尚未确认");
        Check(!AuditAccept(timeline, AuditTag(8, 100020, 54)), "时间线：超时前继续拒绝旧音频尾包");
        timeline.GetType().GetField("epochStarted", ReviewInstance).SetValue(timeline,
            System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 6);
        AuditBeginReconnect(timeline);
        byte[] lateAudio = AuditTag(8, 500, 55), lateVideo = VideoTag(500, 56);
        Check(AuditAccept(timeline, lateAudio) && AuditAccept(timeline, lateVideo),
            "时间线：未确认代际超时后新响应不再永久饿死播放器");
        Check(AuditTimestamp(lateAudio) >= 100020 && AuditTimestamp(lateVideo) >= 100020
            && Math.Abs(AuditTimestamp(lateAudio) - AuditTimestamp(lateVideo)) <= 20,
            "时间线：超时恢复保持输出单调与音视频相对时刻");

        // ⑥ 长时间连续段：同一仿射映射，不产生逐包累计漂移（合成 1 小时跨度）。
        timeline = AuditNewTimeline();
        bool aligned = true;
        for (int raw = 100000; raw <= 3700000; raw += 40)
        {
            byte[] audio = AuditTag(8, raw, 41), video = VideoTag(raw, 42);
            if (!AuditAccept(timeline, audio) || !AuditAccept(timeline, video)
                || AuditTimestamp(audio) != raw || AuditTimestamp(video) != raw) { aligned = false; break; }
        }
        Check(aligned, "时间线：连续段 1 小时跨度（合成）无漂移");
        Check(AuditTimelineCounter(timeline, "Jumps") == 0 && AuditTimelineCounter(timeline, "Corrections") == 0,
            "时间线：连续段零修正");
    }

    /// 合成一个带指定 payload 大小的音/视频媒体 tag（payload 首字节 0x17，非初始化包）。
    private static byte[] AuditMediaTag(int type, int timestamp, int payload)
    {
        byte[] tag = new byte[11 + payload + 4];
        tag[0] = (byte)type;
        tag[1] = (byte)(payload >> 16); tag[2] = (byte)(payload >> 8); tag[3] = (byte)payload;
        tag[4] = (byte)(timestamp >> 16); tag[5] = (byte)(timestamp >> 8);
        tag[6] = (byte)timestamp; tag[7] = (byte)(timestamp >> 24);
        if (payload >= 2) { tag[11] = 0x17; tag[12] = 1; }
        int previous = 11 + payload;
        tag[previous] = (byte)(previous >> 24); tag[previous + 1] = (byte)(previous >> 16);
        tag[previous + 2] = (byte)(previous >> 8); tag[previous + 3] = (byte)previous;
        return tag;
    }

    private static void TestAuditReconnectBudget()
    {
        Type budgetType = hub.GetNestedType("RelayProgressBudget", BindingFlags.Public | BindingFlags.NonPublic);
        if (budgetType == null) throw new Exception("找不到中继无进展预算类型 RelayProgressBudget");
        object budget = Activator.CreateInstance(budgetType, true);
        MethodInfo failed = budgetType.GetMethod("FailedAttempt", ReviewInstance);
        MethodInfo recover = budgetType.GetMethod("Recover", ReviewInstance);
        PropertyInfo exhausted = budgetType.GetProperty("Exhausted", ReviewInstance);
        PropertyInfo consecutive = budgetType.GetProperty("ConsecutiveFailures", ReviewInstance);
        for (int i = 0; i < 499; i++) failed.Invoke(budget, null);
        Check(!(bool)exhausted.GetValue(budget, null) && (int)consecutive.GetValue(budget, null) == 499,
            "重连预算：499 次无进展尚未耗尽");
        recover.Invoke(budget, null);
        Check((int)consecutive.GetValue(budget, null) == 0, "重连预算：有效媒体进展后复位");
        for (int i = 0; i < 500; i++) failed.Invoke(budget, null);
        Check((bool)exhausted.GetValue(budget, null), "重连预算：连续 500 次无进展才耗尽");
        failed.Invoke(budget, null);
        Check((int)consecutive.GetValue(budget, null) == 500, "重连预算：耗尽后计数封顶");

        // 媒体进展门槛：标签数 + 音视频字节 + 至少一条轨推进 ≥1s；静止时间戳不算恢复。
        Type progressType = hub.GetNestedType("RelayMediaProgress", BindingFlags.NonPublic);
        object progress = Activator.CreateInstance(progressType, true);
        MethodInfo observe = progressType.GetMethod("Observe", ReviewInstance);
        FieldInfo stable = progressType.GetField("Stable", ReviewInstance);
        Check(!(bool)observe.Invoke(progress, new object[] { VideoTag(0, 0) }), "媒体进展：初始化包不计进度");
        bool staticStable = false;
        for (int i = 0; i < 40; i++)
            if ((bool)observe.Invoke(progress, new object[] { AuditMediaTag(9, 5000, 400) })) staticStable = true;
        Check(!staticStable && !(bool)stable.GetValue(progress), "媒体进展：时间戳不推进不冒充恢复");

        progress = Activator.CreateInstance(progressType, true);
        for (int i = 0; i < 40; i++)
            observe.Invoke(progress, new object[] { AuditMediaTag(i % 2 == 0 ? 9 : 8, i % 2 == 0 ? 5000 : 6500, 400) });
        Check(!(bool)stable.GetValue(progress), "媒体进展：静止音视频相差1.5s也不能冒充任一轨推进");
        for (int i = 0; i <= 25; i++)
            observe.Invoke(progress, new object[] { AuditMediaTag(8, 6500 + i * 40, 400) });
        Check((bool)stable.GetValue(progress), "媒体进展：任一轨真实推进1s后恢复");

        progress = Activator.CreateInstance(progressType, true);
        observe = progressType.GetMethod("Observe", ReviewInstance);
        stable = progressType.GetField("Stable", ReviewInstance);
        int stableAt = -1;
        for (int i = 0; i <= 32; i++)
        {
            if ((bool)observe.Invoke(progress, new object[] { AuditMediaTag(9, i * 40, 400) })) { stableAt = i; break; }
        }
        Check(stableAt >= 25 && (bool)stable.GetValue(progress), "媒体进展：标签数+字节+1s 推进后判定恢复");

        // 脱敏日志：签名直链只保留主机名。
        Check((string)Call("RelayHostForLog", "https://cdn.example.com/live.flv?token=SECRET&sign=1") == "cdn.example.com",
            "日志脱敏：上游只记录主机名");
        Check((string)Call("RelayHostForLog", (object)null) == "(无上游地址)", "日志脱敏：空地址有固定分类");
    }

    private static bool AuditInboxContains(object inbox, string typeName)
    {
        foreach (object message in (IEnumerable)inbox)
            if (message != null && message.GetType().Name == typeName) return true;
        return false;
    }

    private static void TestAuditDanmakuQueue()
    {
        string dir = Path.Combine(Path.GetTempPath(), "audit-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic);
        object oldRuntime = runtime.GetValue(null);
        Exception failure = null;
        int capacity = 0, total = 0;
        long dropped = 0;
        bool hasSuperChat = false, hasDelete = false, retainedOlderSc = false;
        var thread = new Thread(delegate()
        {
            string configPath = Path.Combine(dir, "danmaku.json");
            File.WriteAllText(configPath, "{\"OpacityPercent\":57}");
            try
            {
                Type overlayType = DanmakuType("DanmakuOverlayForm");
                object config = DanmakuType("DanmakuConfig").GetMethod("Load").Invoke(null, new object[] { configPath });
                using (Form overlay = (Form)Activator.CreateInstance(overlayType, new object[] { config, configPath, 0, "audit-queue" }))
                {
                    capacity = (int)overlayType.GetField("MaxInbox", ReviewStatic).GetValue(null);
                    MethodInfo post = overlayType.GetMethod("PostDanmaku", ReviewInstance);
                    Type itemType = DanmakuType("DanmakuItem"), superType = DanmakuType("SuperChatItem");
                    for (int i = 0; i < capacity + 600; i++) post.Invoke(overlay, new object[] { Activator.CreateInstance(itemType) });
                    object inbox = overlayType.GetField("_inbox", ReviewInstance).GetValue(overlay);
                    PropertyInfo count = inbox.GetType().GetProperty("Count", ReviewInstance);
                    total = (int)count.GetValue(inbox, null);
                    // SC（高优先级）不能被普通弹幕洪水挤掉。
                    overlayType.GetMethod("PostSuperChat", ReviewInstance).Invoke(overlay,
                        new object[] { Activator.CreateInstance(superType) });
                    total = (int)count.GetValue(inbox, null);
                    hasSuperChat = AuditInboxContains(inbox, "SuperChatItem");
                    for (int i = 0; i < capacity + 600; i++) post.Invoke(overlay, new object[] { Activator.CreateInstance(itemType) });
                    hasSuperChat = hasSuperChat && AuditInboxContains(inbox, "SuperChatItem");
                    total = (int)count.GetValue(inbox, null);
                    // SC 删除（最高优先级）不能被普通弹幕满队列挡住。
                    overlayType.GetMethod("PostSuperChatDelete", ReviewInstance).Invoke(overlay, new object[] { 42L });
                    hasDelete = AuditInboxContains(inbox, "ScDelete");
                    total = (int)count.GetValue(inbox, null);
                    dropped = Convert.ToInt64(overlayType.GetField("_inboxDropped", ReviewInstance).GetValue(overlay));
                    // Mixed-order regression: an older paid card precedes weaker chat.
                    // The deletion must evict chat, rather than that first lower-priority SC.
                    inbox.GetType().GetMethod("Clear", ReviewInstance).Invoke(inbox, null);
                    overlayType.GetMethod("PostSuperChat", ReviewInstance).Invoke(overlay,
                        new object[] { Activator.CreateInstance(superType) });
                    for (int i = 1; i < capacity; i++) post.Invoke(overlay, new object[] { Activator.CreateInstance(itemType) });
                    overlayType.GetMethod("PostSuperChatDelete", ReviewInstance).Invoke(overlay, new object[] { 43L });
                    retainedOlderSc = AuditInboxContains(inbox, "SuperChatItem") && AuditInboxContains(inbox, "ScDelete")
                        && (int)count.GetValue(inbox, null) == capacity;
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            runtime.SetValue(null, dir);
            thread.Start();
            Check(thread.Join(20000), "弹幕队列实验线程结束");
            if (failure != null) throw failure;
            Check(capacity >= 512, "弹幕队列有明确容量上限");
            Check(total == capacity, "弹幕队列长度稳定在容量上限");
            Check(dropped > 0, "队列满时按策略丢弃并计数");
            Check(hasSuperChat, "队列满时 SC 高优先级事件不被普通弹幕挤掉");
            Check(hasDelete, "队列满时 SC 删除事件不被普通弹幕挤掉");
            Check(retainedOlderSc, "删除事件优先淘汰最低级聊天，保留队首较早SC与队列容量");
        }
        finally
        {
            runtime.SetValue(null, oldRuntime);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void TestAuditHuyaNoble()
    {
        Type huya = DanmakuType("HuyaDanmakuClient");
        object client = Activator.CreateInstance(huya, new object[] { "333003" });
        MethodInfo parse = huya.GetMethod("ParseMessageNotice", ReviewInstance);
        // tUserInfo{ tag2 昵称, tag5 iNobleLevel=3 } + 外层 tag3 正文 + ContentFormat 颜色。
        const string hex =
            "0a" + "02" + "0000007b" + "1c" + "26" + "0c" + "e6b58be8af95e794a8e688b7" +
            "3c" + "46" + "0d" + "687474703a2f2f617661746172" + "50" + "03" + "0b" +
            "36" + "18" + "e8bf99e698afe4b880e69da1e8998ee78999e5bcb9e5b995" +
            "5a" + "02" + "00ff0000" + "0b";
        object item = parse.Invoke(client, new object[] { HexToBytes(hex) });
        Check(item != null, "虎牙贵族：能解出弹幕");
        if (item == null) return;
        Type itemType = item.GetType();
        Check((string)itemType.GetField("Text").GetValue(item) == "这是一条虎牙弹幕", "虎牙贵族：正文仍取外层 tag3");
        Check((int)itemType.GetField("GuardLevel").GetValue(item) == 0, "虎牙贵族：iNobleLevel 不冒充 B站 守护头衔");
    }

    private static object AuditLookupCandidate(string platform, string accountId)
    {
        Type type = hub.GetNestedType("LookupCandidate", BindingFlags.Public | BindingFlags.NonPublic);
        if (type == null) throw new Exception("找不到候选类型 LookupCandidate");
        object candidate = Activator.CreateInstance(type, true);
        type.GetProperty("platform").SetValue(candidate, platform, null);
        type.GetProperty("account_id").SetValue(candidate, accountId, null);
        return candidate;
    }

    private static void TestAuditLookupPagination()
    {
        Type douyin = hub.GetNestedType("DouyinUserSearch", BindingFlags.Public | BindingFlags.NonPublic);
        if (douyin == null) throw new Exception("找不到抖音搜索类型 DouyinUserSearch");
        string page3 = (string)CallStatic(douyin, "UrlPage", "主播名", 3);
        Check(page3.Contains("offset=20") && page3.Contains("count=10"), "查找分页：抖音第 3 页 offset=20/count=10");
        bool rejected = false;
        try { CallStatic(douyin, "UrlPage", "主播名", 0); }
        catch (TargetInvocationException ex)
        {
            rejected = ex.InnerException != null && ex.InnerException.GetType().Name == "LookupFailure";
        }
        Check(rejected, "查找分页：页码越界被拒绝");
        object first = AuditLookupCandidate("douyu", "123"), same = AuditLookupCandidate("douyu", "123");
        object other = AuditLookupCandidate("douyu", "456");
        Check((string)Call("LookupStableIdentity", first) == (string)Call("LookupStableIdentity", same),
            "查找分页：同一账号稳定身份相同（累加去重）");
        Check((string)Call("LookupStableIdentity", first) != (string)Call("LookupStableIdentity", other),
            "查找分页：不同账号不会被误去重");
    }

    private static void TestAuditSyncDiagnostics()
    {
        string dir = Path.Combine(Path.GetTempPath(), "audit-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic);
        object oldRuntime = runtime.GetValue(null);
        try
        {
            runtime.SetValue(null, dir);
            object target = Call("Parse", "douyu:93589", "bili");
            target.GetType().GetField("SessionId").SetValue(target, "0123456789abcdef0123456789abcdef");
            string sync = (string)Call("PlaybackSyncSummary", "mpv", target);
            Check(sync.Contains("video-sync=audio") && sync.Contains("framedrop=vo")
                && sync.Contains("audio-delay") && sync.Contains("cache-secs=3"), "同步诊断：mpv 同步参数可读");
            Check(((string)Call("PlaybackSyncSummary", "potplayer", target)).Contains("PotPlayer"),
                "同步诊断：PotPlayer 明确不传 mpv 同步参数");

            string diagPath = (string)Call("SessionRelayDiagPath", target);
            Check(diagPath.IndexOf("session-0123", StringComparison.Ordinal) >= 0, "同步诊断：按会话隔离诊断文件");
            File.WriteAllText(diagPath,
                "version=1\nstate=ONLINE\nupdated=" + DateTime.UtcNow.ToString("o") +
                "\nreconnects=3\njumps=1\ncorrections=2\ndiscarded_epoch_tail=75\ndropped_tags=2\nbytes=1048576\nstable=1\n",
                new UTF8Encoding(false));
            string summary = (string)Call("RelayDiagSummary", File.ReadAllText(diagPath, Encoding.UTF8));
            Check(summary.Contains("重连 3 次") && summary.Contains("时间线修正 2 次")
                && summary.Contains("丢弃旧代际尾包 75 个"), "同步诊断：中继计数解读");

            Type sessionType = hub.GetNestedType("PlaybackSession", BindingFlags.NonPublic);
            if (sessionType == null) throw new Exception("找不到会话类型 PlaybackSession");
            object session = Activator.CreateInstance(sessionType, true);
            sessionType.GetField("Target").SetValue(session, target);
            sessionType.GetField("Player").SetValue(session, "mpv");
            object[] args = { session, null };
            bool written = (bool)Call("TryMarkAvSyncIssue", args);
            Check(written && args[1] is string, "同步诊断：标记音画异常返回结果");
            string logPath = Path.Combine(dir, "logs", "avsync-mark.log");
            Check(File.Exists(logPath), "同步诊断：标记写入本地日志");
            if (File.Exists(logPath))
            {
                string log = File.ReadAllText(logPath, Encoding.UTF8);
                Check(log.Contains("音画异常标记") && log.Contains("reconnects:3")
                    && log.Contains("discarded_epoch_tail:75"), "同步诊断：记录会话与中继计数");
                Check(log.IndexOf("http://", StringComparison.OrdinalIgnoreCase) < 0
                    && log.IndexOf("https://", StringComparison.OrdinalIgnoreCase) < 0
                    && log.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) < 0,
                    "同步诊断：本地日志不含 URL/Cookie");
            }
        }
        finally
        {
            runtime.SetValue(null, oldRuntime);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void TestAuditSessionLaunchGate()
    {
        WithWorkbenchForm(-1, delegate(Form form)
        {
            object ui = form.Tag; Type type = ui.GetType();
            FieldInfo closing = type.GetField("closingSession", ReviewInstance);
            MethodInfo update = type.GetMethod("UpdateSessionActions", ReviewInstance);
            Control mpv = (Control)type.GetField("openMpv", ReviewInstance).GetValue(ui);
            Control pot = (Control)type.GetField("openPot", ReviewInstance).GetValue(ui);
            Control input = (Control)type.GetField("input", ReviewInstance).GetValue(ui);
            Control status = (Control)type.GetField("status", ReviewInstance).GetValue(ui);
            input.Text = ""; status.Text = "合成停止操作进行中";
            closing.SetValue(ui, true); update.Invoke(ui, null);
            Check(!mpv.Enabled && !pot.Enabled, "会话互斥：停止期间禁用两个播放器启动按钮");
            type.GetMethod("Launch", ReviewInstance).Invoke(ui, new object[] { "mpv" });
            Check(status.Text == "合成停止操作进行中" && type.GetField("currentLaunch", ReviewInstance).GetValue(ui) == null,
                "会话互斥：快捷入口不能在停止期间改写状态或启动工作线程");
            closing.SetValue(ui, false); update.Invoke(ui, null);
            Check(mpv.Enabled && pot.Enabled, "会话互斥：停止收尾后恢复播放器启动入口");
        });
    }

    private static void TestAuditOwnedPlayback()
    {
        string self = Assembly.GetExecutingAssembly().Location;
        var start = new System.Diagnostics.ProcessStartInfo(self, "--hang");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start))
        {
            if (process == null) throw new Exception("合成播放器进程没有启动");
            try
            {
                Check(!process.HasExited, "会话停止：合成播放器进程已启动");
                string executable = (string)Call("PlayerProcessExecutable", process);
                Check(string.Equals(Path.GetFullPath(self), executable, StringComparison.OrdinalIgnoreCase),
                    "会话停止：记录本程序拉起的可执行文件");
                Type sessionType = hub.GetNestedType("PlaybackSession", BindingFlags.NonPublic);
                if (sessionType == null) throw new Exception("找不到会话类型 PlaybackSession");
                object session = Activator.CreateInstance(sessionType, true);
                object target = Call("Parse", "douyu:93589", "bili");
                sessionType.GetField("Target").SetValue(session, target);
                sessionType.GetField("Player").SetValue(session, "mpv");
                sessionType.GetField("Process").SetValue(session, process);
                sessionType.GetField("PlayerProcessId").SetValue(session, process.Id);
                sessionType.GetField("PlayerExecutable").SetValue(session, executable);
                sessionType.GetField("PlayerStartedUtc").SetValue(session, DateTime.MinValue);
                Check(!(bool)Call("StopOwnedPlayback", new object[] { session, null }),
                    "会话停止：启动时间不匹配时拒绝关闭");
                Check(!process.HasExited, "会话停止：身份校验失败时进程仍存活");
                sessionType.GetField("PlayerStartedUtc").SetValue(session, process.StartTime.ToUniversalTime());
                sessionType.GetField("PlayerExecutable").SetValue(session, Path.Combine(Path.GetTempPath(), "other-player.exe"));
                Check(!(bool)Call("StopOwnedPlayback", new object[] { session, null }),
                    "会话停止：可执行文件不匹配时拒绝关闭");
                Check(!process.HasExited, "会话停止：可执行文件不匹配时进程仍存活");
                sessionType.GetField("PlayerExecutable").SetValue(session, executable);
                sessionType.GetField("PlayerProcessId").SetValue(session, process.Id + 1);
                Check(!(bool)Call("StopOwnedPlayback", new object[] { session, null }),
                    "会话停止：PID 不匹配时拒绝关闭");
                Check(!process.HasExited, "会话停止：PID 不匹配时进程仍存活");
                sessionType.GetField("PlayerProcessId").SetValue(session, process.Id);
                sessionType.GetField("PlayerStartedUtc").SetValue(session, process.StartTime.ToUniversalTime());
                Check((bool)Call("StopOwnedPlayback", new object[] { session, null }),
                    "会话停止：身份匹配时关闭本程序自有进程");
                Check(process.WaitForExit(5000), "会话停止：目标进程已退出");
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(); } catch (Exception) { } }
            }
        }
    }
}
