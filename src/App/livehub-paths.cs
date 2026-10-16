// --------------------------------------------------------------------------------------------
// 路径与配置解析（partial 的另一半）。
//
// 2026-09-27 从 live-hub-v2.cs 搬出来，并按评估报告 §8「开发路径仍写死在源码」整改：
//
// 便携包布局（把整个文件夹拷到任何地方都能用；判据是 exe 旁边有 bin\mpv\mpv.exe）：
//   看直播（封装）\看直播.exe
//                     bin\mpv\mpv.exe、bin\PotPlayer\PotPlayerMini64.exe、bin\yt-dlp.exe
//                     lua\live-title.lua、lua\live-resync.lua
//                     data\（收藏/历史/cookie）、runtime\（临时文件）
//
// 开发机布局**不再写死**，按下面这条链逐项解析（第一个命中的赢）：
//   ① 命令行：--mpv=  --potplayer=  --yt-dlp=  --streamlink=  --runtime=  --config=
//   ② 配置文件：exe 同目录的 live-hub.json（也认 appsettings.json）
//   ③ exe 同目录 / 同目录 bin\ 下的同名程序
//   ④ PATH
//   ⑤ 常见安装目录
//   ⑥ 兜底：程序目录 / 当前用户 LocalAppData，不绑定开发者账号
//
// 每一项的来源都记在 PathSources 里，`--self-test` 会把它写进 selftest.txt ——
// 「这台机器到底用了哪个 mpv」从此是一条可查的事实，不用猜。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // 运行期路径：由 InitPaths() 按「便携包 / 开发机」两种布局填好（见上面的说明）
    private static string Mpv;
    /// <summary>lite 绿色包（不带播放器，用户自己指定）——由 exe 旁边的 portable.marker 决定。</summary>
    internal static bool PortableLite;
    private static string PotPlayer;
    private static string YtDlp;
    private static string StreamlinkConsole;
    private static string LiveTitleScript;
    private static string LiveResyncScript;
    internal static string RuntimeDir;
    private static string LogDir;
    internal static string Cookie;
    internal static string Favorites;
    private static string History;
    private static bool Portable;
    private static string QrLibrary;   // zxing.dll（扫码登录用，可选：没有就提示缺组件）

    /// 命令行覆盖（--mpv= 之类）。key 用小写短名。
    private static readonly Dictionary<string, string> PathOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// 配置文件里读到的路径（live-hub.json / appsettings.json）
    private static readonly Dictionary<string, string> PathConfigValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// 每一项的**最终来源**（诊断与自检报告用）
    internal static readonly Dictionary<string, string> PathSources =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// 实际读到的配置文件路径（没有就是 null）
    internal static string PathConfigFile;

    /// <summary>把 --xxx= 形式的路径覆盖参数摘出来，返回去掉它们之后的 args。</summary>
    /// 为什么必须摘掉：后面的开关全是按位置判断的（args.Length == 1 && args[0] == "--self-test"），
    /// 留着这些参数会让 --self-test / --plan / --play 全部失效。
    internal static string[] ApplyPathOverrides(string[] args)
    {
        if (args == null) return new string[0];
        List<string> rest = new List<string>();
        foreach (string arg in args)
        {
            if (arg != null && arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') > 2)
            {
                int eq = arg.IndexOf('=');
                string key = arg.Substring(2, eq - 2).ToLowerInvariant();
                string value = arg.Substring(eq + 1);
                if (IsPathOption(key) && value.Length > 0)
                {
                    PathOverrides[key] = value;
                    continue;
                }
            }
            rest.Add(arg);
        }
        return rest.ToArray();
    }

    private static bool IsPathOption(string key)
    {
        switch (key)
        {
            case "mpv":
            case "potplayer":
            case "yt-dlp":
            case "ytdlp":
            case "streamlink":
            case "runtime":
            case "runtime-dir":
            case "config":
            case "cookie":
            case "favorites":
            case "history":
            case "qr-library":
            case "live-title":
            case "live-resync":
                return true;
            default:
                return false;
        }
    }

    /// <summary>读配置文件。优先 --config= 指定的，其次 exe 同目录的 live-hub.json / appsettings.json。</summary>
    private static void LoadPathConfig(string dir)
    {
        string path = null;
        string explicitPath;
        if (PathOverrides.TryGetValue("config", out explicitPath) && explicitPath.Length > 0)
            path = ExpandPath(explicitPath, dir);
        else
        {
            foreach (string name in new string[] { "live-hub.json", "appsettings.json" })
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        if (path == null || !File.Exists(path)) return;
        PathConfigFile = path;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null) return;
            string configDir = Path.GetDirectoryName(path);
            foreach (KeyValuePair<string, object> pair in root)
            {
                string value = Convert.ToString(pair.Value);
                if (string.IsNullOrEmpty(value)) continue;
                PathConfigValues[pair.Key.ToLowerInvariant()] = ExpandPath(value, configDir);
            }
        }
        catch (Exception ex)
        {
            // 配置写坏了不能让程序起不来 —— 但要留痕，否则「改了没效果」会变成玄学
            CrashLog("path-config", ex);
        }
    }

    /// <summary>展开 %VAR%，并把相对路径按 baseDir 解析（配置文件里写相对路径很自然）。</summary>
    internal static string ExpandPath(string value, string baseDir)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        try
        {
            if (!Path.IsPathRooted(expanded) && !string.IsNullOrEmpty(baseDir))
                expanded = Path.Combine(baseDir, expanded);
            return Path.GetFullPath(expanded);
        }
        catch (Exception) { return expanded; }
    }

    /// <summary>在 PATH 里找可执行文件，找不到返回 null。</summary>
    internal static string FindOnPath(string[] names)
    {
        string pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;
        string[] dirs = pathVariable.Split(';');
        foreach (string name in names)
        {
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception) { }
            }
        }
        return null;
    }

    private static void Note(string key, string path, string source)
    {
        PathSources[key] = source;
    }

    /// <summary>便携布局下的命令行覆盖：只认命令行，不认配置文件。</summary>
    /// 没给覆盖就返回 fallback（exe 旁边的默认位置）。
    private static string PortableOverride(string key, string fallback)
    {
        string value;
        if (PathOverrides.TryGetValue(key, out value) && value.Length > 0)
        {
            string resolved = ExpandPath(value, Path.GetDirectoryName(Application.ExecutablePath));
            Note(key, resolved, "命令行 --" + key + "=（覆盖便携布局默认）");
            return resolved;
        }
        return fallback;
    }

    /// <summary>便携布局下的**播放器**路径：命令行 &gt; 配置文件（exe 旁边的 live-hub.json）&gt; 包内默认。</summary>
    /// 为什么播放器要例外：lite 包不带播放器，用户必须在界面里选自己的 mpv/PotPlayer，
    /// 那个选择只能存进配置文件；而收藏/历史/Cookie 这些**数据目录**仍然只认 exe 旁边，
    /// 免得把一个从别处拷来的配置里的路径也一起继承过来。
    private static string PortableToolOverride(string key, string fallback)
    {
        string value;
        if (PathOverrides.TryGetValue(key, out value) && value.Length > 0)
        {
            string resolved = ExpandPath(value, Path.GetDirectoryName(Application.ExecutablePath));
            Note(key, resolved, "命令行 --" + key + "=（覆盖便携布局默认）");
            return resolved;
        }
        if (PathConfigValues.TryGetValue(key, out value) && value.Length > 0)
        {
            string resolved = ExpandPath(value, Path.GetDirectoryName(Application.ExecutablePath));
            Note(key, resolved, "配置文件（界面里选的播放器）");
            return resolved;
        }
        return fallback;
    }

    /// <summary>把「界面里选的播放器路径」写进配置文件（便携包就是 exe 旁边的 live-hub.json）。
    /// 失败时用 error 回一句人话，面板上直接显示。</summary>
    internal static bool SaveToolPath(string tool, string path, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "文件不存在：" + path;
                return false;
            }
            string target = PathConfigFile;
            if (string.IsNullOrEmpty(target))
                target = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "live-hub.json");
            var root = new Dictionary<string, object>();
            if (File.Exists(target))
            {
                try
                {
                    var loaded = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(target, Encoding.UTF8));
                    if (loaded != null) root = loaded;
                }
                catch (Exception ex) { Diagnostics.Report("旧 live-hub.json 损坏，按新文件重写", ex); }
            }
            root[tool] = path;
            AtomicWriteAllText(target, new JavaScriptSerializer().Serialize(root));
            PathConfigFile = target;
            PathConfigValues[tool.ToLowerInvariant()] = path;
            return true;
        }
        catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; return false; }
    }

    /// <summary>界面用：当前生效的播放器路径（tool = "mpv" / "potplayer"）。</summary>
    internal static string CurrentToolPath(string tool)
    {
        return tool == "mpv" ? Mpv : PotPlayer;
    }

    /// <summary>界面里选完播放器后立刻生效，不用重启。</summary>
    internal static void ApplyToolPath(string tool, string path)
    {
        if (tool == "mpv") Mpv = path; else PotPlayer = path;
    }

    /// <summary>按优先级链解析一个外部程序的路径。</summary>
    private static string ResolveTool(string key, string configKey, string[] sameDir, string[] pathNames,
        string[] commonPaths, string fallback)
    {
        string value;
        // ① 命令行
        if (PathOverrides.TryGetValue(key, out value) && value.Length > 0)
        {
            string resolved = ExpandPath(value, Path.GetDirectoryName(Application.ExecutablePath));
            Note(key, resolved, "命令行 --" + key + "=");
            return resolved;
        }
        // ② 配置文件
        if (PathConfigValues.TryGetValue(configKey, out value) && value.Length > 0)
        {
            Note(key, value, "配置文件 " + Path.GetFileName(PathConfigFile));
            return value;
        }
        // ③ 同目录
        foreach (string candidate in sameDir)
        {
            if (candidate != null && File.Exists(candidate))
            {
                Note(key, candidate, "程序同目录");
                return candidate;
            }
        }
        // ④ PATH
        string onPath = FindOnPath(pathNames);
        if (onPath != null) { Note(key, onPath, "PATH"); return onPath; }
        // ⑤ 常见安装目录
        foreach (string candidate in commonPaths)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            string expanded = ExpandPath(candidate, null);
            if (File.Exists(expanded)) { Note(key, expanded, "常见安装目录"); return expanded; }
        }
        // ⑥ 兜底（程序目录或当前用户目录）
        Note(key, fallback, "默认位置" + (File.Exists(fallback) ? "" : "，文件不存在"));
        return fallback;
    }

    /// <summary>解析一个目录：命令行 > 配置文件 > 兜底，并创建它。</summary>
    private static string ResolveDirectory(string key, string configKey, string fallback)
    {
        string value;
        string dir = fallback;
        string source = "默认位置";
        if (PathOverrides.TryGetValue(key, out value) && value.Length > 0)
        {
            dir = ExpandPath(value, Path.GetDirectoryName(Application.ExecutablePath));
            source = "命令行 --" + key + "=";
        }
        else if (PathConfigValues.TryGetValue(configKey, out value) && value.Length > 0)
        {
            dir = value;
            source = "配置文件 " + Path.GetFileName(PathConfigFile);
        }
        Note(key, dir, source);
        try { Directory.CreateDirectory(dir); } catch (Exception ex) { Diagnostics.Report("运行目录创建", ex); }
        return dir;
    }

    /// <summary>填入全部运行期路径。</summary>
    private static void InitPaths()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveHub");
        LoadPathConfig(dir);
        string portableMpv = Path.Combine(dir, @"bin\mpv\mpv.exe");
        // 便携判定：自带 mpv 的包（full），**或者**带了 portable.marker 的包（lite：不带播放器，
        // 但同样要「数据放 exe 旁边、删目录即卸载」的便携语义）。
        Portable = File.Exists(portableMpv) || File.Exists(Path.Combine(dir, "portable.marker"));
        // lite 包（portable.marker 里写了 lite）：**本来就不带播放器**，程序要照常起、
        // 由用户在界面里指自己的 mpv/PotPlayer —— 自检与启动提示都按这个事实说话。
        PortableLite = false;
        try
        {
            string marker = Path.Combine(dir, "portable.marker");
            if (File.Exists(marker)) PortableLite = File.ReadAllText(marker).IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception ex) { Diagnostics.Report("读取 portable.marker", ex); }
        if (Portable)
        {
            // 便携布局：默认值全部取自 exe 旁边（拷到哪都一样跑，这是设计意图），
            // **配置文件不参与** —— 那份配置可能是从别的机器/别的包带过来的，跟着走反而更意外。
            // 但**命令行覆盖仍然生效**：`--runtime=.\runtime` 这种是本次调用的显式意图，
            // 便携包也该听（2026-09-27 补；此前便携分支会把命令行一起忽略，
            // 直接后果是「同一个测试在便携布局下必然失败」—— 发布流水线实测撞到过）。
            Mpv = PortableToolOverride("mpv", portableMpv);
            PotPlayer = PortableToolOverride("potplayer", Path.Combine(dir, @"bin\PotPlayer\PotPlayerMini64.exe"));
            YtDlp = PortableOverride("yt-dlp", Path.Combine(dir, @"bin\yt-dlp.exe"));
            StreamlinkConsole = PortableOverride("streamlink", null);
            LiveTitleScript = PortableOverride("live-title", Path.Combine(dir, @"lua\live-title.lua"));
            LiveResyncScript = PortableOverride("live-resync", Path.Combine(dir, @"lua\live-resync.lua"));
            RuntimeDir = PortableOverride("runtime", Path.Combine(dir, "runtime"));
            LogDir = RuntimeDir;
            Cookie = PortableOverride("cookie", Path.Combine(dir, @"data\bili-cookies.txt"));
            Favorites = PortableOverride("favorites", Path.Combine(dir, @"data\favorites.json"));
            History = PortableOverride("history", Path.Combine(dir, @"data\history.json"));
            QrLibrary = PortableOverride("qr-library", ResolvePortableQrLibrary(dir));
            foreach (string key in new string[] { "mpv", "potplayer", "yt-dlp", "runtime", "cookie", "favorites", "qr-library" })
                if (!PathSources.ContainsKey(key)) Note(key, "", "便携包（exe 旁边）");
        }
        else
        {
            Mpv = ResolveTool("mpv", "mpv",
                new string[] { Path.Combine(dir, "mpv.exe"), Path.Combine(dir, @"bin\mpv\mpv.exe") },
                new string[] { "mpv.exe" },
                new string[] { @"%ProgramFiles%\mpv\mpv.exe", @"%LOCALAPPDATA%\Programs\mpv\mpv.exe" },
                Path.Combine(dir, @"bin\mpv\mpv.exe"));
            PotPlayer = ResolveTool("potplayer", "potplayer",
                new string[] { Path.Combine(dir, "PotPlayerMini64.exe"), Path.Combine(dir, @"bin\PotPlayer\PotPlayerMini64.exe") },
                new string[] { "PotPlayerMini64.exe" },
                new string[] { @"%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini64.exe",
                    @"%ProgramFiles(x86)%\DAUM\PotPlayer\PotPlayerMini64.exe" },
                Path.Combine(dir, @"bin\PotPlayer\PotPlayerMini64.exe"));
            YtDlp = ResolveTool("yt-dlp", "ytdlp",
                new string[] { Path.Combine(dir, "yt-dlp.exe"), Path.Combine(dir, @"bin\yt-dlp.exe") },
                new string[] { "yt-dlp.exe" },
                new string[] { @"%LOCALAPPDATA%\Programs\yt-dlp\yt-dlp.exe" },
                Path.Combine(dir, @"bin\yt-dlp.exe"));
            StreamlinkConsole = ResolveTool("streamlink", "streamlink",
                new string[] { Path.Combine(dir, @"bin\streamlink.exe"), Path.Combine(dir, @".venv\Scripts\streamlink.exe") },
                new string[] { "streamlink.exe", "streamlinkw.exe" },
                new string[0],
                Path.Combine(dir, @"bin\streamlink.exe"));
            LiveTitleScript = ResolveTool("live-title", "livetitlescript",
                new string[] { Path.Combine(dir, @"lua\live-title.lua"), Path.Combine(dir, "live-title.lua") },
                new string[0], new string[0],
                Path.Combine(dir, "live-title.lua"));
            LiveResyncScript = ResolveTool("live-resync", "liveresyncscript",
                new string[] { Path.Combine(dir, @"lua\live-resync.lua") },
                new string[0],
                new string[] { @"%APPDATA%\mpv\scripts\live-resync.lua" },
                Path.Combine(dir, @"lua\live-resync.lua"));
            RuntimeDir = ResolveDirectory("runtime", "runtimedir", Path.Combine(userData, "runtime"));
            LogDir = RuntimeDir;
            Note("log", LogDir, "= runtime 目录");
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Cookie = ResolveDevelopmentDataFile("cookie", dir, userData, userProfile, "bili-cookies.txt");
            Favorites = ResolveDevelopmentDataFile("favorites", dir, userData, userProfile, "favorites.json");
            History = ResolveDevelopmentDataFile("history", dir, userData, userProfile, "history.json");
            QrLibrary = ResolveTool("qr-library", "qrlibrary",
                new string[] { Path.Combine(dir, @"lib\zxing.dll"), Path.Combine(dir, @"bin\zxing.dll") },
                new string[0], new string[0],
                Path.Combine(dir, @"lib\zxing.dll"));
        }
        try { Directory.CreateDirectory(RuntimeDir); } catch (Exception ex) { Diagnostics.Report("运行目录创建", ex); }
    }

    internal static string ResolveDevelopmentDataFile(string key, string directory, string userData,
        string userProfile, string fileName)
    {
        string current = Path.Combine(userData, "data", fileName);
        string legacy = Path.Combine(userProfile, ".bili-live-watch", fileName);
        string resolved = ResolveTool(key, key, new string[] { Path.Combine(directory, "data", fileName) },
            new string[0], new string[] { current, legacy }, current);
        string source;
        if (PathSources.TryGetValue(key, out source) && source == "常见安装目录")
            Note(key, resolved, resolved == legacy ? "已有旧版用户数据（原位读取）" : "已有当前用户数据");
        return resolved;
    }

    internal static string ResolvePortableQrLibrary(string directory)
    {
        string current = Path.Combine(directory, @"lib\zxing.dll");
        string legacy = Path.Combine(directory, @"bin\zxing.dll");
        return File.Exists(current) || !File.Exists(legacy) ? current : legacy;
    }

    internal static bool IsPersistentDataPath(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string value in new string[] { Cookie, Favorites, History })
        {
            if (string.IsNullOrEmpty(value)) continue;
            string target = Path.GetFullPath(value);
            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, target + ".json", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>把「解析到了什么、从哪来的」写成几行，给自检报告 / 托盘诊断用。</summary>
    internal static string[] DescribeResolvedPaths()
    {
        List<string> lines = new List<string>();
        lines.Add("PORTABLE=" + (Portable ? "yes" : "no") + (PathConfigFile != null ? "  CONFIG=" + PathConfigFile : "  CONFIG=(none)"));
        foreach (string key in new string[] { "mpv", "potplayer", "yt-dlp", "streamlink", "live-title", "live-resync", "runtime", "cookie", "favorites", "qr-library" })
        {
            string source;
            PathSources.TryGetValue(key, out source);
            lines.Add("TOOL " + key + " = " + GetPathForReport(key) + "   <- " + (source == null ? "(未解析)" : source));
        }
        return lines.ToArray();
    }

    private static string GetPathForReport(string key)
    {
        switch (key)
        {
            case "mpv": return Mpv;
            case "potplayer": return PotPlayer;
            case "yt-dlp": return YtDlp;
            case "streamlink": return StreamlinkConsole;
            case "live-title": return LiveTitleScript;
            case "live-resync": return LiveResyncScript;
            case "runtime": return RuntimeDir;
            case "cookie": return Cookie;
            case "favorites": return Favorites;
            case "history": return History;
            case "qr-library": return QrLibrary;
            default: return "";
        }
    }

    // 传最终绝对路径而非原始 --config，相对配置与便携默认值在子进程中也保持一致。
    private static string ChildProcessPathArguments()
    {
        StringBuilder arguments = new StringBuilder();
        foreach (string key in new string[] { "mpv", "potplayer", "yt-dlp", "streamlink", "live-title", "live-resync",
            "runtime", "cookie", "favorites", "history", "qr-library" })
        {
            string value = GetPathForReport(key);
            if (string.IsNullOrEmpty(value)) continue;
            arguments.Append(' ').Append(QuoteWindowsArg("--" + key + "=" + Path.GetFullPath(value)));
        }
        return arguments.ToString();
    }

    // 几个给测试用的只读入口（产品代码走字段，不需要这些）
    internal static string MpvPathForTest { get { return Mpv; } }
    internal static string YtDlpPathForTest { get { return YtDlp; } }
    internal static string RuntimeDirForTest { get { return RuntimeDir; } }
}
