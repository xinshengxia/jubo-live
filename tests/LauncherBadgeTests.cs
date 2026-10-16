using System;
using System.Collections.Generic;
using System.Reflection;

internal static partial class HardeningTests
{
    // 开播标记的并行调度（2026-09-30）。
    //
    // 为什么值一条用例：这里原来是「每 2 秒只发一个房间」，14 个房间要串行等 ≈28 秒，
    // 而 LauncherUi 是私有嵌套类、测试够不到 —— 把「谁该在什么时候发出去」抽成纯函数，
    // 才能真正把「并行 + 上限 + 新鲜度 + 收藏优先」这几条钉住。
    private static void RegisterLauncherBadgeCases()
    {
        TestRunner.Add("启动器-开播标记", "并行调度：上限 / 新鲜度 / 收藏优先", TestBadgeScheduler);
    }

    private static void TestBadgeScheduler()
    {
        try
        {
            Type t = DanmakuType("BadgeScheduler");
            BindingFlags sf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            int max = Convert.ToInt32(t.GetField("MaxInFlight", sf).GetRawConstantValue());
            Check(max >= 2 && max <= 8, "开播标记：并发上限在合理区间（实得 " + max + "）");

            // 收藏 3 个 + 历史 11 个 = 14 个房间（截图里那种量级）
            List<string> keys = new List<string>();
            for (int i = 0; i < 14; i++) keys.Add("douyin:room" + i);
            HashSet<string> pending = new HashSet<string>();
            Dictionary<string, DateTime> times = new Dictionary<string, DateTime>();
            DateTime now = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);

            List<string> batch = (List<string>)CallStatic(t, "Pick", keys, pending, times, now, 4);
            Check(batch.Count == 4, "开播标记：一轮最多发 4 个（实得 " + batch.Count + "）");
            Check(batch[0] == "douyin:room0" && batch[3] == "douyin:room3",
                "开播标记：按候选顺序取（收藏优先）—— 实得 " + string.Join(",", batch.ToArray()));
            Check(pending.Count == 0 && times.Count == 0, "开播标记：调度器不改入参（在途/时间由调用方维护）");

            // 已经有 3 个在途 → 只补 1 个，凑满上限
            pending.Add("douyin:room0"); pending.Add("douyin:room1"); pending.Add("douyin:room2");
            batch = (List<string>)CallStatic(t, "Pick", keys, pending, times, now, 4);
            Check(batch.Count == 1 && batch[0] == "douyin:room3",
                "开播标记：已有 3 个在途时只补 1 个（实得 " + string.Join(",", batch.ToArray()) + "）");

            // 已经满额 → 一个都不发
            pending.Add("douyin:room3");
            batch = (List<string>)CallStatic(t, "Pick", keys, pending, times, now, 4);
            Check(batch.Count == 0, "开播标记：在途已满就不发（实得 " + batch.Count + "）");

            // 新鲜度：2 分钟内查过的不再查；超过 2 分钟可以重查
            pending.Clear();
            times["douyin:room0"] = now.AddMinutes(-1);
            batch = (List<string>)CallStatic(t, "Pick", keys, pending, times, now, 4);
            Check(!batch.Contains("douyin:room0"), "开播标记：1 分钟前查过的跳过");
            times["douyin:room0"] = now.AddMinutes(-3);
            batch = (List<string>)CallStatic(t, "Pick", keys, pending, times, now, 4);
            Check(batch.Contains("douyin:room0"), "开播标记：3 分钟前查过的可以重查");

            // 重复键不能占两个名额
            List<string> dup = new List<string> { "a:1", "a:1", "a:2" };
            batch = (List<string>)CallStatic(t, "Pick", dup, new HashSet<string>(),
                new Dictionary<string, DateTime>(), now, 4);
            Check(batch.Count == 2, "开播标记：候选里的重复房间只算一个（实得 " + batch.Count + "）");

            // 边界：上限 <= 0 / 空候选
            batch = (List<string>)CallStatic(t, "Pick", keys, new HashSet<string>(),
                new Dictionary<string, DateTime>(), now, 0);
            Check(batch.Count == 0, "开播标记：上限 0 时一个都不发");
            batch = (List<string>)CallStatic(t, "Pick", new List<string>(), new HashSet<string>(),
                new Dictionary<string, DateTime>(), now, 4);
            Check(batch.Count == 0, "开播标记：没有候选时安全返回空");
        }
        catch (Exception ex)
        {
            Check(false, "开播标记调度测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
