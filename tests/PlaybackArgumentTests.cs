using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private static string MpvArgument(string[] args, string name)
    {
        foreach (string arg in args) if (arg.StartsWith(name + "=", StringComparison.Ordinal)) return arg.Substring(name.Length + 1);
        return null;
    }

    private static string ReadMpvListValue(string list, string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(list);
        int at = 0;
        while (at < bytes.Length)
        {
            int start = at;
            while (at < bytes.Length && bytes[at] != '=') at++;
            if (at == bytes.Length) return null;
            string name = Encoding.UTF8.GetString(bytes, start, at - start); at++;
            int count;
            if (at < bytes.Length && bytes[at] == '%')
            {
                start = ++at; while (at < bytes.Length && bytes[at] != '%') at++;
                count = int.Parse(Encoding.ASCII.GetString(bytes, start, at - start)); start = ++at; at += count;
            }
            else
            {
                start = at; while (at < bytes.Length && bytes[at] != ',') at++; count = at - start;
            }
            string value = Encoding.UTF8.GetString(bytes, start, count);
            if (name == key) return value;
            if (at < bytes.Length && bytes[at++] != ',') throw new Exception("mpv 列表值的长度边界错误");
        }
        return null;
    }

    private static void TestMpvPathArguments()
    {
        string dir = Path.Combine(ReviewDirectory(), "Live Hub 中文,a=b"); Directory.CreateDirectory(dir);
        var previous = new Dictionary<string, object>();
        string[] fields = { "Mpv", "YtDlp", "LiveTitleScript", "LiveResyncScript", "RuntimeDir", "Cookie" };
        try
        {
            foreach (string field in fields) previous[field] = hub.GetField(field, ReviewStatic).GetValue(null);
            string title = Path.Combine(dir, "title,a=b.lua"), resync = Path.Combine(dir, "resync.lua");
            string ytdlp = Path.Combine(dir, "yt-dlp.exe"), cookie = Path.Combine(dir, "cookies,a=b.txt");
            foreach (string file in new[] { title, resync, ytdlp, cookie }) File.WriteAllText(file, "synthetic");
            hub.GetField("Mpv", ReviewStatic).SetValue(null, typeof(HardeningTests).Assembly.Location);
            hub.GetField("YtDlp", ReviewStatic).SetValue(null, ytdlp);
            hub.GetField("LiveTitleScript", ReviewStatic).SetValue(null, title);
            hub.GetField("LiveResyncScript", ReviewStatic).SetValue(null, resync);
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, dir);
            hub.GetField("Cookie", ReviewStatic).SetValue(null, cookie);
            foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
            {
                object target = Call("Parse", platform + ":7777", "bili");
                object plan = Call("ResolvePlan", target, "mpv", false, false);
                string[] args = ParseCommandLine("player.exe " + (string)LookupGet(plan, "Arguments"));
                Check(Array.IndexOf(args, "--script=" + title.Replace('\\', '/')) >= 0
                    && Array.IndexOf(args, "--script=" + resync.Replace('\\', '/')) >= 0,
                    platform + " 脚本路径包含空格/中文/逗号时保持完整 argv");
                string options = MpvArgument(args, "--script-opts");
                Check(options != null && ReadMpvListValue(options, "live_title_state_file")
                    == ((string)Call("SessionRoomStatePath", target)).Replace('\\', '/'),
                    platform + " 状态路径按 UTF-8 字节长度完整传给 mpv 列表解析器");
                for (int i = 1; i < args.Length - 1; i++) Check(args[i].StartsWith("--"), platform + " 路径不会拆成额外媒体参数");
                if (platform == "bili")
                {
                    Check(ReadMpvListValue(options, "ytdl_hook-ytdl_path") == ytdlp.Replace('\\', '/'), "yt-dlp 路径内部逗号与等号完整保留");
                    Check(ReadMpvListValue(MpvArgument(args, "--ytdl-raw-options"), "cookies") == cookie.Replace('\\', '/'),
                        "Cookie 路径的 Windows argv 与 mpv 内部列表边界均完整");
                }
            }
        }
        finally
        {
            foreach (var pair in previous) hub.GetField(pair.Key, ReviewStatic).SetValue(null, pair.Value);
            Directory.Delete(Path.GetDirectoryName(dir), true);
        }
    }
}
