using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;

internal enum LiveEventKind { Gift, Guard, Enter, Follow, Notice, Status }
internal sealed class LiveEvent
{
    public LiveEventKind Kind;
    public string Platform = "bili", EventId, ComboId, GiftId, GiftName, UserName, Text, Topic;
    public long UserId;
    public int Count = 1;
    public bool Cumulative;
    internal LiveEvent Copy() { return (LiveEvent)MemberwiseClone(); }
}
internal interface ILiveEventSource { event Action<LiveEvent> LiveEventReceived; }

internal sealed partial class BiliDanmakuClient
{
    public event Action<LiveEvent> LiveEventReceived;
    private readonly Dictionary<string, int> _unknownCommands = new Dictionary<string, int>();
    private void HandleLiveEvent(string cmd, Dictionary<string, object> root)
    {
        List<LiveEvent> events = LiveEventParser.ParseBili(cmd, root);
        if (events.Count == 0)
        {
            if (Regex.IsMatch(cmd, "^[A-Z0-9_]{1,64}$") && (_unknownCommands.Count < 128 || _unknownCommands.ContainsKey(cmd)))
            {
                int count; _unknownCommands.TryGetValue(cmd, out count); _unknownCommands[cmd] = count + 1;
                if (count == 0) DanmakuHost.LogQuiet("B站事件：未显示或未支持 cmd=" + cmd);
            }
            return;
        }
        Action<LiveEvent> handler = LiveEventReceived;
        if (handler != null) foreach (LiveEvent item in events) handler(item);
    }
}

