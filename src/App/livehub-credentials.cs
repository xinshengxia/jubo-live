// --------------------------------------------------------------------------------------------
// 凭据：B站扫码登录 + cookie 落盘 + ACL 收紧（partial 的另一半）。
//
// 2026-09-27 从 live-hub-v2.cs 搬出来（评估报告 §7：启动器是个大单体，按职责拆）。
// 这一块单独成文件的理由：它是**唯一**处理账号凭据的地方 ——
//   · 扫码登录（B站 走公开的 H5 接口，二维码用 zxing 渲染）；
//   · cookie 写成 yt-dlp/mpv 能读的 Netscape 格式 + 结构化旁文件；
//   · ACL 收紧与「收不了紧也必须如实说」的三态结果；
//   · zxing.dll 的哈希校验（它在拿着登录 Cookie 的进程里执行代码，必须防替换）。
// 代码本身是原样搬过来的，只补了 using。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    // ---------------- B站扫码登录（便携包里换机器后重新拿 Cookie） ----------------
    //
    // 流程照 B站 官方 H5 扫码接口：预热拿 buvid → generate 取二维码 → 轮询 poll，
    // 登录成功后 Cookie 有两个来源：响应头的 Set-Cookie（CookieContainer 收着）和 poll 返回的
    // data.url 里的 query（B站经常走这条），两个都取，最后写成 yt-dlp 能读的 Netscape 格式。
    private static void StartBiliQrLogin(IWin32Window owner)
    {
        if (!File.Exists(QrLibrary))
        {
            MessageBox.Show("缺少二维码组件（zxing.dll）：\n" + QrLibrary, "B站扫码登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var jar = new CookieContainer();
        string qrKey;
        string qrUrl;
        if (!RequestQrCode(jar, out qrKey, out qrUrl))
        {
            MessageBox.Show("申请二维码失败，检查网络后重试。", "B站扫码登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Form dialog = new DpiForm();
        dialog.Text = "B站扫码登录";
        dialog.ClientSize = new Size(DpiLayout.S(400), DpiLayout.S(500));
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        DpiLayout.SetDesignFont(dialog, new Font("Microsoft YaHei", 9F));
        // 同样要跟着 DPI 缩放（下面二维码/文字/按钮都是 96 DPI 的像素值）
        dialog.AutoScaleDimensions = new SizeF(96F, 96F);
        dialog.AutoScaleMode = AutoScaleMode.None;
        PictureBox picture = new PictureBox();
        picture.SetBounds(20, 16, 360, 360);
        picture.SizeMode = PictureBoxSizeMode.Zoom;
        picture.Image = RenderQr(qrUrl);
        dialog.Controls.Add(picture);
        Label tip = new ModernLabel();
        tip.SetBounds(20, 386, 360, 44);
        tip.Text = "用手机 B站 App 扫码，然后在手机上确认";
        dialog.Controls.Add(tip);
        Label hint = new ModernLabel();
        hint.SetBounds(20, 430, 360, 20);
        hint.ForeColor = ModernTheme.TextSecondary;
        hint.Text = "登录信息只写入本机的 " + Path.GetFileName(Cookie);
        dialog.Controls.Add(hint);
        Button close = new ModernButton();
        close.Text = "关闭";
        close.SetBounds(150, 456, 100, 30);
        close.Click += delegate { dialog.Close(); };
        dialog.Controls.Add(close);

        bool saved = false;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        timer.Interval = 2000;
        timer.Tick += delegate
        {
            string message;
            string redirect;
            int code = PollQrCode(jar, qrKey, out message, out redirect);
            if (code == 0)
            {
                timer.Stop();
                string file = SaveCookieFile(jar, redirect);
                saved = file != null;
                tip.ForeColor = saved ? ModernTheme.Success : ModernTheme.Danger;
                // 收紧失败（U 盘 / 共享盘）时必须说出来 —— 静默按「已保护」呈现是误导。
                tip.Text = saved
                    ? "登录成功，Cookie 已保存到 " + file
                        + (CookieAcl == AclOutcome.Applied ? "" : "\r\n⚠ " + DescribeAcl(CookieAcl, CookieAclNote))
                    : "登录成功但保存 Cookie 失败";
                if (saved) { System.Windows.Forms.Timer later = new System.Windows.Forms.Timer(); later.Interval = 1500; later.Tick += delegate { later.Stop(); dialog.Close(); }; later.Start(); }
            }
            else if (code == 86038)
            {
                // 二维码过期：自动换一张
                string key, url;
                if (RequestQrCode(jar, out key, out url))
                {
                    qrKey = key;
                    picture.Image = RenderQr(url);
                    tip.Text = "二维码已刷新，请重新扫码";
                }
                else tip.Text = "二维码已过期，且刷新失败，请关闭重试";
            }
            else if (code == 86090) tip.Text = "已扫码，请在手机上点确认";
            else if (code == 86101) tip.Text = "等待扫码…（用手机 B站 App 扫上面的二维码）";
            else tip.Text = message == null || message.Length == 0 ? "等待扫码…" : message;
        };
        dialog.Shown += delegate { timer.Start(); };
        dialog.FormClosed += delegate { timer.Stop(); };
        // 同启动器：DPI 缩放的唯一入口，必须在所有控件建完之后
        DpiLayout.ScaleTree(dialog);
        dialog.ShowDialog(owner);
    }

    internal static void ShowBiliLogin(IWin32Window owner) { StartBiliQrLogin(owner); }

    private static bool RequestQrCode(CookieContainer jar, out string qrKey, out string qrUrl)
    {
        qrKey = null;
        qrUrl = null;
        try
        {
            HttpGetWithJar("https://www.bilibili.com/", jar, null);   // 预热：少了这步 B站不认这个 session
            string json = HttpGetWithJar("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", jar, "https://www.bilibili.com/");
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object codeValue, dataValue;
            if (root == null || !root.TryGetValue("code", out codeValue) || Convert.ToInt32(codeValue) != 0) return false;
            var data = root.TryGetValue("data", out dataValue) ? dataValue as Dictionary<string, object> : null;
            if (data == null) return false;
            qrUrl = Field(data, "url");
            qrKey = Field(data, "qrcode_key");
            return qrUrl != null && qrKey != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 返回 data.code（0=成功，86101 未扫码，86090 已扫码待确认，86038 已过期）
    private static int PollQrCode(CookieContainer jar, string qrKey, out string message, out string redirect)
    {
        message = null;
        redirect = null;
        try
        {
            string json = HttpGetWithJar(
                "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + qrKey + "&source=main-fe-header",
                jar, "https://www.bilibili.com/");
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object dataValue;
            var data = root != null && root.TryGetValue("data", out dataValue) ? dataValue as Dictionary<string, object> : null;
            if (data == null) return -1;
            message = Field(data, "message");
            redirect = Field(data, "url");
            object codeValue;
            return data.TryGetValue("code", out codeValue) ? Convert.ToInt32(codeValue) : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string HttpGetWithJar(string url, CookieContainer jar, string referer)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.CookieContainer = jar;
        request.Timeout = 8000;
        request.ReadWriteTimeout = 8000;
        request.Proxy = null;
        request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
        if (referer != null) request.Referer = referer;
        // 按域名自动带上对应平台的登录 cookie（扫码登录存下来的）。
        // 没登录过就是空串，行为和以前完全一致。
        string hostCookies = PlatformLogin.CookieHeaderForUri(new Uri(url));
        if (hostCookies.Length > 0) request.Headers["Cookie"] = hostCookies;
        request.AllowAutoRedirect = false;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    // 用 ZXing.Net 生成二维码图片（反射加载，缺 dll 时上面已经提示过）
    private static Image RenderQr(string text)
    {
        VerifyQrLibrary();
        Assembly assembly = Assembly.LoadFrom(QrLibrary);
        Type writerType = assembly.GetType("ZXing.BarcodeWriter");
        object writer = Activator.CreateInstance(writerType);
        writerType.GetProperty("Format").SetValue(writer, Enum.Parse(assembly.GetType("ZXing.BarcodeFormat"), "QR_CODE"), null);
        Type optionsType = assembly.GetType("ZXing.QrCode.QrCodeEncodingOptions");
        object options = Activator.CreateInstance(optionsType);
        optionsType.GetProperty("Width").SetValue(options, 360, null);
        optionsType.GetProperty("Height").SetValue(options, 360, null);
        optionsType.GetProperty("Margin").SetValue(options, 2, null);
        optionsType.GetProperty("CharacterSet").SetValue(options, "UTF-8", null);
        writerType.GetProperty("Options").SetValue(writer, options, null);
        return (Image)writerType.GetMethod("Write", new Type[] { typeof(string) }).Invoke(writer, new object[] { text });
    }

    // 把登录后的 Cookie 写成 Netscape 格式（yt-dlp 直接能读）。
    // 第 4 列（secure）保留 Cookie 自身的标记：标了 Secure 的凭据不应该允许经明文 HTTP 发送；
    // 只有 poll 跳转 URL 带来、没有元数据的那些才按 TRUE 兜底。
    // 最近一次保存凭据时的 ACL 结论与原因（登录对话框据此显示「保护上了没有」）。
    internal static AclOutcome CookieAcl = AclOutcome.Failed;
    internal static string CookieAclNote = "";

    private static string SaveCookieFile(CookieContainer jar, string redirectUrl)
    {
        try
        {
            var values = new Dictionary<string, string>();
            var secureFlags = new Dictionary<string, bool>();
            foreach (Cookie cookie in jar.GetCookies(new Uri("https://www.bilibili.com/")))
                if (cookie.Name != null && cookie.Value != null)
                {
                    values[cookie.Name] = cookie.Value;
                    secureFlags[cookie.Name] = cookie.Secure;
                }
            foreach (Cookie cookie in jar.GetCookies(new Uri("https://passport.bilibili.com/")))
                if (cookie.Name != null && cookie.Value != null)
                {
                    values[cookie.Name] = cookie.Value;
                    secureFlags[cookie.Name] = cookie.Secure;
                }
            // poll 返回的 data.url 里也带着 Cookie（B站常见做法）
            Uri redirectUri;
            if (redirectUrl != null
                && Uri.TryCreate(redirectUrl, UriKind.Absolute, out redirectUri)
                && redirectUri.Scheme == Uri.UriSchemeHttps
                && (redirectUri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || redirectUri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
            {
                int q = redirectUrl.IndexOf('?');
                if (q > 0)
                {
                    foreach (string pair in redirectUrl.Substring(q + 1).Split('&'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        string name = Uri.UnescapeDataString(pair.Substring(0, eq));
                        string value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                        if (name.Length > 0 && value.Length > 0 && !values.ContainsKey(name))
                        {
                            values[name] = value;
                            if (!secureFlags.ContainsKey(name)) secureFlags[name] = true;
                        }
                    }
                }
            }
            if (!values.ContainsKey("SESSDATA")) return null;   // 没有 SESSDATA 就等于没登录

            var lines = new List<string>();
            lines.Add("# Netscape HTTP Cookie File");
            lines.Add("# 由「看直播」扫码登录生成；只保存在本机，供 yt-dlp 拉 B站 原画用");
            long expires = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds + 30L * 24 * 3600;
            string[] keep = { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "LIVE_BUVID" };
            foreach (string name in keep)
            {
                string value;
                if (!values.TryGetValue(name, out value)) continue;
                bool secure;
                if (!secureFlags.TryGetValue(name, out secure)) secure = true;
                lines.Add(".bilibili.com\tTRUE\t/\t" + (secure ? "TRUE" : "FALSE")
                    + "\t" + expires + "\t" + name + "\t" + value);
            }
            string aclNote;
            CookieAcl = AtomicWritePrivateText(Cookie,
                string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, out aclNote);
            CookieAclNote = aclNote;

            // 结构化旁文件（<cookie 文件>.json）：裸 Netscape 文件只有 name/value，
            // domain/path/secure/httpOnly/expires 全丢了。旁文件给 DanmakuClient 那条路按域选 cookie 用。
            var records = new List<CookieRecord>();
            foreach (string name in keep)
            {
                string value;
                if (!values.TryGetValue(name, out value)) continue;
                bool secure;
                if (!secureFlags.TryGetValue(name, out secure)) secure = true;
                var record = new CookieRecord();
                record.Name = name;
                record.Value = value;
                record.Domain = ".bilibili.com";
                record.Path = "/";
                record.Secure = secure;
                record.HttpOnly = true;
                record.ExpiresUtc = expires;
                records.Add(record);
            }
            if (CredentialCookies.Save(Cookie, records) == null) return null;
            PlatformHealth.Record("bili", "login", "saved-unverified");
            return Cookie;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 登录凭据落盘（SESSDATA = 完整账号登录态）。文件必须保持明文 Netscape 格式
    // （yt-dlp / mpv 直接读），所以这里做的是**缩小暴露面**而不是加密：写完后断开 ACL 继承，
    // 只留当前用户与 SYSTEM。便携包把 data\ 放在 U 盘/共享盘时，至少保证同机其它账户读不到。
    /// <summary>ACL 收紧的结果。**必须如实回传**（评估报告 §3）。</summary>
    internal enum AclOutcome { Applied, NotSupported, Failed }

    /// <summary>测试注入点：返回非 null 表示「这个位置不支持 ACL」。</summary>
    /// <remarks>真机没有 FAT/exFAT U 盘时没法端到端验这条路径，所以留一个显式的注入缝，
    /// 让「不支持时必须如实告知」这件事可以被自动化测试钉死，而不是靠人拿 U 盘试。</remarks>
#pragma warning disable 649   // 只由测试通过反射赋值；产品代码只读它
    internal static Func<string, string> AclFailureInjectorForTest;
#pragma warning restore 649

    internal static AclOutcome RestrictToCurrentUser(string path)
    {
        string note;
        return RestrictToCurrentUser(path, out note);
    }

    /// <summary>
    /// 原子写入凭据，并在敏感内容进入临时文件之前先收紧其 ACL。
    /// 普通 AtomicWriteAllText 是先写再改权限，会在共享目录或宽松父目录下留下一个短暂可读窗口。
    /// </summary>
    internal static AclOutcome AtomicWritePrivateText(string path, string content, out string note)
    {
        note = "";
        string degradeNote = null;
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(path) + "."
            + Guid.NewGuid().ToString("N") + ".credential.tmp");
        try
        {
            using (FileStream create = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            string tempNote;
            AclOutcome beforeWrite = RestrictToCurrentUser(temp, out tempNote);
            if (beforeWrite == AclOutcome.Failed)
                throw new UnauthorizedAccessException("凭据临时文件权限保护失败：" + tempNote);
            using (FileStream file = new FileStream(temp, FileMode.Truncate, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                file.Flush(true);
            }
            // Replace 会保留已有目标的 ACL；必须在替换前保护旧目标。
            if (File.Exists(path))
            {
                string existingNote;
                AclOutcome existing = RestrictToCurrentUser(path, out existingNote);
                if (existing == AclOutcome.Failed || (beforeWrite == AclOutcome.Applied && existing != AclOutcome.Applied))
                    throw new UnauthorizedAccessException("现有凭据权限保护失败：" + existingNote);
            }
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception moveFailure)
            {
                // FAT/exFAT、部分网络盘不支持 Replace（Move 覆盖同理）。老版本在这里会让
                // 「保存登录状态」整个失败 —— 宁可退化成就地重写也不能让用户登录一次丢一次；
                // 就地重写也必须先保护目标，再复核并如实报告原子性降级。
                if (!File.Exists(path))
                    using (FileStream create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                string protectionNote;
                AclOutcome protection = RestrictToCurrentUser(path, out protectionNote);
                if (protection == AclOutcome.Failed || (beforeWrite == AclOutcome.Applied && protection != AclOutcome.Applied))
                    throw new UnauthorizedAccessException("凭据目标权限保护失败：" + protectionNote, moveFailure);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                degradeNote = "原子替换不可用（" + moveFailure.GetType().Name + "），已就地重写：" + moveFailure.Message;
            }
            string finalNote;
            AclOutcome final = RestrictToCurrentUser(path, out finalNote);
            if (final == AclOutcome.Failed)
                throw new UnauthorizedAccessException("凭据权限复核失败：" + finalNote);
            if (beforeWrite != AclOutcome.Applied && final == AclOutcome.Applied)
                note = "最终文件已保护；临时文件预保护未生效：" + tempNote;
            else note = finalNote.Length > 0 ? finalNote : tempNote;
            if (degradeNote != null) note = note.Length > 0 ? degradeNote + "；" + note : degradeNote;
            return final;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
    }

    /// <summary>断开 ACL 继承、只留当前用户与 SYSTEM。</summary>
    ///
    /// 原来这个方法**吞掉所有异常且返回 void** —— 在 FAT/exFAT U 盘、部分共享目录上
    /// cookie 照样「保存成功」，界面却继续声称「仅当前用户可读」。文案强于实际保证，
    /// 比直接失败更糟。现在返回三态结果 + 一句人能看懂的原因。
    internal static AclOutcome RestrictToCurrentUser(string path, out string note)
    {
        note = "";
        if (string.IsNullOrEmpty(path)) { note = "路径为空"; return AclOutcome.Failed; }
        try
        {
            if (!File.Exists(path)) { note = "文件不存在"; return AclOutcome.Failed; }

            // 测试注入：模拟「这个位置根本不支持 Windows ACL」
            Func<string, string> injector = AclFailureInjectorForTest;
            if (injector != null)
            {
                string injected = injector(path);
                if (injected != null) { note = injected; return AclOutcome.NotSupported; }
            }

            string volumeNote;
            if (!VolumeSupportsAcl(path, out volumeNote)) { note = volumeNote; return AclOutcome.NotSupported; }

            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity == null || identity.User == null) { note = "拿不到当前用户标识"; return AclOutcome.Failed; }
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);   // 断开继承，且不把继承来的规则复制过来
            security.AddAccessRule(new FileSystemAccessRule(identity.User,
                FileSystemRights.FullControl, AccessControlType.Allow));
            try
            {
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                security.AddAccessRule(new FileSystemAccessRule(system,
                    FileSystemRights.FullControl, AccessControlType.Allow));
            }
            catch (Exception) { }
            File.SetAccessControl(path, security);

            // ⚠️ 写完必须**回读验证**。有些位置会「接受调用但不生效」（第三方文件系统驱动、
            //    某些共享盘），那是最坏的一种失败：界面说收紧了，实际谁都能读。
            FileSecurity check = File.GetAccessControl(path);
            if (!check.AreAccessRulesProtected)
            {
                note = "设置权限后 ACL 仍未断开继承（该位置可能不接受权限设置）";
                return AclOutcome.Failed;
            }
            return AclOutcome.Applied;
        }
        catch (PlatformNotSupportedException ex) { note = "该文件系统不支持 Windows ACL：" + ex.Message; return AclOutcome.NotSupported; }
        catch (NotSupportedException ex) { note = "该文件系统不支持 Windows ACL：" + ex.Message; return AclOutcome.NotSupported; }
        catch (Exception ex) { note = ex.GetType().Name + "：" + ex.Message; return AclOutcome.Failed; }
    }

    /// <summary>这个位置的文件系统支不支持 Windows ACL（FAT/exFAT/网络盘都不支持）。</summary>
    private static bool VolumeSupportsAcl(string path, out string note)
    {
        note = "";
        try
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                note = "网络路径（UNC 共享）不保证支持 Windows ACL";
                return false;
            }
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            string format = new DriveInfo(root).DriveFormat;
            if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase))
            {
                note = "卷格式 " + format + " 不支持 Windows ACL";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            note = "判断文件系统失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>给界面用的一句话（凭据保存之后到底保没保护上）。</summary>
    internal static string DescribeAcl(AclOutcome outcome, string note)
    {
        if (outcome == AclOutcome.Applied) return "已收紧为仅当前用户可读";
        if (outcome == AclOutcome.NotSupported)
            return "凭据已保存，但该位置不支持权限保护（" + note + "）—— 同机其它账户可能读到";
        return "凭据已保存，但权限收紧失败（" + note + "）";
    }

    // 已知可用的 zxing.dll：ZXing.Net 0.16.9，三个包里的副本实测同一份 SHA-256。
    // 加载前先比对——这个 dll 是在**拿着你的 B站 登录 Cookie** 的进程里执行代码的。
    // 只信编译进程序的哈希；不能信 DLL 旁边的 .sha256，因为能替换 DLL 的攻击者通常也能
    // 同时替换那个旁文件。升级 ZXing 时必须审阅新二进制并把哈希加入这里。
    private static readonly string[] KnownQrLibraryHashes = {
        "c15741d83e78b0287a9034d583ec4e98903c56b1eedecd43b550ac483c87e29e"
    };

    private static void VerifyQrLibrary()
    {
        string actual;
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(QrLibrary))
            actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        foreach (string known in KnownQrLibraryHashes) if (known == actual) return;
        throw new InvalidOperationException(QrLibrary + " 不是已审阅版本（可能被替换），已拒绝加载。升级 ZXing 时请把新版本哈希加入程序白名单。");
    }
}
