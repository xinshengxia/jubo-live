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
// 四平台扫码登录（斗鱼 / 虎牙 / 抖音；B站 走原来的 passport 接口那条路，不在这里）
//
// 为什么不用各家自己的扫码接口：
//   B站 能那么做是因为它的 `passport.bilibili.com/x/passport-login/web/qrcode/*` 是**公开的纯 HTTP 接口**，
//   自己申请二维码 + 轮询状态就行。斗鱼/虎牙各有一套（能用但要逐个摸），
//   **抖音那套基本没法用裸接口复刻**（二维码和风控签名都在它的 JS SDK 里）。
//
// 2026-10-14：三平台登录统一放在 WebView2 单窗口里。
// CookieManager 读取整个独立上下文，包含 SSO 子域与 HttpOnly Cookie；后台验证后自动保存返回。
// 不再使用置顶提示窗和 UI 线程的 CDP 轮询；实现见 livehub-platform-login-browser.cs。
// 旧 Edge profile 保留；原有 CDP 恢复调用改读 Storage.getCookies，防止漏掉登录子域。
// --------------------------------------------------------------------------------------------

internal sealed class LoginPlatform
{
    public string Id;            // douyu / huya / douyin
    public string Name;          // 斗鱼 / 虎牙 / 抖音
    public string LoginUrl;      // 打开哪个页面让他扫码
    public string Marker;        // 只作为待验证候选标识；最终成功必须来自平台登录检查。
}

internal static class PlatformLogin
{
    internal static readonly LoginPlatform[] All = new LoginPlatform[]
    {
        new LoginPlatform { Id = "douyu",  Name = "斗鱼", LoginUrl = "https://passport.douyu.com/member/login", Marker = "dy_auth" },
        new LoginPlatform { Id = "huya",   Name = "虎牙", LoginUrl = "https://www.huya.com/login",           Marker = "udb_uid" },
        new LoginPlatform { Id = "douyin", Name = "抖音", LoginUrl = "https://sso.douyin.com/",              Marker = "sessionid" }
    };

    internal static LoginPlatform Get(string id)
    {
        for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return All[i];
        return null;
    }

    /// cookie 存这儿。抖音沿用 DouyinGuard 那份，别搞出两个文件。
    internal static string CookiesPath(LoginPlatform platform)
    {
        return platform.Id == "douyin"
            ? DouyinGuard.CookiesPath()
            : Path.Combine(LiveHub.RuntimeDir, platform.Id + "-cookies.txt");
    }

    private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> CachePaths = new Dictionary<string, string>();
    internal static readonly object CredentialGate = new object();

    internal static string LoadCookies(string platformId)
    {
        lock (CredentialGate)
        {
        LoginPlatform platform = Get(platformId);
        string path = platform == null ? "" : CookiesPath(platform);
        string cached;
        string cachedPath;
        if (CachePaths.TryGetValue(platformId, out cachedPath) && string.Equals(path, cachedPath, StringComparison.OrdinalIgnoreCase)
            && Cache.TryGetValue(platformId, out cached)) return cached;
        string result = "";
        try
        {
            if (platform != null)
            {
                if (File.Exists(path)) result = File.ReadAllText(path, Encoding.UTF8).Trim();
            }
        }
        catch (Exception) { }
        Cache[platformId] = result;
        CachePaths[platformId] = path;
        return result;
        }
    }

    internal static void Invalidate(string platformId)
    {
        lock (CredentialGate) { Cache.Remove(platformId); CachePaths.Remove(platformId); }
    }

    internal static bool HasCookies(string platformId)
    {
        return LoadCookies(platformId).Length > 0;
    }

    /// 存 cookie。**返回是否真的写成功** —— 以前这里吞掉异常还照样记「已保存」，
    /// 把失败伪装成成功：用户以为登录好了，其实盘上什么都没有（评估报告点出来的）。
    /// 顺带补上 B站 那条路早就在做的 ACL 收紧 —— cookie 就是登录凭证，
    /// 不能同机其它普通账户随便读。
    /// 最近一次保存后的 ACL 结论（界面据此说话，别把「没保护上」说成「已收紧」）
    internal static LiveHub.AclOutcome LastAcl = LiveHub.AclOutcome.Failed;
    internal static string LastAclNote = "";

    internal static bool Save(LoginPlatform platform, string cookies)
    {
        return Save(platform, cookies, null);
    }

    /// <summary>存 cookie。第二个重载额外接收 **CDP 原始 JSON**，
    /// 用来落一份带 domain/path/secure/httpOnly/expires 的结构化旁文件（评估报告 §3）。</summary>
    internal static bool Save(LoginPlatform platform, string cookies, string cdpJson)
    {
        if (platform == null) return false;
        lock (CredentialGate)
        {
        string content = cookies ?? "";
        string path = CookiesPath(platform);
        try
        {
            Directory.CreateDirectory(LiveHub.RuntimeDir);
            if (!string.IsNullOrEmpty(cdpJson))
            {
                List<CookieRecord> records = CredentialCookies.FilterByPlatform(CredentialCookies.ParseCdp(cdpJson), platform.Id);
                if (records.Count == 0) throw new InvalidDataException("没有本平台可用 cookie");
                content = CredentialCookies.RawHeader(records);
            }
            string aclNote;
            LastAcl = LiveHub.AtomicWritePrivateText(path, content, out aclNote);
            LastAclNote = aclNote;
            if (!SaveStructured(platform, path, cdpJson)) throw new IOException("结构化 cookie 未能保存");
            PlatformHealth.Record(platform.Id, "login", "saved-unverified");
            Cache[platform.Id] = content;
            CachePaths[platform.Id] = path;
            DanmakuHost.Log(platform.Name + "：已保存登录 cookie（" + content.Length + " 字符；"
                + LiveHub.DescribeAcl(LastAcl, LastAclNote) + "）");
            return true;
        }
        catch (Exception ex)
        {
            LastAcl = LiveHub.AclOutcome.Failed;
            LastAclNote = ex.Message;
            PlatformHealth.Record(platform.Id, "login", "save-failed");
            Cache[platform.Id] = "";       // 没写成就别留在内存里冒充有效
            CachePaths[platform.Id] = path;
            DanmakuHost.Log(platform.Name + "：cookie 保存失败 " + ex.Message);
            return false;
        }
        }
    }

