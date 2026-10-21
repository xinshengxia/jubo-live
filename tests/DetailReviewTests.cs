using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{
    private static void RegisterDetailReviewCases(string launcher)
    {
        TestRunner.Add("细节复查", "窄窗口状态提示换行完整且随文本更新", TestReviewStatusWrapping);
        TestRunner.Add("细节复查", "历史空条目、跨平台污染与重复记录容错", TestReviewHistoryEntries);
        TestRunner.Add("持久化", "历史读取失败后解锁仍不覆盖原文件", TestReviewHistoryReadFailure);
        TestRunner.Add("细节复查", "收藏空条目不丢失其他有效记录", TestReviewFavoriteEntries);
        TestRunner.Add("细节复查", "头像预约取消后释放、失败重试与缓存淘汰", TestReviewAvatarLifecycle);
        TestRunner.Add("细节复查", "构建产物携带真实播放器标题脚本", delegate {
            string root = Path.GetDirectoryName(launcher), script = Path.Combine(root, "lua", "live-title.lua");
            if (!File.Exists(script)) script = Path.Combine(root, "live-title.lua");
            Check(File.Exists(script) && File.ReadAllText(script) == File.ReadAllText(Path.Combine(SourceRoot, "live-title.lua")),
                "exe 同级 Lua 脚本存在且与当前源码一致，不能只拼出不存在的 --script 路径");
        });
    }
    private static void TestReviewStatusWrapping()
    {
        RunUiPolish(delegate {
            MethodInfo build = hub.GetMethod("BuildLauncherForm", Flags, null, new Type[] { typeof(int) }, null);
            using (Form form = (Form)build.Invoke(null, new object[] { -1 }))
            {
                Label status = (Label)form.Controls.Find("launcherStatus", true)[0];
                foreach (int dpi in new[] { 96, 144, 192, 288 })
                foreach (int width in new[] { 620, 1120 })
                {
                    SetDpi(form, dpi); form.ClientSize = new Size(width * dpi / 96, 600 * dpi / 96); form.PerformLayout();
                    foreach (string message in new[] {
                        "选房间 → 回车用 mpv 打开（窗口不会自动关）",
                        "打开失败：直播中转未能就绪；请检查网络连接以及播放器配置，稍后再试。房间未开播时可重新输入其他房间继续观看。",
                        "已取消启动" })
                    {
                        status.Text = message;
                        int needed = TextRenderer.MeasureText(status.Text, status.Font,
                            new Size(status.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                        Check(needed <= status.Height, "状态文字全部可读 DPI=" + dpi + " width=" + width + " height=" + status.Height + " needed=" + needed);
                        foreach (Control control in status.Parent.Controls)
                            if (control != status && WorkbenchLocallyVisible(control))
                                Check(!control.Bounds.IntersectsWith(status.Bounds), "状态提示换行后不覆盖同容器的其他操作");
                    }
                    status.Text = new string('长', 500);
                    Check(status.AutoEllipsis && status.Height <= 72 * dpi / 96, "异常长提示限定三行并提供省略提示，不把全部操作推出窗口");
                }
            }
        });
    }
    private static void TestReviewHistoryEntries()
    {
        string dir = ReviewDirectory(); FieldInfo history = hub.GetField("History", Flags); object previous = history.GetValue(null);
        try
        {
            string path = Path.Combine(dir, "history.json"); history.SetValue(null, path);
            string json = "{\"bili\":[null,{\"Room\":null},{\"Room\":\"wrong&room\"},{\"Room\":\"douyu:123\"},{\"Room\":\" 7777 \",\"Name\":\"保留\"},{\"Room\":\"bili:7777\"}],\"huya\":null,\"unknown\":[{\"Room\":\"123\"}]}";
            File.WriteAllText(path, json);
            IDictionary loaded = (IDictionary)Call("LoadHistory"); IList entries = (IList)loaded["bili"];
            Check(entries.Count == 1 && (string)LookupGet(entries[0], "Room") == "7777"
                && (string)LookupGet(entries[0], "Name") == "保留", "空条目和非法/跨平台房间被跳过，规范化并保留首条有效记录");
            Check(!loaded.Contains("huya") && !loaded.Contains("unknown"), "空平台列表与不支持的平台不进入历史界面");
            Check(File.ReadAllText(path) == json, "加载容错不改写用户原始历史");
            var records = new List<string>();
            for (int i = 1; i <= 20; i++) records.Add("{\"Room\":\"" + i + "\"}");
            File.WriteAllText(path, "{\"bili\":[" + string.Join(",", records) + "]}");
            loaded = (IDictionary)Call("LoadHistory"); entries = (IList)loaded["bili"];
            Check(entries.Count == 15 && (string)LookupGet(entries[14], "Room") == "15", "历史读取遵守最近十五条上限及原顺序");
        }
        finally { history.SetValue(null, previous); Directory.Delete(dir, true); }
    }
    private static void TestReviewHistoryReadFailure()
    {
        string dir = ReviewDirectory(); FieldInfo history = hub.GetField("History", Flags); object previous = history.GetValue(null);
        try
        {
            string path = Path.Combine(dir, "history.json"); history.SetValue(null, path);
            string json = "{\"bili\":[{\"Room\":\"7777\",\"Name\":\"原历史\"}]}";
            File.WriteAllText(path, json);
            object loaded;
            using (FileStream locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                loaded = Call("LoadHistory");
            Check(((IDictionary)loaded).Count == 0, "历史读取遇锁时仍可使用空列表继续播放");
            object target = Call("Parse", "bili:8888", "bili");
            Call("AddHistory", loaded, target, "本次播放");
            Check(!(bool)Call("SaveHistory", loaded), "读取失败的历史在文件解锁后仍禁止保存");
            Check(File.ReadAllText(path) == json, "新播放不覆盖启动时未能读出的原历史");
            Check(((IList)((IDictionary)loaded)["bili"]).Count == 1, "本次播放仍进入内存历史");
            Check(!(bool)Call("SaveHistory", loaded) && File.ReadAllText(path) == json,
                "同次启动再次保存仍保留原文件");

            loaded = Call("LoadHistory");
            Call("AddHistory", loaded, target, "重启后播放");
            Check((bool)Call("SaveHistory", loaded), "重新成功读取后允许保存历史");
            Check(((IList)((IDictionary)Call("LoadHistory"))["bili"]).Count == 2,
                "重新读取后保存同时保留原历史和新播放");

            foreach (string invalid in new[] { "invalid json", "null" })
            {
                File.WriteAllText(path, invalid);
                loaded = Call("LoadHistory"); Call("AddHistory", loaded, target, "本次播放");
                Check(!(bool)Call("SaveHistory", loaded) && File.ReadAllText(path) == invalid,
                    "历史解析失败不覆盖原始内容：" + invalid);
            }

            File.WriteAllText(path, json);
            RunUiPolish(delegate {
                MethodInfo build = hub.GetMethod("BuildLauncherForm", Flags, null, new Type[] { typeof(int) }, null);
                using (FileStream locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                using (Form form = (Form)build.Invoke(null, new object[] { -1 }))
                {
                    Label status = (Label)form.Controls.Find("launcherStatus", true)[0];
                    Check(status.Text.Contains("历史读取失败") && status.Text.Contains("重启"),
                        "启动时明确提示历史读取失败及重启恢复方式");
                }
            });

            File.Delete(path);
            loaded = Call("LoadHistory"); Call("AddHistory", loaded, target, "首次播放");
            Check((bool)Call("SaveHistory", loaded) && File.Exists(path), "首次运行没有历史文件时仍正常创建");
        }
        finally { history.SetValue(null, previous); Directory.Delete(dir, true); }
    }
    private static void TestReviewFavoriteEntries()
    {
        string dir = ReviewDirectory(); FieldInfo favorites = hub.GetField("Favorites", Flags); object previous = favorites.GetValue(null);
        try
        {
            string path = Path.Combine(dir, "favorites.json"); favorites.SetValue(null, path);
            string json = "[null,{\"name\":\"有效收藏\",\"room\":\"7777\"},{\"name\":\"无效\",\"room\":\"&bad\"}]";
            File.WriteAllText(path, json);
            var loaded = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(loaded.Count == 1 && loaded[0].Key == "有效收藏" && loaded[0].Value == "7777", "收藏 null 条目不触发整表清空或模态错误框");
            Check(File.ReadAllText(path) == json, "读取时保留原始收藏文件");
        }
        finally { favorites.SetValue(null, previous); Directory.Delete(dir, true); }
    }
    private static void WaitReviewAvatars(object cache)
    {
        object images = LookupGet(cache, "images"), requested = LookupGet(cache, "requested");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            lock (images) { if ((int)LookupGet(requested, "Count") == 0) return; }
            Thread.Sleep(5);
        }
        throw new Exception("头像预约在完成/取消后没有释放");
    }
    private static object ReviewAvatarCandidates(params string[] urls)
    {
        object result = LookupResult();
        foreach (string url in urls) {
            object candidate = LookupCandidate("bili", "123", "7777"); LookupSet(candidate, "avatar_url", url);
            ((IList)LookupGet(result, "candidates")).Add(candidate);
        }
        return LookupGet(result, "candidates");
    }
    private static void TestReviewAvatarLifecycle()
    {
        byte[] bytes;
        using (var bitmap = new Bitmap(2, 2)) using (var stream = new MemoryStream())
        { bitmap.Save(stream, ImageFormat.Png); bytes = stream.ToArray(); }
        object cache = LookupNew("LookupAvatarCache");
        using (var owner = new Control())
        using (var cancel = new CancellationTokenSource())
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            try
            {
                int downloads = 0;
                LookupDelegate(cache, "Download", delegate(object[] args) {
                    Interlocked.Increment(ref downloads); entered.Set();
                    if (!release.WaitOne(3000)) throw new Exception("测试未释放下载");
                    ((CancellationToken)args[1]).ThrowIfCancellationRequested(); return bytes;
                });
                object batch = ReviewAvatarCandidates("https://i0.hdslb.com/first.png", "https://i0.hdslb.com/second.png", "https://i0.hdslb.com/third.png");
                LookupCall(cache, "Preload", batch, owner, cancel.Token);
                Check(entered.WaitOne(3000), "头像请求已进入下载，可确定性取消预约批次");
                cancel.Cancel(); release.Set(); WaitReviewAvatars(cache);
                LookupDelegate(cache, "Download", delegate { Interlocked.Increment(ref downloads); return bytes; });
                LookupCall(cache, "Preload", batch, owner, CancellationToken.None); WaitReviewAvatars(cache);
                Check(downloads == 4 && (int)LookupGet(LookupGet(cache, "images"), "Count") == 3, "取消中的请求和未下载的批次条目均可重新加载");
                string retry = "https://i0.hdslb.com/retry.png"; int attempts = 0;
                LookupDelegate(cache, "Download", delegate { if (Interlocked.Increment(ref attempts) == 1) throw new IOException("合成网络失败"); return bytes; });
                LookupCall(cache, "Preload", ReviewAvatarCandidates(retry), owner, CancellationToken.None); WaitReviewAvatars(cache);
                LookupCall(cache, "Preload", ReviewAvatarCandidates(retry), owner, CancellationToken.None); WaitReviewAvatars(cache);
                Check(attempts == 2, "暂时网络失败后同一个头像 URL 可以重试");
                LookupDelegate(cache, "Download", delegate { return bytes; });
                for (int i = 0; i < 70; i++) {
                    LookupCall(cache, "Preload", ReviewAvatarCandidates("https://i0.hdslb.com/new-" + i + ".png"), owner, CancellationToken.None);
                    WaitReviewAvatars(cache);
                }
                IDictionary images = (IDictionary)LookupGet(cache, "images");
                Check(images.Count == 32 && images.Contains("https://i0.hdslb.com/new-69.png"), "超过旧 64 次请求上限仍可显示新头像，位图缓存始终不超过 32 张");
                using (var canvas = new Bitmap(48, 48)) using (Graphics graphics = Graphics.FromImage(canvas))
                    Check((bool)LookupCall(cache, "Draw", graphics, "https://i0.hdslb.com/new-38.png", new Rectangle(0, 0, 48, 48)), "命中最老缓存头像仍能安全绘制并更新最近使用顺序");
                LookupCall(cache, "Preload", ReviewAvatarCandidates("https://i0.hdslb.com/latest.png"), owner, CancellationToken.None); WaitReviewAvatars(cache);
                Check(images.Contains("https://i0.hdslb.com/new-38.png") && !images.Contains("https://i0.hdslb.com/new-39.png"), "缓存淘汰最久未使用头像，保留刚绘制的位图");
                LookupCall(cache, "Dispose");
                Check(images.Count == 0, "查询关闭释放全部缓存位图");
                LookupCall(cache, "Preload", batch, owner, CancellationToken.None); WaitReviewAvatars(cache);
                Check(images.Count == 0, "缓存关闭后不再接受新请求");
            }
            finally { cancel.Cancel(); release.Set(); LookupCall(cache, "Dispose"); }
        }
    }
}
