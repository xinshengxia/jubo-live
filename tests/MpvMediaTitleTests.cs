using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void TestMpvMediaTitleHints()
    {
        const string roomTitle = "直播标题 - 第二部分，逗号,等号=测试";
        const string fullTitle = "主播 - 艺名 - " + roomTitle;
        object info = Call("ResolveHuyaPlaybackTitle", (Func<string>)delegate {
            return "{\"data\":{\"nick\":\"主播 - 艺名\",\"roomName\":\"" + roomTitle + "\"}}";
        }, (Func<string>)delegate { throw new Exception("健康标题不应重抓网页"); }, "fallback");
        Check((string)LookupGet(info, "FullTitle") == fullTitle && (string)LookupGet(info, "RoomTitle") == roomTitle,
            "虎牙同次解析分别保留窗口标题和原始直播标题，不按分隔符猜测");
        info = Call("ParseHuyaPlaybackTitlePage", "{\"nick\":\"主播 - 艺名\",\"introduction\":\"" + roomTitle + "\"}", "fallback");
        Check((string)LookupGet(info, "FullTitle") == fullTitle && (string)LookupGet(info, "RoomTitle") == roomTitle,
            "虎牙短号网页回退也保留独立直播标题");
        info = Call("ParseDouyuPlaybackTitle", "{\"room\":{\"nickname\":\"主播 - 艺名\",\"room_name\":\"" + roomTitle + "\"}}", "fallback");
        Check((string)LookupGet(info, "FullTitle") == fullTitle && (string)LookupGet(info, "RoomTitle") == roomTitle,
            "斗鱼窗口主播前缀不进入控制条直播标题");
        info = Call("CreatePlaybackTitleInfo", fullTitle, null);
        Check((string)LookupGet(info, "RoomTitle") == fullTitle, "缺失独立标题时回退可读标题");

        string root = ReviewDirectory(), directory = Path.Combine(root, "标题 有空格,中文"); Directory.CreateDirectory(directory);
        var saved = new Dictionary<string, object>();
        foreach (string field in new[] { "Mpv", "RuntimeDir", "LiveTitleScript" }) saved[field] = hub.GetField(field, ReviewStatic).GetValue(null);
        try
        {
            hub.GetField("Mpv", ReviewStatic).SetValue(null, typeof(HardeningTests).Assembly.Location);
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, directory);
            hub.GetField("LiveTitleScript", ReviewStatic).SetValue(null, Path.Combine(SourceRoot, "live-title.lua"));
            foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
            {
                object target = Call("Parse", platform + ":7777", "bili");
                object plan = Call("ResolvePlan", target, "mpv", false, false);
                Call("AddMpvMediaTitle", plan, roomTitle, false);
                Check(Directory.GetFiles(directory).Length == 0 && !((string)LookupGet(plan, "Arguments")).Contains("live_title_media_file"),
                    platform + " 纯计划不写标题文件或引入文件参数");
                Call("AddMpvMediaTitle", plan, roomTitle, true);
                string[] args = ParseCommandLine("mpv.exe " + (string)LookupGet(plan, "Arguments"));
                string path = MpvArgument(args, "--script-opts-append").Substring("live_title_media_file=".Length);
                Check(File.ReadAllText(path) == roomTitle, platform + " 直播标题独立落盘，不包含主播前缀/冻结的日期");
                Check((bool)Call("RuntimeCacheName", Path.GetFileName(path)), platform + " 会话标题纳入已有临时文件清理规则");
                File.Delete(path);
            }
        }
        finally
        {
            foreach (var pair in saved) hub.GetField(pair.Key, ReviewStatic).SetValue(null, pair.Value);
            Directory.Delete(root, true);
        }
    }
}
