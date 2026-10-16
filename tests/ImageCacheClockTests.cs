using System;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static void TestImageBackoffWrap()
    {
        Type type = DanmakuType("DanmakuImageCache");
        MethodInfo complete = type.GetMethod("Complete", ReviewInstance), fetch = type.GetMethod("FetchNow", ReviewInstance);
        MethodInfo get = type.GetMethod("TryGet"); FieldInfo clock = type.GetField("TickCount", ReviewInstance);
        using (var server = new TinyHttpServer())
        foreach (int start in new[] { int.MaxValue - 1000, -1000, int.MinValue + 1000, 1000 })
        {
            int now = start; object cache = Activator.CreateInstance(type);
            clock.SetValue(cache, (Func<int>)delegate { return now; });
            string url = "http://127.0.0.1:" + server.Port + "/missing";
            complete.Invoke(cache, new object[] { url, null });
            now = unchecked(start + 1); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 0, "溢出前的首次退避仍生效，起点=" + start);
            now = unchecked(start + 29999); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 0, "跨过回绕后到期前仍不请求，起点=" + start);
            now = unchecked(start + 30000); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 1, "首轮到期恰好允许重试，起点=" + start);
            now = unchecked(start + 30000 + 119999); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 1, "第二轮退避到期前不请求，起点=" + start);
            now = unchecked(start + 30000 + 120000); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 2, "第二轮到期允许重试，起点=" + start);
            now = unchecked(start + 150000 + 599999); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 2, "第三轮十分钟退避到期前不请求，起点=" + start);
            now = unchecked(start + 150000 + 600000); fetch.Invoke(cache, new object[] { url });
            Check((long)LookupGet(cache, "RequestCount") == 3, "第三轮到期允许重试，起点=" + start);

            cache = Activator.CreateInstance(type); now = start;
            clock.SetValue(cache, (Func<int>)delegate { return now; });
            using (var attempted = new ManualResetEvent(false))
            {
                type.GetField("ValidateUrl", ReviewInstance).SetValue(cache, (Func<string, bool>)delegate { attempted.Set(); return false; });
                const string image = "https://104.16.2.34/emote.png";
                complete.Invoke(cache, new object[] { image, null }); now = unchecked(start + 1);
                get.Invoke(cache, new object[] { image });
                Check(!attempted.WaitOne(100), "TryGet 在回绕附近退避期间不安排校验，起点=" + start);
                now = unchecked(start + 30000); get.Invoke(cache, new object[] { image });
                Check(attempted.WaitOne(2000), "TryGet 到期后恢复后台校验，起点=" + start);
            }
        }
    }
}
