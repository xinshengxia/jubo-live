using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static void RegisterLookupDiscoveryCases()
    {
        TestRunner.Add("主播查询改进", "四平台并发、部分失败、独立缓存与刷新", TestLookupAllPlatforms);
        TestRunner.Add("主播查询改进", "全部平台取消阻止迟到结果和缓存写入", TestLookupAllCancellation);
        TestRunner.Add("主播查询改进", "有来源AI排序、冲突保留及稳定身份隔离", TestLookupReviewRanking);
        TestRunner.Add("主播查询改进", "曾用名归一化、公会前缀与粉丝账号隔离", TestLookupAliases);
        TestRunner.Add("主播查询改进", "账号保留、房间续查与收藏权限分阶段", TestLookupAccountStages);
        TestRunner.Add("主播查询改进", "刷新真实控件与独立来源摘录", TestLookupDiscoveryUi);
        TestRunner.Add("主播查询改进", "全平台结果筛选、迟到核验隔离及AI跨平台置顶", TestLookupResultFilters);
    }
    private static void TestLookupAllPlatforms()
    {
        object service = LookupNew("LookupService"); int calls = 0;
        var entered = new CountdownEvent(4);
        LookupDelegate(service, "PlatformSearch", delegate(object[] args) {
            int request = Interlocked.Increment(ref calls);
            if (request <= 4) { entered.Signal(); if (!entered.Wait(3000)) throw new Exception("平台没有并发进入"); }
            string platform = (string)args[0];
            if (platform == "douyu") throw LookupError("network");
            return LookupResult(LookupCandidate(platform, "123", "456"));
        });
        LookupDelegate(service, "Ai", delegate { throw new Exception("普通查询不能请求AI"); });
        object result = LookupCall(service, "Search", "all", "同名主播", false, false, CancellationToken.None, (Action<string>)delegate {});
        IList candidates = (IList)LookupGet(result, "candidates");
        Check(calls == 4 && (string)LookupGet(result, "status") == "partial" && candidates.Count == 3, "全部平台四路并发，一平台受阻仍保留其他三平台结果");
        Check((string)LookupGet(candidates[0], "platform") == "bili" && (string)LookupGet(candidates[1], "platform") == "huya"
            && (string)LookupGet(candidates[2], "platform") == "douyin", "同名同编号跨平台分别展示，按平台分组");
        IList outcomes = (IList)LookupGet(result, "platforms");
        Check(outcomes.Count == 4 && (string)LookupGet(outcomes[2], "error_code") == "network", "各平台结果携带独立失败原因，不伪装成空结果");
        Check(LookupGet(outcomes[0], "searched_at") != null, "平台搜索记录缓存原始时间");
        result = LookupCall(service, "Search", "all", "同名主播", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check(calls == 5 && (bool)LookupGet(((IList)LookupGet(result, "platforms"))[0], "cached"), "成功平台复用独立缓存，受阻平台重新请求");
        result = LookupCall(service, "Refresh", "all", "同名主播", CancellationToken.None, (Action<string>)delegate {});
        Check(calls == 9 && !(bool)LookupGet(result, "cached"), "全部平台刷新绕过各自缓存，重新发起四路HTTP");
        result = LookupCall(service, "Search", "all", "https://www.huya.com/777", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check(calls == 9 && (string)LookupGet(((IList)LookupGet(result, "candidates"))[0], "platform") == "huya", "完整链接在全部平台中自动识别平台，不进行四路搜索");
        Check(LookupThrows(delegate { LookupCall(service, "Search", "all", "777", false, false, CancellationToken.None, (Action<string>)delegate {}); }, "input"), "裸数字平台不明确时提示选择平台");
        result = LookupCall(service, "Search", "all", "https://space.bilibili.com/123", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check((string)LookupGet(result, "status") == "direct" && (string)LookupGet(((IList)LookupGet(result, "candidates"))[0], "account_id") == "123", "全部平台识别官方账号页");

        object refreshService = LookupNew("LookupService"); int refreshCalls = 0;
        LookupDelegate(refreshService, "PlatformSearch", delegate { refreshCalls++; return refreshCalls == 1 ? LookupResult(LookupCandidate("bili", "123", "456")) : LookupResult(); });
        LookupCall(refreshService, "Search", "bili", "缓存验证", false, false, CancellationToken.None, (Action<string>)delegate {});
        LookupCall(refreshService, "Refresh", "bili", "缓存验证", CancellationToken.None, (Action<string>)delegate {});
        LookupCall(refreshService, "Search", "bili", "缓存验证", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check(refreshCalls == 3, "刷新空结果也撤销旧缓存，下次查询不会重新出现过时成功列表");
        object hintsService = LookupNew("LookupService");
        object wrongGame = LookupCandidate("bili", "1", "11"), correctGame = LookupCandidate("bili", "2", "22");
        LookupSet(wrongGame, "category", "其他游戏"); LookupSet(correctGame, "category", "英雄联盟");
        LookupDelegate(hintsService, "PlatformSearch", delegate(object[] args) { Check((string)args[1] == "同名主播", "补充线索不会被拼成平台昵称检索词"); return LookupResult(wrongGame, correctGame); });
        object hinted = LookupCall(hintsService, "Search", "bili", "同名主播 游戏/领域：英雄联盟 战队/身份：职业选手", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check((string)LookupGet(((IList)LookupGet(hinted, "candidates"))[0], "account_id") == "2", "有平台公开类别依据的游戏线索参与排序，不合并同名账号");
        object errorVerifier = LookupNew("LookupVerifier");
        foreach (int status in new[] { 401, 429, 404 })
        {
            int currentStatus = status; LookupDelegate(errorVerifier, "Transport", delegate { return LookupReply(currentStatus, "", null); });
            string expected = status == 401 ? "auth" : status == 429 ? "rate-limit" : "not-found";
            Check(LookupThrows(delegate { LookupCall(errorVerifier, "Get", "https://www.huya.com/456", CancellationToken.None); }, expected), "HTTP " + status + " 保留对应失败原因和操作出口");
        }
        entered.Dispose();
    }
    private static void TestLookupAllCancellation()
    {
        object service = LookupNew("LookupService"); var cancel = new CancellationTokenSource(); var entered = new CountdownEvent(4);
        var release = new ManualResetEvent(false); Exception failure = null; bool cancelled = false;
        LookupDelegate(service, "PlatformSearch", delegate(object[] args) {
            entered.Signal(); release.WaitOne(3000); return LookupResult(LookupCandidate((string)args[0], "123", "456"));
        });
        var worker = new Thread(delegate() {
            try { LookupCall(service, "Search", "all", "可取消查询", false, false, cancel.Token, (Action<string>)delegate {}); }
            catch (TargetInvocationException ex) { if (ex.InnerException is OperationCanceledException) cancelled = true; else failure = ex; }
        });
        worker.Start();
        try {
            Check(entered.Wait(3000), "全部平台取消测试四路请求已经进入");
            cancel.Cancel(); Check(worker.Join(1000) && cancelled && failure == null, "取消及时返回，不等待忽略取消的旧请求");
        } finally { cancel.Cancel(); release.Set(); worker.Join(3000); }
        int calls = 0; LookupDelegate(service, "PlatformSearch", delegate(object[] args) { Interlocked.Increment(ref calls); return LookupResult(); });
        LookupCall(service, "Search", "all", "可取消查询", false, false, CancellationToken.None, (Action<string>)delegate {});
        Check(calls == 4, "迟到平台结果不得写入取消查询的缓存");
        // Events remain valid while previously released provider calls unwind.
    }
    private static void TestLookupReviewRanking()
    {
        object first = LookupCandidate("huya", "1", "11"), second = LookupCandidate("huya", "2", "22");
        LookupSet(first, "verification", "verified"); LookupSet(first, "user_confirmed", true);
        var current = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(LookupType("LookupCandidate"))); current.Add(first); current.Add(second);
        string json = "{\"candidates\":[],\"reviews\":[{\"platform\":\"huya\",\"account_id\":\"2\",\"decision\":\"supported\",\"reason\":\"职业经历与用户描述相符\",\"source_indices\":[1]},"
            + "{\"platform\":\"huya\",\"account_id\":\"1\",\"decision\":\"conflict\",\"reason\":\"公开简介属于另一个游戏领域\",\"source_indices\":[2]}]}";
        string sources = "[{\"index\":1,\"url\":\"https://www.huya.com/22\",\"snippet\":\"职业经历\"},{\"index\":2,\"url\":\"https://www.huya.com/11\",\"snippet\":\"其他游戏\"}]";
        object parser = LookupParser(); LookupFeed(parser, json, sources, "stop"); object reviewed = LookupCall(parser, "Complete", "huya");
        object service = LookupNew("LookupService"); LookupDelegate(service, "Optimize", delegate { return reviewed; });
        object result = LookupCall(service, "Improve", "huya", "玩家描述", current, CancellationToken.None, (Action<string>)delegate {});
        IList ranked = (IList)LookupGet(result, "candidates");
        Check(ranked.Count == 2 && (string)LookupGet(ranked[0], "account_id") == "2" && (string)LookupGet(ranked[1], "account_id") == "1", "AI来源判断让符合描述的后排候选前移，冲突候选保留");
        Check((string)LookupGet(ranked[1], "relevance") == "conflict" && ((IList)LookupGet(ranked[1], "relevance_sources")).Count == 1, "冲突说明绑定可查看的真实来源");
        Check((string)LookupGet(ranked[0], "room_id") == "22" && (string)LookupGet(ranked[1], "room_id") == "11"
            && !(bool)LookupGet(ranked[1], "user_confirmed") && (string)LookupGet(ranked[1], "verification") == "unverified", "排序不修改房间归属，也不沿用用户确认");
        Check((string)LookupGet(first, "verification") == "verified", "优化使用快照，不修改传入候选对象");
        parser = LookupParser(); LookupFeed(parser, json.Replace("[1]", "[99]").Replace("[2]", "[99]"), sources, "stop");
        Check(((IList)LookupGet(LookupCall(parser, "Complete", "huya"), "reviews")).Count == 0, "引用越界的匹配判断被拒绝，不能影响排序");
        parser = LookupParser(); LookupFeed(parser, "{\"candidates\":[],\"reviews\":[{\"platform\":\"huya\",\"account_id\":\"2\",\"room_id\":\"11\",\"decision\":\"supported\",\"reason\":\"同名\",\"source_indices\":[1]}]}", sources, "stop");
        object cross = LookupCall(parser, "Complete", "huya"); LookupCall(LookupType("LookupDiscovery"), "ApplyReviews", cross, current);
        Check((string)LookupGet(first, "relevance") == "ambiguous", "房间相同但账号不同的评论不能跨账号应用");
        parser = LookupParser();
        LookupFeed(parser, "{\"candidates\":[{\"platform\":\"douyu\",\"room_id\":\"789\",\"source_indices\":[1]}]}", "[{\"index\":1,\"url\":\"https://www.douyu.com/789\"}]", "stop");
        Check(((IList)LookupGet(LookupCall(parser, "Complete", "all"), "candidates")).Count == 1, "全平台检索允许其他平台候选，无需编造转平台公告");
    }
    private static void TestLookupResultFilters()
    {
        Exception failure = null;
        var thread = new Thread(delegate()
        {
            object ui = null; var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            try
            {
                ui = Activator.CreateInstance(LookupType("LookupUi"), LookupFlags, null, new object[] { "all", "", null }, null);
                object form = LookupGet(ui, "Form");
                LookupSet(form, "ShowInTaskbar", false); LookupCall(form, "Show");
                LookupSet(form, "Left", -20000); LookupSet(form, "Top", -20000);
                Check((string)LookupGet(LookupGet(ui, "Platform"), "Text") == "全部平台", "全平台入口默认显示全部平台");
                object service = LookupGet(ui, "Service"), list = LookupGet(ui, "Candidates"), cancel = LookupGet(ui, "Cancel");
                int calls = 0;
                LookupDelegate(service, "PlatformSearch", delegate(object[] args) {
                    Interlocked.Increment(ref calls); return LookupResult(LookupCandidate((string)args[0], "1", "11"));
                });
                LookupSet(LookupGet(ui, "Input"), "Text", "测试主播"); LookupCall(ui, "BeginSearch", false, false);
                LookupPumpUntil(delegate { return !(bool)LookupGet(cancel, "Enabled"); }, 2500);
                Check(calls == 4 && ((IList)LookupGet(list, "Items")).Count == 4, "默认查询汇总四平台账号");
                var filters = (IDictionary)LookupGet(ui, "PlatformFilters");
                Check(filters.Count == 5 && ((string)LookupGet(filters["all"], "Text")).Contains("4")
                    && ((string)LookupGet(filters["huya"], "Text")).Contains("1"), "平台筛选按钮显示全部及各平台数量");
                LookupDelegate(LookupGet(ui, "Verifier"), "Transport", delegate {
                    entered.Set(); release.WaitOne(2000);
                    return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":11,\"uid\":1}}", null);
                });
                LookupSet(list, "SelectedIndex", 0); Check(entered.WaitOne(1000), "筛选测试已进入旧账号核验");
                LookupCall(filters["huya"], "PerformClick"); release.Set(); LookupPumpUntil(delegate { return false; }, 150);
                Check(((IList)LookupGet(list, "Items")).Count == 1 && (string)LookupGet(((IList)LookupGet(list, "Items"))[0], "platform") == "huya",
                    "点击虎牙仅展示本次虎牙结果");
                Check(calls == 4 && (int)LookupGet(list, "SelectedIndex") == -1 && !(bool)LookupGet(LookupGet(ui, "Add"), "Enabled"),
                    "筛选不重新请求网络，迟到核验不能恢复旧收藏权限");
                foreach (string key in new[] { "bili", "douyu", "douyin", "all" })
                {
                    LookupCall(filters[key], "PerformClick");
                    Check(((IList)LookupGet(list, "Items")).Count == (key == "all" ? 4 : 1), "切换" + key + "保留完整搜索集合");
                }
                LookupCall(filters["bili"], "PerformClick");
                LookupDelegate(service, "Optimize", delegate(object[] args) {
                    Check((string)args[0] == "all" && ((IList)args[2]).Count == 4, "筛选后AI优化仍接收完整跨平台候选，不丢隐藏账号");
                    object result = LookupResult(), review = LookupNew("LookupReview"), source = LookupNew("LookupSource");
                    LookupSet(review, "platform", "huya"); LookupSet(review, "account_id", "1");
                    LookupSet(review, "decision", "supported"); LookupSet(review, "reason", "平台公开资料支持目标身份");
                    LookupSet(source, "url", "https://www.huya.com/11"); ((IList)LookupGet(review, "sources")).Add(source);
                    ((IList)LookupGet(result, "reviews")).Add(review); return result;
                });
                LookupCall(LookupGet(ui, "Deep"), "PerformClick");
                LookupPumpUntil(delegate { return !(bool)LookupGet(cancel, "Enabled"); }, 2500);
                Check(((IList)LookupGet(list, "Items")).Count == 2 && (string)LookupGet(((IList)LookupGet(list, "Items"))[0], "platform") == "huya"
                    && (string)LookupGet(((IList)LookupGet(list, "Items"))[1], "platform") == "bili" && (bool)LookupGet(filters["bili"], "Selected"),
                    "保留B站筛选时，AI最适配虎牙候选仍固定第一，其后显示B站结果");
                Check(object.ReferenceEquals(LookupGet(list, "Recommendation"), ((IList)LookupGet(list, "Items"))[0]),
                    "跨平台置顶项使用AI优先标记，区别于普通平台结果");
                LookupCall(filters["douyu"], "PerformClick");
                Check(((IList)LookupGet(list, "Items")).Count == 2 && (string)LookupGet(((IList)LookupGet(list, "Items"))[0], "platform") == "huya"
                    && (string)LookupGet(((IList)LookupGet(list, "Items"))[1], "platform") == "douyu", "再次切换平台也不会隐藏AI置顶候选");
                LookupCall(filters["huya"], "PerformClick");
                Check(((IList)LookupGet(list, "Items")).Count == 1, "推荐与筛选平台一致时只展示一次，不重复插入");
                LookupCall(filters["all"], "PerformClick");
                Check(((IList)LookupGet(list, "Items")).Count == 4 && (string)LookupGet(((IList)LookupGet(list, "Items"))[0], "platform") == "huya",
                    "AI最匹配虎牙候选跨平台置顶，固定平台顺序不再压住推荐");
                Check((int)LookupGet(list, "SelectedIndex") == -1 && !(bool)LookupGet(LookupGet(ui, "Add"), "Enabled"),
                    "AI置顶只调整展示顺序，仍由用户选择核验");
                string output = System.IO.Path.Combine(SourceRoot, "artifacts", "lookup-filters-preview"); System.IO.Directory.CreateDirectory(output);
                SetDpi((System.Windows.Forms.Form)form, 144);
                SaveDpiForm((System.Windows.Forms.Form)form, System.IO.Path.Combine(output, "lookup-filter-all-144.png"));
                LookupCall(filters["bili"], "PerformClick");
                SaveDpiForm((System.Windows.Forms.Form)form, System.IO.Path.Combine(output, "lookup-filter-bili-ai-pinned-144.png"));
                LookupSet(LookupGet(ui, "Input"), "Text", "新的查询");
                Check(((IList)LookupGet(list, "Items")).Count == 0 && LookupGet(list, "Recommendation") == null,
                    "修改查询清除旧AI推荐，不能把上一位主播置顶到新搜索中");
            }
            catch (Exception ex) { failure = ex; }
            finally { release.Set(); if (ui != null) LookupCall(ui, "Dispose"); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(15000)) throw new Exception("平台筛选UI测试超时"); if (failure != null) throw failure;
    }
    private static void TestLookupAliases()
    {
        Type avatars = LookupType("LookupAvatarCache");
        Check((bool)LookupCall(avatars, "Allowed", "https://i0.hdslb.com/avatar.png")
            && !(bool)LookupCall(avatars, "Allowed", "https://i0.hdslb.com.evil.example/avatar.png")
            && !(bool)LookupCall(avatars, "Allowed", "http://i0.hdslb.com/avatar.png"), "头像仅接受实际平台HTTPS CDN，拒绝仿冒域与明文下载");
        object cache = LookupNew("LookupAvatarCache"); int downloads = 0;
        LookupDelegate(cache, "Download", delegate { downloads++; return new byte[262145]; });
        Check(LookupCall(cache, "Load", "https://evil.example/avatar.png", CancellationToken.None) == null && downloads == 0, "任意来源中的头像网址不触发下载");
        Check(LookupCall(cache, "Load", "https://i0.hdslb.com/avatar.png", CancellationToken.None) == null && downloads == 1, "超过头像字节限额的响应不交给图像解码");
        Type bitmapType = cache.GetType().GetMethod("Load", LookupFlags).ReturnType;
        object bitmap = Activator.CreateInstance(bitmapType, new object[] { 2, 2 });
        Type formatType = bitmapType.Assembly.GetType("System.Drawing.Imaging.ImageFormat");
        object format = formatType.GetProperty("Png").GetValue(null, null);
        using (var stream = new System.IO.MemoryStream())
        {
            bitmapType.GetMethod("Save", new[] { typeof(System.IO.Stream), formatType }).Invoke(bitmap, new object[] { stream, format });
            byte[] bytes = stream.ToArray(); LookupDelegate(cache, "Download", delegate { return bytes; });
            object thumbnail = LookupCall(cache, "Load", "https://i0.hdslb.com/avatar.png", CancellationToken.None);
            Check((int)LookupGet(thumbnail, "Width") == 48 && (int)LookupGet(thumbnail, "Height") == 48, "合法平台头像解码为有界缩略图");
            LookupCall(thumbnail, "Dispose");
        }
        LookupCall(bitmap, "Dispose"); LookupCall(cache, "Dispose");
        Type matching = LookupType("LookupDiscovery");
        Check((bool)LookupCall(matching, "AliasMatch", "某公会丶 Career-ID", "careerID"), "公会前缀、空格、标点与大小写可召回艺名");
        Check((bool)LookupCall(matching, "AliasMatch", "ｋＲＹＳＴ４Ｌ", "kRYST4L"), "全半角艺名归一化");
        Check(!(bool)LookupCall(matching, "AliasMatch", "careerID的粉丝", "careerID")
            && !(bool)LookupCall(matching, "AliasMatch", "非本人丶careerID", "careerID")
            && !(bool)LookupCall(matching, "AliasMatch", "careerID丶剪辑", "careerID"), "粉丝尾项和非本人前缀不能冒充艺名匹配");
        object research = LookupNew("DeepSeekResearch"), clue = LookupCandidate("huya", null, null);
        LookupSet(clue, "lookup_terms", new[] { "Old-ID", "CareerID" });
        object actual = LookupCandidate("huya", "123", "456"); LookupSet(actual, "display_name", "某公会丶career-id");
        object twin = LookupCandidate("huya", "124", "457"); LookupSet(twin, "display_name", "careerID");
        int requests = 0;
        LookupDelegate(research, "PlatformSearch", delegate(object[] args) { requests++; return (string)args[1] == "Old-ID" ? LookupResult() : LookupResult(actual, twin); });
        object result = LookupResult(clue); LookupCall(research, "Enrich", result, CancellationToken.None, (Action<string>)delegate {});
        IList enriched = (IList)LookupGet(result, "candidates");
        Check(requests == 2 && enriched.Count == 2, "曾用名未命中时继续查询有来源的职业ID，召回带公会前缀的账号");
        Check((string)LookupGet(enriched[0], "account_id") != (string)LookupGet(enriched[1], "account_id")
            && (string)LookupGet(enriched[0], "verification") == "unverified", "名字归一化只扩大候选，稳定账号分别保留并重新核验");
    }
    private static void TestLookupAccountStages()
    {
        object verifier = LookupNew("LookupVerifier"); bool available = false;
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            string url = (string)args[0];
            if (url.Contains("/nav")) return LookupReply(200, "{\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/abcdefghijklmnopqrstuvwxyz012345.png\",\"sub_url\":\"https://i0.hdslb.com/abcdefghijklmnopqrstuvwxyz012345.png\"}}}", null);
            if (url.Contains("/wbi/search/")) return LookupReply(200, "{\"code\":0,\"data\":{\"result\":[{\"uname\":\"主播\",\"uid\":123,\"roomid\":0,\"area_name\":\"游戏\",\"sign\":\"职业简介\"}]}}", null);
            if (url.Contains("getRoomInfoOld")) return LookupReply(200, available ? "{\"code\":0,\"data\":{\"roomid\":456}}" : "{\"code\":0,\"data\":{\"roomid\":0}}", null);
            if (url.Contains("get_info")) return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":456,\"uid\":123,\"area_name\":\"游戏\",\"live_status\":0}}", null);
            return LookupReply(200, "{\"code\":0,\"data\":{\"info\":{\"uid\":123,\"uname\":\"平台最新昵称\"}}}", null);
        });
        object native = LookupCall(verifier, "SearchNative", "bili", "主播", CancellationToken.None);
        IList candidates = (IList)LookupGet(native, "candidates"); Check(candidates.Count == 1, "没有数字房间但有稳定UID的账号保留在搜索结果");
        object candidate = candidates[0];
        Check(LookupGet(candidate, "room_id") == null && (string)LookupGet(candidate, "category") == "游戏"
            && (string)LookupGet(candidate, "description") == "职业简介", "账号摘要保留平台分类和简介，数字0不当作房间");
        object result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "account-only" && (bool)LookupGet(result, "account_verified")
            && (string)LookupGet(result, "failure_code") == "no-room", "账号资料已核实但无房间时进入独立账号阶段");
        Check(LookupCall(LookupType("LookupData"), "VerifiedRoomKey", result) == null, "账号阶段禁止加入房间收藏");
        Check((string)LookupCall(LookupType("LookupDiscovery"), "AccountPage", result) == "https://space.bilibili.com/123", "房间尚未取得仍可查看官方账号主页");
        available = true; result = LookupCall(verifier, "Verify", result, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && (string)LookupGet(result, "room_id") == "456", "稍后重新解析可从同一账号取得并核验数字房间");
        LookupSet(candidate, "relevance", "conflict"); LookupSet(candidate, "relevance_note", "新的身份线索有冲突");
        LookupDelegate(verifier, "Transport", delegate { throw new Exception("此处必须命中已核验的同账号映射缓存"); });
        object cached = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(cached, "relevance") == "conflict", "近期归属缓存不覆盖新身份匹配意见");
        Check(((string)LookupCall(LookupType("LookupDiscovery"), "Recovery", "no-room", false)).Contains("主页"), "无房间结果给出账号主页和继续解析出口");
    }
    private static void TestLookupDiscoveryUi()
    {
        Exception failure = null; var thread = new Thread(delegate() {
            object ui = null;
            try {
                ui = Activator.CreateInstance(LookupType("LookupUi"), LookupFlags, null, new object[] { "bili", "", null }, null);
                object form = LookupGet(ui, "Form"); LookupSet(form, "StartPosition", Enum.Parse(form.GetType().GetProperty("StartPosition").PropertyType, "Manual"));
                LookupSet(form, "ShowInTaskbar", false); LookupSet(form, "Left", -20000); LookupCall(form, "Show");
                int calls = 0; object service = LookupGet(ui, "Service");
                LookupDelegate(service, "PlatformSearch", delegate {
                    Interlocked.Increment(ref calls); object candidate = LookupCandidate("bili", "123", "456"), source = LookupNew("LookupSource");
                    LookupSet(source, "url", "https://space.bilibili.com/123"); LookupSet(source, "excerpt", "可核对的资料");
                    ((IList)LookupGet(candidate, "sources")).Add(source); return LookupResult(candidate);
                });
                LookupSet(LookupGet(ui, "Input"), "Text", "测试主播"); LookupCall(ui, "BeginSearch", false, false);
                object list = LookupGet(ui, "Candidates"), cancel = LookupGet(ui, "Cancel");
                LookupPumpUntil(delegate { return !(bool)LookupGet(cancel, "Enabled"); }, 2000);
                LookupCall(ui, "BeginSearch", false, false); LookupPumpUntil(delegate { return !(bool)LookupGet(cancel, "Enabled"); }, 2000);
                Check(calls == 1, "重复查询复用搜索缓存");
                LookupCall(LookupGet(ui, "Refresh"), "PerformClick"); LookupPumpUntil(delegate { return !(bool)LookupGet(cancel, "Enabled"); }, 2000);
                Check(calls == 2 && (int)LookupGet(list, "SelectedIndex") == -1, "刷新真实按钮绕过缓存，结果仍不默认选择");
                LookupSet(LookupGet(ui, "Details"), "Text", "账号详情保留");
                object sourceItem = Activator.CreateInstance(LookupType("SourceItem"), LookupFlags, null, new object[] { ((IList)LookupGet(((IList)LookupGet(list, "Items"))[0], "sources"))[0] }, null);
                object sources = LookupGet(ui, "Sources"); ((IList)LookupGet(sources, "Items")).Add(sourceItem); LookupSet(sources, "SelectedIndex", 0);
                Check((string)LookupGet(LookupGet(ui, "Details"), "Text") == "账号详情保留"
                    && ((string)LookupGet(LookupGet(LookupGet(ui, "SourcePreview"), "Inner"), "Text")).Contains("可核对的资料"), "来源摘录使用独立区域，不覆盖账号核验详情");
                Check(((IList)LookupGet(LookupGet(ui, "Platform"), "Items")).Count == 5
                    && ((string)LookupGet(LookupGet(ui, "Clues"), "Text")).Contains("线索"), "界面包含全部平台和补充身份线索入口");
                LookupSet(LookupGet(ui, "Platform"), "SelectedIndex", 4);
                Check(((IList)LookupGet(list, "Items")).Count == 0 && !(bool)LookupGet(LookupGet(ui, "Add"), "Enabled"), "切换全部平台清除旧账号权限");
            } catch (Exception ex) { failure = ex; } finally { if (ui != null) LookupCall(ui, "Dispose"); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); if (!thread.Join(12000)) throw new Exception("新搜索UI测试超时"); if (failure != null) throw failure;
    }
}
