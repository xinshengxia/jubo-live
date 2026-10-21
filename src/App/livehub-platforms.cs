// --------------------------------------------------------------------------------------------
// 平台适配器：把「四家不一样的那几件事」收在一处（评估报告 §7 的建议）。
//
// 原来这些判断散在启动器、中转、路径解析里，同一个知识写在好几个地方：
//   · 显示名：PlatformName 里的三元表达式；
//   · 房间号规则 + 站点根地址：Parse 里两处 if/三元；
//   · 「要不要走本地中转」：ResolveMpvPlan / BuildPotPlayerTitle 里各写一遍 plan.NeedsRelay = true；
//   · 「在播吗」：CheckRoomState 里四段 if (platform == ...)；
//   · 直链解析与 20 秒复用窗口：ResolveRelayUrlFresh / RelayUrlReuseWindow 里再各写一遍。
// 加第五个平台时得改六个地方，漏一个就是「有的地方认、有的地方不认」。
//
// 现在：一张表（All）描述四家，调用方问 Platforms.Get(id) —— 不认识就明确返回 null，
// 不认识的平台**不会**被猜成 B站。2026-10-16 起房间状态由旧 bool? 扩成四态。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal enum RoomStateKind { Unknown, Live, Offline, Replay }

internal static partial class LiveHub
{
    internal static bool? LegacyRoomState(RoomStateKind state)
    {
        return state == RoomStateKind.Live ? true
            : state == RoomStateKind.Offline ? (bool?)false : null;
    }

    internal static bool CanAttemptPlayback(RoomStateKind state)
    {
        return state != RoomStateKind.Offline;
    }

    internal static string RoomStateLabel(string platform, RoomStateKind state)
    {
        if (state == RoomStateKind.Live) return "直播中";
        if (state == RoomStateKind.Offline) return "已下播";
        if (state != RoomStateKind.Replay) return "待查询";
        return platform == "bili" ? "轮播" : platform == "douyu" ? "录播" : "重播";
    }

    internal static string RoomStateBadge(string platform, RoomStateKind state)
    {
        if (state == RoomStateKind.Live) return "[播] ";
        if (state == RoomStateKind.Offline) return "[休] ";
        if (state != RoomStateKind.Replay) return "";
        return "[" + RoomStateLabel(platform, state) + "] ";
    }

    internal static string RoomStateFileText(string platform, RoomStateKind state)
    {
        if (state == RoomStateKind.Live) return "LIVE";
        if (state == RoomStateKind.Offline) return "OFFLINE";
        if (state == RoomStateKind.Replay)
            return platform == "bili" ? "LOOP" : platform == "douyu" ? "RECORDING" : "REPLAY";
        // 查询失败必须覆盖旧值；时间戳说明这是一次新的 Unknown，而不是陈旧 LIVE。
        return "UNKNOWN|" + DateTime.UtcNow.Ticks;
    }

    internal static string ReplayUnavailableMessage(string reason)
    {
        return "重播流取不到（" + (string.IsNullOrEmpty(reason) ? "未解析到可用直链" : reason) + "）";
    }

    /// <summary>一个平台适配器：四家各实现一份，调用方只认这个接口。</summary>
    internal interface IPlatformAdapter
    {
        string Id { get; }                     // bili / huya / douyu / douyin
        string DisplayName { get; }             // B站 / 虎牙 / 斗鱼 / 抖音
        string BaseUrl { get; }                 // 站点根地址（拼直播页 URL 用）
        string RoomPattern { get; }             // 房间号合法性正则
        bool NeedsRelay { get; }                // 播放要不要经本地中转
        double RelayUrlReuseSeconds { get; }    // 中转直链复用窗口（秒）；0 = 每条连接都重解析
        bool CanTellOffline { get; }            // 能不能明确区分「没在播」和「抓取失败」

        /// <summary>解析出可直接喂播放器的上游地址。offline=true 表示上游明确没在播。</summary>
        string ResolveStreamUrl(Target target, out bool offline);
        string ResolveStreamUrl(Target target, out bool offline, out string failureReason);

        /// <summary>四态房间状态；Replay 由平台显示为重播 / 轮播 / 录播。</summary>
        RoomStateKind GetRoomStateKind(string room, out string note);

        /// <summary>旧三态兼容包装：重播不能冒充下播，故映射为 null。</summary>
        bool? GetRoomState(string room, out string note);

        /// <summary>直播页/接口里的主播与房间名；拿不到返回 null（调用方自己兜底）。</summary>
        string GetTitle(Target target);
    }

