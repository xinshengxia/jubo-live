// --------------------------------------------------------------------------------------------
// 2026-09-25：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// （在此之前这些模块是被生成器拼进 live-hub-v2.cs 的，靠宿主文件的 using 才编得过。）
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
// ============================================================================================
//  弹幕 / SC 模块（2026-09-22 新增）
//
//  协议实现与连接策略见下面的说明。
//
//  这条路是**实测**出来的，几个关键点别改错：
//   · 压缩用 protover=2（zlib）：net48 没有 BrotliStream；zlib 要跳 2 字节头再喂 DeflateStream。
//   · getDanmuInfo **强制 wbi 签名**（不签名稳定 code=-352）。
//   · 认证包必须带 buvid；带了 uid 就必须配有效登录态，否则会被服务端强制断开
//     → 所以先问一次 nav 的 data.isLogin，没登录就退回 uid=0。
//   · WS 必须按 EndOfMessage 重组分片，弹幕突发时一条消息会被切好几片。
//   · 匿名连接在部分房间会被脱敏（昵称变 *），所以默认带 Cookie。
//   · 全部代码必须兼容 **C# 5** 语法（legacy 构建路径的编译器要求）：
//     不许用字符串插值 $"..."、?.、nameof、内联 out var、元组、自动属性初始化器。
//     using 指令放在文件顶部（C# 要求 using 在类型声明之前）。
// ============================================================================================

// --------------------------------------------------------------------------------------------
// 配置：一份 JSON，托盘菜单与 GUI 设置对话框都能改；运行中的弹幕进程每秒看一次 mtime 热重载。
// --------------------------------------------------------------------------------------------
internal sealed partial class DanmakuConfig
{
    private static readonly FieldInfo[] Fields = typeof(DanmakuConfig).GetFields(BindingFlags.Instance | BindingFlags.Public);
    private DanmakuConfig _savedSnapshot;
    private string _savedPath;
    // —— 总开关 ——
    public bool Enabled = true;

    // —— 字体与尺寸 ——
    // 默认用随包的 HarmonyOS Sans SC（和界面同一套）。
    // Normalize() 会按 FontName 档位重新推导它，并处理「字体没随包带上」的兜底。
    public string FontFamily = "HarmonyOS Sans SC";
    // 字号档位（对齐 B站 播放器的「超小 / 小 / 标准 / 大 / 超大」）。
    // 100 = 标准 = 服务端下发的 25px。
    public int FontScalePercent = 100;
    // 字号是否随播放画面等比缩放。开：弹幕相对画面的大小恒定（窗口小字就小、全屏字就大），
    // 关：固定逻辑字号，跟随当前屏幕 DPI。默认开 —— 更接近「按画面比例看」的直觉。
    public bool ScaleWithWindow = true;
    public int ScaleReferenceHeight = 720;  // 以多高的画面为 100% 基准（25px 就是在这个高度下的观感）
    public bool SolidText = true;           // Preserve opaque glyph interiors; SC backgrounds retain opacity settings.
    public int OpacityPercent = 90;         // 实心字芯模式下控制 SC 背景；关闭该模式后控制整个覆盖层。
    public bool StrokeEnabled = true;       // 黑描边（B站 弹幕的标志性观感）
    public int StrokeWidth = 1;
    // 弹幕/SC 的字体档位。鸿蒙 = 随包的 HarmonyOS Sans SC（和界面同一套）。
    // Normalize() 会按这个档位推导真正的族名 FontFamily。
    public string FontName = "鸿蒙";

    // —— 运动 ——
    public int SpeedPercent = 100;          // 滚动速度；100 = 约 8 秒横穿一屏（贴近 B站）
    public int MaxOnScreen = 150;           // 同屏上限，空间不足时不插入新弹幕
    // 弹幕延迟补偿（毫秒）。弹幕是**实时到达**的，而视频是**缓冲之后**才显示的 ——
    // 实测各平台缓冲深度：B站 3.5s / 抖音 3.9s / 虎牙 2.6s / 斗鱼 2.6s。
    // 不补偿的话弹幕会**领先画面 2.5~4 秒**：先看到「哈哈哈他没了」，几秒后画面里才没。
    // 0 = 不补偿（默认，保持原行为）。托盘可调，快捷键 Ctrl+Alt+[ / ]。
    public int DanmakuDelayMs = 0;
    // 显示区域 —— 对齐 B站 播放器的「全屏 / 3/4 屏 / 半屏 / 1/4 屏」。
    // 注意这是**画面高度的百分比**，不是面积：半屏 = 弹幕只在上半部分飘。
    public int DisplayAreaPercent = 100;
    // 防挡字幕（B站 同名选项）：开启后底部 20% 不放弹幕，给字幕/UI 让位。
    public bool AntiBlockSubtitle = true;
    public int LineGapPercent = 100;        // 行距微调
    // 层叠方式：false = 贴在播放器正上方（播放器被别的窗口盖住时弹幕一起被盖住）；
    //           true  = HWND_TOPMOST，始终压在所有窗口之上（放副屏常驻时才需要）。
    public bool AlwaysOnTop = false;

