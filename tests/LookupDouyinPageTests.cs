using System;
using System.Collections.Generic;
using System.Threading;

internal static partial class HardeningTests
{
    // These identities and numbers are synthetic; no captured account data is used.
    private const string LookupRoomStoreSec = "MS4wLjSyntheticRoomStoreUser";
    private const string LookupRoomStoreSession = "9800000000000000001";
    private const string LookupRoomStoreWeb = "654321009";
    private const string LookupRoomStoreAccount = "333444555";

    private static void RegisterLookupDouyinPageCases()
    {
        TestRunner.Add("主播查询", "抖音roomStore与旧版别名页结构兼容", TestLookupDouyinRoomStorePage);
        TestRunner.Add("主播查询", "抖音roomStore别名页继续经reflow核验", TestLookupDouyinRoomStoreAlias);
        TestRunner.Add("主播查询", "抖音roomStore异主与缺失场次拒绝绑定", TestLookupDouyinRoomStoreFailures);
    }

    private static string LookupRoomStoreJson(int status)
    {
        return "7:{\"state\":{\"roomStore\":{\"roomInfo\":{\"room\":{\"id_str\":\"" + LookupRoomStoreSession
            + "\",\"status\":" + status + ",\"title\":\"合成直播标题\",\"owner\":{\"sec_uid\":\"" + LookupRoomStoreSec
            + "\",\"id_str\":\"" + LookupRoomStoreAccount + "\",\"web_rid\":\"" + LookupRoomStoreWeb
            + "\",\"nickname\":\"合成主播\"}}}}}}";
    }

    private static string LookupRoomStoreReflow(int status)
    {
        return "{\"status_code\":0,\"data\":{\"room\":{\"id_str\":\"" + LookupRoomStoreSession
            + "\",\"status\":" + status + ",\"owner\":{\"sec_uid\":\"" + LookupRoomStoreSec
            + "\",\"id_str\":\"" + LookupRoomStoreAccount + "\",\"web_rid\":\"" + LookupRoomStoreWeb
            + "\",\"nickname\":\"合成主播\"}}}}";
    }

