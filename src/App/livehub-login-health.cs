using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Read-only login checks. No Cookie values, account identifiers or response bodies are persisted.
internal static class LoginHealth
{
    internal sealed class Result
    {
        public string State, Reason, Stamp;
        public long CheckedUtc, VerifiedUtc;
    }
    internal sealed class Evidence
    {
        internal string Stamp, State, Reason;
        internal List<CookieRecord> Records = new List<CookieRecord>();
    }
    internal sealed class Reply
    {
        internal int Status;
        internal string Body, Location;
    }
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, string> Running = new Dictionary<string, string>();
    internal static Func<string, List<CookieRecord>, Reply> Transport = Fetch;
    internal static readonly string[] Platforms = { "bili", "huya", "douyu", "douyin" };
    internal const int FreshMinutes = 10;

    internal static string CookiePath(string platform)
    {
        return platform == "bili" ? LiveHub.Cookie : PlatformLogin.CookiesPath(PlatformLogin.Get(platform));
    }
    private static string ResultPath(string platform)
    {
        return Path.Combine(LiveHub.RuntimeDir, "health-login-" + platform + ".json");
    }
    private static string Key(string platform) { return ResultPath(platform); }
    private static string Hash(string text)
    {
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
    private static string ReadBounded(string path)
    {
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException();
        return File.ReadAllText(path, Encoding.UTF8);
    }
    private static bool LoginKey(string platform, string name)
    {
        return platform == "bili" ? name == "SESSDATA"
            : platform == "douyu" ? name == "dy_auth" || name == "acf_auth" || name == "acf_stk"
            : platform == "douyin" ? name == "sessionid" || name == "sessionid_ss"
            : name == "udb_biztoken" || name == "udb_l" || name == "udb_uid";
    }
    internal static Evidence ReadLocal(string platform)
    {
        var evidence = new Evidence { State = "missing", Reason = "未保存本程序可用的登录凭据。" };
        lock (PlatformLogin.CredentialGate)
        try
        {
            string path = CookiePath(platform), side = CredentialCookies.SidecarPath(path);
            string raw = ReadBounded(path), json = ReadBounded(side);
            evidence.Stamp = Hash("login-check/2\n" + path + "\n" + raw + "\n" + json);
            if (raw.Length == 0 && json.Length == 0) return evidence;
            evidence.Records = platform == "bili" ? new List<CookieRecord>() : CredentialCookies.Load(path);
            // B站's player and danmaku client use the Netscape file itself, not its sidecar.
            // Verify that exact source even when an older sidecar is still present.
            // Other platforms' bare name=value strings never acquire an invented scope.
            if (platform == "bili" && raw.Length == 0)
            { evidence.Reason = "B站播放使用的 Cookie 文件缺失或为空。"; return evidence; }
            if (platform == "bili")
                foreach (string line in raw.Split('\n'))
                {
                    string entry = line.TrimEnd('\r');
                    if (entry.StartsWith("#HttpOnly_")) entry = entry.Substring(10);
                    else if (entry.StartsWith("#")) continue;
                    string[] parts = entry.Split('\t'); long expiry;
                    if (parts.Length == 7 && long.TryParse(parts[4], out expiry))
                        evidence.Records.Add(new CookieRecord { Domain = parts[0], Path = parts[2], Secure = parts[3] == "TRUE",
                            ExpiresUtc = expiry, Name = parts[5], Value = parts[6] });
                }
            if (evidence.Records.Count == 0)
            {
                evidence.State = "metadata"; evidence.Reason = "Cookie 作用域信息缺失或损坏，请重新登录；不会发送来源不明的裸 Cookie。";
                return evidence;
            }
            bool keySeen = false, usable = false;
            foreach (CookieRecord record in evidence.Records)
                if (CredentialCookies.DomainAllowed(record.Domain, platform) && LoginKey(platform, record.Name) && record.Value.Length > 0)
                { keySeen = true; if (!record.Expired()) usable = true; }
            if (!usable)
            {
                evidence.State = keySeen ? "expired" : "missing";
                evidence.Reason = keySeen ? "本地关键登录 Cookie 已到期，请重新登录。" : "仅找到游客或辅助 Cookie，未找到关键登录凭据。";
                return evidence;
            }
            evidence.Records = CredentialCookies.FilterByPlatform(evidence.Records, platform);
            evidence.State = "saved"; evidence.Reason = "已保存登录凭据，尚待平台确认；本地未到期不等于服务器仍认可。";
        }
        catch (Exception)
        {
            evidence.State = "unreadable"; evidence.Reason = "本地凭据暂时无法读取，未判定登录失效。";
        }
        return evidence;
    }
    private static Result Load(string platform, string stamp)
    {
        try
        {
            Result result = new JavaScriptSerializer().Deserialize<Result>(ReadBounded(ResultPath(platform)));
            if (result != null && result.Stamp == stamp && result.CheckedUtc > 0 && result.CheckedUtc <= DateTime.UtcNow.AddMinutes(1).Ticks
                && Array.IndexOf(new[] { "valid", "invalid", "restricted", "network", "unknown" }, result.State) >= 0) return result;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        return null;
    }
    internal static Result Get(string platform)
    {
        Evidence local = ReadLocal(platform);
        if (local.State != "saved") return new Result { State = local.State, Reason = local.Reason, Stamp = local.Stamp };
        Result result = Load(platform, local.Stamp);
        lock (Gate)
        {
            string stamp;
            if (Running.TryGetValue(Key(platform), out stamp) && stamp == local.Stamp)
                return new Result { State = "checking", Reason = "正在只读检查平台登录态。", VerifiedUtc = result == null ? 0 : result.VerifiedUtc };
        }
        if (result == null) return new Result { State = "saved", Reason = local.Reason, Stamp = local.Stamp };
        if (result.State == "valid" && DateTime.UtcNow.Ticks - result.CheckedUtc > TimeSpan.FromMinutes(FreshMinutes).Ticks)
            return new Result { State = "stale", Reason = "上次验证有效，超过 10 分钟，待复查；这不代表 Cookie 过期。",
                CheckedUtc = result.CheckedUtc, VerifiedUtc = result.VerifiedUtc, Stamp = result.Stamp };
        return result;
    }
    internal static void RequestCheck(string platform, bool force)
    {
        if (Array.IndexOf(Platforms, platform) < 0) return;
        Evidence local = ReadLocal(platform);
        if (local.State != "saved") return;
        Result old = Load(platform, local.Stamp);
        if (old != null && DateTime.UtcNow.Ticks - old.CheckedUtc < TimeSpan.FromSeconds(force ? 10 : FreshMinutes * 60).Ticks) return;
        string key = Key(platform);
        lock (Gate) { if (Running.ContainsKey(key)) return; Running[key] = local.Stamp; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { CheckAndSave(platform, local); }
            finally { lock (Gate) { Running.Remove(key); } }
        });
    }
    internal static void CheckAndSave(string platform, Evidence local)
    {
        Result result = CheckRecords(platform, local.Records);
        lock (PlatformLogin.CredentialGate)
        {
            Evidence current = ReadLocal(platform);
            if (current.Stamp != local.Stamp || current.State != "saved") return;
            Result old = Load(platform, local.Stamp);
            result.Stamp = local.Stamp; result.CheckedUtc = DateTime.UtcNow.Ticks;
            result.VerifiedUtc = result.State == "valid" ? result.CheckedUtc : old == null ? 0 : old.VerifiedUtc;
            try { LiveHub.AtomicWriteAllText(ResultPath(platform), new JavaScriptSerializer().Serialize(result)); }
            catch (Exception ex) { Diagnostics.Report("登录检查结果保存", ex); }
        }
    }
    internal static Result CheckRecords(string platform, List<CookieRecord> records)
    {
        try
        {
            Reply reply = Transport(platform, records);
            if (platform == "douyu" && reply.Status == 302 && DouyuAuthRedirect(reply.Location))
                return NewResult("valid", "斗鱼官方SSO返回明确授权成功跳转，已确认当前会话。" );
            Result result = Evaluate(platform, reply.Status, reply.Body);
            if (platform == "douyu" && result.State == "invalid" && LoginCapture.HasMarker(platform, records))
            {
                bool sso = records.Exists(delegate(CookieRecord c) { return c.Name == "LTP0" && !c.Expired()
                    && CredentialCookies.ShouldSendToUri(c, new Uri(Endpoint("douyu"))); });
                if (!sso) return NewResult("unknown", "只有网页业务凭据，缺少完整SSO登录凭据；无法据此判定失效，请重新登录同步完整会话。" );
            }
            return result;
        }
        catch (Exception) { return NewResult("network", "请求超时或网络异常，无法确认；请检查网络后重试。" ); }
    }
    internal static bool DouyuAuthRedirect(string location)
    {
        Uri uri;
        if (!Uri.TryCreate(location, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "www.douyu.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/api/passport/login") return false;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(parts[0]);
                if (query.ContainsKey(key)) return false;
                query[key] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            }
        }
        catch (UriFormatException) { return false; }
        string code, uid, client, callback; long identity;
        return query.TryGetValue("code", out code) && code.Length > 0 && code.Length <= 8192
            && query.TryGetValue("uid", out uid) && long.TryParse(uid, out identity) && identity > 0
            && query.TryGetValue("client_id", out client) && (client == "1" || client == "")
            && query.TryGetValue("callback", out callback) && callback == "livehubLoginCheck";
    }
    internal static void RememberVerified(string platform, List<CookieRecord> records, Result result)
    {
        if (result == null || result.State != "valid") return;
        lock (PlatformLogin.CredentialGate)
        {
            Evidence current = ReadLocal(platform);
            if (current.State != "saved" || LoginCapture.Fingerprint(current.Records) != LoginCapture.Fingerprint(records)) return;
            result.Stamp = current.Stamp; result.CheckedUtc = result.VerifiedUtc = DateTime.UtcNow.Ticks;
            try { LiveHub.AtomicWriteAllText(ResultPath(platform), new JavaScriptSerializer().Serialize(result)); }
            catch (Exception ex) { Diagnostics.Report("登录确认结果保存", ex); }
        }
    }
    private static Result NewResult(string state, string reason) { return new Result { State = state, Reason = reason }; }
    private static bool Code(IDictionary<string, object> map, string key, int expected)
    {
        object value; int parsed;
        return map != null && map.TryGetValue(key, out value) && int.TryParse(Convert.ToString(value), out parsed) && parsed == expected;
    }
    private static bool Flag(IDictionary<string, object> map, string key, bool expected)
    {
        object value;
        return map != null && map.TryGetValue(key, out value) && value is bool && (bool)value == expected;
    }
    private static bool Identity(IDictionary<string, object> map, string key)
    {
        object value; long number;
        return map != null && map.TryGetValue(key, out value) && long.TryParse(Convert.ToString(value), out number) && number > 0;
    }
    internal static Result Evaluate(string platform, int status, string body)
    {
        if (status == 401 || status == 403 || status == 429 || (status >= 300 && status < 400))
            return NewResult("restricted", "平台拒绝或限制检查请求，可能需要网页登录/验证；未判定 Cookie 已失效。" );
        if (status != 200) return NewResult("network", "平台暂不可用或连接异常，无法确认登录态。" );
        if (string.IsNullOrWhiteSpace(body)) return NewResult("restricted", "平台返回空内容，可能被网页验证拦截；请重新登录或稍后检测。" );
        // Only a fixed callback supplied by this client is accepted; no JavaScript is evaluated.
        string json = body.Trim();
        if (json.StartsWith("livehubLoginCheck("))
        {
            json = json.Substring("livehubLoginCheck(".Length).TrimEnd(';', ' ', '\r', '\n');
            if (json.EndsWith(")")) json = json.Substring(0, json.Length - 1);
        }
        Dictionary<string, object> root;
        try { root = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }.Deserialize<Dictionary<string, object>>(json); }
        catch (Exception) { return NewResult("unknown", "响应格式变化或网页验证，无法确认；请使用网页登录核验。" ); }
        if (root == null) return NewResult("unknown", "未取得明确登录证据。" );
        var data = LiveHub.LookupData.Obj(root, "data");
        bool valid = false, invalid = false;
        if (platform == "bili")
        {
            valid = Code(root, "code", 0) && Flag(data, "isLogin", true) && Identity(data, "mid");
            invalid = Code(root, "code", -101) || (Code(root, "code", 0) && Flag(data, "isLogin", false));
        }
        else if (platform == "huya")
        {
            valid = Flag(root, "isLogined", true) && Identity(root, "uid");
            invalid = Flag(root, "isLogined", false);
        }
        else if (platform == "douyu")
        {
            // The official frontend accepts safeAuth only when error is explicitly zero.
            valid = Code(root, "error", 0) && data != null;
            invalid = Code(root, "error", 16);
        }
        else if (platform == "douyin")
        {
            var user = LiveHub.LookupData.Obj(root, "user");
            valid = Code(root, "status_code", 0) && Identity(user, "uid");
            object message;
            invalid = Code(root, "status_code", 8) && root.TryGetValue("status_msg", out message)
                && (Convert.ToString(message) == "用户未登录" || Convert.ToString(message) == "用户未登陆");
        }
        return valid ? NewResult("valid", "平台只读登录检查明确确认当前凭据已登录。" )
            : invalid ? NewResult("invalid", "平台明确返回未登录，当前保存的登录凭据已失效或被撤销。" )
            : NewResult("unknown", "平台未返回明确登录结论；播放、弹幕或搜索成功不会代替登录验证。" );
    }
    internal static string Endpoint(string platform)
    {
        switch (platform)
        {
            case "bili": return "https://api.bilibili.com/x/web-interface/nav";
            case "huya": return "https://l.huya.com/udb_web/udbport2.php?m=HuyaLogin&do=checkLogin";
            case "douyu": return "https://passport.douyu.com/wgapi/member/passport/safeAuth?client_id=1&redirect_url=https%3A%2F%2Fwww.douyu.com%2F&did=&callback=livehubLoginCheck";
            case "douyin": return "https://www.douyin.com/aweme/v1/web/user/profile/self/?device_platform=webapp&aid=6383";
            default: throw new ArgumentException("未知平台");
        }
    }
    private static Reply Fetch(string platform, List<CookieRecord> records)
    {
        string endpoint = Endpoint(platform);
        if (platform == "douyu")
            foreach (CookieRecord record in records)
                if ((record.Name == "dy_did" || record.Name == "did") && !record.Expired()
                    && CredentialCookies.DomainAllowed(record.Domain, platform))
                { endpoint = endpoint.Replace("&did=&", "&did=" + Uri.EscapeDataString(record.Value) + "&"); break; }
        Uri uri = new Uri(endpoint);
        string cookie = CredentialCookies.HeaderForUri(records, uri);
        if (cookie.Length == 0) return new Reply { Status = 200, Body = "{}" };
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.Method = "GET"; request.AllowAutoRedirect = false;
        request.Timeout = request.ReadWriteTimeout = 10000;
        request.UserAgent = LiveHub.DouyinUserSearch.UserAgent;
        request.Referer = platform == "bili" ? "https://www.bilibili.com/" : platform == "huya" ? "https://www.huya.com/"
            : platform == "douyu" ? "https://www.douyu.com/" : "https://www.douyin.com/";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Headers["Cookie"] = cookie;
        try
        {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var text = new StringBuilder(); var buffer = new char[4096]; int count;
                DateTime deadline = DateTime.UtcNow.AddSeconds(12);
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + count > 256 * 1024 || DateTime.UtcNow > deadline) throw new InvalidDataException();
                    text.Append(buffer, 0, count);
                }
                return new Reply { Status = (int)response.StatusCode, Body = text.ToString(), Location = response.Headers["Location"] };
            }
        }
        catch (WebException ex)
        {
            using (var response = ex.Response as HttpWebResponse)
                if (response != null) return new Reply { Status = (int)response.StatusCode };
            throw;
        }
    }
    internal static string Title(Result result)
    {
        switch (result.State)
        {
            case "valid": return "登录有效";
            case "invalid": return "登录失效";
            case "expired": return "登录凭据已到期";
            case "restricted": return "需要网页核验";
            case "checking": return "正在检测";
            case "saved": return "已保存，待验证";
            case "stale": return "上次有效，待复查";
            case "metadata": return "需要重新登录";
            case "unreadable": return "凭据暂不可读";
            case "network": return "网络异常，无法确认";
            case "unknown": return "无法确认登录态";
            default: return "未保存登录凭据";
        }
    }
    internal static Color ColorFor(Result result)
    {
        return result.State == "valid" ? ModernTheme.Success
            : result.State == "invalid" || result.State == "expired" ? ModernTheme.Danger
            : result.State == "missing" ? ModernTheme.TextSecondary
            : result.State == "checking" ? ModernTheme.Accent
            : ModernTheme.IsDark ? Color.FromArgb(255, 190, 75) : Color.FromArgb(145, 85, 0);
    }
    internal static string TimeText(long ticks)
    {
        return ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") : "尚未在线验证";
    }
    internal static string Details(string platform)
    {
        Result result = Get(platform);
        return LiveHub.Platforms.Name(platform) + " Cookie / 登录态：" + Title(result) + "\r\n  " + result.Reason
            + "\r\n  最近检测：" + TimeText(result.CheckedUtc)
            + (result.VerifiedUtc > 0 ? "；上次明确有效：" + TimeText(result.VerifiedUtc) : "") + "\r\n";
    }
}