// 字段参考 blivedm 的消息模型与 protobuf 声明；协议数据对照，未引入 Python 运行时。
// 原有 MIT 原文见 licenses/blivedm-LICENSE.txt，研究范围见 THIRD_PARTY_NOTICES.md。
internal static class LiveEventParser
{
    private static string S(Dictionary<string, object> data, string key) { return BiliDanmakuClient.Str(data, key); }
    private static int N(Dictionary<string, object> data, string key, int fallback) { return BiliDanmakuClient.Int(data, key, fallback); }
    private static string First(string a, string b) { return string.IsNullOrEmpty(a) ? b : a; }
    internal static List<LiveEvent> ParseBili(string cmd, Dictionary<string, object> root)
    {
        List<LiveEvent> result = new List<LiveEvent>();
        Dictionary<string, object> data = BiliDanmakuClient.Dict(BiliDanmakuClient.Val(root, "data"));
        if (data == null && cmd == "NOTICE_MSG") data = root;
        if (data == null) return result;
        if (cmd == "INTERACT_WORD_V2" || cmd == "SEND_GIFT_V2") return ParseProtobuf(cmd, S(data, "pb"));
        LiveEvent item = new LiveEvent(); item.UserId = BiliDanmakuClient.Long(data, "uid", 0);
        if (cmd == "WATCHED_CHANGE" || cmd == "ONLINE_RANK_COUNT" || cmd == "LIKE_INFO_V3_UPDATE")
        {
            item.Kind = LiveEventKind.Status;
            item.Topic = cmd == "WATCHED_CHANGE" ? "watched" : cmd == "ONLINE_RANK_COUNT" ? "rank-count" : "likes";
            string value = cmd == "WATCHED_CHANGE" ? First(S(data, "text_large"), S(data, "num"))
                : cmd == "ONLINE_RANK_COUNT" ? S(data, "count") : First(S(data, "click_count"), S(data, "total_like"));
            if (string.IsNullOrEmpty(value)) return result;
            item.Text = (item.Topic == "watched" ? "看过 " : item.Topic == "likes" ? "点赞 " : "高能榜 ") + value;
            result.Add(item); return result;
        }
        item.UserName = First(S(data, "uname"), S(data, "username"));
        item.Count = Math.Max(1, Math.Min(999999, N(data, "num", 1)));
        item.EventId = First(S(data, "tid"), S(data, "id")); item.ComboId = First(S(data, "combo_id"), S(data, "batch_combo_id"));
        if (cmd == "SEND_GIFT" || cmd == "COMBO_SEND")
        {
            item.Kind = LiveEventKind.Gift; item.GiftName = First(S(data, "giftName"), S(data, "gift_name"));
            item.GiftId = First(S(data, "giftId"), S(data, "gift_id"));
            item.Cumulative = cmd == "COMBO_SEND";
            if (item.Cumulative) { item.Count = Math.Max(1, Math.Min(999999, N(data, "total_num", 1))); item.EventId = null; }
            if (string.IsNullOrEmpty(item.GiftName)) item.GiftName = "礼物";
        }
        else if (cmd == "GUARD_BUY" || cmd == "USER_TOAST_MSG" || cmd == "USER_TOAST_MSG_V2")
        {
            if (N(data, "source", 0) == 2) return result;
            item.Kind = LiveEventKind.Guard;
            int level = N(data, "guard_level", 3); item.Text = level == 1 ? "开通总督" : level == 2 ? "开通提督" : "开通舰长";
            if (string.IsNullOrEmpty(item.UserName))
            {
                Dictionary<string, object> sender = BiliDanmakuClient.Dict(BiliDanmakuClient.Val(data, "sender_uinfo"));
                Dictionary<string, object> basic = sender == null ? null : BiliDanmakuClient.Dict(BiliDanmakuClient.Val(sender, "base"));
                if (basic != null) item.UserName = S(basic, "name");
            }
        }
        else if (cmd == "INTERACT_WORD")
        {
            int kind = N(data, "msg_type", 0); if (kind != 1 && kind != 2) return result;
            item.Kind = kind == 1 ? LiveEventKind.Enter : LiveEventKind.Follow;
        }
        else if (cmd == "WELCOME" || cmd == "WELCOME_GUARD" || cmd == "ENTRY_EFFECT") item.Kind = LiveEventKind.Enter;
        else if (cmd == "NOTICE_MSG")
        { item.Kind = LiveEventKind.Notice; item.Text = First(S(data, "msg_common"), S(data, "msg_self")); }
        else if (cmd == "POPULARITY_RED_POCKET_START" || cmd == "POPULARITY_RED_POCKET_NEW")
        { item.Kind = LiveEventKind.Notice; item.Text = "直播间红包活动开始"; item.EventId = S(data, "lot_id"); }
        else return result;
        result.Add(item); return result;
    }
    private static List<LiveEvent> ParseProtobuf(string cmd, string encoded)
    {
        List<LiveEvent> result = new List<LiveEvent>();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 256 * 1024) return result;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { return result; }
        ProtoReader reader = new ProtoReader(bytes); int field, wire, kind = 0; long uid = 0; string user = null;
        List<byte[]> gifts = new List<byte[]>();
        while (reader.TryReadTag(out field, out wire))
        {
            if (field == 1 && wire == 0) uid = reader.ReadVarint();
            else if (field == 2 && wire == 2) user = reader.ReadString();
            else if (cmd == "INTERACT_WORD_V2" && field == 5 && wire == 0) kind = (int)reader.ReadVarint();
            else if (cmd == "SEND_GIFT_V2" && field == 10 && wire == 2) { byte[] gift = reader.ReadBytes(); if (gifts.Count < 64) gifts.Add(gift); }
            else reader.Skip(wire);
        }
        if (cmd == "INTERACT_WORD_V2")
        {
            if (kind == 1 || kind == 2) { LiveEvent item = new LiveEvent(); item.Kind = kind == 1 ? LiveEventKind.Enter : LiveEventKind.Follow; item.UserId = uid; item.UserName = user; result.Add(item); }
            return result;
        }
        foreach (byte[] gift in gifts)
        {
            LiveEvent item = new LiveEvent(); item.Kind = LiveEventKind.Gift; item.UserId = uid; item.UserName = user;
            ProtoReader payload = new ProtoReader(gift);
            while (payload.TryReadTag(out field, out wire))
            {
                if (field == 1 && wire == 0) item.GiftId = payload.ReadVarint().ToString();
                else if (field == 2 && wire == 2) item.GiftName = payload.ReadString();
                else if (field == 3 && wire == 0) item.Count = (int)Math.Max(1, Math.Min(999999L, payload.ReadVarint()));
                else if (field == 9 && wire == 2) item.EventId = payload.ReadString();
                else payload.Skip(wire);
            }
            if (!string.IsNullOrEmpty(item.GiftName)) result.Add(item);
        }
        return result;
    }
}