    // —— 界面外观 ——
    // 深色模式。影响的是**启动器面板和弹幕设置对话框**，不影响弹幕覆盖层本身
    // （覆盖层压在视频上，底色必须透明，只有文字和 SC 卡片；它有自己的配色）。
    //
    // 存在这里而不是另造一个设置文件 —— 跟 mpv 画质档位同一个理由：
    // danmaku.json 已经是启动器和覆盖层共用的持久配置。
    public bool DarkMode = false;
    public string UiFontKey = "auto"; // auto / harmony / yahei-ui / yahei / system
    public int UiFontPercent = 100;   // 启动器/设置页字体大小；100 = 标准，重启启动器生效

    // —— 显示哪些内容 ——
    // mpv 画质增强开关（由启动器面板勾选，启动时读这里）。
    //
    // 开 = `--vo=gpu-next,gpu --profile=high-quality`：
    //   · gpu-next 基于 libplacebo —— 这是 **mpv 侧的 madVR 等价物**
    //     （madVR 是 DirectShow 专有滤镜，mpv 根本用不了；libplacebo 干的是同一件事）
    //   · high-quality 是 mpv 内置 profile，含 scale=ewa_lanczossharp + scale-antiring=0.6
    //     GPU 负担取决于设备、视频分辨率和画质设置。
    // 写成 `gpu-next,gpu` 是 mpv 的**回退列表**：老版本没有 gpu-next 就自动退回 gpu，不会黑屏。
    //
    // 关 = 什么都不加，用 mpv 默认（bilinear 系）。默认关，保持原行为。
    /// mpv 画质档位：off / light / high（由启动器面板下拉选择）。
    ///
    /// off 使用播放器默认参数；light 调整抗振铃；high 使用 high-quality profile。
    /// 抗振铃用于减轻画面缩放时的边缘白边，实际 GPU 开销与设备和素材有关。
    /// 默认视频输出驱动也随 mpv 构建与用户配置变化，运行时可查 current-vo。
    public string MpvQuality = "off";

    /// 旧字段（布尔开关），已废弃，只为兼容老配置保留：
    /// 以前开过「画质增强」的用户，Normalize 时自动迁移到 high。
    public bool MpvEnhance = false;

    public bool ShowMedal = false;          // 粉丝勋章（B站 网页默认不显示，所以默认关）
    public bool ShowUserName = false;       // 用户名前缀
    public bool ShowUserLevel = false;      // 用户等级
    public bool ShowGuard = false;          // 舰长/提督/总督标记
    public bool ShowEmoticon = true;        // 表情弹幕（B站 直播间大表情，服务端给图片 URL）
    public bool ShowColorEmoji = true;      // Unicode emoji 用彩色精灵图（关掉则退回系统字体画的单色字形）
    public bool ShowEnterMessage = false;   // 进房欢迎
    public bool ShowGift = false;           // 礼物提示
    public bool ShowNotice = false;         // 平台公告

    // —— SC（醒目留言）——
    public string ScMode = "full";          // full=完整卡片 / compact=单行折叠 / badge=只留计数角标 / off=关闭
    // **滚动阈值**：≤ 这个张数正常堆叠；**超过就启用滚动**（一张都不丢，可视区收到前 N 张那么高，
    // 其余用滚轮翻）。0 = 不按张数收窄，只在真的放不下时才滚。
    //
    // ⚠️ 2026-09-28 语义变更：这一项原来叫「SC 最多几张」、作用是**淘汰上限**（超过就丢最旧的）。
    //    用户澄清设计意图是「同时超过 6 条的时候启用滚动」，所以改成滚动阈值、不再淘汰。
    //    （保留条数另有一个不暴露给用户的硬顶 SuperChatHardCap = 200。）
    public int ScMaxStack = 6;
    public int ScScalePercent = 100;
    public int ScDurationScalePercent = 100;
    public bool ScShowAvatar = true;
    public string ScCorner = "tr";          // tr/br/tl/bl：SC 贴在画面的哪个角（tr = B站 直播间里的位置）

