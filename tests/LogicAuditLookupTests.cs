using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static void RegisterLogicAuditLookupCases()
    {
        TestRunner.Add("主播查询", "抖音重新解析更新场次且保留账号归属校验", TestLogicAuditLookupRefresh);
    }

    private static string LogicAuditDouyinSessionPage(string session, string sec)
    {
        return "<script>self.__next_f.push([1,\"{\\\"roomId\\\":\\\"" + session
            + "\\\",\\\"anchor\\\":{\\\"sec_uid\\\":\\\"" + sec + "\\\",\\\"uid\\\":\\\"999\\\"}}\"])</script>";
    }

    private static string LogicAuditDouyinReflow(string session)
    {
        return "{\"status_code\":0,\"data\":{\"room\":{\"id_str\":\"" + session
            + "\",\"status\":2,\"owner\":{\"sec_uid\":\"MS4wLjfixture\",\"id_str\":\"999\",\"web_rid\":\"456\",\"nickname\":\"Synthetic streamer\"}}}}";
    }

    private static void TestLogicAuditLookupRefresh()
    {
        object verifier = LookupNew("LookupVerifier");
        object candidate = LookupCandidate("douyin", null, "456");
        LookupSet(candidate, "direct_input", true);
        string currentSession = "111111111", currentOwner = "MS4wLjfixture";
        bool blockedReflow = false;
        var requests = new List<string>();
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            string url = (string)args[0]; requests.Add(url);
            string body = url.Contains("/webcast/room/reflow/")
                ? blockedReflow || !url.Contains("room_id=" + currentSession + "&") ? "{\"status_code\":1}" : LogicAuditDouyinReflow(currentSession)
                : LogicAuditDouyinSessionPage(currentSession, currentOwner);
            return LookupReply(200, body, null);
        });
        object first = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(first, "verification") == "verified" && (string)LookupGet(first, "session_room_id") == currentSession,
            "首次核验保存当前场次并返回可复用候选");
        Check(requests.Count == 2, "首次核验仅请求直播页和对应reflow");

        currentSession = "222222222"; requests.Clear();
        object fresh = LookupCall(verifier, "VerifyFresh", first, CancellationToken.None);
        Check((string)LookupGet(fresh, "verification") == "verified" && (string)LookupGet(fresh, "session_room_id") == currentSession,
            "重新解析已核验候选绕过旧映射并取得新场次");
        Check(requests.Count == 2 && requests[0] == "https://live.douyin.com/456" && requests[1].Contains("room_id=222222222&"),
            "重新解析只读取当前直播页和新场次，不请求旧reflow");
        Check((string)LookupGet(fresh, "sec_uid") == "MS4wLjfixture" && (string)LookupGet(fresh, "account_id") == "999"
            && (string)LookupGet(fresh, "room_id") == "456" && !(bool)LookupGet(fresh, "user_confirmed"),
            "刷新保留稳定账号与房间身份，不自动确认收藏");
        Check((string)LookupGet(first, "session_room_id") == "111111111", "刷新不改写调用方旧候选快照");

        currentOwner = "MS4wLjother"; requests.Clear();
        object mismatch = LookupCall(verifier, "VerifyFresh", fresh, CancellationToken.None);
        Check((string)LookupGet(mismatch, "verification") == "mismatch" && requests.Count == 1,
            "刷新遇到直播页改属其他账号立即拒绝，不沿用旧身份或查询reflow");

        currentOwner = "MS4wLjfixture"; blockedReflow = true; requests.Clear();
        object blocked = LookupCall(verifier, "VerifyFresh", fresh, CancellationToken.None);
        Check((string)LookupGet(blocked, "verification") == "blocked" && requests.Count == 2,
            "新场次同样受阻时有限返回，不循环重试或恢复旧场次");

        requests.Clear();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool cancelled = false;
            try { LookupCall(verifier, "VerifyFresh", fresh, cancellation.Token); }
            catch (TargetInvocationException ex) { cancelled = ex.InnerException is OperationCanceledException; }
            Check(cancelled && requests.Count == 0, "取消后的显式刷新不发出平台请求");
        }
    }
}
