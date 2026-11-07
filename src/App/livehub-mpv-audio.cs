using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // 直播默认音频输出：OpenAL Soft → WASAPI。OpenAL Soft 用自己的 WASAPI 缓冲延迟核算，
    // 不再沿用 mpv 直连 WASAPI 的延迟估计；autosync 只平滑延迟读数，不修复设备时钟。
    // 这一段**只进入 mpv 计划**；PotPlayer 计划与启动行为完全不经过这里。
    private const string MpvAudioOutputArgs = " --ao=openal --autosync=30 --stop-playback-on-init-failure=yes";
    // 能力探测：不读用户配置、不加载脚本、只列音频输出。正常 mpv 输出一小段列表并退出 0；
    // 这里不联网、不播放、不建窗口。
    private const string MpvAudioProbeArgs = "--no-config --load-scripts=no --ao=help";
    private const string OpenAlConfigRelativePath = @"assets\audio\openal-wasapi.ini";
    private const int OpenAlConfigMaxBytes = 16 * 1024;
    private const int MpvAudioProbeMaxChars = 64 * 1024;

    // 纯构造：测试直接检查子进程环境与启动模式，不真的拉起播放器，也不改启动器/Windows 环境。
    private static ProcessStartInfo BuildPlayerProcessStartInfo(string player, string fileName, string arguments, string openAlConfig)
    {
        ProcessStartInfo start = new ProcessStartInfo(fileName, arguments);
        start.WorkingDirectory = Path.GetDirectoryName(fileName);
        if (player == "mpv")
        {
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            if (string.IsNullOrEmpty(openAlConfig))
                throw new InvalidOperationException("缺少 OpenAL 音频配置资源 " + OpenAlConfigRelativePath + "，请重新解压完整程序包。");
            // 不带结尾逗号：WASAPI 后端不可用时，OpenAL 不得回退到 DirectSound 等其它后端。
            start.EnvironmentVariables["ALSOFT_DRIVERS"] = "wasapi";
            // ALSOFT_CONF 在普通用户/全局配置之后加载；包内 [wasapi] 只选共享、非空间化输出，
            // 不重写设备级覆盖项。两个变量都只作用于这个 mpv 子进程。
            start.EnvironmentVariables["ALSOFT_CONF"] = openAlConfig;
        }
        else
        {
            start.UseShellExecute = true;
        }
        return start;
    }

    private static string RequireMpvOpenAlConfiguration()
    {
        string config = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), OpenAlConfigRelativePath);
        ValidateMpvOpenAlConfiguration(config);
        return config;
    }

    // 离线校验包内 OpenAL 配置：缺失、读不出、超过 16 KiB、非 UTF-8、缺 [wasapi] 段、
    // exclusive-mode / spatial-api 不是 false 或有重复项，一律拒绝并给出可操作提示，
    // 而不是带着坏配置去拉播放器（那样只会无声或退回非预期后端）。
    private static void ValidateMpvOpenAlConfiguration(string config)
    {
        if (string.IsNullOrEmpty(config) || !File.Exists(config))
            throw new InvalidOperationException("缺少 OpenAL 音频配置资源 " + OpenAlConfigRelativePath + "，请重新解压完整程序包。");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(config); }
        catch (Exception error)
        {
            throw new InvalidOperationException("无法读取 OpenAL 音频配置 " + OpenAlConfigRelativePath + "，请重新解压完整程序包。", error);
        }
        if (bytes.Length > OpenAlConfigMaxBytes)
            throw new InvalidOperationException("OpenAL 音频配置超过 " + (OpenAlConfigMaxBytes / 1024) + " KiB 上限，请重新解压完整程序包。");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error)
        {
            throw new InvalidOperationException("OpenAL 音频配置不是有效的 UTF-8 文本，请重新解压完整程序包。", error);
        }
        if (!MpvOpenAlConfigurationTextIsValid(text))
            throw new InvalidOperationException("OpenAL 音频配置无效（需要 [wasapi] 段的 exclusive-mode=false 与 spatial-api=false，且不得重复），请重新解压完整程序包。");
    }

    // 只认实际的 INI 语义：注释/空行忽略，键必须落在 [wasapi] 段内，两个关键项都必须是 false。
    private static bool MpvOpenAlConfigurationTextIsValid(string text)
    {
        if (text == null) return false;
        bool inWasapi = false, exclusiveSeen = false, spatialSeen = false;
        bool exclusiveFalse = false, spatialFalse = false;
        string[] lines = text.TrimStart('\uFEFF').Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                inWasapi = line.Substring(1, line.Length - 2).Trim() == "wasapi";
                continue;
            }
            if (!inWasapi) continue;
            int equals = line.IndexOf('=');
            if (equals < 0) continue;
            string name = line.Substring(0, equals).Trim();
            string value = line.Substring(equals + 1).Trim();
            if (name == "exclusive-mode")
            {
                if (exclusiveSeen) return false;
                exclusiveSeen = true;
                exclusiveFalse = value == "false";
            }
            else if (name == "spatial-api")
            {
                if (spatialSeen) return false;
                spatialSeen = true;
                spatialFalse = value == "false";
            }
        }
        return exclusiveSeen && spatialSeen && exclusiveFalse && spatialFalse;
    }

    private static bool MpvAudioHelpSupportsOpenAl(string help)
    {
        if (string.IsNullOrEmpty(help)) return false;
        // 只匹配 help 表里的真实行，不把错误信息里出现的 openal 当成支持。
        return Regex.IsMatch(help, @"(?m)^[ \t]*Available audio outputs:[ \t]*\r?$")
            && Regex.IsMatch(help, @"(?m)^[ \t]+openal[ \t]+[^\r\n]+\r?$");
    }

    private static void ValidateMpvAudioHelp(string help)
    {
        if (!MpvAudioHelpSupportsOpenAl(help))
            throw new InvalidOperationException("这个 mpv 不支持直播默认使用的 OpenAL 音频输出。请在播放器路径中选择带 OpenAL 的 mpv，或使用自带兼容 mpv 的 full 版本。");
    }

    // 探测结果的纯判定（退出码/截断/help 文本），测试不需要真的启动进程就能覆盖正反例。
    private static void ValidateMpvAudioProbe(string help, int exitCode, bool truncated)
    {
        if (exitCode != 0)
            throw new InvalidOperationException("mpv 音频能力检查失败（退出码 " + exitCode + "），无法确认 OpenAL 支持。请重新选择有效的 mpv，或使用 full 版本自带的兼容播放器。");
        if (truncated)
            throw new InvalidOperationException("mpv 音频能力检查输出超过 " + (MpvAudioProbeMaxChars / 1024) + " KiB 上限，无法确认 OpenAL 支持。请重新选择有效的 mpv。");
        ValidateMpvAudioHelp(help);
    }

    private static void RequireMpvOpenAl(string executable)
    {
        string help;
        int exitCode;
        bool truncated;
        try
        {
            // 走共享的有界捕获：并排空两路管道、超时自己杀子进程，且只保留前 64 KiB。
            help = RunCaptureBounded(executable, MpvAudioProbeArgs, 3000, MpvAudioProbeMaxChars, out exitCode, out truncated);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("无法检查 " + Path.GetFileName(executable)
                + " 的 OpenAL 音频输出支持（探测进程启动、输出读取或等待失败）。请确认选择的是可用的 mpv，或使用 full 版本自带的兼容播放器。", error);
        }
        ValidateMpvAudioProbe(help, exitCode, truncated);
    }
}
