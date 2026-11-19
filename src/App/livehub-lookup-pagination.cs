using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal static partial class LiveHub
{
    internal sealed partial class LookupService
    {
        internal LookupResult MorePage(Dictionary<string, int> requested, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Length > 600 || requested.Count == 0)
                throw new LookupFailure("input", "请先完成昵称查询。");
            string[] scopes = new List<string>(requested.Keys).ToArray();
            var tasks = new Task<LookupResult>[scopes.Length];
            for (int i = 0; i < scopes.Length; i++)
            {
                string scope = scopes[i];
                int page = requested[scope];
                if (!Platforms.Known(scope) || page < 1 || page > 50)
                    throw new LookupFailure("input", "平台或页码无效。");
                tasks[i] = Task.Factory.StartNew(delegate
                {
                    try
                    {
                        string keyword = scope == "douyin" ? LookupData.DouyinHandleInput(text) : null;
                        LookupResult result = PlatformPageSearch(scope, keyword ?? LookupDiscovery.SearchTerms(text), page, token);
                        token.ThrowIfCancellationRequested();
                        result.searched_at = DateTime.UtcNow.ToString("o");
                        result.status = result.candidates.Count == 0 ? "empty" : "platform";
                        return result;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LookupFailure ex) { return new LookupResult { status = "blocked", error_code = ex.Code, note = ex.Message }; }
                    catch (Exception) { return new LookupResult { status = "blocked", error_code = "network", note = "平台分页响应失败，可重试本页。" }; }
                }, token);
            }
            try { Task.WaitAll(tasks, token); }
            catch (AggregateException) { token.ThrowIfCancellationRequested(); throw; }
            token.ThrowIfCancellationRequested();
            var combined = new LookupResult { searched_at = DateTime.UtcNow.ToString("o") };
            int blocked = 0;
            for (int i = 0; i < scopes.Length; i++)
            {
                LookupResult result = tasks[i].Result;
                combined.platforms.Add(new LookupPlatformOutcome { platform = scopes[i], status = result.status, count = result.candidates.Count,
                    note = result.note, searched_at = result.searched_at, error_code = result.error_code });
                if (result.status == "blocked") blocked++;
                combined.candidates.AddRange(result.candidates);
                combined.sources.AddRange(result.sources);
            }
            LookupDiscovery.Rank(combined.candidates, text, false);
            combined.status = blocked == scopes.Length ? "blocked" : blocked > 0 ? "partial" : combined.candidates.Count == 0 ? "empty" : "platform";
            combined.note = "各平台本次返回" + combined.candidates.Count + "条候选"
                + (blocked > 0 ? "；部分平台受阻，可重试或单独查询。" : "；与已有结果去重合并。") ;
            return combined;
        }
    }

    internal static void UpdateLookupPages(Dictionary<string, int> cursors, string platform, LookupResult result, bool initial)
    {
        if (initial) cursors.Clear();
        var outcomes = result.platforms;
        if (outcomes.Count == 0 && Platforms.Known(platform))
            outcomes = new List<LookupPlatformOutcome> { new LookupPlatformOutcome {
                platform = platform, status = result.status, count = result.candidates.Count } };
        foreach (LookupPlatformOutcome outcome in outcomes)
        {
            int page = 1;
            if (!initial && !cursors.TryGetValue(outcome.platform, out page)) continue;
            if (outcome.status == "blocked") cursors[outcome.platform] = page;
            else if (outcome.status == "platform" && outcome.count > 0 && page < 50)
                cursors[outcome.platform] = page + 1;
            else cursors.Remove(outcome.platform);
        }
    }

    internal static string LookupStableIdentity(LookupCandidate candidate)
    {
        if (candidate == null) return "";
        string key = !string.IsNullOrEmpty(candidate.sec_uid) ? "sec:" + candidate.sec_uid
            : !string.IsNullOrEmpty(candidate.account_id) ? "uid:" + candidate.account_id
            : !string.IsNullOrEmpty(candidate.room_id) ? "room:" + candidate.room_id
            : !string.IsNullOrEmpty(candidate.profile_url) ? "profile:" + candidate.profile_url : "name:" + candidate.display_name;
        return candidate.platform + ":" + key;
    }
}
