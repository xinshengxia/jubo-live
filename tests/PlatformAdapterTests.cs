// --------------------------------------------------------------------------------------------
// 平台适配器表（评估报告 §7 的建议）。
//
// 这一层的价值全在「只有一处定义」：显示名 / 站点根地址 / 房间号规则 / 要不要中转 /
// 直链复用窗口 / 能不能判下播。加第五个平台时改这张表 + 一个适配器类就够。
// 所以这里逐项钉死四家的数据，并验证**真实启动计划**里的 NeedsRelay 与表一致 ——
// 表说一套、计划做另一套的话，这条会红。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void TestPlatformAdapters(string launcher)
    {
        Type platforms = hub.GetNestedType("Platforms", BindingFlags.Public | BindingFlags.NonPublic);
        Type adapterType = hub.GetNestedType("IPlatformAdapter", BindingFlags.Public | BindingFlags.NonPublic);
        Check(platforms != null && adapterType != null, "平台适配器：能找到适配器表与接口");
        if (platforms == null || adapterType == null) return;

        var all = platforms.GetField("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .GetValue(null) as Array;
        Check(all != null && all.Length == 4, "平台适配器：表里有 4 家（实得 " + (all == null ? 0 : all.Length) + "）");
        if (all == null) return;

        Type roomState = hub.Assembly.GetType("RoomStateKind");
        Check(roomState != null && adapterType.GetMethod("GetRoomStateKind") != null && adapterType.GetMethod("GetRoomState") != null,
            "房间状态：四态接口与旧 bool? 兼容包装同时存在");
        object unknown = Enum.Parse(roomState, "Unknown"), live = Enum.Parse(roomState, "Live");
        object offline = Enum.Parse(roomState, "Offline"), replay = Enum.Parse(roomState, "Replay");
        Check((bool)Call("CanAttemptPlayback", live) && (bool)Call("CanAttemptPlayback", replay)
            && (bool)Call("CanAttemptPlayback", unknown) && !(bool)Call("CanAttemptPlayback", offline),
            "房间状态：只有真 OFF 拒播，重播仍尝试取流");
        Check((string)Call("RoomStateBadge", "huya", replay) == "[重播] "
            && (string)Call("RoomStateBadge", "bili", replay) == "[轮播] "
            && (string)Call("RoomStateBadge", "douyu", replay) == "[录播] ",
            "房间状态：收藏/最近播放徽标区分重播、轮播、录播");
        Check((string)Call("RoomStateLabel", "huya", replay) == "重播"
            && (string)Call("RoomStateLabel", "bili", replay) == "轮播"
            && (string)Call("RoomStateLabel", "douyu", replay) == "录播",
            "房间状态：会话列区分重播、轮播、录播");
        Check(Call("LegacyRoomState", replay) == null && Equals(Call("LegacyRoomState", live), true)
            && Equals(Call("LegacyRoomState", offline), false), "房间状态：旧 bool? 包装不把重播压成下播");
        Check((string)Call("RoomStateFileText", "huya", replay) == "REPLAY"
            && (string)Call("RoomStateFileText", "bili", replay) == "LOOP"
            && (string)Call("RoomStateFileText", "douyu", replay) == "RECORDING"
            && ((string)Call("RoomStateFileText", "huya", unknown)).StartsWith("UNKNOWN|"),
            "房间状态：会话文件保留四态，Unknown 带时间戳覆盖陈旧值");
        Check((string)Call("ReplayUnavailableMessage", "HTTP 403") == "重播流取不到（HTTP 403）",
            "重播取流失败：文案不再冒充主播没在播");

        var byId = new Dictionary<string, object>();
        foreach (object adapter in all)
        {
            string id = (string)adapterType.GetProperty("Id").GetValue(adapter, null);
            byId[id] = adapter;
            string name = (string)adapterType.GetProperty("DisplayName").GetValue(adapter, null);
            string baseUrl = (string)adapterType.GetProperty("BaseUrl").GetValue(adapter, null);
            string pattern = (string)adapterType.GetProperty("RoomPattern").GetValue(adapter, null);
            Check(!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(pattern),
                "平台适配器[" + id + "]：显示名/根地址/房间号规则都不为空");
        }
        foreach (string id in new string[] { "bili", "huya", "douyu", "douyin" })
            Check(byId.ContainsKey(id), "平台适配器：表里有 " + id);

        // ---- ① 四家的关键差异（就是原来散在各处的那几个三元表达式）----
        Check(GetBool(adapterType, byId["bili"], "NeedsRelay") == false, "平台适配器：B站 不走中转");
        Check(GetBool(adapterType, byId["huya"], "NeedsRelay"), "平台适配器：虎牙 走中转");
        Check(GetBool(adapterType, byId["douyu"], "NeedsRelay"), "平台适配器：斗鱼 走中转");
        Check(GetBool(adapterType, byId["douyin"], "NeedsRelay"), "平台适配器：抖音 走中转");
        // 2026-10-16：虎牙也能判下播了 —— mp.huya.com 的 liveStatus 能区分「直播中」和
        // 「重播中」，而房间页把重播房间也标成 liveStatus-on（见 tests\README.md 第 7 条）。
        Check(GetBool(adapterType, byId["douyin"], "CanTellOffline") && GetBool(adapterType, byId["huya"], "CanTellOffline"),
            "平台适配器：抖音/虎牙能明确判下播");
        Check(!GetBool(adapterType, byId["douyu"], "CanTellOffline"),
            "平台适配器：斗鱼还判不了下播（靠 ResolveStreamUrl 失败后再问一次）");
        // 短号房间（a16789/clkimmy）接口回 422，得先从页面里取数字房号再问接口 —— 钉住取法。
        MethodInfo numeric = byId["huya"].GetType().GetMethod("HuyaNumericRoomId", BindingFlags.NonPublic | BindingFlags.Static);
        Check(numeric != null, "虎牙：有「短号 → 数字房号」的取法");
        if (numeric != null)
        {
            Check((string)numeric.Invoke(null, new object[] { "\"profileRoom\":528300,\"x\":1" }) == "528300",
                "虎牙短号取数字房号：不带引号的 profileRoom");
            Check((string)numeric.Invoke(null, new object[] { "\"profileRoom\": \"910323\"" }) == "910323",
                "虎牙短号取数字房号：带引号/带空格的 profileRoom");
            Check((string)numeric.Invoke(null, new object[] { "<html>没有这个字段</html>" }) == null,
                "虎牙短号取数字房号：页面里没有就返回 null（不猜）");
        }
        MethodInfo biliState = byId["bili"].GetType().GetMethod("StateFromLiveStatus", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo douyuState = byId["douyu"].GetType().GetMethod("StateFromValues", BindingFlags.NonPublic | BindingFlags.Static);
        Check(biliState != null && Equals(biliState.Invoke(null, new object[] { 2 }), replay),
            "B站：live_status=2 映射轮播态");
        Check(douyuState != null && Equals(douyuState.Invoke(null, new object[] { 2, 1 }), replay)
            && Equals(douyuState.Invoke(null, new object[] { 1, 1 }), live),
            "斗鱼：仅已证实的非直播 videoLoop=1 映射录播，1+1 暂不猜");
        Type huyaState = hub.Assembly.GetType("HuyaRoomState");
        object huyaReplay = Enum.Parse(hub.Assembly.GetType("HuyaLiveState"), "Replay");
        Check(huyaState != null && Equals(huyaState.GetMethod("ParseApiStatus", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { "REPLAY" }), huyaReplay), "虎牙：手机接口 REPLAY 映射独立重播态");
        string huyaDanmaku = System.IO.File.ReadAllText(System.IO.Path.Combine(SourceRoot, "src", "Danmaku", "danmaku-9-huya.cs"));
        Check(huyaDanmaku.Contains("LiveHub.CheckRoomState(\"huya\"") && huyaDanmaku.Contains("FromRoomState"),
            "虎牙弹幕：复用平台四态判据，不再只看网页 liveStatus-on");
        Check(GetDouble(adapterType, byId["douyu"], "RelayUrlReuseSeconds") == 0,
            "平台适配器：斗鱼 直链复用窗口 = 0（一条直链只够喂一条连接）");
        foreach (string id in new string[] { "bili", "huya", "douyin" })
            Check(GetDouble(adapterType, byId[id], "RelayUrlReuseSeconds") > 0,
                "平台适配器：" + id + " 直链复用窗口 > 0（PotPlayer 首包耐心约 1.05 秒）");

        // ---- ② 不认识就返回 null，不猜成 B站（原 PlatformName 的 else 分支就是这个毛病）----
        MethodInfo get = platforms.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(get.Invoke(null, new object[] { "nosuch" }) == null, "平台适配器：不认识的 id 返回 null");
        Check(get.Invoke(null, new object[] { null }) == null, "平台适配器：null 也返回 null");
        Check((string)Call("PlatformName", "nosuch") == "nosuch", "平台名：不认识的 id 原样返回，不冒充 B站");
        foreach (string id in new string[] { "bili", "huya", "douyu", "douyin" })
            Check((string)Call("PlatformName", id) == (string)adapterType.GetProperty("DisplayName").GetValue(byId[id], null),
                "平台名：" + id + " 与适配器一致");

        // ---- ③ Parse 用适配器的根地址与房间号规则 ----
        foreach (string id in new string[] { "bili", "huya", "douyu", "douyin" })
        {
            string room = (id == "bili" || id == "douyin") ? "7777" : "a16789";
            object target = Call("Parse", id + ":" + room, "bili");
            Check(target != null, "Parse[" + id + "]：认这个平台");
            if (target == null) continue;
            string url = (string)target.GetType().GetField("Url").GetValue(target);
            string expected = (string)adapterType.GetProperty("BaseUrl").GetValue(byId[id], null) + room;
            Check(url == expected, "Parse[" + id + "]：根地址来自适配器（" + url + "）");
        }
        Check(Call("Parse", "bili:abc", "bili") == null, "Parse：B站 房间号必须是数字（适配器规则）");
        Check(Call("Parse", "douyin:abc", "bili") == null, "Parse：抖音 房间号必须是数字（适配器规则）");
        Check(Call("Parse", "huya:a16789", "bili") != null, "Parse：虎牙 短号 a16789 合法");

        // ---- ④ 真实启动计划里的 NeedsRelay 必须与表一致 ----
        TestPlatformAdapterPlanConsistency(adapterType, byId);
    }

    private static bool GetBool(Type adapterType, object adapter, string property)
    {
        return (bool)adapterType.GetProperty(property).GetValue(adapter, null);
    }

    private static double GetDouble(Type adapterType, object adapter, string property)
    {
        return (double)adapterType.GetProperty(property).GetValue(adapter, null);
    }

    /// <summary>把四家各跑一遍 ResolvePlan（纯函数、不联网），断言 plan.NeedsRelay == 适配器表的 NeedsRelay。</summary>
    private static void TestPlatformAdapterPlanConsistency(Type adapterType, Dictionary<string, object> byId)
    {
        string originalMpv = (string)hub.GetField("Mpv", Flags).GetValue(null);
        string originalTitle = (string)hub.GetField("LiveTitleScript", Flags).GetValue(null);
        string originalRuntime = (string)hub.GetField("RuntimeDir", Flags).GetValue(null);
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "livehub-adapter-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            hub.GetField("Mpv", Flags).SetValue(null, System.Reflection.Assembly.GetExecutingAssembly().Location);
            hub.GetField("LiveTitleScript", Flags).SetValue(null, System.IO.Path.Combine(directory, "live-title.lua"));
            hub.GetField("RuntimeDir", Flags).SetValue(null, directory);
            MethodInfo resolve = hub.GetMethod("ResolvePlan", Flags);
            Type targetType = hub.GetNestedType("Target", BindingFlags.Public | BindingFlags.NonPublic);
            foreach (string id in new string[] { "bili", "huya", "douyu", "douyin" })
            {
                string room = (id == "bili" || id == "douyin") ? "7777" : "660000";
                object target = Call("Parse", id + ":" + room, "bili");
                if (target == null) { Check(false, "计划一致性[" + id + "]：Parse 失败"); continue; }
                object plan = resolve.Invoke(null, new object[] { target, "mpv", false, false });
                bool needsRelay = (bool)plan.GetType().GetField("NeedsRelay").GetValue(plan);
                bool fromTable = GetBool(adapterType, byId[id], "NeedsRelay");
                Check(needsRelay == fromTable,
                    "计划一致性[" + id + "]：plan.NeedsRelay(" + needsRelay + ") == 适配器表(" + fromTable + ")");
            }
        }
        finally
        {
            hub.GetField("Mpv", Flags).SetValue(null, originalMpv);
            hub.GetField("LiveTitleScript", Flags).SetValue(null, originalTitle);
            hub.GetField("RuntimeDir", Flags).SetValue(null, originalRuntime);
            try { System.IO.Directory.Delete(directory, true); } catch (Exception) { }
        }
    }
}
