using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static partial class HardeningTests
{
    private static void TestWorkbenchAvatarPlatforms()
    {
        object verifier = LookupNew("LookupVerifier");
        var owner = new Dictionary<string, object> {
            { "sec_uid", "synthetic-sec" }, { "id_str", "701000" }, { "web_rid", "701001" }, { "nickname", "合成主播" },
            { "avatar_thumb", new Dictionary<string, object> { { "url_list", new[] { "https://p3.douyinpic.com/synthetic.png" } } } }
        };
        var room = new Dictionary<string, object> { { "id_str", "701002" }, { "status", 4 }, { "owner", owner } };
        var serializer = new JavaScriptSerializer();
        string flight = serializer.Serialize(new { state = new { roomStore = new { roomInfo = new { room = room } } } });
        string page = "<script>self.__next_f.push([1," + serializer.Serialize("0:" + flight) + "])</script>";
        LookupDelegate(verifier, "Transport", delegate(object[] args)
        {
            string url = (string)args[0];
            if (url.Contains("/room/v1/Room/get_info")) return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":701001,\"uid\":701000,\"live_status\":0}}", null);
            if (url.Contains("/live_user/v1/Master/info")) return LookupReply(200, "{\"code\":0,\"data\":{\"info\":{\"uid\":701000,\"uname\":\"合成主播\",\"face\":\"https://i0.hdslb.com/synthetic.jpg\"}}}", null);
            if (url.StartsWith("https://www.huya.com/")) return LookupReply(200,
                "var TT_PROFILE_INFO = {\"profileRoom\":701001,\"lp\":701000,\"nick\":\"合成主播\",\"avatar180\":\"https://huyaimg.msstatic.com/synthetic.png\"};var TT_ROOM_DATA = {\"profileRoom\":701001,\"id\":701000};", null);
            if (url.StartsWith("https://www.douyu.com/")) return LookupReply(200,
                "{\"room\":{\"room_id\":701001,\"owner_uid\":701000,\"owner_name\":\"合成主播\",\"show_status\":2,\"avatar\":{\"big\":\"ignored object\"},\"owner_avatar\":\"https://apic.douyucdn.cn/synthetic.png\"}}", null);
            if (url.StartsWith("https://live.douyin.com/")) return LookupReply(200, page, null);
            if (url.StartsWith("https://webcast.amemv.com/")) return LookupReply(200, serializer.Serialize(new { status_code = 0, data = new { room = room } }), null);
            throw new Exception("Unexpected synthetic avatar request");
        });
        object cache = LookupNew("LookupAvatarCache");
        byte[] bytes;
        using (Bitmap image = new Bitmap(8, 8)) using (MemoryStream stream = new MemoryStream())
        { image.Save(stream, ImageFormat.Png); bytes = stream.ToArray(); }
        string downloaded = null;
        LookupDelegate(cache, "Download", delegate(object[] args) { downloaded = (string)args[0]; return bytes; });
        try
        {
            // 虎牙别名（clkimmy 一类）只能从官方别名页解析数字房间与头像。
            object alias = LookupNew("LookupCandidate");
            LookupSet(alias, "platform", "huya");
            LookupSet(alias, "profile_url", "https://www.huya.com/clkimmy");
            object aliasSource = LookupNew("LookupSource");
            LookupSet(aliasSource, "url", "https://www.huya.com/clkimmy");
            ((IList)LookupGet(alias, "sources")).Add(aliasSource);
            object aliasVerified = LookupCall(verifier, "Verify", alias, CancellationToken.None);
            Check((string)LookupGet(aliasVerified, "verification") == "verified"
                && (string)LookupGet(aliasVerified, "room_id") == "701001", "虎牙别名经官方别名页解析出数字房间");
            Check(((string)LookupGet(aliasVerified, "avatar_url")).Contains("msstatic.com"), "虎牙别名页返回账号头像");

            // 抖音 reflow 受阻时，房间页里主播的头像仍要保留。
            var anchor = new Dictionary<string, object> {
                { "sec_uid", "synthetic-sec2" }, { "id_str", "701000" }, { "nickname", "合成主播" },
                { "avatar_thumb", new Dictionary<string, object> { { "url_list", new[] { "https://p3.douyinpic.com/blocked.png" } } } }
            };
            var anchorRoom = new Dictionary<string, object> { { "id_str", "701004" }, { "status", 4 }, { "anchor", anchor } };
            string anchorFlight = serializer.Serialize(new { state = new { roomStore = new { roomInfo = new { room = anchorRoom } } } });
            string anchorPage = "<script>self.__next_f.push([1," + serializer.Serialize("0:" + anchorFlight) + "])</script>";
            object blocked = LookupNew("LookupVerifier");
            LookupDelegate(blocked, "Transport", delegate(object[] args)
            {
                string url = (string)args[0];
                if (url.StartsWith("https://live.douyin.com/")) return LookupReply(200, anchorPage, null);
                if (url.StartsWith("https://webcast.amemv.com/")) return LookupReply(200, "{\"status_code\":10001}", null);
                throw new Exception("Unexpected blocked avatar request " + url);
            });
            object partial = LookupCandidate("douyin", null, "701004");
            LookupSet(partial, "direct_input", true);
            object partialVerified = LookupCall(blocked, "Verify", partial, CancellationToken.None);
            Check((string)LookupGet(partialVerified, "verification") != "verified", "合成：reflow 受阻不冒充已核验");
            Check(((string)LookupGet(partialVerified, "avatar_url")).EndsWith("blocked.png"), "reflow 受阻时仍使用房间页主播头像");

            foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
            {
                object candidate = LookupCandidate(platform, null, "701001"); LookupSet(candidate, "direct_input", true);
                object verified = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
                Check((string)LookupGet(verified, "verification") == "verified", "账号与房间归属核验 " + platform);
                string url = (string)LookupGet(verified, "avatar_url");
                Check((bool)LookupCall(cache.GetType(), "Allowed", url), "读取真实头像URL字段，忽略嵌套对象占位 " + platform);
                object thumbnail = LookupCall(cache, "Load", url, CancellationToken.None);
                Check(thumbnail != null, "账号头像可解码为受限缩略图 " + platform);
                ((IDisposable)thumbnail).Dispose();
                if (platform == "bili") Check(downloaded.Contains("@96w_96h_1c.png"), "B站使用CDN缩略PNG而不是过大的原始图片");
                if (platform == "douyin") Check((string)LookupGet(verified, "live_status") == "offline", "未开播的抖音账号仍可读取头像");
            }
        }
        finally { LookupCall(cache, "Dispose"); }
    }

    private static void StopWorkbenchTimers(Form form)
    {
        foreach (string name in new[] { "watchTimer", "previewTimer" })
            ((System.Windows.Forms.Timer)form.Tag.GetType().GetField(name, ReviewInstance).GetValue(form.Tag)).Stop();
    }

    private static void TestWorkbenchRefreshContinuity()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form);
            Type ui = form.Tag.GetType();
            var favorites = (IList<KeyValuePair<string, string>>)ui.GetField("favorites", ReviewInstance).GetValue(form.Tag);
            favorites.Clear();
            for (int i = 0; i < 9; i++) favorites.Add(new KeyValuePair<string, string>("合成主播" + i, "bili:" + (700100 + i)));
            ui.GetMethod("RefreshLists", ReviewInstance).Invoke(form.Tag, null);
            ListBox model = (ListBox)WorkbenchField(form, "favList");
            ScrollableControl browser = (ScrollableControl)WorkbenchFind(form, "favoritesBrowser");
            model.SelectedIndex = 7;
            ComboBox quality = (ComboBox)WorkbenchField(form, "qualityBox"), buffer = (ComboBox)WorkbenchField(form, "bufferBox"), danmaku = (ComboBox)WorkbenchField(form, "danmakuBox");
            quality.SelectedIndex = 2; buffer.SelectedIndex = 0; danmaku.SelectedIndex = 2;
            Control input = WorkbenchField(form, "input"), status = WorkbenchField(form, "status");
            input.Text = "douyu:700999";
            WorkbenchForceLayout(form);
            browser.AutoScrollPosition = new Point(0, 120);
            Point scroll = browser.AutoScrollPosition;
            int optionSizes = 0, selections = 0;
            foreach (string name in new[] { "qualityBox", "bufferBox", "danmakuBox", "scBox", "enhanceBox" })
                WorkbenchField(form, name).SizeChanged += delegate { optionSizes++; };
            model.SelectedIndexChanged += delegate { selections++; };
            MethodInfo refresh = ui.GetMethod("RefreshLists", ReviewInstance);
            for (int i = 0; i < 4; i++) { refresh.Invoke(form.Tag, null); status.Text = "合成房间：状态更新 " + i; }
            Check(selections == 0 && model.SelectedIndex == 7, "相同状态刷新不清空模型或撤销选择");
            Check(optionSizes == 0, "短状态提示变化不重新设置五个选项尺寸");
            Check(browser.AutoScrollPosition == scroll, "后台刷新不拉回收藏滚动位置");
            Type state = hub.Assembly.GetType("RoomStateKind");
            ui.GetMethod("UpdateBadge", ReviewInstance).Invoke(form.Tag, new[] { Call("Parse", "bili:700107", "bili"), Enum.Parse(state, "Live") });
            IList current = (IList)ui.GetField("currentFavorites", ReviewInstance).GetValue(form.Tag);
            var selected = (KeyValuePair<string, string>)current[model.SelectedIndex];
            Check(selected.Value == "bili:700107" && model.SelectedIndex == 0, "开播排序变化后仍选中同一个房间");
            Check((int)browser.GetType().GetProperty("SelectedIndex").GetValue(browser, null) == model.SelectedIndex, "可见收藏卡片与模型选择一致");
            Check(input.Text == "douyu:700999", "后台排序不覆盖后来输入的房间");
            Check(quality.SelectedIndex == 2 && buffer.SelectedIndex == 0 && danmaku.SelectedIndex == 2, "后台刷新保留本次播放选项");
            Check(optionSizes == 0, "开播标记刷新不重排播放选项");
        });
    }

    private static void TestWorkbenchChromeRestore()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form);
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { dpi });
                form.ClientSize = new Size(1240 * dpi / 96, 720 * dpi / 96);
                WorkbenchShowPage(form, "favorites");
                Control min = WorkbenchFind(form, "workbenchChromeMin"), max = WorkbenchFind(form, "workbenchChromeMax"), close = WorkbenchFind(form, "workbenchChromeClose");
                Check(min.Left > form.ClientSize.Width * 4 / 5 && min.Right <= max.Left && max.Right <= close.Left && close.Right <= form.ClientSize.Width,
                    "最小化、最大化、关闭依次位于右上角 DPI=" + dpi);
                foreach (Control button in new[] { min, max, close })
                {
                    int dark = 0;
                    Color fill = button == min ? Color.FromArgb(0xFE, 0xBC, 0x2E)
                        : button == max ? Color.FromArgb(0x28, 0xC8, 0x40) : Color.FromArgb(0xFF, 0x5F, 0x57);
                    using (Bitmap image = new Bitmap(button.Width, button.Height))
                    {
                        button.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        for (int y = image.Height / 3; y < image.Height * 2 / 3; y++)
                            for (int x = image.Width / 3; x < image.Width * 2 / 3; x++)
                            { Color c = image.GetPixel(x, y); if (c.GetBrightness() < fill.GetBrightness() * .8f) dark++; }
                    }
                    Check(dark > 2, "不悬停时窗口按钮也显示符号 " + button.Name + " DPI=" + dpi);
                }
                max.GetType().GetProperty("Restore", ReviewInstance).SetValue(max, true, null);
                Check(max.AccessibleName == "还原", "最大化状态显示还原含义");
            }
            int resized = 0;
            form.ClientSizeChanged += delegate { resized++; };
            form.GetType().GetMethod("OnClientSizeChanged", ReviewInstance).Invoke(form, new object[] { EventArgs.Empty });
            Check(resized == 1, "窗口恢复路径保留框架尺寸事件");
            MethodInfo repaint = form.GetType().GetMethod("QueueRestoreRepaint", ReviewInstance);
            repaint.Invoke(form, null); repaint.Invoke(form, null);
            Check((bool)form.GetType().GetField("restoreRepaintQueued", ReviewInstance).GetValue(form), "重复恢复请求合并为一轮延后重绘");
            Application.DoEvents();
            Check(!(bool)form.GetType().GetField("restoreRepaintQueued", ReviewInstance).GetValue(form), "恢复后完成子窗口重绘且不保留悬挂请求");
            Check(!form.Visible, "恢复重绘检查不显示窗口或抢占前台");
        });
    }

    private static void WaitWorkbenchCallbacks(Func<bool> completed)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(4);
        while (!completed() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Check(completed(), "合成头像任务在限定时间内完成");
    }

    private static void TestWorkbenchAccountAvatars()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form); IntPtr hwnd = form.Handle;
            form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { 96 });
            form.ClientSize = new Size(1240, 720); WorkbenchShowPage(form, "favorites");
            byte[] bytes;
            using (Bitmap image = new Bitmap(8, 8))
            using (MemoryStream stream = new MemoryStream())
            {
                using (Graphics g = Graphics.FromImage(image)) g.Clear(Color.CornflowerBlue);
                image.Save(stream, ImageFormat.Png); bytes = stream.ToArray();
            }
            object service = LookupNew("WorkbenchAvatarService");
            int calls = 0, finished = 0;
            using (ManualResetEvent gate = new ManualResetEvent(false))
            {
                LookupDelegate(service, "Resolve", delegate(object[] args) { Interlocked.Increment(ref calls); gate.WaitOne(2000); return "https://i0.hdslb.com/synthetic-avatar.png"; });
                LookupDelegate(service, "Fetch", delegate { return bytes; });
                IList targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(hub.GetNestedType("Target", ReviewStatic)));
                for (int i = 0; i < 6; i++) targets.Add(Call("Parse", "bili:" + (701000 + i), "bili"));
                Action<string, string, byte[]> ready = delegate(string key, string url, byte[] png)
                {
                    Check(key.StartsWith("bili:701") && url.EndsWith("synthetic-avatar.png") && png != null, "头像结果按房间身份回填");
                    finished++;
                };
                LookupCall(service, "Pump", targets, form, ready);
                SpinWait.SpinUntil(delegate { return calls == 3; }, 1500);
                Check(calls == 3, "平台资料查询最多三个在途，不阻塞界面线程");
                gate.Set(); WaitWorkbenchCallbacks(delegate { return finished == 3; });
                LookupCall(service, "Pump", targets, form, ready); WaitWorkbenchCallbacks(delegate { return finished == 6; });
                LookupCall(service, "Pump", targets, form, ready);
                Check(calls == 6 && (string)LookupCall(service, "Get", "bili:701000") == "https://i0.hdslb.com/synthetic-avatar.png", "成功头像映射缓存后不重复查询");
            }
            LookupCall(service, "Dispose");
            Control browser = WorkbenchFind(form, "favoritesBrowser");
            object cache = browser.GetType().GetField("Avatars", ReviewInstance).GetValue(browser);
            LookupDelegate(cache, "Download", delegate { return bytes; });
            object candidate = LookupCandidate("bili", "701000", "701001");
            LookupSet(candidate, "avatar_url", "https://i0.hdslb.com/synthetic-avatar.png");
            IList candidates = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(candidate.GetType())); candidates.Add(candidate);
            LookupCall(cache, "Preload", candidates, browser, CancellationToken.None);
            IDictionary images = (IDictionary)cache.GetType().GetField("images", ReviewInstance).GetValue(cache);
            WaitWorkbenchCallbacks(delegate { lock (images) return images.Count == 1; });
            IList items = (IList)browser.GetType().GetProperty("Items").GetValue(browser, null);
            items[0].GetType().GetField("AvatarUrl", ReviewInstance).SetValue(items[0], "https://i0.hdslb.com/synthetic-avatar.png");
            Check(WorkbenchAvatarPixel(browser, 34, 34, Color.CornflowerBlue), "收藏卡片头像区域绘制已下载的账号图像");
            // 最近播放：头像画在每行最左侧，不是只给收藏卡片。
            WorkbenchShowPage(form, "recent");
            Control recent = WorkbenchFind(form, "recentBrowser");
            IList recentItems = (IList)recent.GetType().GetProperty("Items").GetValue(recent, null);
            Check(recentItems.Count > 0, "最近播放夹具存在记录");
            recentItems[0].GetType().GetField("AvatarUrl", ReviewInstance).SetValue(recentItems[0], "https://i0.hdslb.com/synthetic-avatar.png");
            object recentCache = recent.GetType().GetField("Avatars", ReviewInstance).GetValue(recent);
            LookupDelegate(recentCache, "Download", delegate { return bytes; });
            LookupCall(recentCache, "Preload", candidates, recent, CancellationToken.None);
            IDictionary recentImages = (IDictionary)recentCache.GetType().GetField("images", ReviewInstance).GetValue(recentCache);
            WaitWorkbenchCallbacks(delegate { lock (recentImages) return recentImages.Count == 1; });
            Check(WorkbenchAvatarPixel(recent, 32, 30, Color.CornflowerBlue), "最近播放每行左侧绘制账号头像");
            // 首页「正在观看 / 最近打开」左上角同样显示缓存头像。
            WorkbenchShowPage(form, "favorites");
            Control homeAvatar = WorkbenchField(form, "workbenchHomeAvatar");
            homeAvatar.GetType().GetMethod("SetAvatar", ReviewInstance).Invoke(homeAvatar, new object[] { bytes });
            Check(WorkbenchAvatarPixel(homeAvatar, homeAvatar.Width / 2, homeAvatar.Height / 2, Color.CornflowerBlue), "首页最近打开卡片左上角绘制账号头像");
            object metadata = LookupCandidate("douyu", "701000", "701001");
            LookupCall(LookupType("LookupVerifier"), "Metadata", metadata, new Dictionary<string, object> { { "owner_avatar_big", "https://apic.douyucdn.cn/synthetic-avatar.png" } });
            Check(((string)LookupGet(metadata, "avatar_url")).Contains("douyucdn.cn"), "斗鱼房间返回的账号头像字段可读取");
        });
    }

    private static bool WorkbenchAvatarPixel(Control control, int x, int y, Color expected)
    {
        using (Bitmap render = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(render, new Rectangle(Point.Empty, render.Size));
            Color center = render.GetPixel(Math.Min(x, control.Width - 1), Math.Min(y, control.Height - 1));
            return Math.Abs(center.B - expected.B) < 24 && Math.Abs(center.G - expected.G) < 24 && Math.Abs(center.R - expected.R) < 24;
        }
    }

    private static void TestWorkbenchAvatarCacheRefresh()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form);
            form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { 96 });
            form.ClientSize = new Size(1240, 720); WorkbenchShowPage(form, "favorites");
            string runtime = (string)hub.GetField("RuntimeDir", ReviewStatic).GetValue(null);
            byte[] first = MakePng(10, 10), second = MakePng(12, 12);
            object service = LookupNew("WorkbenchAvatarService");
            int resolves = 0, fetches = 0;
            string url = "https://i0.hdslb.com/synthetic-a.png";
            LookupDelegate(service, "Resolve", delegate(object[] args) { Interlocked.Increment(ref resolves); return url; });
            LookupDelegate(service, "Fetch", delegate(object[] args) { Interlocked.Increment(ref fetches); return first; });
            IList targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(hub.GetNestedType("Target", ReviewStatic)));
            targets.Add(Call("Parse", "bili:702000", "bili"));
            int applied = 0;
            LookupCall(service, "Pump", targets, form, (Action<string, string, byte[]>)delegate(string key, string readyUrl, byte[] png) { applied++; });
            WaitWorkbenchCallbacks(delegate { return applied == 1; });
            Check(File.Exists(Path.Combine(runtime, "workbench-avatars.json")), "头像 URL 索引写入本地缓存");
            Check(File.Exists(Path.Combine(runtime, "workbench-avatars", "bili_702000.png")), "头像 PNG 写入本地缓存");
            Check((string)LookupCall(service, "Get", "bili:702000") == url, "头像 URL 记录在缓存中");
            Check((byte[])LookupCall(service, "ImageBytes", "bili:702000") != null, "头像图像可从缓存读出");
            // 重启（新服务实例）：先画缓存，不联网。
            object restarted = LookupNew("WorkbenchAvatarService");
            int restartedResolves = 0;
            LookupDelegate(restarted, "Resolve", delegate(object[] args) { Interlocked.Increment(ref restartedResolves); return "https://i0.hdslb.com/other.png"; });
            Check((string)LookupCall(restarted, "Get", "bili:702000") == url, "重启后从缓存恢复头像 URL");
            Check((byte[])LookupCall(restarted, "ImageBytes", "bili:702000") != null, "重启后从缓存恢复头像图像");
            LookupCall(restarted, "Pump", targets, form, (Action<string, string, byte[]>)delegate(string key, string readyUrl, byte[] png) { });
            Thread.Sleep(300); Application.DoEvents();
            Check(restartedResolves == 0, "已有头像缓存时启动不重新抓取");
            LookupCall(restarted, "Dispose");
            // 再次选中但平台返回同一 URL：不下载、不替换、不回调。
            IDictionary entries = (IDictionary)service.GetType().GetField("entries", ReviewInstance).GetValue(service);
            object entry = entries["bili:702000"];
            entry.GetType().GetField("Checked", ReviewInstance).SetValue(entry, DateTime.UtcNow.AddMinutes(-5));
            int callbacks = 0;
            LookupCall(service, "Refresh", targets[0], form, (Action<string, string, byte[]>)delegate(string key, string readyUrl, byte[] png) { callbacks++; });
            Thread.Sleep(600); Application.DoEvents();
            Check(callbacks == 0 && fetches == 1, "再次选中但头像未变：不替换也不重绘");
            Check((string)LookupCall(service, "Get", "bili:702000") == url, "未变化的头像 URL 保持原样");
            // 头像真的变了才替换。
            entry.GetType().GetField("Checked", ReviewInstance).SetValue(entry, DateTime.UtcNow.AddMinutes(-5));
            url = "https://i0.hdslb.com/synthetic-b.png";
            LookupDelegate(service, "Fetch", delegate(object[] args) { Interlocked.Increment(ref fetches); return second; });
            LookupCall(service, "Refresh", targets[0], form, (Action<string, string, byte[]>)delegate(string key, string readyUrl, byte[] png)
            {
                Check(readyUrl.EndsWith("synthetic-b.png") && png != null, "再次选中且头像变化：回填新头像");
                callbacks++;
            });
            WaitWorkbenchCallbacks(delegate { return callbacks == 1; });
            Check((string)LookupCall(service, "Get", "bili:702000") == url, "替换后缓存 URL 更新");
            Check(fetches == 2 && resolves == 3, "只对变化的头像重新下载，不重复核验其他房间");
            LookupCall(service, "Dispose");
        });
    }
    private static int WorkbenchComboIndex(Form form, string field)
    {
        ComboBox box = WorkbenchField(form, field) as ComboBox;
        Check(box != null, "工作台：快捷设置仍是可读下拉 " + field);
        return box.SelectedIndex;
    }

    private static void TestWorkbenchSelectionStability()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form);
            form.GetType().GetMethod("SetTestDpi", ReviewInstance).Invoke(form, new object[] { 96 });
            form.ClientSize = new Size(1240, 720);
            Type ui = form.Tag.GetType();
            var favorites = (IList<KeyValuePair<string, string>>)ui.GetField("favorites", ReviewInstance).GetValue(form.Tag);
            favorites.Clear();
            for (int i = 0; i < 5; i++) favorites.Add(new KeyValuePair<string, string>("合成主播" + i, "bili:" + (702100 + i)));
            ui.GetMethod("RefreshLists", ReviewInstance).Invoke(form.Tag, null);
            WorkbenchShowPage(form, "favorites");
            Control browser = WorkbenchFind(form, "favoritesBrowser");
            Control favList = WorkbenchField(form, "favList");
            browser.GetType().GetProperty("SelectedIndex").SetValue(browser, 2, null);
            Application.DoEvents();
            Check((int)favList.GetType().GetProperty("SelectedIndex").GetValue(favList, null) == 2, "选中卡片回填同一房间");
            int[] before = new int[] {
                WorkbenchComboIndex(form, "qualityBox"), WorkbenchComboIndex(form, "bufferBox"),
                WorkbenchComboIndex(form, "danmakuBox"), WorkbenchComboIndex(form, "scBox"),
                WorkbenchComboIndex(form, "enhanceBox") };
            Control status = WorkbenchFind(form, "launcherStatus");
            status.GetType().GetProperty("Text").SetValue(status, "正在查询 B站 702102 的开播状态…", null);
            Application.DoEvents();
            status.GetType().GetProperty("Text").SetValue(status, "B站 702102：正在直播", null);
            Application.DoEvents();
            Type state = hub.Assembly.GetType("RoomStateKind");
            ui.GetMethod("UpdateBadge", ReviewInstance).Invoke(form.Tag,
                new[] { Call("Parse", "bili:702102", "bili"), Enum.Parse(state, "Live") });
            ui.GetMethod("RefreshLists", ReviewInstance).Invoke(form.Tag, null);
            Application.DoEvents();
            IList current = (IList)ui.GetField("currentFavorites", ReviewInstance).GetValue(form.Tag);
            int listIndex = (int)favList.GetType().GetProperty("SelectedIndex").GetValue(favList, null);
            Check(listIndex >= 0 && ((KeyValuePair<string, string>)current[listIndex]).Value == "bili:702102"
                && (int)browser.GetType().GetProperty("SelectedIndex").GetValue(browser, null) == listIndex,
                "状态与开播标记刷新后仍选中同一房间");
            Check(WorkbenchComboIndex(form, "qualityBox") == before[0] && WorkbenchComboIndex(form, "bufferBox") == before[1]
                && WorkbenchComboIndex(form, "danmakuBox") == before[2] && WorkbenchComboIndex(form, "scBox") == before[3]
                && WorkbenchComboIndex(form, "enhanceBox") == before[4], "状态刷新不重置五个播放设置");
            WorkbenchShowPage(form, "danmaku");
            WorkbenchShowPage(form, "favorites");
            Application.DoEvents();
            current = (IList)ui.GetField("currentFavorites", ReviewInstance).GetValue(form.Tag);
            listIndex = (int)favList.GetType().GetProperty("SelectedIndex").GetValue(favList, null);
            Check(listIndex >= 0 && ((KeyValuePair<string, string>)current[listIndex]).Value == "bili:702102"
                && (int)browser.GetType().GetProperty("SelectedIndex").GetValue(browser, null) == listIndex,
                "切到设置页再回来仍选中同一房间");
            Check(WorkbenchComboIndex(form, "qualityBox") == before[0] && WorkbenchComboIndex(form, "scBox") == before[3]
                && WorkbenchComboIndex(form, "enhanceBox") == before[4], "切页不重置五个播放设置");
        });
    }

    private static void TestWorkbenchSettingsReloadIdempotent()
    {
        WithWorkbenchForm(1, delegate(Form form)
        {
            StopWorkbenchTimers(form);
            Control page = WorkbenchFind(form, "workbenchDanmakuSettings");
            Check(page != null, "弹幕设置页已建立");
            FieldInfo countField = page.GetType().GetField("ReloadedCount", ReviewInstance);
            Check(countField != null, "弹幕设置页记录实际重载次数");
            MethodInfo refresh = page.GetType().GetMethod("RefreshFromConfig", ReviewInstance);
            Check(refresh != null, "弹幕设置页可重复读取配置");
            object combo = WorkbenchFind(page, "workbenchSetting_FontScalePercent");
            Check(combo != null, "弹幕设置页有字号下拉");
            PropertyInfo selected = combo.GetType().GetProperty("SelectedIndex", ReviewInstance);
            refresh.Invoke(page, new object[] { false });
            int loaded = (int)countField.GetValue(page);
            int index = (int)selected.GetValue(combo, null);
            refresh.Invoke(page, new object[] { false });
            Check((int)countField.GetValue(page) == loaded, "相同配置重复进入不重写设置控件");
            Check((int)selected.GetValue(combo, null) == index, "重复读取不改变已选设置");
            refresh.Invoke(page, new object[] { true });
            Check((int)countField.GetValue(page) == loaded + 1, "只有显式重新读取才真正重载");
            Check((int)selected.GetValue(combo, null) == index, "强制重载后设置值保持一致");
        });
    }
}