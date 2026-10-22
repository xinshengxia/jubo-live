using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private const BindingFlags ReviewStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags ReviewInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static string ReviewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "livehub-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void InitializeReviewTestPaths()
    {
        string path = ReviewDirectory();
        hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, path);
        hub.GetField("Cookie", ReviewStatic).SetValue(null, Path.Combine(path, "bili-cookies.txt"));
        hub.GetField("Favorites", ReviewStatic).SetValue(null, Path.Combine(path, "favorites.json"));
        hub.GetField("History", ReviewStatic).SetValue(null, Path.Combine(path, "history.json"));
    }

    private static void RegisterReviewFixCases(string launcher, string self)
    {
        TestRunner.Add("审查修复", "七天清理保留凭据", TestReviewPrune);
        TestRunner.Add("审查修复", "私有写入保护旧目标且失败不冒充成功", TestReviewPrivateWrite);
        TestRunner.Add("审查修复", "profile 子项权限与继承", TestReviewProfile);
        TestRunner.Add("审查修复", "路径覆盖传到真实子进程", delegate { TestReviewChildPaths(launcher); });
        TestRunner.Add("审查修复", "重启保留原始参数与自定义数据路径", delegate { TestReviewLauncherRestart(launcher); });
        TestRunner.Add("审查修复", "HTTP 请求分片与截断", TestReviewRequest);
        TestRunner.Add("审查修复", "protobuf 恶意长度与截断", TestReviewProto);
        TestRunner.Add("审查修复", "斗鱼重连心跳退出", TestReviewHeartbeat);
        TestRunner.Add("审查修复", "抖音共享缓存与保存失败", TestReviewCookies);
        TestRunner.Add("审查修复", "配置完整复制与双进程合并", delegate { TestReviewConfig(launcher, self); });
    }

    private static object ReviewCall(Type type, string name, params object[] args)
    {
        return type.GetMethod(name, ReviewStatic).Invoke(null, args);
    }

    private static void TestReviewLauncherRestart(string launcher)
    {
        string dir = ReviewDirectory();
        try
        {
            string runtime = Path.Combine(dir, "合成 runtime") + Path.DirectorySeparatorChar;
            string config = Path.Combine(dir, "合成 config.json");
            string favorites = Path.Combine(dir, "合成 favorites.json");
            File.WriteAllText(config, "{}", Encoding.UTF8);
            string[] original = { launcher, "--self-test", "--runtime=" + runtime,
                "--config=" + config, "--favorites=" + favorites,
                "--cookie=" + Path.Combine(dir, "synthetic-cookie.txt"),
                "--history=" + Path.Combine(dir, "synthetic-history.json") };
            var info = (ProcessStartInfo)Call("CreateLauncherRestartInfo", new object[] { original });
            Check(info.FileName == System.Windows.Forms.Application.ExecutablePath,
                "重启同一启动器");
            Check(info.WorkingDirectory == Environment.CurrentDirectory && info.UseShellExecute,
                "重启保持工作目录与启动方式");
            string[] parsed = ParseCommandLine("launcher.exe " + info.Arguments);
            Check(parsed.Length == original.Length, "重启保留完整参数数量");
            for (int i = 1; i < original.Length; i++)
                Check(parsed[i] == original[i], "重启参数完整往返 " + i);
            string[] edgeCases = { launcher, "", "synthetic \"quoted\" value", "tail\\" };
            var edgeInfo = (ProcessStartInfo)Call("CreateLauncherRestartInfo", new object[] { edgeCases });
            string[] edgeParsed = ParseCommandLine("launcher.exe " + edgeInfo.Arguments);
            Check(edgeParsed.Length == edgeCases.Length, "空参数、引号与反斜杠保留数量");
            for (int i = 1; i < edgeCases.Length; i++)
                Check(edgeParsed[i] == edgeCases[i], "特殊重启参数完整往返 " + i);
            info.FileName = launcher;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("重启参数自检超时"); }
                Check(process.ExitCode == 0, "真实子进程接受重启参数");
            }
            string report = File.ReadAllText(Path.Combine(runtime, "selftest.txt"));
            Check(report.Contains("CONFIG=" + config), "重启继续读取自定义配置文件");
            Check(report.Contains("TOOL runtime = " + runtime), "重启继续使用自定义 runtime");
            Check(report.Contains("TOOL favorites = " + favorites), "重启继续使用自定义收藏路径");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TestReviewPrune()
    {
        string dir = ReviewDirectory();
        FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), cookie = hub.GetField("Cookie", ReviewStatic);
        FieldInfo favorites = hub.GetField("Favorites", ReviewStatic), history = hub.GetField("History", ReviewStatic);
        object oldRuntime = runtime.GetValue(null), oldCookie = cookie.GetValue(null);
        object oldFavorites = favorites.GetValue(null), oldHistory = history.GetValue(null);
        string[] keep = { "douyin-cookies.txt", "douyu-cookies.txt", "huya-cookies.txt", "douyin-cookies.txt.json", "session-private.txt", "session-private.txt.json", "bili-unknown.txt", "session-favorites.txt", "history.state.txt" };
        try
        {
            runtime.SetValue(null, dir); cookie.SetValue(null, Path.Combine(dir, "session-private.txt"));
            favorites.SetValue(null, Path.Combine(dir, "session-favorites.txt"));
            history.SetValue(null, Path.Combine(dir, "history.state.txt"));
            foreach (string name in keep) { File.WriteAllText(Path.Combine(dir, name), "synthetic"); File.SetLastWriteTimeUtc(Path.Combine(dir, name), DateTime.UtcNow.AddDays(-9)); }
            string stale = Path.Combine(dir, "session-old.state.txt"), fresh = Path.Combine(dir, "session-new.state.txt");
            File.WriteAllText(stale, "old"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-9)); File.WriteAllText(fresh, "new");
            Call("PruneRuntime");
            foreach (string name in keep) Check(File.Exists(Path.Combine(dir, name)), "清理保留 " + name);
            Check(!File.Exists(stale) && File.Exists(fresh), "只清理过期会话文件");
        }
        finally
        {
            runtime.SetValue(null, oldRuntime); cookie.SetValue(null, oldCookie);
            favorites.SetValue(null, oldFavorites); history.SetValue(null, oldHistory);
            Directory.Delete(dir, true);
        }
    }

    private static void TestReviewPrivateWrite()
    {
        string dir = ReviewDirectory(), path = Path.Combine(dir, "cookies.txt");
        FieldInfo injector = hub.GetField("AclFailureInjectorForTest", ReviewStatic);
        object previous = injector.GetValue(null);
        try
        {
            File.WriteAllText(path, "old");
            var acl = File.GetAccessControl(path);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            File.SetAccessControl(path, acl);
            bool observed = false;
            injector.SetValue(null, new Func<string, string>(delegate(string candidate) {
                if (candidate == path && File.ReadAllText(path) == "new")
                {
                    observed = true;
                    foreach (FileSystemAccessRule rule in File.GetAccessControl(path).GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        Check(!rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)), "替换后复核之前旧目标已收紧");
                }
                return null;
            }));
            Call("AtomicWritePrivateText", path, "new", "");
            Check(observed && File.ReadAllText(path) == "new", "替换已有凭据成功且复核可观测");
            injector.SetValue(null, new Func<string, string>(delegate(string candidate) { throw new UnauthorizedAccessException("synthetic ACL failure"); }));
            bool rejected = false;
            try { Call("AtomicWritePrivateText", path, "secret", ""); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is UnauthorizedAccessException; }
            Check(rejected && File.ReadAllText(path) == "new", "预保护失败不写新内容");
            Check(Directory.GetFiles(dir).Length == 1, "失败路径释放凭据临时文件");
        }
        finally { injector.SetValue(null, previous); Directory.Delete(dir, true); }
    }

    private static void TestReviewProfile()
    {
        string dir = ReviewDirectory(); Type login = hub.Assembly.GetType("PlatformLogin", true);
        try
        {
            string child = Path.Combine(dir, "Default"), existing = Path.Combine(child, "Cookies");
            Directory.CreateDirectory(child); File.WriteAllText(existing, "synthetic");
            Check((bool)ReviewCall(login, "RestrictProfile", dir), "profile 权限设置成功");
            Check(File.ReadAllText(existing) == "synthetic", "现有 profile 文件仍可读");
            string later = Path.Combine(child, "new-cookie"); File.WriteAllText(later, "synthetic");
            foreach (string file in new string[] { existing, later })
            {
                bool owner = false, broad = false;
                foreach (FileSystemAccessRule rule in File.GetAccessControl(file).GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    var sid = (SecurityIdentifier)rule.IdentityReference;
                    if (rule.AccessControlType != AccessControlType.Allow) continue;
                    if (sid.Equals(WindowsIdentity.GetCurrent().User)) owner = true;
                    else if (!sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) broad = true;
                }
                Check(owner && !broad, "已有/新增 profile 文件仅当前用户与 SYSTEM 可读");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TestReviewChildPaths(string launcher)
    {
        string dir = ReviewDirectory();
        string[] keys = { "mpv", "potplayer", "yt-dlp", "streamlink", "live-title", "live-resync", "runtime", "cookie", "favorites", "history", "qr-library" };
        string[] fields = { "Mpv", "PotPlayer", "YtDlp", "StreamlinkConsole", "LiveTitleScript", "LiveResyncScript", "RuntimeDir", "Cookie", "Favorites", "History", "QrLibrary" };
        string[] names = { "mpv.exe", "PotPlayerMini64.exe", "yt-dlp.exe", "streamlink.exe", "live-title.lua", "live-resync.lua", "", "cookies.txt", "favorites.json", "history.json", "zxing.dll" };
        object[] previous = new object[fields.Length];
        try
        {
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = hub.GetField(fields[i], ReviewStatic); previous[i] = field.GetValue(null);
                string path = i == 6 ? dir : Path.Combine(dir, "含 空格 " + i, names[i]);
                if (i != 6) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "synthetic"); } field.SetValue(null, path);
            }
            string args = (string)Call("ChildProcessPathArguments");
            string[] parsed = ParseCommandLine("child.exe" + args);
            Check(parsed.Length == keys.Length + 1, "全部路径作为独立参数传递");
            for (int i = 0; i < keys.Length; i++) Check(parsed[i + 1] == "--" + keys[i] + "=" + hub.GetField(fields[i], ReviewStatic).GetValue(null), "子进程参数 " + keys[i]);
            Check(RunLauncherExitCode(launcher, "--self-test" + args) == 0, "真实子进程接收路径覆盖");
            string report = File.ReadAllText(Path.Combine(dir, "selftest.txt"));
            foreach (string key in new string[] { "runtime", "cookie", "favorites", "mpv", "streamlink", "live-title", "live-resync" })
                Check(report.Contains("TOOL " + key + " = " + parsed[Array.IndexOf(keys, key) + 1].Substring(key.Length + 3)), "子进程实际解析 " + key);
        }
        finally { for (int i = 0; i < fields.Length; i++) hub.GetField(fields[i], ReviewStatic).SetValue(null, previous[i]); Directory.Delete(dir, true); }
    }

    private static void TestReviewRequest()
    {
        foreach (bool complete in new bool[] { true, false })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using (var client = new TcpClient())
            {
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                using (var server = listener.AcceptTcpClient())
                {
                    listener.Stop(); object[] result = { server.GetStream(), null }; Exception failure = null; bool accepted = false;
                    Thread reader = new Thread(delegate() { try { accepted = (bool)Call("TryReadRelayRequest", result); } catch (Exception ex) { failure = ex; } }); reader.Start();
                    foreach (string part in new string[] { "GET /live.flv?token=abc HTTP/1.1\r", "\nHost: localhost\r\n", complete ? "\r\n" : "" })
                    { byte[] bytes = Encoding.ASCII.GetBytes(part); client.GetStream().Write(bytes, 0, bytes.Length); Thread.Sleep(25); }
                    if (!complete) client.Client.Shutdown(SocketShutdown.Send);
                    Check(reader.Join(2000) && failure == null, "请求读取在分片/EOF 后结束");
                    Check(accepted == complete, "只接受完整 HTTP 头");
                    if (complete) Check((string)result[1] == "GET /live.flv?token=abc HTTP/1.1", "跨 TCP 分片完整恢复请求行");
                }
            }
        }
    }

    private static void TestReviewProto()
    {
        Type type = hub.Assembly.GetType("ProtoReader", true);
        foreach (byte[] vector in new byte[][] { new byte[] { 10, 250, 255, 255, 255, 15 }, new byte[] { 10, 5, 1 }, new byte[] { 9, 1 }, new byte[] { 128 }, new byte[] { 0 }, new byte[] { 15 } })
        {
            object reader = Activator.CreateInstance(type, ReviewInstance, null, new object[] { vector }, null);
            bool rejected = false;
            try
            {
                object[] tag = { 0, 0 };
                if ((bool)type.GetMethod("TryReadTag", ReviewInstance).Invoke(reader, tag)) type.GetMethod("Skip", ReviewInstance).Invoke(reader, new object[] { tag[1] });
            }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
            Check(rejected, "异常 protobuf 被明确拒绝：" + BitConverter.ToString(vector));
        }
        object good = Activator.CreateInstance(type, ReviewInstance, null, new object[] { new byte[] { 10, 2, 104, 98 } }, null);
        object[] validTag = { 0, 0 }; type.GetMethod("TryReadTag", ReviewInstance).Invoke(good, validTag);
        Check(Encoding.UTF8.GetString((byte[])type.GetMethod("ReadBytes", ReviewInstance).Invoke(good, null)) == "hb", "有效长度字段仍可解析");
        Type clientType = hub.Assembly.GetType("DouyinDanmakuClient", true);
        object client = Activator.CreateInstance(clientType, ReviewInstance, null, new object[] { "synthetic" }, null);
        object[] badFrame = { new byte[] { 10, 250, 255, 255, 255, 15 }, "", 0L };
        bool frameRejected = false;
        try { clientType.GetMethod("HandleFrame", ReviewInstance).Invoke(client, badFrame); }
        catch (TargetInvocationException ex) { frameRejected = ex.InnerException is InvalidDataException; }
        Check(frameRejected, "实际帧处理拒绝导致旧版死循环的六字节报文");
        clientType.GetMethod("HandleFrame", ReviewInstance).Invoke(client, new object[] { new byte[] { 16, 1 }, "", 0L });
        Check(true, "拒绝异常帧后可继续处理后续有效帧");
    }

    private static void TestReviewHeartbeat()
    {
        Type type = hub.Assembly.GetType("DouyuDanmakuClient", true);
        object instance = Activator.CreateInstance(type, ReviewInstance, null, new object[] { "synthetic" }, null);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using (var client = new TcpClient())
            {
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                using (var server = listener.AcceptTcpClient())
                {
                    listener.Stop(); client.SendTimeout = 1000;
                    type.GetField("_stream", ReviewInstance).SetValue(instance, client.GetStream());
                    type.GetField("_tcp", ReviewInstance).SetValue(instance, client);
                    type.GetMethod("StartHeartbeat", ReviewInstance).Invoke(instance, new object[] { client.GetStream(), 20 });
                    Thread heartbeat = (Thread)type.GetField("_heartbeat", ReviewInstance).GetValue(instance);
                    server.ReceiveTimeout = 1000;
                    byte[] header = ReviewReadExact(server.GetStream(), 12);
                    int length = BitConverter.ToInt32(header, 0);
                    Check(length == BitConverter.ToInt32(header, 4) && length >= 9 && length < 128, "本次连接收到完整 STT 心跳头");
                    Check(Encoding.UTF8.GetString(ReviewReadExact(server.GetStream(), length - 8)) == "type@=mrkl/\0", "心跳体完整且帧没有交错");
                    type.GetMethod("CloseQuietly", ReviewInstance).Invoke(instance, null);
                    Check(!heartbeat.IsAlive, "重连前旧心跳线程已退出 " + iteration);
                }
            }
        }
        type.GetMethod("Stop", ReviewInstance).Invoke(instance, null);
    }

    private static byte[] ReviewReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        for (int offset = 0; offset < count;)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0) throw new EndOfStreamException("合成 STT 帧截断");
            offset += read;
        }
        return buffer;
    }

    private static void TestReviewCookies()
    {
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic); object old = runtime.GetValue(null);
        Type login = hub.Assembly.GetType("PlatformLogin", true), guard = hub.Assembly.GetType("DouyinGuard", true);
        try
        {
            runtime.SetValue(null, dir);
            Check((string)ReviewCall(guard, "LoadCookies") == "", "预读空缓存");
            object platform = ReviewCall(login, "Get", "douyin");
            MethodInfo save = login.GetMethod("Save", ReviewStatic, null, new Type[] { platform.GetType(), typeof(string) }, null);
            Check((bool)save.Invoke(null, new object[] { platform, "ttwid=fresh" }), "独立登录保存成功");
            Check((string)ReviewCall(guard, "LoadCookies") == "ttwid=fresh", "Guard 立即读到独立登录的新 cookie");
            MethodInfo guardSave = guard.GetMethod("SaveCookies", ReviewStatic, null, new Type[] { typeof(string) }, null);
            string path = (string)ReviewCall(guard, "CookiesPath");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(!(bool)guardSave.Invoke(null, new object[] { "ttwid=unsaved" }), "保存失败必须返回 false");
                Check(!(bool)ReviewCall(guard, "PersistVerifiedCookies", "合成直播间", "ttwid=unsaved", null), "验证通过但保存失败不能返回成功");
            }
            Check((string)ReviewCall(guard, "LoadCookies") != "ttwid=unsaved", "失败 cookie 不进入共享缓存");
            Check(File.ReadAllText(path).Trim() == "ttwid=fresh", "锁定文件保存失败保留磁盘旧值");
        }
        finally { runtime.SetValue(null, old); Directory.Delete(dir, true); }
    }

    private static int RunConfigWriter(string[] args)
    {
        try
        {
            Type type = Assembly.LoadFrom(args[1]).GetType("DanmakuConfig", true);
            object config = type.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { args[2] });
            type.GetField(args[3]).SetValue(config, int.Parse(args[4]));
            File.WriteAllText(args[5], "ready");
            Stopwatch watch = Stopwatch.StartNew();
            while (!File.Exists(args[6]) && watch.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            if (!File.Exists(args[6])) return 2;
            return (bool)type.GetMethod("Save").Invoke(config, new object[] { args[2] }) ? 0 : 3;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 4; }
    }

    private static void TestReviewConfig(string launcher, string self)
    {
        string dir = ReviewDirectory(), path = Path.Combine(dir, "danmaku.json");
        Type configType = hub.Assembly.GetType("DanmakuConfig", true), overlayType = hub.Assembly.GetType("DanmakuOverlayForm", true);
        try
        {
            object first = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { path });
            Check((bool)configType.GetMethod("Save").Invoke(first, new object[] { path }), "建立初始配置");
            object stale = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { path });
            configType.GetField("DarkMode").SetValue(first, true); configType.GetField("MpvQuality").SetValue(first, "light"); configType.GetField("DanmakuDelayMs").SetValue(first, 3500);
            Check((bool)configType.GetMethod("Save").Invoke(first, new object[] { path }), "启动器保存新字段");
            configType.GetField("SpeedPercent").SetValue(stale, 130);
            Check((bool)configType.GetMethod("Save").Invoke(stale, new object[] { path }), "旧覆盖层合并保存成功");
            object merged = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { path });
            Check((bool)configType.GetField("DarkMode").GetValue(merged) && (string)configType.GetField("MpvQuality").GetValue(merged) == "light" && (int)configType.GetField("DanmakuDelayMs").GetValue(merged) == 3500 && (int)configType.GetField("SpeedPercent").GetValue(merged) == 130, "旧覆盖层保存不丢启动器设置");
            overlayType.GetMethod("CopyInto", ReviewStatic).Invoke(null, new object[] { stale, merged });
            foreach (string field in new string[] { "DarkMode", "MpvQuality", "DanmakuDelayMs", "HotkeyDelayLess", "HotkeyDelayMore" }) Check(object.Equals(configType.GetField(field).GetValue(stale), configType.GetField(field).GetValue(merged)), "热重载复制 " + field);
            string go = Path.Combine(dir, "go"), ready1 = Path.Combine(dir, "ready1"), ready2 = Path.Combine(dir, "ready2");
            string common = "--config-writer " + (string)Call("QuoteWindowsArg", launcher) + " " + (string)Call("QuoteWindowsArg", path);
            using (Process a = Process.Start(new ProcessStartInfo(self, common + " SpeedPercent 160 " + (string)Call("QuoteWindowsArg", ready1) + " " + (string)Call("QuoteWindowsArg", go)) { UseShellExecute = false, CreateNoWindow = true }))
            using (Process b = Process.Start(new ProcessStartInfo(self, common + " OpacityPercent 70 " + (string)Call("QuoteWindowsArg", ready2) + " " + (string)Call("QuoteWindowsArg", go)) { UseShellExecute = false, CreateNoWindow = true }))
            {
                Stopwatch watch = Stopwatch.StartNew(); while ((!File.Exists(ready1) || !File.Exists(ready2)) && watch.ElapsedMilliseconds < 4000) Thread.Sleep(10);
                bool ready = File.Exists(ready1) && File.Exists(ready2); File.WriteAllText(go, "go");
                Check(a.WaitForExit(6000) && b.WaitForExit(6000) && ready && a.ExitCode == 0 && b.ExitCode == 0, "两个进程从同一旧版本并发保存成功");
            }
            merged = configType.GetMethod("Load", ReviewStatic).Invoke(null, new object[] { path });
            Check((int)configType.GetField("SpeedPercent").GetValue(merged) == 160 && (int)configType.GetField("OpacityPercent").GetValue(merged) == 70, "跨进程互斥保留双方修改");
        }
        finally { Directory.Delete(dir, true); }
    }
}
