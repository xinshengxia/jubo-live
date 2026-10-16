using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal static partial class LiveHub
{
    internal sealed partial class LookupService
    {
        private LookupResult SearchAll(string text, CancellationToken token, Action<string> progress, bool refresh)
        {
            var tasks = new Task<LookupResult>[LookupDiscovery.PlatformKeys.Length];
            for (int i = 0; i < tasks.Length; i++)
            {
                string platform = LookupDiscovery.PlatformKeys[i];
                tasks[i] = Task.Factory.StartNew(delegate {
                    token.ThrowIfCancellationRequested();
                    LookupResult result;
                    try { result = SearchCore(platform, text, false, false, token, delegate { }, refresh); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { result = new LookupResult { status = "blocked", error_code = "network", note = "平台连接或响应异常；可刷新或打开官网。" }; }
                    token.ThrowIfCancellationRequested();
                    progress(Platforms.Name(platform) + (result.status == "blocked" ? "查询受阻" : "返回 " + result.candidates.Count + " 个候选") + "；其他平台继续查询…");
                    return result;
                }, token);
            }
            try { Task.WaitAll(tasks, token); } catch (AggregateException) { token.ThrowIfCancellationRequested(); throw; } token.ThrowIfCancellationRequested();
            var combined = new LookupResult { status = "platform", searched_at = DateTime.UtcNow.ToString("o"), cached = true };
            var notes = new List<string>(); int blocked = 0;
            for (int i = 0; i < tasks.Length; i++)
            {
                LookupResult result = tasks[i].Result; string platform = LookupDiscovery.PlatformKeys[i];
                combined.platforms.Add(new LookupPlatformOutcome { platform = platform, status = result.status, note = result.note,
                    count = result.candidates.Count, cached = result.cached, searched_at = result.searched_at, error_code = result.error_code });
                combined.cached = combined.cached && result.cached;
                combined.candidates.AddRange(result.candidates); combined.sources.AddRange(result.sources);
                if (result.status == "blocked") blocked++;
                notes.Add(Platforms.Name(platform) + "：" + (result.status == "blocked" ? "受阻" : result.candidates.Count + " 条") + (result.cached ? "（缓存）" : ""));
            }
            LookupDiscovery.Rank(combined.candidates, text, false);
            combined.status = blocked == tasks.Length ? "blocked" : combined.candidates.Count == 0 ? "empty" : blocked > 0 ? "partial" : "platform";
            combined.note = string.Join("；", notes) + "。按匹配线索排序，各平台账号分别展示；受阻平台可单独刷新或打开官网。";
            return combined;
        }
    }
}
