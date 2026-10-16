using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

internal static partial class LiveHub
{
    internal static class DouyinUserSearch
    {
        internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36 Edg/154.0.0.0";
        internal const string Endpoint = "https://www.douyin.com/aweme/v1/web/discover/search/";
        private static readonly object CacheGate = new object();
        private static string observedCookieState;
        private static long cookieGeneration;
        // Protocol fields observed in the official search page, 2026-10-13.
        // No third-party signing code, copied session tokens or device identifiers.
        internal static string Url(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 600) throw new LookupFailure("input", "请输入主播昵称或抖音号。");
            return Endpoint + "?device_platform=webapp&aid=6383&channel=channel_pc_web&search_channel=aweme_user_web"
                + "&keyword=" + Uri.EscapeDataString(keyword.Trim())
                + "&search_source=normal_search&query_correct_type=1&is_filter_search=0&from_group_id=&disable_rs=0"
                + "&offset=0&count=10&need_filter_settings=1&list_type=single&pc_search_top_1_params=" + Uri.EscapeDataString("{\"enable_ai_search_top_1\":1}")
                + "&update_version_code=170400&pc_client_type=1&pc_libra_divert=Windows&support_h265=1&support_dash=0"
                + "&cpu_core_num=" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
                + "&version_code=170400&version_name=17.4.0&cookie_enabled=true&screen_width=1280&screen_height=800"
                + "&browser_language=zh-CN&browser_platform=Win32&browser_name=Edge&browser_version=154.0.0.0&browser_online=true"
                + "&engine_name=Blink&engine_version=154.0.0.0&os_name=Windows&os_version=10&device_memory=8"
                + "&platform=PC&downlink=10&effective_type=4g&round_trip_time=100";
        }
        internal static bool MatchesRequest(string url, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return false;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !LookupUrls.Basic(uri) || uri.Host != "www.douyin.com"
                || uri.AbsolutePath != "/aweme/v1/web/discover/search/") return false;
            string name = null, channel = null, offset = null; var keys = new HashSet<string>();
            try {
                foreach (string pair in uri.Query.TrimStart('?').Split('&')) {
                    string[] parts = pair.Split(new[] { '=' }, 2); string key = Uri.UnescapeDataString(parts[0]);
                    if (!keys.Add(key)) return false;
                    string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace("+", " ")) : "";
                    if (key == "keyword") name = value;
                    else if (key == "search_channel") channel = value;
                    else if (key == "offset") offset = value;
                }
            } catch (UriFormatException) { return false; }
            return name == keyword.Trim() && channel == "aweme_user_web" && (offset == null || offset == "0");
        }
        internal static string SourceStamp()
        {
            try {
                string path = PlatformLogin.CookiesPath(PlatformLogin.Get("douyin")) + ".json";
                var file = new FileInfo(path);
                return path + ":" + (file.Exists ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + file.Length : "none");
            } catch (Exception) { return "unavailable"; }
        }
        internal static string CacheStamp()
        {
            string state = SourceStamp();
            lock (CacheGate) {
                if (observedCookieState != state) { observedCookieState = state; cookieGeneration++; }
                return cookieGeneration.ToString(CultureInfo.InvariantCulture);
            }
        }
        internal static LookupResult Search(LookupVerifier verifier, string keyword, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Transport carries cookies only for their original platform/domain/path.
            // Request even without a saved jar: tests and server responses determine the outcome.
            return Parse(verifier.Get(Url(keyword), token).Body, keyword, "douyin_http");
        }
        private static string NumericId(Dictionary<string, object> user, string key)
        {
            object value; if (user == null || !user.TryGetValue(key, out value) || value == null) return null;
            if (!(value is string) && !(value is long) && !(value is int) && !(value is decimal)) return null;
            if (value is decimal && decimal.Truncate((decimal)value) != (decimal)value) return null;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return LookupData.Digits(text) && text != "0" ? text : null;
        }
        internal static LookupResult Parse(string body, string keyword, string source)
        {
            if (string.IsNullOrWhiteSpace(body)) throw new LookupFailure("auth", "抖音用户搜索未返回数据；请使用“抖音网页辅助”检查登录或验证。");
            if (body.Length > 4 * 1024 * 1024) throw new LookupFailure("format", "抖音搜索响应过长。");
            var data = LookupData.Json(body);
            string code = LookupData.Text(data, "status_code");
            if (code == "2483") throw new LookupFailure("auth", "抖音未接受当前搜索会话（2483）；可使用“抖音网页辅助”恢复后重试。");
            if (code != "0") throw new LookupFailure("blocked", "抖音用户搜索受阻；请使用“抖音网页辅助”查看平台提示。");
            object raw;
            if (data == null || !data.TryGetValue("user_list", out raw) || !(raw is IList))
                throw new LookupFailure("format", "抖音未返回可识别的用户列表；可使用网页辅助核对。");
            var rows = (IList)raw; var result = new LookupResult(); var seen = new HashSet<string>();
            string page = LookupUrls.SearchPage("douyin", keyword); int malformed = 0;
            foreach (object item in rows) {
                var user = LookupData.Obj(item as Dictionary<string, object>, "user_info");
                string sec = LookupData.Text(user, "sec_uid");
                if (sec == null || !Regex.IsMatch(sec, @"^MS4wLj[A-Za-z0-9_-]{1,180}$", RegexOptions.CultureInvariant)) { malformed++; continue; }
                if (!seen.Add(sec)) continue;
                var candidate = new LookupCandidate { platform = "douyin", sec_uid = sec,
                    account_id = NumericId(user, "uid") ?? NumericId(user, "uid_str"),
                    display_name = LookupDiscovery.Clean(LookupData.Text(user, "nickname") ?? "待确认账号"),
                    profile_url = "https://www.douyin.com/user/" + sec, provider = "platform", source = source };
                string handle = LookupData.Text(user, "unique_id");
                if (string.IsNullOrWhiteSpace(handle)) handle = LookupData.Text(user, "short_id");
                if (handle != null && handle != "0" && Regex.IsMatch(handle, @"^[A-Za-z0-9_.-]{1,64}$")) candidate.account_handle = handle;
                LookupVerifier.Metadata(candidate, user);
                candidate.match_reasons = new[] { "抖音官方用户搜索候选；同名和相似名称不代表本人，直播房间另行核验" };
                candidate.sources.Add(new LookupSource { url = page, title = "抖音官方用户搜索", retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "平台返回的昵称、主页身份与抖音号线索" } });
                candidate.sources.Add(new LookupSource { url = candidate.profile_url, title = "搜索返回的官方账号主页", retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "sec_uid" } });
                result.candidates.Add(candidate); if (result.candidates.Count >= 20) break;
            }
            if (rows.Count > 0 && result.candidates.Count == 0 && malformed > 0)
                throw new LookupFailure("format", "抖音用户列表缺少有效主页身份，未将异常数据当作空结果。");
            LookupDiscovery.Rank(result.candidates, keyword, false);
            result.note = result.candidates.Count == 0
                ? "抖音本次返回空用户列表；不据此判定主播不存在。可补充昵称/抖音号，或使用“抖音网页辅助”核对。"
                : "抖音官方用户搜索返回 " + result.candidates.Count + " 个候选；请对照主页与简介选择本人，直播房间仍需独立核验。";
            result.status = result.candidates.Count == 0 ? "empty" : "platform";
            result.searched_at = DateTime.UtcNow.ToString("o");
            return result;
        }
    }
}
