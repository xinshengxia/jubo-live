// --------------------------------------------------------------------------------------------
// 凭据（cookie）的**结构化**存取 —— 评估报告 §3「凭据安全的文案强于实际保证」的整改。
//
// 原来的问题：CDP 把浏览器上下文里的 cookie 交出来之后，代码立刻把它压成
// `k=v; k=v` 这样一串**没有 domain / path / secure / httpOnly / expires 元数据**的字符串。
// 后果有三：
//   ① 之后只能按「顶级域相等」整串发给请求，域名/路径/安全标记全丢；
//   ② 登录跳转途中产生的**无关域** cookie 也被一起收进来、一起落盘；
//   ③ 界面无法回答「这条凭据到底属于谁、什么时候过期」。
//
// 现在：CDP 的原始 JSON 会被解析成 CookieRecord 列表（元数据全留），
//   按平台域名白名单过滤后写一份**结构化旁文件**（<cookie 文件>.json），
//   裸头文本照旧写（yt-dlp / mpv / 各平台 HTTP 客户端读的就是它，兼容不能破）。
//   有旁文件时，按目标 host 选 cookie 走结构化数据；没有时退回原来的整串行为。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Web.Script.Serialization;

/// <summary>一条带完整元数据的 cookie。</summary>
internal sealed class CookieRecord
{
    public string Name = "";
    public string Value = "";
    public string Domain = "";      // 浏览器语义：前导点 = 对子域也有效
    public string Path = "/";
    public bool Secure;
    public bool HttpOnly;
    public string SameSite;         // Optional: old exports remain valid without this field.
    public long ExpiresUtc;         // Unix 秒；0 = 会话 cookie（浏览器关了就没）

    public bool Expired()
    {
        if (ExpiresUtc <= 0) return false;
        return ExpiresUtc <= (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    public override string ToString() { return Name + "=" + Value + " (" + Domain + Path + ")"; }
}

internal static class CredentialCookies
{
    /// <summary>各平台允许落盘的 cookie 域。CDP 交出来的是**整个浏览器上下文**的 cookie，
    /// 里面混着登录跳转时经过的第三方域 —— 不属于本平台的，一律不许进凭据文件。</summary>
    internal static string[] AllowedDomains(string platformId)
    {
        switch ((platformId ?? "").ToLowerInvariant())
        {
            case "douyu": return new string[] { "douyu.com" };
            case "huya": return new string[] { "huya.com" };
            case "douyin": return new string[] { "douyin.com" };
            case "bili":
            case "bilibili": return new string[] { "bilibili.com" };
            default: return new string[0];       // 不认识的平台：没有白名单就不放行任何域
        }
    }

    /// <summary>host 能不能收 domain 的 cookie。语义同 PlatformLogin.Matches：
    /// 完全相等或**子域名**；`evildouyu.com` / `douyu.com.evil.com` 必须被拒。</summary>
    internal static bool DomainMatches(string host, string domain)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain)) return false;
        host = host.ToLowerInvariant();
        domain = domain.ToLowerInvariant().TrimStart('.');
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    internal static bool DomainAllowed(string domain, string platformId)
    {
        foreach (string allowed in AllowedDomains(platformId))
            if (DomainMatches(domain == null ? "" : domain.TrimStart('.'), allowed)) return true;
        return false;
    }

