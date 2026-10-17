// --------------------------------------------------------------------------------------------
// 弹幕 / SC 的离线回归测试。
//
// 2026-09-25 起**直接参与编译**（原来是生成器把它注入 HardeningTests.cs 的 Main 里）：
// 单文件约束解除后，测试也做成 partial class 的另一半，不再需要注入。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static partial class HardeningTests
{    // ==================== 弹幕 / SC（2026-09-22）====================
    //
    // 这些测试**不联网**：wbi 向量是实测抓下来的固定值，解包用自己压出来的数据，
    // 参数校验走启动器 exe 的退出码。在线链路交给 `--danmaku-probe` 单独跑。

    private static Type DanmakuType(string name)
    {
        Type type = hub.Assembly.GetType(name, false);
        if (type == null) throw new Exception("找不到弹幕类型：" + name);
        return type;
    }

    private static object CallStatic(Type type, string name, params object[] args)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (method == null) throw new Exception("找不到静态方法：" + type.Name + "." + name);
        return method.Invoke(null, args);
    }

    private static int RunLauncherExitCode(string exe, string arguments)
    {
        ProcessStartInfo start = new ProcessStartInfo(exe, arguments);
        start.UseShellExecute = false;
        using (Process process = Process.Start(start))
        {
            if (process == null) return -1;
            if (!process.WaitForExit(60000)) { try { process.Kill(); } catch (Exception) { } return -1; }
            return process.ExitCode;
        }
    }

    private static bool IsLowerHex(string text)
    {
        if (text == null || text.Length != 32) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// 把弹幕 / SC 的离线用例登记进运行器（2026-09-27，评估报告 §2）。
    ///
    /// 改造前的写法是一串**顺序调用**：任何一条失败，后面的用例一行都不会跑 ——
    /// 报告实测踩到「WebP 一红，虎牙收割表 / Tars 容错等新测试根本没执行」。
    /// 现在每个用例独立登记，运行器逐个 try/catch、计时、汇总，失败不再互相掩埋。
    private static void RegisterDanmakuCases(string launcher)
    {
        TestRunner.Add("弹幕-协议", "B站 wbi 签名 / 包头 / zlib 解包 / 配置归一化", delegate { TestDanmakuProtocol(); });
        TestRunner.Add("弹幕-配置", "弹幕配置写盘 / 读回 / 缺省", delegate { TestDanmakuConfigRoundTrip(); });
        TestRunner.Add("启动器", "--danmaku 参数校验与 probe 拒绝", delegate { TestDanmakuArgValidation(launcher); });

        // ---- 6c) 彩色 emoji 精灵图：清单 ↔ 运行时 的契约 ----
        // 专门防「图重新生成了、C# 侧区段表/序列表没跟着改」这种**静默错位**（会画出完全不相干的 emoji）。
        // 直接拿真的 emoji-sheet.png + emoji-sheet.txt 过一遍查表逻辑。
        TestRunner.Add("弹幕-表情", "彩色 emoji 精灵图清单契约", delegate { TestEmojiSheet(launcher); });

        // ---- 6c.2) 平台表情字典：`[大笑]` 这类方括号代码 → 图片 ----
        // 用户反馈的现象是「弹幕里的 [汤圆]/[大笑] 没显示成图片」，根因是文本里夹代码
        // 需要客户端查字典，而 dmType==1 那条大表情路径管不到它。这条测试锁住：
        // 字典查表正确 + 文本确实被**换成了图片**（不是只多存了个字段）。
        TestRunner.Add("弹幕-表情", "平台表情字典查表", delegate { TestEmoticonDictionary(launcher); });
        TestRunner.Add("弹幕-表情", "虎牙表情字典", delegate { TestHuyaEmoticons(launcher); });
        TestRunner.Add("弹幕-表情", "斗鱼表情字典", delegate { TestDouyuEmoticons(launcher); });
        TestRunner.Add("弹幕-表情", "抖音表情字典", delegate { TestDouyinEmoticons(launcher); });

        // ---- 6c.3) 静默观测：长时间收不到帧**只记日志、不改变行为** ——
        // 但判据本身必须锁住：抽成纯函数之前这里踩过坑（lastWarn 初始化成「现在」
        // 会把**第一次**告警推到 5 分钟后，实测 200 秒的静默夹具一条日志都没有）。
        TestRunner.Add("弹幕-诊断", "静默观测：阈值与去重", delegate { TestSilenceWatch(launcher); });

        // ---- 6d) 斗鱼 STT 解析：SC（高能弹幕）那条路靠真实报文结构锁住 ----
        // 高能弹幕稀有（实测头部大房间 15 分钟才 1 条），**没法靠碰运气验收** ——
        // 用一条按线上真实结构（带转义、带嵌套）构造的报文做回归。
        TestRunner.Add("弹幕-协议", "斗鱼 STT 解析（含 SC 高能弹幕）", delegate { TestDouyuStt(launcher); });
        TestRunner.Add("弹幕-协议", "虎牙 Tars 编解码逐字节复现", delegate { TestHuyaTars(launcher); });
        TestRunner.Add("弹幕-协议", "虎牙 1400 弹幕解析", delegate { TestHuyaMessageNotice(launcher); });
        TestRunner.Add("弹幕-协议", "抖音签名与 protobuf", delegate { TestDouyinSign(launcher); });
        TestRunner.AddDesktop("弹幕-UI", "设置对话框布局不重叠", delegate { TestSettingsLayout(launcher); });

        // ---- 6e) 弹幕/SC 字体：随包的 HarmonyOS Sans SC ----
        // 用户要求「弹幕和 SC 也换成鸿蒙」。这条锁的是**端到端**：
        // 注册 exe 旁边 fonts\ 里的字体 → 用 config.FontFamily 渲染 → 位图宽度
        // 必须和微软雅黑**不一样**。只断言「配置里写了对的名字」是不够的 ——
        // `new Font("不存在的字体")` 不会抛异常，也不改变 `Font.Name`，
        // 失败时会静默回退，看不出任何异常。
        TestRunner.Add("弹幕-UI", "弹幕字体随包注册（端到端）", delegate { TestDanmakuFont(launcher); });

        // ---- 6e.2) 表情图片**异步下载**：首次出现的表情不能永久停在文字 ----
        // 用户反馈「[虎年] 还是文字」的根因就在这条：位图只在弹幕创建时渲染一次，
        // 那一刻图还没下载完 → 画成字面量 → 之后永远不重算。
        TestRunner.Add("弹幕-表情", "表情异步下载完成后重绘", delegate { TestEmoticonLateImage(launcher); });

        // ---- 6e.3) 抖音的表情图是 WebP，System.Drawing 不支持 ----
        // 「抖音那没弄好，还是有方框」的根因；B站 是 PNG 所以不受影响。
        TestRunner.Add("弹幕-表情", "WebP 解码（含系统能力探测）", delegate { TestWebpDecode(launcher); });

        // ---- 6f) 中转的 FLV 时间戳读写（虎牙首屏重复播放的去重靠它）----
        TestRunner.Add("弹幕-协议", "中转 FLV 时间戳读写", delegate { TestFlvTagTimestamp(launcher); });

        // ---- 6g) 认不出的表情代码要留痕 ----
        // 用户问「为什么这个带方括号的表情没显示成图片」时，光看截图分不清是
        // 「平台字典里没有」还是「我们匹配漏了」。记下来就能直接回答。
        TestRunner.Add("弹幕-表情", "认不出的表情代码留痕", delegate { TestUnknownEmoticonCode(launcher); });

        // ---- 6h) 虎牙收割表：本地 39 条之外的表情 ----
        TestRunner.Add("弹幕-表情", "虎牙收割表补齐", delegate { TestHuyaHarvestedEmoticons(launcher); });

        // ---- 6i) 虎牙 Tars 未定义类型不能拖垮连接 ----
        TestRunner.Add("弹幕-协议", "虎牙未知 Tars 类型容错", delegate { TestHuyaUnknownTarsType(launcher); });
        RegisterDanmakuRecoveryCases();
        RegisterDanmakuLayoutCases();
        TestRunner.AddDesktop("弹幕-UI", "设置对话框保存往返", delegate { TestSettingsRoundTrip(launcher); });
        TestRunner.Add("弹幕-渲染", "防重叠轨道选择", delegate { TestDanmakuNoOverlap(launcher); });
        TestRunner.Add("弹幕-抖音", "验证通过判据", delegate { TestDouyinGuardPassed(launcher); });
        TestRunner.Add("凭据", "Cookie 文件 ACL 只能当前用户读", delegate { TestCredentialAcl(launcher); });
        TestRunner.Add("凭据", "Cookie 域匹配（防子域伪装）", delegate { TestCookieHostMatch(launcher); });
        TestRunner.Add("诊断", "静默失败登记与摘要", delegate { TestDiagnosticsMechanism(launcher); });
        TestRunner.Add("工程", "空 catch 棘轮（只许减）", delegate { TestSilentCatchRatchet(launcher); });
        TestRunner.Add("播放", "B站 格式选择串", delegate { TestBiliFormatSelector(launcher); });
        TestRunner.Add("配置", "画面增强配置夹紧", delegate { TestEnhanceConfig(launcher); });
    }

    /// 协议层 + 配置归一化（原 TestDanmaku 前半段的内联断言，逐字未改）。
    private static void TestDanmakuProtocol()
    {
        Type client = DanmakuType("BiliDanmakuClient");
        Type config = DanmakuType("DanmakuConfig");

        // ---- 1) wbi mixinKey：用真实抓到的 img_key/sub_key 当向量 ----
        string shuffled = "7cd084941338484aae1ad9425b84077c4932caff0ff746eab6f01bf08b70ac45";
        string mixin = (string)CallStatic(client, "MixinKey", shuffled);
        Check(mixin == "ea1db124af3c7062474693fa704f4ff8", "wbi mixinKey 与实测向量一致");

        // ---- 2) wbi 签名：结构、参数排序、w_rid 形状 ----
        object session = Activator.CreateInstance(client, new object[] { "7777", null });
        client.GetField("_wbiMixin", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(session, mixin);
        MethodInfo sign = client.GetMethod("Sign", BindingFlags.NonPublic | BindingFlags.Instance);
        string signed = (string)sign.Invoke(session, new object[] { "id=545068&type=0" });
        Check(signed != null && signed.StartsWith("?id=545068&type=0&wts="),
            "wbi 签名：参数按 key 升序且带 wts（id → type → wts）");
        int ridAt = signed.IndexOf("&w_rid=", StringComparison.Ordinal);
        Check(ridAt > 0 && IsLowerHex(signed.Substring(ridAt + 7)), "wbi 签名：w_rid 是 32 位小写十六进制");

        // ---- 3) zlib 解包：自己压一段、补 2 字节头，再解回来 ----
        string json = "{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16777215]]}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] deflated;
        using (MemoryStream raw = new MemoryStream())
        {
            using (DeflateStream zip = new DeflateStream(raw, CompressionMode.Compress, true))
                zip.Write(payload, 0, payload.Length);
            deflated = raw.ToArray();
        }
        byte[] zlib = new byte[deflated.Length + 6];
        Array.Copy(deflated, 0, zlib, 2, deflated.Length);          // 前 2 字节 zlib 头，尾部 adler32 留空
        byte[] back = (byte[])CallStatic(client, "Inflate", new object[] { zlib });
        Check(Encoding.UTF8.GetString(back) == json, "protover=2 解包：跳 2 字节 zlib 头后内容一致");

        // ---- 4) 包头 16 字节大端编解码 ----
        byte[] header = new byte[16];
        CallStatic(client, "WriteBe32", new object[] { header, 0, 1234567 });
        CallStatic(client, "WriteBe16", new object[] { header, 4, 16 });
        CallStatic(client, "WriteBe16", new object[] { header, 6, 2 });
        CallStatic(client, "WriteBe32", new object[] { header, 8, 5 });
        CallStatic(client, "WriteBe32", new object[] { header, 12, 1 });
        Check((int)CallStatic(client, "Be32", new object[] { header, 0 }) == 1234567, "包头 pack_len 大端解回");
        Check((int)CallStatic(client, "Be16", new object[] { header, 4 }) == 16, "包头 header_size = 16");
        Check((int)CallStatic(client, "Be16", new object[] { header, 6 }) == 2, "包头 ver 解回");
        Check((int)CallStatic(client, "Be32", new object[] { header, 8 }) == 5, "包头 operation 解回");
        Check(header[0] == 0x00 && header[1] == 0x12 && header[2] == 0xD6 && header[3] == 0x87,
            "包头字节序：1234567 = 0x0012D687");

        // ---- 5) Unix 时间戳换算 ----
        long epoch = (long)CallStatic(client, "ToUnixSeconds",
            new object[] { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        Check(epoch == 1767225600L, "Unix 时间戳换算（2026-01-01 = 1767225600）");

        // ---- 6) 配置归一化：手改 JSON 很容易写出越界值 ----
        object settings = Activator.CreateInstance(config);
        config.GetField("FontScalePercent").SetValue(settings, 9999);
        config.GetField("SpeedPercent").SetValue(settings, -5);
        config.GetField("ScMode").SetValue(settings, "bogus");
        config.GetField("ScCorner").SetValue(settings, "zz");
        config.GetField("BlockKeywords").SetValue(settings, null);
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((int)config.GetField("FontScalePercent").GetValue(settings) == 300, "配置夹紧：字号上限 300");
        Check((int)config.GetField("SpeedPercent").GetValue(settings) == 25, "配置夹紧：速度下限 25");
        Check((string)config.GetField("ScMode").GetValue(settings) == "full", "非法 SC 模式回落成完整卡片");
        Check((string)config.GetField("ScCorner").GetValue(settings) == "tr", "非法 SC 角落回落成右上");
        Check(((string[])config.GetField("BlockKeywords").GetValue(settings)).Length == 0, "屏蔽词 null 归一成空数组");

        // ---- 6a-2) SC 最多几张：Normalize 必须保留设置界面给的**每一个**选项 ----
        //
        // 现场（2026-09-28 用户报）：设置里选「不限」或「12 张」，保存后重新打开显示成「1 张」，
        // 而 6 张以下都正常。根因是两处对不上：
        //   · `Normalize()` 写的是 `Clamp(ScMaxStack, 1, 8)`，而界面选项是 1/2/3/4/6/12/不限(0)
        //     → 「不限」(0) 被夹成 1，12 被夹成 8；
        //   · 8 不在选项里 → 界面 `Array.IndexOf` 返回 -1 → `Math.Max(0,-1)` = 0
        //     → 静默显示成第一项「1 张」。
        // 下面这条断言锁的是**不变量**：界面上能选的档位，过一遍 Normalize 必须原样保留。
        // 任何一边改了范围而另一边没跟，这里立刻红。
        int[] uiStackValues = new int[] { 1, 2, 3, 4, 6, 12, 0 };
        foreach (int v in uiStackValues)
        {
            object cfgStack = Activator.CreateInstance(config);
            config.GetField("ScMaxStack").SetValue(cfgStack, v);
            config.GetMethod("Normalize").Invoke(cfgStack, null);
            int got = (int)config.GetField("ScMaxStack").GetValue(cfgStack);
            Check(got == v, "SC 最多几张：界面选项 " + v + " 过 Normalize 后不变（实得 " + got + "）");
        }
        object cfgBig = Activator.CreateInstance(config);
        config.GetField("ScMaxStack").SetValue(cfgBig, 9999);
        config.GetMethod("Normalize").Invoke(cfgBig, null);
        Check((int)config.GetField("ScMaxStack").GetValue(cfgBig) == 200,
            "SC 最多几张：超大值夹到 200（与渲染器的硬顶一致）");
        object cfgNeg = Activator.CreateInstance(config);
        config.GetField("ScMaxStack").SetValue(cfgNeg, -5);
        config.GetMethod("Normalize").Invoke(cfgNeg, null);
        Check((int)config.GetField("ScMaxStack").GetValue(cfgNeg) == 0,
            "SC 最多几张：负数归一成 0（= 不限，不是 1）");

        // ---- 6b) 对齐 B站 的档位：显示区域 / 字体 / 层叠方式 ----
        config.GetField("DisplayAreaPercent").SetValue(settings, 5);
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((int)config.GetField("DisplayAreaPercent").GetValue(settings) == 10, "显示区域下限夹到 10%（1/4 屏以下不再小）");
        config.GetField("DisplayAreaPercent").SetValue(settings, 150);
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((int)config.GetField("DisplayAreaPercent").GetValue(settings) == 100, "显示区域上限夹到 100%（全屏）");
        config.GetField("FontName").SetValue(settings, "黑体");
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((string)config.GetField("FontFamily").GetValue(settings) == "SimHei", "字体档位「黑体」映射到 SimHei");
        config.GetField("FontName").SetValue(settings, "宋体");
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((string)config.GetField("FontFamily").GetValue(settings) == "SimSun", "字体档位「宋体」映射到 SimSun");
        // 弹幕/SC 默认走随包的 HarmonyOS Sans SC（和界面同一套）。
        // ⚠️ 测试进程没注册字体（UiFonts.Register 要 exe 旁边的 fonts\），
        //    所以这里只断言**映射规则**，不断言 Available：
        //    · 「微软雅黑」无论字体在不在，都必须映射到 Microsoft YaHei UI
        //    · 认不出来的档位名要落到一个**真实存在**的族名上，不能留着垃圾值
        object freshCfg = Activator.CreateInstance(config);
        Check((string)config.GetField("FontFamily").GetValue(freshCfg) == "HarmonyOS Sans SC",
            "弹幕字体默认值 = HarmonyOS Sans SC（和界面统一）");
        Check((string)config.GetField("FontName").GetValue(freshCfg) == "鸿蒙",
            "弹幕字体档位默认值 = 鸿蒙");
        config.GetField("FontName").SetValue(settings, "微软雅黑");
        config.GetMethod("Normalize").Invoke(settings, null);
        Check((string)config.GetField("FontFamily").GetValue(settings) == "Microsoft YaHei UI",
            "字体档位「微软雅黑」映射到 Microsoft YaHei UI");
        config.GetField("FontName").SetValue(settings, "不存在的档位");
        config.GetMethod("Normalize").Invoke(settings, null);
        string fallback = (string)config.GetField("FontFamily").GetValue(settings);
        Check(fallback == "HarmonyOS Sans SC" || fallback == "Microsoft YaHei UI",
            "认不出的字体档位会落到一个真实族名（实际 " + fallback + "）");
        object fresh = Activator.CreateInstance(config);
        Check((bool)config.GetField("AlwaysOnTop").GetValue(fresh) == false,
            "默认不常驻最前（贴在播放器正上方）");
        Check((bool)config.GetField("ScClickable").GetValue(fresh) == true, "默认允许点 SC 面板切换形态");
        Check((bool)config.GetField("AntiBlockSubtitle").GetValue(fresh) == true, "默认开启防挡字幕");
    }

    /// 弹幕配置的写盘 / 读回 / 缺省（原第 7 节，独立成用例）。
    private static void TestDanmakuConfigRoundTrip()
    {
        Type config = DanmakuType("DanmakuConfig");
        object settings = Activator.CreateInstance(config);

        // ---- 7) 配置往返 ----
        string path = Path.Combine(Path.GetTempPath(), "danmaku-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            config.GetField("FontScalePercent").SetValue(settings, 125);
            config.GetField("ScCorner").SetValue(settings, "bl");
            config.GetField("ScaleWithWindow").SetValue(settings, false);
            config.GetMethod("Save").Invoke(settings, new object[] { path });
            Check(File.Exists(path), "弹幕配置能写盘");
            object loaded = config.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { path });
            Check((int)config.GetField("FontScalePercent").GetValue(loaded) == 125, "配置往返：字号");
            Check((string)config.GetField("ScCorner").GetValue(loaded) == "bl", "配置往返：SC 角落");
            Check((bool)config.GetField("ScaleWithWindow").GetValue(loaded) == false, "配置往返：字号随窗口缩放");
            object missing = config.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { path + ".not-exist" });
            Check((string)config.GetField("ScMode").GetValue(missing) == "full", "配置缺失时用默认值");

            // SC 最多几张：走**用户实际那条路** —— 保存 → 重新打开（Load 内部会 Normalize）。
            // 上面 6a-2 测的是 Normalize 本身，这里测的是「保存后重开还是不是原来那个值」，
            // 也就是用户报的那个现象。
            foreach (int v in new int[] { 12, 0, 6 })
            {
                object cfgSave = Activator.CreateInstance(config);
                config.GetField("ScMaxStack").SetValue(cfgSave, v);
                config.GetMethod("Save").Invoke(cfgSave, new object[] { path });
                object cfgLoad = config.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { path });
                int back = (int)config.GetField("ScMaxStack").GetValue(cfgLoad);
                Check(back == v, "SC 最多几张：保存 " + v + " → 重新打开仍是 " + v + "（实得 " + back + "）");
            }
        }
        finally { try { File.Delete(path); } catch (Exception) { } }
    }

    /// --danmaku / --danmaku-probe 的参数校验（原第 8、9 节，独立成用例）。
    private static void TestDanmakuArgValidation(string launcher)
    {
        // ---- 8) --danmaku 参数校验（与 --relay 同一套白名单思路）----
        Check(RunLauncherExitCode(launcher, "--danmaku bili ..\\.. 0123456789abcdef0123456789abcdef 0") == 2,
            "--danmaku 拒绝非法房间号");
        Check(RunLauncherExitCode(launcher, "--danmaku bili 7777 not-a-session-id 0") == 2,
            "--danmaku 拒绝非法会话号");
        Check(RunLauncherExitCode(launcher, "--danmaku nosuch 7777 0123456789abcdef0123456789abcdef 0") == 2,
            "--danmaku 拒绝非法平台");

        // ---- 9) --danmaku-probe 对**还没接入**的平台要明确拒绝，而不是静默失败 ----
        string probeOut = Path.Combine(Path.GetTempPath(), "danmaku-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            int code = RunLauncherExitCode(launcher, "--danmaku-probe nosuch:123456 \"" + probeOut + "\" 1");
            Check(code == 2, "--danmaku-probe 对不认识的平台返回 2（三个平台全接入了，用假名字试）");
            Check(File.Exists(probeOut) && File.ReadAllText(probeOut, Encoding.UTF8).Contains("UNSUPPORTED"),
                "--danmaku-probe 把「不支持」写进报告");
        }
        finally { try { File.Delete(probeOut); } catch (Exception) { } }
    }


    /// 斗鱼 STT 解析回归。
    ///
    /// 为什么要**构造**报文而不是抓一条真的：高能弹幕（SC）极稀有 ——
    /// 实测头部大房间（yyf 9999）15 分钟才 1 条，靠蹲是验收不了的。
    /// 下面这条按线上真实结构写：① 值里 `@`→`@A`、`/`→`@S` 的转义；
    /// ② `chatmsg` 字段是**嵌套的一整条 STT 串**（要解两层）；
    /// ③ `type` **不在第一个字段**（这是 comm_chatmsg 最容易踩的坑：不能用 StartsWith 预筛）。
    /// 虎牙 Tars 编解码回归 —— **决定性验证**。
    ///
    /// 判据：我按 Tars 规则写的 C# 构造器，必须**逐字节复现**调研时真实发到虎牙服务器、
    /// 并被正常处理的那两个包（房间 333003：lChannelId=lSubChannelId=2367547387、lYyid=2369247112）。
    /// 只要有一个字节不对，就说明头字节 / 类型窄化 / BYTE_ARRAY 双头 / WUP 嵌套哪里理解错了。
    private static void TestHuyaTars(string launcher)
    {
        Type huya = Assembly.LoadFrom(launcher).GetType("HuyaDanmakuClient", true);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        const long LChannelId = 2367547387L;      // 0x8D1DE7FB
        const long LYyid = 2369247112L;           // 0x8D37D788

        string register = ToHex((byte[])huya.GetMethod("CommandRegisterGroup", flags)
            .Invoke(null, new object[] { LChannelId }));
        const string RegisterExpected =
            "00101d000027090002060f6c6976653a32333637353437333837060f636861743a323336373534373338371600";
        Check(register == RegisterExpected, "虎牙：订阅包（cmd16）逐字节复现实测包");
        if (register != RegisterExpected)
            Console.WriteLine("      实际=" + register + "\n      期望=" + RegisterExpected);

        string heartbeat = ToHex((byte[])huya.GetMethod("CommandHeartbeat", flags)
            .Invoke(null, new object[] { LYyid, LChannelId, LChannelId }));
        const string HeartbeatExpected =
            "00031d0000790000007910032c3c4c56086f6e6c696e657569660f4f6e55736572486561727442656174" +
            "7d00004c0800010604745265711d00003f0a03000000008d37d7881600260036076164725f7761704600" +
            "5c0b13000000008d1de7fb23000000008d1de7fb3c43000000008d37d788500160017c8c9cac8c980ca80c";
        Check(heartbeat == HeartbeatExpected, "虎牙：心跳包（cmd3 WUP）逐字节复现实测包");
        if (heartbeat != HeartbeatExpected)
            Console.WriteLine("      实际=" + heartbeat + "\n      期望=" + HeartbeatExpected);

        // Tars 基础规则：类型窄化 + BYTE_ARRAY 双头 + 大 tag 两字节头
        Type writerType = Assembly.LoadFrom(launcher).GetType("TarsWriter", true);
        Type readerType = Assembly.LoadFrom(launcher).GetType("TarsReader", true);
        object writer = Activator.CreateInstance(writerType);
        MethodInfo i32 = writerType.GetMethod("I32");
        MethodInfo str = writerType.GetMethod("Str");
        i32.Invoke(writer, new object[] { 0, 0 });          // 0 -> ZERO 头，只占 1 字节
        i32.Invoke(writer, new object[] { 1, 100 });        // 落在 int8 -> INT8 头 + 1 字节
        i32.Invoke(writer, new object[] { 2, 40000 });      // 落在 int32 -> INT32 头 + 4 字节
        str.Invoke(writer, new object[] { 16, "x" });       // tag>=15 -> 两字节头
        byte[] encoded = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
        Check(ToHex(encoded) == "0c10642200009c40f6100178",
            "虎牙：Tars 类型窄化 + 大 tag 两字节头编码正确");
        if (ToHex(encoded) != "0c10642200009c40f6100178") Console.WriteLine("      实际=" + ToHex(encoded));

        object reader = Activator.CreateInstance(readerType, new object[] { encoded });
        MethodInfo num = readerType.GetMethod("Num");
        MethodInfo rstr = readerType.GetMethod("Str");
        Check((long)num.Invoke(reader, new object[] { 1, -1L }) == 100, "虎牙：Tars 读 INT8");
        Check((long)num.Invoke(reader, new object[] { 2, -1L }) == 40000, "虎牙：Tars 读 INT32");
        Check((string)rstr.Invoke(reader, new object[] { 16, "" }) == "x", "虎牙：Tars 读大 tag 字符串");
    }

    /// 虎牙 1400 弹幕解析回归 —— 专锁**「结构体读完必须吃掉 STRUCTEND」**这个 bug。
    ///
    /// 真实踩过：`tUserInfo` 读完后游标停在结构体内部，后面取 `sContent`(tag3) 会先在 tUserInfo 里
    /// 找到 tag3（iGender）——拿到的不是弹幕正文；更常见的是直接撞上 STRUCTEND → 返回 null →
    /// **DANMU=0，但包明明收到了**。当时是靠打印 cmd 类型才定位到的。
    ///
    /// 报文是**手工拼的十六进制**（不是用 TarsWriter 生成的）—— 这样 writer 有 bug 也掩盖不了 reader 的问题。
    private static void TestHuyaMessageNotice(string launcher)
    {
        Type huya = Assembly.LoadFrom(launcher).GetType("HuyaDanmakuClient", true);
        object client = Activator.CreateInstance(huya, new object[] { "333003" });
        MethodInfo parse = huya.GetMethod("ParseMessageNotice",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // MessageNotice {
        //   tag0 STRUCTBEGIN tUserInfo { tag0 INT32=123, tag1 ZERO, tag2 STRING1 "测试用户",
        //                                tag3 ZERO, tag4 STRING1 "http://avatar", tag5 INT8=3 } STRUCTEND
        //   tag3 STRING1 "这是一条虎牙弹幕"
        //   tag5 STRUCTBEGIN ContentFormat { tag0 INT32 = 0xFF0000 } STRUCTEND
        // }
        string hex =
            "0a" +                       // STRUCTBEGIN tag0
            "02" + "0000007b" +          // tag0 INT32 = 123
            "1c" +                       // tag1 ZERO
            "26" + "0c" + "e6b58be8af95e794a8e688b7" +      // tag2 STRING1(12) 测试用户
            "3c" +                       // tag3 ZERO
            "46" + "0d" + "687474703a2f2f617661746172" +    // tag4 STRING1(13) http://avatar
            "50" + "03" +                // tag5 INT8 = 3（贵族等级）
            "0b" +                       // STRUCTEND
            "36" + "18" + "e8bf99e698afe4b880e69da1e8998ee78999e5bcb9e5b995" +   // tag3 STRING1(24) 正文
            "5a" +                       // STRUCTBEGIN tag5 = ContentFormat
            "02" + "00ff0000" +          // tag0 INT32 = 0xFF0000
            "0b";

        object item = parse.Invoke(client, new object[] { HexToBytes(hex) });
        Check(item != null, "虎牙 1400：能解出一条弹幕（结构体后能回到外层取正文）");
        if (item == null) return;
        Type itemType = item.GetType();
        Check((string)itemType.GetField("Text").GetValue(item) == "这是一条虎牙弹幕",
            "虎牙 1400：正文 = 外层 tag3（不是 tUserInfo 里的 tag3）");
        Check((string)itemType.GetField("UserName").GetValue(item) == "测试用户",
            "虎牙 1400：昵称 = tUserInfo.tag2");
        Check((int)itemType.GetField("Color").GetValue(item) == 0xFF0000,
            "虎牙 1400：颜色 = ContentFormat.tag0 = 0xFF0000");
        Check((int)itemType.GetField("GuardLevel").GetValue(item) == 3,
            "虎牙 1400：贵族等级 = tUserInfo.tag5 = 3");
    }

    /// 抖音签名回归 —— **决定性验证**。
    ///
    /// 判据：我移植的 C# 签名必须复现 jwwsjlm/douyinLive 的 `native_test.go` 里那两个公开测试向量。
    /// 这条过了，就说明 MD5 两跳 / RC4 / 自定义字母表 base64 / payload 布局**全对** ——
    /// 也就是说「抖音的 signature 可以纯算法算出来、不需要 JS 引擎」这件事在本项目里成立了。
    /// 设置对话框布局回归。
    ///
    /// 真实 bug：窗体高度写死 620、按钮钉死 y=578，但内容一轮轮加到了 **864px**，
    /// 而屏幕逻辑只有 **800** —— 最后几个复选框和「保存」被挤出可视区，用户直接看到了。
    /// 这类问题以前只能靠肉眼，现在**用断言锁住**：按钮必须在客户区内、内容必须装得下。
    /// 顺便把对话框渲染成 PNG，方便人工复核。
    /// 设置对话框「保存」往返测试 —— 防止重写对话框时把字段写错 / 漏写。
    ///
    /// 做法：把**每个字段都设成非默认值** → 构建对话框 → 触发「保存」→ 断言配置**一点没变**。
    /// 只要映射表写错（比如 ScMode 的下标错位），往返后就会变成别的值，立刻暴露。
    /// SC 的形态 / 张数 / 位置这三项尤其要盯 —— 对话框被重写过，而它们只在这里能改。
    private static void TestSettingsRoundTrip(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type dialogType = assembly.GetType("DanmakuSettingsDialog", true);
        Type configType = assembly.GetType("DanmakuConfig", true);
        object config = Activator.CreateInstance(configType);

        // 全部设成**非默认值**，这样「漏写」和「写错」都会让断言失败
        var expected = new Dictionary<string, object>();
        Action<string, object> set = delegate (string field, object value)
        {
            configType.GetField(field).SetValue(config, value);
            expected[field] = value;
        };
        set("FontScalePercent", 130); set("SpeedPercent", 130); set("OpacityPercent", 50);
        set("DisplayAreaPercent", 50); set("MaxOnScreen", 80); set("FontName", "黑体");
        set("ScMode", "compact"); set("ScMaxStack", 4); set("ScCorner", "bl");
        set("StrokeEnabled", false); set("ShowMedal", true); set("ShowUserName", true);
        set("ShowUserLevel", true); set("ShowEmoticon", false); set("AutoStart", false);
        set("HideWhenNotForeground", false); set("AlwaysOnTop", true);
        set("ScClickable", false); set("AntiBlockSubtitle", false); set("ScaleWithWindow", false);

        object parts = dialogType.GetMethod("Build",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { config });
        Type partsType = parts.GetType();
        System.Windows.Forms.Form dialog =
            (System.Windows.Forms.Form)partsType.GetField("Dialog").GetValue(parts);
        System.Windows.Forms.Button ok =
            (System.Windows.Forms.Button)partsType.GetField("Ok").GetValue(parts);

        dialog.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        dialog.Location = new Point(-4000, -4000);
        dialog.Show();
        System.Windows.Forms.Application.DoEvents();
        ok.PerformClick();                     // 触发保存
        System.Windows.Forms.Application.DoEvents();
        dialog.Close();

        int mismatched = 0;
        foreach (KeyValuePair<string, object> pair in expected)
        {
            object actual = configType.GetField(pair.Key).GetValue(config);
            if (!object.Equals(actual, pair.Value))
            {
                mismatched++;
                Console.WriteLine("      " + pair.Key + " 期望=" + pair.Value + " 实际=" + actual);
            }
        }
        Check(mismatched == 0, "设置对话框：保存往返不改变任何字段（含 SC 形态/张数/位置）");
    }
    private static void TestSettingsLayout(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type dialogType = assembly.GetType("DanmakuSettingsDialog", true);
        Type configType = assembly.GetType("DanmakuConfig", true);
        object config = Activator.CreateInstance(configType);

        object parts = dialogType.GetMethod("Build",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { config });
        Type partsType = parts.GetType();
        System.Windows.Forms.Form dialog =
            (System.Windows.Forms.Form)partsType.GetField("Dialog").GetValue(parts);
        System.Windows.Forms.Panel body =
            (System.Windows.Forms.Panel)partsType.GetField("Body").GetValue(parts);
        System.Windows.Forms.Button ok =
            (System.Windows.Forms.Button)partsType.GetField("Ok").GetValue(parts);
        System.Windows.Forms.Button cancel =
            (System.Windows.Forms.Button)partsType.GetField("Cancel").GetValue(parts);

        // ① 两个按钮必须完整落在客户区内（这就是那个 bug 的判据）
        Check(ok.Bottom <= dialog.ClientSize.Height,
            "设置对话框：「保存」按钮在可视区内（bug 判据）");
        Check(cancel.Bottom <= dialog.ClientSize.Height,
            "设置对话框：「取消」按钮在可视区内");
        Check(ok.Right <= dialog.ClientSize.Width && cancel.Right <= dialog.ClientSize.Width,
            "设置对话框：按钮不超出右边界");
        Check(ok.Bottom > dialog.ClientSize.Height - 60,
            "设置对话框：按钮贴在底部（不是飘在中间）");

        // ② 内容区要么装得下，要么可滚动 —— 两者都不满足就是又坏了
        int needed = body.AutoScrollMinSize.Height;
        bool fits = needed <= body.ClientSize.Height;
        Check(fits || body.AutoScroll,
            "设置对话框：内容装得下，或者退化成可滚动（needed=" + needed
            + " 可视=" + body.ClientSize.Height + " 可滚动=" + body.AutoScroll + ")");

        // ③ 内容尽量不用滚动（用户的屏是 1280x800 逻辑像素，双列就是为了让它放得下）
        Check(fits, "设置对话框：内容不用滚动就能放下（needed=" + needed
            + " 可视=" + body.ClientSize.Height + "）");

        // ④ 渲染成 PNG 供人工复核
        try
        {
            dialog.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            dialog.Location = new Point(-4000, -4000);     // 屏幕外，别闪到用户
            dialog.Show();
            System.Windows.Forms.Application.DoEvents();
            // ── 控件不许互相压住（2026-09-25 加）──────────────────────────────
            // 为什么必须机器判：我把「mpv 画质档位」那个 combo 放到了复选框**之后**创建，
            // 而 combo() 会往 comboRow 追加一行，于是它落在复选框区上、压住了「屏蔽词」标签。
            // 编译过、223 项离线过、上面 ①②③ 三条布局断言也全过 —— 只有人眼看截图才发现。
            //
            // ⚠️ 位置必须在 Show() 和 Close() **之间**：之前放在渲染前，控件 Visible 全是
            //    false，收集到 0 个（空转）；放在 Close() 之后同样 0 个。实测两次都报
            //    「查了 0 个」却 PASS —— 空转的断言比没有断言更危险。
            {
                var boxes = new List<Control>();
                CollectVisible(dialog, boxes);
                var clashes = new List<string>();
                for (int a = 0; a < boxes.Count; a++)
                {
                    for (int b = a + 1; b < boxes.Count; b++)
                    {
                        Control ca = boxes[a], cb = boxes[b];
                        if (Math.Abs(ca.Top - cb.Top) > 6) continue;          // 不同行，跳过
                        if (ca.Contains(cb) || cb.Contains(ca)) continue;     // 父子/包含关系
                        Rectangle ra = ca.Bounds, rb = cb.Bounds;
                        int ox = Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left);
                        int oy = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
                        if (ox > 2 && oy > 2)
                            clashes.Add("[" + SafeText(ca) + "] 与 [" + SafeText(cb) + "] 重叠 "
                                + ox + "x" + oy + "px");
                    }
                }
                Check(boxes.Count > 10, "设置对话框：重叠判据收集到 " + boxes.Count + " 个控件（>10 才不算空转）");
                Check(clashes.Count == 0,
                    "设置对话框：同行控件不重叠（查了 " + boxes.Count + " 个）"
                    + (clashes.Count > 0 ? " —— " + string.Join(" / ", clashes.ToArray()) : ""));
            }
            using (Bitmap shot = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(shot, new Rectangle(0, 0, dialog.Width, dialog.Height));
                string outPath = Path.Combine(Path.GetTempPath(), "danmaku-settings-dialog.png");
                shot.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("      设置对话框已渲染 -> " + outPath);
            }
            dialog.Close();
        }
        catch (Exception ex)
        {
            Check(false, "设置对话框渲染失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }
    /// 收集对话框里的可见控件（递归），供布局断言用。**只在 Show() 之后调用才有意义。**
    private static void CollectVisible(Control parent, List<Control> into)
    {
        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;
            into.Add(c);
            if (c.HasChildren) CollectVisible(c, into);
        }
    }

    /// 控件的可读名字：Text 优先，其次 Name，都没有就退回类型名。
    private static string SafeText(Control c)
    {
        try
        {
            if (!string.IsNullOrEmpty(c.Text)) return c.Text;
            if (!string.IsNullOrEmpty(c.Name)) return c.Name;
        }
        catch (Exception) { }
        return c.GetType().Name;
    }
    private static void TestDouyinSign(string launcher)
    {
        Type sign = Assembly.LoadFrom(launcher).GetType("DouyinSign", true);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo method = sign.GetMethod("SignWithValues", flags);
        const string Stub = "704f436b2558b0d7a1c7e758527dd8f1";

        string first = (string)method.Invoke(null, new object[] { Stub, 1, true, 63, 63 });
        Check(first == "6pt0VC0aRZDP7n07", "抖音签名：测试向量 1（counter=1, random=true, 63, 63）");
        if (first != "6pt0VC0aRZDP7n07") Console.WriteLine("      实际=" + first + " 期望=6pt0VC0aRZDP7n07");

        string second = (string)method.Invoke(null, new object[] { Stub, 0, false, 0, 6 });
        Check(second == "fD+evFuzasjOC8Uu", "抖音签名：测试向量 2（counter=0, random=false, 0, 6）");
        if (second != "fD+evFuzasjOC8Uu") Console.WriteLine("      实际=" + second + " 期望=fD+evFuzasjOC8Uu");

        MethodInfo liveSign = sign.GetMethod("Sign", flags);
        string live = (string)liveSign.Invoke(null, new object[] { Stub });
        Check(live != null && live.Length == 16, "抖音签名：线上入口产出 16 字符");
        Check(live != (string)liveSign.Invoke(null, new object[] { Stub }), "抖音签名：每次重算都不同（含随机数，不能缓存）");

        // protobuf 读取器：varint 边界 + length-delimited
        Type proto = Assembly.LoadFrom(launcher).GetType("ProtoReader", true);
        // field1(varint)=300, field2(bytes)="hi", field3(varint)=1
        byte[] sample = new byte[] { 0x08, 0xAC, 0x02, 0x12, 0x02, 0x68, 0x69, 0x18, 0x01 };
        object reader = Activator.CreateInstance(proto, new object[] { sample });
        MethodInfo tryTag = proto.GetMethod("TryReadTag");
        object[] args = new object[] { 0, 0 };
        tryTag.Invoke(reader, args);
        Check((int)args[0] == 1 && (int)args[1] == 0, "抖音 protobuf：读出 field1/varint");
        Check((long)proto.GetMethod("ReadVarint").Invoke(reader, null) == 300, "抖音 protobuf：varint 跨字节 300");
        tryTag.Invoke(reader, args);
        Check((string)proto.GetMethod("ReadString").Invoke(reader, null) == "hi", "抖音 protobuf：length-delimited 字符串");
        tryTag.Invoke(reader, args);
        Check((long)proto.GetMethod("ReadVarint").Invoke(reader, null) == 1, "抖音 protobuf：继续读下一个字段");
    }
    private static byte[] HexToBytes(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
    /// 弹幕**互不重叠**回归。
    ///
    /// 用实际入队和连续帧检查滚动/固定弹幕的矩形，避免只验证私有轨道表。
    private static void TestDanmakuNoOverlap(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type configType = assembly.GetType("DanmakuConfig", true);
        Type rendererType = assembly.GetType("DanmakuRenderer", true);

        // ---- SC 四个角的几何：**左下/右下必须真的在下面** ----
        //
        // 现场（2026-09-28 用户报）：「不一定堆叠在右上角，我有设置可以改啊，都要实现
        // 左上/左下/右上/右下」。查出来旧代码的 bottom 分支配的是
        //     viewTop    = Math.Max(6f, h - margin - (h - 2*margin))   // 恒等于 margin（画面顶部）
        //     viewHeight = h - margin - viewTop                        // 几乎整屏
        // 于是「左下/右下」和「左上/右上」**完全一样**，四个角只有左右生效。
        // 下面这几条把这个规则钉住：底部角的可视区必须贴着下边，内容矮时也要贴下边。
        BindingFlags pubStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo viewTopFn = rendererType.GetMethod("ScViewTop", pubStatic);
        MethodInfo contentTopFn = rendererType.GetMethod("ScContentTop", pubStatic);
        MethodInfo badgeYFn = rendererType.GetMethod("ScBadgeY", pubStatic);
        Check(viewTopFn != null && contentTopFn != null && badgeYFn != null, "SC 四角：几何纯函数存在（可离线验证）");
        if (viewTopFn != null && contentTopFn != null && badgeYFn != null)
        {
            const float H = 1080f, M = 10f, VH = 400f;
            float topTop = (float)viewTopFn.Invoke(null, new object[] { false, H, M, VH });
            float botTop = (float)viewTopFn.Invoke(null, new object[] { true, H, M, VH });
            Check(topTop == 12f, "SC 四角：顶部角可视区从 12 开始（实得 " + topTop + "）");
            Check(Math.Abs(botTop - (H - M - VH)) < 0.01f,
                "SC 四角：底部角可视区**贴着下边**（上边应为 " + (H - M - VH) + "，实得 " + botTop + "）");
            Check(botTop > H / 2f,
                "SC 四角：底部角的上边在下半屏（实得 " + botTop + "）—— 旧代码这里恒等于 " + M + "，等于没区分上下");

            // 内容矮时：顶部角从上排，底部角贴下边（新卡片到达会把整组往上顶）
            float topContent = (float)contentTopFn.Invoke(null, new object[] { false, topTop, VH, 150f });
            float botContent = (float)contentTopFn.Invoke(null, new object[] { true, botTop, VH, 150f });
            Check(topContent == topTop, "SC 四角：内容矮时顶部角从上往下排（实得 " + topContent + "）");
            Check(Math.Abs(botContent - (botTop + (VH - 150f))) < 0.01f,
                "SC 四角：内容矮时底部角**贴着下边**（实得 " + botContent + "）");
            Check(botContent + 150f <= H - M + 0.01f,
                "SC 四角：底部角内容不会越过画面下边（内容底 " + (botContent + 150f) + " ≤ " + (H - M) + "）");

            // 内容比可视区高时两边都从 viewTop 开始（靠滚动看后面的）
            float botFull = (float)contentTopFn.Invoke(null, new object[] { true, botTop, VH, 900f });
            Check(Math.Abs(botFull - botTop) < 0.01f, "SC 四角：内容超高时底部角从 viewTop 开始滚（实得 " + botFull + "）");

            // badge 模式同理
            float badgeTop = (float)badgeYFn.Invoke(null, new object[] { false, H, M, 40f });
            float badgeBot = (float)badgeYFn.Invoke(null, new object[] { true, H, M, 40f });
            Check(badgeTop == 12f && Math.Abs(badgeBot - (H - M - 40f)) < 0.01f,
                "SC 四角：角标（badge）分上下（上 " + badgeTop + " / 下 " + badgeBot + "）");
        }

        // ---- SC 滚动阈值 + 滚动条拖动 ----
        //
        // 用户要求：「超过 6 张有个滚动条，我可以控制滚动看」。
        // 下面锁两件事：
        //   ① 阈值语义 —— **张数 > 阈值** 才收窄可视区（≤ 阈值不滚，哪怕屏幕放得下也不收）；
        //   ② 拖动映射 —— 滑块拖到轨道两端要正好对应 0 / maxScroll，中间线性。
        MethodInfo viewHeightFn = rendererType.GetMethod("ScViewHeightFor", pubStatic);
        MethodInfo barScrollFn = rendererType.GetMethod("ScBarScrollFor", pubStatic);
        Check(viewHeightFn != null && barScrollFn != null, "SC 滚动：阈值与拖动映射是纯函数（可离线验证）");
        if (viewHeightFn != null)
        {
            const float NAT = 1000f, CAP = 300f;
            float f = (float)viewHeightFn.Invoke(null, new object[] { 6, 6, NAT, CAP });
            Check(f == NAT, "SC 滚动：刚好等于阈值（6 张 / 阈值 6）**不收窄**（实得 " + f + "）");
            f = (float)viewHeightFn.Invoke(null, new object[] { 7, 6, NAT, CAP });
            Check(Math.Abs(f - CAP) < 0.01f, "SC 滚动：超过阈值（7 张 / 阈值 6）收窄到前 6 张那么高（实得 " + f + "）");
            f = (float)viewHeightFn.Invoke(null, new object[] { 12, 12, NAT, CAP });
            Check(f == NAT, "SC 滚动：12 张 / 阈值 12 不收窄（用户问过这一档）");
            f = (float)viewHeightFn.Invoke(null, new object[] { 13, 12, NAT, CAP });
            Check(Math.Abs(f - CAP) < 0.01f, "SC 滚动：13 张 / 阈值 12 收窄");
            f = (float)viewHeightFn.Invoke(null, new object[] { 50, 0, NAT, CAP });
            Check(f == NAT, "SC 滚动：阈值 0（不限）不按张数收窄（实得 " + f + "）");
            f = (float)viewHeightFn.Invoke(null, new object[] { 50, 6, NAT, 0f });
            Check(f == NAT, "SC 滚动：量不出高度时（capHeight=0）保持自然高度，不塌成 40");
        }
        if (barScrollFn != null)
        {
            // 轨道 y=100、高 400；滑块高 80 → 行程 320；最大滚动 1600
            float top = (float)barScrollFn.Invoke(null, new object[] { 100f + 40f, 40f, 100f, 400f, 80f, 1600f });
            Check(Math.Abs(top) < 0.01f, "SC 滚动条：拖到最上 = 滚动 0（实得 " + top + "）");
            float bottom = (float)barScrollFn.Invoke(null, new object[] { 100f + 320f + 40f, 40f, 100f, 400f, 80f, 1600f });
            Check(Math.Abs(bottom - 1600f) < 0.01f, "SC 滚动条：拖到最下 = 全部滚完（实得 " + bottom + "）");
            float mid = (float)barScrollFn.Invoke(null, new object[] { 100f + 160f + 40f, 40f, 100f, 400f, 80f, 1600f });
            Check(Math.Abs(mid - 800f) < 0.01f, "SC 滚动条：拖到中间 = 一半（实得 " + mid + "）");
            float over = (float)barScrollFn.Invoke(null, new object[] { 99999f, 0f, 100f, 400f, 80f, 1600f });
            Check(Math.Abs(over - 1600f) < 0.01f, "SC 滚动条：拖出轨道也夹在最大值（实得 " + over + "）");
            float none = (float)barScrollFn.Invoke(null, new object[] { 300f, 0f, 100f, 400f, 80f, 0f });
            Check(Math.Abs(none) < 0.01f, "SC 滚动条：没有可滚内容时恒为 0");
        }

        TestDanmakuPlacement();
    }
    /// 抖音「验证过了没」的判据回归。
    ///
    /// 真实坑：一开始拿 cookie 里有 `ttwid` 当成功信号，但**实测没过验证时
    /// `ttwid` / `s_v_web_id` 就已经有了** —— 会在验证码还挂在屏幕上的时候就误判成功、
    /// 拿着没用的 cookie 继续，然后再次被抓。可靠信号是**页面标题**：
    /// 风控页固定叫「验证码中间页」，过了才变成房间标题。
    private static void TestDouyinGuardPassed(string launcher)
    {
        Type guard = Assembly.LoadFrom(launcher).GetType("DouyinGuard", true);
        MethodInfo passed = guard.GetMethod("Passed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(passed != null, "抖音验证：Passed 判据存在");
        if (passed == null) return;

        Func<string, string, bool> call = delegate (string title, string cookies)
        {
            return (bool)passed.Invoke(null, new object[] { title, cookies });
        };

        Check(!call("验证码中间页", "ttwid=abc; s_v_web_id=verify_x"),
            "抖音验证：光有 ttwid 不算过（标题还是验证码中间页）");
        Check(call("某某的直播间", "ttwid=abc; __ac_signature=xyz"),
            "抖音验证：标题变成房间标题、且有 ttwid 才算过");
        Check(!call("某某的直播间", "__ac_referer=__ac_blank"),
            "抖音验证：只有 __ac_referer 不算过（实测踩过：误判成功，存下一份没用的 cookie）");
        Check(!call(null, "ttwid=abc"), "抖音验证：拿不到标题不算过");
        Check(!call("某某的直播间", ""), "抖音验证：没有 cookie 不算过");
        Check(!call("验证码中间页", ""), "抖音验证：两者都不满足不算过");
    }
    /// 登录凭据的 ACL 回归。
    ///
    /// 评估报告点出的问题：三个新平台的 cookie 只做了裸 File.WriteAllText，
    /// 继承目录 ACL（Authenticated Users 可 Modify、Users 可读），
    /// 而同机其它普通账户就能读走完整的登录凭证 —— B站 那条路早就收紧了，新平台漏了。
    /// 这里**实测**写入后的 ACL，而不是只检查代码里调没调用。
    private static void TestCredentialAcl(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type login = assembly.GetType("PlatformLogin", true);
        Type platformType = assembly.GetType("LoginPlatform", true);
        object douyu = login.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { "douyu" });
        Check(douyu != null, "凭据 ACL：能拿到 douyu 平台定义");
        if (douyu == null) return;

        string path = (string)login.GetMethod("CookiesPath",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { douyu });
        Check(path != null && path.Length > 0, "凭据 ACL：cookie 路径可解析");

        // 记下原内容，测完还原（这是用户的真实凭据，不能弄丢）
        string original = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            // ⚠️ 必须按参数类型取重载：Save 现在有两个（带不带 CDP JSON），
            //    只按名字取会 AmbiguousMatchException（2026-09-27 实测踩到）。
            object ok = login.GetMethod("Save",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new Type[] { platformType, typeof(string) }, null)
                .Invoke(null, new object[] { douyu, "ttwid=acl-test" });
            Check(ok is bool && (bool)ok, "凭据 ACL：Save 如实返回成功");
            Check(File.Exists(path), "凭据 ACL：cookie 文件确实落盘");

            var security = File.GetAccessControl(path);
            Check(security.AreAccessRulesProtected,
                "凭据 ACL：已断开继承（不再跟着目录放权）");

            WindowsIdentity me = WindowsIdentity.GetCurrent();
            bool hasOtherUsers = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (sid == null) continue;
                if (sid == me.User) continue;                                   // 自己
                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) continue; // SYSTEM
                hasOtherUsers = true;
            }
            Check(!hasOtherUsers, "凭据 ACL：除当前用户和 SYSTEM 外没有别的账户可访问");
        }
        finally
        {
            try
            {
                if (original != null) File.WriteAllText(path, original);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }
    }

    /// 域名匹配不能只看后缀：`evildouyu.com` 也必须被拒（否则 cookie 会发给别人的服务器）。
    private static void TestCookieHostMatch(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type login = assembly.GetType("PlatformLogin", true);
        MethodInfo matches = login.GetMethod("Matches",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(matches != null, "Cookie 域：Matches 存在");
        if (matches == null) return;
        Func<string, string, bool> m = delegate (string host, string domain)
        {
            return (bool)matches.Invoke(null, new object[] { host, domain });
        };
        Check(m("www.douyu.com", "douyu.com"), "Cookie 域：子域名命中");
        Check(m("douyu.com", "douyu.com"), "Cookie 域：完全相等命中");
        Check(!m("evildouyu.com", "douyu.com"), "Cookie 域：evildouyu.com 必须被拒");
        Check(!m("douyu.com.evil.com", "douyu.com"), "Cookie 域：douyu.com.evil.com 必须被拒");
        Check(!m("notdouyu.com", "douyu.com"), "Cookie 域：notdouyu.com 必须被拒");
    }
    /// 静默失败登记处（评估报告 §5.9）。
    /// 约定见 src\Danmaku\danmaku-13-diagnostics.cs 顶部：清理类允许静默，
    /// 日志类不抛但要登记，**数据持久化类必须让调用方知道**。
    private static void TestDiagnosticsMechanism(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type diag = assembly.GetType("Diagnostics", true);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo reset = diag.GetMethod("Reset", flags);
        MethodInfo report = diag.GetMethod("Report", flags);
        MethodInfo summary = diag.GetMethod("Summary", flags);
        MethodInfo countOf = diag.GetMethod("CountOf", flags);

        reset.Invoke(null, null);
        Check((string)summary.Invoke(null, null) == null, "静默失败：初始状态没有摘要（托盘不会多一行）");

        report.Invoke(null, new object[] { "测试类别", new InvalidOperationException("boom") });
        string text = (string)summary.Invoke(null, null);
        Check(text != null && text.Contains("测试类别"), "静默失败：登记后出现在摘要里");
        Check((int)countOf.Invoke(null, new object[] { "测试类别" }) == 1, "静默失败：计数为 1");

        // 同类再失败只累加，不重复刷日志（日志本身可能正好是坏的那个）
        report.Invoke(null, new object[] { "测试类别", null });
        Check((int)countOf.Invoke(null, new object[] { "测试类别" }) == 2, "静默失败：同类累加计数");
        Check(((string)summary.Invoke(null, null)).Contains("×2"), "静默失败：摘要显示次数");
        reset.Invoke(null, null);
        Check((string)summary.Invoke(null, null) == null, "静默失败：Reset 后摘要清空");
    }

    /// 空 catch 的**棘轮**：只许减，不许增。
    ///
    /// 报告指出 102 处空 catch 让故障定位变难。逐个甄别是体力活，而且**清理类空 catch 是合理的**
    /// —— 所以不追求清零，改成「不许比基线更多」：新写的代码若又用空 catch 吞掉失败，这里会红。
    /// 计数前必须**剥掉注释与字符串**（第一版没剥，把我写在注释里的示例代码也算进去了，虚高 32）。
    private static void TestSilentCatchRatchet(string launcher)
    {
        string repo = SourceRoot != null ? SourceRoot : Path.GetDirectoryName(Path.GetFullPath(launcher));
        var baseline = new Dictionary<string, int>
        {
            // ⚠️ 这里的数字**只能来自 ScanCode 的真实输出**，别再手填。
            //    2026-09-26 修正：正则剥离器一直少数（块注释会被 `"*/*"` 里的 `/*` 带跑偏，
            //    一次吃掉两万多字符），所以之前几轮标定出来的值**偏低**。
            //    换成状态机扫描后真值浮出来，这里按真值重标 —— 这是订正测量，不是放宽标准。
            { "live-hub-v2.cs", 31 },
            // 2026-10-02：主 partial 机械拆分，原有预算转移，总预算不增加。
            { "livehub-storage.cs", 3 },
            { "livehub-platform-titles.cs", 1 },
            { "livehub-douyin-room.cs", 1 },
            { "livehub-douyu-stream.cs", 1 },
            { "livehub-process-capture.cs", 2 },
            { "livehub-playback-hints.cs", 1 },
            { "livehub-launcher-ui.cs", 13 },
            { "livehub-selftest.cs", 2 },

            { "danmaku-1-config.cs", 1 },   { "danmaku-2-client.cs", 10 },
            { "danmaku-3-render.cs", 2 },   { "danmaku-4-overlay.cs", 8 },
            { "danmaku-7-emoji.cs", 3 },    { "danmaku-8-platforms.cs", 2 },
            { "danmaku-9-huya.cs", 1 },     { "danmaku-10-douyin.cs", 7 },
            { "danmaku-11-douyin-guard.cs", 4 }, { "danmaku-12-login.cs", 5 },
            // BuildInfo.Text 读自身 AssemblyInformationalVersion 那处：失败就返回「未知版本」，
            // 是合理的尽力而为（读自己的元数据，没有可采取的动作，也不值得打扰用户）。
            // 棘轮的意义就在这 —— 逼你对每一处新增做一次**明确决定**，而不是默默再添一个。
            { "danmaku-13-diagnostics.cs", 1 },
            // 2026-09-27 partial 拆分：live-hub-v2.cs 里的路径/配置解析搬到 src\App\livehub-paths.cs。
            // 那几处空 catch 是**原样搬过去**的（ExpandPath 的兜底、FindOnPath 的单条目失败、
            // 创建 runtime 目录失败不阻断启动），所以在这里按搬运后的真值登记，不是放宽标准。
            { "livehub-paths.cs", 3 },
            // 同上：B站扫码登录 + cookie/ACL 那段整块搬到 src\App\livehub-credentials.cs，空 catch 原样跟着走。
            { "livehub-credentials.cs", 2 },
            // 中转链整块（StartRelay/RunRelay/Pump/FLV/房间状态/直链解析）搬到 src\App\livehub-relay.cs，
            // 16 处空 catch 是**原样跟着搬**的（清 runtime 文件、探测失败、写状态文件失败等都不该影响播放）。
            { "livehub-relay.cs", 16 },
            // 平台适配器：CheckRoomState 的四段实现搬进 src\App\livehub-platforms.cs，
            // 其中虎牙那段「认不出开播状态时写一条排查日志」里的空 catch 原样跟着走（1 处）。
            { "livehub-platforms.cs", 1 }
        };
        var files = new List<string>();
        string main = Path.Combine(repo, "live-hub-v2.cs");
        if (File.Exists(main)) files.Add(main);
        string danmakuDir = Path.Combine(repo, "src", "Danmaku");
        if (Directory.Exists(danmakuDir)) files.AddRange(Directory.GetFiles(danmakuDir, "*.cs"));
        string appDir = Path.Combine(repo, "src", "App");
        if (Directory.Exists(appDir)) files.AddRange(Directory.GetFiles(appDir, "*.cs"));
        Check(files.Count > 0, "空catch棘轮：能找到源码（否则这个测试是空转的）");
        if (files.Count == 0) return;

        int baselineTotal = 0;
        foreach (KeyValuePair<string, int> pair in baseline) baselineTotal += pair.Value;
        int total = 0;
        var grown = new List<string>();
        foreach (string file in files)
        {
            string text = StripCommentsAndStrings(File.ReadAllText(file));
            int n = Regex.Matches(text, @"catch\s*(\([^)]*\))?\s*\{\s*\}").Count;
            total += n;
            string name = Path.GetFileName(file);
            int allowed;
            if (!baseline.TryGetValue(name, out allowed)) allowed = 0;   // 新文件不允许有空 catch
            if (n > allowed) grown.Add(name + " " + allowed + "→" + n);
        }
        Check(grown.Count == 0,
            "空catch棘轮：没有任何文件超过基线" + (grown.Count > 0 ? "（超出：" + string.Join(", ", grown.ToArray()) + "）" : ""));
        // 基线总数从字典算出来，别写死 —— 写死的数字迟早和实际对不上（文档里刚踩过这个坑）。
        Console.WriteLine("      空 catch 合计 " + total + " 处（基线 " + baselineTotal + "，只许减）");
    }

    /// 剥掉块注释 / 行注释 / 字符串字面量 —— 否则注释里写的示例代码会被当成真的 catch。
    /// B站 格式选择串的回归（2026-09-25 实测修的两个 bug）。
    ///
    /// ① 卡顿根因：原来 mpv 那条路会挑中 `source-7` = m3u8+fmp4+hevc。
    ///    m3u8 是**分片**投递，片间有天然空档 —— 实测 60 秒里缓冲最低值掉到 0 秒。
    ///    必须优先 `[ext=flv]`（↔ https 连续流）+ `[vcodec^=avc]`（H.264 硬解面广）。
    /// ② 画质档位原来用 `[height<=720]`，而 B站 直播流 height 全是 NA
    ///    → `Requested format is not available`，**流畅/高清两档直接播不了**。
    ///    改成按 format_id 前缀挑。
    private static void TestBiliFormatSelector(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type hub = assembly.GetType("LiveHub", true);
        BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo format = hub.GetMethod("BiliFormat", anyStatic);
        Check(format != null, "B站格式：BiliFormat 存在");
        if (format == null) return;
        // Target 是 LiveHub 里的**嵌套私有类**（全名 LiveHub+Target），
        // 直接 GetType("Target") 会 TypeLoadException。用 LiveHub.Parse 拿实例更稳，
        // 顺便还顺带验证了 Parse 本身能跑。
        MethodInfo parse = hub.GetMethod("Parse", anyStatic);
        Check(parse != null, "B站格式：LiveHub.Parse 存在");
        if (parse == null) return;

        foreach (string quality in new string[] { "smooth", "high", "source" })
        {
            object target = parse.Invoke(null, new object[] { "7777", "bili" });
            Check(target != null, "B站格式[" + quality + "]：Parse 出 Target");
            if (target == null) continue;
            target.GetType().GetField("Quality",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, quality);
            string chain = (string)format.Invoke(null, new object[] { target, false });
            Check(!string.IsNullOrEmpty(chain), "B站格式[" + quality + "]：能拿到选择串");
            if (string.IsNullOrEmpty(chain)) continue;

            Check(chain.IndexOf("[height<=") < 0,
                "B站格式[" + quality + "]：不能用 [height<=]（B站直播流 height 是 NA，会选不出来）");
            Check(chain.IndexOf("[ext=flv][vcodec^=avc]") >= 0,
                "B站格式[" + quality + "]：优先连续投递 + H.264");
            int flvPos = chain.IndexOf("[ext=flv]");
            int fallbackPos = chain.LastIndexOf("/best");
            Check(flvPos >= 0 && flvPos < fallbackPos,
                "B站格式[" + quality + "]：flv 优先，/best 只作兜底");
            Check(chain.EndsWith("/best"),
                "B站格式[" + quality + "]：末尾必须留 /best 兜底（只提供 HLS 时也得能播）");
        }
    }
    /// 画质/同步增强的配置契约（2026-09-25 新增）。
    ///
    /// A. 弹幕延迟补偿：弹幕实时到、视频缓冲后才显示，不补偿就领先画面 2.5~4 秒 = 剧透。
    /// B. mpv 画质增强：--vo=gpu-next,gpu --profile=high-quality（mpv 侧的 madVR 等价物）。
    /// 两条都必须**默认不影响原行为**（延迟 0、增强关），并夹在合理范围内 ——
    /// 这类「顺手加上的开关」最容易变成默认打开的惊吓。
    private static void TestEnhanceConfig(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        Type cfg = assembly.GetType("DanmakuConfig", true);
        object o = Activator.CreateInstance(cfg);
        BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Check((int)cfg.GetField("DanmakuDelayMs", anyInst).GetValue(o) == 0,
            "增强配置：弹幕延迟默认 0（不改变原行为）");
        Check((string)cfg.GetField("MpvQuality", anyInst).GetValue(o) == "off",
            "增强配置：mpv 画质档位默认 off（不改变原行为）");

        // 三档只认 off/light/high（挡掉手改配置文件写进去的乱值），
        // 且老配置的布尔开关要能迁移到 high —— 否则以前开过「画质增强」的人升级后会被静默关掉。
        MethodInfo norm = cfg.GetMethod("Normalize", anyInst);
        string[,] cases = new string[,] {
            { "off", "off" }, { "light", "light" }, { "high", "high" },
            { "ultra", "off" }, { "", "off" }, { "HIGH", "off" } };
        for (int i = 0; i < cases.GetLength(0); i++)
        {
            object fresh = Activator.CreateInstance(cfg);
            cfg.GetField("MpvQuality", anyInst).SetValue(fresh, cases[i, 0]);
            norm.Invoke(fresh, null);
            string got = (string)cfg.GetField("MpvQuality", anyInst).GetValue(fresh);
            Check(got == cases[i, 1],
                "增强配置：档位 \"" + cases[i, 0] + "\" → " + cases[i, 1] + "（实得 " + got + "）");
        }
        object legacy = Activator.CreateInstance(cfg);
        cfg.GetField("MpvEnhance", anyInst).SetValue(legacy, true);   // 老配置：布尔开关开着
        norm.Invoke(legacy, null);
        Check((string)cfg.GetField("MpvQuality", anyInst).GetValue(legacy) == "high",
            "增强配置：老布尔开关 true 迁移到 high（不让老用户升级后被静默关掉）");

        // 夹取：负值没意义，超过 15 秒弹幕就完全对不上画面（实测缓冲 2.6~3.9 秒）
        MethodInfo normalize = cfg.GetMethod("Normalize", anyInst);
        int[] inputs = new int[] { -5000, 0, 2600, 15000, 99999 };
        int[] expect = new int[] { 0, 0, 2600, 15000, 15000 };
        for (int i = 0; i < inputs.Length; i++)
        {
            object fresh = Activator.CreateInstance(cfg);
            cfg.GetField("DanmakuDelayMs", anyInst).SetValue(fresh, inputs[i]);
            normalize.Invoke(fresh, null);
            int got = (int)cfg.GetField("DanmakuDelayMs", anyInst).GetValue(fresh);
            Check(got == expect[i], "增强配置：延迟 " + inputs[i] + " 夹到 " + expect[i] + "（实得 " + got + "）");
        }

        // 热键规格必须能被解析器认出来（ParseHotkey 原来只认字母/数字/F 键/方向键，
        // 方括号是这轮为「延迟加减」专门加的）
        Type form = assembly.GetType("DanmakuOverlayForm", true);
        MethodInfo parse = form.GetMethod("ParseHotkey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(parse != null, "增强配置：ParseHotkey 存在");
        if (parse != null)
        {
            foreach (string spec in new string[] { "Ctrl+Alt+[", "Ctrl+Alt+]" })
            {
                object[] args = new object[] { spec, (uint)0, (uint)0 };
                bool ok = (bool)parse.Invoke(null, args);
                Check(ok && (uint)args[2] != 0, "增强配置：热键 " + spec + " 能被解析");
            }
        }
    }
    private static string StripCommentsAndStrings(string text)
    {
        // ⚠️ 顺序有讲究：**先字符串、后注释**。
        //
        // 2026-09-26 实测踩到：原来把行注释放在字符串前面，而代码里只要有
        // `"https://..."` 这种**字符串里含 `//`** 的常量，那半截 URL 就会被当成行注释切掉，
        // 留下一个不配对的引号；接着字符串正则会从那个孤儿引号一路匹配到下一处引号，
        // **把中间的真实代码整段吃掉** —— 于是空 catch 被漏数。
        //
        // 后果是棘轮长期偏低：实测修顺序后合计 75 → 98（少算了 23 处），
        // 基线也据此重新标定。当时是在给虎牙加表情字典时暴露的
        // （常量 HuyaEmoticonBase 里正好有 `//`）。
        //
        // ⚠️ 2026-09-26 **第三次**被同一类问题咬到，这次直接换掉正则方案。
        //    正则在「谁先剥」上永远有反例：
        //      · 先剥注释 → 字符串里的 `//`（`"https://x"`）被当行注释
        //      · 先剥字符串 → 注释里的引号（`// 他说 "你好`）让字符串段失控
        //      · 块注释更是重灾区：`request.Accept = "*/*"` 里的 `/*` **先于** `*/` 出现，
        //        于是块注释从这里一路吃到**文件后面很远**的 `*/` —— 实测一次吃掉 25,474 字符，
        //        整个棘轮少数 17 处。
        //    改成一次性状态机扫描：逐字符判断「在字符串里 / 在块注释里 / 在行注释里」，
        //    没有先后顺序问题，也没有正则的边界歧义。
        return ScanCode(text);
    }

    /// 剥掉注释与字符串字面量，**一次扫描**按状态走。
    /// 注释和字符串都替换成等长的空白，保证偏移与原文一致（排查时能对上行号）。
    private static string ScanCode(string text)
    {
        StringBuilder output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // 行注释：吃到行尾（保留换行，避免把两行粘成一行）
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') { output.Append(' '); i++; }
                continue;
            }
            // 块注释：吃到配对的 */（没配对就吃到结尾，不会跑到别处找）
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                output.Append("  "); i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    output.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { output.Append("  "); i += 2; }
                continue;
            }
            // 逐字字符串 @"..."：内部 "" 表示一个引号
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            {
                output.Append("\"\""); i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { output.Append("  "); i += 2; continue; }
                        break;
                    }
                    output.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { output.Append('"'); i++; }
                continue;
            }
            // 普通字符串：\" 是转义
            if (c == '"')
            {
                output.Append("\"\""); i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { output.Append("  "); i += 2; continue; }
                    if (text[i] == '"') break;
                    output.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { output.Append('"'); i++; }
                continue;
            }

            output.Append(c);
            i++;
        }
        return output.ToString();
    }
    private static string ToHex(byte[] data)
    {
        StringBuilder builder = new StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; i++) builder.Append(data[i].ToString("x2"));
        return builder.ToString();
    }
    private static void TestDouyuStt(string launcher)
    {
        // 测试程序是**独立编译**的，只通过反射访问 launcher —— 不能直接写类型名
        Type douyu = Assembly.LoadFrom(launcher).GetType("DouyuDanmakuClient", true);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo get = douyu.GetMethod("SttGet", flags);
        MethodInfo unescape = douyu.GetMethod("Unescape", flags);
        MethodInfo intOf = douyu.GetMethod("IntOf", flags);
        Func<string, string, string> SttGet = delegate (string body, string key)
        { return (string)get.Invoke(null, new object[] { body, key }); };

        const string Inner = "type@A=chatmsg@Srid@A=9999@Suid@A=123@Snn@A=测试用户@Stxt@A=这是一条高能弹幕@Slevel@A=30@Sbnn@A=小僵尸@Sbl@A=12@Scid@A=abc@S";
        const string Wire = "btype@=voiceDanmu/chatmsg@=" + Inner
            + "/cprice@=3000/crealPrice@=3000/cet@=60/now@=1790161261383/vrid@=1856171486511906816/rid@=9999/type@=comm_chatmsg/";

        Check(SttGet(Wire, "type") == "comm_chatmsg", "斗鱼：type 不在首字段时也能取到（comm_chatmsg 的坑）");
        Check(SttGet(Wire, "btype") == "voiceDanmu", "斗鱼：btype 取到 voiceDanmu（= 高能弹幕 / SC 的判据）");
        Check(SttGet(Wire, "cprice") == "3000", "斗鱼 SC：cprice 取到 3000（分）");
        Check(SttGet(Wire, "cet") == "60", "斗鱼 SC：cet 取到 60（秒）");
        Check((int)intOf.Invoke(null, new object[] { SttGet(Wire, "cprice") }) / 100 == 30,
            "斗鱼 SC：cprice 分 → 元 换算正确（3000 分 = 30 元）");

        string inner = SttGet(Wire, "chatmsg");
        Check(inner != null && inner.IndexOf("/nn@=测试用户/", StringComparison.Ordinal) >= 0,
            "斗鱼 SC：嵌套 chatmsg 解一层后是合法 STT（/ 与 @ 都还原了）");
        Check(SttGet(inner, "txt") == "这是一条高能弹幕", "斗鱼 SC：内层 txt = 留言正文");
        Check(SttGet(inner, "nn") == "测试用户", "斗鱼 SC：内层 nn = 昵称");

        Check((string)unescape.Invoke(null, new object[] { "a@Sb@Ac" }) == "a/b@c",
            "斗鱼：反转义顺序正确（先 @S 后 @A）");
        Check((string)unescape.Invoke(null, new object[] { "x@ASy" }) == "x@Sy",
            "斗鱼：原文里的 @S 经一轮编解码后不变（@A 保护生效）");
        Check(SttGet("k@=a@Sb/c@=d/", "k") == "a/b", "斗鱼：值里转义的 / 不会被当成字段分隔符");
        Check(SttGet("xcet@=999/cet@=60/", "cet") == "60", "斗鱼：键匹配按字段边界（xcet 不会命中 cet）");
    }    /// 用反射验证 EmojiSheet：区段查表、变体选择符、ZWJ 整串、键帽、肤色修饰符。
    /// 平台表情字典（`[大笑]` 这类方括号代码 → 图片）。
    ///
    /// 为什么值得一条独立测试：用户报的现象是「弹幕里的 [汤圆]/[大笑] 没显示成图片」。
    /// 根因是**文本里夹代码**要靠客户端查字典换成图 —— 这跟 dmType==1 的「大表情弹幕」
    /// （服务端直接给 URL）是两套机制。只加个字段是没用的，必须验证**渲染结果真的变了**。
    /// 虎牙固定表情表（`[笑哭]` 这类）。
    ///
    /// 映射是从官方页面的 JS 里抽出来的（2026-09-26）：
    ///   name = "[" + text + "]"；url = "//a.msstatic.com/huya/main/emot_png/" + code + ".png"
    /// 触发证据是真实弹幕 `可惜[笑哭]`（虎牙 333003，实测 3/2319）。
    /// 抽查 4 个 URL 均 HTTP 200 / image/png，所以这里锁的是**映射正确性**，不重复验网络。
    /// 斗鱼固定表情表。映射从官方前端 JS 抽出（2026-09-26），触发样本是真实弹幕
    /// `[顽皮]`（房间 100）和 `[梗已老实]`（房间 36252）。
    /// 抽查 6 个 URL 均 HTTP 200 / image/png，所以这里锁映射、不重复验网络。
    private static void TestDouyuEmoticons(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type douyu = assembly.GetType("DouyuDanmakuClient", true);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            string[] pairs = (string[])douyu.GetField("DouyuEmoticonPairs", flags).GetValue(null);
            Check(pairs != null && pairs.Length == 250,
                "斗鱼表情：固定表有 250 条（实得 " + (pairs == null ? -1 : pairs.Length) + "）");
            if (pairs == null) return;

            bool hasWanpi = false;
            for (int i = 0; i < pairs.Length; i++) if (pairs[i].IndexOf("顽皮:") == 0) hasWanpi = true;
            Check(hasWanpi, "斗鱼表情：表里有 顽皮（真实弹幕样本里出现的那个）");

            Type dictType = assembly.GetType("EmoticonDictionary", true);
            object dict = Activator.CreateInstance(dictType);
            douyu.GetMethod("FillDouyuEmoticons", flags).Invoke(null, new object[] { dict });

            Type[] getArgs = new Type[] { typeof(string), typeof(string).MakeByRefType() };
            MethodInfo get = dictType.GetMethod("TryGet", getArgs);
            object[] hit = new object[] { "[顽皮]", null };
            Check((bool)get.Invoke(dict, hit), "斗鱼表情：[顽皮] 能查到");
            Check((string)hit[1] == "https://shark2.douyucdn.cn/front-publish/live-next-player-aside-master/assets/images/wanpi_20e1a62.png",
                "斗鱼表情：URL 拼接正确");
            object[] miss = new object[] { "[不存在]", null };
            Check(!(bool)get.Invoke(dict, miss), "斗鱼表情：没登记的不瞎认");
            // 实测服务端会下发**表里没有**的新表情（[梗已老实]），必须优雅退化成文本而不是报错
            object[] unknown = new object[] { "[梗已老实]", null };
            Check(!(bool)get.Invoke(dict, unknown),
                "斗鱼表情：服务端新表情不在表里时查不到（退回文本显示，不崩）");
        }
        catch (Exception ex)
        {
            Check(false, "斗鱼表情测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// 抖音表情字典的**解析**逻辑。用真实响应片段（从实测的 214 条里摘 3 条，字段逐字保留），
    /// 不联网 —— 否则这条路径永远测不到。
    ///
    /// 实测触发率是四个平台里最高的：`[捂脸]` 15/96 ≈ 15.6%
    /// （B站 0.7% / 虎牙 0.09% / 斗鱼约 0.5%），所以这个解析尤其值得锁住。
    private static void TestDouyinEmoticons(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type douyin = assembly.GetType("DouyinDanmakuClient", true);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            Type dictType = assembly.GetType("EmoticonDictionary", true);
            object dict = Activator.CreateInstance(dictType);

            // 真实响应片段（2026-09-26 实测 https://www.douyin.com/aweme/v1/web/emoji/list/）
            const string sample = "{\"emoji_list\":["
                + "{\"origin_uri\":\"weixiao.png\",\"display_name\":\"[微笑]\",\"hide\":0,"
                + "\"emoji_url\":{\"uri\":\"tos-cn-i-tsj2vxp0zn/54a0f69dc150401a85ba8c20c1a05db1\","
                + "\"url_list\":[\"https://p3-pc-sign.douyinpic.com/obj/tos-cn-i-tsj2vxp0zn/54a0f69dc150401a85ba8c20c1a05db1?lk3s=x\","
                + "\"https://p9-pc-sign.douyinpic.com/obj/tos-cn-i-tsj2vxp0zn/54a0f69dc150401a85ba8c20c1a05db1?lk3s=y\"]}},"
                + "{\"origin_uri\":\"wu_lian.png\",\"display_name\":\"[捂脸]\",\"hide\":0,"
                + "\"emoji_url\":{\"uri\":\"tos-cn-i-tsj2vxp0zn/wulian\",\"url_list\":[\"https://p3-pc-sign.douyinpic.com/obj/wulian?lk3s=z\"]}},"
                + "{\"display_name\":\"[没有图片]\",\"hide\":0,\"emoji_url\":{\"uri\":\"x\",\"url_list\":[]}}"
                + "],\"status_code\":0}";

            douyin.GetMethod("FillDouyinEmoticons", flags).Invoke(null, new object[] { dict, sample });

            Type[] getArgs = new Type[] { typeof(string), typeof(string).MakeByRefType() };
            MethodInfo get = dictType.GetMethod("TryGet", getArgs);
            object[] hit = new object[] { "[捂脸]", null };
            Check((bool)get.Invoke(dict, hit), "抖音表情：[捂脸] 能查到（实测最常见的一个）");
            Check((string)hit[1] == "https://p3-pc-sign.douyinpic.com/obj/wulian?lk3s=z",
                "抖音表情：取 url_list[0]（实得 " + hit[1] + "）");

            object[] hit2 = new object[] { "[微笑]", null };
            Check((bool)get.Invoke(dict, hit2), "抖音表情：[微笑] 能查到（真实响应里的第一条）");
            Check((string)hit2[1] == "https://p3-pc-sign.douyinpic.com/obj/tos-cn-i-tsj2vxp0zn/54a0f69dc150401a85ba8c20c1a05db1?lk3s=x",
                "抖音表情：多条 url_list 时取第一条（不是第二条）");

            // url_list 为空的条目必须被跳过，不能塞个空 URL 进去（否则渲染时会去下空地址）
            object[] empty = new object[] { "[没有图片]", null };
            Check(!(bool)get.Invoke(dict, empty), "抖音表情：url_list 为空的条目被跳过（不塞空 URL）");
        }
        catch (Exception ex)
        {
            Check(false, "抖音表情测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }
    private static void TestHuyaEmoticons(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type huya = assembly.GetType("HuyaDanmakuClient", true);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            string[] pairs = (string[])huya.GetField("HuyaEmoticonPairs", flags).GetValue(null);
            Check(pairs != null && pairs.Length == 39,
                "虎牙表情：固定表有 39 条（实得 " + (pairs == null ? -1 : pairs.Length) + "）");
            if (pairs == null) return;

            bool hasXk = false;
            for (int i = 0; i < pairs.Length; i++) if (pairs[i] == "笑哭:xk") hasXk = true;
            Check(hasXk, "虎牙表情：表里有 笑哭:xk（真实弹幕样本里出现的那个）");

            Type dictType = assembly.GetType("EmoticonDictionary", true);
            object dict = Activator.CreateInstance(dictType);
            huya.GetMethod("FillHuyaEmoticons", flags).Invoke(null, new object[] { dict });

            Type[] getArgs = new Type[] { typeof(string), typeof(string).MakeByRefType() };
            MethodInfo get = dictType.GetMethod("TryGet", getArgs);
            object[] hit = new object[] { "[笑哭]", null };
            Check((bool)get.Invoke(dict, hit), "虎牙表情：[笑哭] 能查到");
            Check((string)hit[1] == "https://a.msstatic.com/huya/main/emot_png/xk.png",
                "虎牙表情：URL 拼接正确（实得 " + hit[1] + "）");

            object[] hit2 = new object[] { "[大笑]", null };
            Check((bool)get.Invoke(dict, hit2), "虎牙表情：[大笑] 能查到（danmaku-echo 文档里的例子）");

            object[] miss = new object[] { "[不存在]", null };
            Check(!(bool)get.Invoke(dict, miss), "虎牙表情：没登记的不瞎认");
        }
        catch (Exception ex)
        {
            Check(false, "虎牙表情测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// 「长时间没有收到任何帧」的观测判据。
    ///
    /// 它**只打日志、不参与任何行为决策**（绝不因为「静默」就去重连 ——
    /// 「安静房间」和「断线」在数据层面无法区分）。但判据本身要锁死：
    /// 这里踩过一次坑 —— `lastWarn` 初始化成「现在」，把**第一次**告警推到了
    /// RepeatSeconds 之后，实测 200 秒的静默夹具一条日志都没有。
    private static void TestSilenceWatch(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type watch = assembly.GetType("SilenceWatch", true);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo should = watch.GetMethod("ShouldWarn", flags);
            MethodInfo describe = watch.GetMethod("Describe", flags);
            Check(should != null && describe != null, "静默观测：判据函数存在（否则这条测试是空转的）");
            if (should == null || describe == null) return;

            DateTime now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
            object[] call = new object[3];

            call[0] = 89 * 1000; call[1] = null; call[2] = now;
            Check(!(bool)should.Invoke(null, call), "静默观测：静默 89 秒不报（阈值 90）");

            call[0] = 90 * 1000; call[1] = null; call[2] = now;
            Check((bool)should.Invoke(null, call), "静默观测：静默 90 秒要报 —— 而且**第一次**就该报（null = 从没报过）");

            call[0] = 200 * 1000; call[1] = now.AddSeconds(-100); call[2] = now;
            Check(!(bool)should.Invoke(null, call), "静默观测：刚报过（100 秒前）不重复刷屏");

            call[0] = 200 * 1000; call[1] = now.AddSeconds(-400); call[2] = now;
            Check((bool)should.Invoke(null, call), "静默观测：超过 5 分钟再记一笔");

            string text = (string)describe.Invoke(null, new object[] { "虎牙", 123000, 456 });
            Check(text != null && text.Contains("123 秒") && text.Contains("456 帧") && text.Contains("仅观测"),
                "静默观测：文案带秒数 / 帧数 / 「仅观测」声明（实得 " + text + "）");
        }
        catch (Exception ex)
        {
            Check(false, "静默观测测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// 弹幕 / SC 的字体：随包的 HarmonyOS Sans SC。
    ///
    /// **判据是「渲染出来的位图宽度和微软雅黑不同」**，不是「配置字段写对了」。
    /// 原因：`new Font("不存在的字体", ...)` **不会抛异常**、`Font.Name` 也会原样
    /// 返回请求的名字（2026-09-26 实测），字体没加载时会**静默回退**成默认字体，
    /// 从配置和异常里都看不出来。只有真渲染一次才能证明它在起作用。
    ///
    /// ⚠️ 测量文本必须掺 ASCII：汉字在两个字库里**都是全角 1em 宽**，
    ///    拿纯中文比宽度会得到「完全一样」的假结论（我第一版就这么被骗过一次）。
    private static void TestDanmakuFont(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ① 注册 exe 旁边 fonts\ 里的字体（和程序启动时做的事一致）
        Type uiFonts = assembly.GetType("UiFonts", true);
        string fontDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(launcher)), "fonts");
        uiFonts.GetMethod("Register", flags).Invoke(null, new object[] { fontDir });
        bool available = (bool)uiFonts.GetField("Available", flags).GetValue(null);
        Check(available, "字体：exe 旁边的 fonts\\ 里能注册上 HarmonyOS Sans SC");

        // ② 真渲染一次，和微软雅黑比宽度
        Type cacheType = assembly.GetType("DanmakuTextCache", true);
        Type[] argTypes = new Type[] { typeof(string), typeof(Color), typeof(float),
            typeof(string), typeof(bool), typeof(int), typeof(bool),
            assembly.GetType("EmoticonDictionary"),
            typeof(Func<,>).MakeGenericType(typeof(string), assembly.GetType("SharedBitmap", true)) };
        MethodInfo render = cacheType.GetMethod("Render", argTypes);
        if (render == null) { Check(false, "字体：找不到 DanmakuTextCache.Render"); return; }

        const string probe = "弹幕Font测试ABC123";
        int harmonyWidth, yaheiWidth;
        using (Bitmap b = (Bitmap)render.Invoke(null, new object[] { probe, Color.White, 30f,
            "HarmonyOS Sans SC", true, 2, true, null, null })) harmonyWidth = b.Width;
        using (Bitmap b = (Bitmap)render.Invoke(null, new object[] { probe, Color.White, 30f,
            "Microsoft YaHei UI", true, 2, true, null, null })) yaheiWidth = b.Width;

        Check(harmonyWidth != yaheiWidth,
            "字体：弹幕渲染真的走了 HarmonyOS（鸿蒙 " + harmonyWidth + "px ≠ 雅黑 " + yaheiWidth + "px）");

        // ③ 兜底：字体加载失败时必须落到一个真实存在的族名，不能是空串或垃圾值
        Type cfgType = assembly.GetType("DanmakuConfig", true);
        object cfg = Activator.CreateInstance(cfgType);
        string family = (string)cfgType.GetField("FontFamily").GetValue(cfg);
        Check(!string.IsNullOrEmpty(family), "字体：FontFamily 不会为空（实际 " + family + "）");
    }

    /// 表情图片是**异步下载**的 —— 首字出现的表情不能在文字兜底上永久卡住。
    ///
    /// 用户反馈「`[虎年]` 还是显示成文字」，根因就在这：
    /// 位图只在弹幕**创建时**渲染一次、存到弹幕对象上，之后每帧只 DrawImage（性能设计）。
    /// 而创建那一刻图片通常**还没下载完** → 位图被画成字面量 `[虎年]` → **永远不会重画**。
    /// 所以常见表情（第二次出现时缓存已有）看起来正常，**首次出现**的全部是文字。
    ///
    /// 这条锁住修复所依赖的机制：
    ///   ① 能问出「这条文本里还有图没到位」（HasPendingImage）
    ///   ② 图到位后重取 `Get` 会拿到**不同的**位图（就绪指纹进了 cache key）
    private static void TestEmoticonLateImage(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance;

        Type cacheType = assembly.GetType("DanmakuTextCache", true);
        Type dictType = assembly.GetType("EmoticonDictionary", true);
        object cache = Activator.CreateInstance(cacheType);
        object dict = Activator.CreateInstance(dictType);
        dictType.GetMethod("Add", flags).Invoke(dict, new object[] { "[虎年]", "http://example/hu.png" });
        cacheType.GetField("Emoticons", flags).SetValue(cache, dict);

        const string text = "冲[虎年]";
        string family = "Microsoft YaHei UI";

        Type sharedType = assembly.GetType("SharedBitmap", true);
        // 第一趟：图片**还没下载好**（EmoticonImage 返回 null，和真实首帧一样）
        cacheType.GetField("EmoticonImage", flags).SetValue(cache,
            MakeSharedLookup(sharedType, delegate (string url) { return null; }));

        bool pendingFirst = (bool)cacheType.GetMethod("HasPendingImage", flags)
            .Invoke(cache, new object[] { text, family });
        Check(pendingFirst, "表情补画：图没到位时能问出「还有图没下载好」");

        MethodInfo get = cacheType.GetMethod("Get", flags);
        object fallbackHandle = get.Invoke(cache, new object[] { text, Color.White, 30f, family, true, 2 });
        Bitmap fallback = BitmapOf(fallbackHandle);

        // 第二趟：图片到了
        // ⚠️ 每次查都返回**新**位图：所有权模型里「谁借出谁负责还」，还掉就 Dispose。
        //    原来这里复用同一张 arrived，被还掉之后再拿就是「参数无效」（2026-09-27 实测踩到）。
        cacheType.GetField("EmoticonImage", flags).SetValue(cache,
            MakeSharedLookup(sharedType, delegate (string url) { return new Bitmap(60, 60); }));

        bool pendingAfter = (bool)cacheType.GetMethod("HasPendingImage", flags)
            .Invoke(cache, new object[] { text, family });
        Check(!pendingAfter, "表情补画：图到位后 HasPendingImage 变 false（补画时机判据）");

        object paintedHandle = get.Invoke(cache, new object[] { text, Color.White, 30f, family, true, 2 });
        Bitmap painted = BitmapOf(paintedHandle);
        Check(!object.ReferenceEquals(fallback, painted),
            "表情补画：图到位后重取会拿到**不同的**位图（" + fallback.Width + "px → " + painted.Width + "px）");
        Check(painted.Width < fallback.Width,
            "表情补画：换成图片后位图变窄（文字 '" + text + "' " + fallback.Width
            + "px → 图片 " + painted.Width + "px）—— 方向别猜，实测就是变窄");

        // 借出来的都要还（不还就是 GDI 泄漏 —— 所有权模型里这是唯一要人工守的规矩）
        ReleaseShared(fallbackHandle);
        ReleaseShared(paintedHandle);
    }

    /// 抖音的表情图是 **WebP**，而 `System.Drawing` 不支持 WebP。
    ///
    /// 用户反馈「抖音那没弄好，还是有方框」。根因：`emoji/list` 给的 `url_list[0]`
    /// 实测 `Content-Type: image/webp`、前 4 字节 `RIFF` —— `Image.FromStream`
    /// 拿到它会抛 `ArgumentException: 参数无效`，而那一处 catch 是**空的**，
    /// 于是整条下载路径**每次静默失败**，抖音的表情一个都显示不出来
    /// （B站 的图是真 PNG，所以不受影响 —— 这就是「只有抖音不行」的原因）。
    ///
    /// 修法（2026-09-27，评估报告 §1）：**分层** ——
    ///   ① 内置 VP8L 无损解码器（纯托管、零依赖）：抖音的表情图实测就是 VP8L，
    ///      所以「没装 WebP 图像扩展的 Windows」也能显示表情；
    ///   ② 系统 WIC：有损 VP8 才需要它。
    /// 原来这里把「Windows 10 1809+ 自带 Webp Decoder」当硬前提 —— 那是**错的**
    /// （它是商店扩展），报告在干净环境实测到 0x88982F8B 且只静默失败。
    ///
    /// 本文件同时锁三件事：
    ///   · 真实样本 + 语料**逐像素**（RGBA 的 SHA-256 对真值）；
    ///   · **系统能力缺失时必须明确告知**（不是只写日志）；
    ///   · 本机有 WIC 时，与内置解码器**交叉对照**（两套独立实现的像素必须一致）。
    private static void TestWebpDecode(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type webp = assembly.GetType("WebpImage", true);

        // ① 魔数识别（纯合成字节，不依赖样本文件）
        byte[] riff = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0 };
        Check((bool)webp.GetMethod("LooksLikeWebp", flags).Invoke(null, new object[] { riff }),
            "WebP：RIFF....WEBP 能被识别");
        byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0 };
        Check(!(bool)webp.GetMethod("LooksLikeWebp", flags).Invoke(null, new object[] { png }),
            "WebP：PNG 不会被误判成 WebP");

        // ② ★真解码：拿真实样本走一遍
        // 样本可能在两个地方：仓库根（--src）或 exe 旁边（构建脚本会拷过去）。两处都试。
        Type lossless = assembly.GetType("WebpLossless", true);
        Check(lossless != null, "WebP：内置 VP8L 解码器随程序编译进来（不再依赖系统组件）");

        string fixture = FindWebpFixture(launcher, "tests", "fixtures", "douyin-emoticon.webp");
        if (fixture == null)
        {
            Check(false, "WebP：找不到测试样本 tests\\fixtures\\douyin-emoticon.webp");
            return;
        }
        byte[] data = File.ReadAllBytes(fixture);
        Check((bool)webp.GetMethod("LooksLikeWebp", flags).Invoke(null, new object[] { data }),
            "WebP：真实抖音样本被识别为 WebP（" + data.Length + " 字节）");
        Check((bool)lossless.GetMethod("LooksLikeLossless", flags).Invoke(null, new object[] { data }),
            "WebP：真实抖音样本是 VP8L 无损（内置解码器覆盖的就是这一类）");

        object decoded = webp.GetMethod("Decode", flags).Invoke(null, new object[] { data });
        Check(decoded is Bitmap, "WebP：真实样本能解成 Bitmap（System.Drawing 直接解会抛 ArgumentException）");
        if (decoded is Bitmap)
        {
            using (Bitmap bmp = (Bitmap)decoded)
            {
                Check(bmp.Width == 96 && bmp.Height == 96,
                    "WebP：解出来尺寸正确（实得 " + bmp.Width + "x" + bmp.Height + "，样本是 96x96）");
                Color sample = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
                Check(sample.A > 0 || bmp.Width > 0, "WebP：解出来的位图有像素内容");
            }
        }
        Check(Convert.ToString(webp.GetField("ActiveBackend", flags).GetValue(null)) == "vp8l-managed",
            "WebP：VP8L 走的是内置解码器（实得 " + webp.GetField("ActiveBackend", flags).GetValue(null) + "）");

        // ③ ★语料逐像素：真值 = Pillow/libwebp 解出的 RGBA 字节的 SHA-256
        TestWebpCorpus(launcher, webp, flags);

        // ④ 系统能力缺失时必须**明确告知**（报告 §1：不能只在日志里静默失败）
        TestWebpUnavailableIsLoud(webp, flags);

        // ⑤ 本机有 WIC 时：两套独立实现交叉对照（像素必须一致）
        TestWebpCrossCheckWic(launcher, webp, flags);
    }

    /// 在「仓库根（--src）」或「exe 旁边」两处找样本，找不到返回 null。
    private static string FindWebpFixture(string launcher, params string[] parts)
    {
        string[] roots = new string[]
        {
            SourceRoot != null ? SourceRoot : Path.GetDirectoryName(Path.GetFullPath(launcher)),
            Path.GetDirectoryName(Path.GetFullPath(launcher))
        };
        foreach (string root in roots)
        {
            if (root == null) continue;
            string path = root;
            foreach (string part in parts) path = Path.Combine(path, part);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// 把位图按 RGBA 顺序取字节（和 Pillow 的 `convert('RGBA').tobytes()` 同一套顺序）。
    private static byte[] WebpRgbaBytes(Bitmap bitmap)
    {
        Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        System.Drawing.Imaging.BitmapData locked = bitmap.LockBits(rect,
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] raw = new byte[locked.Stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, raw, 0, raw.Length);
            byte[] rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int src = y * locked.Stride + x * 4;   // 内存序 B,G,R,A
                    int dst = (y * bitmap.Width + x) * 4;
                    rgba[dst] = raw[src + 2];
                    rgba[dst + 1] = raw[src + 1];
                    rgba[dst + 2] = raw[src];
                    rgba[dst + 3] = raw[src + 3];
                }
            }
            return rgba;
        }
        finally { bitmap.UnlockBits(locked); }
    }

    /// 语料回归：每张都要求解出来的 RGBA 字节与真值**完全一致**（SHA-256 相同）。
    private static void TestWebpCorpus(string launcher, Type webp, BindingFlags flags)
    {
        string manifestPath = FindWebpFixture(launcher, "tests", "fixtures", "webp", "corpus.json");
        Check(manifestPath != null, "WebP 语料：找得到 tests\\fixtures\\webp\\corpus.json");
        if (manifestPath == null) return;

        var manifest = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            File.ReadAllText(manifestPath, Encoding.UTF8));
        var list = manifest != null && manifest.ContainsKey("cases")
            ? manifest["cases"] as System.Collections.ArrayList : null;
        Check(list != null && list.Count >= 5, "WebP 语料：清单里有 ≥5 张（实得 " + (list == null ? 0 : list.Count) + "）");
        if (list == null) return;

        string dir = Path.GetDirectoryName(manifestPath);
        int ok = 0;
        var bad = new List<string>();
        foreach (object entry in list)
        {
            var item = entry as Dictionary<string, object>;
            if (item == null) continue;
            string file = Convert.ToString(item["file"]);
            string expect = Convert.ToString(item["sha256"]);
            int width = Convert.ToInt32(item["width"]);
            int height = Convert.ToInt32(item["height"]);
            byte[] bytes = File.ReadAllBytes(Path.Combine(dir, file));
            object bitmap = webp.GetMethod("Decode", flags).Invoke(null, new object[] { bytes });
            if (!(bitmap is Bitmap)) { bad.Add(file + "(解不出来)"); continue; }
            using (Bitmap bmp = (Bitmap)bitmap)
            {
                if (bmp.Width != width || bmp.Height != height) { bad.Add(file + "(尺寸 " + bmp.Width + "x" + bmp.Height + ")"); continue; }
                string actual = BitmapHash(WebpRgbaBytes(bmp));
                if (actual == expect) ok++; else bad.Add(file + "(像素不一致)");
            }
        }
        Check(bad.Count == 0, "WebP 语料：逐像素与 libwebp 一致（" + ok + "/" + list.Count
            + (bad.Count > 0 ? "，不一致：" + string.Join("、", bad.ToArray()) : "") + "）");
    }

    private static string BitmapHash(byte[] bytes)
    {
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(bytes);
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// 模拟「这台机器没有任何 WebP 解码器」：必须返回 null，且**登记一条可见诊断**。
    private static void TestWebpUnavailableIsLoud(Type webp, BindingFlags flags)
    {
        FieldInfo disable = webp.GetField("DisableAllBackendsForTest", flags);
        FieldInfo reported = webp.GetField("_reportedUnavailable", flags);
        Check(disable != null, "WebP 能力探测：有测试注入点（否则这条路径没法在真机上验）");
        if (disable == null) return;

        Type diagnostics = DanmakuType("Diagnostics");
        MethodInfo snapshot = diagnostics.GetMethod("Summary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo reset = diagnostics.GetMethod("Reset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (snapshot != null) snapshot.Invoke(null, null);

        byte[] sample = File.ReadAllBytes(FindWebpFixture(SourceRoot != null ? SourceRoot : ".", "tests", "fixtures", "douyin-emoticon.webp"));
        bool? previous = null;
        if (reported != null) previous = (bool?)reported.GetValue(null);
        try
        {
            if (reported != null) reported.SetValue(null, false);   // 让「只报一次」的闸门重新打开
            disable.SetValue(null, true);
            object none = webp.GetMethod("Decode", flags).Invoke(null, new object[] { sample });
            Check(none == null, "WebP 能力探测：没有后端时返回 null（调用方退回文字）");
            string reason = Convert.ToString(webp.GetField("LastFailureReason", flags).GetValue(null));
            Check(!string.IsNullOrEmpty(reason), "WebP 能力探测：带回可读原因（实得 " + reason + "）");
            Check(!(bool)webp.GetMethod("Available", flags).Invoke(null, null), "WebP 能力探测：Available() 如实返回 false");

            MethodInfo countOf = diagnostics.GetMethod("CountOf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            int registered = countOf == null ? -1 : (int)countOf.Invoke(null, new object[] { "webp-unavailable" });
            Check(registered == 1,
                "WebP 能力探测：**登记了可见诊断**（托盘摘要里能看到，不是只写日志；实得 " + registered + " 条）");
            string summary = null;
            if (snapshot != null) summary = Convert.ToString(snapshot.Invoke(null, null));
            Check(summary != null && summary.IndexOf("webp-unavailable", StringComparison.OrdinalIgnoreCase) >= 0,
                "WebP 能力探测：摘要文案里出现 webp-unavailable（实得：" + summary + "）");
        }
        finally
        {
            disable.SetValue(null, false);
            if (reported != null && previous.HasValue) reported.SetValue(null, previous.Value);
            if (reset != null) reset.Invoke(null, null);
        }
    }

    /// 交叉对照：本机有 WIC 时，用系统解码器解同一张图，与内置解码器**逐像素**比。
    /// 没有 WIC 就跳过（系统能力，不算失败 —— 这正是报告 §2 说的「能力测试与业务测试分开」）。
    private static void TestWebpCrossCheckWic(string launcher, Type webp, BindingFlags flags)
    {
        MethodInfo wicMethod = webp.GetMethod("DecodeWithWic", flags);
        if (wicMethod == null) { TestRunner.Skip("WebP 交叉对照：拿不到 DecodeWithWic"); return; }
        string fixture = FindWebpFixture(launcher, "tests", "fixtures", "douyin-emoticon.webp");
        if (fixture == null) { TestRunner.Skip("WebP 交叉对照：找不到样本"); return; }
        byte[] data = File.ReadAllBytes(fixture);

        object viaWic = wicMethod.Invoke(null, new object[] { data });
        if (!(viaWic is Bitmap))
        {
            TestRunner.Skip("本机没有可用的系统 WebP 解码器（WIC）—— 系统能力测试，不算失败");
            return;
        }
        using (Bitmap wicBmp = (Bitmap)viaWic)
        using (Bitmap managedBmp = (Bitmap)webp.GetMethod("Decode", flags).Invoke(null, new object[] { data }))
        {
            Check(managedBmp != null, "WebP 交叉对照：内置解码器也能解出这张图");
            if (managedBmp == null) return;
            Check(BitmapHash(WebpRgbaBytes(wicBmp)) == BitmapHash(WebpRgbaBytes(managedBmp)),
                "WebP 交叉对照：系统 WIC 与内置解码器逐像素一致（两套独立实现）");
        }
    }

    /// 中转的 FLV 时间戳读写：跨重连去重全靠它，字节顺序写错就会静默失效。
    ///
    /// FLV tag 头的布局容易记反：第 4~6 字节是时间戳**低 24 位**（大端），
    /// 第 7 字节才是**高 8 位**（扩展字节放在后面，不是前面）。
    /// 写错的后果不是崩溃，而是「去重完全不起作用」—— 用户看到的还是首屏重复。
    private static void TestFlvTagTimestamp(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type hub = assembly.GetType("LiveHub", true);
        MethodInfo get = hub.GetMethod("FlvTagTimestamp", flags);
        MethodInfo set = hub.GetMethod("SetFlvTagTimestamp", flags);
        Check(get != null && set != null, "中转 FLV：找得到时间戳读写方法");
        if (get == null || set == null) return;

        // 用真实的时间戳值构造一个最小 tag 头
        long[] cases = new long[] { 0, 1, 255, 256, 65535, 0x00FFFFFF, 0x01000000, 0x012345678L & 0x7FFFFFFF };
        bool allRoundTrip = true;
        for (int i = 0; i < cases.Length; i++)
        {
            byte[] tag = new byte[15];
            tag[0] = 9;                                   // 视频 tag
            set.Invoke(null, new object[] { tag, cases[i] });
            long back = (long)(int)get.Invoke(null, new object[] { tag });
            if (back != cases[i]) { allRoundTrip = false; Check(false,
                "中转 FLV：时间戳 " + cases[i] + " 往返变成了 " + back); break; }
            // 低位必须落在第 4~6 字节、高位落在第 7 字节
            if (tag[4] != (byte)((cases[i] >> 16) & 0xFF) || tag[7] != (byte)((cases[i] >> 24) & 0xFF))
            {
                allRoundTrip = false;
                Check(false, "中转 FLV：时间戳 " + cases[i] + " 字节位置写错了");
                break;
            }
        }
        if (allRoundTrip) Check(true, "中转 FLV：时间戳读写往返正确（含跨 24 位边界的值）");

        Check((int)get.Invoke(null, new object[] { new byte[5] }) == -1,
            "中转 FLV：tag 头不足 11 字节时返回 -1（不越界）");
    }

    /// 认不出的方括号代码要留痕（回答「这个表情为什么没变图片」）。
    ///
    /// 用户拿虎牙的 `[满头问号]` 来问。排查结论是：虎牙前端里**根本没有**这个映射
    /// ——7 个 module chunk + 24 个页面 bundle 全搜过，明文和 `\u` 转义都没有；
    /// 官方表情表就是那 39 条。所以那是观众手打的文字，虎牙自己的播放器也当文字显示。
    ///
    /// 光靠截图分不清「平台没有」和「我们漏了」，所以给字典加了「记下认不出的代码」。
    private static void TestUnknownEmoticonCode(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance;
        Type dictType = assembly.GetType("EmoticonDictionary", true);
        object dict = Activator.CreateInstance(dictType);
        dictType.GetMethod("Add", flags).Invoke(dict, new object[] { "[笑哭]", "http://x/a.png" });

        MethodInfo match = dictType.GetMethod("TryMatchAt", flags);
        Type[] argTypes = new Type[] { typeof(string), typeof(int), typeof(string).MakeByRefType(), typeof(int).MakeByRefType() };

        object[] hit = new object[] { "哈哈[笑哭]", 2, null, 0 };
        Check((bool)match.Invoke(dict, hit), "表情留痕：字典里有的代码能匹配上");

        object[] miss = new object[] { "观众手打的[满头问号]", 5, null, 0 };
        bool matched = (bool)match.Invoke(dict, miss);
        Check(!matched, "表情留痕：字典里没有的代码匹配失败（按文字显示）");
        Check((int)dictType.GetProperty("UnknownCodeCount").GetValue(dict, null) == 1,
            "表情留痕：认不出的代码被记下来了（" + dictType.GetProperty("UnknownCodeCount").GetValue(dict, null) + " 个）");

        string[] codes = (string[])dictType.GetProperty("UnknownCodes").GetValue(dict, null);
        Check(codes.Length == 1 && codes[0] == "[满头问号]",
            "表情留痕：记的正是那个代码（" + (codes.Length > 0 ? codes[0] : "(空)") + "）");

        // 同一个代码反复出现只记一次，不能把上限刷爆
        for (int i = 0; i < 50; i++) match.Invoke(dict, new object[] { "x[满头问号]", 1, null, 0 });
        Check((int)dictType.GetProperty("UnknownCodeCount").GetValue(dict, null) == 1,
            "表情留痕：同一个代码重复出现只记一次");
    }

    /// 虎牙「收割表」：本地 39 条之外的表情（用户报的 `[满头问号]`）。
    ///
    /// 背景：虎牙的本地默认包只有 39 条，真正的包由服务器下发；接口那条路实测走不通
    /// （详见 danmaku-9-huya.cs 里 `HuyaHarvestedEmoticons` 的注释）。所以改成
    /// **从官方页面收割** —— 页面把表情渲染成 `<img … alt="[满头问号]">`，alt 就是名字。
    ///
    /// 这条锁两件事：① 收割表能进字典；② **同名时覆盖本地 39 条**
    /// （实测 `[吐血]` 两边都有但 URL 不同，官方用的是服务器那份）。
    /// 虎牙 Tars：未定义类型**不能抛异常拖垮整条连接**。
    ///
    /// 现场：换到在播房间跑 `--danmaku-probe` 得到
    ///     STATUS=虎牙：解析出错 Tars skip: 未知类型 15
    ///     DANMU=0  TOTAL=0  RESULT=NO-DANMAKU
    /// —— 一条报文里有个我们没定义的类型，`Skip` 的 default 分支直接抛
    /// `InvalidOperationException`，异常一路冒到 ReadLoop，**之后一条弹幕都收不到**。
    ///
    /// 修法：`TrySkip` 遇到未定义类型时把位置推到末尾（中止**本条**报文）并返回 false，
    /// 调用方（`SkipToEnd` / `SkipToTag`）据此收工。实测 `DANMU` 从 0 变成 4、`RESULT=OK`。
    private static void TestHuyaUnknownTarsType(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance;
        Type TarsReaderType = assembly.GetType("TarsReader", true);

        // 构造：tag0/INT32=1、tag1/未知类型15、tag2/INT32=2
        //   head = (tag << 4) | type（本实现的高低位约定，写读一致）
        byte[] data = new byte[] {
            0x02, 0x00, 0x00, 0x00, 0x01,   // tag0 INT32 = 1
            0x1F, 0xAA, 0xBB,               // tag1 类型 15（未定义）+ 两个字节噪声
            0x22, 0x00, 0x00, 0x00, 0x02    // tag2 INT32 = 2
        };
        object reader = Activator.CreateInstance(TarsReaderType, new object[] { data });

        // SkipToEnd 不该抛
        bool threw = false;
        try { TarsReaderType.GetMethod("SkipToEnd", flags).Invoke(reader, null); }
        catch (Exception) { threw = true; }
        Check(!threw, "虎牙 Tars：SkipToEnd 遇到未定义类型不抛异常（一条坏报文不该拖垮整条连接）");

        // AtEnd 应该为 true（位置被推到末尾）
        bool atEnd = (bool)TarsReaderType.GetProperty("AtEnd").GetValue(reader, null);
        Check(atEnd, "虎牙 Tars：未定义类型后解析中止（位置推到末尾）");

        // TrySkip 对已知类型仍返回 true
        object r2 = Activator.CreateInstance(TarsReaderType, new object[] { data });
        bool ok = (bool)TarsReaderType.GetMethod("TrySkip", flags).Invoke(r2, new object[] { 2 });
        Check(ok, "虎牙 Tars：TrySkip 对已知类型（INT32）返回 true");
    }

    private static void TestHuyaHarvestedEmoticons(string launcher)
    {
        Assembly assembly = Assembly.LoadFrom(launcher);
        // ⚠️ 必须带 Instance：FillHuyaEmoticons 是静态的，但 TryGet 是**实例**方法 ——
        //    只给 Static 会让 GetMethod 返回 null，后面一律 NullReference（踩过）。
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance;
        Type huya = assembly.GetType("HuyaDanmakuClient", true);
        Type dictType = assembly.GetType("EmoticonDictionary", true);
        object dict = Activator.CreateInstance(dictType);
        MethodInfo tryGet = dictType.GetMethod("TryGet", flags);

        huya.GetMethod("FillHuyaEmoticons", flags).Invoke(null, new object[] { dict });
        int localCount = (int)dictType.GetProperty("Count").GetValue(dict, null);
        Check(localCount == 39, "虎牙收割表：本地默认包 39 条（实得 " + localCount + "）");

        object[] beforeArgs = new object[] { "[吐血]", null };
        tryGet.Invoke(dict, beforeArgs);
        string before = (string)beforeArgs[1];

        huya.GetMethod("FillHarvestedEmoticons", flags).Invoke(null, new object[] { dict });
        int afterCount = (int)dictType.GetProperty("Count").GetValue(dict, null);
        Check(afterCount > localCount, "虎牙收割表：填完后条目变多（" + localCount + " → " + afterCount + "）");

        object[] hu = new object[] { "[满头问号]", null };
        bool hasHu = (bool)tryGet.Invoke(dict, hu);
        Check(hasHu, "虎牙收割表：用户报的 [满头问号] 能查到（本地 39 条里没有）");
        Check(hasHu && ((string)hu[1]).IndexOf("fileserver.cdn.huya.com", StringComparison.Ordinal) >= 0,
            "虎牙收割表：[满头问号] 指向官方页面的那个 URL（实得 " + (hu[1] == null ? "null" : ((string)hu[1]).Substring(0, Math.Min(60, ((string)hu[1]).Length))) + "）");

        object[] tu = new object[] { "[吐血]", null };
        tryGet.Invoke(dict, tu);
        Check((string)tu[1] != before, "虎牙收割表：同名表情按官方页面覆盖本地 URL（[吐血]）");
    }

    private static void TestEmoticonDictionary(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type dictType = assembly.GetType("EmoticonDictionary", true);
            object dict = Activator.CreateInstance(dictType);
            MethodInfo add = dictType.GetMethod("Add");
            MethodInfo tryGet = dictType.GetMethod("TryGet");
            MethodInfo tryMatchAt = dictType.GetMethod("TryMatchAt");

            // ① 规范化：带不带方括号都应收成 "[名称]"
            add.Invoke(dict, new object[] { "[大笑]", "https://example.invalid/a.png" });
            add.Invoke(dict, new object[] { "汤圆", "https://example.invalid/b.png" });
            add.Invoke(dict, new object[] { null, "https://example.invalid/c.png" });   // 空代码必须被忽略
            Type[] getArgs = new Type[] { typeof(string), typeof(string).MakeByRefType() };
            MethodInfo get = dictType.GetMethod("TryGet", getArgs);
            object[] hit = new object[] { "[大笑]", null };
            Check((bool)get.Invoke(dict, hit), "表情字典：[大笑] 能查到");
            Check((string)hit[1] == "https://example.invalid/a.png", "表情字典：查到的 URL 正确");
            object[] hit2 = new object[] { "[汤圆]", null };
            Check((bool)get.Invoke(dict, hit2), "表情字典：不带方括号加入的也能用 [汤圆] 查到");
            object[] miss = new object[] { "[不存在]", null };
            Check(!(bool)get.Invoke(dict, miss), "表情字典：没登记的代码查不到（不能瞎认）");

            // ② 文本扫描：必须从 '[' 处精确切出代码，且只在 '[' 处才尝试
            Type[] matchArgs = new Type[] { typeof(string), typeof(int),
                typeof(string).MakeByRefType(), typeof(int).MakeByRefType() };
            MethodInfo match = dictType.GetMethod("TryMatchAt", matchArgs);
            object[] m1 = new object[] { "前面[大笑]后面", 2, null, 0 };
            Check((bool)match.Invoke(dict, m1), "表情字典：能从文本中间认出 [大笑]");
            Check((int)m1[3] == 6, "表情字典：给出的结束下标正确（吃掉整个 [大笑]）");
            object[] m2 = new object[] { "前面[大笑]后面", 0, null, 0 };
            Check(!(bool)match.Invoke(dict, m2), "表情字典：非 '[' 起始处不匹配");
            object[] m3 = new object[] { "[大笑", 0, null, 0 };
            Check(!(bool)match.Invoke(dict, m3), "表情字典：没有右括号不匹配");

            // ③ ★核心：渲染出来的位图必须**真的变宽了**（图片替换掉了文字）。
            //    这条才是「修好了」的判据 —— 前面几条只证明查表对，不证明画出来了。
            Type cacheType = assembly.GetType("DanmakuTextCache", true);
            Type sharedBitmapType = assembly.GetType("SharedBitmap", true);
            Type[] renderArgTypes = new Type[] { typeof(string), typeof(Color), typeof(float),
                typeof(string), typeof(bool), typeof(int), typeof(bool), dictType,
                typeof(Func<,>).MakeGenericType(typeof(string), sharedBitmapType) };
            MethodInfo render = cacheType.GetMethod("Render", renderArgTypes);
            if (render == null) { Check(false, "找不到 DanmakuTextCache.Render 的字典重载"); return; }

            string text = "[大笑]";
            int plainWidth;
            using (Bitmap plain = (Bitmap)render.Invoke(null, new object[] { text, Color.White, 30f,
                "Microsoft YaHei", true, 2, true, null, null }))
            {
                plainWidth = plain.Width;
            }

            // 假装图片已下载：给一张 100x100 的实心图
            using (Bitmap fake = new Bitmap(100, 100))
            {
                object lookup = MakeSharedLookup(sharedBitmapType, delegate (string url) { return fake; });
                using (Bitmap withImage = (Bitmap)render.Invoke(null, new object[] { text, Color.White, 30f,
                    "Microsoft YaHei", true, 2, true, dict, lookup }))
                {
                    // ⚠️ 注意方向：命中后位图会变**窄**，不是变宽。
                    //    `[大笑]` 四个字面字符在 30px 字号下约 101px，而 100x100 的图按
                    //    fontSize*1.30 = 39px 高等比缩放后只有 39px 宽。
                    //    我第一版断言写的就是「变宽」，被这条测试当场抓出来 ——
                    //    正确的判据是「宽度 = 图片该有的宽度」，而不是猜方向。
                    int expected = (int)Math.Ceiling(30f * 1.30f) + 2 * 4;   // 图高 + 2*padding
                    Check(Math.Abs(withImage.Width - expected) <= 3,
                        "表情字典：命中后位图宽度 = 图片宽度（文字真的换成图了）"
                        + " —— 期望约 " + expected + "px，实得 " + withImage.Width + "px");
                    Check(withImage.Width < plainWidth - 30,
                        "表情字典：带图明显不同于纯文本宽度（是替换，不是叠加）"
                        + " —— 纯文本 " + plainWidth + "px vs 带图 " + withImage.Width + "px");
                }

                // ④ 图片还没下载好 → 必须**退回显示方括号文本**，不能留空白
                object missing = MakeSharedLookup(sharedBitmapType, delegate (string url) { return null; });
                using (Bitmap fallback = (Bitmap)render.Invoke(null, new object[] { text, Color.White, 30f,
                    "Microsoft YaHei", true, 2, true, dict, missing }))
                {
                    Check(Math.Abs(fallback.Width - plainWidth) <= 2,
                        "表情字典：图片没到位时退回文本（不留空白）"
                        + " —— " + fallback.Width + "px vs " + plainWidth + "px");
                }
            }
        }
        catch (Exception ex)
        {
            Check(false, "表情字典测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }
    private static void TestEmojiSheet(string launcher)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(launcher);
            Type sheet = assembly.GetType("EmojiSheet", true);
            string dir = Path.GetDirectoryName(Path.GetFullPath(launcher));
            string png = Path.Combine(dir, "emoji-sheet.png");
            if (!File.Exists(png))
            {
                Check(false, "找不到 emoji-sheet.png —— 精灵图测试没跑起来");
                return;
            }
            // ⚠️ 先**故意问一次**再 SetPath：真实场景里渲染器/文字缓存可能比 SetPath 更早读到
            //    `Available`/`CellSize`，而 EnsureLoaded 是一次性闩 —— 老实现这一下就把精灵图
            //    永久判成「没有」（2026-09-27 被新加的位图生命周期用例踩出来）。
            object ignored = sheet.GetProperty("Available").GetValue(null, null);
            sheet.GetMethod("SetPath").Invoke(null, new object[] { png });
            Check((bool)sheet.GetProperty("Available").GetValue(null, null),
                "精灵图能加载（且「先被问过再 SetPath」也能加载 —— 一次性闩要解得开）");

            // ① 区段查表
            Rectangle rect;
            Check(TryCell(sheet, 0x1F600, false, out rect), "😀 命中普通段");
            Check(!TryCell(sheet, 0x0041, false, out rect), "'A' 不命中（普通字母不该被当 emoji）");

            // ② 变体选择符：❤ 的黑白格与彩色格必须是**两格**
            Rectangle plain, variant;
            bool hasPlain = TryCell(sheet, 0x2764, false, out plain);
            bool hasVariant = TryCell(sheet, 0x2764, true, out variant);
            Check(hasPlain && hasVariant, "❤ 的普通格与变体格都能取到");
            Check(hasPlain && hasVariant && !plain.Equals(variant), "❤ 普通格 != 变体格（黑白 / 彩色分开）");
            Check((bool)sheet.GetMethod("HasVariant").Invoke(null, new object[] { 0x2764 }), "❤ 有变体格");

            // ③ ZWJ 整串：必须命中，且两边都忽略 FE0F（带不带变体符都命中同一格）
            //
            // 注意：ZWJ(U+200D) / 变体选择符(U+FE0F) 是**不可见字符**，直接写进源码字面量很容易在
            // 复制粘贴 / 工具链里被悄悄吃掉（这里就踩过一次：`🏳‍🌈` 的 ZWJ 没了，测试假失败）。
            // 一律用转义拼出来，看得见也搬不走。
            const string Zwj = "\u200D";
            const string Vs16 = "\uFE0F";
            string family = char.ConvertFromUtf32(0x1F468) + Zwj + char.ConvertFromUtf32(0x1F469) + Zwj + char.ConvertFromUtf32(0x1F467);
            string technologist = char.ConvertFromUtf32(0x1F469) + Zwj + char.ConvertFromUtf32(0x1F4BB);
            string flagWith = char.ConvertFromUtf32(0x1F3F3) + Vs16 + Zwj + char.ConvertFromUtf32(0x1F308);
            string flagWithout = char.ConvertFromUtf32(0x1F3F3) + Zwj + char.ConvertFromUtf32(0x1F308);
            string keycap = "1" + Vs16 + "\u20E3";

            int familyOffset = Cluster(sheet, family);
            int techOffset = Cluster(sheet, technologist);
            int flagWithOffset = Cluster(sheet, flagWith);
            int flagWithoutOffset = Cluster(sheet, flagWithout);
            Check(familyOffset >= 0, "ZWJ 家庭（👨‍👩‍👧）整串命中");
            Check(techOffset >= 0, "ZWJ 职业（👩‍💻）整串命中");
            Check(flagWithOffset >= 0, "ZWJ 彩虹旗（带 FE0F）整串命中");
            Check(flagWithOffset >= 0 && flagWithOffset == flagWithoutOffset,
                "彩虹旗带不带 FE0F 命中同一格（清单与运行时都忽略 FE0F）");

            // ④ 键帽 1️⃣ = 0031 FE0F 20E3
            Check(Cluster(sheet, keycap) >= 0, "键帽 1️⃣ 整串命中");
            Check(Cluster(sheet, char.ConvertFromUtf32(0x1F600)) < 0, "单码点 😀 不走序列匹配（交给普通段）");
            Check(Cluster(sheet, char.ConvertFromUtf32(0x2764) + Vs16) < 0, "❤️ 不是序列（该走变体段）");

            // ⑤ 肤色修饰符：要认出来，否则会在弹幕里留一个色块
            Check((bool)sheet.GetMethod("IsSkinTone").Invoke(null, new object[] { 0x1F3FD }),
                "肤色修饰符能被识别（按基础 emoji 渲染）");
            Check(!(bool)sheet.GetMethod("IsSkinTone").Invoke(null, new object[] { 0x1F600 }),
                "普通 emoji 不算肤色修饰符");
        }
        catch (Exception ex)
        {
            Check(false, "精灵图测试异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static bool TryCell(Type sheet, int codePoint, bool variant, out Rectangle rect)
    {
        object[] args = new object[] { codePoint, variant, null };
        bool result = (bool)sheet.GetMethod("TryGetCell").Invoke(null, args);
        rect = result ? (Rectangle)args[2] : new Rectangle();
        return result;
    }

    private static int Cluster(Type sheet, string text)
    {
        object[] args = new object[] { text, 0, 0 };
        return (int)sheet.GetMethod("MatchCluster").Invoke(null, args);
    }


}
