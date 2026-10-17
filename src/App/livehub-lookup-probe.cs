using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    // Production paths, bounded probe; exports public candidates only, never a raw HTTP body or request URL.
    private static int ProbeDouyinLookup(string keyword, string reportPath, bool browser)
    {
        var report = new Dictionary<string, object> { { "build", BuildInfo.Text }, { "at", DateTime.UtcNow.ToString("o") },
            { "mode", browser ? "webview2" : "native_http" }, { "keyword", keyword }, { "success", false } };
        int exit = 1;
        try
        {
            LookupResult result;
            using (var cancel = new CancellationTokenSource(15000))
                result = browser ? DouyinLookupBrowser.Show(null, keyword, 45, true, false)
                    : new LookupService().Search("douyin", keyword, false, false, cancel.Token, delegate { });
            var rows = new List<Dictionary<string, object>>();
            if (result != null) foreach (var candidate in result.candidates)
                rows.Add(new Dictionary<string, object> { { "display_name", candidate.display_name }, { "follower_count", candidate.follower_count }, { "sec_uid", candidate.sec_uid },
                    { "account_handle", candidate.account_handle }, { "account_id", candidate.account_id }, { "profile_url", candidate.profile_url },
                    { "room_id", candidate.room_id }, { "verification", candidate.verification }, { "source", candidate.source } });
            report["candidates"] = rows; report["candidate_count"] = rows.Count;
            report["status"] = result == null ? "cancelled" : result.status; report["note"] = result == null ? "网页辅助关闭。" : result.note;
            report["error_code"] = result == null ? null : result.error_code;
            report["success"] = rows.Count > 0; exit = rows.Count > 0 ? 0 : 1;
        }
        catch (Exception ex) { report["error_type"] = ex.GetType().Name; }
        try { string note; AtomicWritePrivateText(reportPath, new JavaScriptSerializer().Serialize(report), out note); }
        catch (Exception) { return 2; }
        return exit;
    }
}