    /// <summary>四家共用的默认值，各适配器只覆盖不一样的。</summary>
    internal abstract class PlatformAdapterBase : IPlatformAdapter
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public abstract string BaseUrl { get; }
        public virtual string RoomPattern { get { return @"^[A-Za-z0-9_-]+$"; } }
        public virtual bool NeedsRelay { get { return true; } }
        public virtual double RelayUrlReuseSeconds { get { return LiveHub.RelayUrlReuseSeconds; } }
        public virtual bool CanTellOffline { get { return false; } }
        public virtual string ResolveStreamUrl(Target target, out bool offline, out string failureReason)
        { offline = false; failureReason = null; return null; }
        public string ResolveStreamUrl(Target target, out bool offline)
        { string failureReason; return ResolveStreamUrl(target, out offline, out failureReason); }
        public virtual RoomStateKind GetRoomStateKind(string room, out string note)
        { note = null; return RoomStateKind.Unknown; }
        public bool? GetRoomState(string room, out string note)
        { return LegacyRoomState(GetRoomStateKind(room, out note)); }
        public virtual string GetTitle(Target target) { return null; }
    }

    /// <summary>四家的表。加平台 = 加一行 + 一个适配器类，调用方不用动。</summary>
    internal static class Platforms
    {

        internal static readonly IPlatformAdapter[] All = new IPlatformAdapter[]
        {
            new BiliAdapter(),
            new HuyaAdapter(),
            new DouyuAdapter(),
            new DouyinAdapter()
        };

        /// <summary>按 id 取适配器；不认识返回 null（**不猜**成默认平台）。</summary>
        internal static IPlatformAdapter Get(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < All.Length; i++)
                if (All[i].Id == id) return All[i];
            return null;
        }

        internal static bool Known(string id) { return Get(id) != null; }

        /// <summary>显示名；不认识的 id 原样返回（宁可显示原文，也不要错误地标成 B站）。</summary>
        internal static string Name(string id)
        {
            IPlatformAdapter adapter = Get(id);
            return adapter == null ? (id == null ? "" : id) : adapter.DisplayName;
        }
    }

    internal sealed class BiliAdapter : PlatformAdapterBase
    {
        public override string Id { get { return "bili"; } }
        public override string DisplayName { get { return "B站"; } }
        public override string BaseUrl { get { return "https://live.bilibili.com/"; } }
        public override string RoomPattern { get { return @"^\d+$"; } }
        public override bool NeedsRelay { get { return false; } }   // B站 直链可以直接喂播放器（tests\README.md 第 4 条）
        public override string GetTitle(Target target) { return FetchBiliRoomTitle(target.Room); }
        public override string ResolveStreamUrl(Target target, out bool offline, out string failureReason)
        {
            // B站 不走中转，这里只给「本地解析」用：交给 yt-dlp 那条路，没有第二实现
            offline = false;
            failureReason = null;
            return null;
        }
        internal static RoomStateKind StateFromLiveStatus(int value)
        {
            return value == 1 ? RoomStateKind.Live : value == 2 ? RoomStateKind.Replay
                : value == 0 ? RoomStateKind.Offline : RoomStateKind.Unknown;
        }
        public override RoomStateKind GetRoomStateKind(string room, out string note)
        {
            note = null;
            try
            {
                string json = HttpGetText("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + room, "https://live.bilibili.com/");
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var data = Field2(root, "data");
                string status = Field(data, "live_status");
                int value;
                if (status == null || !int.TryParse(status, out value))
                {
                    note = Field(root, "code") == "1" ? "房间不存在或已失效" : "接口没给出开播状态";
                    return RoomStateKind.Unknown;
                }
                RoomStateKind state = StateFromLiveStatus(value);
                note = value == 2 ? "房间在放轮播" : null;
                return state;
            }
            catch (Exception ex) { note = "查询失败：" + ex.GetType().Name; return RoomStateKind.Unknown; }
        }
    }

    internal sealed class HuyaAdapter : PlatformAdapterBase
    {
        public override string Id { get { return "huya"; } }
        public override string DisplayName { get { return "虎牙"; } }
        public override string BaseUrl { get { return "https://www.huya.com/"; } }
        public override bool NeedsRelay { get { return true; } }
        // 2026-10-16 起能判下播：mp.huya.com 的 liveStatus 能区分「直播中」和「重播中」，
        // 而房间页的 liveStatus-on/off 不能（部分重播房间的页面也是 on，见下面 GetRoomStateKind）。
        public override bool CanTellOffline { get { return true; } }
        public override string GetTitle(Target target) { return FetchHuyaTitle(target.Room); }
        public override string ResolveStreamUrl(Target target, out bool offline, out string failureReason)
        {
            offline = false;
            // 真 OFF 由中转编排层拒播；REPLAY 必须走到这里实际尝试 hs/al/tx，不能提前当下播。
            return ResolveHuyaUrl(target, out failureReason);
        }
        public override RoomStateKind GetRoomStateKind(string room, out string note)
        {
            note = null;
            try
            {
                // 判据分两层（2026-10-16 实测后定的顺序）：
                //   ① mp.huya.com 的 liveStatus：ON / OFF / REPLAY —— 官网关注列表的
                //      「直播中 / 重播中」就是这个字段，是唯一能分辨「直播」与「重播」的信号；
                //   ② 房间页的 liveStatus-on / -replay / -off：接口查不到时兜底。
                // 为什么不能只看页面：主播下播转「重播中」后，房间页仍然是 liveStatus-on，
                // 而且照样给得出直链（实测 910323：api=REPLAY、页面 on、3 条 flv 直链），
                // 于是被当成「在播」——中转走不到「已下播挂起等待重开」，界面也一直显示在播。
                // 为什么不能只看接口：短号房间（a16789 安德罗妮、clkimmy）接口回 status=422
                // 「该主播不存在」（2026-09-19 的坑）——那种房间先从页面里取数字房号
                // （"profileRoom"）再问接口，取不到才退回页面判断。
                string html = null;
                HuyaLiveState mobile = FetchHuyaMobileState(room);
                if (mobile == HuyaLiveState.Unknown)
                {
                    html = HttpGetText("https://www.huya.com/" + room, "https://www.huya.com/");
                    string numeric = HuyaNumericRoomId(html);
                    if (numeric != null && numeric != room) mobile = FetchHuyaMobileState(numeric);
                }
                if (mobile == HuyaLiveState.Live) return RoomStateKind.Live;
                if (mobile == HuyaLiveState.Replay) { note = "房间在放重播"; return RoomStateKind.Replay; }
                if (mobile == HuyaLiveState.Offline) { note = "主播已下播"; return RoomStateKind.Offline; }

                if (html == null) html = HttpGetText("https://www.huya.com/" + room, "https://www.huya.com/");
                HuyaLiveState state = HuyaRoomState.Parse(html);
                if (state == HuyaLiveState.Unknown)
                {
                    // 只在认不出来时留一条线索，方便下次页面改版时排查
                    if (!BoundedLog.TryAppend(Path.Combine(RuntimeDir, "logs", "huya-check.log"),
                        PlatformHealth.ResponseSummary(html))) Diagnostics.ReportLogBroken(null);
                    note = "页面里没找到开播状态（可能改版）";
                    return RoomStateKind.Unknown;
                }
                // 页面说「在播」而接口没给出状态时**不要**当成在播：房间页把「重播中」也标成
                // liveStatus-on（2026-10-16 实测），接口一旦改版、出新状态值、或者短号取不到
                // 数字房号，这一条就会把重播谎报成在播。按 livehub-huya-state.cs 的既定原则：
                // 认不出来就是「查不到」（null），既不冒充在播也不冒充下播。
                if (state == HuyaLiveState.Live)
                {
                    note = "接口没给出开播状态（页面显示在播，可能是重播）";
                    return RoomStateKind.Unknown;
                }
                if (state == HuyaLiveState.Replay) { note = "房间在放重播"; return RoomStateKind.Replay; }
                return RoomStateKind.Offline;                     // off = 未开播
            }
            catch (Exception ex) { note = "查询失败：" + ex.GetType().Name; return RoomStateKind.Unknown; }
        }

        /// <summary>房间页里的数字房号（"profileRoom":528300）。短号房间靠它才能问接口。</summary>
        private static string HuyaNumericRoomId(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            Match match = Regex.Match(html, "\"profileRoom\"\\s*:\\s*\"?(\\d+)\"?");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>mp.huya.com 手机端接口的 liveStatus → HuyaLiveState。
        /// 短号（422）、网络失败、改版出新状态一律 Unknown —— 调用方退回房间页判断，不猜。</summary>
        private static HuyaLiveState FetchHuyaMobileState(string room)
        {
            if (string.IsNullOrEmpty(room)) return HuyaLiveState.Unknown;
            try
            {
                string json = HttpGetText("https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid="
                    + room, "https://www.huya.com/");
                if (string.IsNullOrEmpty(json)) return HuyaLiveState.Unknown;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (root == null || Field(root, "status") != "200") return HuyaLiveState.Unknown;
                var data = Field2(root, "data");
                if (data == null) return HuyaLiveState.Unknown;
                string liveStatus = Field(data, "liveStatus");
                if (liveStatus == null) return HuyaLiveState.Unknown;
                return HuyaRoomState.ParseApiStatus(liveStatus);
            }
            catch (Exception) { return HuyaLiveState.Unknown; }
        }
    }

    internal sealed class DouyuAdapter : PlatformAdapterBase
    {
        public override string Id { get { return "douyu"; } }
        public override string DisplayName { get { return "斗鱼"; } }
        public override string BaseUrl { get { return "https://www.douyu.com/"; } }
        public override bool NeedsRelay { get { return true; } }
        // 斗鱼的直链只够喂一条连接，复用等于第二条连接收 31KB 就 EOF —— 所以窗口是 0。
        public override double RelayUrlReuseSeconds { get { return LiveHub.DouyuRelayUrlReuseSeconds; } }
        public override string GetTitle(Target target) { return FetchDouyuTitle(target.Room); }
        public override string ResolveStreamUrl(Target target, out bool offline, out string failureReason)
        {
            offline = false;
            failureReason = null;
            string url = ResolveDouyuUrl(target.Room, RelayQuality);
            if (url == null) failureReason = "未解析到可用直链";
            return url;
        }
        internal static RoomStateKind StateFromValues(int showStatus, int videoLoop)
        {
            // 2026-10-16 再抽样 30 个目录在播房：show_status=1 且 videoLoop 全为 0，仍没有
            // 证据证明录播会同时给 show_status=1；因此不把 1+videoLoop=1 擅自改判为录播。
            if (showStatus == 1) return RoomStateKind.Live;
            return videoLoop == 1 ? RoomStateKind.Replay : RoomStateKind.Offline;
        }
        public override RoomStateKind GetRoomStateKind(string room, out string note)
        {
            note = null;
            try
            {
                string json = HttpGetText("https://www.douyu.com/betard/" + room, "https://www.douyu.com/");
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var data = Field2(root, "room");
                if (data == null) { note = "房间不存在或已失效"; return RoomStateKind.Unknown; }
                string status = Field(data, "show_status");
                int value;
                if (status == null || !int.TryParse(status, out value)) { note = "接口没给出开播状态"; return RoomStateKind.Unknown; }
                int loop;
                if (!int.TryParse(Field(data, "videoLoop"), out loop)) loop = 0;
                RoomStateKind state = StateFromValues(value, loop);
                note = state == RoomStateKind.Replay ? "房间在放录播" : null;
                return state;
            }
            catch (Exception ex) { note = "查询失败：" + ex.GetType().Name; return RoomStateKind.Unknown; }
        }
    }

    internal sealed class DouyinAdapter : PlatformAdapterBase
    {
        public override string Id { get { return "douyin"; } }
        public override string DisplayName { get { return "抖音"; } }
        public override string BaseUrl { get { return "https://live.douyin.com/"; } }
        public override string RoomPattern { get { return @"^\d+$"; } }
        public override bool NeedsRelay { get { return true; } }
        // 抖音仅将已知 status=4 判为下播；未知状态和抓取失败不能冒充 offline。
        public override bool CanTellOffline { get { return true; } }
        public override string GetTitle(Target target) { return FetchDouyinTitle(target.Room); }
        public override string ResolveStreamUrl(Target target, out bool offline, out string failureReason)
        {
            offline = false;
            failureReason = null;
            if (target != null)
            {
                string url = ResolveDouyinUrl(target.Room, out offline);
                if (url == null && !offline) failureReason = "未解析到可用直链";
                return url;
            }
            failureReason = "房间参数无效";
            return null;
        }
        public override RoomStateKind GetRoomStateKind(string room, out string note)
        {
            note = null;
            DouyinRoom info = FetchDouyinRoom(room);
            if (info == null) { note = "抓不到房间页（网络或页面改版）"; return RoomStateKind.Unknown; }
            if (info.Status == 2) return RoomStateKind.Live;
            if (info.Status == 4) return RoomStateKind.Offline;   // 4=未开播
            // 非 2/4 一律当「查不到」：解析器在字段缺失时把 Status 置 0，若在这里 return false，
            // 房间页一改版就会把**在播**的房间判成下播（启动器的徽标、中转的 preflight 都会跟着错）。
            // 与 livehub-huya-state.cs「未识别/请求失败必须是 Unknown」同一条原则。
            note = "房间状态异常（status=" + info.Status + "）";
            return RoomStateKind.Unknown;
        }
    }
}
