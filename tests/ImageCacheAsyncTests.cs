using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static void TestImageValidationAsync()
    {
        Type type = DanmakuType("DanmakuImageCache"); object cache = Activator.CreateInstance(type);
        MethodInfo get = type.GetMethod("TryGet"); int validations = 0, validationThread = 0;
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var returned = new ManualResetEvent(false))
        {
            type.GetField("ValidateUrl", ReviewInstance).SetValue(cache, (Func<string, bool>)delegate {
                Interlocked.Increment(ref validations); validationThread = Thread.CurrentThread.ManagedThreadId;
                entered.Set(); if (!release.WaitOne(5000)) throw new Exception("慢 DNS 校验夹具未释放"); return false;
            });
            Exception failure = null;
            var ui = new Thread(delegate() {
                try { get.Invoke(cache, new object[] { "https://cdn.example.com/emote.png" }); }
                catch (Exception ex) { failure = ex; }
                finally { returned.Set(); }
            });
            ui.SetApartmentState(ApartmentState.STA); ui.IsBackground = true; ui.Start();
            try
            {
                Check(entered.WaitOne(3000), "已进入受控慢 DNS/安全校验");
                Check(returned.WaitOne(300), "渲染调用在安全校验仍阻塞时已返回");
                Check(failure == null && validationThread != ui.ManagedThreadId, "完整地址校验运行在后台线程");
                var clock = Stopwatch.StartNew(); get.Invoke(cache, new object[] { "https://cdn.example.com/emote.png" });
                Check(clock.ElapsedMilliseconds < 300 && validations == 1, "pending 命中不等待或重复执行 DNS 校验");
                release.Set(); ui.Join(2000);
                object gate = type.GetField("_gate", ReviewInstance).GetValue(cache);
                var pending = type.GetField("_pending", ReviewInstance).GetValue(cache);
                clock.Restart();
                while (clock.ElapsedMilliseconds < 2000)
                {
                    lock (gate) { if ((int)LookupGet(pending, "Count") == 0) break; }
                    Thread.Sleep(10);
                }
                lock (gate) Check((int)LookupGet(pending, "Count") == 0, "安全拒绝后释放下载预约");
                get.Invoke(cache, new object[] { "https://cdn.example.com/emote.png" });
                Check(validations == 1 && (long)LookupGet(cache, "RequestCount") == 0,
                    "退避命中不重复校验，安全拒绝的地址不发起下载");
            }
            finally { release.Set(); ui.Join(2000); }
        }
    }
}
