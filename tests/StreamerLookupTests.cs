using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private const BindingFlags LookupFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type LookupType(string name) { return hub.GetNestedType(name, LookupFlags); }
    private static object LookupNew(string name) { return Activator.CreateInstance(LookupType(name), true); }
    private static object LookupCall(object target, string method, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        foreach (MethodInfo info in type.GetMethods(LookupFlags))
            if (info.Name == method && info.GetParameters().Length == args.Length) return info.Invoke(target is Type ? null : target, args);
        throw new MissingMethodException(type.FullName, method);
    }
    private static object LookupGet(object value, string name)
    {
        FieldInfo field = value.GetType().GetField(name, LookupFlags);
        return field != null ? field.GetValue(value) : value.GetType().GetProperty(name, LookupFlags).GetValue(value, null);
    }
    private static void LookupSet(object value, string name, object content)
    {
        FieldInfo field = value.GetType().GetField(name, LookupFlags);
        if (field != null) field.SetValue(value, content); else value.GetType().GetProperty(name, LookupFlags).SetValue(value, content, null);
    }
    private static void LookupDelegate(object target, string field, Func<object[], object> action)
    {
        FieldInfo info = target.GetType().GetField(field, LookupFlags); Type delegateType = info.FieldType;
        info.SetValue(target, LookupMakeDelegate(delegateType, action));
    }
    private static Delegate LookupMakeDelegate(Type delegateType, Func<object[], object> action)
    {
        ParameterInfo[] args = delegateType.GetMethod("Invoke").GetParameters(); var expressions = new ParameterExpression[args.Length]; var boxed = new Expression[args.Length];
        for (int i = 0; i < args.Length; i++) { expressions[i] = Expression.Parameter(args[i].ParameterType, "p" + i); boxed[i] = Expression.Convert(expressions[i], typeof(object)); }
        Expression invoke = Expression.Invoke(Expression.Constant(action), Expression.NewArrayInit(typeof(object), boxed));
        Type returnType = delegateType.GetMethod("Invoke").ReturnType;
        Expression body = returnType == typeof(void) ? (Expression)Expression.Block(invoke, Expression.Empty()) : Expression.Convert(invoke, returnType);
        return Expression.Lambda(delegateType, body, expressions).Compile();
    }
    private static Exception LookupError(string code) { return (Exception)Activator.CreateInstance(LookupType("LookupFailure"), LookupFlags, null, new object[] { code, "合成错误" }, null); }
    private static string LookupEscape(string value) { return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }
    private static object LookupCandidate(string platform, string account, string room)
    {
        object value = LookupNew("LookupCandidate"); LookupSet(value, "platform", platform); LookupSet(value, "display_name", "同名主播");
        LookupSet(value, "account_id", account); LookupSet(value, "room_id", room); LookupSet(value, "source", "ai_web_search"); return value;
    }
    private static object LookupResult(params object[] candidates)
    {
        object result = LookupNew("LookupResult"); foreach (object candidate in candidates) ((IList)LookupGet(result, "candidates")).Add(candidate); return result;
    }
    private static object LookupParser()
    {
        return Activator.CreateInstance(LookupType("LookupAiProtocol").GetNestedType("StreamParser", LookupFlags), true);
    }
    private static void LookupFeed(object parser, string content, string sources, string finish)
    {
        string json = "{\"output\":{\"choices\":[{\"message\":{\"content\":\"" + LookupEscape(content) + "\"},\"finish_reason\":\"" + finish + "\"}],\"search_info\":{\"search_results\":" + sources + "}}}";
        LookupCall(parser, "Feed", json);
    }
    private static string LookupSourceFixture = "[{\"index\":1,\"title\":\"公开身份来源\",\"url\":\"https://space.bilibili.com/123\"}]";
    private static string LookupCandidateJson(string account, string room, string refs)
    {
        return "{\"platform\":\"bilibili\",\"display_name\":\"同名主播\",\"account_id\":" + account + ",\"room_id\":" + room
            + ",\"verified\":true,\"verification\":\"verified\",\"source_indices\":" + refs + ",\"match_reasons\":[\"公开身份线索\"]}";
    }
    private static bool LookupThrows(Action action, string code)
    {
        try { action(); return false; } catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name == "LookupFailure" && (string)LookupGet(ex, "Code") == code;
        }
    }
    private static void TestLookupContract()
    {
        string body = (string)LookupCall(LookupType("LookupAiProtocol"), "RequestBody", "qwen-plus", "douyin", "晚安 火影", false);
        var root = (Dictionary<string, object>)LookupCall(LookupType("LookupData"), "Json", body);
        var parameters = (Dictionary<string, object>)root["parameters"]; var search = (Dictionary<string, object>)parameters["search_options"];
        Check((bool)parameters["enable_search"] && !(bool)parameters["enable_thinking"] && (bool)parameters["incremental_output"], "原生请求开启联网、非思考和增量流式输出");
        Check((bool)search["forced_search"] && (bool)search["enable_source"] && (bool)search["prepend_search_result"] && (string)search["search_strategy"] == "max", "显式AI使用完整检索、真实来源、强制联网与提前来源");
        Check((string)((Dictionary<string, object>)parameters["response_format"])["type"] == "json_object", "启用原生JSON模式避免Markdown破坏候选格式");
        Check((int)parameters["max_tokens"] == 1200 && !body.Contains("Authorization") && !body.Contains("sk-"), "详细提示词使用1200 Token结构化输出预算");
        string deep = (string)LookupCall(LookupType("LookupAiProtocol"), "RequestBody", "qwen-plus", "bili", "鬼叔", true);
        Check(deep.Contains("\"search_strategy\":\"max\""), "主动深搜单次采用max");
        Type config = LookupType("DeepSeekLookupConfig");
        Check((string)config.GetField("Endpoint", LookupFlags).GetRawConstantValue() == "https://api.deepseek.com/anthropic/v1/messages", "DeepSeek 凭据目的地固定官方原生搜索接口");
        object invalidConfig = LookupNew("DeepSeekLookupConfig"); LookupSet(invalidConfig, "model", "invalid-model");
        Check(LookupThrows(delegate { LookupCall(config, "Save", invalidConfig, "fixture"); }, "config"), "无效模型在写入本机凭据前拒绝");
        string providerSource = File.ReadAllText(Path.Combine(SourceRoot, "src", "App", "livehub-lookup-deepseek.cs"));
        Check(providerSource.Contains("AllowAutoRedirect = false") && providerSource.Contains("request.Headers[\"Authorization\"] = \"Bearer \" + key")
            && !providerSource.Contains("PlatformLogin.CookieHeaderForUri"), "AI请求密钥仅用于认证，禁用重定向且不读取平台Cookie");
        Check(providerSource.Contains("CancelAfter(120000)") && !providerSource.Contains("CancelAfter(4000)"), "手动AI取消4秒上限并保留长时间连接保护");
        string native = (string)LookupCall(LookupType("DeepSeekResearch"), "MessagesBody", "deepseek-v4-flash", body, true);
        var nativeRoot = (Dictionary<string, object>)LookupCall(LookupType("LookupData"), "Json", native);
        IList tools = (IList)nativeRoot["tools"]; var tool = (Dictionary<string, object>)tools[0];
        Check((string)tool["type"] == "web_search_20250305" && (int)tool["max_uses"] == 5 && !native.Contains("enable_search"), "实际 DeepSeek 请求使用原生服务端搜索工具与独立协议");
        var context = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(LookupType("LookupCandidate")));
        object candidate = LookupCandidate("bili", "123", "456"); LookupSet(candidate, "verification", "verified"); LookupSet(candidate, "user_confirmed", true); context.Add(candidate);
        string projected = (string)LookupCall(LookupType("LookupAiProtocol"), "PublicContext", context);
        Check(projected.Contains("123") && !projected.Contains("user_confirmed") && !projected.Contains("verification") && !projected.Contains("Cookie"), "优化只发送当前公开候选，不发送确认、核验或凭据状态");
    }
    private static void TestLookupSources()
    {
        object parser = LookupParser(); string item = LookupCandidateJson("\"123\"", "\"9876543210987654321\"", "[1]");
        string content = "{\"candidates\":[" + item + "]}"; int split = content.Length / 2;
        LookupFeed(parser, "", LookupSourceFixture, "null"); LookupFeed(parser, content.Substring(0, split), "[]", "null");
        Check(LookupThrows(delegate { LookupCall(parser, "Complete", "bili"); }, "format"), "半截JSON和未完成流不能产生候选");
        LookupFeed(parser, content.Substring(split), "[]", "stop"); object result = LookupCall(parser, "Complete", "bili");
        IList candidates = (IList)LookupGet(result, "candidates"); object candidate = candidates[0];
        Check(candidates.Count == 1 && (string)LookupGet(candidate, "room_id") == "9876543210987654321", "64位及更长编号保持字符串精度");
        Check((string)LookupGet(candidate, "platform") == "bili" && (string)LookupGet(candidate, "verification") == "unverified"
            && !(bool)LookupGet(candidate, "user_confirmed"), "平台键映射集中，AI不能声明核验或用户确认");
        IList sources = (IList)LookupGet(candidate, "sources"); Check(sources.Count == 1 && LookupGet(sources[0], "retrieved_at") != null, "候选绑定实际引用索引并保留来源检索时间");
        object none = LookupParser(); LookupFeed(none, content, "[]", "stop");
        Check(LookupThrows(delegate { LookupCall(none, "Complete", "bili"); }, "sources"), "HTTP成功但没有检索来源不能视为联网成功");
        object bad = LookupParser(); LookupFeed(bad, "{\"candidates\":[" + LookupCandidateJson("\"123\"", "\"456\"", "[99]") + "]}", LookupSourceFixture, "stop");
        Check(((IList)LookupGet(LookupCall(bad, "Complete", "bili"), "candidates")).Count == 0, "引用越界候选被拒绝");
        object rounded = LookupParser(); LookupFeed(rounded, "{\"candidates\":[" + LookupCandidateJson("123", "\"456\"", "[1]") + "]}", LookupSourceFixture, "stop");
        Check(LookupThrows(delegate { LookupCall(rounded, "Complete", "bili"); }, "format"), "数字类型ID不被默默转换成字符串");
        object twins = LookupParser(); LookupFeed(twins, "{\"candidates\":[" + LookupCandidateJson("\"123\"", "\"456\"", "[1]") + "," + LookupCandidateJson("\"124\"", "\"457\"", "[2]") + "]}", "[{\"index\":1,\"url\":\"https://space.bilibili.com/123\"},{\"index\":2,\"url\":\"https://space.bilibili.com/124\"}]", "stop");
        Check(((IList)LookupGet(LookupCall(twins, "Complete", "bili"), "candidates")).Count == 2, "同名不同账号保留不同候选");
        object conflict = LookupParser(); LookupFeed(conflict, "{\"candidates\":[" + LookupCandidateJson("\"999\"", "\"456\"", "[1]") + "]}", LookupSourceFixture, "stop");
        object corrected = ((IList)LookupGet(LookupCall(conflict, "Complete", "bili"), "candidates"))[0];
        Check((string)LookupGet(corrected, "account_id") == "123" && LookupGet(corrected, "room_id") == null, "AI账号与引用官方账号页冲突时只保留来源账号，清除旧房间映射");
        object unsupported = LookupParser(); LookupFeed(unsupported, "{\"candidates\":[" + LookupCandidateJson("\"123\"", "\"456\"", "[1]") + "]}", "[{\"index\":1,\"url\":\"https://news.example.com/article\"}]", "stop");
        object clue = ((IList)LookupGet(LookupCall(unsupported, "Complete", "bili"), "candidates"))[0];
        Check(LookupGet(clue, "account_id") == null && LookupGet(clue, "room_id") == null, "缺少实际官方身份网址的模型编号降为有来源昵称线索");
        object forged = LookupCandidate("bili", "123", "456"); LookupSet(forged, "room_url", "https://live.bilibili.com.evil.example/456");
        Check(!(bool)LookupCall(LookupType("LookupUrls"), "Candidate", forged), "仿冒平台子域被拒绝");
        LookupSet(forged, "room_url", "https://live.bilibili.com/999");
        Check(!(bool)LookupCall(LookupType("LookupUrls"), "Candidate", forged), "编造链接与候选房间号不一致被拒绝");
        object noRoom = LookupCandidate("douyin", null, null); LookupSet(noRoom, "sec_uid", "MS4wLjfixture");
        Check(LookupCall(LookupType("LookupData"), "FavoriteKey", noRoom) == null, "缺少数字网页房间不可加入收藏");
    }
    private static void TestLookupOrder()
    {
        object service = LookupNew("LookupService"); var calls = new List<string>();
        LookupDelegate(service, "Ai", delegate(object[] args) { calls.Add("ai"); return LookupResult(LookupCandidate("huya", "123", "456")); });
        LookupDelegate(service, "PlatformSearch", delegate(object[] args) { calls.Add("platform"); return LookupResult(LookupCandidate("huya", "123", "456")); });
        Action<string> progress = delegate { }; object result = LookupCall(service, "Search", "huya", "langD", false, false, CancellationToken.None, progress);
        Check(string.Join(",", calls.ToArray()) == "platform" && (string)LookupGet(result, "status") == "platform", "普通名字查询只调用平台HTTP，不调用AI");
        result = LookupCall(service, "Search", "huya", "langD", false, false, CancellationToken.None, progress);
        Check((bool)LookupGet(result, "cached") && calls.Count == 1, "近期相同查询从候选缓存读取，不新增服务请求");
        result = LookupCall(service, "Search", "huya", "langD", false, true, CancellationToken.None, progress);
        Check(string.Join(",", calls.ToArray()) == "platform,ai" && (string)LookupGet(result, "status") == "ai", "手动AI新搜不被同关键词平台缓存吞掉");
        result = LookupCall(service, "Search", "huya", "langD", false, true, CancellationToken.None, progress);
        Check(string.Join(",", calls.ToArray()) == "platform,ai,ai", "再次手动新搜发起新请求，不展示旧AI缓存");
        LookupDelegate(service, "Ai", delegate { object empty = LookupResult(); LookupSet(empty, "note", "AI没有有来源的身份线索"); LookupSet(empty, "usage", "usage-fixture"); LookupSet(empty, "model_candidates", 2); return empty; });
        object emptyAi = LookupCall(service, "Search", "huya", "new-query", false, true, CancellationToken.None, progress);
        Check((string)LookupGet(emptyAi, "note") == "AI没有有来源的身份线索" && (string)LookupGet(emptyAi, "usage") == "usage-fixture"
            && (int)LookupGet(emptyAi, "model_candidates") == 2, "AI空结果保留真实原因、用量和诊断，不丢成泛泛提示");
        var current = LookupGet(result, "candidates");
        LookupDelegate(service, "Optimize", delegate(object[] args) { calls.Add("optimize"); Check(((IList)args[2]).Count == 1, "手动优化接收当前候选快照"); return LookupResult(LookupCandidate("huya", "123", "456")); });
        result = LookupCall(service, "Improve", "huya", "langD", current, CancellationToken.None, progress);
        Check(calls[calls.Count - 1] == "optimize" && (string)LookupGet(result, "status") == "ai-optimize", "优化使用独立显式入口");
        LookupDelegate(service, "Optimize", delegate { return LookupResult(LookupCandidate("huya", null, null)); });
        result = LookupCall(service, "Improve", "huya", "langD", current, CancellationToken.None, progress);
        Check(((IList)LookupGet(result, "candidates")).Count == 2 && (string)LookupGet(((IList)LookupGet(result, "candidates"))[0], "room_id") == "456", "AI名字线索不能抹掉现有平台稳定身份与房间号");
        calls.Clear(); result = LookupCall(service, "Search", "bili", "https://live.bilibili.com/7777", false, false, CancellationToken.None, progress);
        Check(calls.Count == 0 && (string)LookupGet(result, "status") == "direct", "确定直播链接跳过AI走解析与后续核验");
        LookupDelegate(service, "Share", delegate(object[] args) { calls.Add("share"); throw LookupError("blocked"); });
        result = LookupCall(service, "Search", "douyin", "分享 https://v.douyin.com/Fixture/", false, false, CancellationToken.None, progress);
        Check(calls.Count == 1 && calls[0] == "share" && (string)LookupGet(result, "status") == "share-blocked", "分享文案优先HTTP解析，受阻不自动请求AI");
        foreach (string code in new[] { "timeout", "unconfigured", "format", "sources", "network", "empty" })
        {
            calls.Clear(); string failure = code; LookupDelegate(service, "Ai", delegate(object[] args) { calls.Add("ai"); throw LookupError(failure); });
            LookupDelegate(service, "PlatformSearch", delegate(object[] args) { calls.Add("platform"); throw LookupError(failure); });
            result = LookupCall(service, "Search", "bili", "failure-" + code, false, false, CancellationToken.None, progress);
            Check(string.Join(",", calls.ToArray()) == "platform" && (string)LookupGet(result, "status") == "blocked", "平台" + code + "只提示，不自动请求AI");
            calls.Clear(); result = LookupCall(service, "Search", "bili", "failure-" + code, false, true, CancellationToken.None, progress);
            Check(string.Join(",", calls.ToArray()) == "ai" && (string)LookupGet(result, "status") == "blocked", "手动AI" + code + "失败不悄悄切换结果来源");
        }
        var cancellation = new CancellationTokenSource(); cancellation.Cancel(); calls.Clear(); bool cancelled = false;
        try { LookupCall(service, "Search", "bili", "cancelled", false, false, cancellation.Token, progress); }
        catch (TargetInvocationException ex) { cancelled = ex.InnerException is OperationCanceledException; }
        Check(cancelled && calls.Count == 0, "已取消查询不调用Provider或回退"); cancellation.Dispose();
        object guard = LookupNew("LookupGeneration"); int first = (int)LookupCall(guard, "Next"); int second = (int)LookupCall(guard, "Next");
        Check(!(bool)LookupCall(guard, "Current", first) && (bool)LookupCall(guard, "Current", second), "新查询或新选择使旧响应代次失效");
    }
    private static object LookupReply(int status, string body, string location)
    {
        object reply = LookupNew("LookupHttpReply"); LookupSet(reply, "Status", status); LookupSet(reply, "Body", body); LookupSet(reply, "Location", location); return reply;
    }
    private static void TestLookupIdentity()
    {
        object verifier = LookupNew("LookupVerifier"); object candidate = LookupCandidate("bili", "123", "456");
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":456,\"uid\":124}}", null); });
        object result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "mismatch", "合法房间和高匹配线索不能绕过账号归属不符");
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(403, null, null); });
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "blocked", "登录验证限流受阻不当作零结果或下播");
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":456,\"uid\":123}}", null); });
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && !(bool)LookupGet(result, "user_confirmed"), "平台核验和用户确认是两个独立状态");
        object noAccount = LookupCandidate("bili", null, "456"); result = LookupCall(verifier, "Verify", noAccount, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "blocked", "AI仅给房间号不能借直接添加绕过身份核验");
        LookupSet(noAccount, "direct_input", true); result = LookupCall(verifier, "Verify", noAccount, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified", "用户明确输入房间号保持已有添加能力");
        LookupSet(noAccount, "direct_input", false); result = LookupCall(verifier, "Verify", noAccount, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "blocked", "直接输入映射缓存不能污染AI身份核验");
        object sourceRoom = LookupCandidate("bili", null, "456"), source = LookupNew("LookupSource");
        LookupSet(source, "url", "https://live.bilibili.com/456"); ((IList)LookupGet(sourceRoom, "sources")).Add(source);
        result = LookupCall(verifier, "Verify", sourceRoom, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && (string)LookupGet(result, "account_id") == "123", "真实引用指定同一平台房间页时，用该页稳定owner与room字段建立关联");
        LookupSet(source, "url", "https://live.bilibili.com/999"); result = LookupCall(verifier, "Verify", sourceRoom, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "blocked", "其他房间来源及来源缓存不能支持此候选的账号归属");
        object huya = LookupCandidate("huya", "999", "456");
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(200, "var TT_PROFILE_INFO = {\"profileRoom\":456,\"lp\":999,\"nick\":\"最新昵称\"}; var TT_ROOM_DATA={\"id\":999};", null); });
        result = LookupCall(verifier, "Verify", huya, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && (string)LookupGet(result, "display_name") == "最新昵称", "虎牙SSR账号与房间核验后展示平台最新昵称");
        object douyu = LookupCandidate("douyu", "999", "456");
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(200, "{\"room\":{\"room_id\":456,\"owner_uid\":999,\"owner_name\":\"斗鱼主播\"}}", null); });
        result = LookupCall(verifier, "Verify", douyu, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified", "斗鱼用owner_uid确认房间归属");
        object douyin = LookupCandidate("douyin", "999", "456"); LookupSet(douyin, "sec_uid", "MS4wLjfixture"); LookupSet(douyin, "session_room_id", "9876543210987654321");
        string reflow = "{\"status_code\":0,\"data\":{\"room\":{\"id_str\":\"9876543210987654321\",\"owner\":{\"sec_uid\":\"MS4wLjfixture\",\"id_str\":\"999\",\"web_rid\":\"456\",\"nickname\":\"抖音主播\"}}}}";
        LookupDelegate(verifier, "Transport", delegate { return LookupReply(200, reflow, null); }); result = LookupCall(verifier, "Verify", douyin, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && (string)LookupGet(result, "room_id") == "456", "抖音reflow同时核对sec_uid与内部场次，收藏使用web_rid");
        object root = LookupCall(LookupType("LookupData"), "Json", reflow.Replace("MS4wLjfixture", "MS4wLjwrong"));
        Check(LookupThrows(delegate { LookupCall(LookupType("LookupVerifier"), "ApplyReflow", douyin, root); }, "mismatch"), "抖音owner身份错误被拒绝");
        root = LookupCall(LookupType("LookupData"), "Json", reflow.Replace("9876543210987654321", "9876543210987654322"));
        Check(LookupThrows(delegate { LookupCall(LookupType("LookupVerifier"), "ApplyReflow", douyin, root); }, "mismatch"), "抖音内部场次错误被拒绝");
        int requests = 0; LookupDelegate(verifier, "Transport", delegate { requests++; return LookupReply(302, null, "https://www.douyin.com.evil.example/a"); });
        Check(LookupThrows(delegate { LookupCall(verifier, "Get", "https://v.douyin.com/Fixture/", CancellationToken.None); }, "url") && requests == 1, "每跳检查域名，仿冒重定向在发送下一跳前被拒绝");
    }
    private static void TestLookupFavorites()
    {
        string dir = ReviewDirectory(); FieldInfo path = hub.GetField("Favorites", Flags); object old = path.GetValue(null);
        try
        {
            string file = Path.Combine(dir, "favorites.json"); path.SetValue(null, file); File.WriteAllText(file, "[]");
            var favorites = new List<KeyValuePair<string, string>>(); object candidate = LookupCandidate("douyin", "123", "456");
            LookupSet(candidate, "user_confirmed", true);
            Check(LookupThrows(delegate { Call("SaveLookupFavorite", candidate, "未核验", favorites); }, "unverified") && File.ReadAllText(file) == "[]", "用户确认不能绕过平台核验，拒绝时不写盘");
            LookupSet(candidate, "verification", "verified"); Check((bool)Call("SaveLookupFavorite", candidate, "主播", favorites), "主动添加且核验成功后沿用收藏原子写入");
            Check(favorites[0].Value == "douyin:456" && Directory.GetFiles(dir, "*.bak").Length == 1, "收藏使用数字web_rid且保存前备份既有文件");
            string saved = File.ReadAllText(file); Check(!(bool)Call("SaveLookupFavorite", candidate, "重命名", favorites) && File.ReadAllText(file) == saved && favorites.Count == 1, "同平台同号不重复写盘或覆盖名称");
            LookupSet(candidate, "user_confirmed", false);
            Check(LookupThrows(delegate { Call("SaveLookupFavorite", candidate, "取消", favorites); }, "unverified") && File.ReadAllText(file) == saved, "没有主动添加操作不写收藏");
        }
        finally { path.SetValue(null, old); Directory.Delete(dir, true); }
    }
    private static void LookupPumpUntil(Func<bool> done, int timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Type application = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name == "System.Windows.Forms") application = assembly.GetType("System.Windows.Forms.Application");
        if (application == null) throw new InvalidOperationException("查询窗体没有加载WinForms程序集");
        while (!done() && watch.ElapsedMilliseconds < timeout)
        {
            application.GetMethod("DoEvents").Invoke(null, null); Thread.Sleep(10);
        }
        application.GetMethod("DoEvents").Invoke(null, null);
    }
    private static void TestLookupUiRaces()
    {
        Exception failure = null; var thread = new Thread(delegate()
        {
            object ui = null; var oldSearch = new ManualResetEvent(false); var searchEntered = new ManualResetEvent(false);
            var oldVerify = new ManualResetEvent(false); var verifyEntered = new ManualResetEvent(false);
            int saved = 0; object savedCandidate = null;
            try
            {
                Type type = LookupType("LookupUi"); ConstructorInfo ctor = type.GetConstructor(LookupFlags, null, new Type[] {
                    typeof(string), typeof(string), typeof(Action<,>).MakeGenericType(LookupType("LookupCandidate"), typeof(string)) }, null);
                Delegate onSave = LookupMakeDelegate(ctor.GetParameters()[2].ParameterType, delegate(object[] args) { saved++; savedCandidate = args[0]; return null; });
                ui = ctor.Invoke(new object[] { "bili", "", onSave }); object form = LookupGet(ui, "Form");
                // PerformClick requires a visible control hierarchy; show the private form offscreen.
                LookupSet(form, "StartPosition", Enum.Parse(form.GetType().GetProperty("StartPosition").PropertyType, "Manual"));
                LookupSet(form, "ShowInTaskbar", false); LookupSet(form, "Left", -20000); LookupSet(form, "Top", -20000); LookupCall(form, "Show");
                object input = LookupGet(ui, "Input"), list = LookupGet(ui, "Candidates");
                object service = LookupGet(ui, "Service"), verifier = LookupGet(ui, "Verifier");
                LookupDelegate(service, "PlatformSearch", delegate(object[] args)
                {
                    if ((string)args[1] == "旧查询") { searchEntered.Set(); oldSearch.WaitOne(3000); return LookupResult(LookupCandidate("bili", "111", "1")); }
                    return LookupResult(LookupCandidate("bili", "222", "2"), LookupCandidate("bili", "333", "3"));
                });
                LookupDelegate(service, "Ai", delegate { throw LookupError("blocked"); });
                LookupSet(input, "Text", "旧查询"); LookupCall(ui, "BeginSearch", false, false);
                Check(searchEntered.WaitOne(1000), "旧查询实际进入异步Provider");
                LookupSet(input, "Text", "新查询"); LookupCall(ui, "BeginSearch", false, false);
                LookupPumpUntil(delegate { return ((IList)LookupGet(list, "Items")).Count == 2; }, 1500);
                Check(((IList)LookupGet(list, "Items")).Count == 2 && (int)LookupGet(list, "SelectedIndex") == -1, "异步候选展示后没有默认选中第一条");
                oldSearch.Set(); LookupPumpUntil(delegate { return oldSearch.WaitOne(0) && searchEntered.WaitOne(0); }, 100); Thread.Sleep(80);
                LookupPumpUntil(delegate { return false; }, 50);
                Check(((IList)LookupGet(list, "Items")).Count == 2 && (string)LookupGet(((IList)LookupGet(list, "Items"))[0], "account_id") == "222", "忽略取消的旧查询响应不能覆盖新候选");
                LookupDelegate(verifier, "Transport", delegate(object[] args)
                {
                    if (((string)args[0]).EndsWith("room_id=2")) { verifyEntered.Set(); oldVerify.WaitOne(3000); return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":2,\"uid\":222}}", null); }
                    return LookupReply(200, "{\"code\":0,\"data\":{\"room_id\":3,\"uid\":333}}", null);
                });
                LookupSet(list, "SelectedIndex", 0); Check(verifyEntered.WaitOne(1000), "旧选择实际进入异步平台核验");
                LookupSet(list, "SelectedIndex", 1); object add = LookupGet(ui, "Add");
                LookupPumpUntil(delegate { return (bool)LookupGet(add, "Enabled"); }, 1500);
                Check((bool)LookupGet(add, "Enabled") && (string)LookupGet(LookupGet(list, "SelectedItem"), "account_id") == "333" && saved == 0,
                    "新选择核验完成后直接允许点击收藏，但不自动写入");
                oldVerify.Set(); LookupPumpUntil(delegate { return false; }, 150);
                Check((int)LookupGet(list, "SelectedIndex") == 1 && (string)LookupGet(LookupGet(list, "SelectedItem"), "room_id") == "3", "旧账号核验结果不能污染新选择");
                Check((bool)LookupGet(LookupGet(ui, "Resolve"), "Enabled") && (bool)LookupGet(LookupGet(ui, "OpenRoom"), "Enabled"), "核验完成可重新解析并主动打开数字直播链接");
                LookupCall(LookupGet(ui, "Resolve"), "PerformClick");
                Check(!(bool)LookupGet(add, "Enabled") && !(bool)LookupGet(LookupGet(ui, "OpenRoom"), "Enabled"), "重新解析清除添加与打开权限，不沿用旧快照");
                LookupPumpUntil(delegate { return (bool)LookupGet(add, "Enabled"); }, 1500);
                Check((int)LookupGet(list, "SelectedIndex") == 1 && (bool)LookupGet(LookupGet(ui, "OpenRoom"), "Enabled") && saved == 0, "重新解析只核验所选账号，保留候选且不写收藏");
                LookupDelegate(service, "Optimize", delegate { throw LookupError("network"); });
                LookupCall(LookupGet(ui, "Deep"), "PerformClick");
                LookupPumpUntil(delegate { return !(bool)LookupGet(LookupGet(ui, "Cancel"), "Enabled"); }, 1500);
                Check(((IList)LookupGet(list, "Items")).Count == 2 && !(bool)LookupGet(add, "Enabled"), "手动AI优化失败保留原候选且要求重新选择核验");
                var aiEntered = new ManualResetEvent(false); var aiRelease = new ManualResetEvent(false);
                LookupDelegate(service, "Ai", delegate { aiEntered.Set(); aiRelease.WaitOne(1000); return LookupResult(LookupCandidate("bili", "444", "4")); });
                LookupCall(LookupGet(ui, "ContinueAi"), "PerformClick"); Check(aiEntered.WaitOne(1000), "AI新搜按钮实际进入显式Provider");
                LookupCall(LookupGet(ui, "Cancel"), "PerformClick"); aiRelease.Set(); LookupPumpUntil(delegate { return false; }, 100);
                Check(!(bool)LookupGet(add, "Enabled") && saved == 0, "真实UI取消事件禁用添加且不调用收藏写入");
                Check(((IList)LookupGet(list, "Items")).Count == 2, "取消AI新搜保留原结果并拒绝迟到AI响应");
                LookupDelegate(service, "Ai", delegate {
                    object empty = LookupResult(), source = LookupNew("LookupSource"); LookupSet(empty, "note", "AI没有找到目标身份");
                    LookupSet(source, "url", "https://www.huya.com/222523"); ((IList)LookupGet(empty, "sources")).Add(source); return empty;
                });
                LookupCall(LookupGet(ui, "ContinueAi"), "PerformClick");
                LookupPumpUntil(delegate { return !(bool)LookupGet(LookupGet(ui, "Cancel"), "Enabled"); }, 1500);
                Check(((IList)LookupGet(list, "Items")).Count == 2 && ((string)LookupGet(LookupGet(ui, "Status"), "Text")).Contains("上次候选")
                    && ((IList)LookupGet(LookupGet(ui, "Sources"), "Items")).Count == 1, "AI空结果明确标注保留的旧候选，并展示本次真实来源");
                Check(((Array)LookupCall(LookupGet(form, "Controls"), "Find", "lookupConfirm", true)).Length == 0, "查询窗口移除额外确认勾选项");
                LookupSet(list, "SelectedIndex", 1);
                LookupPumpUntil(delegate { return (bool)LookupGet(add, "Enabled"); }, 1500);
                LookupSet(LookupGet(ui, "Name"), "Text", "");
                Check(!(bool)LookupGet(add, "Enabled"), "核验通过后空收藏名称仍禁用添加");
                LookupSet(LookupGet(ui, "Name"), "Text", "测试主播");
                LookupCall(add, "PerformClick");
                Check(saved == 1 && (bool)LookupGet(savedCandidate, "user_confirmed") && (string)LookupGet(savedCandidate, "verification") == "verified"
                    && (string)LookupGet(savedCandidate, "room_id") == "3", "直接点击收藏只提交已核验的所选账号");
                Check(!(bool)LookupGet(LookupGet(list, "SelectedItem"), "user_confirmed") && !(bool)LookupGet(add, "Enabled"), "点击授权仅用于保存快照，完成后禁用重复提交");
                string[] names = { "lookupPlatform", "lookupInput", "lookupCandidates", "lookupSources", "lookupAdd" };
                foreach (string name in names)
                {
                    object controls = LookupGet(form, "Controls"); Array matches = (Array)LookupCall(controls, "Find", name, true);
                    Check(matches.Length == 1, "查询窗口真实控件树包含 " + name);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                oldSearch.Set(); oldVerify.Set(); if (ui != null) LookupCall(ui, "Dispose");
                // Worker delegates can still be unwinding; events must stay valid until their completed waits return.
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(8000)) throw new Exception("查询UI离线回归超时"); if (failure != null) throw failure;
    }
    private static void RegisterLookupCases()
    {
        RegisterDouyinSearchCases();
        RegisterLookupDiscoveryCases();
        TestRunner.Add("主播查询", "原生搜索协议与凭据边界", TestLookupContract);
        TestRunner.Add("主播查询", "SSE引用、结构化候选及ID精度", TestLookupSources);
        TestRunner.Add("主播查询", "平台优先、手动AI、缓存与取消代次", TestLookupOrder);
        TestRunner.Add("主播查询", "四平台房间归属与逐跳核验", TestLookupIdentity);
        TestRunner.Add("主播查询", "收藏确认、备份与去重", TestLookupFavorites);
        TestRunner.Add("主播查询", "真实控件事件、取消及旧响应隔离", TestLookupUiRaces);
        TestRunner.Add("主播查询", "原型HTTP、RSS及Flight解析", TestLookupNative);
        TestRunner.Add("主播查询", "查询窗口DPI与最小布局", TestLookupLayout);
        TestRunner.Add("主播查询", "深浅主题输入框像素与操作布局", TestLookupTheme);
        TestRunner.Add("主播查询", "抖音账号资料与别名直播页串联", TestLookupDouyinProfile);
        TestRunner.Add("主播查询", "AI官方入口、平台迁移证据及别名缓存隔离", TestLookupAiEntries);
        TestRunner.Add("主播查询", "DeepSeek原生搜索、三阶段来源边界与取消", TestDeepSeekResearch);
    }
    private static string DeepSeekTextFixture(string text)
    {
        return "{\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":20},\"content\":[{\"type\":\"text\",\"text\":\"" + LookupEscape(text) + "\"}]}";
    }
    private static void TestDeepSeekResearch()
    {
        string search = "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"web_search_tool_result\",\"content\":[{\"type\":\"web_search_result\",\"url\":\"https://www.huya.com/456\",\"title\":\"官方艺名\",\"page_age\":\"2026-09-30\"},{\"type\":\"web_search_result\",\"url\":\"https://www.huya.com/456\"}]},{\"type\":\"text\",\"text\":\"研究摘要\",\"citations\":[{\"url\":\"https://www.huya.com/456\",\"cited_text\":\"官方账号身份线索\"}]}]}";
        object research = LookupNew("DeepSeekResearch"); int calls = 0;
        LookupDelegate(research, "Request", delegate(object[] args) {
            string body = (string)args[0]; calls++;
            if (calls == 1) { Check(!body.Contains("web_search_20250305"), "身份规划不把模型记忆伪装成联网搜索"); return DeepSeekTextFixture("{\"queries\":[\"真实艺名 职业ID 游戏\"]}"); }
            if (calls == 2) { Check(body.Contains("真实艺名 职业ID 游戏") && body.Contains("web_search_20250305"), "扩展后的身份关键词实际进入原生搜索请求"); return search; }
            Check(!body.Contains("web_search_20250305") && body.Contains("官方账号身份线索") && body.Contains("2026-09-30"), "抽取阶段只消费真实结构化搜索结果、引用摘录与来源日期");
            return DeepSeekTextFixture("{\"candidates\":[{\"platform\":\"huya\",\"display_name\":\"官方艺名\",\"room_id\":\"456\",\"source_indices\":[1]}]}");
        });
        object result = LookupCall(research, "Run", "huya", "圈内简称", null, CancellationToken.None, (Action<string>)delegate {});
        object candidate = ((IList)LookupGet(result, "candidates"))[0];
        Check(calls == 3 && (string)LookupGet(candidate, "provider") == "deepseek_official" && (string)LookupGet(candidate, "verification") == "unverified", "三阶段完成后仍要求独立平台核验，不自动确认或收藏");
        IList sources = (IList)LookupGet(candidate, "sources");
        Check(sources.Count == 1 && (string)LookupGet(sources[0], "published_at") == "2026-09-30", "结构化来源按URL去重，发布日期保留到候选");
        var cancellation = new CancellationTokenSource(); calls = 0;
        LookupDelegate(research, "Request", delegate { calls++; cancellation.Cancel(); return DeepSeekTextFixture("{\"queries\":[\"真实艺名\"]}"); });
        bool cancelled = false; try { LookupCall(research, "Run", "huya", "简称", null, cancellation.Token, (Action<string>)delegate {}); }
        catch (TargetInvocationException ex) { cancelled = ex.InnerException is OperationCanceledException; }
        Check(cancelled && calls == 1, "规划后取消不会继续搜索或消耗后续模型调用"); cancellation.Dispose();
        calls = 0; LookupDelegate(research, "Request", delegate { calls++; return calls == 1 ? DeepSeekTextFixture("{\"queries\":[\"真实艺名\"]}") : DeepSeekTextFixture("搜索完成：https://www.huya.com/456"); });
        Check(LookupThrows(delegate { LookupCall(research, "Run", "huya", "简称", null, CancellationToken.None, (Action<string>)delegate {}); }, "sources") && calls == 2,
            "仅模型正文网址而无原生搜索块时停止，不继续编造候选");
        Check(LookupThrows(delegate { LookupCall(LookupType("DeepSeekResearch"), "TextResponse", DeepSeekTextFixture("半截").Replace("end_turn", "max_tokens")); }, "format"), "截断或未完整结束的 DeepSeek 响应不能采用");
        object clue = LookupCandidate("huya", null, null); LookupSet(clue, "display_name", "职业ID中文外号"); LookupSet(clue, "lookup_terms", new[] { "careerID" });
        LookupSet(clue, "match_reasons", new[] { "引用没有对应官方账号或房间网址，只保留昵称线索，不能直接收藏" });
        object fan = LookupCandidate("huya", "1", "101"), actual = LookupCandidate("huya", "2", "202"), twin = LookupCandidate("huya", "3", "303");
        LookupSet(fan, "display_name", "careerID的粉丝"); LookupSet(actual, "display_name", "careerID"); LookupSet(twin, "display_name", "careerID");
        LookupSet(actual, "user_confirmed", true); LookupSet(actual, "verification", "verified");
        LookupDelegate(research, "PlatformSearch", delegate(object[] args) { Check((string)args[1] == "careerID", "有来源身份线索继续查询真实艺名，不重复原始外号"); return LookupResult(fan, actual, twin); });
        object enriched = LookupResult(clue); LookupCall(research, "Enrich", enriched, CancellationToken.None, (Action<string>)delegate {});
        IList enrichedCandidates = (IList)LookupGet(enriched, "candidates");
        Check(enrichedCandidates.Count == 2 && (string)LookupGet(enrichedCandidates[0], "account_id") == "2" && (string)LookupGet(enrichedCandidates[1], "account_id") == "3",
            "艺名续查剔除模糊粉丝尾项，同名不同稳定账号分别保留，不能按名字合并");
        Check(!(bool)LookupGet(enrichedCandidates[0], "user_confirmed") && (string)LookupGet(enrichedCandidates[0], "verification") == "unverified", "续查平台候选清除旧确认与核验状态，要求用户重新选择");
        Check(!string.Join("；", (string[])LookupGet(enrichedCandidates[0], "match_reasons")).Contains("不能直接收藏"), "平台续查补齐房间后清除已过时的无数字号提示");
    }
    private static void TestLookupAiEntries()
    {
        string[] platformNames = { "bili", "huya", "douyu", "douyin" };
        foreach (string platform in platformNames)
        {
            string request = (string)LookupCall(LookupType("LookupAiProtocol"), "RequestBody", "qwen-plus", platform, "圈内外号", false);
            Check(request.Contains((string)LookupCall(LookupType("LookupAiProtocol"), "PlatformInstruction", platform))
                && request.Contains("update_source_indices") && request.Contains("未开播") && request.Contains("全新语义搜索"), "实际请求注入本平台规则、身份消歧与近期转平台证据契约：" + platform);
        }
        object numeric = LookupParser();
        LookupFeed(numeric, "{\"candidates\":[{\"platform\":\"huya\",\"display_name\":\"主播\",\"profile_url\":\"https://www.huya.com/456\",\"source_indices\":[1]}]}",
            "[{\"index\":1,\"url\":\"https://www.huya.com/456\"}]", "stop");
        object numericCandidate = ((IList)LookupGet(LookupCall(numeric, "Complete", "huya"), "candidates"))[0];
        Check((string)LookupGet(numericCandidate, "room_id") == "456" && LookupGet(numericCandidate, "profile_url") == null, "官方数字房间误放profile字段时归一化，不整条丢弃");
        object aliasParser = LookupParser();
        LookupFeed(aliasParser, "{\"candidates\":[{\"platform\":\"huya\",\"display_name\":\"主播\",\"account_id\":\"example\",\"room_id\":\"example\",\"room_url\":\"https://www.huya.com/example\",\"source_indices\":[1]}]}",
            "[{\"index\":1,\"url\":\"https://www.huya.com/example\"}]", "stop");
        object alias = ((IList)LookupGet(LookupCall(aliasParser, "Complete", "huya"), "candidates"))[0];
        Check(LookupGet(alias, "account_id") == null && LookupGet(alias, "room_id") == null && (string)LookupGet(alias, "profile_url") == "https://www.huya.com/example", "引用支持的英文入口保留为待核验链接，不冒充数字ID");
        object verifier = LookupNew("LookupVerifier"); int calls = 0;
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            calls++; string path = (string)args[0]; string room = path.EndsWith("second") ? "789" : "456";
            return LookupReply(200, "var TT_PROFILE_INFO={\"profileRoom\":" + room + ",\"lp\":123,\"nick\":\"实际艺名\"};", null);
        });
        object mapped = LookupCall(verifier, "Verify", alias, CancellationToken.None);
        Check((string)LookupGet(mapped, "verification") == "verified" && (string)LookupGet(mapped, "room_id") == "456"
            && (string)LookupGet(mapped, "account_id") == "123", "官方英文入口由平台SSR建立数字房间与稳定账号归属");
        object other = LookupCandidate("huya", null, null), otherSource = LookupNew("LookupSource"); LookupSet(other, "profile_url", "https://www.huya.com/second");
        LookupSet(otherSource, "url", "https://www.huya.com/second"); ((IList)LookupGet(other, "sources")).Add(otherSource);
        mapped = LookupCall(verifier, "Verify", other, CancellationToken.None);
        Check((string)LookupGet(mapped, "room_id") == "789" && calls == 2, "不同英文入口不能复用相同空账号与房间的映射缓存");
        ((IList)LookupGet(alias, "sources")).Clear(); mapped = LookupCall(verifier, "Verify", alias, CancellationToken.None);
        Check((string)LookupGet(mapped, "verification") == "blocked" && calls == 2, "缺少实际引用的英文入口不能借缓存绕过归属核验");
        Check(!(bool)LookupCall(LookupType("LookupUrls"), "HuyaAlias", "https://www.huya.com/search?hsk=test")
            && !(bool)LookupCall(LookupType("LookupUrls"), "HuyaAlias", "https://www.huya.com.evil.example/anchor"), "搜索页和仿冒域不能充当主播入口");
        string migration = "{\"platform\":\"douyu\",\"display_name\":\"主播\",\"room_id\":\"789\",\"source_indices\":[1],\"platform_update\":\"公开公告称已转到斗鱼，公告日期2026-09-01\",\"update_source_indices\":[2]}";
        string migrationSources = "[{\"index\":1,\"url\":\"https://www.douyu.com/789\"},{\"index\":2,\"url\":\"https://www.douyu.com/notice/move\"}]";
        object migrated = LookupParser(); LookupFeed(migrated, "{\"candidates\":[" + migration + "]}", migrationSources, "stop");
        object migrationCandidate = ((IList)LookupGet(LookupCall(migrated, "Complete", "huya"), "candidates"))[0];
        Check((string)LookupGet(migrationCandidate, "platform") == "douyu" && ((IList)LookupGet(migrationCandidate, "update_sources")).Count == 1
            && LookupGet(migrationCandidate, "platform_update") != null && (string)LookupGet(migrationCandidate, "verification") == "unverified", "有真实动态引用时可展示新平台候选，迁移说明不冒充房间核验");
        object unproven = LookupParser(); LookupFeed(unproven, "{\"candidates\":[" + migration.Replace("\"update_source_indices\":[2]", "\"update_source_indices\":[99]") + "]}", migrationSources, "stop");
        Check(((IList)LookupGet(LookupCall(unproven, "Complete", "huya"), "candidates")).Count == 0, "无真实动态引用的跨平台推荐不能假称转会");
        object old = LookupParser(); LookupFeed(old, "{\"candidates\":[" + migration.Replace("\"platform\":\"douyu\"", "\"platform\":\"huya\"").Replace("[2]", "[99]") + "]}", migrationSources, "stop");
        object oldCandidate = ((IList)LookupGet(LookupCall(old, "Complete", "huya"), "candidates"))[0];
        Check(LookupGet(oldCandidate, "platform_update") == null, "缺乏有效证据时清除迁移结论，不能因离线就声称换平台");
    }
    private static void TestLookupNative()
    {
        object verifier = LookupNew("LookupVerifier"); int requests = 0;
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            requests++; string url = (string)args[0];
            if (url.EndsWith("/nav")) return LookupReply(200, "{\"data\":{\"wbi_img\":{\"img_url\":\"https://i0.hdslb.com/01234567890123456789012345678901.png\",\"sub_url\":\"https://i0.hdslb.com/abcdefghijklmnopqrstuvwxyzabcdef.png\"}}}", null);
            Check(url.Contains("/wbi/search/type?") && url.Contains("w_rid=") && url.Contains("web_location=1550101"), "B站移植原型WBI签名协议");
            return LookupReply(200, "{\"code\":0,\"data\":{\"result\":[{\"uname\":\"测试主播\",\"roomid\":456,\"uid\":123}]}}", null);
        });
        object result = LookupCall(verifier, "SearchNative", "bili", "主播", CancellationToken.None);
        Check(requests == 2 && ((IList)LookupGet(result, "candidates")).Count == 1, "B站HTTP原生搜索读取真实主播结构");
        string rss = "<rss><channel><item><title>同名的抖音直播间 - 抖音直播</title><link>https://live.douyin.com/456</link></item><item><title>同名的抖音 - 抖音</title><link>https://www.douyin.com/user/MS4wLjfixture</link></item><item><link>https://live.douyin.com.evil.example/789</link></item><item><link>https://live.douyin.com/456</link></item></channel></rss>";
        result = LookupCall(LookupType("LookupVerifier"), "ParseIndex", rss);
        Check(((IList)LookupGet(result, "candidates")).Count == 2 && LookupGet(((IList)LookupGet(result, "candidates"))[0], "sec_uid") == null, "同名索引直播页与账号页不合并，拒绝仿冒域并去重URL");
        Check(LookupThrows(delegate { LookupCall(LookupType("LookupVerifier"), "ParseIndex", "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///fixture'>]><rss>&x;</rss>"); }, "blocked"), "RSS禁用DTD和外部实体");
        LookupDelegate(verifier, "Transport", delegate(object[] args) { Check(((string)args[0]).StartsWith("https://cn.bing.com/search?"), "独立公开索引线索入口使用 RSS，不冒充抖音官方查询"); return LookupReply(200, rss, null); });
        result = LookupCall(verifier, "SearchIndex", "同名", CancellationToken.None);
        Check(((IList)LookupGet(result, "candidates")).Count == 2, "公开索引候选保持待核验");
        string page = "self.__next_f.push([1,\"7:{\\\"navigation\\\":{\\\"sec_uid\\\":\\\"MS4wLjwrong\\\"},\\\"anchor\\\":{\\\"sec_uid\\\":\\\"MS4wLjfixture\\\",\\\"nickname\\\":\\\"主播\\\"},\\\"roomId\\\":\\\"9876543210987654321\\\"}\"]);";
        object room = LookupCall(LookupType("LookupVerifier"), "LookupDouyinPage", page);
        Check(room != null && (string)LookupGet(room, "OwnerSecUid") == "MS4wLjfixture", "Flight解析读取同一roomId对象的anchor，不取导航账号");
        requests = 0; LookupDelegate(verifier, "Transport", delegate(object[] args) {
            requests++; if (requests == 1) return LookupReply(302, null, "https://webcast.amemv.com/reflow/9876543210987654321?sec_user_id=MS4wLjfixture");
            Check(((string)args[0]).Contains("/webcast/room/reflow/info/"), "分享跳转含内部编号后直接核验，不请求落地壳");
            return LookupReply(200, "{\"status_code\":0,\"data\":{\"room\":{\"id_str\":\"9876543210987654321\",\"owner\":{\"id_str\":\"123\",\"sec_uid\":\"MS4wLjfixture\",\"web_rid\":\"456\"}}}}", null);
        });
        object shared = LookupCall(verifier, "ShareCore", "https://v.douyin.com/Fixture/", CancellationToken.None);
        Check(requests == 2 && (string)LookupGet(shared, "verification") == "verified" && (string)LookupGet(shared, "room_id") == "456", "分享HTTP链路核对身份并只输出数字web_rid");
    }
    private static void TestLookupLayout()
    {
        Exception failure = null; var thread = new Thread(delegate() {
            Type dpi = hub.Assembly.GetType("DpiLayout"); FieldInfo scale = dpi.GetField("Scale", LookupFlags); object old = scale.GetValue(null);
            try {
                foreach (float factor in new[] { 1f, 1.5f, 2f }) {
                    scale.SetValue(null, 1f); object ui = Activator.CreateInstance(LookupType("LookupUi"), LookupFlags, null, new object[] { "bili", "", null }, null);
                    try {
                        object form = LookupGet(ui, "Form"); LookupGet(form, "Handle"); LookupCall(form, "SetTestDpi", (int)Math.Round(96 * factor));
                        foreach (bool minimum in new[] { false, true }) {
                        if (minimum) LookupSet(form, "Size", LookupGet(form, "MinimumSize"));
                        LookupCall(form, "PerformLayout");
                        foreach (string field in new[] { "Candidates", "Sources" }) {
                            object list = LookupGet(ui, field); Check((int)LookupGet(list, "ItemHeight") == (int)Math.Round((field == "Candidates" ? 72 : 20) * factor), "查询" + field + "行高随DPI=" + factor + "缩放，避免文字遮挡");
                            if ((int)LookupGet(list, "Height") <= 40 * factor)
                            {
                                hub.GetMethod("WriteUiProbe", Flags).Invoke(null, new object[] { form, Path.Combine(SourceRoot, "artifacts", "dpi-preview", "lookup-failure.json") });
                            }
                            Check((int)LookupGet(list, "Height") > 40 * factor, "查询" + field + "有可读的滚动区域DPI=" + factor + " height=" + LookupGet(list, "Height"));
                        }
                        object add = LookupGet(ui, "Add"), parent = LookupGet(add, "Parent");
                        Check((int)LookupGet(add, "Bottom") <= (int)LookupGet(LookupGet(parent, "ClientSize"), "Height"), "收藏按钮完整可见DPI=" + factor);
                        foreach (string field in new[] { "Search", "Deep", "ContinueAi", "Add", "Resolve", "OpenRoom" }) {
                            object control = LookupGet(ui, field); int top = 0, left = 0;
                            for (object current = control; current != null && current != form; current = LookupGet(current, "Parent")) {
                                top += (int)LookupGet(current, "Top"); left += (int)LookupGet(current, "Left");
                            }
                            object client = LookupGet(form, "ClientSize");
                            Check(top >= 0 && left >= 0 && top + (int)LookupGet(control, "Height") <= (int)LookupGet(client, "Height")
                                && left + (int)LookupGet(control, "Width") <= (int)LookupGet(client, "Width"), "操作" + field + "不伸出查询窗口DPI=" + factor);
                        }
                        Check((string)LookupGet(LookupGet(ui, "Search"), "Text") == "查询" && LookupGet(LookupGet(ui, "ContinueAi"), "Parent") != null, "默认查询与手动AI入口独立DPI=" + factor);
                        int[] input = LookupBoundsInForm(LookupGet(ui, "Input"), form), platform = LookupBoundsInForm(LookupGet(ui, "Platform"), form);
                        foreach (string field in new[] { "Platform", "Search", "Cancel" }) {
                            int[] bounds = LookupBoundsInForm(LookupGet(ui, field), form);
                            Check(Math.Abs(bounds[1] - input[1]) <= 1 && Math.Abs(bounds[3] - input[3]) <= 1,
                                "输入行" + field + "上下边缘对齐DPI=" + factor + " minimum=" + minimum + " input=" + string.Join(",", input) + " control=" + string.Join(",", bounds));
                        }
                        int[] deep = LookupBoundsInForm(LookupGet(ui, "Refresh"), form), candidates = LookupBoundsInForm(LookupGet(ui, "Candidates"), form),
                            details = LookupBoundsInForm(LookupGet(LookupGet(ui, "Details"), "Parent"), form);
                        // Details is the inner edit; compare the surrounding card to the list edge.
                        Check(deep[0] == platform[0] && candidates[0] == platform[0],
                            "首列控件与候选区域使用同一左边距DPI=" + factor);
                        Check(candidates[1] == details[1], "候选列表与账号详情顶边对齐DPI=" + factor);
                        foreach (System.Collections.DictionaryEntry filter in (System.Collections.IDictionary)LookupGet(ui, "PlatformFilters")) {
                            int[] bounds = LookupBoundsInForm(filter.Value, form);
                            Check(bounds[0] >= 0 && bounds[2] <= (int)LookupGet(LookupGet(form, "ClientSize"), "Width")
                                && bounds[3] <= candidates[1], "平台筛选" + filter.Key + "完整可见且不覆盖结果DPI=" + factor + " minimum=" + minimum);
                        }
                        int[] footer = LookupBoundsInForm(LookupGet(LookupGet(ui, "Add"), "Parent"), form);
                        Check(footer[1] - candidates[3] >= Math.Round(8 * factor), "底部操作与结果区留有间距，不贴边重叠DPI=" + factor);
                        string[] fields = { "Platform", "Input", "Search", "Cancel", "Deep", "ContinueAi", "Web", "Config", "Refresh", "Clues", "Candidates", "Sources", "Add", "Resolve", "OpenRoom", "OpenProfile", "SourcesButton", "SourcePreview" };
                        for (int i = 0; i < fields.Length; i++) for (int j = i + 1; j < fields.Length; j++) {
                            int[] a = LookupBoundsInForm(LookupGet(ui, fields[i]), form), b = LookupBoundsInForm(LookupGet(ui, fields[j]), form);
                            Check(Math.Min(a[2], b[2]) <= Math.Max(a[0], b[0]) || Math.Min(a[3], b[3]) <= Math.Max(a[1], b[1]),
                                fields[i] + "/" + fields[j] + "不重叠DPI=" + factor + " minimum=" + minimum);
                        }
                        }
                    } finally { LookupCall(ui, "Dispose"); }
                }
            } catch (Exception ex) { failure = ex; } finally { scale.SetValue(null, old); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); if (!thread.Join(15000)) throw new Exception("查询布局测试超时"); if (failure != null) throw failure;
    }
    private static int[] LookupBoundsInForm(object control, object form)
    {
        int top = 0, left = 0;
        int right = (int)LookupGet(control, "Width"), bottom = (int)LookupGet(control, "Height");
        for (object current = control; current != null && current != form; current = LookupGet(current, "Parent"))
        {
            int dx = (int)LookupGet(current, "Left"), dy = (int)LookupGet(current, "Top");
            left += dx; right += dx; top += dy; bottom += dy;
            object parent = LookupGet(current, "Parent");
            if (parent is System.Windows.Forms.ScrollableControl && ((System.Windows.Forms.ScrollableControl)parent).AutoScroll)
            {
                var viewport = ((System.Windows.Forms.ScrollableControl)parent).ClientRectangle;
                left = Math.Max(left, viewport.Left); top = Math.Max(top, viewport.Top);
                right = Math.Max(left, Math.Min(right, viewport.Right)); bottom = Math.Max(top, Math.Min(bottom, viewport.Bottom));
            }
        }
        return new[] { left, top, right, bottom };
    }
    private static void TestLookupTheme()
    {
        Exception failure = null; var thread = new Thread(delegate() {
            Type theme = hub.Assembly.GetType("ModernTheme"); bool old = (bool)theme.GetProperty("IsDark").GetValue(null, null);
            object ui = null;
            try {
                ui = Activator.CreateInstance(LookupType("LookupUi"), LookupFlags, null, new object[] { "bili", "", null }, null);
                object form = LookupGet(ui, "Form"); LookupSet(form, "Left", -20000); LookupSet(form, "Top", -20000); LookupSet(form, "ShowInTaskbar", false);
                LookupCall(form, "Show");
                foreach (bool dark in new[] { true, false, true }) {
                    LookupCall(theme, "Apply", dark); LookupCall(form, "PerformLayout");
                    object input = LookupGet(ui, "Input"), inner = LookupGet(input, "Inner");
                    object card = theme.GetProperty("Card").GetValue(null, null), surface = theme.GetProperty("Surface").GetValue(null, null);
                    Check(LookupGet(inner, "Dock").ToString() == "None" && Math.Abs((int)LookupGet(inner, "Top") * 2 + (int)LookupGet(inner, "Height") - (int)LookupGet(input, "Height")) <= 1,
                        "单行输入框手动垂直居中，停靠不覆盖布局，dark=" + dark);
                    Check(LookupGet(inner, "BackColor").Equals(card) && LookupGet(form, "BackColor").Equals(surface), "切换主题后内外输入框与窗口跟随调色板，dark=" + dark);
                    foreach (string field in new[] { "Details", "Name" })
                        Check(LookupGet(LookupGet(ui, field), "BorderStyle").ToString() == "None", field + "不保留原生白色边框");
                    Check(!(bool)LookupGet(LookupGet(ui, "Add"), "Enabled"), "切换主题不放开未核验收藏操作，dark=" + dark);
                    Type bitmapType = card.GetType().Assembly.GetType("System.Drawing.Bitmap");
                    object bitmap = Activator.CreateInstance(bitmapType, new object[] { (int)LookupGet(input, "Width"), (int)LookupGet(input, "Height") });
                    try {
                        LookupCall(input, "DrawToBitmap", bitmap, LookupGet(input, "ClientRectangle"));
                        int x = (int)LookupGet(input, "Width") / 2, y = (int)LookupGet(inner, "Top") + (int)LookupGet(inner, "Height") / 2;
                        Check(LookupCall(bitmap, "GetPixel", x, y).Equals(card) && LookupCall(bitmap, "GetPixel", x, (int)LookupGet(input, "Height") - 4).Equals(card),
                            "真实输入框像素上下底色一致，无黑色横条，dark=" + dark);
                    } finally { ((IDisposable)bitmap).Dispose(); }
                    LookupSet(form, "Size", LookupGet(form, "MinimumSize")); LookupCall(form, "PerformLayout");
                    object inputParent = LookupGet(input, "Parent");
                    Check((int)LookupGet(input, "Bottom") <= (int)LookupGet(inputParent, "Height"), "最小窗口输入框不覆盖下一排操作，dark=" + dark);
                }
            } catch (Exception ex) { failure = ex; }
            finally { if (ui != null) LookupCall(ui, "Dispose"); LookupCall(theme, "Apply", old); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); if (!thread.Join(15000)) throw new Exception("查询主题测试超时"); if (failure != null) throw failure;
    }
    private static void TestLookupDouyinProfile()
    {
        object candidate = LookupCandidate("douyin", null, null);
        LookupSet(candidate, "sec_uid", "MS4wLjfixture"); LookupSet(candidate, "profile_url", "https://www.douyin.com/user/MS4wLjfixture");
        LookupSet(candidate, "account_handle", "wrong_ai_hint");
        string profile = "{\"status_code\":0,\"user_info\":{\"sec_uid\":\"MS4wLjfixture\",\"unique_id\":\"FixtureHandle822\",\"nickname\":\"平台最新昵称\"}}";
        string page = "<script>self.__next_f.push([1,\"{\\\"roomId\\\":\\\"9876543210987654321\\\",\\\"anchor\\\":{\\\"sec_uid\\\":\\\"MS4wLjfixture\\\",\\\"uid\\\":\\\"999\\\"}}\"])</script>";
        string reflow = "{\"status_code\":0,\"data\":{\"room\":{\"id_str\":\"9876543210987654321\",\"status\":4,\"owner\":{\"sec_uid\":\"MS4wLjfixture\",\"id_str\":\"999\",\"web_rid\":\"456\",\"nickname\":\"平台最新昵称\"}}}}";
        var requests = new List<string>(); object verifier = LookupNew("LookupVerifier");
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            string url = (string)args[0]; requests.Add(url);
            return LookupReply(200, url.Contains("/user/info/") ? profile : url.Contains("/webcast/room/reflow/") ? reflow
                : url.Contains("/user/") ? "<html>homepage without live data</html>" : page, null);
        });
        object result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified" && (string)LookupGet(result, "room_url") == "https://live.douyin.com/456", "主页无房间时继续账号接口、抖音号别名页、reflow，最终得到数字网址");
        Check((string)LookupGet(result, "account_handle") == "FixtureHandle822" && (bool)LookupGet(result, "account_verified"), "平台抖音号覆盖模型提示，并与同一 sec_uid 绑定");
        Check((string)LookupGet(result, "resolution_path") == "profile_api_then_alias_ssr_then_reflow" && requests.Count == 4 && requests[2].EndsWith("/FixtureHandle822"), "请求按缺失信息顺序串联，不再次搜索昵称或触发AI");
        Check((string)LookupGet(result, "live_status") == "offline" && (string)LookupGet(result, "room_id") == "456", "明确未开播仍保留已核验数字直播房间");
        Check(((IList)LookupGet(result, "sources")).Count == 3, "账号资料、别名直播页和数字号归属均记录实际来源与时间");
        LookupCall(verifier, "Verify", candidate, CancellationToken.None); Check(requests.Count == 4, "同账号短期成功映射可复用");
        LookupCall(verifier, "ClearCache"); LookupCall(verifier, "Verify", candidate, CancellationToken.None); Check(requests.Count == 8, "手动重新解析清除映射并重新请求平台");
        requests.Clear(); LookupCall(verifier, "ClearCache"); string original = profile;
        profile = profile.Replace("MS4wLjfixture", "MS4wLjother"); result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "mismatch" && requests.Count == 2, "账号接口 sec_uid 不符立即拒绝，不转向别人的直播间");
        profile = original; string originalPage = page; page = page.Replace("MS4wLjfixture", "MS4wLjother"); requests.Clear();
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "mismatch" && requests.Count == 3, "别名直播页属于其他 sec_uid 时拒绝，不调用reflow");
        page = originalPage; profile = profile.Replace("FixtureHandle822", "0"); requests.Clear();
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "account-only" && (bool)LookupGet(result, "account_verified") && LookupGet(result, "room_id") == null,
            "账号已核实但没有有效抖音号时保留账号资料，不把短号0当成房间或判定下播");
        LookupSet(candidate, "account_handle", "FixtureHandle822"); profile = "{\"status_code\":1}"; requests.Clear();
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "verified", "账号接口受阻时可使用抖音号线索，但别名页和reflow必须核对同一 sec_uid");
        LookupCall(verifier, "ClearCache"); LookupSet(candidate, "account_handle", "../evil"); requests.Clear();
        result = LookupCall(verifier, "Verify", candidate, CancellationToken.None);
        Check((string)LookupGet(result, "verification") == "blocked" && requests.Count == 2, "危险抖音号不能生成别名请求");
        LookupSet(candidate, "account_handle", "FixtureHandle822"); var cancel = new CancellationTokenSource(); requests.Clear();
        LookupDelegate(verifier, "Transport", delegate(object[] args) {
            string url = (string)args[0]; requests.Add(url); if (url.Contains("/user/info/")) cancel.Cancel();
            return LookupReply(200, url.Contains("/user/info/") ? original : "<html></html>", null);
        });
        bool canceled = false; try { LookupCall(verifier, "Verify", candidate, cancel.Token); } catch (TargetInvocationException ex) { canceled = ex.InnerException is OperationCanceledException; }
        Check(canceled && requests.Count == 2, "资料解析途中取消阻止后续别名和房间请求"); cancel.Dispose();
        object service = LookupNew("LookupService"); LookupSet(candidate, "account_handle", null);
        LookupDelegate(service, "Optimize", delegate {
            object addition = LookupCandidate("douyin", null, null); LookupSet(addition, "sec_uid", "MS4wLjfixture"); LookupSet(addition, "account_handle", "FixtureHandle822");
            return LookupResult(addition);
        });
        result = LookupCall(service, "Improve", "douyin", "测试账号", LookupGet(LookupResult(candidate), "candidates"), CancellationToken.None, new Action<string>(delegate { }));
        IList merged = (IList)LookupGet(result, "candidates");
        Check(merged.Count == 1 && (string)LookupGet(merged[0], "account_handle") == "FixtureHandle822" && (string)LookupGet(merged[0], "sec_uid") == "MS4wLjfixture", "手动AI补充同一sec_uid的抖音号保留为待核验线索，不只合并匹配说明");
    }
}
