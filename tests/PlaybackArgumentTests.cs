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

    private static void TestMpvLiveClockPolicy()
    {
        string directory = ReviewDirectory();
        var previous = new Dictionary<string, object>();
        string[] fields = { "Mpv", "RuntimeDir", "Favorites", "Cookie", "LiveTitleScript", "LiveResyncScript" };
        try
        {
            foreach (string field in fields) previous[field] = hub.GetField(field, ReviewStatic).GetValue(null);
            hub.GetField("Mpv", ReviewStatic).SetValue(null, typeof(HardeningTests).Assembly.Location);
            hub.GetField("RuntimeDir", ReviewStatic).SetValue(null, directory);
            hub.GetField("Favorites", ReviewStatic).SetValue(null, Path.Combine(directory, "favorites.json"));
            hub.GetField("Cookie", ReviewStatic).SetValue(null, Path.Combine(directory, "synthetic-cookies.txt"));
            hub.GetField("LiveTitleScript", ReviewStatic).SetValue(null, Path.Combine(directory, "live-title.lua"));
            hub.GetField("LiveResyncScript", ReviewStatic).SetValue(null, Path.Combine(directory, "live-resync.lua"));
            foreach (string quality in new[] { "off", "light", "high" })
            {
                // DefaultPath follows Favorites, so all quality reads use this synthetic configuration.
                File.WriteAllText(Path.Combine(directory, "danmaku.json"), "{\"MpvQuality\":\"" + quality + "\"}");
                foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
                foreach (string buffer in new[] { "low", "stable" })
                {
                    string label = platform + "/" + buffer + "/" + quality;
                    object target = Call("Parse", platform + ":7777", "bili");
                    target.GetType().GetField("BufferMode").SetValue(target, buffer);
                    object plan = Call("ResolvePlan", target, "mpv", false, false);
                    string[] args = ParseCommandLine("mpv.exe " + (string)LookupGet(plan, "Arguments"));
                    int syncCount = 0, dropCount = 0, audioCount = 0, syncAt = -1, dropAt = -1, audioAt = -1, lastProfileAt = -1;
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i].StartsWith("--video-sync=", StringComparison.Ordinal)) { syncCount++; syncAt = i; }
                        if (args[i].StartsWith("--framedrop=", StringComparison.Ordinal)) { dropCount++; dropAt = i; }
                        if (args[i].StartsWith("--ao=", StringComparison.Ordinal)) { audioAt = i; audioCount++; }
                        if (args[i].StartsWith("--profile=", StringComparison.Ordinal)) lastProfileAt = i;
                    }
                    Check(syncCount == 1 && args[syncAt] == "--video-sync=audio",
                        label + " 直播唯一同步策略为音频时钟");
                    Check(dropCount == 1 && args[dropAt] == "--framedrop=vo",
                        label + " 明确允许视频输出丢弃迟到帧保持同步");
                    Check(syncAt > lastProfileAt && dropAt > lastProfileAt,
                        label + " 同步策略在画质 profile 之后生效");
                    Check(audioCount == 1 && MpvArgument(args, "--ao") == "openal" && audioAt > lastProfileAt,
                        label + " 默认 OpenAL 输出在画质 profile 之后生效");
                    Check(MpvArgument(args, "--autosync") == "30"
                        && MpvArgument(args, "--stop-playback-on-init-failure") == "yes",
                        label + " 平滑读数并在音频初始化失败时停止，避免无声播放");
                    Check(MpvArgument(args, "--hwdec") == "auto-safe", label + " 保留安全硬件解码");
                    Check(MpvArgument(args, "--cache") == "yes"
                        && MpvArgument(args, "--cache-secs") == (buffer == "low" ? "3" : "15")
                        && MpvArgument(args, "--demuxer-readahead-secs") == (buffer == "low" ? "2" : "10"),
                        label + " 保留原有缓冲档位");
                    Check((quality == "high" && MpvArgument(args, "--profile") == "high-quality")
                        || (quality != "high" && lastProfileAt == -1), label + " 按配置选择画质 profile");
                    Check((quality == "light" && MpvArgument(args, "--scale-antiring") == "0.7")
                        || (quality != "light" && MpvArgument(args, "--scale-antiring") == null),
                        label + " 按配置选择轻量画质参数");
                }
            }
        }
        finally
        {
            foreach (var pair in previous) hub.GetField(pair.Key, ReviewStatic).SetValue(null, pair.Value);
            Directory.Delete(directory, true);
        }
    }

    private static void TestMpvOpenAlLaunchPolicy()
    {
        string withOpenAl = "Available audio outputs:\r\n  wasapi Windows WASAPI audio output\r\n  openal OpenAL audio output\r\n  null Null audio output\r\n";
        Check((bool)Call("MpvAudioHelpSupportsOpenAl", withOpenAl), "OpenAL 能力检查接受真实 help 表形状与 CRLF");
        Check((bool)Call("MpvAudioHelpSupportsOpenAl", withOpenAl.Replace("\r\n", "\n")), "OpenAL 能力检查接受 LF");
        foreach (string help in new[] { "", "Error opening openal output", "Available audio outputs:\n  wasapi Windows WASAPI\n  null Null output\n", "  openal OpenAL output\n" })
        {
            Check(!(bool)Call("MpvAudioHelpSupportsOpenAl", help), "没有 OpenAL 帮助表条目时不猜测能力");
            bool rejected = false;
            try { Call("ValidateMpvAudioHelp", help); }
            catch (TargetInvocationException error)
            {
                rejected = error.InnerException is InvalidOperationException
                    && error.InnerException.Message.Contains("OpenAL") && error.InnerException.Message.Contains("full");
            }
            Check(rejected, "缺少 OpenAL 时给出可操作错误，不启动播放器或退回静音");
        }
        Call("ValidateMpvAudioHelp", withOpenAl);

        Call("ValidateMpvAudioProbe", withOpenAl, 0, false);
        foreach (int code in new[] { 1, 3 })
        {
            string message = ExpectMpvAudioError("ValidateMpvAudioProbe", withOpenAl, code, false);
            Check(message != null && message.Contains("退出码 " + code) && message.Contains("mpv"),
                "能力探测拒绝非零退出码 " + code + "，给出可操作错误");
        }
        string truncatedMessage = ExpectMpvAudioError("ValidateMpvAudioProbe", withOpenAl, 0, true);
        Check(truncatedMessage != null && truncatedMessage.Contains("64 KiB") && truncatedMessage.Contains("mpv"),
            "能力探测拒绝被截断的 help 输出，不猜测能力");
        Check(ExpectMpvAudioError("ValidateMpvAudioProbe",
            "Available audio outputs:\n  wasapi Windows WASAPI\n", 0, false) != null,
            "能力探测在 help 表缺少 openal 时拒绝");
        TestMpvOpenAlConfigurationValidation();
        foreach (string platform in new[] { "bili", "huya", "douyu", "douyin" })
        {
            object potTarget = Call("Parse", platform + ":7777", "bili");
            object potPlan = Call("ResolvePlan", potTarget, "potplayer", false, false);
            string planned = (string)LookupGet(potPlan, "Arguments");
            Check((string)LookupGet(potPlan, "Player") == "potplayer"
                && planned.StartsWith("/new ", StringComparison.Ordinal)
                && planned.IndexOf("--ao=", StringComparison.Ordinal) < 0
                && planned.IndexOf("--video-sync", StringComparison.Ordinal) < 0
                && planned.IndexOf("--autosync", StringComparison.Ordinal) < 0
                && planned.IndexOf("--stop-playback-on-init-failure", StringComparison.Ordinal) < 0,
                platform + " PotPlayer 计划不含 mpv 音频/同步参数");
        }

        string previous = Environment.GetEnvironmentVariable("ALSOFT_DRIVERS");
        string previousConfig = Environment.GetEnvironmentVariable("ALSOFT_CONF");
        string directory = ReviewDirectory();
        try
        {
            Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", "synthetic-parent-backend");
            Environment.SetEnvironmentVariable("ALSOFT_CONF", "synthetic-parent-config.ini");
            string executable = Path.Combine(directory, "synthetic player", "mpv.exe");
            string audioConfig = Path.Combine(directory, "synthetic-audio.ini");
            var start = (System.Diagnostics.ProcessStartInfo)Call("BuildPlayerProcessStartInfo", "mpv", executable,
                "--ao=openal -- https://example.invalid/live.flv", audioConfig);
            Check(!start.UseShellExecute && start.CreateNoWindow, "mpv 直接创建子进程并抑制控制台窗口");
            Check(start.EnvironmentVariables["ALSOFT_DRIVERS"] == "wasapi", "OpenAL 子进程严格锁定 WASAPI，不允许 DirectSound 回退");
            Check(Environment.GetEnvironmentVariable("ALSOFT_DRIVERS") == "synthetic-parent-backend", "音频策略不修改启动器或系统环境");
            Check(start.EnvironmentVariables["ALSOFT_CONF"] == audioConfig
                && Environment.GetEnvironmentVariable("ALSOFT_CONF") == "synthetic-parent-config.ini",
                "共享音频配置仅覆盖子进程的普通 OpenAL 全局配置，父环境不变");
            Check(start.FileName == executable && start.WorkingDirectory == Path.GetDirectoryName(executable)
                && start.Arguments == "--ao=openal -- https://example.invalid/live.flv", "音频环境设置保留原程序路径、参数和工作目录");
            var other = (System.Diagnostics.ProcessStartInfo)Call("BuildPlayerProcessStartInfo", "potplayer", executable, "/new", null);
            Check(other.UseShellExecute, "mpv 音频环境不改变 PotPlayer 启动行为");
            string missingPlayer = Path.Combine(directory, "missing-mpv.exe");
            string probeMessage = ExpectMpvAudioError("RequireMpvOpenAl", missingPlayer);
            Check(probeMessage != null && probeMessage.Contains("无法检查") && probeMessage.Contains("mpv"),
                "能力探测在播放器无法启动时给出可操作错误");
            Check(Environment.GetEnvironmentVariable("ALSOFT_DRIVERS") == "synthetic-parent-backend"
                && Environment.GetEnvironmentVariable("ALSOFT_CONF") == "synthetic-parent-config.ini",
                "能力探测失败也不修改启动器或系统环境");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", previous);
            Environment.SetEnvironmentVariable("ALSOFT_CONF", previousConfig);
            Directory.Delete(directory, true);
        }
    }

    private static string ExpectMpvAudioError(string method, params object[] args)
    {
        try { Call(method, args); }
        catch (TargetInvocationException error)
        {
            InvalidOperationException inner = error.InnerException as InvalidOperationException;
            return inner == null ? null : inner.Message;
        }
        return null;
    }

    private static void TestMpvOpenAlConfigurationValidation()
    {
        string directory = ReviewDirectory();
        try
        {
            string config = Path.Combine(directory, "assets", "audio", "openal-wasapi.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(config));
            string missing = Path.Combine(directory, "missing", "openal-wasapi.ini");
            string message = ExpectMpvAudioError("ValidateMpvOpenAlConfiguration", missing);
            Check(message != null && message.Contains("assets") && message.Contains("重新解压完整程序包"),
                "缺少包内 OpenAL 配置时拒绝并提示重新解压");
            File.WriteAllText(config, "[wasapi]\nexclusive-mode=false\nspatial-api=false\n", new UTF8Encoding(false));
            Call("ValidateMpvOpenAlConfiguration", config);
            Check(true, "合法的 [wasapi] 共享输出配置通过校验");
            File.WriteAllText(config, "\uFEFF[general]\nfoo=bar\n[wasapi]\n# 注释\n exclusive-mode = false \nspatial-api=false\n", new UTF8Encoding(false));
            Call("ValidateMpvOpenAlConfiguration", config);
            Check(true, "BOM/注释/其它段与空白不误伤合法配置");
            string[] invalid =
            {
                "",
                "[general]\nexclusive-mode=false\nspatial-api=false\n",
                "[wasapi]\nexclusive-mode=true\nspatial-api=false\n",
                "[wasapi]\nexclusive-mode=false\n",
                "[wasapi]\nspatial-api=false\n",
                "[wasapi]\nexclusive-mode=false\nexclusive-mode=false\nspatial-api=false\n",
                "[wasapi]\nexclusive-mode=false\nspatial-api=true\n",
                "[wasapi]\nexclusive-mode=0\nspatial-api=false\n"
            };
            for (int index = 0; index < invalid.Length; index++)
            {
                File.WriteAllText(config, invalid[index], new UTF8Encoding(false));
                message = ExpectMpvAudioError("ValidateMpvOpenAlConfiguration", config);
                Check(message != null && message.Contains("重新解压完整程序包"), "非法 OpenAL 配置被拒绝：样例 " + index);
            }
            File.WriteAllText(config, new string('x', 16 * 1024 + 1), new UTF8Encoding(false));
            message = ExpectMpvAudioError("ValidateMpvOpenAlConfiguration", config);
            Check(message != null && message.Contains("16 KiB") && message.Contains("重新解压完整程序包"), "超过 16 KiB 的配置被拒绝");
            File.WriteAllBytes(config, new byte[] { 0xFF, 0xFE, 0x00, 0x41 });
            message = ExpectMpvAudioError("ValidateMpvOpenAlConfiguration", config);
            Check(message != null && message.Contains("UTF-8") && message.Contains("重新解压完整程序包"), "非 UTF-8 配置被拒绝");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string FindBundledMpvExecutable()
    {
        if (string.IsNullOrEmpty(SourceRoot)) return null;
        string bin = Path.Combine(SourceRoot, "bin");
        if (!Directory.Exists(bin)) return null;
        string direct = Path.Combine(bin, "mpv", "mpv.exe");
        if (File.Exists(direct)) return direct;
        foreach (string directory in Directory.GetDirectories(bin, "mpv*"))
        {
            string candidate = Path.Combine(directory, "mpv.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void TestMpvOpenAlProbeAgainstBundledMpv()
    {
        string mpv = FindBundledMpvExecutable();
        if (mpv == null) TestRunner.Skip("当前布局没有自带 mpv（lite 包/开发树未放播放器），跳过真实 --ao=help 探测");
        Call("RequireMpvOpenAl", mpv);
        Check(true, "真实 mpv --ao=help 探测识别 OpenAL 输出（" + Path.GetFileName(Path.GetDirectoryName(mpv)) + "）");
    }

    private static void TestBoundedCapture(string self)
    {
        MethodInfo bounded = hub.GetMethod("RunCaptureBounded", Flags);
        object[] huge = new object[] { self, "--emit-stdout-huge", 3000, 1024, 0, false };
        string output = (string)bounded.Invoke(null, huge);
        Check(output.Length <= 1024 && (bool)huge[5], "有界捕获截断超限输出并标记 truncated");
        Check((int)huge[4] == 0, "有界捕获返回被测进程退出码 0");
        object[] failed = new object[] { self, "--exit-code 7", 3000, 0, 0, false };
        output = (string)bounded.Invoke(null, failed);
        Check(output.Length == 0 && (int)failed[4] == 7 && !(bool)failed[5], "有界捕获如实返回非零退出码且不误报截断");
        object[] legacy = new object[] { self, "--emit-stdout-huge", 3000 };
        output = (string)hub.GetMethod("RunCapture", Flags).Invoke(null, legacy);
        Check(output.Length >= 200000, "RunCapture 旧调用方仍不限输出（语义未变）");
    }
}