internal sealed partial class DanmakuRenderer
{
    private sealed class ActiveEvent
    {
        internal LiveEvent Item;
        internal string Key;
        internal int Delta, Reported;
        internal DateTime Created, Expires;
        internal int Count { get { return Math.Max(Delta, Reported); } }
    }
    private readonly List<ActiveEvent> _events = new List<ActiveEvent>();
    private sealed class LiveStatus { internal string Text; internal DateTime Seen; }
    private readonly Dictionary<string, LiveStatus> _liveStatus = new Dictionary<string, LiveStatus>();
    private readonly Dictionary<string, DateTime> _eventIds = new Dictionary<string, DateTime>();
    private RectangleF _eventReserved;
    private DateTime _eventWindow = DateTime.MinValue;
    private int _eventWindowCount;
    internal long EventReceived, EventDisplayed, EventMerged, EventDropped, EventFiltered;
    private bool EventEnabled(LiveEvent item)
    { return item.Kind == LiveEventKind.Gift ? _config.ShowGift : item.Kind == LiveEventKind.Guard ? _config.ShowGuardEvents
        : item.Kind == LiveEventKind.Status ? _config.ShowLiveStats : item.Kind == LiveEventKind.Notice ? _config.ShowNotice : _config.ShowEnterMessage; }
    private static string Bounded(string value, int limit) { return value == null || value.Length <= limit ? value : value.Substring(0, limit); }
    public void AddLiveEvent(LiveEvent item)
    {
        if (item == null) return; EventReceived++;
        if (!EventEnabled(item)) { EventFiltered++; return; }
        item = item.Copy(); item.UserName = Bounded(item.UserName, 128); item.Text = Bounded(item.Text, 512);
        item.Platform = Bounded(item.Platform, 24);
        item.Text = System.Net.WebUtility.HtmlDecode(item.Text ?? "");
        item.Text = Regex.Replace(item.Text, "</?[A-Za-z][^>]{0,256}>", "").Replace('\r', ' ').Replace('\n', ' ');
        item.EventId = Bounded(item.EventId, 128); item.ComboId = Bounded(item.ComboId, 128);
        item.GiftName = Bounded(item.GiftName, 128); item.GiftId = Bounded(item.GiftId, 64); item.Count = Math.Max(1, Math.Min(999999, item.Count));
        if (_rules.User(EventUser(item), _config.BlockUsers) || _rules.Any(item.Text, _config.BlockKeywords)) { EventFiltered++; return; }
        PruneEvents(Now);
        if (item.Kind == LiveEventKind.Status)
        {
            if (item.Topic != "watched" && item.Topic != "likes" && item.Topic != "rank-count") { EventDropped++; return; }
            LiveStatus status;
            if (!_liveStatus.TryGetValue(item.Topic, out status)) { status = new LiveStatus(); _liveStatus[item.Topic] = status; EventDisplayed++; _layoutDirty = true; }
            else EventMerged++;
            status.Text = item.Text; status.Seen = Now;
            return;
        }
        string key = item.Platform + ":" + item.Kind + ":" + item.UserId + ":" + item.UserName + ":"
            + (item.Kind == LiveEventKind.Notice ? item.Text : item.GiftId ?? item.GiftName);
        if (!string.IsNullOrEmpty(item.ComboId)) key += ":" + item.ComboId;
        if (!string.IsNullOrEmpty(item.EventId))
        {
            string id = item.Platform + ":" + item.Kind + ":" + item.EventId; DateTime seen;
            if (_eventIds.TryGetValue(id, out seen) && (Now - seen).TotalSeconds < 15) { EventMerged++; return; }
            if (_eventIds.Count >= 512) _eventIds.Clear(); _eventIds[id] = Now;
        }
        foreach (ActiveEvent active in _events)
        {
            if (active.Key != key || active.Expires <= Now) continue;
            if (item.Kind == LiveEventKind.Gift)
            {
                if (item.Cumulative) active.Reported = Math.Max(active.Reported, item.Count);
                else active.Delta = Math.Min(999999, active.Delta + item.Count);
                active.Expires = Earlier(Now.AddSeconds(_config.EventDurationSeconds), active.Created.AddSeconds(10));
            }
            EventMerged++; return;
        }
        if ((Now - _eventWindow).TotalSeconds >= 1) { _eventWindow = Now; _eventWindowCount = 0; }
        bool priority = item.Kind == LiveEventKind.Guard || item.Kind == LiveEventKind.Notice;
        if (_eventWindowCount >= _config.EventRatePerSecond + (priority ? 1 : 0)) { EventDropped++; return; }
        _eventWindowCount++;
        if (_events.Count >= _config.EventMaxVisible)
        {
            if (!priority) { EventDropped++; return; }
            _events.RemoveAt(0); EventDropped++;
        }
        ActiveEvent added = new ActiveEvent(); added.Item = item; added.Key = key; added.Created = Now;
        added.Expires = Now.AddSeconds(_config.EventDurationSeconds); added.Delta = item.Cumulative ? 0 : Math.Max(1, item.Count); added.Reported = item.Cumulative ? Math.Max(1, item.Count) : 0;
        _events.Add(added); EventDisplayed++; _layoutDirty = true;
    }
    private static DateTime Earlier(DateTime a, DateTime b) { return a < b ? a : b; }
    private static DanmakuItem EventUser(LiveEvent item) { DanmakuItem user = new DanmakuItem(); user.UserId = item.UserId; user.UserName = item.UserName; return user; }
    private RectangleF MeasureEventReservation()
    {
        if ((_events.Count == 0 && _liveStatus.Count == 0) || _width == 0) return RectangleF.Empty;
        float width = Math.Min(_width - 20, 350f * _uiScale), height = (_events.Count * 30f + (_liveStatus.Count > 0 ? 24f : 0f)) * _uiScale;
        if (width <= 0 || height > _areaHeight) return RectangleF.Empty;
        for (float y = 10f; y + height <= _areaHeight; y += Math.Max(4f, 8f * _uiScale))
        {
            RectangleF candidate = new RectangleF(10f, y, width, height); bool blocked = candidate.IntersectsWith(_scReserved);
            foreach (string area in _config.ReservedAreas ?? new string[0])
            {
                RectangleF r; if (!DanmakuRegions.TryParse(area, out r)) continue;
                RectangleF mapped = new RectangleF(r.X * _width / 100f, r.Y * _height / 100f, r.Width * _width / 100f, r.Height * _height / 100f);
                if (mapped.IntersectsWith(candidate)) { blocked = true; break; }
            }
            if (!blocked) return candidate;
        }
        return RectangleF.Empty;
    }
    private void PruneEvents(DateTime now)
    {
        for (int i = _events.Count - 1; i >= 0; i--)
            if (_events[i].Expires <= now || !EventEnabled(_events[i].Item)) _events.RemoveAt(i);
        List<string> expired = new List<string>();
        foreach (KeyValuePair<string, LiveStatus> item in _liveStatus)
            if (!_config.ShowLiveStats || (now - item.Value.Seen).TotalSeconds >= 30) expired.Add(item.Key);
        foreach (string key in expired) _liveStatus.Remove(key);
    }
    private static string EventLabel(ActiveEvent active)
    {
        LiveEvent item = active.Item; string user = string.IsNullOrEmpty(item.UserName) ? "观众" : item.UserName;
        string text = item.Kind == LiveEventKind.Gift ? user + " 赠送 " + (item.GiftName ?? "礼物") + " ×" + active.Count
            : item.Kind == LiveEventKind.Enter ? user + " 进入直播间" : item.Kind == LiveEventKind.Follow ? user + " 关注了主播"
            : item.Kind == LiveEventKind.Guard ? user + " " + item.Text : "公告：" + item.Text;
        return text.Length > 256 ? text.Substring(0, 256) : text;
    }
    private void DrawLiveEvents(Graphics graphics)
    {
        if (_eventReserved.IsEmpty) return;
        using (Font font = new Font(_config.FontFamily, 13f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush background = new SolidBrush(SurfaceColor(Color.FromArgb(180, 22, 35, 52))))
        using (SolidBrush text = new SolidBrush(Color.White))
        using (StringFormat format = new StringFormat())
        {
            format.Trimming = StringTrimming.EllipsisCharacter; format.FormatFlags = StringFormatFlags.NoWrap; format.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < _events.Count; i++)
            {
                RectangleF rect = new RectangleF(_eventReserved.X, _eventReserved.Y + i * 30f * _uiScale, _eventReserved.Width, 28f * _uiScale);
                graphics.FillRectangle(background, rect); rect.Inflate(-6f * _uiScale, 0f);
                graphics.DrawString(EventLabel(_events[i]), font, text, rect, format);
            }
            if (_liveStatus.Count > 0)
            {
                List<string> values = new List<string>();
                foreach (string key in new string[] { "watched", "likes", "rank-count" }) { LiveStatus value; if (_liveStatus.TryGetValue(key, out value)) values.Add(value.Text); }
                RectangleF rect = new RectangleF(_eventReserved.X, _eventReserved.Y + _events.Count * 30f * _uiScale, _eventReserved.Width, 22f * _uiScale);
                graphics.FillRectangle(background, rect); rect.Inflate(-6f * _uiScale, 0);
                graphics.DrawString(string.Join(" · ", values.ToArray()), font, text, rect, format);
            }
        }
    }
}