    /// <summary>从 CDP 响应里解析出结构化 cookie：**按平台域名白名单过滤**后写旁文件。</summary>
    ///
    /// Storage.getCookies / WebView2 CookieManager 全上下文读取会包含登录跳转的其他域，
    /// 不能把它们无差别压进裸头、一起落盘 ——
    /// 现在只留本平台的域（白名单见 CredentialCookies.AllowedDomains）。
    private static bool SaveStructured(LoginPlatform platform, string cookiePath, string cdpJson)
    {
        if (string.IsNullOrEmpty(cdpJson)) return true;
        List<CookieRecord> all = CredentialCookies.ParseCdp(cdpJson);
        if (all.Count == 0) return false;
        List<CookieRecord> kept = CredentialCookies.FilterByPlatform(all, platform.Id);
        int dropped = all.Count - kept.Count;
        string sidecar = CredentialCookies.Save(cookiePath, kept);
        if (sidecar != null)
            DanmakuHost.Log(platform.Name + "：结构化 cookie " + kept.Count + " 条（丢掉无关域/过期 "
                + dropped + " 条）→ " + Path.GetFileName(sidecar));
        return sidecar != null;
    }

    /// 登录 profile 目录也收紧 —— 那里面装着浏览器登录态，比 cookie 文件还完整。
    /// 注意：只对**我们自己建的** profile 目录做，不动用户自己的浏览器配置。
    internal static bool RestrictProfile(string profileDir)
    {
        try
        {
            if (!Directory.Exists(profileDir)) return false;
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity == null || identity.User == null) return false;
            ProtectProfileDirectory(new DirectoryInfo(profileDir), identity.User,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            return true;
        }
        catch (Exception ex) { Diagnostics.Report("登录 profile 权限保护", ex); return false; }
    }

    private static void ProtectProfileDirectory(DirectoryInfo directory, SecurityIdentifier user, SecurityIdentifier system)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("profile 中存在重解析目录：" + directory.Name);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        // 迁移旧版关闭继承或带宽松显式规则的子项；不跟随 junction/symlink 离开 profile。
        foreach (DirectoryInfo child in directory.GetDirectories()) ProtectProfileDirectory(child, user, system);
        foreach (FileInfo file in directory.GetFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("profile 中存在重解析文件：" + file.Name);
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
    }

    /// 把 cookie 拼成能直接塞进请求头的字符串（含各平台自己的 nonce 之类的调用方自己加）
    internal static string CookieHeader(string platformId)
    {
        return LoadCookies(platformId);
    }

    /// 按请求的域名自动挑对应的登录 cookie。
    /// 放在 HttpGetText 这一个公共出口里，四处调用点不用改 —— 以后新增的请求也自动带上。
    /// 没登录过就是空串，行为和以前完全一致。
    internal static string CookieHeaderForHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        return CookieHeaderForUri(new Uri("https://" + host + "/"));
    }

    internal static string CookieHeaderForUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return "";
        string host = uri.Host.ToLowerInvariant();
        // ⚠️ 不能只 EndsWith("douyu.com") —— 那样 `evildouyu.com` 也会命中、
        // 把登录 cookie 发给别人的服务器。要求「完全相等」或「子域名」。
        if (Matches(host, "douyu.com")) return HeaderFor("douyu", uri);
        if (Matches(host, "huya.com")) return HeaderFor("huya", uri);
        if (Matches(host, "douyin.com")) return HeaderFor("douyin", uri);
        return "";
    }

    /// 选 cookie 发出去。
    ///
    /// 有结构化旁文件（<cookie 文件>.json）时：按 **domain/path/secure** 精确选，
    /// 而不是把整串无差别贴上去（评估报告 §3 建议的 CookieContainer 语义，这里手写实现，
    /// 因为裸头文本本身就是要发给平台的东西，换成 CookieContainer 反而多一层转换）。
    /// 没有旁文件时要求重新登录，不发送无法确认作用域的裸串。
    private static string HeaderFor(string platformId, Uri uri)
    {
        LoginPlatform platform = Get(platformId);
        if (platform != null)
        {
            List<CookieRecord> records = CredentialCookies.Load(CookiesPath(platform));
            if (records.Count > 0)
                return CredentialCookies.HeaderForUri(CredentialCookies.FilterByPlatform(records, platformId), uri);
        }
        // 缺失/损坏/全过期的元数据不能退回来源不明的整串。
        // 裸文件仍可读取用于登录提示；重新登录生成旁文件后才用于出站请求。
        return "";
    }

    private static bool Matches(string host, string domain)
    {
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    /// 单窗口内完成网页登录并自动验证。返回 true = 保存完成（可为用户主动保存的待验证状态）。
    internal static bool Show(IWin32Window owner, string platformId)
    {
        LoginPlatform platform = Get(platformId);
        return platform != null && PlatformLoginBrowser.Show(owner, platform);
    }
}