    private static object LookupRoomStoreVerify(string page, string reflow, List<string> requests)
    {
        object candidate = LookupCandidate("douyin", null, null);
        LookupSet(candidate, "sec_uid", LookupRoomStoreSec);
        LookupSet(candidate, "profile_url", "https://www.douyin.com/user/" + LookupRoomStoreSec);
        string profile = "{\"status_code\":0,\"user_info\":{\"sec_uid\":\"" + LookupRoomStoreSec
            + "\",\"unique_id\":\"SyntheticAlias822\",\"nickname\":\"合成主播\"}}";
        object verifier = LookupNew("LookupVerifier");
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            string url = (string)args[0]; requests.Add(url);
            if (url == "https://www.douyin.com/user/" + LookupRoomStoreSec)
                return LookupReply(200, "<html>synthetic profile without live data</html>", null);
            if (url.Contains("/user/info/?sec_uid=")) return LookupReply(200, profile, null);
            if (url == "https://live.douyin.com/SyntheticAlias822") return LookupReply(200, page, null);
            if (url.Contains("/webcast/room/reflow/info/?")) return LookupReply(200, reflow, null);
            throw new Exception("Unexpected synthetic lookup request: " + url);
        });
        return LookupCall(verifier, "Verify", candidate, CancellationToken.None);
    }

    private static void TestLookupDouyinRoomStorePage()
    {
        foreach (bool array in new[] { false, true })
            foreach (string tail in new[] { "])", ")]" })
                foreach (string script in new[] { "__pace_f", "__next_f" })
                {
                    string json = LookupRoomStoreJson(2);
                    if (array) json = "7:[" + json.Substring(2) + "]";
                    string page = DouyinFlight(json, tail).Replace("__pace_f", script);
                    object room = LookupCall(LookupType("LookupVerifier"), "LookupDouyinPage", page);
                    string variant = "array=" + array + ",tail=" + tail + ",script=" + script;
                    Check(room != null && (string)LookupGet(room, "RoomId") == LookupRoomStoreSession
                        && (string)LookupGet(room, "OwnerSecUid") == LookupRoomStoreSec,
                        "roomStore别名页保留内部场次与sec_uid：" + variant);
                    Check((string)LookupGet(room, "OwnerId") == LookupRoomStoreAccount
                        && (string)LookupGet(room, "OwnerWebRid") == LookupRoomStoreWeb
                        && (string)LookupGet(room, "Title") == "合成直播标题" && (int)LookupGet(room, "Status") == 2,
                        "roomStore房间字段保持现有解析语义：" + variant);
                }
        string legacy = DouyinFlight("7:{\"roomId\":\"" + LookupRoomStoreSession
            + "\",\"anchor\":{\"sec_uid\":\"" + LookupRoomStoreSec + "\",\"uid\":\""
            + LookupRoomStoreAccount + "\",\"nickname\":\"合成旧版主播\"}}", "])").Replace("__pace_f", "__next_f");
        object oldRoom = LookupCall(LookupType("LookupVerifier"), "LookupDouyinPage", legacy);
        Check(oldRoom != null && (string)LookupGet(oldRoom, "RoomId") == LookupRoomStoreSession
            && (string)LookupGet(oldRoom, "OwnerSecUid") == LookupRoomStoreSec
            && (string)LookupGet(oldRoom, "OwnerId") == LookupRoomStoreAccount,
            "roomId与anchor旧版Flight结构继续解析");
    }

    private static void TestLookupDouyinRoomStoreAlias()
    {
        foreach (int status in new[] { 2, 4 })
        {
            var requests = new List<string>();
            object result = LookupRoomStoreVerify(DouyinFlight(LookupRoomStoreJson(status), "])"), LookupRoomStoreReflow(status), requests);
            Check((string)LookupGet(result, "verification") == "verified" && (bool)LookupGet(result, "account_verified"),
                "标准roomStore别名页能完成账号与房间归属核验，status=" + status);
            Check(requests.Count == 4 && requests[2] == "https://live.douyin.com/SyntheticAlias822"
                && requests[3].Contains("room_id=" + LookupRoomStoreSession)
                && requests[3].Contains("sec_user_id=" + LookupRoomStoreSec),
                "别名页后仍以同一sec_uid与内部场次请求reflow，status=" + status);
            Check((string)LookupGet(result, "session_room_id") == LookupRoomStoreSession
                && (string)LookupGet(result, "room_id") == LookupRoomStoreWeb
                && (string)LookupGet(result, "room_url") == "https://live.douyin.com/" + LookupRoomStoreWeb,
                "最终数字直播链接使用reflow网页号而非抖音号或内部场次，status=" + status);
            Check((string)LookupGet(result, "live_status") == (status == 2 ? "live" : "offline")
                && (string)LookupGet(result, "resolution_path") == "profile_api_then_alias_ssr_then_reflow"
                && (string)LookupCall(LookupType("LookupData"), "VerifiedRoomKey", result) == "douyin:" + LookupRoomStoreWeb,
                "开播与下播均保留已核验数字房间，status=" + status);
            Check(!(bool)LookupGet(result, "user_confirmed") && LookupCall(LookupType("LookupData"), "FavoriteKey", result) == null,
                "房间核验不会代替用户确认收藏，status=" + status);
            LookupSet(result, "user_confirmed", true);
            Check((string)LookupCall(LookupType("LookupData"), "FavoriteKey", result) == "douyin:" + LookupRoomStoreWeb,
                "用户确认后收藏使用已核验网页房间号，status=" + status);
        }
    }

    private static void TestLookupDouyinRoomStoreFailures()
    {
        string page = DouyinFlight(LookupRoomStoreJson(2), "])");
        string reflow = LookupRoomStoreReflow(2);
        var requests = new List<string>();
        object result = LookupRoomStoreVerify(page.Replace(LookupRoomStoreSec, "MS4wLjSyntheticOtherUser"), reflow, requests);
        Check((string)LookupGet(result, "verification") == "mismatch" && requests.Count == 3
            && LookupGet(result, "room_id") == null && LookupGet(result, "session_room_id") == null,
            "roomStore别名页异主立即拒绝且不调用reflow");
        foreach (string mismatch in new[] { reflow.Replace(LookupRoomStoreSec, "MS4wLjSyntheticOtherUser"),
            reflow.Replace(LookupRoomStoreSession, "9800000000000000002") })
        {
            requests.Clear(); result = LookupRoomStoreVerify(page, mismatch, requests);
            Check((string)LookupGet(result, "verification") == "mismatch" && requests.Count == 4
                && LookupGet(result, "room_id") == null && LookupCall(LookupType("LookupData"), "VerifiedRoomKey", result) == null,
                "roomStore解析成功后reflow仍拒绝异主或异场次");
        }
        requests.Clear(); result = LookupRoomStoreVerify(page, reflow.Replace("\"web_rid\":\"" + LookupRoomStoreWeb + "\",", ""), requests);
        Check((string)LookupGet(result, "verification") == "account-only" && requests.Count == 4
            && LookupGet(result, "room_id") == null && LookupCall(LookupType("LookupData"), "VerifiedRoomKey", result) == null,
            "reflow缺网页号时不凭roomStore网页号跳过归属核验");
        foreach (string incomplete in new[] {
            LookupRoomStoreJson(2).Replace("\"id_str\":\"" + LookupRoomStoreSession + "\",", ""),
            LookupRoomStoreJson(2).Replace(LookupRoomStoreSession, "0"),
            LookupRoomStoreJson(2).Replace("\"sec_uid\":\"" + LookupRoomStoreSec + "\",", ""),
            LookupRoomStoreJson(2).Replace(LookupRoomStoreSec, " "),
            "7:{\"state\":{\"streamStore\":{},\"user\":{\"nickname\":\"合成主播\"}}}"
        })
        {
            string incompletePage = DouyinFlight(incomplete, "])");
            Check(LookupCall(LookupType("LookupVerifier"), "LookupDouyinPage", incompletePage) == null,
                "缺少可关联的内部场次或sec_uid时不生成房间");
            requests.Clear(); result = LookupRoomStoreVerify(incompletePage, reflow, requests);
            Check((string)LookupGet(result, "verification") == "account-only" && (bool)LookupGet(result, "account_verified")
                && requests.Count == 3 && LookupGet(result, "room_id") == null
                && LookupCall(LookupType("LookupData"), "VerifiedRoomKey", result) == null,
                "无有效roomStore场次保留已核实账号且不编造直播链接");
        }
        Check(LookupCall(LookupType("LookupVerifier"), "LookupDouyinPage", new string(' ', 4 * 1024 * 1024) + page) == null,
            "roomStore别名页超过响应预算时不继续解析");
    }
}
