// --------------------------------------------------------------------------------------------
// 播放计划与启动（partial 的另一半）：ResolvePlan(纯) → Launch(副作用) → BuildStart(兼容入口)。
//
// 2026-09-27 从 live-hub-v2.cs 搬出来（评估报告 §7）。放一起的理由：这三层是**一个整体**，
// 中间的边界（哪里不许有副作用）本身就是被回归测试盯着的东西（TestPlanIsPure）。
// 同一块还包括：mpv/PotPlayer 的命令行拼装、FLV 元数据探测（PotPlayer 标题里的画质/帧率/码率）。
// 代码原样搬运，只补 using。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static partial class LiveHub
{

    // ---------------- 解析（纯）与启动（副作用）分离 ----------------
    //
    // 2026-09-20 架构调整。以前 BuildStart() 一个函数同时干三件事：解析地址、拉起中转进程、
    // 拼播放器命令行。后果：
    //   ① --plan 这条「只打印计划」的诊断路径其实会真的起一个没人管的中转——tests\README 第 6 条
    //      记的「偶发不再接收连接、根因未定位」就是这个结构性问题的症状（副作用和待验证的计划
    //      粘在一个函数里，没法单独验证）；
    //   ② 端口是 PickFreePort() 先占后放分配的（探测→释放→子进程再绑，中间谁都能抢走）；
    //   ③ 起播前要 Thread.Sleep(700) 等中转，因为「中转好了」这件事只能靠猜。
    //
    // 现在分三层：
    //   ResolvePlan() 纯：算地址、取标题、拼参数（allowSideEffects=false 连提示文件都不写）；
    //   Launch()      唯一的副作用点：起中转（端口 0 由中转自己绑、READY 回报）→ 回填端口 → 拉播放器；
    //   BuildStart()  兼容入口 = ResolvePlan + Launch（--self-test 只取计划）。
    // 于是 --plan 变成零副作用、可当回归；--play 走与 GUI 完全相同的两条调用。
    private const string RelayPortToken = "{relay-port}";

    // mpv 的窗口开关用 `immediate`，不是 `yes`：
    // `yes` 要等到**真的开始播放**才建窗口 —— 上游取不到流时（下播/重播/CDN 403）进程活着
    // 但屏幕上什么都没有，用户看到的就是「点开了却没反应」。
    // immediate 会在等待媒体数据时也创建窗口，让起播状态可见。
    private const string MpvWindowArgs = "--force-window=immediate --keep-open=yes --load-scripts=no";

    private sealed class LaunchPlan
    {
        public Target Target;
        public string Player;
        public string FileName;
        public string Arguments;
        public bool NeedsRelay;
    }

    private static LaunchPlan ResolvePlan(Target target, string player, bool allowNetwork, bool allowSideEffects)
    {
        // 会话号在这里兜底生成：中转 URL 拿它当令牌，状态文件也按它命名，必须早于任何拼串
        if (target.SessionId == null) target.SessionId = NewSessionId();
        return player == "potplayer"
            ? ResolvePotPlayerPlan(target, allowNetwork, allowSideEffects)
            : ResolveMpvPlan(target, allowNetwork, allowSideEffects);
    }

    // 唯一做副作用的地方：起中转 → 等 READY 拿到实际端口 → 回填 → 返回播放器启动信息
    private static ProcessStartInfo Launch(LaunchPlan plan)
    {
        string arguments = plan.Arguments;
        if (plan.NeedsRelay)
        {
            int port = StartRelay(plan.Target);
            arguments = arguments.Replace(RelayPortToken, port.ToString());
        }
        if (!File.Exists(plan.FileName) || !LooksLikeExecutable(plan.FileName))
        {
            // 落在包内 bin\ 下的默认路径 = 「这个包没带这个播放器」（lite 包不带 mpv；PotPlayer 一律不带）；
            // 其它情况 = 用户自己指过路径但文件没了，两种说法的下一步不一样。
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            bool bundledDefault = !string.IsNullOrEmpty(exeDir)
                && plan.FileName.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase);
            bool exists = File.Exists(plan.FileName);
            throw new FileNotFoundException(
                !exists
                    ? (bundledDefault
                        ? "还没设置播放器：在主界面「播放器路径」里点「选择程序…」，指到你自己的 mpv.exe（或 PotPlayerMini64.exe）"
                        : ("找不到播放器：" + plan.FileName))
                    : ("这个播放器文件不是有效的程序（可能是占位或已损坏）：" + plan.FileName
                        + "\r\n请点主界面「播放器路径 → 选择程序…」重新指定一个能用的播放器。"),
                plan.FileName);
        }
        ProcessStartInfo start = new ProcessStartInfo(plan.FileName, arguments);
        start.UseShellExecute = true;
        start.WorkingDirectory = Path.GetDirectoryName(plan.FileName);
        return start;
    }

    private static ProcessStartInfo BuildStart(Target target, string player)
    {
        if (SelfTestMode)   // 自检只校验计划：不联网、不起中转、不写提示文件
        {
            // 走 ResolvePlan（而不是直接调 Resolve*Plan）：会话号兜底在里面，
            // 否则自检打印出来的中转 URL 会是空令牌 live.flv?t=（2026-09-20 独立验证挑出来的）
            LaunchPlan offline = ResolvePlan(target, player, false, false);
            return new ProcessStartInfo(offline.FileName, offline.Arguments);
        }
        return Launch(ResolvePlan(target, player, true, true));
    }

    /// <summary>能不能当程序启动：至少 1KB 且是 PE（MZ）。0 字节占位文件会让 Process.Start 抛 Win32 193。</summary>
    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            if (new FileInfo(path).Length < 1024) return false;
            using (FileStream stream = File.OpenRead(path))
            {
                int b0 = stream.ReadByte(), b1 = stream.ReadByte();
                return b0 == 'M' && b1 == 'Z';
            }
        }
        catch (Exception) { return false; }
    }

    private static string NewSessionId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeQuality(string quality)
    {
        return quality == "high" || quality == "smooth" ? quality : "best";
    }

    private static string MpvBufferArgs(Target target)
    {
        // ── 播放稳定性（2026-09-25 实测后补）────────────────────────────────────
        //
        // ① --hwdec=auto-safe：优先使用安全的硬件解码组合，减轻高码率直播的软件解码负担。
        //    解码进入临界状态时可能只是**偶尔迟到**而不是丢帧，所以 mpv 的
        //    frame-drop-count / decoder-frame-drop-count 都还是 0，症状却是
        //    「每隔几秒顿一下」，从计数器上完全看不出来。
        //    用 auto-safe 而不是 auto：只启用驱动已知没问题的组合，不安全就自动退回软解。
        //
        // ② cache 参数按稳定性档位提供缓冲余量。
        //    （注意 --cache-secs 是**上限**不是目标 —— 上游按 1.0x 实时投递时，
        //      缓存就停在这个平衡点，调大这个值并不会让它更满。）
        // ③ --video-sync=display-resample（2026-10-17 补）：默认 audio 模式下，
        //    mpv 的播放循环按音频时钟投递视频帧、并不知道 vsync 何时发生；
        //    60fps 内容落在 120Hz 屏上时相位会持续漂移，某一帧偶尔晚一个
        //    vsync 上屏（观感「卡一下」），而 frame-drop-count 仍是 0 ——
        //    之前用计数器完全看不出这种卡顿（见 2026-10-16 排查记录）。
        //    display-resample 把视频时钟锁到显示器并重采样音频（±0.125%），
        //    整数倍刷新率下不丢/不重复帧；vo 取不到 vsync 时 mpv 会自动回退。
        string common = " --hwdec=auto-safe --video-sync=display-resample";
        // 画质增强：面板开关存在 danmaku.json 里（启动器已有的持久配置，不再另造一个设置文件）。
        // 读失败就按关处理 —— 画质设置读不到不该让播放起不来。
        try
        {
            string quality = DanmakuHost.LoadOrCreateConfig().MpvQuality;
            if (quality == "light")
                common += " --scale-antiring=0.7";                       // GPU 平均几乎不涨
            else if (quality == "high")
                common += " --vo=gpu-next,gpu --profile=high-quality";   // GPU 翻倍
        }
        catch (Exception ex) { Diagnostics.Report("读取画质增强开关", ex); }
        return common + (target.BufferMode == "low"
            ? " --cache=yes --cache-secs=3 --demuxer-readahead-secs=2"
            : " --cache=yes --cache-secs=15 --demuxer-readahead-secs=10");
    }

    // B站 直播流的 format_id 前缀就是画质档位（实测 7777 的 JSON：quality 字段
    // ultra_high_res=2 < blue_ray=3 < source=4），所以**按前缀挑画质**。
    //
    // ⚠️ 别把这里的映射「修」成看起来更对的样子 —— 2026-09-25 查过 B站 官方 API
    //    （getRoomPlayInfo?qn=150），同一个房间会给出两套互相矛盾的答案：
    //
    //      g_qn_desc 声称支持：杜比30000 / 4K20000 / 2K15000 / 原画10000 /
    //                          蓝光400 / 超清250 / 高清150 / 流畅80
    //      实际返回的流 accept_qn：**只有 10000, 400, 250**（原画/蓝光/超清）
    //      而且 current_qn=250 —— 请求 150（高清）会被**回落到 250**。
    //
    //    也就是说：房间「声称」有 流畅/高清，但**根本不发这两档**。
    //    真能拿到的最低档就是 超清(250)。
    //    所以「流畅 → ultra_high_res(超清)」不是选错了，是**取最低可用的那一档**。
    //    名字对不上是真的（超清 ≠ 流畅），但那是 B站 的档位命名，不是我们的 bug。
    private static readonly string[] BiliTiersSmooth = { "ultra_high_res", "blue_ray", "source" };
    private static readonly string[] BiliTiersHigh = { "blue_ray", "source", "ultra_high_res" };
    private static readonly string[] BiliTiersSource = { "source", "blue_ray", "ultra_high_res" };

    /// B站 的 yt-dlp 格式选择串。
    ///
    /// 2026-09-25 实测重写，修两个 bug：
    ///
    /// ① **卡顿的根因**：原来 mpv 这条路用 `best[format_id^=source]/...`，会挑中
    ///    `source-7` = `m3u8_native`+`fmp4`+**hevc**。m3u8 是**分片**投递，片与片之间
    ///    有天然空档 —— 实测 60 秒里 mpv 的缓冲最低值掉到 **0 秒**、丢帧 2 个，
    ///    症状就是「每隔一会儿顿一下」。而同一个房间的 `source-5` 是
    ///    `https`+`flv`+`avc`（连续字节流 + H.264）。
    ///    讽刺的是「优先 flv」这条规则**早就写着**（见 BiliFlvFormat 的注释），
    ///    但只给了 PotPlayer 那条路，mpv 这条路漏了。
    /// ② **画质档位整个是坏的**：原来流畅/高清用 `[height<=720]`、`[height<=1080]`，
    ///    而 B站 直播流的 height 全是 `NA` → 一个都匹配不上 →
    ///    `Requested format is not available`，**选这两档直接播不了**。
    ///
    /// 所以现在的策略是「连续投递 + H.264 优先，画质按 format_id 前缀挑」，
    /// 末尾一律留 /best 兜底：万一某天 B站 只提供 HLS，也得能播。
    private static string BiliFormat(Target target, bool flvOnly)
    {
        // flvOnly 这个参数已经没意义了 —— 两条路现在都要「连续投递 + H.264」。
        // 保留签名只是不想动调用点。
        string quality = NormalizeQuality(target.Quality);
        string[] tiers = quality == "smooth" ? BiliTiersSmooth
            : quality == "high" ? BiliTiersHigh : BiliTiersSource;

        var chain = new List<string>();
        foreach (string tier in tiers)
        {
            chain.Add("best[format_id^=" + tier + "][ext=flv][vcodec^=avc]");  // 最理想
            chain.Add("best[format_id^=" + tier + "][ext=flv]");               // 连续投递，编码不限
            chain.Add("best[format_id^=" + tier + "]");                        // 该档位任意形态
        }
        chain.Add("best[ext=flv][vcodec^=avc]");
        chain.Add("best[ext=flv]");
        chain.Add("best");
        return string.Join("/", chain.ToArray());
    }

    // mpv 的**纯计划**：只做网络解析与拼参数，不起任何进程。
    // 端口不在这里分配——命令行里放 {relay-port} 占位符，由 Launch 阶段在中转真的绑定端口后回填。
    private static LaunchPlan ResolveMpvPlan(Target target, bool allowNetwork, bool allowSideEffects)
    {
        // lite 绿色包本来就不带播放器：**自检只验证计划**，这时不抛（SelfTest 会打 MPV-BUNDLED=False）；
        // 真正启动（Launch）时才因为文件不存在给出「先选播放器」的提示。
        if (!File.Exists(Mpv) && !(PortableLite && SelfTestMode)) throw new FileNotFoundException("找不到 mpv", Mpv);
        LaunchPlan plan = new LaunchPlan();
        plan.Target = target;
        plan.Player = "mpv";
        plan.FileName = Mpv;
        // 「要不要走本地中转」只由适配器表说了算（2026-09-27，评估报告 §7）：
        // 原来 four 个分支里各写一遍 plan.NeedsRelay = true，加平台时漏一处就是静默直链超时。
        IPlatformAdapter adapter = Platforms.Get(target.Platform);
        plan.NeedsRelay = adapter != null && adapter.NeedsRelay;
        string mediaTitle = null;
        if (target.Platform == "bili")
        {
            // B站：交给 mpv 的 ytdl_hook 调 yt-dlp（便携包里 yt-dlp 在旁边，用 script-opts 指给它）
            string anchor = allowNetwork ? FetchBiliAnchor(target.Room) : null;
            string roomTitle = allowNetwork ? FetchBiliRoomTitle(target.Room) : null;
            string baseTitle = JoinTitle(anchor, roomTitle, "B站直播 " + target.Room);
            mediaTitle = CreatePlaybackTitleInfo(baseTitle, roomTitle).RoomTitle;
            string baseFile = allowSideEffects ? WriteBaseHint(target, baseTitle) : null;
            string args = MpvWindowArgs + MpvBufferArgs(target)
                + " " + QuoteWindowsArg("--script=" + LiveTitleScript.Replace('\\', '/'));
            if (File.Exists(LiveResyncScript)) args += " " + QuoteWindowsArg("--script=" + LiveResyncScript.Replace('\\', '/'));
            args += " " + QuoteWindowsArg("--script-opts=" + YtdlHookOpts()
                + ",live_title_state_file=" + MpvListValue(SessionRoomStatePath(target))
                + (baseFile != null ? ",live_title_base_file=" + MpvListValue(baseFile) : ""));
            args += " --title=" + QuoteWindowsArg(SafeTitle(baseTitle))
                + " --stream-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=30"
                + " --ytdl-format=\"" + BiliFormat(target, false) + "\"";
            if (File.Exists(Cookie)) args += " " + QuoteWindowsArg("--ytdl-raw-options=cookies=" + MpvListValue(Cookie));
            args += " \"" + target.Url + "\"";
            plan.Arguments = args;
        }
        else if (target.Platform == "huya")
        {
            // 虎牙：同样交给 mpv+yt-dlp 取流；主播/房间名由启动器从虎牙页面拿，写进基础标题文件
            PlaybackTitleInfo titles = allowNetwork ? FetchHuyaPlaybackTitle(target.Room) : CreatePlaybackTitleInfo("虎牙直播 " + target.Room, null);
            string baseTitle = titles.FullTitle;
            mediaTitle = titles.RoomTitle;
            string baseFile = allowSideEffects ? WriteBaseHint(target, baseTitle) : null;
            // 走本地中转：虎牙 CDN 也会定期掐连接，中转负责重连，播放器无感。
            // 虎牙流自带 demux-bitrate，标题不需要预先再解析一次上游；避免重播无流时重复长兜底。
            string args = MpvWindowArgs + " --ytdl=no" + MpvBufferArgs(target)
                + " " + QuoteWindowsArg("--script=" + LiveTitleScript.Replace('\\', '/'));
            if (File.Exists(LiveResyncScript)) args += " " + QuoteWindowsArg("--script=" + LiveResyncScript.Replace('\\', '/'));
            // 注意：别给 mpv 加 --network-timeout=3600，超出允许范围会让 mpv 直接退出（踩过）
            string opts = "live_title_bitrate_file=" + MpvListValue(BitrateHintPath(target))
                + ",live_title_state_file=" + MpvListValue(SessionRoomStatePath(target))
                + ",live_title_relay_file=" + MpvListValue(SessionRelayStatePath(target))
                + ",live_title_autoreplay=yes";
            if (baseFile != null) opts += ",live_title_base_file=" + MpvListValue(baseFile);
            args += " " + QuoteWindowsArg("--script-opts=" + opts);
            args += " --stream-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=30";
            args += " \"" + RelayUrl(target, RelayPortToken) + "\"";
            plan.Arguments = args;
        }
        else if (target.Platform == "douyin")
        {
            // 抖音：网页端不签名取流（见「抖音」一节）。CDN 给的是 chunked + keep-alive、没有长度，
            // 和虎牙/斗鱼一样走本地中转，顺带得到断流重连。
            string baseTitle = allowNetwork ? null : "抖音直播 " + target.Room;
            if (allowSideEffects)
            {
                string douyinTitle;
                PrepareDouyinWithMediaTitle(target, out douyinTitle, out mediaTitle);   // 返回的直链交给中转自己按需重解析
                baseTitle = douyinTitle;
            }
            string baseFile = allowSideEffects ? WriteBaseHint(target, baseTitle) : null;
            string args = MpvWindowArgs + MpvBufferArgs(target)
                + " " + QuoteWindowsArg("--script=" + LiveTitleScript.Replace('\\', '/'));
            if (File.Exists(LiveResyncScript)) args += " " + QuoteWindowsArg("--script=" + LiveResyncScript.Replace('\\', '/'));
            args += " --ytdl=no";
            string opts = "live_title_bitrate_file=" + MpvListValue(BitrateHintPath(target))
                + ",live_title_state_file=" + MpvListValue(SessionRoomStatePath(target))
                + ",live_title_relay_file=" + MpvListValue(SessionRelayStatePath(target))
                + ",live_title_autoreplay=yes";
            if (baseFile != null) opts += ",live_title_base_file=" + MpvListValue(baseFile);
            args += " " + QuoteWindowsArg("--script-opts=" + opts);
            args += " --stream-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=30";
            args += " \"" + RelayUrl(target, RelayPortToken) + "\"";
            plan.Arguments = args;
        }
        else
        {
            // 斗鱼：yt-dlp 解析不了，走启动器自带的本地中转（无边界响应 + 每条连接重新解析）
            PlaybackTitleInfo titles = allowNetwork ? FetchDouyuPlaybackTitle(target.Room) : CreatePlaybackTitleInfo("斗鱼直播 " + target.Room, null);
            string baseTitle = titles.FullTitle;
            mediaTitle = titles.RoomTitle;
            string baseFile = allowSideEffects ? WriteBaseHint(target, baseTitle) : null;
            if (allowSideEffects) WriteBitrateHint(target);
            string args = MpvWindowArgs + MpvBufferArgs(target)
                + " " + QuoteWindowsArg("--script=" + LiveTitleScript.Replace('\\', '/'));
            if (File.Exists(LiveResyncScript)) args += " " + QuoteWindowsArg("--script=" + LiveResyncScript.Replace('\\', '/'));
            args += " --ytdl=no";
            string opts = "live_title_bitrate_file=" + MpvListValue(BitrateHintPath(target))
                + ",live_title_state_file=" + MpvListValue(SessionRoomStatePath(target))
                + ",live_title_relay_file=" + MpvListValue(SessionRelayStatePath(target))
                + ",live_title_autoreplay=yes";
            if (baseFile != null) opts += ",live_title_base_file=" + MpvListValue(baseFile);
            args += " " + QuoteWindowsArg("--script-opts=" + opts);
            args += " --stream-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=30";
            args += " \"" + RelayUrl(target, RelayPortToken) + "\"";
            plan.Arguments = args;
        }
        AddMpvMediaTitle(plan, mediaTitle, allowSideEffects);
        return plan;
    }

    // PotPlayer 路径：先把直播直链解析出来，再交给 PotPlayer 打开。
    // 不用 Streamlink 的本地 HTTP 中转：它给播放器的响应没有长度信息，PotPlayer 会一直停在「即将播放」。
    private static LaunchPlan ResolvePotPlayerPlan(Target target, bool allowNetwork, bool allowSideEffects)
    {
        // PotPlayer **不随包分发**（专有软件，EULA 不允许再分发），所以「没装」是常态：
        // 规划阶段不拦，真正点「用 PotPlayer 打开」时由 Launch 给出「请先指定路径」的提示。
        LaunchPlan plan = new LaunchPlan();
        plan.Target = target;
        plan.Player = "potplayer";
        plan.FileName = PotPlayer;
        // 同 ResolveMpvPlan：中转与否看适配器表，别在每个分支里各写一遍。
        IPlatformAdapter potAdapter = Platforms.Get(target.Platform);
        plan.NeedsRelay = potAdapter != null && potAdapter.NeedsRelay;
        if (!allowNetwork)   // 自检：不联网、不起中转、不写提示文件
        {
            plan.Arguments = "/new \"https://example.invalid/live.flv\" /title=\"self-test\"";
            return plan;
        }
        string title;
        string probeUnused;
        string url = BuildPotPlayerTitle(target, plan, allowSideEffects, out title, out probeUnused);
        if (url == null || url.Length == 0)
            throw new InvalidOperationException("没解析出 " + PlatformName(target.Platform) + " 的直播地址");
        plan.Arguments = "/new " + QuoteWindowsArg(url) + " /title=" + QuoteWindowsArg(SafeTitle(title));
        return plan;
    }

    // PotPlayer 没有 mpv 那种脚本能力，所以标题由启动器拼：主播/标题（接口或 Streamlink 元数据）
    // + 画质（直接读流开头的 FLV onMetaData：宽高、帧率、编码、码率）。
    // 走本地中转的三端在这里只**标记** plan.NeedsRelay 并把端口留成占位符，真正起中转在 Launch。
    private static string BuildPotPlayerTitle(Target target, LaunchPlan plan, bool allowSideEffects, out string title, out string probeUrl)
    {
        string url;
        string baseTitle;
        string douyinProbe = null;   // 抖音：本次解析出的上游地址，直接拿它当画质探针，省一次抓页面
        if (target.Platform == "bili")
        {
            if (!File.Exists(YtDlp)) throw new FileNotFoundException("找不到 yt-dlp", YtDlp);
            string args = "-g --no-warnings -f \"" + BiliFormat(target, true) + "\"";
            if (File.Exists(Cookie)) args += " --cookies \"" + Cookie + "\"";
            args += " \"" + target.Url + "\"";
            url = FirstUrl(RunCapture(YtDlp, args, 40000));
            string anchor = FetchBiliAnchor(target.Room);
            if (allowSideEffects) WriteBaseHint(target, anchor);
            string roomTitle = FetchBiliRoomTitle(target.Room);
            if (anchor != null && roomTitle != null) baseTitle = anchor + " - " + roomTitle;
            else if (anchor != null) baseTitle = anchor;
            else if (roomTitle != null) baseTitle = roomTitle;
            else baseTitle = "B站直播 " + target.Room;
        }
        else if (target.Platform == "douyu")
        {
            url = RelayUrl(target, RelayPortToken);
            baseTitle = FetchDouyuTitle(target.Room);
        }
        else if (target.Platform == "douyin")
        {
            string douyinTitle;
            douyinProbe = PrepareDouyin(target, out douyinTitle);
            baseTitle = douyinTitle;
            url = RelayUrl(target, RelayPortToken);
        }
        else
        {
            // 虎牙：也走本地中转（CDN 会定期掐连接，中转负责重连），主播名自己取
            url = RelayUrl(target, RelayPortToken);
            baseTitle = FetchHuyaTitle(target.Room);
        }
        // 画质从「上游真实地址」读：斗鱼走中转时，本地地址是启动器自己的服务，元数据一样（原样转发），
        // 但为了不和播放抢连接，这里单独解析一个上游地址来探测。
        int douyuKbps = 0;
        string streamProbe = target.Platform == "douyu" ? ResolveDouyuUrl(target.Room, target.Quality, out douyuKbps)
            : target.Platform == "huya" ? ResolveHuyaUrl(target)
            : target.Platform == "douyin" ? douyinProbe : url;
        StreamInfo info = streamProbe == null ? null : ReadFlvInfo(streamProbe);
        // 斗鱼 onMetaData 不带 videodatarate：用接口 multirates 里的码率补上（免费）
        if (info != null && info.Kbps <= 0 && douyuKbps > 0) info.Kbps = douyuKbps;
        title = ComposeTitle(baseTitle, info);
        probeUrl = streamProbe;   // 出参：--streaminfo 拿它做画质探针（不再对着本地中转地址探）
        return url;
    }

    private sealed class StreamInfo
    {
        public int Width;
        public int Height;
        public double Fps;
        public string Codec;
        public int Kbps;
    }



    // 拼成和 mpv 那边一致的格式：<主播 - 标题>  ·  <宽x高> <fps>fps <编码> <码率>kbps
    // 「先吐一段内存里已有的字节、再接着读底层流」的小包装。
    // 用途：ReadFlvInfo 已经把开头 8KB 读进 buffer 了（流的读位置已过界），要接着量后续 tag
    // 就必须先把 buffer 里剩下的那段补回去，否则会从某个 tag 中间开始读。
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] prefix;
        private readonly int end;
        private int at;
        private readonly Stream inner;

        public PrefixedStream(byte[] prefix, int start, int count, Stream inner)
        {
            this.prefix = prefix;
            this.at = start;
            this.end = Math.Min(prefix == null ? 0 : prefix.Length, start + Math.Max(0, count));
            this.inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int fromPrefix = 0;
            if (at < end)
            {
                fromPrefix = Math.Min(count, end - at);
                Buffer.BlockCopy(prefix, at, buffer, offset, fromPrefix);
                at += fromPrefix;
                if (fromPrefix == count) return fromPrefix;
            }
            int fromInner = inner.Read(buffer, offset + fromPrefix, count - fromPrefix);
            return fromInner <= 0 ? fromPrefix : fromPrefix + fromInner;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    // 深度测量：onMetaData 里没有 framerate / videodatarate 的流（斗鱼就是），
    // 按真实 FLV tag 头量一小段：fps = 视频帧数 / 时间跨度，kbps = 视频字节×8 / 跨度。
    // ⚠️ 它是**按实时速率**读流的（直播流给多快就读多快），3 秒窗口就要 3 秒 —— 只给 --streaminfo 用，
    // 绝不要放进起播路径。
    private static bool MeasureFlvWindow(Stream input, int maxBytes, int maxSeconds, out double fps, out int kbps)
    {
        fps = 0;
        kbps = 0;
        long videoBytes = 0;
        int frames = 0;
        long firstTimestamp = -1;
        long lastTimestamp = 0;
        int budget = maxBytes;
        DateTime started = DateTime.UtcNow;
        while (budget > 0 && (DateTime.UtcNow - started).TotalSeconds < maxSeconds)
        {
            byte[] tag = ReadCompleteFlvTag(input, null);
            if (tag == null) break;
            budget -= tag.Length;
            if ((tag[0] & 0x1f) != 9) continue;   // 只统计视频 tag
            long timestamp = ((long)tag[7] << 24) | ((long)tag[4] << 16) | ((long)tag[5] << 8) | tag[6];
            if (firstTimestamp < 0) firstTimestamp = timestamp;
            lastTimestamp = timestamp;
            frames++;
            videoBytes += tag.Length;
        }
        double seconds = (lastTimestamp - firstTimestamp) / 1000.0;
        if (frames < 2 || seconds <= 0.2) return false;
        fps = (frames - 1) / seconds;
        kbps = (int)Math.Round(videoBytes * 8.0 / 1000.0 / seconds);
        return fps > 0 && kbps > 0;
    }

    private static string ComposeTitle(string baseTitle, StreamInfo info)
    {
        if (info == null) return baseTitle;
        var parts = new List<string>();
        if (info.Width > 0 && info.Height > 0) parts.Add(info.Width + "x" + info.Height);
        if (info.Fps > 0) parts.Add(Math.Round(info.Fps) + "fps");
        if (info.Codec != null) parts.Add(info.Codec);
        if (info.Kbps > 0) parts.Add(info.Kbps + "kbps");
        if (parts.Count == 0) return baseTitle;
        return baseTitle + "  ·  " + string.Join(" ", parts.ToArray());
    }

    // 读流开头的一小段，解析 FLV 的 onMetaData。
    // deep=true 时，若 onMetaData 缺 framerate/videodatarate，会再按实时速率量 3 秒流补齐
    // （斗鱼就是这种情况；--streaminfo 用 deep，起播路径不要用）。
    private static StreamInfo ReadFlvInfo(string url, bool deep)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                byte[] buffer = new byte[8192];
                int total = 0, read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    total += read;
                if (total < 30) return null;
                if (buffer[0] != 'F' || buffer[1] != 'L' || buffer[2] != 'V') return null;
                int tag = 13;   // 9 字节 FLV 头 + 4 字节 PreviousTagSize
                if (tag + 11 > total || buffer[tag] != 18) return null;   // 18 = script data(onMetaData)
                int size = (buffer[tag + 1] << 16) | (buffer[tag + 2] << 8) | buffer[tag + 3];
                int p = tag + 11;
                int end = p + size;
                if (end > total) return null;
                if (p + 3 > end || buffer[p] != 0x02) return null;        // AMF0 string
                int nameLen = (buffer[p + 1] << 8) | buffer[p + 2];
                p += 3 + nameLen;
                if (buffer[p] == 0x08) p += 5;          // ECMA array：marker + 4 字节元素个数
                else if (buffer[p] == 0x03) p += 1;     // 匿名 object：没有计数字段
                else return null;
                var info = new StreamInfo();
                // AMF0 的 ECMA 数组/object 里，键是「u16 长度 + UTF-8 字节」，前面没有 0x02 类型标记；
                // 结束标记是 00 00 09。
                while (p + 2 <= end)
                {
                    if (p + 3 <= end && buffer[p] == 0x00 && buffer[p + 1] == 0x00 && buffer[p + 2] == 0x09) break;
                    int keyLen = (buffer[p] << 8) | buffer[p + 1];
                    if (keyLen <= 0 || p + 2 + keyLen > end) break;
                    string key = Encoding.UTF8.GetString(buffer, p + 2, keyLen);
                    p += 2 + keyLen;
                    if (p >= end) break;
                    byte marker = buffer[p++];
                    if (marker == 0x00 && p + 8 <= end)
                    {
                        byte[] raw = new byte[8];
                        Array.Copy(buffer, p, raw, 0, 8);
                        if (BitConverter.IsLittleEndian) Array.Reverse(raw);
                        double value = BitConverter.ToDouble(raw, 0);
                        p += 8;
                        if (key == "width") info.Width = (int)Math.Round(value);
                        else if (key == "height") info.Height = (int)Math.Round(value);
                        else if (key == "framerate") info.Fps = value;
                        else if (key == "videodatarate") info.Kbps = (int)Math.Round(value);
                        else if (key == "videocodecid") info.Codec = CodecName((int)Math.Round(value));
                        else if (key == "codecname" && info.Codec == null) info.Codec = CodecFromName(null);
                    }
                    else if (marker == 0x01 && p + 1 <= end) p += 1;
                    else if (marker == 0x02 && p + 2 <= end)   // 字符串：u16 长度 + 内容（marker 已经吃掉）
                    {
                        int valueLen = (buffer[p] << 8) | buffer[p + 1];
                        if (key == "codecname" && info.Codec == null && valueLen > 0 && valueLen <= 16)
                            info.Codec = CodecFromName(Encoding.UTF8.GetString(buffer, p + 2, valueLen));
                        p += 2 + valueLen;
                    }
                    else if (marker == 0x0c && p + 4 <= end)   // 长字符串：u32 长度 + 内容
                    {
                        int valueLen = (buffer[p] << 24) | (buffer[p + 1] << 16) | (buffer[p + 2] << 8) | buffer[p + 3];
                        if (valueLen < 0 || p + 4 + valueLen > end) break;
                        p += 4 + valueLen;
                    }
                    else if (marker == 0x05 || marker == 0x06) { }        // null / undefined
                    else if (marker == 0x0b && p + 10 <= end) p += 10;    // Date
                    else break;
                }
                if (deep && (info.Fps <= 0 || info.Kbps <= 0))
                {
                    // 下一个 tag 的起点 = onMetaData 负载末尾 + 4 字节 PreviousTagSize；
                    // 把 buffer 里剩下的那段先补回去，再接着读网络流。
                    int nextTag = end + 4;
                    using (var window = new PrefixedStream(buffer, nextTag, total - nextTag, stream))
                    {
                        double fps;
                        int kbps;
                        if (MeasureFlvWindow(window, 4 * 1024 * 1024, 3, out fps, out kbps))
                        {
                            if (info.Fps <= 0) info.Fps = fps;
                            if (info.Kbps <= 0) info.Kbps = kbps;
                        }
                    }
                }
                return info;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static StreamInfo ReadFlvInfo(string url)
    {
        return ReadFlvInfo(url, false);
    }

    private static string UnusedHexHead(string url, int count)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                byte[] buffer = new byte[count];
                int total = 0, read;
                while (total < count && (read = stream.Read(buffer, total, count - total)) > 0) total += read;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < total; i++) sb.Append(buffer[i].ToString("x2")).Append(' ');
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            return "ERR " + ex.Message;
        }
    }

    private static string CodecFromName(string name)
    {
        if (name == null) return null;
        string lower = name.ToLowerInvariant();
        if (lower.Contains("avc") || lower.Contains("h264")) return "h264";
        if (lower.Contains("hev") || lower.Contains("h265")) return "hevc";
        return null;
    }

    private static string CodecName(int id)
    {
        if (id == 7) return "h264";
        if (id == 12) return "hevc";
        if (id == 2) return "h263";
        if (id == 4) return "vp6";
        return null;
    }
}
