using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static string DouyinSearchFixture()
    {
        return "{\"status_code\":0,\"myself_user_id\":\"private-current-user\",\"extra\":{\"token\":\"private-response-token\"},\"user_list\":["
            + "{\"user_info\":{\"uid\":9007199254740993,\"sec_uid\":\"MS4wLjAAA_first\",\"nickname\":\"同名主播\",\"unique_id\":\"first.id\",\"signature\":\"游戏简介\",\"room_id\":\"999\",\"avatar_thumb\":{\"url_list\":[\"https://p3.douyinpic.com/avatar.jpg\"]}}},"
            + "{\"user_info\":{\"uid\":\"22\",\"sec_uid\":\"MS4wLjAAA_second\",\"nickname\":\"同名主播\",\"unique_id\":\"second\"}},"
            + "{\"user_info\":{\"uid\":\"22\",\"sec_uid\":\"MS4wLjAAA_second\",\"nickname\":\"重复账号\"}},"
            + "{\"user_info\":{\"uid\":1.25,\"sec_uid\":\"MS4wLjAAA_third\",\"nickname\":\"第三个\"}},"
            + "{\"user_info\":{\"sec_uid\":\"bad/identity\",\"nickname\":\"无效\"}}]}";
    }
    private static object DouyinSearchParse(string body, string query)
    { return LookupCall(LookupType("DouyinUserSearch"), "Parse", body, query, "douyin_http"); }
    private static void TestDouyinSearchProtocol()
    {
        string query = "名字 & offset=999 #/?+中文";
        string url = (string)LookupCall(LookupType("DouyinUserSearch"), "Url", query);
        Check(url.StartsWith("https://www.douyin.com/aweme/v1/web/discover/search/?"), "抖音查询直达官方用户接口，不查询 Bing");
        Check(url.Contains("keyword=" + Uri.EscapeDataString(query)) && url.Contains("offset=0&count=10"), "关键词作为单一编码参数，不能注入分页或片段");
        Check(url.Contains("search_source=normal_search") && url.Contains("need_filter_settings=1"), "使用实际网页普通搜索语义");
        Check(!url.Contains("a_bogus=") && !url.Contains("msToken=") && !url.Contains("uifid=") && !url.Contains("webid="), "普通模板不含固定签名或设备身份令牌");
        Check((bool)LookupCall(LookupType("DouyinUserSearch"), "MatchesRequest", url, query), "当前关键词首屏请求可关联");
        foreach (string wrong in new[] { url.Replace("www.douyin.com", "www.douyin.com.evil.com"), url.Replace("https://", "http://"),
            url.Replace("offset=0", "offset=10"), url + "&keyword=other", url.Replace("aweme_user_web", "aweme_video_web"),
            url.Replace("/discover/search/", "/hot/search/list/"), url.Replace("www.douyin.com/", "www.douyin.com:444/") })
            Check(!(bool)LookupCall(LookupType("DouyinUserSearch"), "MatchesRequest", wrong, query), "拒绝错域/错词/其他分类/其他分页/重复参数请求");
        Check(!(bool)LookupCall(LookupType("DouyinUserSearch"), "MatchesRequest", url, "其他名字"), "旧关键词响应不能回填新查询");
    }
    private static void TestDouyinSearchCandidates()
    {
        object result = DouyinSearchParse(DouyinSearchFixture(), "second"); var rows = (IList)LookupGet(result, "candidates");
        Check(rows.Count == 3, "按 sec_uid 去重，保留不同账号的同名候选，剔除无效主页身份");
        Check((string)LookupGet(rows[0], "account_handle") == "second", "抖音号精确匹配可优先排序");
        object first = rows[1];
        Check((string)LookupGet(first, "account_id") == "9007199254740993", "JSON 整数 ID 超过 2^53 仍无浮点精度损失");
        Check((string)LookupGet(first, "account_handle") == "first.id", "保留含点的有效抖音号");
        Check((string)LookupGet(first, "description") == "游戏简介" && ((string)LookupGet(first, "avatar_url")).EndsWith("avatar.jpg"), "映射公开简介和头像");
        Check(LookupGet(rows[2], "account_id") == null, "拒绝浮点账号 ID，不静默截断");
        foreach (object c in rows) {
            Check(LookupGet(c, "room_id") == null && LookupGet(c, "session_room_id") == null, "搜索里的账号与内部场次不能冒充数字直播房间");
            Check((string)LookupGet(c, "verification") == "unverified" && !(bool)LookupGet(c, "user_confirmed"), "候选不自动核验、选中或收藏");
            Check(LookupCall(LookupType("LookupData"), "FavoriteKey", c) == null, "未核验候选不能通过收藏写入边界");
            foreach (object source in (IList)LookupGet(c, "sources")) Check(!((string)LookupGet(source, "url")).Contains("a_bogus"), "来源链接只保留官方公开页面");
        }
        string projected = (string)LookupCall(LookupType("LookupAiProtocol"), "PublicContext", rows);
        Check(!projected.Contains("private-current-user") && !projected.Contains("private-response-token"), "不把当前登录用户与响应附加令牌转入候选或 AI 上下文");
    }
    private static void TestDouyinSearchErrors()
    {
        foreach (string body in new[] { "", "   ", "{\"status_code\":2483,\"status_msg\":\"server secret must not surface\"}" })
            Check(LookupThrows(delegate { DouyinSearchParse(body, "主播"); }, "auth"), "空响应和会话拒绝与有效空列表分开");
        Check(LookupThrows(delegate { DouyinSearchParse("{\"status_code\":9}", "主播"); }, "blocked"), "未知业务错误不能冒充无结果");
        foreach (string body in new[] { "<html>verification</html>", "{\"status_code\":0}", "{\"status_code\":0,\"user_list\":{}}",
            "{\"status_code\":0,\"user_list\":[{\"user_info\":{\"nickname\":\"缺少身份\"}}]}" })
            Check(LookupThrows(delegate { DouyinSearchParse(body, "主播"); }, "format"), "非 JSON/缺字段/错误集合/全部无效账号都报告格式变化");
        object empty = DouyinSearchParse("{\"status_code\":0,\"user_list\":[]}", "主播");
        Check(((IList)LookupGet(empty, "candidates")).Count == 0 && ((string)LookupGet(empty, "note")).Contains("不据此判定"), "有效空列表不宣称主播不存在");
        Check(LookupThrows(delegate { DouyinSearchParse(new string('x', 4*1024*1024+1), "主播"); }, "format"), "搜索解析资源预算有界");
    }
    private static void TestDouyinSearchTransport()
    {
        object verifier = LookupNew("LookupVerifier"); int calls = 0;
        LookupDelegate(verifier, "Transport", delegate(object[] args) { calls++; Check(((string)args[0]).Contains("/discover/search/?"), "实际 SearchNative 走抖音官方用户 API"); return LookupReply(200, DouyinSearchFixture(), null); });
        object result = LookupCall(verifier, "SearchNative", "douyin", "同名主播", CancellationToken.None);
        Check(calls == 1 && ((IList)LookupGet(result, "candidates")).Count == 3, "原生查询直接生成候选，不等待公开索引");
        LookupDelegate(verifier, "Transport", delegate { calls++; return LookupReply(200, "{\"status_code\":2483}", null); });
        Check(LookupThrows(delegate { LookupCall(verifier, "SearchNative", "douyin", "主播", CancellationToken.None); }, "auth") && calls == 2, "会话失效不偷偷降级到 Bing 并伪装平台成功");
        var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled = false;
        try { LookupCall(verifier, "SearchNative", "douyin", "主播", cancel.Token); } catch (TargetInvocationException ex) { cancelled = ex.InnerException is OperationCanceledException; }
        Check(cancelled && calls == 2, "已取消的抖音查询不发网络请求"); cancel.Dispose();
    }
    private static void TestDouyinNumericHandle()
    {
        object service=LookupNew("LookupService");int calls=0;
        LookupDelegate(service,"PlatformSearch",delegate(object[] args){calls++;Check((string)args[0]=="douyin" && (string)args[1]=="1076554100","显式抖音号只查询对应平台的账号");return DouyinSearchParse(DouyinSearchFixture(),"1076554100");});
        object account=LookupCall(service,"Search","douyin","@1076554100",false,false,CancellationToken.None,(Action<string>)delegate{});
        Check(calls==1 && (string)LookupGet(account,"status")!="direct","纯数字抖音号加 @ 后不被识别为直播房间");
        LookupCall(service,"Search","all","抖音号：1076554100",false,false,CancellationToken.None,(Action<string>)delegate{});
        Check(calls==2,"全部平台支持明确的抖音号前缀，而非猜测数字归属");
        LookupCall(service,"Search","douyin","@1076554100 游戏/领域：直播",false,false,CancellationToken.None,(Action<string>)delegate{});
        Check(calls==3,"补充身份线索不会被拼入抖音号或误报账号格式错误");
        object room=LookupCall(service,"Search","douyin","1076554100",false,false,CancellationToken.None,(Action<string>)delegate{});
        Check(calls==3 && (string)LookupGet(room,"status")=="direct","保留裸数字房间输入的既有语义");
        Check(LookupThrows(delegate{LookupCall(service,"Search","douyin","@../bad",false,false,CancellationToken.None,(Action<string>)delegate{});},"input"),"抖音号前缀不允许注入链接或路径");
    }
    private static void TestDouyinCredentialCache()
    {
        var field = hub.GetField("RuntimeDir", LookupFlags); object previous = field.GetValue(null);
        string directory = Path.Combine(ReviewDirectory(), "douyin-cache"); Directory.CreateDirectory(directory); field.SetValue(null, directory);
        try {
            object service = LookupNew("LookupService"); int calls = 0;
            LookupDelegate(service, "PlatformSearch", delegate { calls++; return DouyinSearchParse(DouyinSearchFixture(), "主播"); });
            LookupCall(service, "Search", "douyin", "主播", false, false, CancellationToken.None, (Action<string>)delegate {});
            LookupCall(service, "Search", "douyin", "主播", false, false, CancellationToken.None, (Action<string>)delegate {});
            Check(calls == 1, "同一 Cookie 状态下复用搜索缓存");
            string path = Path.Combine(directory, "douyin-cookies.txt.json"); File.WriteAllText(path, "synthetic cache revision");
            LookupCall(service, "Search", "douyin", "主播", false, false, CancellationToken.None, (Action<string>)delegate {});
            Check(calls == 2, "重新登录生成旁文件后不复用旧会话的查询缓存");
            File.Delete(path);
            LookupCall(service, "Search", "douyin", "主播", false, false, CancellationToken.None, (Action<string>)delegate {});
            Check(calls == 3, "删除 Cookie 后不复活登录前的同词缓存");
            LookupDelegate(service, "PlatformSearch", delegate { calls++; return LookupResult(); });
            LookupCall(service, "Refresh", "douyin", "主播", CancellationToken.None, (Action<string>)delegate {});
            LookupCall(service, "Search", "douyin", "主播", false, false, CancellationToken.None, (Action<string>)delegate {});
            Check(calls == 5, "刷新撤销旧成功，空列表不会被当作成功缓存");
        } finally { field.SetValue(null, previous); }
    }
    private static void TestDouyinCookieSameSite()
    {
        Type cookies = hub.Assembly.GetType("CredentialCookies");
        object list = LookupCall(cookies, "ParseCdp", "{\"cookies\":[{\"name\":\"test\",\"value\":\"synthetic\",\"domain\":\".douyin.com\",\"path\":\"/\",\"secure\":true,\"httpOnly\":true,\"sameSite\":\"Strict\",\"expires\":0}]}");
        Check((string)LookupGet(((IList)list)[0], "SameSite") == "Strict", "浏览器导出保留 SameSite");
        string path = Path.Combine(ReviewDirectory(), "samesite-cookies.txt");
        Check(LookupCall(cookies, "Save", path, list) != null, "结构化 Cookie 可保存");
        object loaded = LookupCall(cookies, "Load", path);
        Check((string)LookupGet(((IList)loaded)[0], "SameSite") == "Strict", "保存再读取不丢 SameSite");
        object legacy = LookupCall(cookies, "ParseCdp", "{\"cookies\":[{\"name\":\"old\",\"value\":\"synthetic\",\"domain\":\".douyin.com\"}]}");
        Check(((IList)legacy).Count == 1, "旧 Cookie 旁文件没有 SameSite 时仍可兼容");
    }
    private static void TestDouyinBrowserAssets()
    {
        string root = Path.GetDirectoryName(hub.Assembly.Location);
        LookupCall(LookupType("DouyinLookupBrowser"), "VerifyAssets", root);
        Check(true, "实际构建随带 SDK 与两种架构加载器，固定哈希均通过");
        string altered = Path.Combine(ReviewDirectory(), "bad-webview"); Directory.CreateDirectory(altered);
        File.WriteAllText(Path.Combine(altered, "Microsoft.Web.WebView2.Core.dll"), "tampered synthetic asset");
        bool rejected = false;
        try { LookupCall(LookupType("DouyinLookupBrowser"), "VerifyAssets", altered); } catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
        Check(rejected, "组件被替换时在浏览器加载之前拒绝");
    }
    private static void TestDouyinBrowserLogout()
    {
        var field=hub.GetField("RuntimeDir",LookupFlags);object old=field.GetValue(null);
        string directory=Path.Combine(ReviewDirectory(),"logout-webview");Directory.CreateDirectory(directory);field.SetValue(null,directory);
        try {
            string profile=Path.Combine(directory,"douyin-webview-login-profile","Default");Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile,"Cookies"),"synthetic profile");
            Check((string)LookupCall(hub.Assembly.GetType("LocalData"),"Logout","douyin")=="", "抖音退出登录包括新的网页辅助会话");
            Check(!Directory.Exists(Path.GetDirectoryName(profile)), "退出后删除网页辅助 profile，不能从旧会话静默恢复登录");
        }finally{field.SetValue(null,old);}
    }
    private static void TestDouyinBrowserUi()
    {
        Exception failure = null; var thread = new Thread(delegate() {
            object ui = null; int saves = 0, browsers = 0;
            try {
                Type type = LookupType("LookupUi"); ConstructorInfo ctor = type.GetConstructors(LookupFlags)[0];
                foreach(var c in type.GetConstructors(LookupFlags)) if(c.GetParameters().Length==3)ctor=c;
                Delegate save = LookupMakeDelegate(ctor.GetParameters()[2].ParameterType, delegate { saves++; return null; });
                ui = ctor.Invoke(new object[] { "douyin", "同名主播", save }); object form = LookupGet(ui, "Form");
                LookupSet(form, "StartPosition", Enum.Parse(form.GetType().GetProperty("StartPosition").PropertyType, "Manual"));
                LookupSet(form, "ShowInTaskbar", false); LookupSet(form, "Left", -20000); LookupSet(form, "Top", -20000); LookupCall(form, "Show");
                LookupDelegate(ui, "BrowserSearch", delegate(object[] args) { browsers++; Check((string)args[1] == "同名主播", "网页辅助只使用当前昵称"); return DouyinSearchParse(DouyinSearchFixture(), "同名主播"); });
                object button = LookupGet(ui, "DouyinBrowser"), list = LookupGet(ui, "Candidates");
                Check((bool)LookupGet(button, "Enabled") && browsers==0, "普通查找窗口不自动启动网页辅助");
                LookupCall(button, "PerformClick");
                Check(browsers==1 && ((IList)LookupGet(list, "Items")).Count==3, "手动网页辅助结果回填现有候选列表");
                Check((int)LookupGet(list, "SelectedIndex")==-1 && !(bool)LookupGet(LookupGet(ui,"Add"),"Enabled") && saves==0, "网页结果不自动选中、核验或收藏");
                LookupSet(LookupGet(ui,"Input"),"Text","另一个主播");
                Check(((IList)LookupGet(list,"Items")).Count==0, "换关键词立即清除上一查询的网页候选");
                LookupSet(LookupGet(ui,"Platform"),"SelectedIndex",0);
                Check(!(bool)LookupGet(button,"Enabled"), "B站单平台查询不显示可用抖音辅助入口");
                LookupCall(ui,"BeginBrowserSearch"); Check(browsers==1,"不能把B站查询误发到抖音辅助");
            }catch(Exception ex){failure=ex;}finally{if(ui!=null)LookupCall(ui,"Dispose");}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Check(thread.Join(6000),"网页辅助控件测试有界结束");if(failure!=null)throw failure;
    }
    private static void RegisterDouyinSearchCases()
    {
        TestRunner.Add("抖音用户搜索", "官方请求与关键词关联边界", TestDouyinSearchProtocol);
        TestRunner.Add("抖音用户搜索", "身份精度、去重、同名与房间隔离", TestDouyinSearchCandidates);
        TestRunner.Add("抖音用户搜索", "会话拒绝、格式变化与空列表区分", TestDouyinSearchErrors);
        TestRunner.Add("抖音用户搜索", "原生请求、失败与取消", TestDouyinSearchTransport);
        TestRunner.Add("抖音用户搜索", "数字抖音号与直播房间输入区分", TestDouyinNumericHandle);
        TestRunner.Add("抖音用户搜索", "Cookie 更新与缓存隔离", TestDouyinCredentialCache);
        TestRunner.Add("抖音用户搜索", "SameSite 导入导出兼容", TestDouyinCookieSameSite);
        TestRunner.Add("抖音用户搜索", "SDK 组件完整性边界", TestDouyinBrowserAssets);
        TestRunner.Add("抖音用户搜索", "退出登录清理网页辅助会话", TestDouyinBrowserLogout);
        TestRunner.Add("抖音用户搜索", "手动网页回填及跨平台隔离", TestDouyinBrowserUi);
    }
}
