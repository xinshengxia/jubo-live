using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    internal sealed class LookupSource
    {
        public string index { get; set; }
        public string url { get; set; }
        public string title { get; set; }
        public string retrieved_at { get; set; }
        public string excerpt { get; set; }
        public string published_at { get; set; }
        public string[] supports { get; set; }
    }

    internal sealed class LookupCandidate
    {
        public string platform { get; set; }
        public string display_name { get; set; }
        public string category { get; set; }
        public string description { get; set; }
        public string avatar_url { get; set; }
        public string live_title { get; set; }
        public string relevance { get; set; }
        public string relevance_note { get; set; }
        public List<LookupSource> relevance_sources { get; set; }
        public string failure_code { get; set; }
        public string account_id { get; set; }
        public string sec_uid { get; set; }
        public string account_handle { get; set; }
        public string profile_url { get; set; }
        // Public web room number; douyin internal session number is never used here.
        public string room_id { get; set; }
        public string session_room_id { get; set; }
        public string room_url { get; set; }
        public string source { get; set; }
        public string provider { get; set; }
        public List<LookupSource> sources { get; set; }
        public string[] match_reasons { get; set; }
        public string platform_update { get; set; }
        public string[] lookup_terms { get; set; }
        public List<LookupSource> update_sources { get; set; }
        public string verification { get; set; }
        public string verification_note { get; set; }
        public string verified_at { get; set; }
        public bool account_verified { get; set; }
        public string resolution_path { get; set; }
        public string live_status { get; set; }
        // 平台返回的粉丝/关注数（纯数字文本）。null = 平台没有返回，绝不由别名或房间推测。
        public string follower_count { get; set; }
        public bool user_confirmed { get; set; }
        public bool direct_input { get; set; }
        public LookupCandidate()
        {
            relevance = "ambiguous"; relevance_sources = new List<LookupSource>(); sources = new List<LookupSource>(); update_sources = new List<LookupSource>(); lookup_terms = new string[0]; match_reasons = new string[0]; verification = "unverified";
        }
        public override string ToString()
        {
            return (display_name ?? account_handle ?? account_id ?? sec_uid ?? "待确认账号") + "  ·  "
                + Platforms.Name(platform) + "  ·  " + (room_id ?? "直播间待解析")
                + (string.IsNullOrEmpty(follower_count) ? "" : "  ·  粉丝 " + LookupDiscovery.FollowerText(follower_count))
                + "  ·  " + LookupData.State(verification);
        }
    }

    internal sealed class LookupResult
    {
        public List<LookupCandidate> candidates = new List<LookupCandidate>();
        public List<LookupSource> sources = new List<LookupSource>();
        public List<LookupReview> reviews = new List<LookupReview>();
        public List<LookupPlatformOutcome> platforms = new List<LookupPlatformOutcome>();
        public string searched_at;
        public string error_code;
        public string status;
        public string note;
        public bool cached;
        public string usage;
        public string planning_usage;
        public string[] research_queries = new string[0];
        public int model_candidates;
        public int rejected_candidates;
        public long first_source_ms;
        public long candidate_ms;
    }

    internal sealed class LookupFailure : Exception
    {
        internal readonly string Code;
        internal LookupFailure(string code, string message) : base(message) { Code = code; }
    }

    internal static class LookupData
    {
        internal static string Platform(string value) { return value == "bilibili" ? "bili" : value; }
        internal static string Text(IDictionary<string, object> obj, string key)
        {
            object value; return obj != null && obj.TryGetValue(key, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        // Model IDs must be strings. Never silently convert an already rounded JSON number.
        internal static string Id(IDictionary<string, object> obj, string key)
        {
            object value; if (obj == null || !obj.TryGetValue(key, out value) || value == null) return null;
            string text = value as string;
            if (text == null) throw new LookupFailure("format", "搜索结果中的账号或房间编号不是字符串，请重新搜索。");
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        internal static Dictionary<string, object> Obj(IDictionary<string, object> obj, string key)
        {
            object value; return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
        internal static IEnumerable Rows(IDictionary<string, object> obj, string key)
        {
            object value; return obj != null && obj.TryGetValue(key, out value) && !(value is string) ? value as IEnumerable : null;
        }
        internal static Dictionary<string, object> Json(string text)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 100 }.Deserialize<Dictionary<string, object>>(text); }
            catch (Exception) { throw new LookupFailure("format", "服务返回的内容格式无法解析。"); }
        }
        internal static bool Digits(string value) { return value != null && Regex.IsMatch(value, @"^[0-9]{1,24}$"); }
        // 粉丝量只接受平台直接返回的纯数字；0、缺失和非数字都不写入，展示层不得据此推断。
        internal static void Followers(LookupCandidate candidate, string value)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(value)) return;
            long parsed;
            if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed) || parsed <= 0) return;
            candidate.follower_count = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static string State(string value)
        {
            return value == "verified" ? "账号与房间已核验" : value == "account-only" ? "找到账号，房间待解析" : value == "blocked" ? "核验受阻" : value == "mismatch" ? "身份不符" : "待核验";
        }
        internal static LookupCandidate Copy(LookupCandidate value)
        {
            var json = new JavaScriptSerializer(); return json.Deserialize<LookupCandidate>(json.Serialize(value));
        }
        internal static string FavoriteKey(LookupCandidate value)
        {
            return value != null && value.user_confirmed ? VerifiedRoomKey(value) : null;
        }
        internal static string VerifiedRoomKey(LookupCandidate value)
        {
            if (value == null || value.verification != "verified" || !Digits(value.room_id) || !Platforms.Known(value.platform)) return null;
            return value.platform == "bili" ? value.room_id : value.platform + ":" + value.room_id;
        }
        internal static Target Direct(string platform, string text)
        {
            string value = (text ?? "").Trim();
            if (Regex.IsMatch(value, @"^(?:(?:bili|huya|douyu|douyin):)?[0-9]{1,24}$")) return Parse(value, platform);
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !LookupUrls.Room(uri, out platform)) return null;
            return Parse(uri.GetLeftPart(UriPartial.Path), platform);
        }
        internal static string Share(string text)
        {
            Match m = Regex.Match(text ?? "", @"(?<![A-Za-z0-9./_-])(?:https?://)?v\.(?:douyin|iesdouyin)\.com/[A-Za-z0-9_-]+/?", RegexOptions.IgnoreCase);
            return m.Success ? "https://" + Regex.Replace(m.Value, @"^https?://", "", RegexOptions.IgnoreCase) : null;
        }
        internal static string DouyinHandleInput(string text)
        {
            Match match = Regex.Match(LookupDiscovery.SearchTerms(text ?? ""), @"^(?:@|抖音号\s*[：:])\s*(.*?)\s*$");
            if (!match.Success) return null;
            string handle = match.Groups[1].Value;
            if (!Regex.IsMatch(handle, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$"))
                throw new LookupFailure("input", "抖音号格式无效；可输入 @抖音号，或改用主播昵称。" );
            return handle;
        }
    }

    // Generation guards are shared by UI and probes. Cancellation alone cannot stop a late callback.
    internal sealed class LookupGeneration
    {
        private int generation;
        internal int Next() { return Interlocked.Increment(ref generation); }
        internal bool Current(int value) { return value == Interlocked.CompareExchange(ref generation, 0, 0); }
    }

    internal sealed partial class LookupService
    {
        internal Func<string, string, bool, CancellationToken, Action<string>, LookupResult> Ai;
        internal Func<string, string, CancellationToken, LookupResult> PlatformSearch;
        internal Func<string, string, List<LookupCandidate>, CancellationToken, Action<string>, LookupResult> Optimize;
        internal Func<string, CancellationToken, LookupCandidate> Share;
        private readonly Dictionary<string, Tuple<DateTime, string>> cache = new Dictionary<string, Tuple<DateTime, string>>();
        internal LookupService()
        {
            Ai = DeepSeekLookup.Search; Optimize = DeepSeekLookup.Optimize; PlatformSearch = LookupVerifier.SearchPlatform; Share = LookupVerifier.ResolveShare;
        }
        internal LookupResult Search(string platform, string text, bool deep, bool forceAi, CancellationToken token, Action<string> progress)
        {
            return SearchCore(platform, text, deep, forceAi, token, progress, false);
        }
        internal LookupResult Refresh(string platform, string text, CancellationToken token, Action<string> progress)
        {
            return SearchCore(platform, text, false, false, token, progress, true);
        }
        private LookupResult SearchCore(string platform, string text, bool deep, bool forceAi, CancellationToken token, Action<string> progress, bool refresh)
        {
            token.ThrowIfCancellationRequested(); platform = LookupData.Platform(platform); text = (text ?? "").Trim();
            if (!LookupDiscovery.KnownScope(platform) || text.Length == 0 || text.Length > 600) throw new LookupFailure("input", "请输入 1—600 字的昵称、描述或分享文案。");
            if (platform == "all" && !forceAi)
            {
                if (Regex.IsMatch(text, @"^抖音号\s*[：:]")) return SearchCore("douyin", text, deep, false, token, progress, refresh);
                Target link = LookupData.Direct("bili", text);
                if (link != null && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^[0-9]+$"))
                    return SearchCore(link.Platform, text, deep, false, token, progress, refresh);
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^[0-9]+$"))
                    throw new LookupFailure("input", "仅有数字时请先选择平台，或粘贴完整直播链接。");
                if (LookupData.Share(text) != null) return SearchCore("douyin", text, deep, false, token, progress, refresh);
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^uid:[0-9]+$")) return SearchCore("bili", text, deep, false, token, progress, refresh);
                foreach (string actual in LookupDiscovery.PlatformKeys)
                {
                    var profileInput = new LookupCandidate { platform = actual, profile_url = text, display_name = "指定账号" };
                    if (LookupUrls.Candidate(profileInput)) return SearchCore(actual, text, deep, false, token, progress, refresh);
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^MS4wLj[A-Za-z0-9_-]{1,180}$")) return SearchCore("douyin", text, deep, false, token, progress, refresh);
                return SearchAll(text, token, progress, refresh);
            }
            string accountKeyword = platform == "douyin" ? LookupData.DouyinHandleInput(text) : null;
            if (!forceAi && accountKeyword == null)
            {
                Match uid = Regex.Match(text, @"^uid:([0-9]{1,24})$");
                var profile = new LookupCandidate { platform = platform, source = "direct_profile", provider = "platform", display_name = "指定账号" };
                if (platform == "bili" && uid.Success) profile.account_id = uid.Groups[1].Value;
                else if (text.StartsWith("https://", StringComparison.Ordinal)) profile.profile_url = text;
                else if (platform == "douyin" && Regex.IsMatch(text, @"^MS4wLj[A-Za-z0-9_-]{1,180}$")) profile.profile_url = "https://www.douyin.com/user/" + text;
                if ((profile.account_id != null || profile.profile_url != null) && LookupUrls.Candidate(profile))
                {
                    profile.sources.Add(new LookupSource { url = profile.profile_url ?? "https://space.bilibili.com/" + profile.account_id, title = "用户指定的官方账号页", retrieved_at = DateTime.UtcNow.ToString("o") });
                    var profileResult = new LookupResult { status = "direct", note = "已识别官方账号；请选中核验直播间归属。" }; profileResult.candidates.Add(profile); return profileResult;
                }
            }
            Target direct = forceAi || accountKeyword != null ? null : LookupData.Direct(platform, text);
            if (direct != null)
            {
                var result = new LookupResult { status = "direct", note = "输入已识别为确定房间；选中后核验平台信息。" };
                var candidate = new LookupCandidate { platform = direct.Platform, room_id = direct.Room, room_url = direct.Url,
                    display_name = "房间 " + direct.Room, direct_input = true, source = "direct_input", provider = "platform" };
                candidate.sources.Add(new LookupSource { url = direct.Url, title = "用户输入的直播间", retrieved_at = DateTime.UtcNow.ToString("o") });
                result.candidates.Add(candidate); return result;
            }
            string share = forceAi ? null : LookupData.Share(text);
            if (share != null)
            {
                progress("正在解析抖音分享链接…");
                try { var result = new LookupResult { status = "share" }; result.candidates.Add(Share(share, token)); return result; }
                catch (OperationCanceledException) { throw; }
                catch (LookupFailure ex) { return new LookupResult { status = "share-blocked", error_code = ex.Code, note = ex.Message + " " + LookupDiscovery.Recovery(ex.Code, false) }; }
            }
            string key = platform + "\n" + text + "\n" + (forceAi ? "ai" : "platform") + "\n" + deep;
            if (platform == "douyin" && !forceAi) key += "\n" + DouyinUserSearch.CacheStamp();
            if (refresh) lock (cache) cache.Remove(key);
            if (!forceAi && !refresh) lock (cache)
            {
                Tuple<DateTime, string> item;
                if (cache.TryGetValue(key, out item) && (DateTime.UtcNow - item.Item1).TotalMinutes < 10)
                {
                    LookupResult cached = new JavaScriptSerializer().Deserialize<LookupResult>(item.Item2);
                    cached.cached = true; cached.note = "显示搜索缓存（" + LookupDiscovery.LocalTime(cached.searched_at) + "）；点击刷新结果可重新检索，房间仍需核验。"; return cached;
                }
            }
            progress(forceAi ? "正在使用 DeepSeek 联网研究（可取消）…" : "正在查询平台与公开索引…");
            LookupResult found;
            try
            {
                found = forceAi ? Ai(platform, text, deep, token, progress) : PlatformSearch(platform, accountKeyword ?? LookupDiscovery.SearchTerms(text), token);
                token.ThrowIfCancellationRequested(); found.status = forceAi ? "ai" : "platform"; found.searched_at = DateTime.UtcNow.ToString("o");
                LookupDiscovery.Rank(found.candidates, accountKeyword ?? text, false);
                if (found.candidates.Count == 0)
                {
                    found.status = "empty";
                    if (string.IsNullOrWhiteSpace(found.note)) found.note = forceAi ? "AI 未找到有来源支持的新候选；可补充游戏、职业身份或别名后重试。"
                        : "本次没有返回可用候选；可修改线索，或手动选择 AI 重新搜索。";
                    return found;
                }
                if (!forceAi) lock (cache)
                {
                    if (cache.Count >= 64) cache.Clear();
                    cache[key] = Tuple.Create(DateTime.UtcNow, new JavaScriptSerializer().Serialize(found));
                }
                return found;
            }
            catch (OperationCanceledException) { throw; }
            catch (LookupFailure ex)
            {
                token.ThrowIfCancellationRequested(); return new LookupResult { status = "blocked", error_code = ex.Code, note = ex.Message + " " + LookupDiscovery.Recovery(ex.Code, forceAi) };
            }
        }
        internal LookupResult Improve(string platform, string text, List<LookupCandidate> current, CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            if (current == null || current.Count == 0) throw new LookupFailure("input", "请先查询得到候选，再优化现有结果。");
            progress("正在使用 DeepSeek 优化当前候选（可取消）…");
            LookupResult result = Optimize(platform, text, current, token, progress);
            token.ThrowIfCancellationRequested(); result.status = "ai-optimize";
            int added = result.candidates.Count;
            // AI supplements the current list. It cannot erase the platform's stable identity fields.
            var merged = new List<LookupCandidate>();
            foreach (LookupCandidate existing in current)
            {
                LookupCandidate copy = LookupData.Copy(existing); copy.user_confirmed = false; copy.verification = "unverified";
                copy.account_verified = false; copy.verified_at = null; copy.live_status = "unknown";
                copy.relevance = "ambiguous"; copy.relevance_note = null; copy.relevance_sources.Clear();
                copy.verification_note = "优化后仍需选中独立核验；平台原始字段保留。"; merged.Add(copy);
            }
            foreach (LookupCandidate addition in result.candidates)
            {
                LookupCandidate match = merged.Find(delegate(LookupCandidate c) {
                    if (c.platform != addition.platform || (c.account_id != null && addition.account_id != null && c.account_id != addition.account_id)
                        || (c.sec_uid != null && addition.sec_uid != null && c.sec_uid != addition.sec_uid)) return false;
                    return (c.sec_uid != null && c.sec_uid == addition.sec_uid) || (c.account_id != null && c.account_id == addition.account_id)
                        || (c.room_id != null && c.room_id == addition.room_id);
                });
                if (match == null) { merged.Add(addition); continue; }
                var reasons = new List<string>(match.match_reasons); reasons.AddRange(addition.match_reasons); match.match_reasons = reasons.ToArray();
                foreach (LookupSource source in addition.sources)
                    if (!match.sources.Exists(delegate(LookupSource s) { return s.url == source.url; })) match.sources.Add(source);
                // Keep useful identity hints from explicit AI review. They still
                // have to match the official account when resolving the room.
                if (string.IsNullOrWhiteSpace(match.account_handle)) match.account_handle = addition.account_handle;
                if (match.profile_url == null) match.profile_url = addition.profile_url;
                if (match.room_id == null) { match.room_id = addition.room_id; match.room_url = addition.room_url; }
                if (match.category == null) match.category = addition.category;
                if (match.description == null) match.description = addition.description;
                foreach (string term in addition.lookup_terms) if (Array.IndexOf(match.lookup_terms, term) < 0) { var aliases = new List<string>(match.lookup_terms); aliases.Add(term); match.lookup_terms = aliases.ToArray(); }
                if (addition.platform_update != null) { match.platform_update = addition.platform_update; match.update_sources = addition.update_sources; }
                match.source += "+ai_context_review";
            }
            LookupDiscovery.ApplyReviews(result, merged); LookupDiscovery.Rank(merged, text, false);
            result.candidates = merged; result.note = added == 0 && result.reviews.Count == 0 ? "本次 AI 未提供可采用的新线索，原平台候选已保留；可选择核验。"
                : "已保留平台账号，按有来源的匹配判断排序；冲突和歧义见候选详情，请重新选择核验。"; return result;
        }
        internal void ClearCache() { lock (cache) cache.Clear(); }
    }

    // Only the UI's selected, verified snapshot may reach this write boundary.
    private static bool SaveLookupFavorite(LookupCandidate candidate, string name, List<KeyValuePair<string, string>> favorites)
    {
        string key = LookupData.FavoriteKey(candidate);
        if (key == null || string.IsNullOrWhiteSpace(name)) throw new LookupFailure("unverified", "请先选中并核验数字房间号。");
        foreach (var old in favorites)
        {
            Target target = Parse(old.Value, "bili");
            if (target != null && target.Platform == candidate.platform && target.Room == candidate.room_id) return false;
        }
        var next = CreateFavoritesUpdate(favorites);
        next.Add(new KeyValuePair<string, string>(name.Trim(), key));
        if (File.Exists(Favorites)) File.Copy(Favorites, Favorites + ".before-lookup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak", false);
        SaveFavorites(next); favorites.Clear(); favorites.AddRange(next); return true;
    }
}
