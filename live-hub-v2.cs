using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 2026-09-27：拆成 partial（评估报告 §7）。路径与配置解析在 src\App\livehub-paths.cs，
// 凭据/中转等区域也会陆续搬出去；本文件保留入口与启动保护，其余职责位于 App 分片。
internal static partial class LiveHub
{
    // 运行期路径（Mpv / PotPlayer / YtDlp / RuntimeDir / Cookie …）与它们的解析规则
    // 2026-09-27 起搬到 src\App\livehub-paths.cs（partial 的另一半），见那里的说明。
    // PotPlayer 播不了 B站 HLS 的 hevc 变体，优先挑 FLV（H.264）的原画
    private const string BiliFlvFormat =
        "best[format_id^=source][ext=flv]/best[format_id^=blue_ray][ext=flv]/best[ext=flv]/best";

    private static bool SelfTestMode;
    private static string LastAnchorError;

    // internal（不是 private）：平台适配器接口是 public 的，成员签名不能比接口更封闭（CS0051）。
    internal sealed class Target
    {
        public string Platform;
        public string Room;
        public string Url;
        public string SessionId;
        public string Quality = "best";
        public string BufferMode = "low";
        public string DanmakuMode = "auto";   // auto=按设置 / on=本次开 / off=本次关
        public int RelayProcessId;
    }

    private sealed class PlaybackSession
    {
        public Target Target;
        public string Player;
        public Process Process;
        public RoomStateKind LiveState = RoomStateKind.Unknown;
        public bool Polling;
        public DateTime LastPoll;
        public bool Ended;
    }

    private static string RelaySessionId;
    private static string RelayQuality = "best";
    private static string RelayPlatform;
    private static string RelayRoom;
    // 单飞（界面同一时刻只允许一次启动，见 currentLaunch 守卫），所以普通静态字段就够；
    // 原先是 [ThreadStatic]，把"当前启动"绑在某个线程上，换线程就悄悄失效。
    private static CancellationToken LaunchCancellation;

    private sealed class HistoryEntry
    {
        public string Room;
        public string Name;
        public string At;
    }

    // InitPaths()（便携包 / 开发机两种布局的路径解析）2026-09-27 搬到 src\App\livehub-paths.cs，
    // 并按评估报告 §8 改成「命令行 > 配置文件 > 同目录 > PATH > 常见安装目录 > 兜底」，
    // 开发机绝对路径只作为最后一档兜底，不再是唯一出路。

    // 进程级安全网 + 网络默认值（2026-09-20 加固）
    //
    // 连接上限：.NET Framework 的 ServicePointManager.DefaultConnectionLimit 默认就是 **2**
    // 。而本程序是「一个中转进程同时持有多条长连接」
    // 的模型——探测连接 + 正式连接 + 重连就是 2 条打底，第 3 条 HttpWebRequest 会排在长连接
    // 后面等到 Timeout(15s) 才失败，表现成「偶发连不上/首包特别慢」。这里统一放宽到 64。
    // TLS：原来是硬编码 Tls12，平台以后要求 TLS 1.3 时会被自己挡住；SystemDefault 交给系统策略。
    // 异常：winexe 里未捕获异常以前会让进程静默消失（中转崩 = 播放器那头只是「卡住」），
    // 现在至少落一份 runtime\logs\crash-*.log，并把中转状态写成 RECONNECTING 让播放器提示。
    private static void InstallSafetyNets()
    {
        // 为兼容 .NET Framework 4.8 的流处理，避免 SystemDefault 与部分 CDN 协商到
        // TLS 1.3，而 4.8 的 SslStream 处理不了，症状是中转「上游异常: WebException 基础连接已经关闭:
        // 接收时发生错误」、连刷重连、播放器 0 字节（页面的 HTTPS 调用却正常，所以极难一眼看出）。
        // 显式钉 Tls12；将来要上 TLS 1.3 得先把运行时升到 .NET Framework 4.8.1+（Win11）。
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (Exception) { }
        try { ServicePointManager.DefaultConnectionLimit = 64; } catch (Exception) { }
        try { ServicePointManager.Expect100Continue = false; } catch (Exception) { }
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch (Exception) { }
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            CrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            PublishCrashState();
        };
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
        {
            CrashLog("Application.ThreadException", e.Exception);
            PublishCrashState();
            // ⚠️ 覆盖层进程**不弹模态框**：它是压在视频上的透明层，弹个框既挡住游戏/直播，
            //    又会把渲染循环停在那里等用户点确定。
            //    改成写日志 + 在画面顶部飘一行字，用户照样知道，但不用去点它。
            if (OverlayMessage != null)
            {
                try { OverlayMessage("弹幕遇到内部错误（已记日志）：" + (e.Exception == null ? "" : e.Exception.Message)); }
                catch (Exception report) { CrashLog("OverlayMessage", report); }   // 提示也失败就再记一条，别静默
                return;
            }
            try
            {
                string where = LogsDirectory();
                MessageBox.Show("看直播遇到一个内部错误，已经记到：\n" + where + "\n\n"
                    + (e.Exception == null ? "" : e.Exception.Message), "看直播",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception) { }
        };
    }

