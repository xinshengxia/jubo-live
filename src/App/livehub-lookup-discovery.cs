using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static partial class LiveHub
{
    internal sealed class LookupReview
    {
        public string platform { get; set; }
        public string account_id { get; set; }
        public string sec_uid { get; set; }
        public string room_id { get; set; }
        public string decision { get; set; }
        public string reason { get; set; }
        public List<LookupSource> sources { get; set; }
        public LookupReview() { sources = new List<LookupSource>(); }
    }
    internal sealed class LookupPlatformOutcome
    {
        public string platform;
        public string status;
        public string note;
        public string error_code;
        public bool cached;
        public string searched_at;
        public int count;
    }
    internal static class LookupDiscovery
    {
        internal static readonly string[] PlatformKeys = { "bili", "huya", "douyu", "douyin" };
        internal static bool KnownScope(string platform) { return platform == "all" || Platforms.Known(platform); }
        internal static string ScopeName(string platform) { return platform == "all" ? "全部平台" : Platforms.Name(platform); }
        internal static string LocalTime(string utc)
        {
            DateTime parsed; return DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未知";
        }
        internal static string Clean(string value)
        {
            return Regex.Replace(System.Net.WebUtility.HtmlDecode(value ?? ""), "<[^>]+>", "").Trim();
        }
        internal static string Normalize(string value)
        {
            return Regex.Replace(Clean(value).Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), @"[\s\p{P}\p{S}_]+", "");
        }
        // Prefixes are separated from a name; arbitrary substring matches never qualify.
        internal static bool AliasMatch(string name, string term)
        {
            string wanted = Normalize(term), full = Normalize(name);
            if (wanted.Length == 0) return false;
            if (wanted == full) return true;
            string[] parts = Regex.Split(Clean(name), @"[丶·•|｜:：/\\—–-]+");
            for (int i = 1; i < parts.Length; i++)
                if (Normalize(string.Join("", parts, i, parts.Length - i)) == wanted && !Regex.IsMatch(Normalize(parts[0]), "粉丝|剪辑|搬运|高仿|非本人|fan"))
                    return true;
            return false;
        }
        internal static bool SameAccount(LookupCandidate a, LookupCandidate b)
        {
            if (a.platform != b.platform || (a.account_id != null && b.account_id != null && a.account_id != b.account_id)
                || (a.sec_uid != null && b.sec_uid != null && a.sec_uid != b.sec_uid)) return false;
            return (a.sec_uid != null && a.sec_uid == b.sec_uid) || (a.account_id != null && a.account_id == b.account_id)
                || (a.room_id != null && a.room_id == b.room_id);
        }
        internal static void Rank(List<LookupCandidate> candidates, string query, bool grouped)
        {
            var order = new Dictionary<LookupCandidate, int>();
            for (int i = 0; i < candidates.Count; i++) order[candidates[i]] = i;
            candidates.Sort(delegate(LookupCandidate a, LookupCandidate b) {
                int comparison = grouped ? Array.IndexOf(PlatformKeys, a.platform).CompareTo(Array.IndexOf(PlatformKeys, b.platform)) : 0;
                if (comparison == 0) comparison = Tier(a, query).CompareTo(Tier(b, query));
                return comparison == 0 ? order[a].CompareTo(order[b]) : comparison;
            });
        }
        private static int Tier(LookupCandidate candidate, string query)
        {
            if (candidate.relevance == "conflict") return 5;
            if (candidate.relevance == "supported") return 0;
            string game = Hint(query, "游戏/领域"), identity = Hint(query, "战队/身份");
            if ((!string.IsNullOrWhiteSpace(game) && Normalize(game).Length > 0 && Normalize(candidate.category).Contains(Normalize(game)))
                || (!string.IsNullOrWhiteSpace(identity) && Normalize(identity).Length > 0 && Normalize(candidate.description).Contains(Normalize(identity)))) return 1;
            string name = SearchTerms(query);
            if (!string.IsNullOrWhiteSpace(candidate.account_handle) && Normalize(candidate.account_handle) == Normalize(name)) return 2;
            if (Normalize(candidate.display_name) == Normalize(name)) return 2;
            if (AliasMatch(candidate.display_name, name)) return 3;
            return 4;
        }
        internal static string SearchTerms(string text)
        {
            Match hint = Regex.Match(text ?? "", @"(?:^|\s)(?:游戏/领域|战队/身份|外号/曾用名)：");
            if (!hint.Success) return text;
            string name = text.Substring(0, hint.Index).Trim();
            if (name.Length > 0) return name;
            string alias = Hint(text, "外号/曾用名"); return string.IsNullOrWhiteSpace(alias) ? text : alias;
        }
        internal static string Hint(string text, string label)
        {
            Match match = Regex.Match(text ?? "", @"(?:^|\s)" + Regex.Escape(label) + @"：(.+?)(?=\s(?:游戏/领域|战队/身份|外号/曾用名)：|$)");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        internal static string RelevanceName(string value)
        {
            return value == "supported" ? "线索更相符" : value == "conflict" ? "线索有冲突" : "身份待辨认";
        }
        internal static string Summary(LookupCandidate candidate)
        {
            string facts = !string.IsNullOrWhiteSpace(candidate.relevance_note) ? candidate.relevance_note
                : candidate.match_reasons != null && candidate.match_reasons.Length > 0 ? candidate.match_reasons[0] : "请选择查看账号与来源";
            return (string.IsNullOrWhiteSpace(candidate.category) ? "" : candidate.category + " · ")
                + (candidate.room_id == null ? "房间待解析" : "房间 " + candidate.room_id) + " · " + Clean(facts);
        }
        internal static string Recovery(string code, bool ai)
        {
            if (code == "unconfigured" || code == "config") return "请打开 DeepSeek 设置检查模型和密钥；也可使用平台查询。";
            if (code == "auth") return "请检查登录或 AI 凭据，再重试；也可打开平台网页。";
            if (code == "rate-limit") return "请稍后刷新结果，或打开平台网页查看；无需连续重复请求。";
            if (code == "not-found") return "可核对链接、查看账号主页或打开官网搜索。";
            if (code == "timeout" || code == "network") return "可刷新结果重试，或打开平台网页；已有账号线索可继续解析。";
            if (code == "no-room") return "可打开账号主页，稍后重新解析房间，或补充直播分享链接。";
            if (code == "mismatch") return "请选择其他候选或补充线索；冲突房间不会加入收藏。";
            if (code == "sources" || code == "format") return "本次材料未通过完整性检查；可补充游戏、战队或曾用名后重试。";
            return ai ? "可补充游戏、战队或曾用名后重试，或打开平台网页。" : "可刷新结果、打开平台网页，或手动使用 AI 搜索。";
        }
        internal static string AccountPage(LookupCandidate candidate)
        {
            if (candidate == null) return null;
            var copy = LookupData.Copy(candidate);
            if (copy.profile_url != null && LookupUrls.Candidate(copy)) return copy.profile_url;
            return candidate.platform == "bili" && LookupData.Digits(candidate.account_id) ? "https://space.bilibili.com/" + candidate.account_id : null;
        }
        internal static void ApplyReviews(LookupResult result, List<LookupCandidate> candidates)
        {
            foreach (LookupReview review in result.reviews)
            {
                if (review.sources.Count == 0 || string.IsNullOrWhiteSpace(review.reason)) continue;
                var identity = new LookupCandidate { platform = review.platform, account_id = review.account_id, sec_uid = review.sec_uid, room_id = review.room_id };
                foreach (LookupCandidate candidate in candidates)
                {
                    if (!SameAccount(candidate, identity)) continue;
                    // Multiple contradictory reviews reduce certainty, never silently pick one.
                    if (candidate.relevance_sources.Count > 0 && candidate.relevance != review.decision)
                    { candidate.relevance = "ambiguous"; candidate.relevance_note = "本次资料判断存在矛盾，请查看来源并补充线索。"; }
                    else { candidate.relevance = review.decision; candidate.relevance_note = review.reason; }
                    foreach (LookupSource source in review.sources)
                    {
                        if (!candidate.relevance_sources.Exists(delegate(LookupSource s) { return s.url == source.url; })) candidate.relevance_sources.Add(source);
                        if (!candidate.sources.Exists(delegate(LookupSource s) { return s.url == source.url; })) candidate.sources.Add(source);
                    }
                }
            }
        }
    }

    internal sealed class LookupCandidateList : ListBox, IDpiScaled
    {
        internal bool Grouped;
        internal LookupCandidate Recommendation;
        internal readonly LookupAvatarCache Avatars = new LookupAvatarCache();
        public LookupCandidateList()
        {
            DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
            DpiLayout.SetDesignFont(this, ModernTheme.UiFont); FollowTheme(); OnDpiScaled(DpiLayout.Scale);
            ModernTheme.Changed += FollowTheme;
        }
        public void OnDpiScaled(float scale) { ItemHeight = (int)Math.Round(72 * scale); }
        private void FollowTheme() { BackColor = ModernTheme.Card; ForeColor = ModernTheme.Text; if (IsHandleCreated) ModernTheme.ApplyScrollbarTheme(this); Invalidate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ModernTheme.ApplyScrollbarTheme(this); }
        protected override void Dispose(bool disposing) { if (disposing) { ModernTheme.Changed -= FollowTheme; Avatars.Dispose(); } base.Dispose(disposing); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var candidate = Items[e.Index] as LookupCandidate; if (candidate == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? ModernTheme.Selection : ModernTheme.Card)) e.Graphics.FillRectangle(brush, e.Bounds);
            int pad = DpiLayout.S(this, 8), line = DpiLayout.S(this, 23);
            var avatar = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + DpiLayout.S(this, 12), DpiLayout.S(this, 40), DpiLayout.S(this, 40));
            if (!Avatars.Draw(e.Graphics, candidate.avatar_url, avatar))
            {
                using (var brush = new SolidBrush(ModernTheme.Selection)) e.Graphics.FillEllipse(brush, avatar);
                string name = string.IsNullOrWhiteSpace(candidate.display_name) ? (candidate.account_handle ?? "主") : candidate.display_name;
                string letter = string.IsNullOrEmpty(name) ? "主" : name.Substring(0, 1);
                TextRenderer.DrawText(e.Graphics, letter, Font, avatar, ModernTheme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            var first = new Rectangle(avatar.Right + pad, e.Bounds.Y + DpiLayout.S(this, 4), Math.Max(0, e.Bounds.Width - DpiLayout.S(this, 40) - pad * 3), line);
            var second = new Rectangle(first.X, first.Bottom, first.Width, line);
            var third = new Rectangle(first.X, second.Bottom, first.Width, DpiLayout.S(this, 19));
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            string title = (candidate.display_name ?? candidate.account_handle ?? candidate.account_id ?? "待确认账号") + " 〔" + Platforms.Name(candidate.platform) + "〕";
            if (ReferenceEquals(candidate, Recommendation)) title = "AI 优先 · " + title;
            if (Grouped && (e.Index == 0 || ((LookupCandidate)Items[e.Index - 1]).platform != candidate.platform)) title = "▸ " + title;
            TextRenderer.DrawText(e.Graphics, title, Font, first, selected ? ModernTheme.Accent : ModernTheme.Text, flags);
            TextRenderer.DrawText(e.Graphics, LookupDiscovery.Summary(candidate), Font, second, ModernTheme.TextSecondary, flags);
            TextRenderer.DrawText(e.Graphics, LookupDiscovery.RelevanceName(candidate.relevance) + " · " + LookupData.State(candidate.verification), Font, third,
                candidate.relevance == "conflict" ? Color.IndianRed : ModernTheme.TextSecondary, flags);
            using (var pen = new Pen(ModernTheme.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left + pad, e.Bounds.Bottom - 1, e.Bounds.Right - pad, e.Bounds.Bottom - 1);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
}
