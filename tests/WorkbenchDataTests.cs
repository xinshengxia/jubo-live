using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private static void RegisterWorkbenchDataCases()
    {
        TestRunner.Add("工作台数据", "白名单数据只读投影与凭据隐藏", TestWorkbenchDataProjection);
        TestRunner.Add("工作台数据", "构建版本与来源时间可追踪", TestWorkbenchBuildMetadata);
    }

    private static Type WorkbenchDataType()
    {
        return hub.GetNestedType("WorkbenchDataUi", BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void TestWorkbenchDataProjection()
    {
        string root = ReviewDirectory();
        try
        {
            string favorites = Path.Combine(root, "favorites.json"), history = Path.Combine(root, "history.json"), config = Path.Combine(root, "danmaku.json");
            string runtime = Path.Combine(root, "runtime"); Directory.CreateDirectory(runtime);
            Directory.CreateDirectory(Path.Combine(runtime, "logs"));
            Directory.CreateDirectory(Path.Combine(runtime, "login-profile"));
            File.WriteAllText(favorites, "[{\"name\":\"合成收藏\",\"room\":\"huya:12345\"}]", Encoding.UTF8);
            File.WriteAllText(history, "{\"bili\":[{\"Room\":\"7777\",\"Name\":\"合成历史\",\"At\":\"10-21 12:00\"}]}", Encoding.UTF8);
            File.WriteAllText(config, "{\"Enabled\":true,\"ScMode\":\"badge\",\"MpvQuality\":\"light\",\"FontName\":\"鸿蒙\",\"FontScalePercent\":130,\"api_key\":\"SYNTHETIC_PRIVATE_VALUE\",\"Cookie\":\"SYNTHETIC_PRIVATE_VALUE\"}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(runtime, "session-example.state.txt"), "offline");
            File.WriteAllText(Path.Combine(runtime, "bili-cookies.txt"), "SYNTHETIC_PRIVATE_VALUE");
            File.WriteAllText(Path.Combine(runtime, "login-profile", "secret.txt"), "SYNTHETIC_PRIVATE_VALUE");
            string log = Path.Combine(runtime, "logs", "danmaku.log");
            File.WriteAllText(log, "normal line\nCookie: SYNTHETIC_PRIVATE_VALUE\nhttps://example.invalid/path?token=SYNTHETIC_PRIVATE_VALUE\nready\n");
            Type type = WorkbenchDataType();
            IList favoriteRows = (IList)ReviewCall(type, "ReadFavoriteRows", favorites);
            Check(favoriteRows.Count == 1 && ((string[])favoriteRows[0])[2] == "12345", "收藏窗口按真实格式解析平台与房间");
            IList historyRows = (IList)ReviewCall(type, "ReadHistoryRows", history);
            Check(historyRows.Count == 1 && ((string[])historyRows[0])[1] == "合成历史", "历史窗口展示名称和时间");
            IList configRows = (IList)ReviewCall(type, "ReadDisplayConfigRows", config);
            var rendered = new StringBuilder();
            foreach (string[] row in configRows) rendered.Append(string.Join("|", row));
            Check(rendered.ToString().Contains("badge") && !rendered.ToString().Contains("SYNTHETIC_PRIVATE_VALUE"), "只展示允许的显示配置，不泄露未知凭据项");
            MethodInfo snapshotMethod = type.GetMethod("ReadSnapshot", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) }, null);
            object snapshot = snapshotMethod.Invoke(null, new object[] { favorites, history, config, runtime, "mpv.exe", "PotPlayerMini64.exe" });
            IList categories = (IList)LookupGet(snapshot, "Categories");
            Check(categories.Count == 5, "数据页面始终提供收藏、历史、配置、缓存、日志五类");
            IList cache = (IList)LookupGet(categories[3], "Rows");
            Check(cache.Count == 1 && ((string[])cache[0])[0] == "session-example.state.txt", "缓存列表不遍历凭据或登录profile");
            string preview = (string)ReviewCall(type, "ReadLogPreview", log);
            Check(preview.Contains("normal line") && preview.Contains("ready") && !preview.Contains("SYNTHETIC_PRIVATE_VALUE"), "日志查看过滤凭据和URL并保留普通内容");
            Check(((string)ReviewCall(type, "ReadLogPreview", Path.Combine(runtime, "bili-cookies.txt"))).Contains("不属于"), "日志入口不能读取Cookie文件");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestWorkbenchBuildMetadata()
    {
        Type type = WorkbenchDataType();
        const string info = "1.2.3+0123456789ab (2026-10-21T12:34:56Z)";
        Check((string)ReviewCall(type, "BuildVersion", info) == "1.2.3+0123456789ab", "构建版本保留版本号与commit");
        Check((string)ReviewCall(type, "BuildTime", info) == "2026-10-21 12:34:56 UTC", "来源时间按UTC明确呈现");
        Check((string)ReviewCall(type, "BuildTime", "unknown") == "未知时间", "缺失时间不伪造构建时刻");
    }
}
