using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

internal static partial class HardeningTests
{
    private sealed class DanmakuScene : IDisposable
    {
        internal readonly object Config, Renderer;
        internal readonly Type RendererType;
        internal readonly Bitmap Canvas;
        internal readonly Graphics Graphics;
        internal int Width, Height;
        internal DanmakuScene(int width, int height)
        {
            Config = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
            Set(Config, "BlockDuplicate", false);
            Set(Config, "SmoothDanmaku", false);
            Set(Config, "QueueWaitMs", 0);
            Set(Config, "AntiBlockSubtitle", false);
            Set(Config, "ScShowAvatar", false);
            RendererType = DanmakuType("DanmakuRenderer");
            Renderer = Activator.CreateInstance(RendererType, new object[] { Config });
            Canvas = new Bitmap(1280, 720, PixelFormat.Format32bppPArgb);
            Graphics = Graphics.FromImage(Canvas);
            Resize(width, height);
        }
        internal static object Get(object obj, string field)
        { return obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(obj); }
        internal static void Set(object obj, string field, object value)
        { obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(obj, value); }
        internal static float Number(object obj, string field) { return Convert.ToSingle(Get(obj, field)); }
        internal object Call(string method, params object[] args)
        { return RendererType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(Renderer, args); }
        internal IList Flying { get { return (IList)Get(Renderer, "_flying"); } }
        internal object Add(string text, int mode)
        {
            object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
            Set(item, "Text", text); Set(item, "Mode", mode); Call("AddDanmaku", item); return item;
        }
        internal void Resize(int width, int height) { Width = width; Height = height; Call("Resize", width, height); }
        internal void Configure() { Call("ApplyConfig"); Resize(Width, Height); }
        internal void Draw() { Graphics.Clear(Color.Transparent); Call("Draw", Graphics); }
        internal void Step(float seconds)
        {
            // 推进模拟时间，同时保持生产代码基于 UTC 的固定弹幕剩余寿命一致。
            foreach (object f in Flying)
                if ((bool)Get(f, "Fixed")) Set(f, "FixedUntil", ((DateTime)Get(f, "FixedUntil")).AddSeconds(-seconds));
            Call("Advance", DateTime.UtcNow, seconds);
        }
        internal void Image(string url, int width, int height)
        {
            object cache = Get(Renderer, "_imageCache");
            Bitmap image = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.MediumSeaGreen);
            cache.GetType().GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(cache, new object[] { url, image });
        }
        internal void Sc(long id)
        {
            object sc = Activator.CreateInstance(DanmakuType("SuperChatItem"));
            Set(sc, "Id", id); Set(sc, "UserName", "示例观众"); Set(sc, "Message", "合成测试留言，弹幕会避开卡片");
            Set(sc, "Price", 30); Set(sc, "DurationSec", 60); Call("AddSuperChat", sc);
        }
        public void Dispose() { ((IDisposable)Renderer).Dispose(); Graphics.Dispose(); Canvas.Dispose(); }
    }

    private static void AssertDanmakuScene(DanmakuScene scene)
    {
        RectangleF sc = (RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved");
        float area = DanmakuScene.Number(scene.Renderer, "_areaHeight");
        for (int i = 0; i < scene.Flying.Count; i++)
        {
            object a = scene.Flying[i];
            RectangleF ra = new RectangleF(DanmakuScene.Number(a, "X"), DanmakuScene.Number(a, "Y"),
                DanmakuScene.Number(a, "Width"), DanmakuScene.Number(a, "Height"));
            if (ra.Top < -0.01f || ra.Bottom > area + 0.01f
                || ((bool)DanmakuScene.Get(a, "Fixed") && !sc.IsEmpty && ra.IntersectsWith(sc)))
                throw new Exception("弹幕越出显示区或固定弹幕进入 SC：" + ra + " / SC " + sc + " / area " + area);
            for (int j = i + 1; j < scene.Flying.Count; j++)
            {
                object b = scene.Flying[j];
                RectangleF rb = new RectangleF(DanmakuScene.Number(b, "X"), DanmakuScene.Number(b, "Y"),
                    DanmakuScene.Number(b, "Width"), DanmakuScene.Number(b, "Height"));
                if (ra.IntersectsWith(rb)) throw new Exception("弹幕相交：" + ra + " / " + rb);
            }
        }
    }

    private static void RegisterDanmakuLayoutCases()
    {
        RegisterDanmakuMergeCases();
        RegisterDanmakuRoadmapCases();
        TestRunner.Add("弹幕-渲染", "SC 四角局部遮挡与固定弹幕避让", TestDanmakuScPlacement);
        TestRunner.Add("弹幕-渲染", "顶部四分之一区域与 SC 持续共存", TestDanmakuScNarrowArea);
        TestRunner.Add("弹幕-渲染", "SC 部分遮挡保留全部滚动轨道和密集准入", TestDanmakuScPartialDensity);
        TestRunner.Add("弹幕-渲染", "窗口缩放与热设置保持穿屏进度", TestDanmakuResizePlacement);
        TestRunner.Add("弹幕-渲染", "表情异步变宽、比例与低行距", TestDanmakuImagePlacement);
        TestRunner.Add("弹幕-渲染", "重复计数合并、寿命与满屏准入", TestDanmakuRepeatPlacement);
        TestRunner.Add("弹幕-渲染", "合成场景离屏渲染预览", TestDanmakuLayoutPreview);
    }

    private static void TestDanmakuPlacement()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            scene.Add("先到的顶部", 5); scene.Add("第二条顶部", 5); scene.Add("底部固定", 4);
            object first = scene.Flying[0]; float firstY = DanmakuScene.Number(first, "Y");
            DanmakuScene.Set(first, "FixedUntil", DateTime.UtcNow.AddSeconds(-1));
            scene.Call("Advance", DateTime.UtcNow, 0f); scene.Add("复用已释放的位置", 5);
            Check(Math.Abs(DanmakuScene.Number(scene.Flying[scene.Flying.Count - 1], "Y") - firstY) < 0.01f,
                "固定弹幕复用空槽，不持续往屏幕外堆叠");
            int accepted = 0;
            for (int frame = 0; frame < 720; frame++)
            {
                if (frame % 8 == 0) { scene.Add("混合弹幕 " + frame, frame % 3 == 0 ? 5 : frame % 5 == 0 ? 4 : 1); accepted++; }
                scene.Step(1f / 60f);
                AssertDanmakuScene(scene);
            }
            Check(scene.Flying.Count > 0 && accepted > 50, "混合滚动/顶部/底部连续 720 帧无矩形相交");
            int count = scene.Flying.Count;
            for (int i = 0; i < 100; i++) scene.Add("固定突发 " + i, 5);
            AssertDanmakuScene(scene);
            Check(scene.Flying.Count < count + 100, "固定弹幕溢出时受显示区域限制");
        }
    }

    private static void TestDanmakuScPlacement()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            foreach (string corner in new string[] { "tl", "tr", "bl", "br" })
            foreach (string mode in new string[] { "full", "compact", "badge" })
            {
                DanmakuScene.Set(scene.Config, "ScCorner", corner);
                DanmakuScene.Set(scene.Config, "ScMode", mode);
                DanmakuScene.Set(scene.Config, "ScClickable", false);
                scene.Configure(); scene.Sc(1); scene.Draw();
                float height = ((RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved")).Height;
                Check(height > 0f && height < scene.Height / 2f, "SC " + corner + "/" + mode + " 只预留实际可见高度，关闭点击仍避让");
                for (int frame = 0; frame < 180; frame++)
                {
                    if (frame % 12 == 0) scene.Add("SC 避让 " + corner + mode + frame, frame % 24 == 0 ? 5 : 1);
                    scene.Step(1f / 30f);
                    scene.Draw(); AssertDanmakuScene(scene);
                }
            }
            DanmakuScene.Set(scene.Config, "ScMode", "full");
            DanmakuScene.Set(scene.Config, "ScMaxStack", 2); scene.Configure();
            for (int id = 2; id <= 8; id++) scene.Sc(id);
            scene.Call("ScrollSuperChats", -120); scene.Draw(); AssertDanmakuScene(scene);
            Check((bool)scene.RendererType.GetProperty("SuperChatScrollable").GetValue(scene.Renderer, null), "SC 超阈值后滚动仍可用");
            scene.Call("ClearSuperChats"); scene.Draw();
            Check(((RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved")).IsEmpty, "清空 SC 后释放占位");
            Check(((IList)DanmakuScene.Get(scene.Renderer, "_scHits")).Count == 0, "清空 SC 后不留下透明点击区域");
            scene.Sc(9); DanmakuScene.Set(scene.Config, "ScMode", "off"); scene.Configure(); scene.Draw();
            Check(((RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved")).IsEmpty, "关闭 SC 后释放占位");
            DanmakuScene.Set(scene.Config, "ScMode", "full"); scene.Configure();
            foreach (object sc in (IList)DanmakuScene.Get(scene.Renderer, "_superChats"))
                DanmakuScene.Set(sc, "ExpiresAt", DateTime.UtcNow.AddSeconds(-1));
            scene.Step(0f); scene.Draw();
            Check(((RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved")).IsEmpty, "SC 到期后释放占位");
        }
    }

    private static void TestDanmakuScNarrowArea()
    {
        foreach (string corner in new string[] { "tl", "tr" })
        foreach (int mode in new int[] { 1, 6 })
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow;
            DanmakuScene.Set(scene.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
            DanmakuScene.Set(scene.Config, "DisplayAreaPercent", 25);
            DanmakuScene.Set(scene.Config, "FontScalePercent", 80);
            DanmakuScene.Set(scene.Config, "ScCorner", corner);
            DanmakuScene.Set(scene.Config, "ScShowAvatar", true);
            scene.Configure(); scene.Add("SC 到达前已经在飞的弹幕", mode); scene.Step(1.5f);
            int before = scene.Flying.Count;
            scene.Sc(1);
            object sc = ((IList)DanmakuScene.Get(scene.Renderer, "_superChats"))[0];
            DanmakuScene.Set(sc, "Message", new string('测', 140));
            scene.Draw();
            RectangleF reserved = (RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved");
            Check(reserved.Bottom >= DanmakuScene.Number(scene.Renderer, "_areaHeight"), "合成 SC 覆盖整个弹幕高度 " + corner);
            Check(before > 0 && scene.Flying.Count == before, "SC 到达不清空在飞弹幕 " + corner + "/" + mode);
            scene.Add("SC 出现后新到的弹幕", mode);
            Check(scene.Flying.Count > before, "SC 存续时继续接收普通弹幕 " + corner + "/" + mode);

            bool visible = false;
            for (int frame = 0; frame < 120; frame++)
            {
                scene.Step(1f / 30f); scene.Draw();
                if (frame % 10 != 0) continue;
                int pixels = 0;
                for (int y = 0; y < 180; y += 2)
                for (int x = 0; x < scene.Width; x += 2)
                    if (!reserved.Contains(x, y) && scene.Canvas.GetPixel(x, y).A > 0) pixels++;
                visible |= pixels > 30;
            }
            Check(visible, "SC 持续显示时卡片以外有真实弹幕像素 " + corner + "/" + mode);
            if (corner == "tl" && mode == 1)
            using (Bitmap preview = new Bitmap(scene.Width, scene.Height))
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(Color.FromArgb(31, 44, 60));
                graphics.DrawImageUnscaled(scene.Canvas, 0, 0);
                preview.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "sc-narrow-area.png"), ImageFormat.Png);
            }

            // 强制让一条弹幕横跨卡片边缘，比较有无弹幕时的 SC 像素。
            object flying = scene.Flying[0];
            float width = DanmakuScene.Number(flying, "Width");
            DanmakuScene.Set(flying, "X", corner == "tl" ? reserved.Right - width / 2f : reserved.Left - width / 2f);
            scene.Draw();
            using (Bitmap withDanmaku = (Bitmap)scene.Canvas.Clone())
            {
                foreach (object item in scene.Flying) DanmakuScene.Set(item, "X", -10000f);
                scene.Draw();
                bool clean = true;
                for (int y = Math.Max(0, (int)Math.Ceiling(reserved.Top)); y < Math.Min(scene.Height, reserved.Bottom); y += 2)
                for (int x = Math.Max(0, (int)Math.Ceiling(reserved.Left)); x < Math.Min(scene.Width, reserved.Right); x += 2)
                    clean &= withDanmaku.GetPixel(x, y) == scene.Canvas.GetPixel(x, y);
                Check(clean, "SC 正文、透明圆角和进度条不被弹幕覆盖 " + corner + "/" + mode);
            }
            scene.Call("ClearSuperChats"); scene.Draw(); scene.Add("SC 结束后继续显示", mode);
            Check(((RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved")).IsEmpty && scene.Flying.Count > 0,
                "SC 清除后释放遮挡区域并继续准入 " + corner + "/" + mode);
        }
    }

    private static void TestDanmakuScPartialDensity()
    {
        foreach (string corner in new string[] { "tl", "tr" })
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow;
            DanmakuScene.Set(scene.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
            DanmakuScene.Set(scene.Config, "DisplayAreaPercent", 25);
            DanmakuScene.Set(scene.Config, "FontScalePercent", 80);
            DanmakuScene.Set(scene.Config, "ScCorner", corner); scene.Configure();
            int tracks = (int)DanmakuScene.Get(scene.Renderer, "_trackCount");
            for (int i = 0; i < tracks; i++) scene.Add("密集弹幕轨道 " + i, 1);
            scene.Step(2f);
            object[] active = new object[scene.Flying.Count]; scene.Flying.CopyTo(active, 0);
            float[] positions = new float[active.Length];
            for (int i = 0; i < active.Length; i++) positions[i] = DanmakuScene.Number(active[i], "Y");
            Check(active.Length >= 4, "SC 到达前覆盖多条滚动轨道 " + corner);
            scene.Sc(1); scene.Draw();
            RectangleF reserved = (RectangleF)DanmakuScene.Get(scene.Renderer, "_scReserved");
            Check(reserved.Bottom < DanmakuScene.Number(scene.Renderer, "_areaHeight"), "SC 只遮挡部分轨道 " + corner);
            Check(scene.Flying.Count == active.Length, "部分遮挡不删弹幕、不挤到剩余一两行 " + corner);
            for (int i = 0; i < active.Length; i++)
                Check(scene.Flying.Contains(active[i]) && DanmakuScene.Number(active[i], "Y") == positions[i],
                    "SC 到达保持原轨道 " + corner + "/" + i);
            if (corner == "tl")
            using (Bitmap preview = new Bitmap(scene.Width, scene.Height))
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(Color.FromArgb(31, 44, 60)); graphics.DrawImageUnscaled(scene.Canvas, 0, 0);
                preview.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "sc-dense-area.png"), ImageFormat.Png);
            }
        }
        foreach (string corner in new string[] { "tl", "tr" })
        using (DanmakuScene baseline = new DanmakuScene(1280, 720))
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DateTime now = DateTime.UtcNow;
            foreach (DanmakuScene target in new DanmakuScene[] { baseline, scene })
            {
                DanmakuScene.Set(target.Renderer, "Clock", new Func<DateTime>(delegate { return now; }));
                DanmakuScene.Set(target.Config, "DisplayAreaPercent", 25);
                DanmakuScene.Set(target.Config, "FontScalePercent", 80);
                DanmakuScene.Set(target.Config, "ScCorner", corner);
                DanmakuScene.Set(target.Config, "SmoothDanmaku", true);
                DanmakuScene.Set(target.Config, "QueueWaitMs", 800); target.Configure();
            }
            bool sameDensity = true;
            for (int frame = 0; frame < 360; frame++)
            {
                now = now.AddSeconds(1f / 30f);
                if (frame == 45 || frame == 240) scene.Sc(frame);
                if (frame == 180) scene.Call("ClearSuperChats");
                if (frame % 3 == 0)
                {
                    baseline.Add("连续密集弹幕 " + frame, 1); scene.Add("连续密集弹幕 " + frame, 1);
                }
                baseline.Call("Advance", now, 1f / 30f); scene.Call("Advance", now, 1f / 30f);
                baseline.Draw(); scene.Draw();
                sameDensity &= baseline.Flying.Count == scene.Flying.Count;
                AssertDanmakuScene(scene);
            }
            Check(sameDensity, "SC 到达、清除、再次到达的 360 帧准入量与无 SC 一致 " + corner);
            object expected = baseline.Call("GetCounters"), actual = scene.Call("GetCounters");
            Check((long)DanmakuScene.Get(expected, "Displayed") == (long)DanmakuScene.Get(actual, "Displayed")
                && (long)DanmakuScene.Get(expected, "NoSpace") == (long)DanmakuScene.Get(actual, "NoSpace")
                && (long)DanmakuScene.Get(actual, "ReflowRemoved") == 0,
                "SC 不额外增加空间丢弃或重排删除 " + corner);
        }
    }

    private static void TestDanmakuResizePlacement()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "ScaleWithWindow", false); scene.Configure();
            scene.Add("只改变窗口宽度，也应更新速度", 1);
            scene.Call("Advance", DateTime.UtcNow, 2f);
            object first = scene.Flying[0]; float oldWidth = DanmakuScene.Number(first, "Width");
            float progress = (1280f - DanmakuScene.Number(first, "X")) / (1280f + oldWidth);
            scene.Resize(1920, 720);
            float width = DanmakuScene.Number(first, "Width");
            Check(Math.Abs(DanmakuScene.Number(first, "Speed") - (1920f + width) / 8f) < 0.01f,
                "宽度变化且字号不变时，旧弹幕按新窗口重算速度");
            Check(Math.Abs((1920f - DanmakuScene.Number(first, "X")) / (1920f + width) - progress) < 0.001f,
                "窗口缩放保留穿屏进度");
            DanmakuScene.Set(scene.Config, "SpeedPercent", 170); DanmakuScene.Set(scene.Config, "FontScalePercent", 170);
            DanmakuScene.Set(scene.Config, "DisplayAreaPercent", 25); scene.Configure();
            Check(Math.Abs(DanmakuScene.Number(first, "Speed") - (1920f + DanmakuScene.Number(first, "Width")) / (8f / 1.7f)) < 0.1f,
                "热修改速度和字号立即作用于在飞弹幕");
            for (int frame = 0; frame < 360; frame++)
            {
                if (frame % 9 == 0) scene.Add("缩放后新弹幕 " + frame, frame % 18 == 0 ? 5 : 1);
                if (frame == 60) scene.Resize(960, 540);
                if (frame == 180) scene.Resize(3840, 2160);
                scene.Step(1f / 60f); AssertDanmakuScene(scene);
            }
            Check(scene.Flying.Count > 0, "缩小、4K、170% 字号、1/4 显示区混排无相交");
        }
    }

    private static void TestDanmakuImagePlacement()
    {
        foreach (int height in new int[] { 1080, 2160 })
        using (DanmakuScene scene = new DanmakuScene(height * 16 / 9, height))
        {
            DanmakuScene.Set(scene.Config, "FontScalePercent", 170); DanmakuScene.Set(scene.Config, "LineGapPercent", 60);
            scene.Configure();
            const string url = "fixture:wide-emote";
            object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
            DanmakuScene.Set(item, "Text", "表情测试"); DanmakuScene.Set(item, "IsEmoticon", true);
            DanmakuScene.Set(item, "EmoticonUrl", url);
            DanmakuScene.Set(item, "EmoticonWidth", 60); DanmakuScene.Set(item, "EmoticonHeight", 120);
            scene.Call("AddDanmaku", item); object first = scene.Flying[0];
            scene.Call("Advance", DateTime.UtcNow, 1.5f); scene.Add("后续弹幕", 1);
            scene.Image(url, 240, 60); scene.Draw();
            Check(Math.Abs(DanmakuScene.Number(first, "EmoteWidth") / DanmakuScene.Number(first, "EmoteBox") - 4f) < 0.01f,
                height + "p 非方形表情到位后保持真实宽高比");
            Check(DanmakuScene.Number(first, "Height") <= DanmakuScene.Number(scene.Renderer, "_trackHeight") + 0.1f,
                height + "p 表情不撑破低行距轨道");
            scene.Image("fixture:tall-emote", 60, 120);
            object tall = Activator.CreateInstance(DanmakuType("DanmakuItem"));
            DanmakuScene.Set(tall, "Text", "竖图测试"); DanmakuScene.Set(tall, "IsEmoticon", true);
            DanmakuScene.Set(tall, "EmoticonUrl", "fixture:tall-emote"); scene.Call("AddDanmaku", tall);
            object last = scene.Flying[scene.Flying.Count - 1];
            Check(Math.Abs(DanmakuScene.Number(last, "EmoteWidth") / DanmakuScene.Number(last, "EmoteBox") - 0.5f) < 0.01f,
                height + "p 竖图也保持原始比例");
            for (int frame = 0; frame < 480; frame++)
            { scene.Step(1f / 60f); AssertDanmakuScene(scene); }
            Check(true, height + "p 图片异步变宽后与后续弹幕不追尾");
        }
    }

    private static void TestDanmakuRepeatPlacement()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true); DanmakuScene.Set(scene.Config, "MaxOnScreen", 1);
            scene.Configure(); scene.Add("重复内容", 1); object first = scene.Flying[0];
            DateTime created = (DateTime)DanmakuScene.Get(first, "CreatedAt");
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < 500; i++) scene.Add("重复内容", 1);
            scene.Draw(); watch.Stop();
            Check(scene.Flying.Count == 1 && (string)DanmakuScene.Get(first, "Label") == "重复内容 ×501", "满屏后仍合并计数，每帧一次更新位图");
            Check((DateTime)DanmakuScene.Get(first, "CreatedAt") == created, "合并不延长首条寿命");
            Console.WriteLine("MEASURE: 500 duplicate admissions + draw = " + watch.ElapsedMilliseconds + " ms");
            scene.Call("Advance", DateTime.UtcNow, 10f); scene.Add("重复内容", 1);
            Check(scene.Flying.Count == 1 && (int)DanmakuScene.Get(scene.Flying[0], "RepeatCount") == 1,
                "离屏后同文案可再次出现，不被旧去重记录吞掉");
            scene.Draw(); AssertDanmakuScene(scene);
        }
    }

    private static void TestDanmakuLayoutPreview()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true);
            DanmakuScene.Set(scene.Config, "DisplayAreaPercent", 50);
            DanmakuScene.Set(scene.Config, "AntiBlockSubtitle", true); scene.Configure();
            scene.Sc(1); scene.Add("固定留言也会让出空间", 5);
            string[] labels = { "这段很精彩", "慢慢看，不着急", "清晰一点舒服多了", "每条弹幕留出间距", "保持画面整洁" };
            for (int i = 0; i < labels.Length; i++) scene.Add(labels[i], 1);
            for (int i = 0; i < 11; i++) scene.Add(labels[0], 1);
            scene.Step(2.4f); scene.Draw(); AssertDanmakuScene(scene);
            using (Bitmap preview = new Bitmap(1280, 800))
            using (Graphics g = Graphics.FromImage(preview))
            using (Font title = new Font("Microsoft YaHei UI", 20f, GraphicsUnit.Pixel))
            using (Font small = new Font("Microsoft YaHei UI", 14f, GraphicsUnit.Pixel))
            using (SolidBrush panel = new SolidBrush(Color.FromArgb(31, 44, 60)))
            using (Pen grid = new Pen(Color.FromArgb(40, 58, 76)))
            {
                g.Clear(Color.FromArgb(17, 27, 40));
                g.DrawString("弹幕布局离线预览 · 合成数据", title, Brushes.White, 24f, 15f);
                g.FillRectangle(panel, 0, 60, 1280, 720);
                for (int x = 0; x < 1280; x += 80) g.DrawLine(grid, x, 60, x, 780);
                for (int y = 60; y < 780; y += 80) g.DrawLine(grid, 0, y, 1280, y);
                g.DrawImageUnscaled(scene.Canvas, 0, 60);
                g.DrawString("弹幕使用上半屏；底部画面留白。SC 仅预留实际可见区域。", small, Brushes.LightSlateGray, 24f, 744f);
                string path = Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "danmaku-preview.png");
                preview.Save(path, ImageFormat.Png);
                Check(File.Exists(path), "用真实渲染器生成合成预览，可离屏检查布局");
            }
        }
    }
}
