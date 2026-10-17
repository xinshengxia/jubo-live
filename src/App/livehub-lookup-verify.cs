using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    internal static class LookupUrls
    {
        internal static bool Basic(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0;
        }
        internal static bool PublicSource(string value)
        {
            Uri uri; IPAddress ip;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && Basic(uri) && !IPAddress.TryParse(uri.Host, out ip)
                && uri.Host.Contains(".") && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool Room(Uri uri, out string platform)
        {
            platform = null; if (!Basic(uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (host == "live.bilibili.com" && Regex.IsMatch(uri.AbsolutePath, @"^/(?:blanc/)?[0-9]{1,24}/?$")) platform = "bili";
            else if ((host == "huya.com" || host == "www.huya.com") && Regex.IsMatch(uri.AbsolutePath, @"^/[0-9]{1,24}/?$")) platform = "huya";
            else if ((host == "douyu.com" || host == "www.douyu.com" || host == "m.douyu.com") && Regex.IsMatch(uri.AbsolutePath, @"^/[0-9]{1,24}/?$")) platform = "douyu";
            else if (host == "live.douyin.com" && Regex.IsMatch(uri.AbsolutePath, @"^/[0-9]{1,24}/?$")) platform = "douyin";
            return platform != null;
        }
        internal static bool Candidate(LookupCandidate candidate)
        {
            if (!Platforms.Known(candidate.platform) || (candidate.room_id != null && !LookupData.Digits(candidate.room_id))
                || (candidate.account_id != null && !LookupData.Digits(candidate.account_id))
                || (candidate.session_room_id != null && !LookupData.Digits(candidate.session_room_id))) return false;
            if (candidate.sec_uid != null && !Regex.IsMatch(candidate.sec_uid, @"^MS4wLj[A-Za-z0-9_-]{1,180}$")) return false;
            Uri uri; string platform;
            if (candidate.room_url != null)
            {
                if (!Uri.TryCreate(candidate.room_url, UriKind.Absolute, out uri) || !Room(uri, out platform) || platform != candidate.platform) return false;
                Target room = LookupData.Direct(platform, candidate.room_url);
                if (room == null || (candidate.room_id != null && candidate.room_id != room.Room)) return false;
                candidate.room_id = room.Room; candidate.room_url = room.Url;
            }
            if (candidate.profile_url != null)
            {
                if (!Uri.TryCreate(candidate.profile_url, UriKind.Absolute, out uri) || !Basic(uri)) return false;
                Match m;
                if (candidate.platform == "bili" && uri.Host == "space.bilibili.com" && (m = Regex.Match(uri.AbsolutePath, @"^/([0-9]{1,24})/?$")).Success)
                {
                    if (candidate.account_id != null && candidate.account_id != m.Groups[1].Value) return false;
                    candidate.account_id = m.Groups[1].Value;
                }
                else if (candidate.platform == "douyin" && uri.Host == "www.douyin.com" && (m = Regex.Match(uri.AbsolutePath, @"^/user/(MS4wLj[A-Za-z0-9_-]{1,180})/?$")).Success)
                {
                    if (candidate.sec_uid != null && candidate.sec_uid != m.Groups[1].Value) return false;
                    candidate.sec_uid = m.Groups[1].Value;
                }
                else if (!(candidate.platform == "huya" && HuyaAlias(candidate.profile_url))) return false;
            }
            return candidate.room_id != null || candidate.account_id != null || candidate.sec_uid != null || !string.IsNullOrWhiteSpace(candidate.display_name);
        }
        internal static bool HuyaAlias(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !Basic(uri) || (uri.Host != "www.huya.com" && uri.Host != "huya.com")
                || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            string path = uri.AbsolutePath.Trim('/');
            return Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z][A-Za-z0-9_-]{0,63}/?$")
                && !Regex.IsMatch(path, @"^(search|video|g|l|u|about|download|index|activity|user|login|register)$", RegexOptions.IgnoreCase);
        }
        internal static bool PlatformRequest(Uri uri)
        {
            if (!Basic(uri)) return false;
            switch (uri.Host.ToLowerInvariant())
            {
                case "api.live.bilibili.com": case "api.bilibili.com": case "live.bilibili.com": case "space.bilibili.com":
                case "www.huya.com": case "huya.com": case "search.cdn.huya.com":
                case "www.douyu.com": case "douyu.com": case "m.douyu.com":
                case "www.douyin.com": case "live.douyin.com": case "v.douyin.com": case "v.iesdouyin.com":
                case "www.iesdouyin.com": case "webcast.amemv.com": return true;
                default: return false;
            }
        }
        internal static string SearchPage(string platform, string keyword)
        {
            string encoded = Uri.EscapeDataString(keyword);
            return platform == "bili" ? "https://search.bilibili.com/live?keyword=" + encoded
                : platform == "huya" ? "https://www.huya.com/search?hsk=" + encoded
                : platform == "douyu" ? "https://www.douyu.com/search/?kw=" + encoded
                : "https://www.douyin.com/search/" + encoded + "?type=user";
        }
    }

    internal sealed class LookupHttpReply
    {
        internal int Status;
        internal string Body;
        internal string Location;
        internal string Url;
    }

    internal sealed class LookupVerifier
    {
        // Per-instance synthetic transport; production never falls back to a fake response.
        internal Func<string, CancellationToken, LookupHttpReply> Transport;
        private readonly Dictionary<string, Tuple<DateTime, LookupCandidate>> mappings = new Dictionary<string, Tuple<DateTime, LookupCandidate>>();
        internal LookupVerifier() { Transport = Http; }
        internal void ClearCache() { lock (mappings) mappings.Clear(); }
        internal LookupHttpReply Get(string url, CancellationToken token)
        {
            var seen = new HashSet<string>();
            for (int hop = 0; hop < 4; hop++)
            {
                token.ThrowIfCancellationRequested(); Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !LookupUrls.PlatformRequest(uri))
                    throw new LookupFailure("url", "平台链接或跳转地址不受支持。");
                if (!seen.Add(uri.AbsoluteUri)) throw new LookupFailure("url", "分享链接出现循环跳转。");
                LookupHttpReply reply = Transport(uri.AbsoluteUri, token); reply.Url = uri.AbsoluteUri;
                if (reply.Status >= 300 && reply.Status < 400)
                {
                    Uri target; if (string.IsNullOrEmpty(reply.Location) || !Uri.TryCreate(uri, reply.Location, out target) || !LookupUrls.PlatformRequest(target))
                        throw new LookupFailure("url", "平台跳转被拒绝，未访问无关地址。");
                    url = target.AbsoluteUri; continue;
                }
                if (reply.Status == 401) throw new LookupFailure("auth", "平台要求登录后查询。");
                if (reply.Status == 429) throw new LookupFailure("rate-limit", "平台请求频率受限。");
                if (reply.Status == 404) throw new LookupFailure("not-found", "公开入口暂不可访问，请核对链接。");
                if (reply.Status != 200) throw new LookupFailure("blocked", "平台请求受阻（验证、登录、限流或页面不可访问），请稍后重试。");
                return reply;
            }
            throw new LookupFailure("url", "分享链接超过三次跳转。");
        }
        private static LookupHttpReply Http(string url, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.AllowAutoRedirect = false; request.Timeout = request.ReadWriteTimeout = 4000;
            request.UserAgent = DouyinUserAgent; request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Referer = new Uri(url).GetLeftPart(UriPartial.Authority) + "/";
            if (new Uri(url).Host == "www.douyin.com" && new Uri(url).AbsolutePath == "/aweme/v1/web/discover/search/")
            {
                request.UserAgent = DouyinUserSearch.UserAgent;
                request.Accept = "application/json, text/plain, */*";
                request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
                request.Referer = "https://www.douyin.com/";
            }
            if (new Uri(url).Host == "v.douyin.com" || new Uri(url).Host == "v.iesdouyin.com")
            {
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                request.Referer = "https://www.douyin.com/";
            }
            string cookie = PlatformLogin.CookieHeaderForUri(new Uri(url));
            if (new Uri(url).Host == "api.bilibili.com")
            {
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                request.Referer = "https://www.bilibili.com/";
                if (cookie.IndexOf("buvid3=", StringComparison.OrdinalIgnoreCase) < 0) cookie += (cookie.Length == 0 ? "" : "; ") + "buvid3=" + Guid.NewGuid().ToString();
            }
            if (new Uri(url).Host == "live.douyin.com") cookie = string.IsNullOrEmpty(cookie) ? "__ac_nonce=" + DouyinNonce() : cookie + "; __ac_nonce=" + DouyinNonce();
            if (!string.IsNullOrEmpty(cookie)) request.Headers["Cookie"] = cookie;
            using (token.Register(delegate { request.Abort(); }))
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                        return new LookupHttpReply { Status = (int)response.StatusCode, Location = response.Headers["Location"] };
                    using (Stream stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var body = new StringBuilder(); char[] buffer = new char[4096]; int count;
                        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); if (body.Length + count > 4 * 1024 * 1024) throw new LookupFailure("blocked", "平台响应过长，已停止读取。");
                            body.Append(buffer, 0, count);
                        }
                        return new LookupHttpReply { Status = (int)response.StatusCode, Body = body.ToString() };
                    }
                }
            }
            catch (WebException ex)
            {
                token.ThrowIfCancellationRequested(); using (var response = ex.Response as HttpWebResponse)
                {
                    if (response != null) return new LookupHttpReply { Status = (int)response.StatusCode };
                }
                throw new LookupFailure(ex.Status == WebExceptionStatus.Timeout ? "timeout" : "network", "平台连接超时或网络受阻，未判定主播不存在。");
            }
        }
        internal LookupCandidate Verify(LookupCandidate input, CancellationToken token)
        {
            var result = LookupData.Copy(input); result.verification = "unverified"; result.user_confirmed = false;
            result.account_verified = false; result.live_status = "unknown"; result.resolution_path = null; result.failure_code = null;
            string key = result.platform + ":" + (result.sec_uid ?? result.account_id) + ":" + result.room_id + ":" + result.session_room_id + ":" + result.direct_input + ":" + RoomEvidence(result)
                + ":" + result.profile_url + ":" + result.account_handle + ":" + HuyaAliasEvidence(result);
            Tuple<DateTime, LookupCandidate> known;
            lock (mappings) if (mappings.TryGetValue(key, out known) && (DateTime.UtcNow - known.Item1).TotalSeconds < 60)
            {
                LookupCandidate cached = LookupData.Copy(known.Item2);
                foreach (LookupSource source in result.sources) if (!cached.sources.Exists(delegate(LookupSource s) { return s.url == source.url; })) cached.sources.Add(source);
                cached.relevance = result.relevance; cached.relevance_note = result.relevance_note; cached.relevance_sources = result.relevance_sources;
                cached.match_reasons = result.match_reasons; cached.lookup_terms = result.lookup_terms; cached.platform_update = result.platform_update; cached.update_sources = result.update_sources;
                cached.verification_note = "近期平台身份映射已核验（" + cached.verified_at + "），不代表当前开播状态。"; return cached;
            }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(5000);
                try
                {
                    if (!LookupUrls.Candidate(result)) throw new LookupFailure("url", "候选链接或编号不合法。");
                    if (result.room_id == null && result.account_id != null && (result.platform == "huya" || result.platform == "douyu") && result.profile_url == null)
                        ResolveAccountRoom(result, deadline.Token);
                    if (result.platform == "bili") Bili(result, deadline.Token);
                    else if (result.platform == "huya") Huya(result, deadline.Token);
                    else if (result.platform == "douyu") Douyu(result, deadline.Token);
                    else Douyin(result, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!LookupData.Digits(result.room_id)) throw new LookupFailure("no-room", "平台未提供可用数字房间号。");
                    result.room_url = Platforms.Get(result.platform).BaseUrl + result.room_id;
                    result.account_verified = true; result.verification = "verified"; result.verified_at = DateTime.UtcNow.ToString("o"); result.verification_note = "平台已核对账号与数字房间归属；请确认这是要找的主播。";
                    lock (mappings) { if (mappings.Count >= 64) mappings.Clear(); mappings[key] = Tuple.Create(DateTime.UtcNow, LookupData.Copy(result)); }
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested(); result.verification = "blocked"; result.failure_code = "timeout"; result.verification_note = "房间核验超时，请重试。";
                }
                catch (LookupFailure ex)
                {
                    result.failure_code = ex.Code;
                    result.verification = ex.Code == "mismatch" ? "mismatch" : ex.Code == "no-room" && (result.account_verified || result.account_id != null || result.sec_uid != null) ? "account-only" : "blocked";
                    result.verification_note = ex.Message + " " + LookupDiscovery.Recovery(ex.Code, false);
                }
                token.ThrowIfCancellationRequested(); return result;
            }
        }
        private void ResolveAccountRoom(LookupCandidate candidate, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(candidate.display_name)) throw new LookupFailure("no-room", "已保留账号，但缺少可用于平台续查的昵称。");
            LookupResult found = SearchNative(candidate.platform, candidate.display_name, token); token.ThrowIfCancellationRequested();
            foreach (LookupCandidate item in found.candidates)
            {
                if (item.account_id != candidate.account_id || !LookupData.Digits(item.room_id)) continue;
                candidate.room_id = item.room_id; candidate.room_url = item.room_url;
                if (candidate.follower_count == null) LookupData.Followers(candidate, item.follower_count);
                foreach (LookupSource source in item.sources) if (!candidate.sources.Exists(delegate(LookupSource s) { return s.url == source.url; })) candidate.sources.Add(source);
                return;
            }
            throw new LookupFailure("no-room", "已找到平台账号，平台续查暂未返回该账号的数字房间。");
        }
        private static void Owner(LookupCandidate candidate, string actual)
        {
            if (!LookupData.Digits(actual)) throw new LookupFailure("blocked", "平台没有返回稳定账号标识，无法核验房间归属。");
            if (candidate.account_id != null && candidate.account_id != actual) throw new LookupFailure("mismatch", "房间所属账号与所选候选不同，不能加入收藏。");
            if (!candidate.direct_input && candidate.account_id == null && candidate.sec_uid == null && !RoomEvidence(candidate))
                throw new LookupFailure("blocked", "候选缺少可关联的稳定账号标识，不能仅凭昵称或房间号确认归属。");
            candidate.account_id = actual;
        }
        // A source returned by the search service can identify a single platform room page.
        // Its own owner + room fields establish the association; no nickname-based join is performed.
        private static bool RoomEvidence(LookupCandidate candidate)
        {
            if (!LookupData.Digits(candidate.room_id) || candidate.sources == null) return false;
            foreach (LookupSource source in candidate.sources)
            {
                Target target = LookupData.Direct(candidate.platform, source.url);
                if (target != null && target.Platform == candidate.platform && target.Room == candidate.room_id) return true;
            }
            return false;
        }
        private void Bili(LookupCandidate candidate, CancellationToken token)
        {
            if (candidate.room_id == null && candidate.account_id != null)
            {
                var root = LookupData.Json(Get("https://api.live.bilibili.com/room/v1/Room/getRoomInfoOld?mid=" + candidate.account_id, token).Body);
                if (LookupData.Text(root, "code") != "0") throw new LookupFailure("blocked", "B站账号查询受阻。");
                candidate.room_id = LookupData.Text(LookupData.Obj(root, "data"), "roomid");
            }
            if (!LookupData.Digits(candidate.room_id) || candidate.room_id == "0")
            {
                candidate.room_id = null;
                if (LookupData.Digits(candidate.account_id))
                {
                    var profileRoot = LookupData.Json(Get("https://api.live.bilibili.com/live_user/v1/Master/info?uid=" + candidate.account_id, token).Body);
                    var profileData = LookupData.Obj(profileRoot, "data");
                    var info = LookupData.Obj(profileData, "info"); string uid = LookupData.Text(info, "uid");
                    if (uid != null && uid != candidate.account_id) throw new LookupFailure("mismatch", "B站账号资料返回了不同账号。");
                    candidate.account_verified = LookupData.Text(profileRoot, "code") == "0" && uid == candidate.account_id;
                    if (candidate.account_verified) { candidate.display_name = LookupData.Text(info, "uname") ?? candidate.display_name; Metadata(candidate, info); }
                    LookupData.Followers(candidate, LookupData.Text(profileData, "follower_num"));
                    candidate.profile_url = "https://space.bilibili.com/" + candidate.account_id;
                }
                throw new LookupFailure("no-room", "已保留B站账号，平台暂未返回数字直播间。");
            }
            var data = LookupData.Json(Get("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + candidate.room_id, token).Body);
            if (LookupData.Text(data, "code") != "0") throw new LookupFailure("blocked", "B站房间信息受阻或不可用。");
            var room = LookupData.Obj(data, "data"); string canonical = LookupData.Text(room, "room_id"), shortId = LookupData.Text(room, "short_id");
            if (!LookupData.Digits(canonical)) throw new LookupFailure("blocked", "B站未提供房间关联字段。");
            if (candidate.room_id != canonical && candidate.room_id != shortId) throw new LookupFailure("mismatch", "B站返回了不同房间。");
            Owner(candidate, LookupData.Text(room, "uid")); candidate.room_id = canonical; Metadata(candidate, room);
            string live = LookupData.Text(room, "live_status"); candidate.live_status = live == "1" ? "live" : live == "0" || live == "2" ? "offline" : "unknown";
            var user = LookupData.Obj(LookupData.Obj(LookupData.Json(Get("https://api.live.bilibili.com/live_user/v1/Master/info?uid=" + candidate.account_id, token).Body), "data"), "info");
            if (LookupData.Text(user, "uid") != null && LookupData.Text(user, "uid") != candidate.account_id) throw new LookupFailure("mismatch", "B站主播资料返回了不同账号。");
            candidate.display_name = LookupData.Text(user, "uname") ?? "B站账号 " + candidate.account_id; Metadata(candidate, user);
        }
        internal static Dictionary<string, object> Assigned(string html, string variable)
        {
            Match match = Regex.Match(html ?? "", @"\b" + Regex.Escape(variable) + @"\s*=\s*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            return match.Success ? ObjectAt(html, match.Index + match.Length) : null;
        }
        private static Dictionary<string, object> ObjectAt(string text, int start)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length || text[start] != '{') return null;
            int depth = 0; bool quoted = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted) { if (c == '\\') { i++; continue; } if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try { return LookupData.Json(text.Substring(start, i - start + 1)); } catch (LookupFailure) { return null; }
                }
            }
            return null;
        }
        private void Huya(LookupCandidate candidate, CancellationToken token)
        {
            bool alias = !LookupData.Digits(candidate.room_id) && HuyaAliasEvidence(candidate);
            if (!LookupData.Digits(candidate.room_id) && !alias) throw new LookupFailure("no-room", "虎牙候选没有可核验的数字房间号或有来源的官方别名链接。");
            string html = Get(alias ? candidate.profile_url : "https://www.huya.com/" + candidate.room_id, token).Body;
            var profile = Assigned(html, "TT_PROFILE_INFO"); var room = Assigned(html, "TT_ROOM_DATA");
            string roomId = LookupData.Text(profile, "profileRoom") ?? LookupData.Text(room, "profileRoom");
            if (roomId == null) throw new LookupFailure("blocked", "虎牙页面没有账号与房间关联字段，可能需要验证或页面已变化。");
            if (alias)
            {
                if (!LookupData.Digits(roomId)) throw new LookupFailure("blocked", "虎牙别名页面没有有效的数字房间关联。");
                candidate.room_id = roomId;
                candidate.sources.Add(new LookupSource { url = "https://www.huya.com/" + roomId, title = "虎牙官方别名页返回的数字房间", retrieved_at = DateTime.UtcNow.ToString("o") });
            }
            else if (roomId != candidate.room_id) throw new LookupFailure("mismatch", "虎牙返回了不同房间。");
            Owner(candidate, LookupData.Text(profile, "lp") ?? LookupData.Text(room, "id")); candidate.display_name = LookupData.Text(profile, "nick") ?? candidate.display_name; Metadata(candidate, profile); Metadata(candidate, room);
        }
        private static bool HuyaAliasEvidence(LookupCandidate candidate)
        {
            return candidate.platform == "huya" && LookupUrls.HuyaAlias(candidate.profile_url)
                && candidate.sources != null && candidate.sources.Exists(delegate(LookupSource s) { return s.url == candidate.profile_url; });
        }
        private void Douyu(LookupCandidate candidate, CancellationToken token)
        {
            if (!LookupData.Digits(candidate.room_id)) throw new LookupFailure("no-room", "斗鱼候选没有可核验的数字房间号。");
            var root = LookupData.Json(Get("https://www.douyu.com/betard/" + candidate.room_id, token).Body); var room = LookupData.Obj(root, "room");
            string roomId = LookupData.Text(room, "room_id");
            if (roomId == null) throw new LookupFailure("blocked", "斗鱼没有返回账号与房间关联信息。");
            if (roomId != candidate.room_id) throw new LookupFailure("mismatch", "斗鱼返回了不同房间。");
            Owner(candidate, LookupData.Text(room, "owner_uid")); candidate.display_name = LookupData.Text(room, "owner_name") ?? candidate.display_name; Metadata(candidate, room);
            string live = LookupData.Text(room, "show_status"); candidate.live_status = live == "1" ? "live" : live == "2" ? "offline" : "unknown";
        }
        private void Douyin(LookupCandidate candidate, CancellationToken token)
        {
            if (candidate.sec_uid == null && !candidate.direct_input && !RoomEvidence(candidate)) throw new LookupFailure("blocked", "候选缺少抖音 sec_uid，无法仅凭昵称或数字号确认主播身份。");
            if (candidate.session_room_id == null)
            {
                if (candidate.room_id != null)
                {
                    string html = Get("https://live.douyin.com/" + candidate.room_id, token).Body;
                    DouyinRoom page = ParseDouyinRoom(html);
                    if (page == null || page.OwnerSecUid == null) page = LookupDouyinPage(html);
                    if (page == null || page.OwnerSecUid == null || page.RoomId == null) throw new LookupFailure("blocked", "抖音房间页缺少账号关联，可能被验证、登录或页面变化阻挡。");
                    if (candidate.sec_uid != null && candidate.sec_uid != page.OwnerSecUid) throw new LookupFailure("mismatch", "抖音房间页主播与所选账号不同。");
                    candidate.sec_uid = page.OwnerSecUid; candidate.session_room_id = page.RoomId;
                }
                else if (candidate.profile_url != null)
                {
                    try
                    {
                        DouyinRoom page = LookupDouyinPage(Get(candidate.profile_url, token).Body);
                        if (page != null)
                        {
                            if (page.OwnerSecUid != candidate.sec_uid) throw new LookupFailure("mismatch", "抖音主页中的直播账号与所选账号不同。");
                            candidate.session_room_id = page.RoomId; candidate.resolution_path = "profile_ssr_then_reflow";
                        }
                    }
                    catch (LookupFailure ex) { if (ex.Code == "mismatch" || ex.Code == "url") throw; }
                }
                if (candidate.session_room_id == null && candidate.sec_uid != null) DouyinProfileAlias(candidate, token);
            }
            if (candidate.sec_uid == null || !LookupData.Digits(candidate.session_room_id)) throw new LookupFailure("no-room", "缺少可核验的抖音账号与内部场次号。");
            var root = LookupData.Json(Get("https://webcast.amemv.com/webcast/room/reflow/info/?room_id=" + candidate.session_room_id
                + "&sec_user_id=" + Uri.EscapeDataString(candidate.sec_uid) + "&live_id=1&app_id=1128&type_id=0", token).Body);
            if (LookupData.Text(root, "status_code") != "0") throw new LookupFailure("blocked", "抖音 reflow 返回受阻或过期场次，不能判定主播不存在。");
            ApplyReflow(candidate, root);
        }
        private void DouyinProfileAlias(LookupCandidate candidate, CancellationToken token)
        {
            string profileApi = "https://www.iesdouyin.com/web/api/v2/user/info/?sec_uid=" + Uri.EscapeDataString(candidate.sec_uid);
            bool officialHandle = false;
            try
            {
                var root = LookupData.Json(Get(profileApi, token).Body); var user = LookupData.Obj(root, "user_info");
                string sec = LookupData.Text(user, "sec_uid");
                if (sec != null && sec != candidate.sec_uid) throw new LookupFailure("mismatch", "抖音账号资料与所选 sec_uid 不一致。");
                if (LookupData.Text(root, "status_code") != "0" || sec == null)
                    throw new LookupFailure("blocked", "抖音账号资料接口受阻；已保留所选账号线索。");
                string handle = LookupData.Text(user, "unique_id");
                if (string.IsNullOrWhiteSpace(handle)) handle = LookupData.Text(user, "short_id");
                candidate.account_verified = true; candidate.display_name = LookupData.Text(user, "nickname") ?? candidate.display_name; Metadata(candidate, user);
                candidate.profile_url = "https://www.douyin.com/user/" + candidate.sec_uid;
                candidate.account_handle = ValidDouyinHandle(handle) && handle != "0" ? handle : null;
                officialHandle = candidate.account_handle != null;
                RecordResolutionSource(candidate, profileApi, "抖音公开账号资料", "sec_uid、抖音号与平台昵称");
                string session = LookupData.Text(user, "room_id");
                if (LookupData.Digits(session) && session != "0")
                {
                    candidate.session_room_id = session; candidate.resolution_path = "profile_api_then_reflow"; return;
                }
            }
            catch (LookupFailure ex) { if (ex.Code == "mismatch" || ex.Code == "url") throw; }
            if (candidate.account_handle != null && !ValidDouyinHandle(candidate.account_handle)) throw new LookupFailure("url", "抖音号格式无效，不能用于直播别名解析。");
            if (!ValidDouyinHandle(candidate.account_handle) || candidate.account_handle == "0")
                throw new LookupFailure("no-room", candidate.account_verified ? "账号资料已核实，但平台未提供可解析的抖音号或直播场次；可稍后重新解析。"
                    : "账号主页未返回直播数据，账号资料接口也未提供抖音号；可重新解析或使用直播分享链接。");
            string alias = "https://live.douyin.com/" + Uri.EscapeDataString(candidate.account_handle);
            DouyinRoom page = LookupDouyinPage(Get(alias, token).Body);
            if (page == null || page.OwnerSecUid == null || !LookupData.Digits(page.RoomId))
                throw new LookupFailure("no-room", "已取得抖音号，但直播别名页未返回可核验场次；可能受验证阻挡，可重新解析或使用直播分享链接。");
            if (page.OwnerSecUid != candidate.sec_uid) throw new LookupFailure("mismatch", "抖音号对应直播间的主播与所选账号不同，未绑定房间。");
            candidate.account_verified = true; candidate.session_room_id = page.RoomId;
            candidate.resolution_path = officialHandle ? "profile_api_then_alias_ssr_then_reflow" : "handle_hint_then_alias_ssr_then_reflow";
            RecordResolutionSource(candidate, alias, "抖音号对应直播页", "同一 sec_uid 与内部直播场次（数字网页号继续核验）");
        }
        private static bool ValidDouyinHandle(string handle)
        {
            return handle != null && Regex.IsMatch(handle, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
        }
        private static void RecordResolutionSource(LookupCandidate candidate, string url, string title, string supports)
        {
            candidate.sources.RemoveAll(delegate(LookupSource source) { return source.url == url; });
            candidate.sources.Add(new LookupSource { url = url, title = title, retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { supports } });
        }
        // Read Flight strings as JSON data; never execute page scripts or join by nickname.
        internal static DouyinRoom LookupDouyinPage(string html)
        {
            foreach (string view in PageViews(html))
                foreach (Match match in Regex.Matches(view, "\"roomId\"\\s*:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                {
                    int start = match.Index;
                    for (int attempt = 0; attempt < 20 && start > Math.Max(0, match.Index - 65536); attempt++)
                    {
                        start = view.LastIndexOf('{', start - 1); if (start < 0) break;
                        var data = ObjectAt(view, start); var anchor = LookupData.Obj(data, "anchor");
                        string id = LookupData.Text(data, "roomId"), sec = LookupData.Text(anchor, "sec_uid");
                        if (LookupData.Digits(id) && sec != null)
                            return new DouyinRoom { RoomId = id, OwnerSecUid = sec, OwnerId = LookupData.Text(anchor, "id_str") ?? LookupData.Text(anchor, "uid"), Owner = LookupData.Text(anchor, "nickname") };
                    }
                }
            return null;
        }
        private static List<string> PageViews(string html)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            var views = new List<string> { WebUtility.HtmlDecode(html ?? "") };
            foreach (string literal in ExtractPacePayloads((html ?? "").Replace("self.__next_f.push([", "self.__pace_f.push([").Replace("window.__next_f.push([", "self.__pace_f.push([")))
                try { views.Add(WebUtility.HtmlDecode(json.Deserialize<string>(literal))); } catch (Exception) { continue; }
            foreach (string view in views.ToArray())
                for (int offset = 0; offset < view.Length && views.Count < 64;)
                {
                    int start = view.IndexOf('"', offset); if (start < 0) break;
                    int end = FindJsonStringEnd(view, start); if (end < 0) break; offset = end + 1;
                    string literal = view.Substring(start, end - start + 1);
                    if (!literal.Contains("roomId") && !literal.Contains("roomInfo")) continue;
                    try { views.Add(WebUtility.HtmlDecode(json.Deserialize<string>(literal))); } catch (Exception) { continue; }
                }
            return views;
        }
        internal static void ApplyReflow(LookupCandidate candidate, Dictionary<string, object> root)
        {
            var room = LookupData.Obj(LookupData.Obj(root, "data"), "room"); var owner = LookupData.Obj(room, "owner");
            if (LookupData.Text(owner, "sec_uid") != candidate.sec_uid || LookupData.Text(room, "id_str") != candidate.session_room_id)
                throw new LookupFailure("mismatch", "抖音房间的主播或内部场次与请求不一致。");
            string web = LookupData.Text(owner, "web_rid");
            if (!LookupData.Digits(web)) throw new LookupFailure("no-room", "抖音未提供数字 web_rid，不能用内部场次号加入收藏。");
            if (candidate.room_id != null && candidate.room_id != web) throw new LookupFailure("mismatch", "抖音返回了不同的网页房间号。");
            Owner(candidate, LookupData.Text(owner, "id_str") ?? LookupData.Text(owner, "id"));
            candidate.room_id = web; candidate.display_name = LookupData.Text(owner, "nickname") ?? candidate.display_name; Metadata(candidate, owner); Metadata(candidate, room);
            candidate.account_verified = true;
            string status = LookupData.Text(room, "status"); candidate.live_status = status == "2" ? "live" : status == "4" ? "offline" : "unknown";
            if (candidate.resolution_path == null) candidate.resolution_path = "room_ssr_then_reflow";
            string reflow = "https://webcast.amemv.com/webcast/room/reflow/info/?room_id=" + candidate.session_room_id
                + "&sec_user_id=" + Uri.EscapeDataString(candidate.sec_uid) + "&live_id=1&app_id=1128&type_id=0";
            RecordResolutionSource(candidate, reflow, "抖音房间归属核验", "主播 sec_uid、内部场次、数字网页号与核验时直播状态");
        }
        internal static LookupCandidate ResolveShare(string share, CancellationToken token)
        {
            var verifier = new LookupVerifier(); return verifier.ShareCore(share, token);
        }
        internal LookupCandidate ShareCore(string share, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(5000);
                try
                {
                    string url = share;
                    for (int hop = 0; hop < 4; hop++)
                    {
                        deadline.Token.ThrowIfCancellationRequested(); Uri uri;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !LookupUrls.PlatformRequest(uri)) throw new LookupFailure("url", "分享跳转地址不合法。");
                        Match room = Regex.Match(uri.AbsolutePath, @"^/(?:reflow|room)/([0-9]{6,24})/?$");
                        string internalId = room.Success ? room.Groups[1].Value : Query(uri, "room_id");
                        string sec = Query(uri, "sec_user_id");
                        LookupCandidate candidate = null;
                        if (LookupData.Digits(internalId) && sec != null && Regex.IsMatch(sec, @"^MS4wLj[A-Za-z0-9_-]{1,180}$"))
                            candidate = new LookupCandidate { platform = "douyin", session_room_id = internalId, sec_uid = sec, source = "douyin_share_http", provider = "platform" };
                        else
                        {
                            Target direct = LookupData.Direct("douyin", url);
                            if (direct != null && direct.Platform == "douyin") candidate = new LookupCandidate { platform = "douyin", room_id = direct.Room, direct_input = true, source = "douyin_share_http", provider = "platform" };
                            else if (uri.Host == "www.douyin.com" && Regex.IsMatch(uri.AbsolutePath, @"^/user/MS4wLj[A-Za-z0-9_-]+/?$"))
                                candidate = new LookupCandidate { platform = "douyin", profile_url = uri.GetLeftPart(UriPartial.Path), sec_uid = uri.AbsolutePath.TrimEnd('/').Substring(6), source = "douyin_share_http", provider = "platform" };
                        }
                        if (candidate != null)
                        {
                            LookupCandidate verified = Verify(candidate, deadline.Token);
                            if (verified.verification != "verified") throw new LookupFailure("blocked", verified.verification_note);
                            verified.sources.Add(new LookupSource { url = share, title = "抖音分享 HTTP 跳转与房间核验", retrieved_at = DateTime.UtcNow.ToString("o") });
                            return verified;
                        }
                        if (hop == 3) break;
                        LookupHttpReply reply = Transport(uri.AbsoluteUri, deadline.Token);
                        if (reply.Status < 300 || reply.Status >= 400 || string.IsNullOrEmpty(reply.Location)) throw new LookupFailure("blocked", "分享链接没有返回可解析跳转，可能过期或需要验证。");
                        url = new Uri(uri, reply.Location).AbsoluteUri;
                    }
                    throw new LookupFailure("blocked", "分享链接未取得可核验的账号与数字房间。");
                }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new LookupFailure("timeout", "分享链接解析超时。"); }
            }
        }
        private static string Query(Uri uri, string name)
        {
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split(new char[] { '=' }, 2);
                if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == name) return Uri.UnescapeDataString(parts[1]);
            }
            return null;
        }
        internal static void Metadata(LookupCandidate candidate, Dictionary<string, object> row)
        {
            string category = First(row, "area_name", "parent_area_name", "game_name", "gameFullName", "cate2_name", "cate_name");
            string description = First(row, "signature", "sign", "introduction", "description", "desc");
            string title = First(row, "title", "room_name", "live_intro", "introduction");
            string avatar = First(row, "face", "uface", "avatar180", "game_avatarUrl180", "game_avatarUrl52", "avatarUrl", "avatar", "avatar_thumb", "logo", "owner_avatar");
            // 四个平台各自公开的粉丝字段：B站 attentions/attention/follower_num、虎牙 game_activityCount/fans、
            // 斗鱼 fansNum/followerCount、抖音 follower_count。只写入平台真实返回的纯数字。
            string followers = First(row, "attentions", "attention", "follower_count", "follower_num", "fansNum", "followerCount", "fans", "game_activityCount");
            if (!string.IsNullOrWhiteSpace(category)) candidate.category = LookupDiscovery.Clean(category);
            if (!string.IsNullOrWhiteSpace(description)) candidate.description = LookupDiscovery.Clean(description);
            if (!string.IsNullOrWhiteSpace(title)) candidate.live_title = LookupDiscovery.Clean(title);
            var nestedAvatar = LookupData.Obj(row, "avatar_thumb");
            if (nestedAvatar != null) { var urls = LookupData.Rows(nestedAvatar, "url_list"); if (urls != null) foreach (object raw in urls) { if (raw is string) { avatar = (string)raw; break; } } }
            if (!string.IsNullOrWhiteSpace(avatar)) {
                if (avatar.StartsWith("//")) avatar = "https:" + avatar;
                else if (avatar.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) avatar = "https://" + avatar.Substring(7);
                candidate.avatar_url = avatar;
            }
            if (!string.IsNullOrWhiteSpace(followers)) LookupData.Followers(candidate, followers);
        }
        private static string First(Dictionary<string, object> row, params string[] keys)
        {
            foreach (string key in keys) { string value = LookupData.Text(row, key); if (!string.IsNullOrWhiteSpace(value)) return value; } return null;
        }
        internal static LookupResult SearchPlatform(string platform, string keyword, CancellationToken token)
        {
            return new LookupVerifier().SearchNative(platform, keyword, token);
        }
        internal LookupResult SearchNative(string platform, string keyword, CancellationToken token)
        {
            var result = new LookupResult(); string url; string q = Uri.EscapeDataString(keyword);
            if (platform == "bili") url = null;
            else if (platform == "huya") url = "https://search.cdn.huya.com/?m=Search&do=getSearchContent&q=" + q + "&uid=0&v=4&typ=-5&livestate=0&rows=10";
            else if (platform == "douyu") url = "https://www.douyu.com/japi/search/api/searchUser?kw=" + q + "&page=1&pageSize=10";
            else if (platform == "douyin") return DouyinUserSearch.Search(this, keyword, token);
            else throw new LookupFailure("input", "不支持的平台。");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(8000);
                try
                {
                    if (platform == "bili") url = BiliSearchUrl(keyword, deadline.Token);
                    var data = LookupData.Json(Get(url, deadline.Token).Body); IEnumerable rows;
                    if (platform == "bili") { if (LookupData.Text(data, "code") != "0") throw new LookupFailure("blocked", "B站平台搜索受登录、签名或限流限制。"); rows = LookupData.Rows(LookupData.Obj(data, "data"), "result"); }
                    else if (platform == "huya") { if (LookupData.Text(LookupData.Obj(data, "responseHeader"), "status") != "0") throw new LookupFailure("blocked", "虎牙平台搜索受阻。"); rows = LookupData.Rows(LookupData.Obj(LookupData.Obj(data, "response"), "1"), "docs"); }
                    else { if (LookupData.Text(data, "error") != "0") throw new LookupFailure("blocked", "斗鱼平台搜索受阻。"); rows = LookupData.Rows(LookupData.Obj(data, "data"), "relateUser"); }
                    if (rows == null) throw new LookupFailure("blocked", "平台搜索没有返回可识别的主播分类。");
                    foreach (object raw in rows)
                    {
                        var row = raw as Dictionary<string, object>; if (platform == "douyu") row = LookupData.Obj(row, "anchorInfo");
                        var candidate = new LookupCandidate { platform = platform, source = "platform_search", provider = "platform",
                            display_name = LookupData.Text(row, platform == "bili" ? "uname" : platform == "huya" ? "game_nick" : "nickName"),
                            room_id = LookupData.Text(row, platform == "bili" ? "roomid" : platform == "huya" ? "room_id" : "rid"),
                            account_id = LookupData.Text(row, "uid") ?? LookupData.Text(row, "owner_uid") };
                        Metadata(candidate, row);
                        if (!LookupData.Digits(candidate.account_id) || candidate.account_id == "0") candidate.account_id = null;
                        if (!LookupData.Digits(candidate.room_id) || candidate.room_id == "0") candidate.room_id = null;
                        if (candidate.room_id == null && candidate.account_id == null) continue;
                        if (candidate.room_id != null) candidate.room_url = Platforms.Get(platform).BaseUrl + candidate.room_id;
                        if (platform == "bili" && candidate.account_id != null) candidate.profile_url = "https://space.bilibili.com/" + candidate.account_id;
                        if (candidate.display_name != null) candidate.display_name = Regex.Replace(WebUtility.HtmlDecode(candidate.display_name), "<[^>]+>", "");
                        candidate.sources.Add(new LookupSource { url = url, title = Platforms.Name(platform) + "官方主播搜索", retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "平台返回的账号/房间线索" } });
                        if (candidate.room_url != null) candidate.sources.Add(new LookupSource { url = candidate.room_url, title = "官方搜索返回的房间链接", retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "room_id" } });
                        candidate.match_reasons = new[] { "官方平台搜索结果，身份归属待核验" };
                        result.candidates.Add(candidate); if (result.candidates.Count >= 8) break;
                    }
                    return result;
                }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new LookupFailure("timeout", "平台搜索等待超时，可打开官网搜索。"); }
            }
        }
        private string BiliSearchUrl(string keyword, CancellationToken token)
        {
            var nav = LookupData.Obj(LookupData.Json(Get("https://api.bilibili.com/x/web-interface/nav", token).Body), "data");
            var images = LookupData.Obj(nav, "wbi_img");
            string image = LookupData.Text(images, "img_url"), sub = LookupData.Text(images, "sub_url");
            if (image == null || sub == null) throw new LookupFailure("blocked", "B站没有返回搜索签名密钥，请重试。");
            string mixin = BiliDanmakuClient.MixinKey(Path.GetFileNameWithoutExtension(new Uri(image).AbsolutePath) + Path.GetFileNameWithoutExtension(new Uri(sub).AbsolutePath));
            string cleaned = Regex.Replace(keyword, "[!'()*]", "");
            string canonical = "keyword=" + Uri.EscapeDataString(cleaned).Replace("%20", "+") + "&page=1&search_type=live_user&web_location=1550101&wts="
                + ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string digest;
            using (var md5 = System.Security.Cryptography.MD5.Create()) digest = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(canonical + mixin))).Replace("-", "").ToLowerInvariant();
            return "https://api.bilibili.com/x/web-interface/wbi/search/type?" + canonical + "&w_rid=" + digest;
        }
        internal LookupResult SearchIndex(string keyword, CancellationToken token)
        {
            // Anonymous public index request: Http scopes cookies to platform hosts, never Bing.
            string url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString("site:douyin.com " + keyword) + "&format=rss";
            LookupHttpReply reply = Transport(url, token); token.ThrowIfCancellationRequested();
            if (reply.Status != 200) throw new LookupFailure("blocked", "公开索引请求受阻，可重试或手动使用 AI。");
            var result = ParseIndex(reply.Body);
            result.note = "抖音公开索引候选（可能过时）；同名账号分别展示，请选中核验或使用分享链接。";
            return result;
        }
        internal static LookupResult ParseIndex(string xml)
        {
            var document = new System.Xml.XmlDocument { XmlResolver = null };
            try
            {
                using (var reader = System.Xml.XmlReader.Create(new StringReader(xml ?? ""), new System.Xml.XmlReaderSettings {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 })) document.Load(reader);
            }
            catch (System.Xml.XmlException) { throw new LookupFailure("blocked", "公开索引没有返回有效 RSS，不能当作无此主播。"); }
            var result = new LookupResult(); var seen = new HashSet<string>();
            foreach (System.Xml.XmlNode item in document.SelectNodes("/rss/channel/item"))
            {
                string link = item.SelectSingleNode("link") == null ? "" : item.SelectSingleNode("link").InnerText;
                string title = item.SelectSingleNode("title") == null ? "" : item.SelectSingleNode("title").InnerText;
                Uri uri; if (!Uri.TryCreate(link, UriKind.Absolute, out uri) || !LookupUrls.Basic(uri) || !seen.Add(link)) continue;
                var candidate = new LookupCandidate { platform = "douyin", provider = "public_index", source = "bing_rss", display_name = title };
                string platform;
                if (LookupUrls.Room(uri, out platform) && platform == "douyin")
                {
                    candidate.room_url = link; candidate.room_id = LookupData.Direct("douyin", link).Room;
                    candidate.display_name = Regex.Replace(title, @"的抖音直播间\s*[-－]\s*抖音直播.*$", "");
                }
                else if (uri.Host == "www.douyin.com" && Regex.IsMatch(uri.AbsolutePath, @"^/user/MS4wLj[A-Za-z0-9_-]+/?$"))
                {
                    candidate.profile_url = link; candidate.sec_uid = uri.AbsolutePath.TrimEnd('/').Substring(6);
                    candidate.display_name = Regex.Replace(title, @"的抖音\s*[-－]\s*抖音.*$", "");
                }
                else continue;
                candidate.sources.Add(new LookupSource { url = link, title = title, retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "公开索引标题及房间/账号网址（未核验）" } });
                candidate.match_reasons = new[] { "公开索引线索，可能过时；不凭同名合并账号与房间" };
                result.candidates.Add(candidate); if (result.candidates.Count >= 8) break;
            }
            result.candidates.Sort(delegate(LookupCandidate a, LookupCandidate b) { return (a.room_id == null ? 1 : 0).CompareTo(b.room_id == null ? 1 : 0); });
            return result;
        }
    }
}
