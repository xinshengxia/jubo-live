using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterDanmakuRoadmapCases()
    {
        TestRunner.Add("弹幕-完善", "逆向、混排、缩放及高级文字降级", TestRoadmapDirections);
        TestRunner.Add("弹幕-完善", "准入洪水、队列上限和统计守恒", TestRoadmapAdmission);
        TestRunner.Add("弹幕-完善", "排队重复合并与限时释放", TestRoadmapQueuedMerge);
        TestRunner.Add("弹幕-完善", "类型、UID、高亮置顶与正则超时", TestRoadmapRules);
        TestRunner.Add("弹幕-完善", "安全区、独立透明度与描边", TestRoadmapAppearance);
        TestRunner.Add("弹幕-完善", "礼物新旧协议、连击和事件限流", TestRoadmapEvents);
        TestRunner.Add("弹幕-完善", "平台统计原位更新、到期和关闭释放", TestRoadmapStatus);
        TestRunner.Add("弹幕-完善", "文本缓存字节上限与借用寿命", TestRoadmapCacheBudget);
        TestRunner.Add("弹幕-完善", "更多设置离屏缩放与安全区预览", TestRoadmapSettings);
        TestRunner.AddDesktop("弹幕-设置", "更多设置主题、滚动和200%缩放适配", TestSettingsAdaptation);
        TestRunner.AddDesktop("弹幕-设置", "更多设置数字编辑、应用取消和页签键盘", TestSettingsEditing);
    }
    private static long RoadmapCounter(DanmakuScene scene, string field)
    { return Convert.ToInt64(DanmakuScene.Get(scene.Call("GetCounters"), field)); }
    private static object RoadmapItem(string text, int mode)
    {
        object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
        DanmakuScene.Set(item, "Text", text); DanmakuScene.Set(item, "Mode", mode); return item;
    }
    private static void TestRoadmapDirections()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            scene.Add("向左", 1); scene.Add("向右", 6);
            object reverse = scene.Flying[1]; float oldX = DanmakuScene.Number(reverse, "X");
            scene.Step(1f); Check(DanmakuScene.Number(reverse, "X") > oldX, "mode 6 实际向右移动");
            float remaining = FlightRemaining(scene, reverse); scene.Resize(1920, 1080);
            Check(Math.Abs(FlightRemaining(scene, reverse) - remaining) < 0.01f, "逆向缩放保留剩余时长");
            for (int i = 0; i < 240; i++)
            {
                if (i % 20 == 0) scene.Add("混排 " + i, i % 40 == 0 ? 6 : 1);
                scene.Step(1f / 60f); scene.Draw(); AssertDanmakuScene(scene);
            }
            Check(true, "双向混排连续绘制没有矩形相交");
            DanmakuScene.Set(scene.Config, "ShowReverse", false); scene.Configure();
            foreach (object f in scene.Flying) Check((int)DanmakuScene.Get(DanmakuScene.Get(f, "Source"), "Mode") != 6, "关闭逆向同时清理已显示逆向弹幕");
            scene.Add("[0.5,0.5,\"1-1\",4,\"高级内容纯文字\"]", 7);
            object last = scene.Flying[scene.Flying.Count - 1];
            Check((string)DanmakuScene.Get(last, "Label") == "高级内容纯文字", "高级 JSON 只显示文字内容");
            long unsupported = RoadmapCounter(scene, "Unsupported"); scene.Add("script-like text", 8);
            Check(RoadmapCounter(scene, "Unsupported") == unsupported + 1, "脚本模式不执行并有不支持统计");
        }
    }
    private static void TestRoadmapAdmission()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow; DanmakuScene.Set(scene.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
            DanmakuScene.Set(scene.Config, "SmoothDanmaku", true); DanmakuScene.Set(scene.Config, "QueueWaitMs", 800);
            DanmakuScene.Set(scene.Config, "MaxOnScreen", 40); scene.Configure(); int received = 0;
            for (int frame = 0; frame < 300; frame++)
            {
                int count = (frame + 1) * 100 / 30 - frame * 100 / 30;
                for (int i = 0; i < count; i++) scene.Add("合成洪水 " + received++, 1);
                now = now.AddSeconds(1.0 / 30); scene.Call("Advance", now, 1f / 30f);
                if (frame % 5 == 0) scene.Draw(); AssertDanmakuScene(scene);
                if (RoadmapCounter(scene, "Pending") > 64) throw new Exception("短队列越过容量");
            }
            Check(RoadmapCounter(scene, "Received") == 1000 && RoadmapCounter(scene, "Displayed") <= 55, "100 条/秒十秒洪水受平滑准入约束");
            now = now.AddSeconds(2); scene.Call("Advance", now, 2f);
            Check(RoadmapCounter(scene, "Pending") == 0 && RoadmapCounter(scene, "QueueExpired") > 0, "超时消息释放，不积攒旧弹幕");
            long terminal = RoadmapCounter(scene, "Displayed") + RoadmapCounter(scene, "Merged") + RoadmapCounter(scene, "Filtered")
                + RoadmapCounter(scene, "Unsupported") + RoadmapCounter(scene, "NoSpace") + RoadmapCounter(scene, "Density")
                + RoadmapCounter(scene, "QueueFull") + RoadmapCounter(scene, "QueueExpired");
            Check(terminal == 1000, "所有收到消息均有明确准入/合并/舍弃去向");
        }
    }
    private static void TestRoadmapQueuedMerge()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow; DanmakuScene.Set(scene.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
            DanmakuScene.Set(scene.Config, "SmoothDanmaku", true); DanmakuScene.Set(scene.Config, "QueueWaitMs", 800);
            DanmakuScene.Set(scene.Config, "AdmissionPerSecond", 10); DanmakuScene.Set(scene.Config, "BlockDuplicate", true); scene.Configure();
            scene.Add("先来的", 1); for (int i = 0; i < 20; i++) scene.Add("排队合并", 1);
            Check(RoadmapCounter(scene, "Pending") == 1 && RoadmapCounter(scene, "Merged") == 19, "排队期间相同文本只保留一个代表项");
            now = now.AddMilliseconds(150); scene.Call("Advance", now, 0.15f); scene.Draw();
            Check(scene.Flying.Count == 2 && ((string)DanmakuScene.Get(scene.Flying[1], "Label")).EndsWith("×20"), "准入后保留完整排队合并计数");
            AssertDanmakuScene(scene);
        }
    }
    private static void TestRoadmapRules()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockUsers", new string[] { "uid:1001", "name:精确昵称", "re:^合成用户[0-9]+$" });
            object item = RoadmapItem("内容", 1); DanmakuScene.Set(item, "UserId", 1001L);
            Check((bool)scene.Call("IsBlocked", item), "UID 精确匹配");
            DanmakuScene.Set(item, "UserId", 2002L); DanmakuScene.Set(item, "UserName", "精确昵称后缀");
            Check(!(bool)scene.Call("IsBlocked", item), "name: 不误屏蔽昵称前缀相同的其他用户");
            DanmakuScene.Set(item, "UserName", "合成用户12"); Check((bool)scene.Call("IsBlocked", item), "昵称正则可用");
            DanmakuScene.Set(scene.Config, "BlockKeywords", new string[] { "re:(a+)+$" }); scene.Configure();
            DanmakuScene.Set(item, "UserName", "普通用户"); DanmakuScene.Set(item, "Text", new string('a', 20000) + "!");
            Stopwatch watch = Stopwatch.StartNew(); scene.Call("IsBlocked", item); watch.Stop();
            Check(watch.ElapsedMilliseconds < 1000, "病态正则有时间上限，不挂住渲染线程");
            watch.Restart(); scene.Call("IsBlocked", item); watch.Stop(); Check(watch.ElapsedMilliseconds < 40, "超时规则被暂停，后续消息不反复消耗预算");
            DanmakuScene.Set(scene.Config, "BlockKeywords", new string[0]); DanmakuScene.Set(scene.Config, "HighlightKeywords", new string[] { "重点" });
            DanmakuScene.Set(scene.Config, "PinKeywords", new string[] { "置顶" }); scene.Configure();
            scene.Add("重点置顶内容", 1); object f = scene.Flying[0];
            Check((bool)DanmakuScene.Get(f, "Fixed") && ((Color)DanmakuScene.Get(f, "TextColor")).ToArgb() != Color.White.ToArgb(), "重点内容高亮并通过固定弹幕占位");
            DanmakuScene.Set(scene.Config, "ShowColored", false); scene.Configure();
            object colored = RoadmapItem("彩色", 1); DanmakuScene.Set(colored, "Color", 0xFF9900);
            Check((bool)scene.Call("IsBlocked", colored), "彩色弹幕屏蔽可独立开启");
        }
    }
    private static void TestRoadmapAppearance()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "ReservedAreas", new string[] { "0,0,100,15", "35,25,30,50" }); scene.Configure();
            for (int i = 0; i < 35; i++) scene.Add("安全区 " + i, i % 4 == 0 ? 6 : 1);
            for (int frame = 0; frame < 240; frame++)
            {
                scene.Step(1f / 60f); scene.Draw(); AssertDanmakuScene(scene);
                foreach (object f in scene.Flying)
                {
                    RectangleF rect = new RectangleF(DanmakuScene.Number(f, "X"), DanmakuScene.Number(f, "Y"), DanmakuScene.Number(f, "Width"), DanmakuScene.Number(f, "Height"));
                    if (rect.IntersectsWith(new RectangleF(0, 0, 1280, 108)) || rect.IntersectsWith(new RectangleF(448, 180, 384, 360))) throw new Exception("弹幕进入安全区");
                }
            }
            Check(scene.Flying.Count > 0, "双向弹幕不穿过标题和中央主体安全区");
        }
        foreach (string style in new string[] { "outline", "heavy", "shadow" })
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BorderStyle", style); DanmakuScene.Set(scene.Config, "TextOpacityPercent", 50); scene.Configure();
            scene.Add("透明度测试 WW", 5); scene.Draw(); int maxAlpha = 0;
            for (int y = 0; y < 80; y++) for (int x = 0; x < 1280; x++) maxAlpha = Math.Max(maxAlpha, scene.Canvas.GetPixel(x, y).A);
            Check(maxAlpha > 80 && maxAlpha <= 130, style + " 模式支持独立弹幕透明度");
        }
        using (DanmakuScene scene = new DanmakuScene(320, 180))
        {
            DanmakuScene.Set(scene.Config, "ScaleWithWindow", false); scene.Configure(); scene.Add(new string('W', 60), 5);
            Check(scene.Flying.Count == 1 && !(bool)DanmakuScene.Get(scene.Flying[0], "Fixed"), "小窗过长固定留言自动改为滚动");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "TopFixedZonePercent", 30); DanmakuScene.Set(scene.Config, "MaxFixedPerMode", 3); scene.Configure();
            for (int i = 0; i < 15; i++) scene.Add("固定区域 " + i, 5);
            Check(scene.Flying.Count == 3, "固定弹幕有独立条数限制");
            foreach (object f in scene.Flying) if (DanmakuScene.Number(f, "Y") + DanmakuScene.Number(f, "Height") > 216f) throw new Exception("顶部固定越过分区");
            Check(true, "顶部固定弹幕遵守区域百分比");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "MaxOnScreen", 6); scene.Configure();
            for (int i = 0; i < 6; i++) scene.Add("密度缩小 " + i, 1);
            Check(DanmakuScene.Number(scene.Flying[5], "FontSize") < DanmakuScene.Number(scene.Flying[0], "FontSize")
                && RoadmapCounter(scene, "Shrunk") > 0, "拥挤时普通弹幕适当缩小并计数");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            const string url = "fixture:platform-large-emote";
            scene.Image(url, 200, 100);
            object normal = RoadmapItem("普通表情", 1); DanmakuScene.Set(normal, "EmoticonUrl", url); DanmakuScene.Set(normal, "IsEmoticon", true);
            scene.Call("AddDanmaku", normal);
            object large = RoadmapItem("平台大表情", 1); DanmakuScene.Set(large, "EmoticonUrl", url); DanmakuScene.Set(large, "IsEmoticon", true); DanmakuScene.Set(large, "LargeEmoticon", true);
            scene.Call("AddDanmaku", large);
            Check(DanmakuScene.Number(scene.Flying[1], "EmoteBox") > DanmakuScene.Number(scene.Flying[0], "EmoteBox"), "平台大表情标记增高，普通图片仍保持小尺寸");
            for (int i = 0; i < 90; i++) { scene.Step(1f / 60f); AssertDanmakuScene(scene); }
            DanmakuScene.Set(scene.Config, "EmoteSizeMode", "small"); scene.Configure();
            Check(Math.Abs(DanmakuScene.Number(scene.Flying[1], "EmoteBox") - DanmakuScene.Number(scene.Flying[0], "EmoteBox")) < 0.01f, "小表情设置同时收敛大表情并重新排版");
        }
    }
    private static object _roadmapEvent;
    private static void CaptureRoadmapEvent(object item) { _roadmapEvent = item; }
    private static object RoadmapEvent(string kind, long uid, string id, int count, bool cumulative)
    {
        object item = Activator.CreateInstance(DanmakuType("LiveEvent"));
        DanmakuScene.Set(item, "Kind", Enum.Parse(DanmakuType("LiveEventKind"), kind));
        DanmakuScene.Set(item, "UserId", uid); DanmakuScene.Set(item, "UserName", "合成观众");
        DanmakuScene.Set(item, "GiftName", "合成礼物"); DanmakuScene.Set(item, "GiftId", "7"); DanmakuScene.Set(item, "ComboId", "synthetic-combo");
        DanmakuScene.Set(item, "EventId", id); DanmakuScene.Set(item, "Count", count); DanmakuScene.Set(item, "Cumulative", cumulative); return item;
    }
    private static void TestRoadmapEvents()
    {
        Type bili = DanmakuType("BiliDanmakuClient"); object client = Activator.CreateInstance(bili, new object[] { "6", null });
        EventInfo source = bili.GetEvent("LiveEventReceived");
        Delegate handler = Delegate.CreateDelegate(source.EventHandlerType, typeof(HardeningTests).GetMethod("CaptureRoadmapEvent", BindingFlags.Static | BindingFlags.NonPublic));
        source.AddEventHandler(client, handler);
        string gift = "{\"cmd\":\"SEND_GIFT\",\"data\":{\"uid\":101,\"uname\":\"合成观众\",\"giftId\":7,\"giftName\":\"合成礼物\",\"num\":2,\"tid\":\"synthetic-event\"}}";
        bili.GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[] { gift });
        Check(_roadmapEvent != null && (int)DanmakuScene.Get(_roadmapEvent, "Count") == 2, "真实分发入口发出类型化送礼事件");
        _roadmapEvent = null;
        string pb = Convert.ToBase64String(new byte[] { 8, 101, 18, 4, 116, 101, 115, 116, 40, 2 });
        bili.GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
            new object[] { "{\"cmd\":\"INTERACT_WORD_V2\",\"data\":{\"pb\":\"" + pb + "\"}}" });
        Check(_roadmapEvent != null && DanmakuScene.Get(_roadmapEvent, "Kind").ToString() == "Follow", "新版 protobuf 互动消息映射为关注");
        byte[] giftBytes = new byte[] { 8, 101, 18, 4, 116, 101, 115, 116, 82, 14, 8, 7, 18, 4, 82, 111, 115, 101, 24, 3, 74, 2, 105, 100 };
        _roadmapEvent = null;
        bili.GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
            new object[] { "{\"cmd\":\"SEND_GIFT_V2\",\"data\":{\"pb\":\"" + Convert.ToBase64String(giftBytes) + "\"}}" });
        Check(_roadmapEvent != null && (string)DanmakuScene.Get(_roadmapEvent, "GiftName") == "Rose"
            && (int)DanmakuScene.Get(_roadmapEvent, "Count") == 3, "新版 protobuf 礼物列表解析名称和数量");
        source.RemoveEventHandler(client, handler); _roadmapEvent = null;
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "ShowGift", true); DanmakuScene.Set(scene.Config, "ShowGuardEvents", true);
            DanmakuScene.Set(scene.Config, "ReservedAreas", new string[] { "0,0,100,12" }); scene.Configure(); scene.Sc(1);
            scene.Call("AddLiveEvent", RoadmapEvent("Gift", 101, "delta-a", 1, false));
            scene.Call("AddLiveEvent", RoadmapEvent("Gift", 101, null, 1, true));
            scene.Call("AddLiveEvent", RoadmapEvent("Gift", 101, "delta-b", 2, false));
            scene.Call("AddLiveEvent", RoadmapEvent("Gift", 101, null, 3, true));
            scene.Call("AddLiveEvent", RoadmapEvent("Gift", 101, "delta-a", 1, false));
            IList active = (IList)DanmakuScene.Get(scene.Renderer, "_events");
            int count = (int)active[0].GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(active[0], null);
            Check(active.Count == 1 && count == 3, "送礼增量、连击累计和事务重发不重复加数");
            for (int i = 0; i < 200; i++) scene.Call("AddLiveEvent", RoadmapEvent("Gift", 1000 + i, "flood-" + i, 1, false));
            Check(active.Count <= 3 && Convert.ToInt64(DanmakuScene.Get(scene.Renderer, "EventDropped")) > 0, "事件洪水有条数及速率上限");
            for (int i = 0; i < 12; i++) scene.Add("事件避让 " + i, 1);
            scene.Step(1.5f); scene.Draw(); RectangleF reserved = (RectangleF)DanmakuScene.Get(scene.Renderer, "_eventReserved");
            scene.Canvas.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "danmaku-events-preview.png"), ImageFormat.Png);
            Check(!reserved.IsEmpty && reserved.Top >= 86, "事件提示避开手工标题安全区");
            foreach (object f in scene.Flying)
                if (reserved.IntersectsWith(new RectangleF(DanmakuScene.Number(f, "X"), DanmakuScene.Number(f, "Y"), DanmakuScene.Number(f, "Width"), DanmakuScene.Number(f, "Height")))) throw new Exception("弹幕进入事件提示区域");
            Check(scene.Call("GetCounters") != null, "事件通道与普通弹幕统计独立可读");
            DanmakuScene.Set(scene.Config, "ShowGift", false); scene.Configure(); scene.Draw();
            Check(((IList)DanmakuScene.Get(scene.Renderer, "_events")).Count == 0, "关闭事件后立即释放提示占位");
        }
    }
    private static void TestRoadmapStatus()
    {
        Type bili = DanmakuType("BiliDanmakuClient"); object client = Activator.CreateInstance(bili, new object[] { "6", null });
        EventInfo source = bili.GetEvent("LiveEventReceived");
        Delegate handler = Delegate.CreateDelegate(source.EventHandlerType, typeof(HardeningTests).GetMethod("CaptureRoadmapEvent", BindingFlags.Static | BindingFlags.NonPublic));
        source.AddEventHandler(client, handler); _roadmapEvent = null;
        bili.GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client,
            new object[] { "{\"cmd\":\"WATCHED_CHANGE\",\"data\":{\"num\":1234,\"text_large\":\"1234人看过\"}}" });
        Check(_roadmapEvent != null && DanmakuScene.Get(_roadmapEvent, "Kind").ToString() == "Status"
            && (string)DanmakuScene.Get(_roadmapEvent, "Topic") == "watched", "平台人数消息进入独立状态通道");
        source.RemoveEventHandler(client, handler);
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow; DanmakuScene.Set(scene.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
            scene.Call("AddLiveEvent", _roadmapEvent); scene.Draw();
            IDictionary statuses = (IDictionary)DanmakuScene.Get(scene.Renderer, "_liveStatus");
            Check(statuses.Count == 0, "默认不额外占用画面显示平台统计");
            DanmakuScene.Set(scene.Config, "ShowLiveStats", true); scene.Configure();
            for (int i = 0; i < 100; i++) scene.Call("AddLiveEvent", _roadmapEvent);
            scene.Draw();
            Check(statuses.Count == 1 && ((IList)DanmakuScene.Get(scene.Renderer, "_events")).Count == 0, "同主题统计原位更新，不挤占礼物条数");
            Check(!((RectangleF)DanmakuScene.Get(scene.Renderer, "_eventReserved")).IsEmpty, "统计条参与布局占位");
            now = now.AddSeconds(31); scene.Call("Advance", now, 31f); scene.Draw();
            Check(statuses.Count == 0 && ((RectangleF)DanmakuScene.Get(scene.Renderer, "_eventReserved")).IsEmpty, "统计失去更新三十秒后释放占位");
            scene.Call("AddLiveEvent", _roadmapEvent); DanmakuScene.Set(scene.Config, "ShowLiveStats", false); scene.Configure(); scene.Draw();
            Check(statuses.Count == 0, "关闭平台统计立即释放状态");
        }
        _roadmapEvent = null;
    }
    private static void TestRoadmapCacheBudget()
    {
        Type type = DanmakuType("DanmakuTextCache"); object cache = Activator.CreateInstance(type); object held = null;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                object handle = type.GetMethod("Get").Invoke(cache, new object[] { new string('W', 200), Color.FromArgb(40 + i, 150, 200), 64f, "Arial", true, 1 });
                if (i == 0) held = handle; else ReleaseShared(handle);
            }
            Check(Convert.ToInt64(DanmakuScene.Get(cache, "_cacheBytes")) <= 32L * 1024 * 1024 && BitmapOf(held).Width > 0,
                "文本缓存受字节预算限制，淘汰后在飞租约仍可画");
            type.GetMethod("Clear").Invoke(cache, null);
            Check(Convert.ToInt64(DanmakuScene.Get(cache, "_cacheBytes")) == 0 && BitmapOf(held).Width > 0, "清缓存正确清零字节数，不释放借用中的位图");
            object oversized = type.GetMethod("Get").Invoke(cache, new object[] { new string('W', 200), Color.White, 600f, "Arial", true, 1 });
            try {
                bool cached = (bool)oversized.GetType().GetProperty("Cached", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(oversized, null);
                Check(!cached && BitmapOf(oversized).Width > 0, "超大单条直接退出缓存，但返回的借用句柄仍有效");
            } finally { ReleaseShared(oversized); }
        }
        finally { if (held != null) ReleaseShared(held); type.GetMethod("Clear").Invoke(cache, null); }
    }
    private static void TestRoadmapSettings()
    {
        object config = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
        using (Form form = (Form)CallStatic(DanmakuType("DanmakuFeatureSettings"), "BuildPreview", config))
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
            form.Show(); Application.DoEvents();
            foreach (int dpi in new int[] { 96, 144, 192, 96 })
            {
                DanmakuType("DpiForm").GetMethod("ApplyDpi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { dpi, false, null });
                foreach (Control control in form.Controls)
                    if (control.Dock == DockStyle.Bottom)
                    {
                        if (!control.Visible || control.Bottom > form.ClientSize.Height) throw new Exception("更多设置底部条越过客户区");
                        foreach (Control sibling in form.Controls)
                            if (sibling is TabControl && sibling.Bounds.IntersectsWith(control.Bounds)) throw new Exception("标签内容盖住底部按钮条");
                        foreach (Control button in control.Controls)
                            if (!button.Visible || button.Bottom > control.Height || button.Right > control.Width) throw new Exception("更多设置底部按钮越界");
                    }
            }
            using (Bitmap image = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height)); image.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "danmaku-more-settings.png"), ImageFormat.Png); }
            Check(true, "更多设置无前台窗口情况下完成多 DPI 布局和渲染");
            form.Hide();
        }
        Type canvasType = DanmakuType("DanmakuSafeAreaCanvas");
        using (Control canvas = (Control)Activator.CreateInstance(canvasType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new string[] { "0,0,100,8", "35,20,30,60" } }, null))
        {
            canvas.Size = new Size(800, 450);
            using (Bitmap image = new Bitmap(800, 450))
            { canvas.DrawToBitmap(image, new Rectangle(0, 0, 800, 450)); image.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "danmaku-safe-areas.png"), ImageFormat.Png); }
            string[] areas = (string[])canvasType.GetMethod("GetAreas", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, null);
            Check(areas.Length == 2, "安全区编辑器保留标准化百分比矩形");
        }
    }
}
