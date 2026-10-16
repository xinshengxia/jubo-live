using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 从主 partial 原样搬运；仍在同一 net48 编译单元。
internal static partial class LiveHub
{
    private static bool IsSafeStreamUrl(string url)
    {
        return IsSafeRemoteHttpUrl(url);
    }

    /// <summary>校验由远端平台返回的 HTTP(S) 地址：先看字面（协议 / 内嵌凭据 / 本地域名 / 私网字面 IP），
    /// 再把域名解析成地址复核一遍 —— 只查字面挡不住「攻击者域名指向 127.0.0.1」这种 SSRF。
    /// 2026-09-28：解析复核与私网段补全（上一轮 4a921c5 只做了字面检查）。</summary>
    internal static bool IsSafeRemoteHttpUrl(string url)
    {
        return IsSafeRemoteHttpUrlResolved(url, null);
    }

    private static readonly IPAddress[] NoDnsAddresses = new IPAddress[0];
    internal static bool IsSafeRemoteHttpUrlSyntax(string url)
    {
        // 只检查 URL 字面，不在渲染线程解析 DNS；下载前仍须完整校验并钉扎地址。
        return IsSafeRemoteHttpUrlResolved(url, delegate(string host) { return NoDnsAddresses; });
    }

    /// <summary>resolver 供字面校验和测试注入（传 null 走系统 DNS）：离线环境里没法让域名真的解析到回环，
    /// 只能把解析结果喂进来验判据。</summary>
    internal static bool IsSafeRemoteHttpUrlResolved(string url, Func<string, IPAddress[]> resolver)
    {
        Uri uri;
        if (string.IsNullOrEmpty(url) || url.Length > 4096) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)) return false;
        IPAddress literal;
        if (IPAddress.TryParse(host, out literal)) return IsPublicAddress(literal);
        // 域名：解析后复查。**解析不出来就放行**（当成连不上，交给后面正常的连接失败去报错）——
        // 无网络/离线环境下如果在这里一律拒绝，会把所有表情图和直链一起误杀。
        // 注意这仍有 TOCTOU 余量（校验和真正连接各解析一次）；要彻底封死得自己接 socket 连 IP。
        IPAddress[] addresses;
        try { addresses = resolver != null ? resolver(host) : Dns.GetHostAddresses(host); }
        catch (Exception) { return true; }
        if (addresses == null || addresses.Length == 0) return true;
        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address)) return false;
        }
        return true;
    }

    /// <summary>单个地址是否属于「公网可达」。字面检查与解析复核共用这一份判据。</summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.Broadcast)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0                          // 0.0.0.0/8 「本网络」
                || bytes[0] == 10                           // 10.0.0.0/8
                || bytes[0] == 127                          // 127.0.0.0/8
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)   // 100.64.0.0/10 CGNAT
                || (bytes[0] == 169 && bytes[1] == 254)     // 169.254.0.0/16 链路本地
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)    // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)      // 192.0.0.0/24
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)      // 192.0.2.0/24 TEST-NET-1
                || (bytes[0] == 192 && bytes[1] == 168)                     // 192.168.0.0/16
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)   // 198.51.100.0/24 TEST-NET-2
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)    // 203.0.113.0/24 TEST-NET-3
                || bytes[0] >= 224);                        // 组播 224/4 + 保留 240/4 + 广播
            // 刻意**不拦** 198.18.0.0/15：部分透明代理（Clash TUN fake-ip）会把真实域名解析到
            // 这个段里，拦了等于 TUN 模式下所有表情图一起失效，而它并不通向内网。
        }
        // IPv6：ULA(fc00::/7) / 链路本地(fe80::/10) / 站点本地 / 组播 / 文档(2001:db8::/32) / 丢弃(100::/64)
        // 同样刻意不拦 2001:2::/48（RFC 5180 基准测试前缀）：mihomo 的 fake-ip v6 就用它
        // ，拦截此类合成地址会导致 TUN 模式下的请求误判。
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        return !((bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || (bytes[0] == 0x01 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0));
    }

    // ---------------- DNS 型 SSRF 的 TOCTOU 封口（2026-09-28） ----------------
    //
    // IsSafeRemoteHttpUrl 是「看看这个地址安不安全」，但它解析一次、HttpWebRequest 连接时
    // 会再解析一次：攻击者控制的域名完全可以第一次回公网、第二次回 127.0.0.1（DNS rebinding）。
    // 下面的做法是**让校验用的地址就是连接用的地址**：
    //   · 域名 → 现在解析，只留公网地址，把 URL 的连接目标换成这些 IP 字面量；
    //     再把原域名交给 request.Host —— .NET 用 Host 同时决定 Host 头、TLS SNI 与证书名校验
    //     （环回 ClientHello 测试可验证：设 Host=pinned.example.com 时 SNI 就是
    //     pinned.example.com，证书也按它校验；不设 Host 时 IP 字面量连 SNI 扩展都不发），
    //     所以 CDN 的虚拟主机路由不受影响。
    //   · 字面 IP 地址 → 本来就没有解析这一步，没有 TOCTOU。

    /// <summary>域名 + 已校验的公网地址 → 连接用的 IP 字面量 URL（路径/查询原样保留）。</summary>
    internal static string BuildPinnedUrl(Uri uri, IPAddress address)
    {
        string host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + address + "]" : address.ToString();
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.PathAndQuery;
    }

    /// <summary>算出连接候选地址（IP 字面量 URL；IPv4 优先，逐个尝试，避免丢掉 .NET 原有的多地址回退）。
    /// hostHeader = 交给 Host 头/SNI/证书名校验的原域名（字面 IP 地址时为 null）。
    /// 返回空数组 = 没有可用公网地址，调用方**必须拒绝连接**。
    /// literalIsTrusted：字面 IP 地址是否已由调用点校验过（离线测试要连本地服务器的字面回环地址，
    /// 产品路径则由入口守卫先校验）——false 时字面地址也要判公网。
    /// resolver 只给测试注入（传 null 走系统 DNS）：离线环境没法让域名真的解析到回环。</summary>
    internal static string[] PinnedCandidatesFor(string url, Func<string, IPAddress[]> resolver, bool literalIsTrusted,
                                                out string hostHeader)
    {
        hostHeader = null;
        Uri uri;
        if (string.IsNullOrEmpty(url) || url.Length > 4096) return new string[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return new string[0];
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return new string[0];
        string host = uri.DnsSafeHost.TrimEnd('.');
        IPAddress literal;
        if (IPAddress.TryParse(host, out literal))
            return literalIsTrusted || IsPublicAddress(literal) ? new string[] { url } : new string[0];

        IPAddress[] addresses;
        try { addresses = resolver != null ? resolver(host) : Dns.GetHostAddresses(host); }
        catch (Exception) { return new string[0]; }
        if (addresses == null) return new string[0];
        List<string> v4 = new List<string>();
        List<string> v6 = new List<string>();
        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address)) continue;
            string pinned = BuildPinnedUrl(uri, address);
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (!v4.Contains(pinned)) v4.Add(pinned);
            }
            else if (!v6.Contains(pinned)) v6.Add(pinned);
        }
        if (v4.Count == 0 && v6.Count == 0) return new string[0];
        hostHeader = host;
        v4.AddRange(v6);
        return v4.ToArray();
    }

    /// <summary>远端 URL 的统一取回入口：连接目标钉在已校验的公网地址上，Host 头/SNI/证书名校验
    /// 仍用原域名。WebException 且**没有响应**（连接级失败：连不上/超时/TLS 失败）才换下一个候选地址；
    /// 对端给了响应（403/404…）就照实返回，不重试。
    /// extra 用来加调用方特有的设置（Proxy、Origin 等）。</summary>
    internal static HttpWebResponse OpenRemoteResponse(string url, int timeoutMs, string userAgent, string referer,
                                                      Action<HttpWebRequest> extra, bool literalIsTrusted)
    {
        Uri uri;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            throw new InvalidDataException("远端地址无效，已拒绝连接");
        string hostHeader;
        string[] candidates = PinnedCandidatesFor(url, null, literalIsTrusted, out hostHeader);
        if (candidates.Length == 0)
            throw new InvalidDataException("远端域名没有可用公网地址，已拒绝连接");
        for (int i = 0; i < candidates.Length; i++)
        {
            try
            {
                return SendOnce(candidates[i], hostHeader, timeoutMs, userAgent, referer, extra);
            }
            catch (WebException ex)
            {
                // 有响应 = 对端明确答复（403/404…），换地址是浪费也是掩饰；最后一个候选也直接抛
                if (ex.Response != null || i == candidates.Length - 1) throw;
            }
        }
        throw new InvalidDataException("远端地址全部连接失败");
    }

    private static HttpWebResponse SendOnce(string pinnedUrl, string hostHeader, int timeoutMs, string userAgent,
                                            string referer, Action<HttpWebRequest> extra)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(pinnedUrl);
        if (hostHeader != null) request.Host = hostHeader;
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.AllowAutoRedirect = false;
        // ServicePoint 现在按 IP 分池：同一个 IP 上挂着多个域名时，别让连接被跨虚拟主机复用
        if (hostHeader != null) request.KeepAlive = false;
        if (userAgent != null) request.UserAgent = userAgent;
        if (referer != null) request.Referer = referer;
        if (extra != null) extra(request);
        return (HttpWebResponse)request.GetResponse();
    }

    // 抖音房间页的 IPv4 与 IPv6 CDN 可能返回不同内容，包括缺少 __pace_f 的验证中间页。
    // 优先解析 A 记录连接 IPv4，并通过 Host 保留域名对应的 TLS SNI 和证书校验。
    // 兜底顺序：IPv4 直连 → 域名直连（IPv6 边）→ 系统代理（IPv4 边也被挡时才用得上）。
}
