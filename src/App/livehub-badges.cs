using System;
using System.Collections.Generic;

// --------------------------------------------------------------------------------------------
// 开播标记的调度策略（2026-09-30）
//
// 背景：启动器要为「收藏 + 最近播放」的每个房间查一次开播状态，用来显示 `[播]` / `[休]`。
// 原来是**每 2 秒的定时器只发一个房间**（原注释：避免集中请求平台接口）——
// 14 个房间就要串行等 14 个 tick ≈ 28 秒才把标记填满，用户看到的就是一个一个慢慢跳。
//
// 现在改成**有上限的并行**：同一轮最多同时查 MaxInFlight 个，回来一个立刻补一个。
// 上限不是可有可无的 —— 抖音有风控，B站/斗鱼/虎牙也不该在同一秒被同一个客户端打十几个请求。
// 并行的前提也核对过：四个 GetRoomState 都是无共享状态的（每次新建 HttpWebRequest），
// HttpGetText 只读登录 cookie，没有可写的静态状态。
//
// 抽成静态纯函数是为了能离线回归：「谁该在什么时候发出去」本来就和 UI 无关，
// 而 LauncherUi 是私有嵌套类，测试够不到它。
// --------------------------------------------------------------------------------------------

internal static class BadgeScheduler
{
    /// <summary>同一时刻最多在途几个开播查询。</summary>
    /// <remarks>
    /// 取 4 是折中：14 个房间从「串行 14×2 秒」降到「约 4 轮」；
    /// 又不至于同一秒给某个平台打十几个请求（抖音有风控）。要调就调这一个数。
    /// </remarks>
    internal const int MaxInFlight = 4;

    /// <summary>同一个房间多久之后允许重查（原来写死在调用处的 2 分钟）。</summary>
    internal const int FreshMinutes = 2;

    /// <summary>
    /// 从 <paramref name="orderedKeys"/>（收藏在前、最近播放在后）里挑出**这一轮该发出去**的键。
    ///
    /// 跳过：已在途（<paramref name="pending"/>）、最近 FreshMinutes 内查过（<paramref name="times"/>）、
    /// 以及本轮已经挑过的重复键；总数受 <paramref name="maxInFlight"/> 限制 ——
    /// pending 里可能还有别的房间在途，所以是「补到上限」而不是「一次挑 maxInFlight 个」。
    ///
    /// **不修改任何入参**：调用方负责把挑出来的键 Add 进 pending（我们只读它）。
    /// </summary>
    internal static List<string> Pick(IList<string> orderedKeys, ICollection<string> pending,
        IDictionary<string, DateTime> times, DateTime now, int maxInFlight)
    {
        List<string> batch = new List<string>();
        if (orderedKeys == null || maxInFlight <= 0) return batch;
        int inFlight = pending == null ? 0 : pending.Count;
        for (int i = 0; i < orderedKeys.Count && inFlight < maxInFlight; i++)
        {
            string key = orderedKeys[i];
            if (string.IsNullOrEmpty(key)) continue;
            if (pending != null && pending.Contains(key)) continue;
            if (batch.Contains(key)) continue;                        // 输入里可能有重复键
            DateTime at;
            if (times != null && times.TryGetValue(key, out at)
                && (now - at).TotalMinutes < FreshMinutes) continue;
            batch.Add(key);
            inFlight++;                        // 本轮挑出来的也算在途，否则会超过上限
        }
        return batch;
    }
}
