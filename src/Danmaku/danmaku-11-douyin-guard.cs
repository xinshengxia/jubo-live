// --------------------------------------------------------------------------------------------
// 2026-09-25：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// （在此之前这些模块是被生成器拼进 live-hub-v2.cs 的，靠宿主文件的 using 才编得过。）
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
// --------------------------------------------------------------------------------------------
// 抖音人机验证（风控）处理
//
// 背景：抖音的风控会给「不像浏览器」的请求返回一个 6 KB 的**验证码中间页**
// （`sec_sdk_build/*/captcha/index.js`），里面**没有房间数据**，所以取房间号会失败。
// 这个拦截是 **IP + 时间窗** 级别的，改请求头、换 UA、加 sec-ch-ua 都无效（都实测过）。
//
// 为什么不能内嵌验证码：
//   WinForms 自带的 WebBrowser 是 **IE11 内核**，实测能加载这个页面、标题也对，
//   但**永远停在加载动画上** —— 字节的验证码 SDK 是给现代浏览器写的，IE 跑不动。
//
// 所以走这条路（**零依赖**，只用 Windows 自带的 Edge + 它自己的调试协议）：
//   ① 用**独立 user-data-dir** 起一个 Edge 窗口，打开房间页；
//   ② 你在窗口里正常过验证码（滑块/点选，跟平时上网一样）；
//   ③ 程序通过 CDP（`http://127.0.0.1:<port>/json/list` + WebSocket）把 cookie 读出来；
//   ④ 存到 runtime\douyin-cookies.txt，之后的取页面请求带上它，风控就过了。
//
// 为什么不直接读 Chrome/Edge 的 cookie 数据库：那是 SQLite + DPAPI + AES-GCM，
// .NET Framework 4.8 连 `AesGcm` 都没有，得手写 GHASH —— 为了这点事不值得。
// 走 CDP 让浏览器自己把 cookie 交出来，代码量少一个数量级。
// --------------------------------------------------------------------------------------------

internal static class DouyinGuard
{
    /// 验证过的 cookie 存这儿；下次取页面直接带上
    internal static string CookiesPath()
    {
        return Path.Combine(LiveHub.RuntimeDir, "douyin-cookies.txt");
    }

    internal static string LoadCookies()
    {
        return PlatformLogin.LoadCookies("douyin");
    }

    /// 最近一次保存后的 ACL 结论（如实回传，别把「没保护上」说成「已收紧」）
    internal static LiveHub.AclOutcome LastAcl = LiveHub.AclOutcome.Failed;
    internal static string LastAclNote = "";

    /// 存 cookie：**原子写 + ACL 收紧**，并返回是否真的写成功。
    /// 抖音这份尤其敏感 —— 里面有 `sessionid`，是完整的账号登录态。
    internal static bool SaveCookies(string cookies)
    {
        return SaveCookies(cookies, null);
    }

    /// <summary>带 CDP 原始 JSON 的版本：额外落一份结构化旁文件（domain/path/secure/httpOnly/expires），
    /// 并且**只保留 douyin.com 域**的 cookie —— 浏览器上下文里混着的无关域不许进凭据文件。</summary>
    internal static bool SaveCookies(string cookies, string cdpJson)
    {
        lock (PlatformLogin.CredentialGate)
        {
            bool saved = PlatformLogin.Save(PlatformLogin.Get("douyin"), cookies, cdpJson);
            LastAcl = PlatformLogin.LastAcl;
            LastAclNote = PlatformLogin.LastAclNote;
            return saved;
        }
    }

    /// 这页是不是风控的验证码中间页（它的特征很明确：标题「验证码中间页」+ captcha sdk）
    internal static bool IsCaptchaPage(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return html.Contains("验证码中间页")
            || html.Contains("sec_sdk_build")
            || (html.Length < 20000 && html.Contains("captcha"));
    }

    /// 是不是**真的登录了**。
    /// 关键区分：只过滑块（不登录）拿到的 cookie **大约一天就失效** —— 风控从头再来一遍。
    /// 登录态有 `sessionid`，profile 会把它持久化，之后可以**静默自动续**，用户再也不用管。
    internal static bool IsLoggedIn(string cookies)
    {
        return !string.IsNullOrEmpty(cookies)
            && cookies.IndexOf("sessionid=", StringComparison.Ordinal) >= 0;
    }