    // —— 屏蔽 ——
    public string[] BlockKeywords = new string[0];   // 前缀 re: 表示正则
    public string[] BlockUsers = new string[0];
    public int MinUserLevel = 0;
    public int MinMedalLevel = 0;
    public bool BlockDuplicate = true;      // 同类型、颜色及相同文案在窗口内合并为一条 ×N；不延长寿命
    public bool EnlargeMergedDanmaku = true;
    public bool NormalizeDuplicateText = true; // 只归一空白和全半角 ASCII，保留首条原文
    public int DuplicateWindowMs = 5000;
    public int MergedMaxScalePercent = 140;   // 热度放大上限；仍受布局空间限制

    // —— 行为 ——
    public bool AutoStart = true;           // 启动器每次打开 B站 直播时自动带起弹幕
    // 只在 AlwaysOnTop=true 时有意义：不在前台就整个藏起来。
    // 默认的「贴在播放器正上方」模式不需要它 —— 播放器被盖住时弹幕自然一起被盖住。
    public bool HideWhenNotForeground = true;
    public bool ShowPanelOnStart = true;
    // 点 SC 面板切换形态（完整卡片 → 折叠一行 → 只留角标），点卡片右上角的 × 关掉那一条。
    public bool ScClickable = true;
    public string HotkeyToggleDanmaku = "Ctrl+Alt+D";
    public string HotkeyCycleSc = "Ctrl+Alt+S";
    public string HotkeyToggleOverlay = "Ctrl+Alt+O";
    public string HotkeyFontUp = "Ctrl+Alt+Up";
    public string HotkeyFontDown = "Ctrl+Alt+Down";
    public string HotkeyAreaUp = "Ctrl+Alt+Right";
    public string HotkeyAreaDown = "Ctrl+Alt+Left";
    public string HotkeySpeedUp = "Ctrl+Alt+PgUp";     // 弹幕速度：PgUp 更快 / PgDn 更慢
    public string HotkeySpeedDown = "Ctrl+Alt+PgDn";
    // 弹幕延迟补偿的实时微调：[ 减、] 加（每次 500ms）
    public string HotkeyDelayLess = "Ctrl+Alt+[";
    public string HotkeyDelayMore = "Ctrl+Alt+]";

    /// 和收藏 / 历史放在同一个目录（便携包是 `data\`，开发机是 `%USERPROFILE%\.bili-live-watch\`）。
    /// **不能放 runtime\**：那是给会话状态/日志用的临时目录，janitor 会按 7 天清理，配置放进去迟早被清掉。
    public static string DefaultPath()
    {
        string dir = Path.GetDirectoryName(LiveHub.Favorites);
        if (dir == null || dir.Length == 0) dir = LiveHub.RuntimeDir;
        return Path.Combine(dir, "danmaku.json");
    }

    public static DanmakuConfig Load(string path)
    {
        DanmakuConfig config;
        if (File.Exists(path) && TryLoad(path, out config)) return config;
        config = new DanmakuConfig();
        config.Normalize();
        // 初次启动仍可继续；保存时必须重读现存文件，不能用默认值覆盖坏文件。
        config._savedPath = Path.GetFullPath(path);
        config._savedSnapshot = config.Snapshot();
        return config;
    }

