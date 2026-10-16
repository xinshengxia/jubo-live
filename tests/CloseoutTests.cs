using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterCloseoutCases(string launcher)
    {
        TestRunner.Add("登录恢复", "本机 CDP 获取三平台凭据并保存作用域", TestLocalCdpCookies);
        TestRunner.Add("抖音弹幕", "抖音房间请求与验证码回退", TestDouyinRoomRecovery);
        TestRunner.AddDesktop("核对表闭环", "本地数据面板缩放与窗口伸缩", TestLocalDataLayout);
        TestRunner.Add("核对表闭环", "升级保留旧收藏与历史路径", TestLegacyUserData);
        TestRunner.Add("核对表闭环", "Cookie URL 作用域和重定向", TestCookieUrlScope);
        TestRunner.Add("核对表闭环", "真实 HTTP 重定向逐跳重选 Cookie", TestScopedHttpRedirects);
        TestRunner.Add("核对表闭环", "便携二维码库寻址与离线生成", delegate { TestPortableQrLibrary(launcher); });
        TestRunner.Add("核对表闭环", "日志轮换上限与凭据保留", TestBoundedLogs);
        TestRunner.Add("核对表闭环", "缓存清理与退出登录失败反馈", TestLocalDataManagement);
        TestRunner.Add("核对表闭环", "平台分类与脱敏观测", TestHealthObservations);
        TestRunner.Add("核对表闭环", "历史与自检报告写入失败可见", delegate { TestPersistenceFailures(launcher); });
        TestRunner.Add("核对表闭环", "构建版本可追踪", delegate {
            string value = (string)DanmakuType("BuildInfo").GetProperty("Text").GetValue(null, null);
            Check(value != "未知版本" && value.Contains("+"), "程序集具有可追踪 build ID");
        });
    }

    private static void TestLocalDataLayout()
    {
        Type dpi = DanmakuType("DpiLayout"), theme = DanmakuType("ModernTheme");
        FieldInfo scale = dpi.GetField("Scale", ReviewStatic); object savedScale = scale.GetValue(null);
        bool savedTheme = (bool)theme.GetProperty("IsDark").GetValue(null, null);
        try
        {
            foreach (float factor in new float[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            {
                scale.SetValue(null, 1f);
                using (Form dialog = (Form)ReviewCall(DanmakuType("LocalData"), "BuildDialog"))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-10000, -10000);
                    TextBox info = (TextBox)dialog.Controls.Find("info", true)[0];
                    FlowLayoutPanel actions = (FlowLayoutPanel)dialog.Controls.Find("actions", true)[0];
                    Control[] buttons = { dialog.Controls.Find("platforms", true)[0], dialog.Controls.Find("checkLogin", true)[0],
                        dialog.Controls.Find("checkAllLogins", true)[0], dialog.Controls.Find("loginAgain", true)[0],
                        dialog.Controls.Find("logout", true)[0], dialog.Controls.Find("clean", true)[0] };
                    info.Text = new string('中', 300) + Environment.NewLine + string.Join(Environment.NewLine, new string[80]);
                    dialog.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(dialog, new object[] { (int)Math.Round(96 * factor) });
                    dialog.Show(); Application.DoEvents();
                    int largeWidth = 0, largeHeight = 0, defaultButtonTop = -1, defaultToolbarHeight = 0;
                    Size defaultInfoSize = Size.Empty;
                    foreach (Size logical in new Size[] { new Size(700, 420), new Size(1100, 650), new Size(360, 320), new Size(700, 420) })
                    {
                        dialog.ClientSize = new Size((int)Math.Round(logical.Width * factor), (int)Math.Round(logical.Height * factor));
                        // Dock + AutoSize can post another layout after the first message pump.
                        // Compare steady layouts, rather than one transient frame under CPU load.
                        string previous = null; int stable = 0;
                        for (int pump = 0; pump < 40 && stable < 3; pump++)
                        {
                            dialog.PerformLayout(); actions.PerformLayout(); Application.DoEvents();
                            string current = actions.Size + "/" + buttons[2].Top + "/" + info.Size;
                            stable = current == previous ? stable + 1 : 0; previous = current;
                            Thread.Sleep(5);
                        }
                        Check(stable >= 3, "异步布局在限定时间内稳定");
                        string context = "DPI=" + factor + " 窗口=" + logical;
                        Rectangle available = info.Parent.DisplayRectangle;
                        Check(info.Visible && info.Width > 0 && info.Height >= (int)(70 * factor), context + "：正文确实可见，有可滚动阅读空间（高度=" + info.Height + "）");
                        Check(info.Left == available.Left && Math.Abs(info.Right - available.Right) <= 1
                            && Math.Abs(info.Bottom - available.Bottom) <= 1, context + "：正文填满窗口剩余宽高");
                        Check(info.Top >= actions.Bottom && info.Parent.ClientRectangle.Contains(info.Bounds), context + "：正文与操作区不重叠且不越界");
                        for (int a = 0; a < buttons.Length; a++)
                        {
                            Check(buttons[a].Visible && actions.ClientRectangle.Contains(buttons[a].Bounds), context + "：操作控件完整可见 " + a);
                            for (int b = a + 1; b < buttons.Length; b++)
                                Check(!buttons[a].Bounds.IntersectsWith(buttons[b].Bounds), context + "：操作控件不重叠 " + a + "/" + b);
                        }
                        if (logical.Width == 1100)
                        {
                            largeWidth = info.Width; largeHeight = info.Height;
                            Check(buttons[2].Top < (int)(20 * factor), context + "：宽窗口操作排成一行");
                        }
                        if (logical.Width == 360)
                        {
                            Check(info.Width < largeWidth && info.Height < largeHeight, context + "：正文随窗口缩小而收缩");
                            Check(buttons[2].Top > buttons[0].Top, context + "：窄窗口操作自动换行");
                        }
                        if (logical.Width == 700)
                        {
                            if (defaultButtonTop < 0)
                            {
                                defaultButtonTop = buttons[2].Top; defaultToolbarHeight = actions.Height; defaultInfoSize = info.Size;
                            }
                            else
                                Check(buttons[2].Top == defaultButtonTop && actions.Height == defaultToolbarHeight && info.Size == defaultInfoSize,
                                    context + "：从窄窗口恢复后布局与原尺寸一致，不残留多余操作行" + " expected=" + defaultButtonTop + "/" + defaultToolbarHeight + "/" + defaultInfoSize + " actual=" + buttons[2].Top + "/" + actions.Height + "/" + info.Size);
                        }
                    }
                    Check(info.ReadOnly && info.WordWrap && info.ScrollBars == ScrollBars.Vertical, "正文长文本自动换行并保留纵向滚动");
                    ReviewCall(theme, "Apply", !savedTheme);
                    Check(info.BackColor == (Color)theme.GetProperty("Card").GetValue(null, null)
                        && dialog.BackColor == (Color)theme.GetProperty("Surface").GetValue(null, null), "面板跟随深浅主题");
                    ReviewCall(theme, "Apply", savedTheme);
                    dialog.Close();
                }
            }
        }
        finally { scale.SetValue(null, savedScale); ReviewCall(theme, "Apply", savedTheme); }
    }

    private static void TestLegacyUserData()
    {
        string dir = ReviewDirectory(), app = Path.Combine(dir, "app"), local = Path.Combine(dir, "local"), profile = Path.Combine(dir, "profile");
        var overrides = (Dictionary<string, string>)hub.GetField("PathOverrides", ReviewStatic).GetValue(null);
        var config = (Dictionary<string, string>)hub.GetField("PathConfigValues", ReviewStatic).GetValue(null);
        var savedOverrides = new Dictionary<string, string>(overrides);
        var savedConfig = new Dictionary<string, string>(config);
        FieldInfo favorites = hub.GetField("Favorites", ReviewStatic), history = hub.GetField("History", ReviewStatic);
        object savedFavorites = favorites.GetValue(null), savedHistory = history.GetValue(null);
        try
        {
            overrides.Clear(); config.Clear();
            Directory.CreateDirectory(Path.Combine(profile, ".bili-live-watch"));
            string oldFavorites = Path.Combine(profile, ".bili-live-watch", "favorites.json");
            string oldHistory = Path.Combine(profile, ".bili-live-watch", "history.json");
            string json = "[{\"name\":\"升级前收藏\",\"room\":\"huya:333003\"},{\"name\":\"旧斗鱼\",\"room\":\"douyu:93589\"},{\"name\":\"旧抖音\",\"room\":\"douyin:6753235366\"}]";
            File.WriteAllText(oldFavorites, json); File.WriteAllText(oldHistory, "{\"bili\":[{\"Room\":\"7777\",\"Name\":\"旧播放\"}]}");
            string resolved = (string)Call("ResolveDevelopmentDataFile", "favorites", app, local, profile, "favorites.json");
            Check(resolved == oldFavorites, "升级原位找到当前用户旧收藏，无作者账号硬编码");
            favorites.SetValue(null, resolved);
            var entries = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(entries.Count == 3 && entries[0].Key == "升级前收藏", "实际收藏加载保留多平台数据，不退回两个默认收藏");
            resolved = (string)Call("ResolveDevelopmentDataFile", "history", app, local, profile, "history.json");
            Check(resolved == oldHistory, "升级原位找到旧历史"); history.SetValue(null, resolved);
            var loaded = (System.Collections.IDictionary)Call("LoadHistory");
            Check(loaded.Contains("bili") && ((System.Collections.ICollection)loaded["bili"]).Count == 1, "实际历史加载保留旧记录");
            Check(File.ReadAllText(oldFavorites) == json, "兼容读取不迁移或改写原收藏");
            string current = Path.Combine(local, "data", "favorites.json"); Directory.CreateDirectory(Path.GetDirectoryName(current));
            File.WriteAllText(current, "[]");
            Check((string)Call("ResolveDevelopmentDataFile", "favorites", app, local, profile, "favorites.json") == current, "已有新目录数据优先，避免覆盖升级后编辑");
            string bundled = Path.Combine(app, "data", "favorites.json"); Directory.CreateDirectory(Path.GetDirectoryName(bundled)); File.WriteAllText(bundled, "[]");
            Check((string)Call("ResolveDevelopmentDataFile", "favorites", app, local, profile, "favorites.json") == bundled, "程序旁已有数据仍优先");
            config["favorites"] = Path.Combine(dir, "explicit-config.json");
            Check((string)Call("ResolveDevelopmentDataFile", "favorites", app, local, profile, "favorites.json") == config["favorites"], "显式配置优先于兼容路径");
            overrides["favorites"] = Path.Combine(dir, "explicit-cli.json");
            Check((string)Call("ResolveDevelopmentDataFile", "favorites", app, local, profile, "favorites.json") == overrides["favorites"], "显式命令行优先于配置和旧路径");
            Check((string)Call("ResolveDevelopmentDataFile", "cookie", app, local, profile, "bili-cookies.txt") == Path.Combine(local, "data", "bili-cookies.txt"), "无旧数据的新安装保持 LocalAppData 默认值");
        }
        finally
        {
            favorites.SetValue(null, savedFavorites); history.SetValue(null, savedHistory);
            overrides.Clear(); foreach (var pair in savedOverrides) overrides.Add(pair.Key, pair.Value);
            config.Clear(); foreach (var pair in savedConfig) config.Add(pair.Key, pair.Value);
            Directory.Delete(dir, true);
        }
    }

    private static void TestPortableQrLibrary(string launcher)
    {
        string dir = ReviewDirectory(); FieldInfo library = hub.GetField("QrLibrary", ReviewStatic); object old = library.GetValue(null);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "lib")); Directory.CreateDirectory(Path.Combine(dir, "bin"));
            string current = Path.Combine(dir, "lib", "zxing.dll"), legacy = Path.Combine(dir, "bin", "zxing.dll");
            File.WriteAllBytes(legacy, new byte[0]);
            Check((string)Call("ResolvePortableQrLibrary", dir) == legacy, "兼容旧便携 bin 二维码库");
            File.WriteAllBytes(current, new byte[0]);
            Check((string)Call("ResolvePortableQrLibrary", dir) == current, "标准 lib 布局优先，避免加载旧 bin 副本");
            string actual = Path.Combine(Path.GetDirectoryName(launcher), "lib", "zxing.dll");
            Check(File.Exists(actual), "验收产物旁有真实受校验二维码库"); library.SetValue(null, actual);
            using (IDisposable bitmap = (IDisposable)Call("RenderQr", "offline-synthetic-regression"))
                Check(bitmap != null, "SHA-256 校验后可离线生成合成二维码，未访问平台");
        }
        finally { library.SetValue(null, old); Directory.Delete(dir, true); }
    }

    private static void TestPersistenceFailures(string launcher)
    {
        string dir = ReviewDirectory(); FieldInfo history = hub.GetField("History", ReviewStatic); object old = history.GetValue(null);
        try
        {
            string file = Path.Combine(dir, "history.json"); File.WriteAllText(file, "{}"); history.SetValue(null, file);
            MethodInfo save = hub.GetMethod("SaveHistory", ReviewStatic);
            object empty = Activator.CreateInstance(save.GetParameters()[0].ParameterType);
            using (FileStream locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!(bool)save.Invoke(null, new object[] { empty }), "历史文件占用时返回失败，不冒充已保存");
            Check((bool)save.Invoke(null, new object[] { empty }), "历史文件解锁后可保存");
            string runtime = Path.Combine(dir, "runtime"); Directory.CreateDirectory(runtime);
            string report = Path.Combine(runtime, "selftest.txt"); File.WriteAllText(report, "synthetic");
            string arguments = "--self-test \"--runtime=" + runtime + "\" \"--cookie=" + Path.Combine(dir, "cookie.txt")
                + "\" \"--favorites=" + Path.Combine(dir, "favorites.json") + "\" \"--history=" + file + "\"";
            using (FileStream locked = File.Open(report, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(RunLauncherExitCode(launcher, arguments) == 1, "自检报告无法写入时真实子进程退出 1");
            Check(RunLauncherExitCode(launcher, arguments) == 0 && File.ReadAllText(report).StartsWith("BUILD="), "解锁后自检退出 0 且报告写入 build ID");
        }
        finally { history.SetValue(null, old); Directory.Delete(dir, true); }
    }

    private static void TestCookieUrlScope()
    {
        Type cookies = DanmakuType("CredentialCookies");
        object records = NewRecordList(
            NewRecord("sid", "root", ".douyin.com", "/", true, true, 4102444800L),
            NewRecord("sid", "path", ".douyin.com", "/api", true, true, 4102444800L),
            NewRecord("host", "exact", "live.douyin.com", "/", false, true, 4102444800L),
            NewRecord("bad", "a; injected=yes", ".douyin.com", "/", false, true, 4102444800L));
        Func<string, string> header = delegate(string url) { return (string)ReviewCall(cookies, "HeaderForUri", records, new Uri(url)); };
        Check(header("https://live.douyin.com/api/list") == "sid=path; sid=root; host=exact", "不同路径同名 cookie 以长路径优先，非法标头值不发送");
        Check(header("https://live.douyin.com/apix") == "sid=root; host=exact", "路径必须在斜杠边界匹配");
        Check(header("https://sub.live.douyin.com/api") == "sid=path; sid=root", "host-only cookie 不扩散到子域");
        Check(header("http://live.douyin.com/api") == "host=exact", "Secure cookie 不在明文请求中发送");
        Check(header("https://evildouyin.com/api") == "", "伪装平台域得不到凭据");
        string mixed = "{\"cookies\":[{\"name\":\"ttwid\",\"value\":\"ok\",\"domain\":\".douyin.com\",\"path\":\"/\"},{\"name\":\"secret\",\"value\":\"evil\",\"domain\":\".evil.com\",\"path\":\"/\"}]}";
        Check((string)ReviewCall(DanmakuType("DouyinGuard"), "ExtractPlatformCookieHeader", mixed, "douyin") == "ttwid=ok", "CDP 混合域在提取裸头前过滤");
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic); object old = runtime.GetValue(null);
        try
        {
            runtime.SetValue(null, dir); Type login = DanmakuType("PlatformLogin");
            object platform = ReviewCall(login, "Get", "douyin");
            MethodInfo save = login.GetMethod("Save", ReviewStatic, null, new Type[] { platform.GetType(), typeof(string), typeof(string) }, null);
            Check((bool)save.Invoke(null, new object[] { platform, "ttwid=ok; secret=evil", mixed }), "混合 CDP 凭据能够保存");
            Check(File.ReadAllText(Path.Combine(dir, "douyin-cookies.txt")) == "ttwid=ok", "裸文件也不保留第三方 cookie");
            ReviewCall(cookies, "Save", Path.Combine(dir, "douyin-cookies.txt"), NewRecordList(NewRecord("sid", "expired", ".douyin.com", "/", true, true, 1)));
            Check((string)ReviewCall(login, "CookieHeaderForUri", new Uri("https://live.douyin.com/")) == "", "全过期元数据不能回退旧裸头");
        }
        finally { runtime.SetValue(null, old); Directory.Delete(dir, true); }
        Uri redirected = (Uri)Call("ScopedRedirectTarget", new Uri("https://live.douyin.com/a"), "https://8.8.8.8/b");
        Check(header(redirected.AbsoluteUri) == "", "跨域重定向重新选择作用域，不转发原 Cookie");
        bool rejected = false;
        try { Call("ScopedRedirectTarget", new Uri("https://live.douyin.com/a"), "http://live.douyin.com/b"); }
        catch (TargetInvocationException) { rejected = true; }
        Check(rejected, "HTTPS 重定向不能降级明文");
    }

    private static void TestScopedHttpRedirects()
    {
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic);
        FieldInfo transport = hub.GetField("ScopedGetTransportForTest", ReviewStatic), resolver = hub.GetField("ScopedRedirectResolverForTest", ReviewStatic);
        object oldRuntime = runtime.GetValue(null), oldTransport = transport.GetValue(null), oldResolver = resolver.GetValue(null);
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; string[] requests = new string[4]; Exception serverFailure = null;
        IWebProxy oldProxy = WebRequest.DefaultWebProxy; WebProxy syntheticProxy = new WebProxy("http://127.0.0.1:1"); bool proxyPreserved = false;
        Thread server = new Thread(delegate()
        {
            try
            {
                for (int i = 0; i < 4; i++) using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = client.SendTimeout = 4000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        StringBuilder request = new StringBuilder(); int previous = -1, breaks = 0;
                        while (request.Length < 8192)
                        {
                            int value = stream.ReadByte(); if (value < 0) throw new IOException("请求截断"); request.Append((char)value);
                            if (value == 10 && previous == 13) breaks++; else if (value != 13) breaks = 0;
                            previous = value; if (breaks == 2) break;
                        }
                        requests[i] = request.ToString();
                        string reply = i == 0 ? "HTTP/1.1 302 Found\r\nLocation: /apix\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : i == 1 ? "HTTP/1.1 302 Found\r\nLocation: https://www.example.com/api\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK";
                        byte[] bytes = Encoding.ASCII.GetBytes(reply); stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) { serverFailure = ex; }
        }); server.IsBackground = true; server.Start();
        try
        {
            runtime.SetValue(null, dir);
            ReviewCall(DanmakuType("CredentialCookies"), "Save", Path.Combine(dir, "douyin-cookies.txt"), NewRecordList(
                NewRecord("sid", "root", ".douyin.com", "/", true, true, 4102444800L),
                NewRecord("sid", "path", ".douyin.com", "/api", true, true, 4102444800L)));
            resolver.SetValue(null, new Func<string, IPAddress[]>(delegate(string host) { return new IPAddress[] { IPAddress.Parse("8.8.8.8") }; }));
            transport.SetValue(null, new Func<Uri, Action<HttpWebRequest>, HttpWebResponse>(delegate(Uri logical, Action<HttpWebRequest> configure)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/fixture");
                request.Proxy = null; request.Timeout = request.ReadWriteTimeout = 4000; request.KeepAlive = false; request.AllowAutoRedirect = false;
                configure(request);
                if (ReferenceEquals(request.Proxy, syntheticProxy)) proxyPreserved = true;
                request.Proxy = null; // 合成代理只验证配置，不建立连接；传输始终是 loopback。
                return (HttpWebResponse)request.GetResponse();
            }));
            Check((string)Call("HttpGetText", "https://live.douyin.com/api", null) == "OK", "三跳合成 HTTP 返回最终响应");
            WebRequest.DefaultWebProxy = syntheticProxy;
            using (HttpWebResponse response = (HttpWebResponse)Call("OpenScopedGet", "https://www.example.com/proxy", 4000, "offline", null, null, true))
                Check(response.StatusCode == HttpStatusCode.OK && proxyPreserved, "抖音请求保留系统代理配置，测试传输仍只使用 loopback");
            Check(server.Join(4000) && serverFailure == null, "合成 HTTP 服务器完成并退出");
            Check(requests[0].Contains("Cookie: sid=path; sid=root"), "首跳保留同名不同路径 Cookie");
            Check(requests[1].Contains("Cookie: sid=root") && !requests[1].Contains("sid=path"), "第二跳重新按路径选择");
            Check(!requests[2].Contains("Cookie:"), "跨域第三跳不携带平台 Cookie");
        }
        finally
        {
            transport.SetValue(null, oldTransport); resolver.SetValue(null, oldResolver); runtime.SetValue(null, oldRuntime);
            WebRequest.DefaultWebProxy = oldProxy;
            listener.Stop(); server.Join(4000); Directory.Delete(dir, true);
        }
    }

    private static void TestBoundedLogs()
    {
        Type log = DanmakuType("BoundedLog"); string dir = ReviewDirectory(), path = Path.Combine(dir, "danmaku.log");
        FieldInfo cookieField = hub.GetField("Cookie", ReviewStatic); object oldCookie = cookieField.GetValue(null);
        try
        {
            bool written = true;
            for (int i = 0; i < 370; i++) written &= (bool)ReviewCall(log, "TryAppend", path, new string('x', 16000));
            Check(written, "跨过五次轮换仍能写入");
            long total = 0; foreach (string file in Directory.GetFiles(dir)) total += new FileInfo(file).Length;
            Check(Directory.GetFiles(dir).Length <= 5 && total <= 5L * 1024 * 1024, "当前日志和四份归档的数量/总量受限");
            Check(File.ReadAllText(path).StartsWith("BUILD="), "轮换后首行仍保留 build ID");
            string cookie = Path.Combine(dir, "douyin-cookies.txt"); File.WriteAllText(cookie, "synthetic"); File.SetLastWriteTimeUtc(cookie, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); ReviewCall(log, "Maintain", dir);
            Check(!File.Exists(path) && File.Exists(cookie), "过期日志清理不触碰 cookie");
            for (int i = 0; i < 34; i++) File.WriteAllBytes(Path.Combine(dir, "session-" + i.ToString("x32") + ".log"), new byte[1024 * 1024]);
            ReviewCall(log, "Maintain", dir); total = 0;
            foreach (string file in Directory.GetFiles(dir)) if ((bool)ReviewCall(log, "OwnedName", Path.GetFileName(file))) total += new FileInfo(file).Length;
            Check(total <= 32L * 1024 * 1024, "目录总日志量受限");
            File.WriteAllText(path, "synthetic-credential"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); cookieField.SetValue(null, path);
            ReviewCall(log, "Maintain", dir);
            Check(File.Exists(path) && !(bool)ReviewCall(log, "TryAppend", path, "must-not-append") && File.ReadAllText(path) == "synthetic-credential", "即使 Cookie 覆盖到日志名，维护/轮换也不会删改凭据");
        }
        finally { cookieField.SetValue(null, oldCookie); Directory.Delete(dir, true); }
    }

    private static void TestLocalDataManagement()
    {
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic), cookie = hub.GetField("Cookie", ReviewStatic);
        object oldRuntime = runtime.GetValue(null), oldCookie = cookie.GetValue(null); Type data = DanmakuType("LocalData");
        try
        {
            runtime.SetValue(null, dir); cookie.SetValue(null, Path.Combine(dir, "session-private.txt"));
            foreach (string name in new string[] { "session-private.txt", "douyin-cookies.txt", "douyin-cookies.txt.json", "config.json", "favorites.json", "history.json", "session-123.state.txt" }) File.WriteAllText(Path.Combine(dir, name), "synthetic");
            string profile = Path.Combine(dir, "douyin-login-profile"); Directory.CreateDirectory(profile); File.WriteAllText(Path.Combine(profile, "Cookies"), "synthetic");
            string usage = (string)ReviewCall(data, "Usage");
            Check(usage.Contains("8 个文件") && usage.Contains("profile") && usage.Contains("凭据") && usage.Contains("临时缓存"), "占用统计包含文件数且区分凭据/profile/缓存");
            Check((string)ReviewCall(data, "CleanCache") == "", "缓存清理成功如实反馈");
            Check(!File.Exists(Path.Combine(dir, "session-123.state.txt")) && File.Exists((string)cookie.GetValue(null))
                && File.Exists(Path.Combine(dir, "douyin-cookies.txt")) && Directory.Exists(profile), "清缓存保留配置和登录态包括显式 Cookie 路径");
            using (FileStream locked = File.Open(Path.Combine(profile, "Cookies"), FileMode.Open, FileAccess.Read, FileShare.None))
                Check(((string)ReviewCall(data, "Logout", "douyin")).Length > 0, "profile 占用时退出登录报告未完成");
            Check((string)ReviewCall(data, "Logout", "douyin") == "", "解锁后可完成退出登录");
            Check(!Directory.Exists(profile) && !File.Exists(Path.Combine(dir, "douyin-cookies.txt.json")), "所选平台 Cookie 与 profile 被清除");
            Check(File.Exists(Path.Combine(dir, "config.json")) && File.Exists((string)cookie.GetValue(null)), "退出某平台保留配置和其他平台凭据");
        }
        finally { runtime.SetValue(null, oldRuntime); cookie.SetValue(null, oldCookie); Directory.Delete(dir, true); }
    }

    private static void TestHealthObservations()
    {
        Type health = DanmakuType("PlatformHealth");
        Check((string)ReviewCall(health, "Classify", new WebException("synthetic", WebExceptionStatus.Timeout)) == "timeout", "超时分类");
        Check((string)ReviewCall(health, "Classify", new System.Security.Authentication.AuthenticationException("synthetic")) == "auth-required", "认证错误分类");
        Check((string)ReviewCall(health, "Classify", new InvalidDataException("secret=sensitive")) == "parse-error", "解析错误分类");
        string summary = (string)ReviewCall(health, "ResponseSummary", "sessionid=secret&token=sensitive");
        Check(summary.Contains("sha256=") && !summary.Contains("secret") && !summary.Contains("sensitive"), "响应摘要不保留正文或凭据");
        string dir = ReviewDirectory(); FieldInfo runtime = hub.GetField("RuntimeDir", ReviewStatic); object old = runtime.GetValue(null);
        try
        {
            runtime.SetValue(null, dir);
            Check(((string)ReviewCall(health, "Describe")).Contains("尚无记录"), "没有观测记录不推断凭据是否存在");
            ReviewCall(health, "Record", "douyin", "playback", "parse-error");
            ReviewCall(health, "Record", "douyin", "login", "saved-unverified");
            Type roomState = hub.Assembly.GetType("RoomStateKind");
            ReviewCall(health, "RecordRoomState", "huya", "910323", Enum.Parse(roomState, "Replay"));
            string text = (string)ReviewCall(health, "Describe");
            Check(text.Contains("响应解析失败") && text.Contains("凭据已保存，待验证") && text.Contains("弹幕连接: 尚无记录"), "中文事件不冒充登录有效");
            Check(text.Contains("房间状态: 重播") && text.Contains("房间 910323"), "健康面板单独记录重播房间状态与房间号");
            ReviewCall(health, "RecordRoomState", "huya", "910323", Enum.Parse(roomState, "Unknown"));
            Check(((string)ReviewCall(health, "Describe")).Contains("房间状态: 本次未查到状态"), "健康面板 Unknown 覆盖陈旧重播值");
            File.WriteAllText(Path.Combine(dir, "health-douyin-danmaku.state.txt"), DateTime.UtcNow.AddHours(-1).Ticks + "|ready");
            Check(((string)ReviewCall(health, "Describe")).Contains("待重新观测；不表示 Cookie 过期"), "旧观测与 Cookie 过期明确区分");
        }
        finally { runtime.SetValue(null, old); Directory.Delete(dir, true); }
    }
}