    /// **静默重取**：用常驻的 Edge profile 在屏幕外开一次房间页，把 cookie 收回来。
    /// 登录过的话通常连验证码都不出现 —— 全程无需人工，这是「彻底修好」的关键一环。
    /// 返回 true = 拿到了能用的 cookie。
    internal static bool TryRefreshSilently(string room)
    {
        // ⚠️ 「冷却 + 只允许一次在跑」必须是**原子的 check-then-set**。
        //    原来只有一句无锁的 60 秒冷却判断：多个线程会同时通过 → 同时拉起多个 Edge，
        //    而它们共用同一个 --user-data-dir（第二个实例通常根本起不来），还会把风控惹毛。
        //    2026-09-30 开播标记改成并行查询后，这条路真的会被并发走到（会话轮询那条路本来就会）。
        lock (SilentGate)
        {
            if (_silentRunning) return false;                       // 已经有一次在跑，别叠
            if ((DateTime.UtcNow - _lastSilentAttempt).TotalSeconds < 60) return false;   // 冷却，别把风控惹毛
            _lastSilentAttempt = DateTime.UtcNow;
            _silentRunning = true;
        }
        try { return RefreshSilentlyCore(room); }
        finally { lock (SilentGate) { _silentRunning = false; } }
    }

    /// <summary>真正干活那段（屏幕外起 Edge + CDP 收 cookie）。single-flight 与冷却由调用方负责。</summary>
    private static bool RefreshSilentlyCore(string room)
    {
        string edge = FindEdge();
        if (edge == null) return false;
        int port = FreePort();
        string profileDir = Path.Combine(LiveHub.RuntimeDir, "douyin-verify-profile");
        Process browser = null;
        try
        {
            Directory.CreateDirectory(profileDir);
            if (!PlatformLogin.RestrictProfile(profileDir)) throw new IOException("验证 profile 权限保护失败");
            var start = new ProcessStartInfo(edge,
                "--user-data-dir=\"" + profileDir + "\""
                + " --remote-debugging-port=" + port
                + " --remote-debugging-address=127.0.0.1"
                + " --no-first-run --no-default-browser-check"
                + " --window-position=-32000,-32000 --window-size=520,700"    // 屏幕外，不打扰用户
                + " --app=\"https://live.douyin.com/" + room + "\"");
            start.UseShellExecute = false;
            browser = Process.Start(start);
            if (browser == null) return false;

            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(800);
                string title = ReadTitleViaCdp(port, null);
                string cookieJson = ReadCookiesViaCdpJson(port, "https://live.douyin.com/");
                string cookies = ExtractCookieHeader(cookieJson);
                if (Passed(title, cookies))
                {
                    if (!PersistVerifiedCookies(title, cookies, cookieJson)) return false;
                    DanmakuHost.Log("抖音：静默重取 cookie 成功（登录态=" + IsLoggedIn(cookies) + "）");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            DanmakuHost.Log("抖音：静默重取出错 " + ex.Message);
            return false;
        }
        finally
        {
            try { if (browser != null && !browser.HasExited) browser.Kill(); } catch (Exception) { }
        }
    }

    private static DateTime _lastSilentAttempt = DateTime.MinValue;

    /// <summary>静默重取的 single-flight：同一时刻只允许一个线程真正去拉起 Edge。</summary>
    private static readonly object SilentGate = new object();
    private static bool _silentRunning;

    /// 验证过没过，**不能只看 cookie** —— 实测没过验证时 `ttwid` / `s_v_web_id` 就已经有了，
    /// 只看它们会在验证码还挂着的时候就误判成功。
    /// 可靠信号是**页面标题**：风控页标题固定是「验证码中间页」，过了就变成房间标题。
    internal static bool Passed(string title, string cookies)
    {
        if (string.IsNullOrEmpty(cookies)) return false;
        // **必须**有 ttwid —— 它是真正的设备 cookie，风控认它。
        // 踩过：只拿到 `__ac_referer=__ac_blank` 也算「有 cookie」，于是误判成功，
        // 存下一份完全没用的 cookie，下次照样被拦。
        if (cookies.IndexOf("ttwid=", StringComparison.Ordinal) < 0) return false;
        if (!string.IsNullOrEmpty(title) && title.Contains("验证码中间页")) return false;
        // 标题拿不到（如静默重取时页面还没起完）时，只要 cookie 里已经有登录态就算过
        if (string.IsNullOrEmpty(title)) return IsLoggedIn(cookies);
        return true;
    }

    internal static bool PersistVerifiedCookies(string title, string cookies, string cookieJson)
    {
        return Passed(title, cookies) && SaveCookies(cookies, cookieJson);
    }

    /// 启动 Edge 让用户过验证码，成功后把 cookie 取回来。
    /// 返回 true = 拿到了可用 cookie。
    internal static bool Verify(IWin32Window owner, string room)
    {
        string edge = FindEdge();
        if (edge == null)
        {
            MessageBox.Show(owner,
                "没找到 Microsoft Edge，无法打开验证窗口。\r\n"
                + "请手动在浏览器里打开一次 https://live.douyin.com/" + room + " 并完成验证，然后再试。",
                "抖音验证", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        int port = FreePort();
        string profileDir = Path.Combine(LiveHub.RuntimeDir, "douyin-verify-profile");
        try
        {
            Directory.CreateDirectory(profileDir);
            if (!PlatformLogin.RestrictProfile(profileDir)) throw new IOException("验证 profile 权限保护失败");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "建不了验证用的临时目录：" + ex.Message, "抖音验证",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        string url = "https://live.douyin.com/" + room;
        Process browser = null;
        Form prompt = null;
        try
        {
            var start = new ProcessStartInfo(edge,
                "--user-data-dir=\"" + profileDir + "\""
                + " --remote-debugging-port=" + port
                + " --remote-debugging-address=127.0.0.1"
                + " --no-first-run --no-default-browser-check --disable-features=Translate"
                + " --disable-extensions --disable-sync --disable-component-extensions-with-background-pages"
                + " --window-size=560,760 --app=\"" + url + "\"");
            start.UseShellExecute = false;
            browser = Process.Start(start);
            if (browser == null) throw new Exception("Edge 没能启动");

            // 提示窗口：不挡着 Edge，用户过完验证点「我已完成」或等程序自动识别
            prompt = BuildPromptForm(room, browser);
            prompt.Show(owner);
            prompt.Activate();

            DateTime deadline = DateTime.UtcNow.AddMinutes(10);   // 扫码登录要翻手机，给足时间
            string cookies = null;
            string cookieJson = null;
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(700);
                if (prompt.IsDisposed) break;                       // 用户关了窗口 = 放弃
                string title = ReadTitleViaCdp(port, url);
                cookieJson = ReadCookiesViaCdpJson(port, url);
                cookies = ExtractCookieHeader(cookieJson);
                if (Passed(title, cookies)) break;
            }

            string verifiedTitle = ReadTitleViaCdp(port, url);
            if (Passed(verifiedTitle, cookies))
            {
                if (!PersistVerifiedCookies(verifiedTitle, cookies, cookieJson))
                {
                    MessageBox.Show(owner, "验证已通过，但 cookie 保存失败。请检查目录权限后重试。",
                        "抖音验证", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                bool loggedIn = IsLoggedIn(cookies);
                DanmakuHost.Log("抖音：验证完成（已登录=" + loggedIn + "）");
                if (!loggedIn)
                    MessageBox.Show(owner,
                        "验证过了，但**没有检测到登录态** —— 这份 cookie 大约一天后会失效，风控会再来。\r\n\r\n"
                        + "想彻底解决：下次弹验证窗口时**扫码登录**抖音，之后程序会自己在后台续 cookie，\r\n"
                        + "你就再也不用管了。",
                        "建议扫码登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            DanmakuHost.Log("抖音：验证窗口没等到有效 cookie");
            return false;
        }
        catch (Exception ex)
        {
            DanmakuHost.Log("抖音：验证流程出错 " + ex.Message);
            return false;
        }
        finally
        {
            if (prompt != null && !prompt.IsDisposed) { try { prompt.Close(); } catch (Exception) { } }
            // 只关我们自己起的那个 Edge 实例（用户平时开的 Edge 不受影响）
            try { if (browser != null && !browser.HasExited) browser.Kill(); } catch (Exception) { }
        }
    }

    private static Form BuildPromptForm(string room, Process browser)
    {
        Form form = new Form();
        form.Text = "抖音人机验证";
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.StartPosition = FormStartPosition.CenterParent;
        form.ClientSize = new Size(460, 250);
        form.Font = new Font("Microsoft YaHei", 9F);
        form.TopMost = true;

        Label text = new Label();
        text.SetBounds(18, 16, 424, 170);
        text.Text = "抖音要求人机验证，普通请求拿不到房间信息。\r\n"
            + "已经给你打开一个验证窗口（房间 " + room + "）。\r\n\r\n"
            + "★ **请扫码登录抖音** —— 这一步是「彻底修好」的关键：\r\n"
            + "  · 只过滑块不登录：cookie 大约**一天就失效**，风控会再来；\r\n"
            + "  · **登录**：登录态存在浏览器 profile 里，之后 cookie 到期\r\n"
            + "    程序会**自己在后台静默续上**，你再也不用管。\r\n\r\n"
            + "**登录完不用点任何按钮** —— 程序每秒检查一次，\r\n"
            + "一检测到登录态就自动收走 cookie 继续播放。";
        form.Controls.Add(text);

        Label wait = new Label();
        wait.ForeColor = Color.DimGray;
        wait.SetBounds(18, 190, 424, 20);
        wait.Text = "等待验证通过…（cookie 也可以直接贴进 runtime\\douyin-cookies.txt）";
        form.Controls.Add(wait);

        Button cancel = new Button();
        cancel.Text = "放弃";
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(354, 212, 88, 28);
        form.Controls.Add(cancel);
        form.CancelButton = cancel;

        return form;
    }

    internal static string FindEdge()
    {
        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe")
        };
        for (int i = 0; i < candidates.Length; i++)
            if (File.Exists(candidates[i])) return candidates[i];
        return null;
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// 用 CDP 把 cookie 读出来。
    /// ⚠️ 必须走 CDP，不能在页面里 `document.cookie` —— `ttwid` / `__ac_signature` 都是 HttpOnly。
    private static string ReadCookiesViaCdp(int port, string pageUrl)
    {
        return ExtractCookieHeader(ReadCookiesViaCdpJson(port, pageUrl));
    }

    /// 同上，但返回**原始 JSON** —— 结构化旁文件要靠它拿到 domain/path/secure/httpOnly/expires。
    internal static string ReadCookiesViaCdpJson(int port, string pageUrl)
    {
        try
        {
            return CdpCall(port, pageUrl,
                "{\"id\":1,\"method\":\"Network.getCookies\",\"params\":{\"urls\":[\"" + pageUrl + "\"]}}");
        }
        catch (Exception) { return null; }
    }

    /// 找那个抖音页面 target 的调试 WebSocket 地址
    /// 找页面 target 的调试 WebSocket。
    /// ⚠️ 独立 profile 也可能包含与目标平台无关的**扩展欢迎页 / 广告页**，
    /// 所以「随便挑一个非 edge:// 的页面」是错的 —— 会对着无关页面读标题和 cookie。
    /// 现在：给了 domainHint 就优先匹配它的页面；扩展页/欢迎页一律跳过。
    private static string FindPageSocket(int port, string domainHint)
    {
        string listJson = ReadLocalTargets(port);
        if (string.IsNullOrEmpty(listJson)) return null;
        var targets = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(
            listJson.Replace("\r", "").Replace("\n", ""));
        if (targets == null) return null;
        string fallback = null;
        foreach (Dictionary<string, object> target in targets)
        {
            object type, url, ws;
            if (!target.TryGetValue("type", out type) || Convert.ToString(type) != "page") continue;
            if (!target.TryGetValue("webSocketDebuggerUrl", out ws)) continue;
            Uri socketUri;
            if (!Uri.TryCreate(Convert.ToString(ws), UriKind.Absolute, out socketUri)
                || socketUri.Scheme != "ws" || socketUri.Host != "127.0.0.1" || socketUri.Port != port
                || socketUri.UserInfo.Length > 0 || socketUri.Query.Length > 0 || socketUri.Fragment.Length > 0
                || !socketUri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal)) continue;
            target.TryGetValue("url", out url);
            string pageUrl = Convert.ToString(url);
            if (pageUrl == null) continue;
            if (pageUrl.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)) continue;
            if (pageUrl.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) continue;
            if (pageUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) continue;
            if (domainHint != null && string.Equals(HostOf(pageUrl), domainHint, StringComparison.OrdinalIgnoreCase))
                return Convert.ToString(ws);
            if (fallback == null) fallback = Convert.ToString(ws);
        }
        return domainHint == null ? fallback : null;
    }

    // 本程序启动的 Edge CDP 是本机接口，不经过公网 HTTP 出口。
    // 固定回环地址/路径，无代理、Cookie 或重定向；端口由登录流程分配。
    private static string ReadLocalTargets(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.Timeout = request.ReadWriteTimeout = 4000;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("本机 CDP 列表响应无效");
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                const int limit = 1024 * 1024;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (read > limit - memory.Length) throw new InvalidDataException("本机 CDP 列表超过 1 MiB");
                    memory.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch (Exception) { return null; }
    }

    /// 发一条 CDP 命令，返回原始 JSON 响应
    internal static string CdpCall(int port, string pageUrl, string request)
    {
        try
        {
            string wsUrl = FindPageSocket(port, pageUrl == null ? null : HostOf(pageUrl));
            return wsUrl == null ? null : CdpCallOn(wsUrl, request);
        }
        catch (Exception ex)
        {
            // Edge 启动尚未监听时返回空，让原登录轮询继续；不能一次连接失败就退出。
            Diagnostics.Report("本机 CDP 列表读取", ex);
            return null;
        }
    }

    private static string CdpCallOn(string wsUrl, string request)
    {
        try
        {
            using (ClientWebSocket socket = new ClientWebSocket())
            using (CancellationTokenSource cts = new CancellationTokenSource(4000))
            using (MemoryStream collected = new MemoryStream())
            {
                socket.Options.Proxy = null;
                socket.ConnectAsync(new Uri(wsUrl), cts.Token).Wait();
                byte[] payload = Encoding.UTF8.GetBytes(request);
                socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cts.Token).Wait();
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    WebSocketReceiveResult result = socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cts.Token).Result;
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    if (result.Count > 8 * 1024 * 1024 - collected.Length) throw new InvalidDataException("本机 CDP 响应超过 8 MiB");
                    collected.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }
                return Encoding.UTF8.GetString(collected.ToArray());
            }
        }
        catch (Exception) { return null; }
    }

    /// 用 CDP 读页面标题 —— 判断验证过没过的可靠信号
    private static string ReadTitleViaCdp(int port, string pageUrl)
    {
        try
        {
            string response = CdpCall(port, pageUrl,
                "{\"id\":2,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"document.title\",\"returnByValue\":true}}");
            if (response == null) return null;
            int at = response.IndexOf("\"value\"", StringComparison.Ordinal);
            if (at < 0) return null;
            int start = response.IndexOf('"', at + 7);
            if (start < 0) return null;
            int end = start + 1;
            while (end < response.Length && response[end] != '"')
            {
                if (response[end] == '\\') end++;
                end++;
            }
            return response.Substring(start + 1, end - start - 1);
        }
        catch (Exception) { return null; }
    }

    /// 取这个浏览器上下文里的**全部** cookie（扫码登录用 —— 登录态可能挂在别的域名下，
    /// 按 url 过滤会漏）。取不到页面 target 时返回 null。
    internal static string CdpCallCookies(int port)
    {
        return ExtractCookieHeader(CdpCallCookiesJson(port));
    }

    /// 同上，但返回**原始 JSON**（调用方要落结构化旁文件）。
    internal static string CdpCallCookiesJson(int port)
    {
        return CdpCall(port, null, "{\"id\":1,\"method\":\"Storage.getCookies\"}");
    }

    /// ExtractCookieHeader 的对外入口：登录流程要**同一份 JSON**既取裸头、又落结构化旁文件。
    internal static string ExtractCookieHeaderPublic(string json)
    {
        return ExtractCookieHeader(json);
    }

    /// 从 CDP 的 JSON 响应里拼出 `k=v; k=v` 形式的 Cookie 头
    private static string ExtractCookieHeader(string json)
    {
        return ExtractPlatformCookieHeader(json, "douyin");
    }

    internal static string ExtractPlatformCookieHeader(string json, string platformId)
    {
        List<CookieRecord> records = CredentialCookies.FilterByPlatform(CredentialCookies.ParseCdp(json), platformId);
        string header = CredentialCookies.RawHeader(records);
        return header.Length == 0 ? null : header;
    }
}