    internal static bool TryLoad(string path, out DanmakuConfig config)
    {
        config = null;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            config = new JavaScriptSerializer().Deserialize<DanmakuConfig>(json);
            if (config == null) throw new InvalidDataException("配置内容为空");
        }
        catch (Exception ex) { Diagnostics.Report("配置读取", ex); config = null; return false; }
        config.Normalize();
        config._savedPath = Path.GetFullPath(path);
        config._savedSnapshot = config.Snapshot();
        return true;
    }

    // 手改配置很容易写出越界值（或删掉某个键导致反序列化成 0），统一在这里夹紧。
    public void Normalize()
    {
        // 画质档位只认这三个值；老配置的布尔开关迁移成 high
        if (MpvQuality != "light" && MpvQuality != "high") MpvQuality = "off";
        if (MpvEnhance && MpvQuality == "off") MpvQuality = "high";
        // 弹幕延迟夹到 0~15 秒：负值没意义，超过 15 秒弹幕就完全对不上画面了
        // （各平台实测缓冲 2.6~3.9 秒，15 秒已远超需要）。
        if (DanmakuDelayMs < 0) DanmakuDelayMs = 0;
        if (DanmakuDelayMs > 15000) DanmakuDelayMs = 15000;
        if (FontFamily == null || FontFamily.Trim().Length == 0) FontFamily = UiFonts.Family;
        if (FontName == null || FontName.Trim().Length == 0) FontName = "鸿蒙";
        // FontName 是对外的档位名（鸿蒙 / 微软雅黑 / 黑体 / 宋体），FontFamily 是真正拿去 new Font 的族名。
        // 默认用随包的 HarmonyOS Sans SC —— 界面已经用它了，弹幕跟着统一；
        // 万一字体没随包带上（UiFonts.Available=false），退回微软雅黑，
        // 否则 GDI+ 会**静默**换成一个默认字体，看起来像「设置没生效」。
        if (FontName == "黑体") FontFamily = "SimHei";
        else if (FontName == "宋体") FontFamily = "SimSun";
        else if (FontName == "鸿蒙" && UiFonts.Available) FontFamily = UiFonts.Family;
        else if (FontName == "微软雅黑") FontFamily = "Microsoft YaHei UI";
        else { FontName = UiFonts.Available ? "鸿蒙" : "微软雅黑"; FontFamily = UiFonts.Available ? UiFonts.Family : "Microsoft YaHei UI"; }
        FontScalePercent = Clamp(FontScalePercent, 50, 300);
        UiFontPercent = Clamp(UiFontPercent, 85, 130);
        if (UiFontKey != "harmony" && UiFontKey != "yahei-ui" && UiFontKey != "yahei" && UiFontKey != "system") UiFontKey = "auto";
        ScaleReferenceHeight = Clamp(ScaleReferenceHeight, 360, 2160);
        OpacityPercent = Clamp(OpacityPercent, 10, 100);
        SpeedPercent = Clamp(SpeedPercent, 25, 400);
        MaxOnScreen = Clamp(MaxOnScreen, 10, 600);
        DuplicateWindowMs = Clamp(DuplicateWindowMs, 1000, 15000);
        MergedMaxScalePercent = Clamp(MergedMaxScalePercent, 100, 180);
        DisplayAreaPercent = Clamp(DisplayAreaPercent, 10, 100);
        LineGapPercent = Clamp(LineGapPercent, 60, 250);
        StrokeWidth = Clamp(StrokeWidth, 1, 4);
        // ScMaxStack：0 = **不限**（渲染器给 200 的硬顶），正数是「最多同时堆几张」。
        //
        // ⚠️ 2026-09-28 修：这里原来写的是 `Clamp(ScMaxStack, 1, 8)`，而设置界面给的选项是
        //    1 / 2 / 3 / 4 / 6 / 12 / 不限(0) —— 两边对不上，后果是：
        //      · 选「不限」(0) → 被夹成 1
        //      · 选 12        → 被夹成 8
        //      · 8 又不在选项里 → 设置界面 `Array.IndexOf` 返回 -1 → Math.Max(0,-1)=0
        //        → 回填时显示成第一项「1 张」
        //    用户看到的就是「选不限或 12，保存后打开还是 1；6 以下都正常」。
        //    现在允许 0（不限），其余夹到 1..200（与渲染器那个硬顶一致）。
        if (ScMaxStack < 0) ScMaxStack = 0;
        if (ScMaxStack > 200) ScMaxStack = 200;
        ScScalePercent = Clamp(ScScalePercent, 60, 200);
        ScDurationScalePercent = Clamp(ScDurationScalePercent, 20, 400);
        MinUserLevel = Clamp(MinUserLevel, 0, 60);
        MinMedalLevel = Clamp(MinMedalLevel, 0, 40);
        if (ScMode == null) ScMode = "full";
        ScMode = ScMode.Trim().ToLowerInvariant();
        if (ScMode != "full" && ScMode != "compact" && ScMode != "badge" && ScMode != "off") ScMode = "full";
        if (ScCorner == null) ScCorner = "tr";
        ScCorner = ScCorner.Trim().ToLowerInvariant();
        if (ScCorner != "tr" && ScCorner != "br" && ScCorner != "tl" && ScCorner != "bl") ScCorner = "tr";
        if (BlockKeywords == null) BlockKeywords = new string[0];
        if (BlockUsers == null) BlockUsers = new string[0];
        NormalizeFeatures();
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// 存配置。**返回是否真的写成功** —— 以前这里吞掉一切，
    /// 于是「设置没保存上」和「保存成功」在界面上长得一模一样（评估报告 §5.9 点名的就是这处）。
    public bool Save(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            byte[] digest;
            using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
            string mutexName = "Local\\LiveHub.Config." + BitConverter.ToString(digest).Replace("-", "");
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("配置正在被另一个进程保存");
                    DanmakuConfig merged = Snapshot();
                    if (_savedSnapshot != null && string.Equals(_savedPath, fullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                    {
                        // 保存路径读取失败不能退回缺省值覆盖掉现有配置。
                        merged = new JavaScriptSerializer().Deserialize<DanmakuConfig>(File.ReadAllText(fullPath, Encoding.UTF8));
                        if (merged == null) throw new InvalidDataException("配置内容为空");
                        foreach (FieldInfo field in Fields)
                            if (!EqualField(field.GetValue(this), field.GetValue(_savedSnapshot)))
                                field.SetValue(merged, CopyField(field.GetValue(this)));
                    }
                    merged.Normalize();
                    LiveHub.AtomicWriteAllText(fullPath, new JavaScriptSerializer().Serialize(merged));
                    CopyFrom(merged);
                    _savedPath = fullPath;
                    _savedSnapshot = Snapshot();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Report("配置保存", ex);
            return false;
        }
    }

    public DanmakuConfig Clone()
    {
        DanmakuConfig copy = Snapshot();
        copy._savedSnapshot = _savedSnapshot;
        return copy;
    }

    internal void CopyFrom(DanmakuConfig source)
    {
        foreach (FieldInfo field in Fields) field.SetValue(this, CopyField(field.GetValue(source)));
        _savedPath = source._savedPath;
        _savedSnapshot = Snapshot();
    }

    private DanmakuConfig Snapshot()
    {
        DanmakuConfig copy = (DanmakuConfig)MemberwiseClone();
        copy._savedSnapshot = null;
        foreach (FieldInfo field in Fields) field.SetValue(copy, CopyField(field.GetValue(this)));
        return copy;
    }

    private static object CopyField(object value)
    {
        string[] array = value as string[];
        return array == null ? value : (string[])array.Clone();
    }

    private static bool EqualField(object first, object second)
    {
        string[] a = first as string[], b = second as string[];
        if (a == null || b == null) return object.Equals(first, second);
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}

// --------------------------------------------------------------------------------------------
// 一条弹幕（已解析成可以直接画的东西）
// --------------------------------------------------------------------------------------------
internal sealed class DanmakuItem
{
    public string Text;
    public int Color;              // 服务端给的十进制 RGB
    public int Mode;               // 1/2/3=滚动  4=底部  5=顶部
    public int FontSize;           // 服务端给的原始字号（一般 25）
    public long TimestampMs;
    public long Rnd;               // 前端叫「弹幕ID」，用来去重
    public long UserId;
    public string UserName;
    public int UserLevel;
    public int GuardLevel;         // 0 无 / 1 总督 / 2 提督 / 3 舰长
    public int MedalLevel;
    public string MedalName;
    public int MedalColor;
    public bool IsEmoticon;
    public string EmoticonUrl;
    public int EmoticonWidth;
    public int EmoticonHeight;
    public DateTime ArrivedAt = DateTime.UtcNow;
    public bool LargeEmoticon;
    public bool AdvancedText;
    public bool Pinned;
    internal DanmakuItem Copy() { return (DanmakuItem)MemberwiseClone(); }
}

// --------------------------------------------------------------------------------------------
// 一条 SC（醒目留言）
// --------------------------------------------------------------------------------------------
internal sealed class SuperChatItem
{
    public long Id;
    public int Price;
    public int DurationSec;        // 服务端给的展示秒数（time）
    public long StartTime;
    public long EndTime;
    public string Message;
    public string MessageTrans;
    public string UserName;
    public string Face;
    public string FaceFrame;       // 头像框图片（B站 的 face_frame，很多房间为空）
    public int UserLevel;
    public int GuardLevel;
    public int MedalLevel;
    public string MedalName;
    public Color BackgroundColor = Color.FromArgb(237, 245, 255);
    public Color BackgroundBottomColor = Color.FromArgb(42, 96, 178);
    public Color BackgroundColorStart = Color.FromArgb(49, 113, 210);
    public Color BackgroundColorEnd = Color.FromArgb(64, 93, 133);
    public Color BackgroundPriceColor = Color.FromArgb(116, 151, 205);
    public Color MessageFontColor = Color.FromArgb(163, 246, 255);
    public Color NameColor = Color.FromArgb(102, 102, 102);
    public DateTime ArrivedAt = DateTime.UtcNow;
    public DateTime ExpiresAt = DateTime.UtcNow.AddSeconds(60);


    // 布局高度缓存。放开 SC 数量上限后，每帧重新测量所有卡片会卡死 ——
    // 折行量宽是逐字符查精灵图的（O(n²)），几十张就能把帧率拖垮。
    // 缓存在卡片自己身上，缩放变了（CachedScale）才重量。
    public float CachedHeight;
    public float CachedScale;
}
