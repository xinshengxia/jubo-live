using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class LoginCapture
{
    internal static List<Dictionary<string, object>> Rows(List<CookieRecord> records)
    {
        var sorted = new List<CookieRecord>(records);
        sorted.Sort(delegate(CookieRecord a, CookieRecord b) {
            return string.CompareOrdinal(a.Domain + "\n" + a.Path + "\n" + a.Name + "\n" + a.Value,
                b.Domain + "\n" + b.Path + "\n" + b.Name + "\n" + b.Value); });
        var rows = new List<Dictionary<string, object>>();
        foreach (var c in sorted)
            rows.Add(new Dictionary<string, object> { { "name", c.Name }, { "value", c.Value }, { "domain", c.Domain },
                { "path", c.Path }, { "secure", c.Secure }, { "httpOnly", c.HttpOnly }, { "expires", c.ExpiresUtc }, { "sameSite", c.SameSite ?? "" } });
        return rows;
    }
    internal static string Json(List<CookieRecord> records) { return new JavaScriptSerializer().Serialize(new { cookies = Rows(records) }); }
    internal static string Fingerprint(List<CookieRecord> records)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json(records)))).Replace("-", "");
    }
    internal static bool HasMarker(string platform, List<CookieRecord> records)
    {
        if (PlatformLogin.Get(platform) == null) return false;
        foreach (CookieRecord record in CredentialCookies.FilterByPlatform(records, platform))
            if (record.Value.Length > 0 && (record.Name == PlatformLogin.Get(platform).Marker
                || platform == "douyin" && record.Name == "sessionid_ss"
                || platform == "douyu" && (record.Name == "acf_auth" || record.Name == "acf_stk"))) return true;
        return false;
    }
    internal static bool Save(LoginPlatform platform, List<CookieRecord> records, LoginHealth.Result verified)
    {
        records = CredentialCookies.FilterByPlatform(records, platform.Id);
        if (records.Count == 0) return false;
        if (!PlatformLogin.Save(platform, CredentialCookies.RawHeader(records), Json(records))) return false;
        LoginHealth.RememberVerified(platform.Id, records, verified);
        return true;
    }
}