    /// <summary>解析 CDP `Network.getCookies` 的响应 JSON（也接受只含 cookies 数组的对象）。
    /// 失败返回空列表，绝不抛异常 —— 它跑在登录流程里，出错只该退化成「没有结构化数据」。</summary>
    internal static List<CookieRecord> ParseCdp(string json)
    {
        var records = new List<CookieRecord>();
        if (string.IsNullOrEmpty(json)) return records;
        try
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 8 * 1024 * 1024;   // 整个浏览器的 cookie 可能不小
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null) return records;
            object cookiesObject = null;
            if (root.ContainsKey("cookies")) cookiesObject = root["cookies"];
            else if (root.ContainsKey("result"))
            {
                var result = root["result"] as Dictionary<string, object>;
                if (result != null && result.ContainsKey("cookies")) cookiesObject = result["cookies"];
            }
            var list = cookiesObject as System.Collections.ArrayList;
            if (list == null) return records;
            foreach (object entry in list)
            {
                var map = entry as Dictionary<string, object>;
                if (map == null) continue;
                var record = new CookieRecord();
                record.Name = GetString(map, "name");
                record.Value = GetString(map, "value");
                record.Domain = GetString(map, "domain");
                string path = GetString(map, "path");
                record.Path = path.Length == 0 ? "/" : path;
                record.Secure = GetBool(map, "secure");
                record.HttpOnly = GetBool(map, "httpOnly");
                record.SameSite = GetString(map, "sameSite");
                record.ExpiresUtc = GetLong(map, "expires");
                if (record.Name.Length == 0) continue;
                records.Add(record);
            }
        }
        catch (Exception ex)
        {
            // 不静默：解析失败意味着「这次登录拿不到结构化 cookie」，
            // 而它同时又会让裸头变成空串 —— 用户看到的会是「没读到 cookie」，日志里得能查到根因。
            Diagnostics.Report("cookie-cdp-parse", ex);
        }
        return records;
    }

    private static string GetString(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return "";
        return Convert.ToString(value);
    }

    private static bool GetBool(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return false;
        if (value is bool) return (bool)value;
        bool parsed;
        return bool.TryParse(Convert.ToString(value), out parsed) && parsed;
    }

    private static long GetLong(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return 0;
        try { return Convert.ToInt64(value); } catch (Exception) { return 0; }
    }

    /// <summary>按平台域名白名单过滤 + 丢掉已过期的。顺序保持不变（后续同名覆盖靠它）。</summary>
    internal static List<CookieRecord> FilterByPlatform(List<CookieRecord> records, string platformId)
    {
        var kept = new List<CookieRecord>();
        if (records == null) return kept;
        foreach (CookieRecord record in records)
        {
            if (record == null || record.Name.Length == 0) continue;
            if (!DomainAllowed(record.Domain, platformId)) continue;
            if (record.Expired()) continue;
            kept.Add(record);
        }
        return kept;
    }

    /// <summary>这条 cookie 该不该发给 host：域匹配 + 路径前缀匹配 + secure 只走 https。</summary>
    internal static bool ShouldSendTo(CookieRecord record, string host, bool https)
    {
        return ShouldSendToUri(record, new Uri((https ? "https://" : "http://") + host + "/"));
    }

    internal static bool ShouldSendToUri(CookieRecord record, Uri uri)
    {
        if (record == null || record.Expired()) return false;
        if (uri == null || !uri.IsAbsoluteUri) return false;
        bool secure = uri.Scheme == "https" || uri.Scheme == "wss";
        if (record.Secure && !secure) return false;
        if (!DomainMatches(uri.Host, record.Domain)) return false;
        if (!record.Domain.StartsWith(".", StringComparison.Ordinal)
            && !string.Equals(uri.Host, record.Domain, StringComparison.OrdinalIgnoreCase)) return false;
        string path = string.IsNullOrEmpty(record.Path) ? "/" : record.Path;
        string requestPath = uri.AbsolutePath;
        if (!path.StartsWith("/", StringComparison.Ordinal)) return false;
        return requestPath == path || (requestPath.StartsWith(path, StringComparison.Ordinal)
            && (path.EndsWith("/", StringComparison.Ordinal) || (requestPath.Length > path.Length && requestPath[path.Length] == '/')));
    }

    /// <summary>按 host 拼 Cookie 头（同名后者覆盖前者，和浏览器一致）。</summary>
    internal static string HeaderForHost(List<CookieRecord> records, string host, bool https)
    {
        if (string.IsNullOrEmpty(host)) return "";
        return HeaderForUri(records, new Uri((https ? "https://" : "http://") + host + "/"));
    }

    internal static string HeaderForUri(List<CookieRecord> records, Uri uri)
    {
        if (records == null || uri == null) return "";
        var selected = new List<CookieRecord>();
        foreach (CookieRecord record in records)
        {
            if (!ShouldSendToUri(record, uri) || !IsHeaderSafe(record)) continue;
            selected.Add(record);
        }
        // 同名不同路径不能合并；先发更具体路径，保留浏览器的匹配顺序。
        selected = selected.OrderByDescending(record => (record.Path ?? "/").Length).ToList();
        var builder = new StringBuilder();
        foreach (CookieRecord record in selected)
        {
            if (builder.Length > 0) builder.Append("; ");
            builder.Append(record.Name).Append('=').Append(record.Value);
        }
        return builder.ToString();
    }

    private static bool IsHeaderSafe(CookieRecord record)
    {
        if (string.IsNullOrEmpty(record.Name) || record.Value == null) return false;
        foreach (char c in record.Name)
            if (c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".IndexOf(c) >= 0) return false;
        foreach (char c in record.Value) if (c < 32 || c == 127 || c == ';') return false;
        return true;
    }

    /// <summary>结构化旁文件路径。裸 cookie 文件旁边放一份 <名字>.json。</summary>
    /// <summary>SidecarPath 的别名，给测试用（产品代码用 SidecarPath）。</summary>
    internal static string SidecarPathForTest(string cookieFilePath) { return SidecarPath(cookieFilePath); }

    internal static string SidecarPath(string cookieFilePath)
    {
        return string.IsNullOrEmpty(cookieFilePath) ? null : cookieFilePath + ".json";
    }

    /// <summary>写结构化旁文件（原子写 + ACL 收紧）。返回 null = 没写；否则返回实际路径。</summary>
    internal static string Save(string cookieFilePath, List<CookieRecord> records)
    {
        if (string.IsNullOrEmpty(cookieFilePath) || records == null) return null;
        try
        {
            var payload = new Dictionary<string, object>();
            payload["schema"] = 1;
            payload["savedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var list = new List<Dictionary<string, object>>();
            foreach (CookieRecord record in records)
            {
                var item = new Dictionary<string, object>();
                item["name"] = record.Name;
                item["value"] = record.Value;
                item["domain"] = record.Domain;
                item["path"] = record.Path;
                item["secure"] = record.Secure;
                item["httpOnly"] = record.HttpOnly;
                if (!string.IsNullOrEmpty(record.SameSite)) item["sameSite"] = record.SameSite;
                item["expires"] = record.ExpiresUtc;
                list.Add(item);
            }
            payload["cookies"] = list;
            string json = new JavaScriptSerializer().Serialize(payload);
            string path = SidecarPath(cookieFilePath);
            string aclNote;
            LiveHub.AtomicWritePrivateText(path, json, out aclNote);
            return path;
        }
        catch (Exception ex)
        {
            Diagnostics.Report("cookie-sidecar", ex);
            return null;
        }
    }

    /// <summary>读结构化旁文件；没有 / 坏了就返回空列表（调用方不发送 cookie）。</summary>
    internal static List<CookieRecord> Load(string cookieFilePath)
    {
        var records = new List<CookieRecord>();
        string path = SidecarPath(cookieFilePath);
        if (path == null || !File.Exists(path)) return records;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 8 * 1024 * 1024;
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("cookies")) return records;
            var list = root["cookies"] as System.Collections.ArrayList;
            if (list == null) return records;
            foreach (object entry in list)
            {
                var map = entry as Dictionary<string, object>;
                if (map == null) continue;
                var record = new CookieRecord();
                record.Name = GetString(map, "name");
                record.Value = GetString(map, "value");
                record.Domain = GetString(map, "domain");
                string recordPath = GetString(map, "path");
                record.Path = recordPath.Length == 0 ? "/" : recordPath;
                record.Secure = GetBool(map, "secure");
                record.HttpOnly = GetBool(map, "httpOnly");
                record.SameSite = GetString(map, "sameSite");
                record.ExpiresUtc = GetLong(map, "expires");
                if (record.Name.Length > 0) records.Add(record);
            }
        }
        catch (Exception ex) { Diagnostics.Report("cookie-sidecar-load", ex); }
        return records;
    }

    /// <summary>把记录压回裸头（不按 host 过滤，全量拼接）—— 外部工具 / 老路径用。</summary>
    internal static string RawHeader(List<CookieRecord> records)
    {
        if (records == null || records.Count == 0) return "";
        var order = new List<string>();
        var values = new Dictionary<string, string>();
        foreach (CookieRecord record in records)
        {
            if (record == null || record.Name.Length == 0) continue;
            if (!values.ContainsKey(record.Name)) order.Add(record.Name);
            values[record.Name] = record.Value;
        }
        var builder = new StringBuilder();
        foreach (string name in order)
        {
            if (builder.Length > 0) builder.Append("; ");
            builder.Append(name).Append('=').Append(values[name]);
        }
        return builder.ToString();
    }
}
