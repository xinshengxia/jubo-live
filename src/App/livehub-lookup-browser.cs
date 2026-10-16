using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static partial class LiveHub
{
    internal static class DouyinLookupBrowser
    {
        // Official Microsoft.Web.WebView2 1.0.4258.31 package, see licenses/SOURCES.json.
        // Check before calling the SDK-bearing method; no credential-bearing URL is logged.
        internal static void VerifyAssets(string directory)
        {
            string[] paths = { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
                "lib/webview2/x64/WebView2Loader.dll", "lib/webview2/x86/WebView2Loader.dll" };
            string[] hashes = { "D60E6E94245078CB56E44EBD5F360DC04BF18E460136BE93BFC4CFCA43B98505", "6A9E6856F23C463783C3AFB0CA5D78F56324C86657162CCB16D5227B3233B32A",
                "3426DCC55FDFB8B5E7AC623CF33B4CCF283FBF68A0C5A65BDDFDB4D749A8ABEE", "87C61E2CAE380112C60853E28178B8EA9A0D63739FC03CEAD680D4AD3FDE6B7D" };
            for (int i = 0; i < paths.Length; i++)
                using (var stream = File.OpenRead(Path.Combine(directory, paths[i])))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != hashes[i])
                        throw new InvalidDataException("网页辅助组件校验失败。");
        }
        internal static LookupResult Show(IWin32Window owner, string keyword) { return Show(owner, keyword, 0, false, true); }
        internal static LookupResult Show(IWin32Window owner, string keyword, int timeoutSeconds, bool offscreen, bool persistCookies)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 600) throw new LookupFailure("input", "请输入主播昵称或抖音号。");
            try { VerifyAssets(Path.GetDirectoryName(typeof(LiveHub).Assembly.Location)); }
            catch (Exception) { return new LookupResult { status = "blocked", error_code = "assets", note = "抖音网页辅助组件缺失或校验失败，请使用完整构建包。" }; }
            return ShowCore(owner, keyword.Trim(), timeoutSeconds, offscreen, persistCookies);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LookupResult ShowCore(IWin32Window owner, string keyword, int timeoutSeconds, bool offscreen, bool persistCookies)
        {
            using (var window = new BrowserWindow(keyword, timeoutSeconds, offscreen, persistCookies))
            { window.ShowDialog(owner); return window.Result; }
        }
        private sealed class BrowserWindow : DpiForm
        {
            private readonly WebView2 browser = new WebView2 { Dock = DockStyle.Fill };
            private readonly Label status = new ModernLabel { Dock = DockStyle.Fill, AutoSize = true };
            private readonly Button retry = new ModernButton { Text = "重新搜索", AutoSize = true };
            private readonly Button finish = new ModernButton { Text = "返回当前结果", AutoSize = true, Enabled = false };
            private readonly string keyword;
            private readonly bool offscreen, persist;
            private readonly int timeout;
            private readonly CancellationTokenSource closing = new CancellationTokenSource();
            private System.Windows.Forms.Timer timer;
            private LookupResult latest;
            private string cookieMarker;
            private bool reading, completing, disposed;
            internal LookupResult Result;
            internal BrowserWindow(string query, int seconds, bool hidden, bool save)
            {
                keyword = query; timeout = seconds; offscreen = hidden; persist = save;
                Text = "抖音网页辅助 · " + query; Name = "douyinLookupBrowser";
                DpiLayout.SetDesignFont(this, ModernTheme.UiFont); BackColor = ModernTheme.Surface;
                ClientSize = new Size(DpiLayout.S(1040), DpiLayout.S(760)); MinimumSize = new Size(DpiLayout.S(700), DpiLayout.S(500));
                StartPosition = FormStartPosition.CenterParent;
                if (offscreen) { FitWorkingArea = false; StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000); ShowInTaskbar = false; }
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(DpiLayout.S(8)) };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                status.Text = "正在初始化网页辅助；如需登录或验证，请在下方正常完成。结果不会自动收藏。";
                status.MaximumSize = new Size(DpiLayout.S(1000), 0);
                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
                var close = new ModernButton { Text = "关闭", AutoSize = true };
                retry.Enabled = false; retry.Click += delegate { if (browser.CoreWebView2 != null && !completing) { latest = null; finish.Enabled = false; status.Text = "正在搜索“" + keyword + "”…"; browser.CoreWebView2.Navigate(LookupUrls.SearchPage("douyin", keyword)); } };
                finish.Click += async delegate { if (latest != null) await Complete(latest); };
                close.Click += delegate { Close(); };
                actions.Controls.AddRange(new Control[] { retry, finish, close });
                layout.Controls.Add(status, 0, 0); layout.Controls.Add(browser, 0, 1); layout.Controls.Add(actions, 0, 2); Controls.Add(layout);
                Shown += async delegate { await Initialize(); };
                FormClosing += delegate { closing.Cancel(); if (timer != null) timer.Stop(); };
            }
            private async Task Initialize()
            {
                try
                {
                    string directory = Path.GetDirectoryName(typeof(LiveHub).Assembly.Location);
                    try { CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(directory, "lib", "webview2", Environment.Is64BitProcess ? "x64" : "x86")); }
                    catch (InvalidOperationException) { System.Diagnostics.Debug.WriteLine("WebView2 loader already initialized after a previous checked call."); }
                    string profile = Path.Combine(RuntimeDir, "douyin-webview-login-profile");
                    Directory.CreateDirectory(profile);
                    if (!PlatformLogin.RestrictProfile(profile)) throw new IOException("不能保护网页会话目录。");
                    cookieMarker = Path.Combine(profile, "source-cookie.stamp");
                    var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                    if (closing.IsCancellationRequested) return;
                    await browser.EnsureCoreWebView2Async(environment);
                    if (closing.IsCancellationRequested) return;
                    var core = browser.CoreWebView2;
                    core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                    core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; if (AllowedPage(e.Uri)) core.Navigate(e.Uri); };
                    core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { if (!AllowedPage(e.Uri)) { e.Cancel = true; status.Text = "已阻止离开抖音的页面跳转；可使用平台网页搜索。"; } };
                    var manager = core.CookieManager;
                    var existing = await manager.GetCookiesAsync("https://www.douyin.com/");
                    if (closing.IsCancellationRequested) return;
                    bool profileSession = false;
                    foreach (var cookie in existing) if (cookie.Name == "sessionid" || cookie.Name == "sessionid_ss") profileSession = true;
                    string currentStamp = DouyinUserSearch.SourceStamp();
                    string priorStamp = File.Exists(cookieMarker) ? File.ReadAllText(cookieMarker) : null;
                    if (!profileSession || priorStamp != currentStamp)
                        foreach (CookieRecord item in CredentialCookies.FilterByPlatform(CredentialCookies.Load(PlatformLogin.CookiesPath(PlatformLogin.Get("douyin"))), "douyin"))
                        {
                            try {
                                var cookie = manager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                                cookie.IsSecure = item.Secure; cookie.IsHttpOnly = item.HttpOnly;
                                if (item.ExpiresUtc > 0) cookie.Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(item.ExpiresUtc);
                                CoreWebView2CookieSameSiteKind sameSite;
                                if (item.SameSite != null && Enum.TryParse(item.SameSite, true, out sameSite)) cookie.SameSite = sameSite;
                                manager.AddOrUpdateCookie(cookie);
                            } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("WebView2 cookie import: " + ex.GetType().Name); }
                        }
                    File.WriteAllText(cookieMarker, currentStamp);
                    core.WebResourceResponseReceived += async delegate(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e) { await Receive(e); };
                    retry.Enabled = true; core.Navigate(LookupUrls.SearchPage("douyin", keyword));
                    status.Text = "正在搜索“" + keyword + "”；若需登录或验证，请完成后点“重新搜索”。仅回填当前关键词的账号候选。";
                    if (timeout > 0) { timer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, Math.Min(timeout, 120) * 1000) }; timer.Tick += delegate { Result = latest ?? new LookupResult { status = "blocked", error_code = "timeout", note = "网页辅助等待超时，没有判定账号不存在。" }; Close(); }; timer.Start(); }
                }
                catch (Exception)
                {
                    if (closing.IsCancellationRequested) return;
                    Result = new LookupResult { status = "blocked", error_code = "runtime", note = "无法初始化 WebView2；请检查本机 WebView2 Runtime 或使用平台网页搜索。" };
                    status.Text = Result.note;
                    if (offscreen) Close();
                }
            }
            internal static bool AllowedPage(string value)
            {
                if (value == "about:blank") return true;
                Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && LookupUrls.Basic(uri)
                    && (uri.Host == "douyin.com" || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));
            }
            private async Task Receive(CoreWebView2WebResourceResponseReceivedEventArgs e)
            {
                if (closing.IsCancellationRequested || completing || reading || e.Request.Method != "GET" || !DouyinUserSearch.MatchesRequest(e.Request.Uri, keyword)) return;
                reading = true;
                try
                {
                    if (e.Response.StatusCode != 200) { status.Text = "抖音搜索请求受阻，请查看网页提示或稍后重新搜索。"; return; }
                    string body;
                    using (var stream = await e.Response.GetContentAsync())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = new StringBuilder(); char[] chars = new char[4096]; int count;
                        while ((count = await reader.ReadAsync(chars, 0, chars.Length)) > 0) { closing.Token.ThrowIfCancellationRequested(); if (text.Length + count > 4 * 1024 * 1024) throw new InvalidDataException(); text.Append(chars, 0, count); }
                        body = text.ToString();
                    }
                    if (closing.IsCancellationRequested) return;
                    latest = DouyinUserSearch.Parse(body, keyword, "douyin_webview"); finish.Enabled = true; status.Text = latest.note;
                    if (latest.candidates.Count > 0) await Complete(latest);
                }
                catch (OperationCanceledException) { return; }
                catch (LookupFailure ex) { if (!closing.IsCancellationRequested) status.Text = ex.Message; }
                catch (Exception) { if (!closing.IsCancellationRequested) status.Text = "网页搜索响应暂无法读取，可重新搜索或关闭后重试。"; }
                finally { reading = false; }
            }
            private async Task Complete(LookupResult result)
            {
                if (completing || closing.IsCancellationRequested) return;
                completing = true;
                try
                {
                    if (persist && result.candidates.Count > 0)
                    {
                        var records = new List<CookieRecord>();
                        var currentCookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(null);
                        if (closing.IsCancellationRequested) return;
                        foreach (var item in currentCookies)
                            records.Add(new CookieRecord { Name = item.Name, Value = item.Value, Domain = item.Domain, Path = item.Path,
                                Secure = item.IsSecure, HttpOnly = item.IsHttpOnly, SameSite = item.SameSite.ToString(),
                                ExpiresUtc = item.IsSession ? 0 : (long)(item.Expires.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds });
                        records = CredentialCookies.FilterByPlatform(records, "douyin");
                        bool session = records.Exists(delegate(CookieRecord c) { return c.Name == "sessionid" || c.Name == "sessionid_ss"; });
                        if (session) {
                            if (!PlatformLogin.Save(PlatformLogin.Get("douyin"), CredentialCookies.RawHeader(records), new JavaScriptSerializer().Serialize(new { cookies = CookieJson(records) })))
                                result.note += " 本次候选已取得，但会话保存失败；下次可再次使用网页辅助。";
                            else File.WriteAllText(cookieMarker, DouyinUserSearch.SourceStamp());
                        }
                    }
                    if (!closing.IsCancellationRequested) { Result = result; DialogResult = DialogResult.OK; Close(); }
                }
                catch (Exception) { if (!closing.IsCancellationRequested) { result.note += " 会话未能保存，本次候选仍可核验。"; Result = result; Close(); } }
            }
            private static List<Dictionary<string, object>> CookieJson(List<CookieRecord> records)
            {
                var rows = new List<Dictionary<string, object>>();
                foreach (var c in records) rows.Add(new Dictionary<string, object> { { "name", c.Name }, { "value", c.Value }, { "domain", c.Domain }, { "path", c.Path }, { "secure", c.Secure }, { "httpOnly", c.HttpOnly }, { "expires", c.ExpiresUtc }, { "sameSite", c.SameSite } });
                return rows;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed) { disposed = true; closing.Cancel(); if (timer != null) timer.Dispose(); browser.Dispose(); closing.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
