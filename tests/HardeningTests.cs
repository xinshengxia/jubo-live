using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static Type hub;
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

    // 源码根（`--src <目录>`，默认 = 启动器 exe 所在目录）。
    //
    // 为什么需要：少数用例要读**仓库里的源码/样本**（空 catch 棘轮扫 src\Danmaku\*.cs、
    // WebP 用 tests\fixtures\ 的真实样本）。而 `Build-Hardened.ps1 -Out <别处>` 会把 exe
    // 放到别处，那些用例就报「找不到源码（否则这个测试是空转的）」—— 2026-09-27 实测踩到，
    // 与产品缺陷无关，纯粹是测试定位源码的方式太脆。构建脚本现在显式传 --src。
    private static string SourceRoot;

    private static object Call(string name, params object[] args)
    {
        return hub.GetMethod(name, Flags).Invoke(null, args);
    }

    // 断言：成功记一条 PASS 行；失败**立刻**打印 `FAIL: ` 行（干净构建脚本靠它定位第一条失败），
    // 再抛 TestFailedException 中止**当前用例** —— 运行器会继续跑后面的用例（2026-09-27 改造）。
    private static void Check(bool okay, string name)
    {
        if (!okay) TestRunner.Fail(name);
        TestRunner.Pass(name);
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        int count;
        IntPtr memory = CommandLineToArgvW(commandLine, out count);
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
            return args;
        }
        finally { LocalFree(memory); }
    }

    private static void TestArguments()
    {
        string[] titles = { "普通中文标题", "主播 \"A\" --script=evil.lua", "尾部反斜杠\\", "a\\\"b" };
        foreach (string title in titles)
        {
            string quoted = (string)Call("QuoteWindowsArg", title);
            string[] parsed = ParseCommandLine("player.exe --title=" + quoted);
            Check(parsed.Length == 2 && parsed[1] == "--title=" + title, "标题单参数：" + title);
        }
        Check((string)Call("SafeTitle", "一行\r\n二行") == "一行  二行", "标题控制字符过滤");
    }

    private static void TestHuyaTitleFallback()
    {
        const string fallback = "虎牙直播 a16789";
        const string expected = "安德罗妮丶 - 中午11点安BA！s2启动 郭神加入";
        int pageCalls = 0;
        Func<string> page = delegate { pageCalls++; return expected; };
        string[] missingProfiles = {
            "{\"status\":422,\"message\":\"该主播不存在！\",\"data\":[]}",
            "{\"status\":200,\"data\":{\"nick\":\" \",\"roomName\":\"\",\"introduction\":\"\"}}",
            "null", "invalid json"
        };
        foreach (string json in missingProfiles)
        {
            int before = pageCalls;
            string title = (string)Call("ResolveHuyaTitle", (Func<string>)delegate { return json; }, page, fallback);
            Check(title == expected && pageCalls == before + 1, "虎牙无效接口响应走网页兜底：" + json);
        }
        Check((string)Call("ResolveHuyaTitle", (Func<string>)delegate { throw new WebException("timeout"); },
            page, fallback) == expected, "虎牙接口网络异常走网页兜底");
        Func<string> normal = delegate {
            return "{\"status\":200,\"data\":{\"nick\":\"主播\",\"roomName\":\"直播标题\",\"introduction\":\"介绍\"}}";
        };
        int callsBeforeNormal = pageCalls;
        Check((string)Call("ResolveHuyaTitle", normal, page, fallback) == "主播 - 直播标题"
            && pageCalls == callsBeforeNormal, "虎牙有效接口标题不重复请求网页");
        Check((string)Call("ResolveHuyaTitle", (Func<string>)delegate {
            return "{\"data\":{\"nick\":\"主播\",\"roomName\":\"\",\"introduction\":\"直播介绍\"}}";
        }, page, fallback) == "主播 - 直播介绍", "虎牙 roomName 为空时使用 introduction");
        Func<string> missing = delegate { return "{\"status\":422,\"data\":[]}"; };
        Check((string)Call("ResolveHuyaTitle", missing, (Func<string>)delegate { return " "; }, fallback) == fallback,
            "虎牙接口和网页均无标题时保留房间号");
        Check((string)Call("ResolveHuyaTitle", missing, (Func<string>)delegate { throw new WebException("timeout"); },
            fallback) == fallback, "虎牙网页请求失败时保留房间号");
    }

    private static void TestCapture(string self)
    {
        var watch = Stopwatch.StartNew();
        string output = (string)Call("RunCapture", self, "--emit-stderr", 3000);
        Check(output.Trim() == "ok" && watch.ElapsedMilliseconds < 3000, "stderr 填满时仍可完成");
        watch.Restart();
        bool timedOut = false;
        try { Call("RunCapture", self, "--hang", 250); }
        catch (TargetInvocationException ex)
        {
            timedOut = ex.InnerException is InvalidOperationException
                && ex.InnerException.Message.Contains("解析超时");
        }
        Check(timedOut && watch.ElapsedMilliseconds < 2500, "无输出进程按时超时");
    }

    private static void TestAtomicWrite()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "live-hub-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "favorites.json");
            Call("AtomicWriteAllText", path, "{\"old\":1}");
            Call("AtomicWriteAllText", path, "{\"new\":2}");
            Check(File.ReadAllText(path, Encoding.UTF8) == "{\"new\":2}" &&
                Directory.GetFiles(directory).Length == 1, "保存替换完整且无临时文件");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestFavoritesMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "live-hub-favorites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        FieldInfo field = hub.GetField("Favorites", Flags);
        object original = field.GetValue(null);
        try
        {
            string path = Path.Combine(directory, "favorites.json");
            field.SetValue(null, path);
            File.WriteAllText(path, "{\"同名\":\"7777\"}", Encoding.UTF8);
            var entries = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(entries.Count == 1 && entries[0].Value == "7777", "旧收藏格式可读取");
            entries.Add(new KeyValuePair<string, string>("同名", "douyu:952595"));
            Call("SaveFavorites", entries);
            var reloaded = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(reloaded.Count == 2 && reloaded[0].Key == reloaded[1].Key
                && reloaded[0].Value != reloaded[1].Value, "新收藏格式保留跨平台同名房间");
        }
        finally
        {
            field.SetValue(null, original);
            Directory.Delete(directory, true);
        }
    }

    private static void TestSessionAndQuality()
    {
        FieldInfo runtime = hub.GetField("RuntimeDir", Flags);
        object originalRuntime = runtime.GetValue(null);
        try
        {
            runtime.SetValue(null, Path.GetTempPath());
            object first = Call("Parse", "bili:7777", "bili");
            object second = Call("Parse", "bili:7777", "bili");
            first.GetType().GetField("SessionId").SetValue(first, "11111111111111111111111111111111");
            second.GetType().GetField("SessionId").SetValue(second, "22222222222222222222222222222222");
            Check(!Equals(Call("SessionRoomStatePath", first), Call("SessionRoomStatePath", second)),
                "相同房间的播放会话使用独立状态文件");
        }
        finally { runtime.SetValue(null, originalRuntime); }
        var urls = new Dictionary<string, string> {
            { "FULL_HD1", "https://example.test/full.flv" },
            { "HD1", "https://example.test/hd.flv" },
            { "SD1", "https://example.test/sd.flv" }
        };
        Check((string)Call("PickDouyinFlvUrlForQuality", urls, "high") == urls["HD1"], "抖音高清档选流");
        Check((string)Call("PickDouyinFlvUrlForQuality", urls, "smooth") == urls["SD1"], "抖音流畅档选流");
    }

    private static string CookieField(string text, string name)
    {
        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.Trim().Split('\t');
            if (fields.Length == 7 && fields[5] == name) return fields[3];
        }
        return null;
    }

    private static void TestCookieSecureFlag()
    {
        string directory = Path.Combine(Path.GetTempPath(), "live-hub-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object original = hub.GetField("Cookie", Flags).GetValue(null);
        try
        {
            string path = Path.Combine(directory, "bili-cookies.txt");
            hub.GetField("Cookie", Flags).SetValue(null, path);
            var jar = new CookieContainer();
            jar.Add(new Cookie("SESSDATA", "secret", "/", ".bilibili.com") { Secure = true });
            jar.Add(new Cookie("bili_jct", "csrf", "/", ".bilibili.com") { Secure = false });
            string saved = (string)Call("SaveCookieFile", jar,
                "https://passport.bilibili.com/login/crossDomain?DedeUserID=1&sid=from-url&SESSDATA=attacker");
            string text = File.ReadAllText(path, Encoding.UTF8);
            Check(saved == path && CookieField(text, "SESSDATA") == "TRUE", "Secure Cookie 保留 TRUE");
            Check(CookieField(text, "bili_jct") == "FALSE", "非 Secure Cookie 保持 FALSE");
            Check(CookieField(text, "sid") == "TRUE", "跳转 URL 带来的 Cookie 默认 TRUE");
            Check(text.Contains("\tSESSDATA\tsecret") && !text.Contains("\tSESSDATA\tattacker"),
                "登录跳转参数不能覆盖 Set-Cookie 得到的 SESSDATA");

            var secondJar = new CookieContainer();
            secondJar.Add(new Cookie("SESSDATA", "secret2", "/", ".bilibili.com") { Secure = true });
            Call("SaveCookieFile", secondJar, "https://evil.example/callback?sid=stolen");
            string second = File.ReadAllText(path, Encoding.UTF8);
            Check(!second.Contains("\tsid\tstolen"), "非 B站 跳转 URL 不能注入登录 Cookie");
        }
        finally
        {
            hub.GetField("Cookie", Flags).SetValue(null, original);
            Directory.Delete(directory, true);
        }
    }

    private static void TestQrLibraryPinning()
    {
        string directory = Path.Combine(Path.GetTempPath(), "live-hub-qr-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        FieldInfo field = hub.GetField("QrLibrary", Flags);
        object original = field.GetValue(null);
        try
        {
            string fake = Path.Combine(directory, "zxing.dll");
            byte[] payload = Encoding.UTF8.GetBytes("not a reviewed assembly");
            File.WriteAllBytes(fake, payload);
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(fake + ".sha256", hash, Encoding.UTF8);
            field.SetValue(null, fake);
            bool rejected = false;
            try { Call("VerifyQrLibrary"); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
            Check(rejected, "二维码 DLL：攻击者自带的同目录 .sha256 不能绕过内置哈希白名单");
        }
        finally
        {
            field.SetValue(null, original);
            Directory.Delete(directory, true);
        }
    }

    // ---------------- 抖音 ----------------

    private static object DouyinField(object room, string name)
    {
        if (room == null) return null;
        FieldInfo field = room.GetType().GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field == null ? null : field.GetValue(room);
    }

    // 复刻页面里的形态：self.__pace_f.push([1,"<JSON 字符串字面量>"])</script>
    private static string DouyinFlight(string innerJson, string tail)
    {
        string escaped = innerJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "<html><script>self.__pace_f.push([0])</script><script>self.__pace_f.push([1,\""
            + escaped + "\"" + tail + "</script></html>";
    }

    private static string DouyinRoomJson(int status, string title, string owner, string flv)
    {
        return "7:{\"state\":{\"roomStore\":{\"roomInfo\":{\"room\":{"
            + "\"status\":" + status + ",\"id_str\":\"723910165684\",\"title\":\"" + title + "\","
            + "\"owner\":{\"nickname\":\"" + owner + "\"},"
            + "\"stream_url\":{\"flv_pull_url\":{" + flv + "}}}}},\"streamStore\":{}}}";
    }

    private static void TestDouyinParse()
    {
        const string full = "\"FULL_HD1\":\"https://pull-flv.example/full.flv\","
            + "\"HD1\":\"https://pull-flv.example/hd.flv\","
            + "\"SD1\":\"https://pull-flv.example/sd1.flv\","
            + "\"SD2\":\"https://pull-flv.example/sd2.flv\"";

        // 页面结尾两种变体都要能解析（抖音按请求头/灰度返回不同形态）
        foreach (string tail in new[] { "])", ")]" })
        {
            object room = Call("ParseDouyinRoom", DouyinFlight(DouyinRoomJson(2, "测试房间", "测试主播", full), tail));
            Check(room != null
                && Equals(DouyinField(room, "Status"), 2)
                && Equals(DouyinField(room, "RoomId"), "723910165684")
                && Equals(DouyinField(room, "Title"), "测试房间")
                && Equals(DouyinField(room, "Owner"), "测试主播"),
                "抖音负载解析（结尾 " + tail + "）");
        }

        // 负载是 JSON 数组的形态（[{...}]）也要能解析
        string objectForm = DouyinRoomJson(2, "数组形态", "主播", full);
        object wrapped = Call("ParseDouyinRoom", DouyinFlight("7:[" + objectForm.Substring(2) + "]", "])"));
        Check(wrapped != null && Equals(DouyinField(wrapped, "Title"), "数组形态"), "抖音负载为数组形态时也能解析");

        object offline = Call("ParseDouyinRoom", DouyinFlight(DouyinRoomJson(4, "没开播", "主播", ""), "])"));
        Check(offline != null && Equals(DouyinField(offline, "Status"), 4), "抖音未开播仍能读出状态");

        object quoted = Call("ParseDouyinRoom", DouyinFlight(
            DouyinRoomJson(2, "主播 \\\"A\\\" --script=evil.lua", "引号主播", full), "])"));
        Check(quoted != null && ((string)DouyinField(quoted, "Title")).Contains("\"A\""),
            "抖音标题里的转义引号不破坏解析");

        Check(Call("ParseDouyinRoom", "<html><body>nothing</body></html>") == null, "抖音垃圾页面返回 null");
        Check(Call("ParseDouyinRoom", "") == null, "抖音空页面返回 null");
        Check(Call("ParseDouyinRoom", DouyinFlight("7:{\"state\":{\"streamStore\":{\"a\":1}}}", "])")) == null,
            "抖音缺少 roomStore 的负载被跳过");

        var urls = new Dictionary<string, string> { { "HD1", "https://pull-flv.example/hd.flv" } };
        Check((string)Call("PickDouyinFlvUrl", urls) == "https://pull-flv.example/hd.flv", "抖音缺原画时退高清");
        urls["FULL_HD1"] = "file:///C:/Windows/System32/calc.exe";
        Check((string)Call("PickDouyinFlvUrl", urls) == "https://pull-flv.example/hd.flv", "抖音拒绝非 http(s) 直链");
        urls["FULL_HD1"] = "https://pull-flv.example/full.flv";
        Check((string)Call("PickDouyinFlvUrl", urls) == "https://pull-flv.example/full.flv", "抖音默认取原画 FULL_HD1");
        Check((string)Call("PickDouyinFlvUrl", new Dictionary<string, string>()) == null, "抖音没有可用直链时返回 null");

        object target = Call("Parse", "douyin:723910165684", "bili");
        Check(target != null && Equals(DouyinField(target, "Platform"), "douyin")
            && Equals(DouyinField(target, "Url"), "https://live.douyin.com/723910165684"), "抖音房间号解析");
        object linked = Call("Parse", "https://live.douyin.com/723910165684", "bili");
        Check(linked != null && Equals(DouyinField(linked, "Platform"), "douyin"), "抖音链接解析");
        Check(Call("Parse", "https://live.douyin.com/abc", "bili") == null, "抖音非数字房间号被拒");
        Check(Call("Parse", "https://evil.example/douyin", "douyin") == null, "非抖音域名不会冒充抖音");
    }

    private static byte[] FlvHeader = { 70, 76, 86, 1, 5, 0, 0, 0, 9, 0, 0, 0, 0 };
    private static byte[] MetaTag = { 18, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 11 };

    private static byte[] VideoTag(int timestamp, byte marker)
    {
        return new byte[] { 9, 0, 0, 4, (byte)(timestamp >> 16), (byte)(timestamp >> 8),
            (byte)timestamp, (byte)(timestamp >> 24), 0, 0, 0, 0x17, marker, 0, 0, 0, 0, 0, 15 };
    }

    private static byte[] Join(params byte[][] parts)
    {
        using (var stream = new MemoryStream())
        {
            foreach (byte[] part in parts) stream.Write(part, 0, part.Length);
            return stream.ToArray();
        }
    }

    private static void TestPreamble()
    {
        byte[] video = VideoTag(2000, 2);
        using (var input = new MemoryStream(Join(FlvHeader, MetaTag, video)))
        {
            object[] args = { input, null };
            bool okay = (bool)Call("TrySkipFlvPreamble", args);
            byte[] header = args[1] as byte[];
            Check(okay && header != null && header[0] == 9 && input.ReadByte() == 0x17,
                "重连跳过元数据并保留视频 tag");
        }
        using (var input = new MemoryStream(Join(FlvHeader, MetaTag, MetaTag, video)))
        {
            object[] args = { input, null };
            bool okay = (bool)Call("TrySkipFlvPreamble", args);
            byte[] header = args[1] as byte[];
            Check(okay && header != null && header[0] == 9 && input.ReadByte() == 0x17,
                "连续元数据 tag 均被跳过");
        }
        using (var input = new MemoryStream(Join(FlvHeader, video)))
        {
            object[] args = { input, null };
            bool okay = (bool)Call("TrySkipFlvPreamble", args);
            byte[] header = args[1] as byte[];
            Check(okay && header != null && header.Length == 11 && header[0] == 9
                && input.ReadByte() == 0x17, "无元数据时保留首个视频 tag");
        }
        using (var input = new MemoryStream(new byte[] { 70, 76, 86 }))
        {
            object[] args = { input, null };
            Check(!(bool)Call("TrySkipFlvPreamble", args), "截断的 FLV 被拒绝");
        }
    }

    private static void ReadRequest(NetworkStream stream)
    {
        stream.ReadTimeout = 5000;
        int matched = 0;
        byte[] end = { 13, 10, 13, 10 };
        while (matched < 4)
        {
            int next = stream.ReadByte();
            if (next < 0) throw new EndOfStreamException();
            matched = next == end[matched] ? matched + 1 : (next == 13 ? 1 : 0);
        }
    }

    private static byte[] ReadExactly(NetworkStream stream, int count)
    {
        byte[] bytes = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int got = stream.Read(bytes, offset, count - offset);
            if (got <= 0) throw new EndOfStreamException();
            offset += got;
        }
        return bytes;
    }

    // 每个测试一个解析器实例：上一个测试遗留的 Pump 线程还在跑，
    // 要是共用静态端口，它会把下一个测试的假上游连接抢走（踩过一次）。
    private sealed class FakeResolver
    {
        public int Port;
        public int Phase;
        public bool OfflineAtPhase2;
        public bool FailWithoutOffline;
        public ManualResetEvent ResolveEntered;
        public ManualResetEvent ResolveRelease;

        public string Resolve(out bool offline)
        {
            offline = false;
            Phase++;
            if (ResolveEntered != null) ResolveEntered.Set();
            if (ResolveRelease != null && !ResolveRelease.WaitOne(5000)) throw new Exception("解析夹具未释放");
            if (FailWithoutOffline) return null;
            if (OfflineAtPhase2 && Phase == 2) { offline = true; return null; }
            return "http://127.0.0.1:" + Port + "/live.flv";
        }
    }

    // LiveHub 里的 RelayResolver 是私有嵌套委托，反射里得照着它的签名造一个
    private static Delegate RelayResolverFor(FakeResolver target)
    {
        Type resolverType = hub.GetNestedType("RelayResolver", BindingFlags.NonPublic);
        MethodInfo method = typeof(FakeResolver).GetMethod("Resolve");
        return Delegate.CreateDelegate(resolverType, target, method);
    }

    private static void TestReconnect()
    {
        byte[] first = Join(FlvHeader, MetaTag, VideoTag(1000, 1));
        byte[] secondVideo = VideoTag(2000, 2);
        byte[] second = Join(FlvHeader, MetaTag, secondVideo);
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var server = new Thread(delegate()
        {
            foreach (byte[] segment in new byte[][] { first, second })
            {
                using (TcpClient connection = upstream.AcceptTcpClient())
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/x-flv\r\nConnection: close\r\n\r\n");
                    stream.Write(response, 0, response.Length);
                    stream.Write(segment, 0, segment.Length);
                }
            }
            upstream.Stop();
        });
        server.IsBackground = true;
        server.Start();

        var downstream = new TcpListener(IPAddress.Loopback, 0);
        downstream.Start();
        int downstreamPort = ((IPEndPoint)downstream.LocalEndpoint).Port;
        using (var player = new TcpClient("127.0.0.1", downstreamPort))
        using (var relayClient = downstream.AcceptTcpClient())
        {
            downstream.Stop();
            Delegate resolver = RelayResolverFor(new FakeResolver { Port = upstreamPort });
            var pump = new Thread(delegate()
            {
                try { Call("Pump", relayClient, "douyu", "fixture", resolver); }
                catch (Exception) { }
            });
            pump.IsBackground = true;
            pump.Start();
            NetworkStream stream = player.GetStream();
            stream.ReadTimeout = 8000;
            byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n");
            stream.Write(request, 0, request.Length);
            ReadRequest(stream); // HTTP response header has the same CRLF terminator
            byte[] actual = ReadExactly(stream, first.Length + secondVideo.Length);
            byte[] expected = Join(first, secondVideo);
            Check(Convert.ToBase64String(actual) == Convert.ToBase64String(expected),
                "断流重连只跳过第二段 FLV 头和元数据");
        }
    }

    private static void TestIncompleteTagDiscard()
    {
        byte[] video = VideoTag(1000, 1);
        using (var complete = new MemoryStream(video))
            Check(Convert.ToBase64String((byte[])Call("ReadCompleteFlvTag", complete, null))
                == Convert.ToBase64String(video), "完整 FLV 标签按原样转发");
        using (var truncated = new MemoryStream(video, 0, video.Length - 3))
            Check(Call("ReadCompleteFlvTag", truncated, null) == null,
                "上游在 FLV 标签中途断开时丢弃残片");
    }

    private static void TestRelayOfflineWait()
    {
        byte[] first = Join(FlvHeader, MetaTag, VideoTag(1000, 1));
        byte[] secondVideo = VideoTag(2000, 2);
        byte[] second = Join(FlvHeader, MetaTag, secondVideo);

        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var server = new Thread(delegate()
        {
            foreach (byte[] segment in new byte[][] { first, second })
            {
                using (TcpClient connection = upstream.AcceptTcpClient())
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/x-flv\r\nConnection: close\r\n\r\n");
                    stream.Write(response, 0, response.Length);
                    stream.Write(segment, 0, segment.Length);
                }
            }
            upstream.Stop();
        });
        server.IsBackground = true;
        server.Start();

        string directory = Path.Combine(Path.GetTempPath(), "live-hub-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object originalRuntime = hub.GetField("RuntimeDir", Flags).GetValue(null);
        object originalRetry = hub.GetField("RelayRetrySeconds", Flags).GetValue(null);
        object originalLog = hub.GetField("RelayLogFile", Flags).GetValue(null);

        var downstream = new TcpListener(IPAddress.Loopback, 0);
        downstream.Start();
        int downstreamPort = ((IPEndPoint)downstream.LocalEndpoint).Port;
        try
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, directory);
            hub.GetField("RelayRetrySeconds", Flags).SetValue(null, 0);   // 测试里别真等 20 秒
            string relayLogPath = Path.Combine(directory, "relay.log");
            hub.GetField("RelayLogFile", Flags).SetValue(null, relayLogPath);
            var fake = new FakeResolver { Port = upstreamPort, OfflineAtPhase2 = true };

            using (var player = new TcpClient("127.0.0.1", downstreamPort))
            using (var relayClient = downstream.AcceptTcpClient())
            {
                downstream.Stop();
                Delegate resolver = RelayResolverFor(fake);
                bool running = true;
                var seen = new List<string>();
                var sampler = new Thread(delegate()
                {
                    // 每 20ms 看一眼状态文件，把出现过的状态记下来
                    while (running)
                    {
                        try
                        {
                            // 中转写的是连接状态文件（房间是否在播由启动器写，不在这里）
                            string path = Path.Combine(directory, "douyu-fixture.relay.txt");
                            if (File.Exists(path))
                            {
                                string text = File.ReadAllText(path, Encoding.UTF8).Trim();
                                if (text.Length > 0 && (seen.Count == 0 || seen[seen.Count - 1] != text)) seen.Add(text);
                            }
                        }
                        catch (Exception) { }
                        Thread.Sleep(20);
                    }
                });
                sampler.IsBackground = true;
                sampler.Start();

                var pump = new Thread(delegate()
                {
                    try { Call("Pump", relayClient, "douyu", "fixture", resolver); }
                    catch (Exception) { }
                });
                pump.IsBackground = true;
                pump.Start();

                NetworkStream stream = player.GetStream();
                stream.ReadTimeout = 8000;
                byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n");
                stream.Write(request, 0, request.Length);
                ReadRequest(stream);
                byte[] actual;
                try { actual = ReadExactly(stream, first.Length + secondVideo.Length); }
                catch (Exception ex)
                {
                    running = false;
                    Thread.Sleep(120);
                    string statePath = Path.Combine(directory, "douyu-fixture.relay.txt");
                    string stateText = File.Exists(statePath) ? File.ReadAllText(statePath, Encoding.UTF8) : "(无)";
                    string relayText = File.Exists(relayLogPath) ? File.ReadAllText(relayLogPath, Encoding.UTF8) : "(无)";
                    throw new Exception("下播重开测试读取失败: " + ex.Message
                        + " | 解析次数=" + fake.Phase + " | 状态文件=" + stateText
                        + " | 客户端待读=" + player.Available
                        + " | 观察到=" + string.Join(",", seen.ToArray())
                        + " | 中转日志=" + relayText.Replace(Environment.NewLine, " / "));
                }
                byte[] expected = Join(first, secondVideo);

                running = false;
                Thread.Sleep(80);
                Check(Convert.ToBase64String(actual) == Convert.ToBase64String(expected),
                    "下播重开后续喂同一条播放器连接");
                Check(seen.Contains("OFFLINE"), "下播时写出 OFFLINE 状态（播放器据此提示）");
                Check(seen.Contains("ONLINE"), "恢复后状态回到 ONLINE");
            }
        }
        finally
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, originalRuntime);
            hub.GetField("RelayRetrySeconds", Flags).SetValue(null, originalRetry);
            hub.GetField("RelayLogFile", Flags).SetValue(null, originalLog);
            try { Directory.Delete(directory, true); } catch (Exception) { }
        }
    }

    private static void TestRelayFailureSignal()
    {
        object originalRuntime = hub.GetField("RuntimeDir", Flags).GetValue(null);
        string directory = Path.Combine(Path.GetTempPath(), "live-hub-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, directory);
            string status = Path.Combine(directory, "status.txt");
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            Call("RunRelay", "invalid", "room", port, status);
            Check(File.ReadAllText(status, Encoding.UTF8) == "ERROR", "中转解析失败时发布错误状态");
        }
        finally { hub.GetField("RuntimeDir", Flags).SetValue(null, originalRuntime); Directory.Delete(directory, true); }
    }

    // 播放器在等首包时断开（实测 PotPlayer 约 1 秒没数据就断开重连）：
    // 中转必须把这条连接按「播放器断开」收尾，而不是掉进上游异常分支、再解析一次上游地址重连。
    // 出过的事故：一个断开的播放器让同一个连接在 25 秒里重解析了 30 多次抖音房间页（每次整页抓取）。
    private static void TestPlayerDisconnectStopsReconnect()
    {
        byte[] segment = Join(FlvHeader, MetaTag, VideoTag(1000, 1));
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var server = new Thread(delegate()
        {
            while (true)
            {
                TcpClient connection;
                try { connection = upstream.AcceptTcpClient(); } catch (Exception) { return; }
                using (connection)
                using (NetworkStream stream = connection.GetStream())
                {
                    ReadRequest(stream);
                    Thread.Sleep(600);          // 故意晚于播放器断开，复现「首包还没到，播放器已经走了」
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/x-flv\r\nConnection: close\r\n\r\n");
                    stream.Write(response, 0, response.Length);
                    stream.Write(segment, 0, segment.Length);
                }
            }
        });
        server.IsBackground = true;
        server.Start();

        string directory = Path.Combine(Path.GetTempPath(), "live-hub-disconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object originalRuntime = hub.GetField("RuntimeDir", Flags).GetValue(null);
        object originalLog = hub.GetField("RelayLogFile", Flags).GetValue(null);

        var relaySide = new TcpListener(IPAddress.Loopback, 0);
        relaySide.Start();
        int relayPort = ((IPEndPoint)relaySide.LocalEndpoint).Port;
        try
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, directory);
            string relayLogPath = Path.Combine(directory, "relay.log");
            hub.GetField("RelayLogFile", Flags).SetValue(null, relayLogPath);
            var fake = new FakeResolver { Port = upstreamPort };

            using (var player = new TcpClient("127.0.0.1", relayPort))
            using (TcpClient relayClient = relaySide.AcceptTcpClient())
            {
                relaySide.Stop();
                var pump = new Thread(delegate()
                {
                    try { Call("Pump", relayClient, "douyin", "room", RelayResolverFor(fake)); }
                    catch (Exception) { }
                });
                pump.IsBackground = true;
                pump.Start();

                byte[] request = Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                player.GetStream().Write(request, 0, request.Length);
                Thread.Sleep(150);
                player.Close();                  // 只发请求、不读响应就断开
                pump.Join(4000);
            }
            string log = File.Exists(relayLogPath) ? File.ReadAllText(relayLogPath, Encoding.UTF8) : "";
            Check(fake.Phase == 1, "播放器断开后中转不再重复解析上游地址");
            Check(log.Contains("播放器断开"), "播放器断开记成播放器断开（不是上游异常）");
        }
        finally
        {
            hub.GetField("RuntimeDir", Flags).SetValue(null, originalRuntime);
            hub.GetField("RelayLogFile", Flags).SetValue(null, originalLog);
            try { upstream.Stop(); } catch (Exception) { }
            try { relaySide.Stop(); } catch (Exception) { }
            try { Directory.Delete(directory, true); } catch (Exception) { }
        }
    }

    // 2026-09-20：把「解析」和「启动」拆开之后，--plan 的可信度就靠这条——计划必须是纯的，
    // 端口只能留占位符（真端口由中转绑定后回报），令牌必须进 URL。
    private static void TestPlanIsPure()
    {
        // 测试进程没跑过 Main，路径字段还是 null。这里给「存在的替身 + 临时 RuntimeDir」，
        // 这样同一份测试在开发机与绿色封装包里都能跑（它只校验计划内容，不启动播放器）。
        string originalMpv = (string)hub.GetField("Mpv", Flags).GetValue(null);
        string originalTitleScript = (string)hub.GetField("LiveTitleScript", Flags).GetValue(null);
        string originalRuntime = (string)hub.GetField("RuntimeDir", Flags).GetValue(null);
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "live-hub-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (originalMpv == null || !File.Exists(originalMpv))
                hub.GetField("Mpv", Flags).SetValue(null, Assembly.GetExecutingAssembly().Location);
            if (originalTitleScript == null)
                hub.GetField("LiveTitleScript", Flags).SetValue(null, "live-title.lua");
            hub.GetField("RuntimeDir", Flags).SetValue(null, directory);

            object target = Call("Parse", "douyu:952595", "bili");
            object plan = Call("ResolvePlan", target, "mpv", false, false);
            Type planType = plan.GetType();
            string arguments = (string)planType.GetField("Arguments").GetValue(plan);
            bool needsRelay = (bool)planType.GetField("NeedsRelay").GetValue(plan);
            string sessionId = (string)target.GetType().GetField("SessionId").GetValue(target);
            Check(needsRelay, "计划：走中转的平台标记 NeedsRelay");
            Check(arguments.Contains("{relay-port}"), "计划：端口留占位符（解析阶段不分配端口）");
            Check(!arguments.Contains("127.0.0.1:0"), "计划：没有假端口");
            Check(sessionId != null && sessionId.Length == 32 && arguments.Contains("?t=" + sessionId),
                "计划：中转 URL 带会话令牌");
            object biliPlan = Call("ResolvePlan", Call("Parse", "7777", "bili"), "mpv", false, false);
            Check(!(bool)biliPlan.GetType().GetField("NeedsRelay").GetValue(biliPlan), "计划：B站 不需要中转");
        }
        finally
        {
            hub.GetField("Mpv", Flags).SetValue(null, originalMpv);
            hub.GetField("LiveTitleScript", Flags).SetValue(null, originalTitleScript);
            hub.GetField("RuntimeDir", Flags).SetValue(null, originalRuntime);
            try { Directory.Delete(directory, true); } catch (Exception) { }
        }
    }

    // 中转只服务本进程拉起的播放器：URL 带会话令牌，首行不匹配就 403。
    // 无令牌模式（RelaySessionId=null）必须放行，否则旧的命令行与直接调 Pump 的离线测试全废。
    private static void TestRelayTokenGate()
    {
        string original = (string)hub.GetField("RelaySessionId", Flags).GetValue(null);
        try
        {
            const string token = "0123456789abcdef0123456789abcdef";
            hub.GetField("RelaySessionId", Flags).SetValue(null, token);
            Check((bool)Call("RelayRequestAllowed", "GET /live.flv?t=" + token + " HTTP/1.1"),
                "令牌门：令牌正确放行");
            Check((bool)Call("RelayRequestAllowed", "GET /live.flv?t=" + token.ToUpperInvariant() + " HTTP/1.1"),
                "令牌门：大小写不敏感");
            Check(!(bool)Call("RelayRequestAllowed", "GET /live.flv HTTP/1.1"), "令牌门：没有令牌拒绝");
            Check(!(bool)Call("RelayRequestAllowed", "GET /live.flv?t=00000000000000000000000000000000 HTTP/1.1"),
                "令牌门：错令牌拒绝");
            hub.GetField("RelaySessionId", Flags).SetValue(null, null);
            Check((bool)Call("RelayRequestAllowed", "GET /live.flv HTTP/1.1"),
                "令牌门：无令牌模式不拦（旧命令行 / 直接调 Pump）");
        }
        finally { hub.GetField("RelaySessionId", Flags).SetValue(null, original); }
    }

    // 斗鱼档位与码率提取：2026-09-20 在这段栽过一次（bit 单位误判 + rate=0 被过滤掉 +
    // 原画混进排序会悄悄改掉「高清优先」的档位），所以抽成纯函数并在这里钉死行为。
    private static void TestDouyuRatePicker()
    {
        object[] multirates = new object[]
        {
            Rate("原画1080P60", 0, 8456),
            Rate("蓝光4M", 4, 4000),
            Rate("超清", 3, 2000),
            Rate("高清", 2, 900)
        };
        MethodInfo pick = hub.GetMethod("PickDouyuRate", Flags);

        object[] args = new object[] { multirates, "best", 0 };
        int selected = (int)pick.Invoke(null, args);
        Check(selected == 0 && (int)args[2] == 8456, "斗鱼档位：原画 → 不换档，码率取 rate=0 那一档");

        args = new object[] { multirates, "high", 0 };
        selected = (int)pick.Invoke(null, args);
        Check(selected == 3 && (int)args[2] == 2000, "斗鱼档位：高清 → 转码三档取中间（原画不参与排序）");

        args = new object[] { multirates, "smooth", 0 };
        selected = (int)pick.Invoke(null, args);
        Check(selected == 2 && (int)args[2] == 900, "斗鱼档位：流畅 → 最低档");

        object[] noOriginal = new object[] { Rate("超清", 3, 2000), Rate("高清", 2, 900) };
        args = new object[] { noOriginal, "best", 0 };
        selected = (int)pick.Invoke(null, args);
        Check(selected == 0 && (int)args[2] == 0, "斗鱼档位：响应里没有原画档 → 留 0 不猜");

        args = new object[] { null, "high", 0 };
        selected = (int)pick.Invoke(null, args);
        Check(selected == 0 && (int)args[2] == 0, "斗鱼档位：没有 multirates → 不换档不猜码率");
    }

    private static Dictionary<string, object> Rate(string name, int rate, int bit)
    {
        return new Dictionary<string, object> { { "name", name }, { "rate", rate }, { "bit", bit } };
    }

    // 深度测量（onMetaData 缺 framerate/videodatarate 时用，斗鱼就是）：用合成 FLV 流钉死算法。
    private static void TestFlvWindowMeasure()
    {
        int payload = 1000;   // 注意别写成 const：常量会让 (byte)payload 变成编译期转换而报 CS0221
        var stream = new MemoryStream();
        for (int frame = 0; frame < 60; frame++)
        {
            int timestamp = frame * 16;                 // 16ms/帧 → 62.5fps
            byte[] tag = new byte[11 + payload + 4];
            tag[0] = 9;                                 // 9 = video tag
            tag[1] = (byte)(payload >> 16); tag[2] = (byte)(payload >> 8); tag[3] = (byte)payload;
            tag[4] = (byte)(timestamp >> 16); tag[5] = (byte)(timestamp >> 8); tag[6] = (byte)timestamp;
            int previous = payload + 11;
            int at = 11 + payload;
            tag[at] = (byte)(previous >> 24); tag[at + 1] = (byte)(previous >> 16);
            tag[at + 2] = (byte)(previous >> 8); tag[at + 3] = (byte)previous;
            stream.Write(tag, 0, tag.Length);
        }
        stream.Position = 0;
        object[] args = new object[] { stream, 8 * 1024 * 1024, 5, 0.0, 0 };
        bool ok = (bool)hub.GetMethod("MeasureFlvWindow", Flags).Invoke(null, args);
        double fps = (double)args[3];
        int kbps = (int)args[4];
        Check(ok && Math.Abs(fps - 62.5) < 1.0, "FLV 窗口测量：帧率 ≈62.5（实得 " + fps.ToString("0.0") + "）");
        Check(kbps > 400 && kbps < 700, "FLV 窗口测量：码率量级正确（实得 " + kbps + "kbps）");

        var single = new MemoryStream();
        byte[] one = new byte[11 + 100 + 4];
        one[0] = 9; one[3] = 100;
        int tail = 111;
        one[tail + 3] = (byte)tail;
        single.Write(one, 0, one.Length);
        single.Position = 0;
        object[] args2 = new object[] { single, 8 * 1024 * 1024, 5, 0.0, 0 };
        Check(!(bool)hub.GetMethod("MeasureFlvWindow", Flags).Invoke(null, args2),
            "FLV 窗口测量：样本不足时返回 false 而不是编一个数");
    }

    // 中转存活/并发规则（2026-09-20 从 RunRelay 里抽出来，原来这三条只有发布前的行为观察，没有回归）：
    //   并发上限 8、服务过连接后闲置 180 秒退、从没来过连接 120 秒退。
    private static void TestRelayPolicy()
    {
        MethodInfo accepts = hub.GetMethod("RelayAcceptsConnection", Flags);
        Check((bool)accepts.Invoke(null, new object[] { 7 }), "中转策略：第 8 条连接仍接受");
        Check(!(bool)accepts.Invoke(null, new object[] { 8 }), "中转策略：达到上限后拒绝新连接");

        MethodInfo exits = hub.GetMethod("RelayShouldExit", Flags);
        DateTime started = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        Check(!(bool)exits.Invoke(null, new object[] { false, 0, started, started, started.AddSeconds(119) }),
            "中转策略：没有连接来过时，120 秒内不退出");
        Check((bool)exits.Invoke(null, new object[] { false, 0, started, started, started.AddSeconds(121) }),
            "中转策略：空转超过 120 秒退出");
        Check(!(bool)exits.Invoke(null, new object[] { true, 1, started, started.AddSeconds(1000), started.AddSeconds(2000) }),
            "中转策略：还有活跃连接时永不退出");
        Check(!(bool)exits.Invoke(null, new object[] { true, 0, started, started.AddSeconds(100), started.AddSeconds(279) }),
            "中转策略：最后一个连接走后 180 秒内不退出（等 PotPlayer 重连）");
        Check((bool)exits.Invoke(null, new object[] { true, 0, started, started.AddSeconds(100), started.AddSeconds(281) }),
            "中转策略：闲置超过 180 秒退出");
    }
    // ----------------------------------------------------------------------------------------
    // 入口：登记用例 → 交给 TestRunner 逐个跑（2026-09-27 改造，评估报告 §2）。
    //
    // 参数：
    //   --exe <路径>      反射加载的启动器 exe（构建脚本用；目标被占用、构建到别处时也能量）
    //   --report <路径>   写 JSON 报告（每条用例的状态/耗时/断言数 + 汇总）
    //   --filter <子串>   只跑「名字或分类」含该子串的用例
    //   --list            只列出用例，不执行
    //   --verbose         逐条打印用例名，并打印异常堆栈
    // 退出码：0 = 无失败（跳过不算失败）；1 = 至少一个用例失败或启动器加载不了。
    // ----------------------------------------------------------------------------------------
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 4 && args[0] == "--audit-image-memory")
            return RunAuditImageMemory(args[1],args[2],args[3]);
        if (args.Length == 4 && args[0] == "--audit-flv-capture")
        {
            hub = Assembly.LoadFrom(args[1]).GetType("LiveHub",true);
            return RunAuditFlvCapture(args[2],args[3]);
        }
        if (args.Length > 0 && args[0] == "--config-writer") return RunConfigWriter(args);
        if (args.Length > 0 && args[0] == "--persistence-writer") return RunPersistenceWriter(args);
        if (args.Length == 1 && args[0] == "--emit-stderr")
        {
            Console.Error.Write(new string('e', 200000));
            Console.WriteLine("ok");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--hang") { Thread.Sleep(5000); return 0; }

        string launcher = null, filter = null, report = null, srcRoot = null;
        bool list = false, verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--exe" && i + 1 < args.Length) launcher = Path.GetFullPath(args[++i]);
            else if (args[i] == "--src" && i + 1 < args.Length) srcRoot = Path.GetFullPath(args[++i]);
            else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--report" && i + 1 < args.Length) report = Path.GetFullPath(args[++i]);
            else if (args[i] == "--list") list = true;
            else if (args[i] == "--verbose") verbose = true;
            else if (args[i] == "--audit-desktop") AuditDesktopEnabled = true;
        }

        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        // 开发版叫 看直播-mpv.exe，绿色封装包里叫 看直播.exe —— 两边都认，同一份测试源码通吃
        if (launcher == null)
            foreach (string name in new string[] { "看直播-mpv.exe", "看直播.exe" })
            {
                string candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) { launcher = candidate; break; }
            }
        if (launcher == null)
        {
            Console.Error.WriteLine("找不到启动器 exe（看直播-mpv.exe / 看直播.exe）：" + root);
            return 1;
        }

        try
        {
            hub = Assembly.LoadFrom(launcher).GetType("LiveHub", true);
        }
        catch (Exception ex)
        {
            // hub 都加载不了的话每个用例都会以同样的方式失败 —— 直接说清楚，别刷 40 遍
            Console.Error.WriteLine("加载启动器失败：" + launcher);
            Console.Error.WriteLine(ex);
            return 1;
        }

        // 与正式入口一致：在任何字体或控件被创建前启用 DPI 感知并注册字体。
        // 否则完整测试中晚注册字体会让 GDI 缓存与离屏位图使用不同的 DPI，截图文字被放大。
        hub.Assembly.GetType("DpiAwareness", true).GetMethod("Enable", Flags | BindingFlags.Public).Invoke(null, null);
        hub.Assembly.GetType("UiFonts", true).GetMethod("Register", Flags | BindingFlags.Public)
            .Invoke(null, new object[] { Path.Combine(Path.GetDirectoryName(launcher), "fonts") });
        System.Windows.Forms.Application.EnableVisualStyles();
        SourceRoot = srcRoot != null ? srcRoot : Path.GetDirectoryName(launcher);
        // 离线测试只能使用合成数据，不能沿用开发机/便携包的真实 cookie。
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
        InitializeReviewTestPaths();
        RegisterCases(launcher, Assembly.GetExecutingAssembly().Location);
        if (list) { TestRunner.List(); return 0; }
        return TestRunner.Run(filter, report, verbose) == 0 ? 0 : 1;
    }

    /// 登记全部用例。分类是第一列，用来一眼看出「哪一块红了」，也是 --filter 的匹配对象。
    private static void RegisterCases(string launcher, string self)
    {
        TestRunner.Add("启动器", "标题参数转义", delegate { TestArguments(); });
        TestRunner.Add("启动器", "mpv 空格、中文和逗号路径参数往返", TestMpvPathArguments);
        TestRunner.Add("播放", "控制条使用独立直播标题且保持纯计划", TestMpvMediaTitleHints);
        TestRunner.Add("解析", "虎牙短号标题网页兜底", TestHuyaTitleFallback);
        TestRunner.Add("启动器", "子进程输出捕获与超时", delegate { TestCapture(self); });
        TestRunner.Add("持久化", "收藏原子写入", delegate { TestAtomicWrite(); });
        TestRunner.Add("持久化", "收藏格式迁移", delegate { TestFavoritesMigration(); });
        TestRunner.Add("会话", "会话状态文件隔离与画质档", delegate { TestSessionAndQuality(); });
        TestRunner.Add("凭据", "Cookie Secure 标记", delegate { TestCookieSecureFlag(); });
        TestRunner.Add("凭据", "B站旁文件保存失败不冒充登录保存成功", TestBiliSidecarSaveFailure);
        TestRunner.Add("供应链", "二维码 DLL 只信内置哈希", delegate { TestQrLibraryPinning(); });
        TestRunner.Add("解析", "抖音房间页解析", delegate { TestDouyinParse(); });
        TestRunner.Add("中转", "FLV 预头跳过", delegate { TestPreamble(); });
        TestRunner.Add("中转", "不完整 tag 丢弃", delegate { TestIncompleteTagDiscard(); });
        TestRunner.Add("中转", "断流重连", delegate { TestReconnect(); });
        TestRunner.Add("中转", "中转失败状态回报", delegate { TestRelayFailureSignal(); });
        TestRunner.Add("中转", "播放器断开后不再重解析", delegate { TestPlayerDisconnectStopsReconnect(); });
        TestRunner.Add("中转", "解析失败时播放器断开立即结束", TestDisconnectDuringResolveFailure);
        TestRunner.Add("中转", "下播等待与恢复", delegate { TestRelayOfflineWait(); });
        TestRunner.Add("播放", "启动计划是纯函数", delegate { TestPlanIsPure(); });
        TestRunner.Add("中转", "中转请求令牌闸门", delegate { TestRelayTokenGate(); });
        TestRunner.Add("解析", "斗鱼码率档选择", delegate { TestDouyuRatePicker(); });
        TestRunner.Add("中转", "FLV 窗口度量", delegate { TestFlvWindowMeasure(); });
        TestRunner.Add("中转", "中转退出策略", delegate { TestRelayPolicy(); });
        TestRunner.Add("凭据", "结构化 cookie 与 ACL 结果如实反馈", delegate { TestCredentialStore(launcher); });
        TestRunner.Add("凭据", "按 host 选 cookie（缺失元数据时关闭发送）", delegate { TestCookieHeaderIntegration(launcher); });
        TestRunner.Add("弹幕-表情", "图片下载加固与位图生命周期", delegate { TestImageCacheHardening(launcher); });
        TestRunner.Add("弹幕-表情", "图片安全校验不阻塞渲染线程", TestImageValidationAsync);
        TestRunner.Add("弹幕-表情", "图片退避计时回绕与到期边界", TestImageBackoffWrap);
        TestRunner.Add("配置", "外部程序路径可配置（命令行/配置文件/PATH）", delegate { TestPathResolution(launcher); });
        TestRunner.Add("启动器-UI", "控件树结构（关键控件/同行不重叠/最小宽度）", delegate { TestLauncherUiStructure(launcher); });
        TestRunner.Add("启动器-平台", "平台适配器表与启动计划一致", delegate { TestPlatformAdapters(launcher); });
        TestRunner.Add("启动器-UI", "深色/浅色：每个控件的背景都在正确一侧", delegate { TestThemeColors(launcher); });
        TestRunner.Add("启动器-配置", "打开配置文件不走文件关联（不弹「打开方式」）", delegate { TestOpenConfigWithoutPicker(launcher); });
        TestRunner.Add("弹幕-渲染", "位图所有权：在飞的弹幕手里的位图不会被释放", delegate { TestSharedBitmapOwnership(launcher); });
        RegisterDanmakuCases(launcher);
        RegisterLauncherBadgeCases();
        RegisterReviewFixCases(launcher, self);
        RegisterCloseoutCases(launcher);
        RegisterLoginHealthCases();
        RegisterLoginBrowserCases();
        RegisterLookupCases();
        RegisterDpiCases();
        RegisterUiPolishCases();
        RegisterDetailReviewCases(launcher);
        RegisterPersistenceConcurrencyCases(launcher, self);
        RegisterAuditPendingCases();
    }
}