internal static class PlatformLoginBrowser
{
    internal static bool AllowedPage(string platform, string value)
    {
        if (value == "about:blank") return true;
        Uri uri;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && CredentialCookies.DomainAllowed(uri.Host, platform);
    }
    internal static bool Show(IWin32Window owner, LoginPlatform platform)
    {
        try { LiveHub.DouyinLookupBrowser.VerifyAssets(Path.GetDirectoryName(typeof(LiveHub).Assembly.Location)); }
        catch (Exception)
        {
            MessageBox.Show(owner, "登录组件缺失或校验失败，请使用完整构建包。", platform.Name + "登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return ShowCore(owner, platform);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ShowCore(IWin32Window owner, LoginPlatform platform)
    {
        using (var window = new BrowserWindow(platform, true)) { window.ShowDialog(owner); return window.Saved; }
    }

    internal sealed class BrowserWindow : DpiForm
    {
        private readonly LoginPlatform platform;
        private readonly WebView2 browser = new WebView2 { Dock = DockStyle.Fill, Name = "loginWebPage" };
        internal readonly Label Status = new ModernLabel { Name = "loginBrowserStatus", Dock = DockStyle.Fill, AutoSize = true };
        internal readonly Button Retry = new ModernButton { Name = "loginBrowserRetry", Text = "重新检测", AutoSize = true, Enabled = false };
        internal readonly Button SavePending = new ModernButton { Name = "loginBrowserSavePending", Text = "保存后返回（待验证）", AutoSize = true, Enabled = false };
        internal readonly Button CloseButton = new ModernButton { Name = "loginBrowserClose", Text = "关闭", AutoSize = true };
        internal Func<Task<List<CookieRecord>>> ReadCookies;
        internal Func<string, List<CookieRecord>, LoginHealth.Result> Verify = LoginHealth.CheckRecords;
        internal Func<LoginPlatform, List<CookieRecord>, LoginHealth.Result, bool> Persist = LoginCapture.Save;
        internal bool Saved;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private List<CookieRecord> pending;
        private string lastAttempt;
        private string sourceMarker;
        private DateTime nextAttempt;
        private bool reading, closing, disposed;

        internal BrowserWindow(LoginPlatform loginPlatform, bool initialize)
        {
            platform = loginPlatform;
            Name = "platformLoginBrowser"; Text = platform.Name + "登录";
            ClientSize = new Size(1000, 740); MinimumSize = new Size(600, 440);
            StartPosition = FormStartPosition.CenterParent; TopMost = false;
            BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Text;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Status.Text = "在下方官网扫码或完成登录。程序会自动检测并保存，不需要点击“我已完成”。";
            Status.MaximumSize = new Size(960, 0); Status.Padding = new Padding(4, 8, 4, 8);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            actions.Controls.AddRange(new Control[] { Retry, SavePending, CloseButton });
            layout.Controls.Add(Status, 0, 0); layout.Controls.Add(browser, 0, 1); layout.Controls.Add(actions, 0, 2); Controls.Add(layout);
            ReadCookies = ReadBrowserCookies;
            Retry.Click += async delegate { await Poll(true); };
            SavePending.Click += async delegate { await SaveUnverified(); };
            CloseButton.Click += delegate { Close(); };
            timer.Tick += async delegate { await Poll(false); };
            FormClosing += delegate { closing = true; timer.Stop(); };
            Action theme = delegate { BackColor = ModernTheme.Surface; ForeColor = ModernTheme.Text; ModernTheme.ApplyWindowChrome(this); };
            ModernTheme.Changed += theme; Disposed += delegate { ModernTheme.Changed -= theme; };
            HandleCreated += delegate { ModernTheme.ApplyWindowChrome(this); };
            if (initialize) Shown += async delegate { await Initialize(); };
        }
        private async Task Initialize()
        {
            try
            {
                string directory = Path.GetDirectoryName(typeof(LiveHub).Assembly.Location);
                try { CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(directory, "lib", "webview2", Environment.Is64BitProcess ? "x64" : "x86")); }
                catch (InvalidOperationException) { System.Diagnostics.Debug.WriteLine("Using previously checked WebView2 loader."); }
                string profile = Path.Combine(LiveHub.RuntimeDir, platform.Id + "-webview-login-profile");
                Directory.CreateDirectory(profile);
                if (!PlatformLogin.RestrictProfile(profile)) throw new IOException("登录会话目录权限保护失败");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                if (closing) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closing) return;
                var core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    if (!AllowedPage(platform.Id, e.Uri)) { e.Cancel = true; Status.Text = "已阻止离开本平台的跳转，请使用当前官网完成登录。"; } };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) {
                    e.Handled = true; if (AllowedPage(platform.Id, e.Uri)) core.Navigate(e.Uri); };
                // Import only this app's scoped credentials, once per source revision. Keep browser renewals intact.
                string marker = Path.Combine(profile, "source-cookie.stamp");
                sourceMarker = marker;
                string stamp = LoginHealth.ReadLocal(platform.Id).Stamp;
                var existing = await core.CookieManager.GetCookiesAsync(null);
                if (closing) return;
                if (existing.Count == 0 || (File.Exists(marker) && File.ReadAllText(marker) != stamp))
                {
                    foreach (CookieRecord item in CredentialCookies.FilterByPlatform(CredentialCookies.Load(PlatformLogin.CookiesPath(platform)), platform.Id))
                    {
                        var cookie = core.CookieManager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                        cookie.IsSecure = item.Secure; cookie.IsHttpOnly = item.HttpOnly;
                        if (item.ExpiresUtc > 0) cookie.Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(item.ExpiresUtc);
                        CoreWebView2CookieSameSiteKind sameSite;
                        if (Enum.TryParse(item.SameSite, true, out sameSite)) cookie.SameSite = sameSite;
                        core.CookieManager.AddOrUpdateCookie(cookie);
                    }
                    if (stamp != null) File.WriteAllText(marker, stamp);
                }
                Retry.Enabled = true;
                core.Navigate(platform.Id == "douyu" ? "https://www.douyu.com/" : platform.LoginUrl);
                timer.Start(); await Poll(true);
            }
            catch (Exception)
            {
                if (!closing) Status.Text = "登录网页无法初始化，请检查本机 WebView2 Runtime 后重试。关闭按钮仍可用。";
            }
        }
        private async Task<List<CookieRecord>> ReadBrowserCookies()
        {
            var records = new List<CookieRecord>();
            if (browser.CoreWebView2 == null) return records;
            // null reads ALL domains in this independent context, including passport / SSO cookies.
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(null);
            foreach (var item in cookies)
                records.Add(new CookieRecord { Name = item.Name, Value = item.Value, Domain = item.Domain, Path = item.Path,
                    Secure = item.IsSecure, HttpOnly = item.IsHttpOnly, SameSite = item.SameSite.ToString(),
                    ExpiresUtc = item.IsSession ? 0 : (long)(item.Expires.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds });
            return CredentialCookies.FilterByPlatform(records, platform.Id);
        }
        internal async Task Poll(bool force)
        {
            if (reading || closing || Saved) return;
            reading = true;
            try
            {
                var records = CredentialCookies.FilterByPlatform(await ReadCookies(), platform.Id);
                if (closing) return;
                if (records.Count == 0) { Status.Text = "请在下方官网完成登录，程序会自动检测。"; return; }
                if (!LoginCapture.HasMarker(platform.Id, records)) { Status.Text = "等待网页登录，检测到登录凭据后会自动继续。"; return; }
                string fingerprint = LoginCapture.Fingerprint(records);
                if (!force && fingerprint == lastAttempt && DateTime.UtcNow < nextAttempt) return;
                lastAttempt = fingerprint; nextAttempt = DateTime.UtcNow.AddSeconds(8);
                Status.Text = "正在检查网页登录态…可以继续操作网页或关闭窗口。";
                LoginHealth.Result result = await Task.Run(delegate { return Verify(platform.Id, records); });
                if (closing) return;
                var current = CredentialCookies.FilterByPlatform(await ReadCookies(), platform.Id);
                if (closing) return;
                if (LoginCapture.Fingerprint(current) != fingerprint) { lastAttempt = null; return; }
                if (result.State == "valid")
                {
                    if (SaveCurrent(current, result)) { Saved = true; timer.Stop(); Status.Text = "登录已确认并保存。"; DialogResult = DialogResult.OK; Close(); }
                    else Status.Text = "登录已确认，但凭据保存失败；请检查目录权限后重试。";
                    return;
                }
                pending = LoginCapture.HasMarker(platform.Id, current) && result.State != "invalid" ? current : null;
                SavePending.Enabled = pending != null;
                Status.Text = result.State == "invalid" ? "平台尚未确认登录。请在下方完成登录，程序会自动继续检测。"
                    : LoginHealth.Title(result) + "。" + result.Reason + (pending != null ? " 也可保存后返回，状态会保留为待验证。" : "");
            }
            catch (Exception) { if (!closing) Status.Text = "暂未取得完整登录信息，请继续完成网页登录或重新检测。"; }
            finally { reading = false; }
        }
        private async Task SaveUnverified()
        {
            try
            {
                if (closing || pending == null) return;
                var current = CredentialCookies.FilterByPlatform(await ReadCookies(), platform.Id);
                if (closing) return;
                if (LoginCapture.Fingerprint(current) != LoginCapture.Fingerprint(pending)) { pending = null; SavePending.Enabled = false; await Poll(true); return; }
                if (SaveCurrent(current, null)) { Saved = true; DialogResult = DialogResult.OK; Close(); }
                else Status.Text = "登录信息未能保存，请检查目录权限后重试。";
            }
            catch (Exception) { if (!closing) Status.Text = "本次登录信息暂不能保存，请重新检测后重试。"; }
        }
        private bool SaveCurrent(List<CookieRecord> records, LoginHealth.Result result)
        {
            if (closing || !Persist(platform, records, result)) return false;
            // Saving this browser's cookies must advance its import marker too. Otherwise the next
            // open could overwrite browser-renewed credentials with the previous exported snapshot.
            if (sourceMarker != null)
                try { File.WriteAllText(sourceMarker, LoginHealth.ReadLocal(platform.Id).Stamp); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
            return true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { disposed = true; closing = true; timer.Dispose(); browser.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