    // runtime\ 是个只长不缩的目录：每个播放会话 3 个文件（state/relay/log）＋每个房间的
    // anchor/bitrate 提示＋每个中转的 status。长时间运行后这些文件会持续积累。
    // 启动时清一次：只删 runtime\ **根下**、mtime 超过 7 天、且文件名命中已知前缀/后缀的文件；
    // runtime\logs\ 不动（那是给人看的日志）。删不掉就跳过，绝不影响启动。
    private static void PruneRuntime()
    {
        try
        {
            if (RuntimeDir == null || !Directory.Exists(RuntimeDir)) return;
            BoundedLog.Maintain(Path.Combine(RuntimeDir, "logs"));
            BoundedLog.Maintain(RuntimeDir);
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            int removed = 0;
            foreach (string path in Directory.GetFiles(RuntimeDir))
            {
                string name = Path.GetFileName(path);
                if (!IsRuntimeTemporaryFile(name)) continue;
                if (IsPersistentDataPath(path)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (Exception) { }
            }
            if (removed > 0 && !BoundedLog.TryAppend(Path.Combine(LogsDirectory(), "janitor.log"),
                "清理 runtime 根下 " + removed + " 个超过 7 天的文件")) Diagnostics.ReportLogBroken(null);

        }
        catch (Exception) { }
    }

    internal static bool RuntimeCacheName(string name) { return IsRuntimeTemporaryFile(name); }

    private static bool IsRuntimeTemporaryFile(string name)
    {
        // 凭据的 mtime 不是有效期；任何清理规则都不能删除它们或其原子写临时文件。
        if (string.IsNullOrEmpty(name) || name.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0
            || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".credential.tmp", StringComparison.OrdinalIgnoreCase)) return false;
        return name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("relay-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("plan-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("out-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("err-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-plan.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".anchor.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bitrate.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".streaminfo.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".state.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".relay.txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".status", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    // 日志统一落 runtime\logs\（janitor 只清 runtime\ 根下的状态/中转文件，不碰这个目录）
    private static string LogsDirectory()
    {
        string dir = Path.Combine(RuntimeDir == null ? "." : RuntimeDir, "logs");
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
        return dir;
    }

    private static void CrashLog(string source, Exception ex)
    {
        try
        {
            if (!BoundedLog.TryAppend(Path.Combine(LogsDirectory(), "crash-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + source + "] "
                + (ex == null ? "(没有异常对象)" : ex.ToString()))) Diagnostics.ReportLogBroken(ex);
        }
        catch (Exception) { }
    }

    // 中转进程崩了要让播放器看得见：把当前会话的连接状态写成 RECONNECTING（live-title.lua 会弹「连接中断，正在重连…」）
    private static void PublishCrashState()
    {
        try
        {
            if (RelaySessionId != null && RelayPlatform != null && RelayRoom != null)
                PublishRelayState(RelayPlatform, RelayRoom, "RECONNECTING");
        }
        catch (Exception) { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // ⚠️ 必须是**第一件事**：DPI 感知一旦创建了窗口就改不了了。
        // 不声明的话，200% 缩放的屏上 Windows 会把整个窗口位图放大 2 倍 ——
        // 尺寸对但**所有文字都糊**（用户反馈的就是这个）。
        DpiAwareness.Enable();
        // 必须在 Enable **之后**读 DPI —— 感知级别没设对时拿到的永远是 96。
        // 之后所有布局缩放都走 DpiLayout.S()，见它自己的注释。
        DpiLayout.Measure();

        // 界面字体：随包的 HarmonyOS Sans SC。也要在建控件之前注册 ——
        // ModernTheme.UiFont 是静态字段，第一次访问就定下来了。
        UiFonts.Register(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "fonts"));

        // 路径覆盖参数（--mpv= / --potplayer= / --yt-dlp= / --streamlink= / --runtime= / --config=）
        // 要先于 InitPaths 解析，并且**从 args 里摘掉** —— 后面那些按位置取参数的开关（--self-test 等）
        // 用的是 args.Length，留着会让它们全部失效。
        args = ApplyPathOverrides(args);

        InitPaths();
        InstallSafetyNets();
        PruneRuntime();

        if (args.Length >= 3 && args.Length <= 4 && args[0] == "--lookup-douyin-probe")
        {
            bool browserProbe = args.Length == 4 && args[3] == "--browser";
            if (args.Length == 4 && !browserProbe) { Environment.Exit(2); return; }
            Environment.Exit(ProbeDouyinLookup(args[1], args[2], browserProbe)); return;
        }

        // --ui-probe <json>：只建窗、不进消息循环，把真实控件树导成 JSON（结构回归用）。
        // 不弹窗、不联网：GUI 冒烟（UIA）只能在有桌面会话时跑，这条给离线回归兜底。
        // 可选参数：
        //   --dark / --light          强制主题（回归测试不依赖用户的配置）
        //   --then-dark / --then-light 建完控件后再切一次主题 —— 这才是**运行时切换**的真实路径
        //                              （用户在设置对话框里勾/取消），专查「只在构造函数里取色」的控件。
        if (args.Length >= 2 && args.Length <= 4 && args[0] == "--ui-probe")
        {
            try
            {
                int themeOverride = args.Length >= 3 && args[2] == "--dark" ? 1
                    : args.Length >= 3 && args[2] == "--light" ? -1 : 0;
                Form probe = BuildLauncherForm(themeOverride);
                if (args.Length == 4 && args[3] == "--then-dark") ModernTheme.Apply(true);
                else if (args.Length == 4 && args[3] == "--then-light") ModernTheme.Apply(false);
                WriteUiProbe(probe, args[1]);
                probe.Dispose();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[1], "EXCEPTION " + ex); } catch (Exception) { }
                Environment.Exit(1);
            }
        }

        // --settings-probe <json>：把「弹幕与 SC 设置」对话框搭出来（不显示）并导出控件树 ——
        // 深色模式最常在**这个对话框**里切，所以它的配色也得能被离线回归盯住。
        if (args.Length >= 2 && args.Length <= 4 && args[0] == "--settings-probe")
        {
            try
            {
                int themeOverride = args.Length >= 3 && args[2] == "--dark" ? 1
                    : args.Length >= 3 && args[2] == "--light" ? -1 : 0;
                LauncherUi.ApplyTheme(themeOverride);
                SettingsDialogParts parts = DanmakuSettingsDialog.Build(DanmakuHost.LoadOrCreateConfig());
                // 对话框里那个「深色模式」勾选框走的是同一个 Changed 事件，这里等价复现
                if (args.Length == 4 && args[3] == "--then-dark") ModernTheme.Apply(true);
                else if (args.Length == 4 && args[3] == "--then-light") ModernTheme.Apply(false);
                DpiLayout.ScaleTree(parts.Dialog);
                WriteUiProbe(parts.Dialog, args[1]);
                parts.Dialog.Dispose();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[1], "EXCEPTION " + ex); } catch (Exception) { }
                Environment.Exit(1);
            }
        }

