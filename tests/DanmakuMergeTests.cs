using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterDanmakuMergeCases()
    {
        TestRunner.Add("弹幕-合并", "热度分档、封顶与实际剩余寿命", TestMergeGrowth);
        TestRunner.Add("弹幕-合并", "拥挤与 SC 到来时缩小保留计数", TestMergeFallback);
        TestRunner.Add("弹幕-合并", "有限归一化、原文保留与合并窗口", TestMergeNormalization);
        TestRunner.Add("弹幕-合并", "设置默认值、归一化与无窗口控件构造", TestMergeSettings);
        TestRunner.Add("弹幕-合并", "热度表情异步到位与实际寿命", TestMergeEmote);
        TestRunner.Add("弹幕-合并", "大字号粗描边不裁边", TestMergeStroke);
        TestRunner.Add("弹幕-合并", "合并大小对比离屏预览", TestMergeGrowthPreview);
    }

    private static float FlightRemaining(DanmakuScene scene, object flying)
    { return (float)scene.Call("Remaining", flying, DateTime.UtcNow); }

    private static void TestMergeGrowth()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true);
            DanmakuScene.Set(scene.Config, "DuplicateWindowMs", 15000); scene.Configure();
            scene.Add("这波操作很稳", 1); scene.Step(2f);
            object first = scene.Flying[0];
            float baseSize = DanmakuScene.Number(first, "FontSize"), remaining = FlightRemaining(scene, first);
            float x = DanmakuScene.Number(first, "X"); int received = 1;
            int[] counts = { 2, 4, 8, 16, 32, 501, 9999 };
            float[] scales = { 1.08f, 1.16f, 1.24f, 1.32f, 1.4f, 1.4f, 1.4f };
            for (int k = 0; k < counts.Length; k++)
            {
                while (received < counts[k]) { scene.Add("这波操作很稳", 1); received++; }
                scene.Draw(); AssertDanmakuScene(scene);
                Check(Math.Abs(DanmakuScene.Number(first, "FontSize") / baseSize - scales[k]) < 0.001f,
                    "×" + counts[k] + " 的实际字号为 " + scales[k] + " 倍，基础轨道未限制放大");
                Check(Math.Abs(FlightRemaining(scene, first) - remaining) < 0.01f && Math.Abs(DanmakuScene.Number(first, "X") - x) < 0.01f,
                    "×" + counts[k] + " 更新保持左边缘和实际剩余时长");
            }
            scene.Add("这波操作很稳", 1);
            Check(!(bool)DanmakuScene.Get(first, "VisualDirty"), "计数到达上限后不反复请求重画");
            scene.Resize(1920, 1080);
            Check(Math.Abs(FlightRemaining(scene, first) - remaining) < 0.01f, "放大后再改变窗口大小，离屏时刻仍不漂移");
            DanmakuScene.Set(scene.Config, "SpeedPercent", 170); scene.Configure();
            Check(Math.Abs(FlightRemaining(scene, first) - remaining / 1.7f) < 0.02f, "改变速度时按新时长比例调整剩余时间");
        }
        foreach (int mode in new int[] { 4, 5 })
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true); scene.Configure();
            scene.Add("固定热度", mode); object first = scene.Flying[0];
            DateTime until = (DateTime)DanmakuScene.Get(first, "FixedUntil");
            for (int k = 1; k < 64; k++) scene.Add("固定热度", mode);
            scene.Draw(); AssertDanmakuScene(scene);
            Check((DateTime)DanmakuScene.Get(first, "FixedUntil") == until, "固定弹幕放大仍按首条时间到期");
            float center = DanmakuScene.Number(first, "X") + DanmakuScene.Number(first, "Width") / 2f;
            Check(Math.Abs(center - 640f) < 0.01f, "计数预留与固定文字整体保持居中");
        }
    }

    private static void TestMergeFallback()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true);
            DanmakuScene.Set(scene.Config, "ScaleWithWindow", false); scene.Configure();
            float track = DanmakuScene.Number(scene.Renderer, "_trackHeight");
            scene.Resize(1280, (int)(track * 2f));
            scene.Add("热点", 1); scene.Add("另一条需要保留的消息", 1); scene.Step(2f);
            object first = scene.Flying[0], other = scene.Flying[1];
            float baseSize = DanmakuScene.Number(first, "FontSize"), width = DanmakuScene.Number(first, "Width");
            for (int i = 1; i < 128; i++) scene.Add("热点", 1);
            scene.Draw(); AssertDanmakuScene(scene);
            Check(scene.Flying.Count == 2 && object.ReferenceEquals(other, scene.Flying[1]), "拥挤时保留其他消息");
            Check(DanmakuScene.Number(first, "FontSize") == baseSize && DanmakuScene.Number(first, "Width") == width
                && ((string)DanmakuScene.Get(first, "Label")).EndsWith("×128"), "空间不足时回退字号，预留空间容纳最新计数");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true); DanmakuScene.Set(scene.Config, "ScaleWithWindow", false);
            scene.Configure(); float track = DanmakuScene.Number(scene.Renderer, "_trackHeight");
            scene.Resize(1280, (int)(track * 3f + 1f)); scene.Add("热点", 1);
            object first = scene.Flying[0]; float baseSize = DanmakuScene.Number(first, "FontSize");
            for (int i = 1; i < 32; i++) scene.Add("热点", 1);
            scene.Draw(); Check(DanmakuScene.Number(first, "FontSize") > baseSize, "SC 到来前有空间放大");
            DanmakuScene.Set(scene.Config, "ScMode", "badge"); scene.Configure(); scene.Sc(1); scene.Draw();
            AssertDanmakuScene(scene);
            Check(scene.Flying.Count == 1 && DanmakuScene.Number(first, "FontSize") < baseSize * 1.4f,
                "SC 新占位后，已放大的弹幕也能缩小保留");
        }
    }

    private static void TestMergeNormalization()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true); DanmakuScene.Set(scene.Config, "DuplicateWindowMs", 8000); scene.Configure();
            const string original = "  ＡＢＣ　你好  ";
            scene.Add(original, 1); scene.Add("ABC 你好", 1); scene.Draw();
            Check(scene.Flying.Count == 1 && ((string)DanmakuScene.Get(scene.Flying[0], "Label")).StartsWith(original),
                "合并空白和全半角变体，显示保留首条原文");
            scene.Add("ABC你好", 1); scene.Add("完成？", 1); scene.Add("完成", 1); scene.Add("ABC 你好", 5);
            Check(scene.Flying.Count == 5, "词间空白、问句和不同类型仍可区分");
            object first = scene.Flying[0]; DanmakuScene.Set(first, "CreatedAt", DateTime.UtcNow.AddSeconds(-9));
            scene.Add("ABC 你好", 1);
            Check(scene.Flying.Count == 6, "超过自定义首条合并窗口后另建一条");
            AssertDanmakuScene(scene);
        }
    }

    private static void TestMergeSettings()
    {
        Type configType = DanmakuType("DanmakuConfig"); object config = Activator.CreateInstance(configType);
        Check((bool)DanmakuScene.Get(config, "EnlargeMergedDanmaku") && (int)DanmakuScene.Get(config, "MergedMaxScalePercent") == 140,
            "缺省配置开启受限放大");
        DanmakuScene.Set(config, "DuplicateWindowMs", -1); DanmakuScene.Set(config, "MergedMaxScalePercent", 9999);
        configType.GetMethod("Normalize").Invoke(config, null);
        Check((int)DanmakuScene.Get(config, "DuplicateWindowMs") == 1000 && (int)DanmakuScene.Get(config, "MergedMaxScalePercent") == 180,
            "合并窗口和放大上限有界");
        foreach (int mode in new int[] { 0, 1, 2 })
        {
            DanmakuScene.Set(config, "BlockDuplicate", mode != 0); DanmakuScene.Set(config, "EnlargeMergedDanmaku", mode == 2);
            DanmakuScene.Set(config, "DuplicateWindowMs", 8200);
            object parts = DanmakuType("DanmakuSettingsDialog").GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { config });
            using (Form form = (Form)parts.GetType().GetField("Dialog").GetValue(parts))
            {
                ComboBox picker = (ComboBox)form.Controls.Find("DuplicateMode", true)[0];
                ComboBox window = (ComboBox)form.Controls.Find("DuplicateWindow", true)[0];
                Check(picker.SelectedIndex == mode && window.SelectedItem.ToString().Contains("8.2"),
                    "不显示窗口即可构造合并模式和自定义时间，模式 " + mode);
            }
        }
    }

    private static void TestMergeGrowthPreview()
    {
        using (Bitmap preview = new Bitmap(1280, 600))
        using (Graphics g = Graphics.FromImage(preview))
        using (Font title = new Font("Microsoft YaHei UI", 22f, GraphicsUnit.Pixel))
        using (Font caption = new Font("Microsoft YaHei UI", 17f, GraphicsUnit.Pixel))
        {
            g.Clear(Color.FromArgb(20, 29, 43));
            g.DrawString("合并热度：次数增多，字号适度变大", title, Brushes.White, 32f, 24f);
            int row = 0;
            foreach (int count in new int[] { 1, 2, 8, 32, 501 })
            using (DanmakuScene scene = new DanmakuScene(1280, 720))
            {
                DanmakuScene.Set(scene.Config, "BlockDuplicate", true); scene.Configure();
                for (int i = 0; i < count; i++) scene.Add("这波操作很稳！", 5);
                scene.Draw(); object f = scene.Flying[0];
                float scale = DanmakuScene.Number(f, "AppliedMergeScale");
                g.DrawString("×" + count + "   " + Math.Round(scale * 100) + "%", caption, Brushes.LightSteelBlue, 40f, 103f + row * 88);
                g.DrawImage(scene.Canvas, new Rectangle(220, 90 + row * 88, 1040, 72), new Rectangle(220, 0, 1040, 72), GraphicsUnit.Pixel);
                row++;
            }
            g.DrawString("空间紧张时优先保留计数，字号逐档回退；首条展示时长保持不变。", caption, Brushes.LightSlateGray, 40f, 554f);
            preview.Save(Path.Combine(Path.GetDirectoryName(hub.Assembly.Location), "danmaku-merge-growth.png"), ImageFormat.Png);
            Check(true, "生成真实渲染器的合并字号对比图");
        }
    }

    private static void TestMergeEmote()
    {
        using (DanmakuScene scene = new DanmakuScene(3840, 2160))
        {
            DanmakuScene.Set(scene.Config, "BlockDuplicate", true); DanmakuScene.Set(scene.Config, "FontScalePercent", 170);
            DanmakuScene.Set(scene.Config, "LineGapPercent", 60); scene.Configure();
            object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
            DanmakuScene.Set(item, "Text", "合成表情"); DanmakuScene.Set(item, "IsEmoticon", true);
            DanmakuScene.Set(item, "EmoticonUrl", "fixture:merge-emote");
            for (int i = 0; i < 32; i++) scene.Call("AddDanmaku", item);
            scene.Draw(); scene.Step(2f); scene.Add("旁边的普通消息", 1);
            object first = scene.Flying[0]; float remaining = FlightRemaining(scene, first);
            scene.Image("fixture:merge-emote", 240, 60); scene.Draw();
            Check(Math.Abs(FlightRemaining(scene, first) - remaining) < 0.01f, "热度表情异步变宽后实际离屏时间不延长");
            Check(Math.Abs(DanmakuScene.Number(first, "EmoteWidth") / DanmakuScene.Number(first, "EmoteBox") - 4f) < 0.01f,
                "合并放大后的非方形表情仍按原比例显示");
            Check(DanmakuScene.Number(first, "EmoteBox") > DanmakuScene.Number(scene.Renderer, "_trackHeight"),
                "热度表情可以占多行，不被基础单轨高度限制");
            for (int frame = 0; frame < 120; frame++) { scene.Step(1f / 60f); scene.Draw(); AssertDanmakuScene(scene); }
            Check(true, "4K、170% 字号、低行距热度表情混排不相交");
        }
    }

    private static void TestMergeStroke()
    {
        Type type = DanmakuType("DanmakuTextCache"); object cache = Activator.CreateInstance(type);
        try
        {
            foreach (float size in new float[] { 96f, 200f, 360f })
            {
                object shared = type.GetMethod("Get").Invoke(cache, new object[] { "WW", Color.White, size, "Arial", true, 4 });
                try
                {
                    Bitmap bitmap = BitmapOf(shared); bool edgeClear = true, painted = false;
                    for (int x = 0; x < bitmap.Width; x++)
                        if (bitmap.GetPixel(x, 0).A != 0 || bitmap.GetPixel(x, bitmap.Height - 1).A != 0) edgeClear = false;
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        if (bitmap.GetPixel(0, y).A != 0 || bitmap.GetPixel(bitmap.Width - 1, y).A != 0) edgeClear = false;
                        for (int x = 1; !painted && x < bitmap.Width - 1; x++) painted = bitmap.GetPixel(x, y).A > 0;
                    }
                    Check(painted && edgeClear, size + "px、4px 配置描边的字形确实绘制且外边缘透明");
                }
                finally { ReleaseShared(shared); }
            }
        }
        finally { type.GetMethod("Clear").Invoke(cache, null); }
    }
}
