// --------------------------------------------------------------------------------------------
// 位图生命周期：**所有权说了算**，不是「等一会儿」也不是「事后问一句」。
//
// 2026-09-27 用户报的崩溃：
//   System.ArgumentException: 参数无效。
//     在 System.Drawing.Image.get_Width()
//     在 DanmakuRenderer.Draw(Graphics g)          ← 读 f.Bitmap.Width
// 老实现是「淘汰的位图进隔离区等 60 秒再释放」—— 但热门房间一秒能退役几十个 key，
// 隔离区 512 条的硬上限一撞上就从最老的那头无条件 Dispose，安全期形同虚设，
// 正好把**还在飞的弹幕**手里那张位图放掉了。改成「事后问一句还在不在用」也不成立：
// GDI+ 对已释放的位图抛的是 ArgumentException（dotnet/runtime#29841），没有可靠的活着判据。
//
// 现在：SharedBitmap 引用计数 —— 位图只有在「缓存不再持有」**并且**「借出去的都还了」之后才释放，
// 于是「手里拿着一张已经释放的位图」在结构上不可能发生。
//
// 代价：每个借出点都必须 Release（漏了是 GDI 泄漏，不会再崩）。第 ⑥ 条专门盯着这件事。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections;
using System.Drawing;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void TestSharedBitmapOwnership(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
        Type sharedType = assembly.GetType("SharedBitmap", true);
        Type cacheType = assembly.GetType("DanmakuTextCache", true);
        Type imageCacheType = assembly.GetType("DanmakuImageCache", true);
        Type rendererType = assembly.GetType("DanmakuRenderer", true);
        Type configType = assembly.GetType("DanmakuConfig", true);
        Type itemType = assembly.GetType("DanmakuItem", true);
        Type flyingType = assembly.GetType("FlyingDanmaku", true);

        ConstructorInfo ctor = sharedType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Bitmap) }, null);
        MethodInfo acquire = sharedType.GetMethod("Acquire", priv);
        MethodInfo release = sharedType.GetMethod("Release", priv);
        MethodInfo drop = sharedType.GetMethod("DropFromCache", priv);
        Check(ctor != null && acquire != null && release != null && drop != null,
            "位图所有权：SharedBitmap 暴露 ctor/Acquire/Release/DropFromCache（否则这条是空转）");
        if (ctor == null || acquire == null || release == null || drop == null) return;

        // ---- ① 基本语义：借用期间绝不释放，最后一份还回来才释放 ----
        object one = ctor.Invoke(new object[] { new Bitmap(8, 8) });
        acquire.Invoke(one, null);
        acquire.Invoke(one, null);          // 借两份
        drop.Invoke(one, null);             // 缓存淘汰
        release.Invoke(one, null);          // 还一份：还有一份在外面
        Check(!IsFreed(one), "位图所有权：还了一份但还有人在用 → 不释放（是借用计数，不是布尔）");
        release.Invoke(one, null);          // 最后一份
        Check(IsFreed(one), "位图所有权：最后一份还回来 → 立刻释放（不需要等安全期）");

        object orphan = ctor.Invoke(new object[] { new Bitmap(8, 8) });
        drop.Invoke(orphan, null);
        Check(IsFreed(orphan), "位图所有权：从没借出去过的，一淘汰就释放（不留孤儿）");

        // ---- ② 这就是那次崩溃的条件：缓存清了，飞行中的弹幕还要接着画 ----
        object textCache = Activator.CreateInstance(cacheType);
        MethodInfo get = cacheType.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo clear = cacheType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo liveCount = sharedType.GetProperty("LiveCount", BindingFlags.NonPublic | BindingFlags.Static);
        int liveBefore = (int)liveCount.GetValue(null, null);
        object onFlight = get.Invoke(textCache, new object[] { "崩溃条件", Color.White, 24f, "Microsoft YaHei UI", true, 2 });
        int widthWhileCached = BitmapOf(onFlight).Width;
        clear.Invoke(textCache, null);      // 换房间 / 改字号 / 换表情字典都会走这里
        bool stillDrawable = true;
        try { stillDrawable = BitmapOf(onFlight).Width == widthWhileCached; }
        catch (Exception) { stillDrawable = false; }
        Check(stillDrawable, "位图所有权：缓存清空后，飞行中的弹幕手里那张照样能画（就是那次崩溃的条件）");
        ReleaseShared(onFlight);
        Check(IsFreed(onFlight), "位图所有权：弹幕摘掉、还回来之后才释放");

        // ---- ③ 图片缓存同理：没下载好的 URL 返回 null，调用方走占位 ----
        object imageCache = Activator.CreateInstance(imageCacheType);
        MethodInfo tryGet = imageCacheType.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Instance);
        Check(tryGet.Invoke(imageCache, new object[] { "http://example.invalid/none.png" }) == null,
            "图片缓存：没下载好的 URL 返回 null（不借出空句柄）");

        // ---- ④ 渲染器端到端：真起一条弹幕，清空缓存再画一帧 ----
        object config = Activator.CreateInstance(configType);
        configType.GetField("SmoothDanmaku").SetValue(config, false);
        configType.GetField("QueueWaitMs").SetValue(config, 0);
        object renderer = Activator.CreateInstance(rendererType, new object[] { config });
        rendererType.GetMethod("Resize").Invoke(renderer, new object[] { 1280, 700 });
        object item = Activator.CreateInstance(itemType);
        itemType.GetField("Text").SetValue(item, "位图所有权回归");
        rendererType.GetMethod("AddDanmaku").Invoke(renderer, new object[] { item });
        FieldInfo flyingField = rendererType.GetField("_flying", BindingFlags.NonPublic | BindingFlags.Instance);
        IList list = (IList)flyingField.GetValue(renderer);
        Check(list.Count == 1, "位图所有权：弹幕已入队（实得 " + list.Count + " 条）");
        if (list.Count != 1) return;
        object onScreen = flyingType.GetField("Handle").GetValue(list[0]);
        Check(onScreen != null, "位图所有权：这条弹幕挂着句柄（不再是裸 Bitmap）");
        if (onScreen == null) return;
        // ⚠️ 必须挪进可视区：Draw 里有 `f.X > _width → continue`，不挪的话下面的「画一帧」是空转的
        flyingType.GetField("X").SetValue(list[0], 100f);
        flyingType.GetField("Y").SetValue(list[0], 20f);

        FieldInfo textCacheField = rendererType.GetField("_textCache", BindingFlags.NonPublic | BindingFlags.Instance);
        clear.Invoke(textCacheField.GetValue(renderer), null);     // 缩放 / 换房间：整批换 key
        MethodInfo draw = rendererType.GetMethod("Draw");
        using (Bitmap canvas = new Bitmap(1280, 700))
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            bool threw = false;
            try { draw.Invoke(renderer, new object[] { graphics }); }
            catch (Exception) { threw = true; }
            Check(!threw && !IsFreed(onScreen), "位图所有权：清空缓存后画一帧照常（旧实现在这里抛「参数无效」）");
        }

        // 弹幕离屏 → 摘掉 → 句柄还回去 → 释放
        MethodInfo advance = rendererType.GetMethod("Advance");
        flyingType.GetField("X").SetValue(list[0], -10000f);
        advance.Invoke(renderer, new object[] { DateTime.UtcNow.AddSeconds(60), 0.05f });
        Check(list.Count == 0, "位图所有权：离屏后被摘掉（实得 " + list.Count + " 条）");
        Check(IsFreed(onScreen), "位图所有权：摘掉时把借来的位图还了并释放（不漏 GDI 对象）");

        // ---- ⑤ 兜底层仍在：位图真被外部弄坏时，只丢那一条，不许带走整帧 ----
        object item2 = Activator.CreateInstance(itemType);
        itemType.GetField("Text").SetValue(item2, "坏位图");
        rendererType.GetMethod("AddDanmaku").Invoke(renderer, new object[] { item2 });
        Check(list.Count == 1, "位图所有权：第二条弹幕也入队了（实得 " + list.Count + " 条）");
        if (list.Count >= 1)
        {
            object broken = flyingType.GetField("Handle").GetValue(list[0]);
            flyingType.GetField("X").SetValue(list[0], 100f);
            flyingType.GetField("Y").SetValue(list[0], 60f);
            BitmapOf(broken).Dispose();          // 外部把底层位图弄坏（不该发生，但兜底必须成立）
            using (Bitmap canvas = new Bitmap(1280, 700))
            using (Graphics graphics = Graphics.FromImage(canvas))
            {
                bool threw = false;
                try { draw.Invoke(renderer, new object[] { graphics }); }
                catch (Exception) { threw = true; }
                Check(!threw, "位图所有权：位图真失效时那一帧仍不抛（原来这一下把覆盖层打断）");
                Check(list.Count == 0, "位图所有权：画不出来的那条被摘掉（实得 " + list.Count + " 条）");
            }
        }

        // ---- ⑤b 被丢弃的弹幕不许漏租约（轨道全忙时会频繁走到这条路）----
        // 2026-09-27：所有权改造时 `if (track < 0) { DroppedCount++; return; }` 漏了归还，
        // 而「轨道全忙」在热门房间是常态 —— 每丢一条就漏一张位图。这条就是盯它的。
        rendererType.GetMethod("Resize").Invoke(renderer, new object[] { 320, 70 });   // 轨道压到只有一条
        int liveBeforeFlood = (int)liveCount.GetValue(null, null);
        object flood = Activator.CreateInstance(itemType);
        for (int i = 0; i < 300; i++)
        {
            itemType.GetField("Text").SetValue(flood, "刷屏第" + i + "条");
            rendererType.GetMethod("AddDanmaku").Invoke(renderer, new object[] { flood });
        }
        int dropped = (int)rendererType.GetField("DroppedCount", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(renderer);
        Check(dropped > 0, "位图所有权：轨道全忙时确实会丢弹幕（否则这条是空转，实丢 " + dropped + " 条）");
        // 收尾：把还在飞的清掉（它们手里的租约要还），清空缓存（缓存放手）
        IList flyingList = (IList)flyingField.GetValue(renderer);
        for (int i = 0; i < flyingList.Count; i++)
            flyingType.GetField("X").SetValue(flyingList[i], -10000f);
        advance.Invoke(renderer, new object[] { DateTime.UtcNow.AddSeconds(600), 0.05f });
        clear.Invoke(textCacheField.GetValue(renderer), null);
        Check(flyingList.Count == 0, "位图所有权：收尾时列表已清空（实得 " + flyingList.Count + " 条）");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int liveAfterFlood = (int)liveCount.GetValue(null, null);
        Check(liveAfterFlood <= liveBeforeFlood,
            "位图所有权：被丢弃的弹幕没有漏租约（刷屏前 " + liveBeforeFlood + " → 收尾后 " + liveAfterFlood + "，丢 " + dropped + " 条）");

        // ---- ⑤c 增长必须有界：缓存容量是硬上限，存活数不能随输入线性涨 ----
        // 这条对着「demo 实跑时存活数一路涨」那个现象：涨到容量附近必须平，否则就是漏还。
        object boundCache = Activator.CreateInstance(cacheType);
        int capacity = (int)cacheType.GetField("Capacity", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check(capacity > 0, "位图所有权：读得到文字缓存容量（否则这条是空转，实得 " + capacity + "）");
        int liveBeforeFill = (int)liveCount.GetValue(null, null);
        for (int i = 0; i < capacity + 200; i++)
        {
            object lease = get.Invoke(boundCache, new object[] { "灌" + i, Color.White, 20f, "Microsoft YaHei UI", false, 0 });
            ReleaseShared(lease);        // 借用者当场还，只剩缓存那一份
        }
        int liveAfterFill = (int)liveCount.GetValue(null, null);
        int grew = liveAfterFill - liveBeforeFill;
        Check(grew <= capacity + 20,
            "位图所有权：灌 " + (capacity + 200) + " 张后存活数有界（涨 " + grew + " ≤ 容量 " + capacity + " + 余量）");
        clear.Invoke(boundCache, null);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int liveAfterClear = (int)liveCount.GetValue(null, null);
        Check(liveAfterClear <= liveBeforeFill,
            "位图所有权：清空缓存后全部回收（" + liveBeforeFill + " → " + liveAfterClear + "）—— 没有漏还的租约");

        // ---- ⑥ 没有忘还的租约：全部收尾后存活数回到基线 ----
        clear.Invoke(textCache, null);
        clear.Invoke(textCacheField.GetValue(renderer), null);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int liveAfter = (int)liveCount.GetValue(null, null);
        Check(liveAfter <= liveBefore,
            "位图所有权：测试全程没有漏还的租约（基线 " + liveBefore + " → 现在 " + liveAfter + "）");
    }
}