        if (args.Length == 1 && args[0] == "--self-test")
        {
            SelfTestMode = true;   // 自检不联网、不弹窗
            Environment.Exit(SelfTest() ? 0 : 1);
        }
        // 斗鱼中转进程：--relay <平台> <房间> <端口>（winexe 无窗口，由启动器拉起后独立运行）
        if ((args.Length == 4 || args.Length == 5 || args.Length == 6) && args[0] == "--relay")
        {
            int relayPort;
            string statusFile = args.Length == 5 && Regex.IsMatch(args[4], "^[0-9a-f]{32}$")
                ? Path.Combine(RuntimeDir, "relay-" + args[4] + ".status") : null;
            if (args.Length == 6 && Regex.IsMatch(args[4], "^[0-9a-f]{32}$"))
            {
                RelaySessionId = args[4];
                RelayQuality = NormalizeQuality(args[5]);
                statusFile = Path.Combine(RuntimeDir, "relay-" + args[4] + ".status");
            }
            // 平台/房间一律按白名单校验：这两个值直接拼进文件名（runtime\relay-*、session-*）与日志，
            // 不校验时 `--relay huya ..\..\x 1234` 能写到 runtime\ 之外（--watch 分支早就是这么做的）。
            if (!Regex.IsMatch(args[1], "^(bili|huya|douyu|douyin)$") || !Regex.IsMatch(args[2], "^[A-Za-z0-9_-]+$"))
            {
                RelayLog("中转参数非法（平台/房间），拒绝启动：" + args[1] + " " + args[2]);
                PublishRelayStatus(statusFile, "ERROR");
                Environment.Exit(2);
            }
            RelayPlatform = args[1];
            RelayRoom = args[2];
            if (int.TryParse(args[3], out relayPort))
            {
                try { RunRelay(args[1], args[2], relayPort, statusFile); }
                catch (Exception ex)
                {
                    RelayLog("中转启动失败: " + ex.GetType().Name + " " + ex.Message);
                    PublishRelayStatus(statusFile, "ERROR");
                    Environment.Exit(2);
                }
            }
            Environment.Exit(0);
        }
        if (args.Length == 5 && args[0] == "--watch")
        {
            int playerId;
            if (Regex.IsMatch(args[1], "^(bili|huya|douyu|douyin)$")
                && Regex.IsMatch(args[2], "^[A-Za-z0-9_-]+$")
                && Regex.IsMatch(args[3], "^[0-9a-f]{32}$")
                && int.TryParse(args[4], out playerId))
                RunRoomWatcher(args[1], args[2], args[3], playerId);
            Environment.Exit(0);
        }
        // ==DANMAKU-MAIN-BEGIN==
        // 弹幕覆盖层：--danmaku <平台> <房间> <会话号> <播放器pid> [overlay]
        // 与 --watch 同构：由启动器按会话拉起，播放器退出就自己收工；关掉启动器窗口不影响它。
        if (args.Length >= 5 && args[0] == "--danmaku")
        {
            int danmakuPlayerPid;
            if (Regex.IsMatch(args[1], "^(bili|douyu|huya|douyin)$")
                && Regex.IsMatch(args[2], "^[A-Za-z0-9_-]+$")
                && Regex.IsMatch(args[3], "^[0-9a-f]{32}$")
                && int.TryParse(args[4], out danmakuPlayerPid))
            {
                string danmakuMode = args.Length >= 6 ? args[5] : "overlay";
                string danmakuShot = args.Length >= 7 ? args[6] : null;
                int danmakuExit = 1;
                try { danmakuExit = DanmakuHost.Run(args[1], args[2], args[3], danmakuPlayerPid, danmakuMode, danmakuShot); }
                catch (Exception ex) { DanmakuHost.Log("弹幕进程异常: " + ex); }
                Environment.Exit(danmakuExit);
            }
            Environment.Exit(2);
        }
        // 弹幕链路自检：--danmaku-probe 平台:房间 输出文件 [秒数]（无界面，可当健康检查反复跑）
        if (args.Length >= 3 && args[0] == "--danmaku-probe")
        {
            int danmakuProbeSeconds = 20;
            if (args.Length >= 4) int.TryParse(args[3], out danmakuProbeSeconds);
            Environment.Exit(DanmakuHost.Probe(args[1], args[2], danmakuProbeSeconds));
        }
        // ==DANMAKU-MAIN-END==
        // 调试用：--qr <二维码png> <报告txt>（走一遍扫码登录的申请+轮询，但不弹窗）
        if (args.Length == 3 && args[0] == "--qr")
        {
            var report = new List<string>();
            try
            {
                var jar = new CookieContainer();
                string key, url;
                bool ok = RequestQrCode(jar, out key, out url);
                report.Add("GENERATE=" + ok);
                if (ok)
                {
                    report.Add("QR-URL=" + url);
                    using (Image image = RenderQr(url))
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    report.Add("QR-PNG=" + args[1]);
                    string message, redirect;
                    int code = PollQrCode(jar, key, out message, out redirect);
                    report.Add("POLL-CODE=" + code + " MSG=" + (message ?? ""));
                }
            }
            catch (Exception ex)
            {
                report.Add("ERROR=" + ex.GetType().Name + " " + ex.Message);
            }
            File.WriteAllLines(args[2], report, new UTF8Encoding(false));
            Environment.Exit(0);
        }
        // 调试用：--streaminfo 平台:房间 输出文件（打印解析到的标题/画质，不启动播放）
        if (args.Length == 3 && args[0] == "--streaminfo")
        {
            var lines = new List<string>();
            int exitCode = 0;
            try
            {
                Target info = Parse(args[1], "bili");
                if (info == null) { lines.Add("PARSE-FAILED"); exitCode = 1; }
                else
                {
                    info.SessionId = NewSessionId();
                    LaunchPlan infoPlan = new LaunchPlan();
                    infoPlan.Target = info;
                    infoPlan.Player = "potplayer";
                    infoPlan.FileName = PotPlayer;
                    string titleText;
                    string infoProbe;
                    // 这条诊断路径**只解析、不起中转**（allowSideEffects=false）：中转地址里是端口占位符
                    string playerUrl = BuildPotPlayerTitle(info, infoPlan, false, out titleText, out infoProbe);
                    lines.Add("TITLE=" + titleText);
                    lines.Add("URL=" + playerUrl);
                    lines.Add("RELAY=" + (infoPlan.NeedsRelay ? "not-started（只解析；要真起中转走 --plan --relay 或 --play）" : "none"));
                    if (info.Platform == "huya")
                    {
                        string direct = BuildHuyaUrlFromPage(info.Room);
                        lines.Add("HUYA-DIRECT=" + (direct == null ? "(null)" : direct));
                        lines.Add("HUYA-DIRECT-OK=" + (direct != null && UrlResponds(direct)));
                        lines.Add("HUYA-CDNS=" + ListHuyaCdns(info.Room));
                    }
                    string probe = infoProbe;
                    // 深度测量要按实时速率读 3 秒流，所以只在这条诊断路径上开，且只在字段确实缺的时候生效
                    StreamInfo meta = probe == null ? null : ReadFlvInfo(probe, true);
                    lines.Add(meta == null
                        ? "INFO=(null)"
                        : "INFO=" + meta.Width + "x" + meta.Height
                          + " fps=" + (meta.Fps > 0 ? Math.Round(meta.Fps).ToString() : "?(上游元数据没有，可看 DEEP)")
                          + " codec=" + (meta.Codec ?? "-")
                          + " kbps=" + (meta.Kbps > 0 ? meta.Kbps.ToString() : "?"));   // 0 会被误读成「真的 0」，所以未知一律打 ?

                }
            }
            catch (Exception ex)
            {
                lines.Add("ERROR=" + ex.GetType().Name + " " + ex.Message);
                exitCode = 1;
            }
            File.WriteAllLines(args[2], lines, new UTF8Encoding(false));
            Environment.Exit(exitCode);
        }
        // 调试用：--plan [mpv|potplayer] 平台:房间 输出文件（不启动播放，只把命令行写出来）
        if (args.Length >= 3 && args[0] == "--plan")
        {
            // --relay 可出现任意位置：带上它才真起中转（给需要实播的自测脚本用）
            bool withRelay = false;
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--relay") withRelay = true; else rest.Add(args[i]);
            }
            string player = rest.Count == 3 ? rest[0] : "mpv";
            string spec = rest.Count == 3 ? rest[1] : rest[0];
            var report = new List<string>();
            int exitCode = 0;
            Target planTarget = Parse(spec, "bili");
            if (planTarget == null)
            {
                report.Add("PARSE-FAILED");
                exitCode = 1;
            }
            else
            {
                try
                {
                    // 默认纯计划（零副作用）；--relay 时连提示文件一起写，并真起中转把端口回填
                    LaunchPlan plan = ResolvePlan(planTarget, player, true, withRelay);
                    if (withRelay && plan.NeedsRelay)
                    {
                        int relayPort = StartRelay(planTarget);
                        plan.Arguments = plan.Arguments.Replace(RelayPortToken, relayPort.ToString());
                        report.Add("RELAY=port=" + relayPort);
                    }
                    else
                    {
                        report.Add("RELAY=" + (plan.NeedsRelay ? "not-started（纯计划；要真起中转请加 --relay）" : "none"));
                    }
                    report.Add("PLAYER=" + player);
                    report.Add("FILE=" + plan.FileName);
                    report.Add("ARGS=" + plan.Arguments);
                    string anchorPath = Path.Combine(RuntimeDir, planTarget.Platform + "-" + planTarget.Room + ".anchor.txt");
                    report.Add("ANCHOR=" + (File.Exists(anchorPath) ? File.ReadAllText(anchorPath, Encoding.UTF8) : "(none)"));
                    report.Add("ANCHOR-ERROR=" + (LastAnchorError ?? "(none)"));
                }
                catch (Exception ex)
                {
                    report.Add("ERROR=" + ex.Message);
                    exitCode = 1;
                }
            }
            File.WriteAllLines(rest[rest.Count - 1], report, new UTF8Encoding(false));
            Environment.Exit(exitCode);
        }

        // 调试用：--play [mpv|potplayer] 平台:房间 输出文件 —— 走与 GUI **完全相同**的
        // ResolvePlan + Launch（含中转端口握手），把计划写进文件并真的拉起播放器，不弹界面。
        // 这是走本地中转那三端唯一可信的端到端自测路径（--plan 默认只算不启动）。
        // 单独跑一次抖音人机验证（开 Edge 窗口 → 用户过验证 → 收 cookie），便于验证和排障
    // 单独跑一次扫码登录（开浏览器窗口 → 用户扫码 → 收 cookie）
    if (args.Length >= 2 && args[0] == "--login")
    {
        bool okLogin = PlatformLogin.Show(null, args[1]);
        DanmakuHost.Log("--login " + args[1] + " -> " + okLogin);
        Environment.Exit(okLogin ? 0 : 1);
        return;
    }

    if (args.Length >= 2 && args[0] == "--douyin-verify")
    {
        bool ok = DouyinGuard.Verify(null, args[1]);
        Console.WriteLine(ok ? "VERIFY-OK cookies=" + DouyinGuard.LoadCookies().Length
                             : "VERIFY-FAILED");
        Environment.Exit(ok ? 0 : 1);
        return;
    }

    if (args.Length >= 3 && args[0] == "--play")
        {
            string playPlayer = args.Length == 4 ? args[1] : "mpv";
            string playSpec = args.Length == 4 ? args[2] : args[1];
            var playReport = new List<string>();
            int playExit = 0;
            Target playTarget = Parse(playSpec, "bili");
            if (playTarget == null)
            {
                playReport.Add("PARSE-FAILED");
                playExit = 1;
            }
            else
            {
                try
                {
                    LaunchPlan playPlan = ResolvePlan(playTarget, playPlayer, true, true);
                    ProcessStartInfo playStart = Launch(playPlan);
                    playReport.Add("PLAYER=" + playPlayer);
                    playReport.Add("FILE=" + playStart.FileName);
                    playReport.Add("ARGS=" + playStart.Arguments);
                    playReport.Add("RELAY=" + (playPlan.NeedsRelay ? "started" : "none"));
                    Process playerProcess = Process.Start(playStart);
                    playReport.Add("PID=" + (playerProcess == null ? 0 : playerProcess.Id));
                    if (playerProcess != null) StartRoomWatcher(playTarget, playerProcess);
                    if (playerProcess != null) StartDanmakuHost(playTarget, playerProcess);
                }
                catch (Exception ex)
                {
                    playReport.Add("ERROR=" + ex.GetType().Name + " " + ex.Message);
                    playExit = 1;
                }
            }
            File.WriteAllLines(args[args.Length - 1], playReport, new UTF8Encoding(false));
            Environment.Exit(playExit);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ShowDialog();
    }

    // 虎牙直链：按 streamlink 插件的算法自己算签名。
    // 为什么必须自己算：yt-dlp 给的直链签名 CDN 只喂 ~1.7MB 就掐断（表现为"看几秒就停"），
    // 而 streamlink 用页面里的 sFlvAntiCode + 自己算的 wsSecret 就稳定。便携包不能依赖 Python，
    // 所以把算法搬到这里（实测与 streamlink 产出的 URL 同构）。
}
