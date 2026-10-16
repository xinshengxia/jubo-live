// --------------------------------------------------------------------------------------------
// 2026-09-26：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;

// 注意：**不要**包 namespace。本项目其它 danmaku-*.cs 模块都在全局命名空间里，
// 包一层会与宿主文件里已存在的 LiveHub 命名空间冲突（error CS0101）。
// --------------------------------------------------------------------------------------------
// 平台表情字典：把弹幕文本里的「方括号代码」映射到图片 URL。
//
// 为什么需要它（用户反馈的现象）：
//   B站弹幕里会出现 `[汤圆]` `[大笑]` 这种**字面量**，网页播放器拿到文本后自己查字典
//   把它们换成图片；我们的覆盖层只把原始文本画出来，于是用户看到的是 `健身是吧[汤圆]`。
//
// 这跟 danmaku-2-client.cs 里处理的 `dmType == 1`（服务端**直接给图片 URL** 的大表情）
// 是**两套完全不同的机制** —— 那条路一条弹幕就是一张图；这条路是文本里夹着代码。
//
// B站表情接口：
//   接口 GET https://api.live.bilibili.com/xlive/web-ucenter/v2/emoticon/GetEmoticons
//        ?platform=pc&room_id=<真实房间号>
//   返回 ~202 个表情，`emoji` 字段的值**就是** `[大笑]` / `[汤圆]` 这种带方括号的代码，
//   配 `url` 直接用。
//
// ⚠️ 两个必须踩对的参数（不符合要求时接口会报错）：
//   · room_id 必须是**真实房间号**，不是显示号 —— 传显示号返回 -500
//   · platform 必须是 `pc` —— 传 `web` 返回「参数错误，平台来源错误」
// --------------------------------------------------------------------------------------------
internal sealed class EmoticonDictionary
{
    // 代码（含方括号，如 "[大笑]"）→ 图片 URL。
    // Ordinal 比较：这是**协议数据**不是人类语言，不需要也不该按区域比较。
    private readonly Dictionary<string, string> _byCode =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// 单次匹配的最大长度。B站 最长的像 `[不问天_大笑]`（7 字），留足余量；
    /// 上限的作用是别让一段普通文本里的 `[` 去扫描全文。
    private const int MaxCodeLength = 24;

    /// 认不出来的方括号代码，各记一次（上限 200，防内存无限涨）。
    ///
    /// 存在的理由：用户反馈「为什么这个带方括号的表情没显示成图片」时，
    /// 光看截图没法判断 —— 是字典里没有？还是匹配逻辑漏了？
    /// 记下来就能直接回答：**这个代码确实不在平台给的字典里**（多半是观众手打的文字），
    /// 而不是我们的 bug。平台加了新表情也一眼能看出来。
    private readonly HashSet<string> _unknownCodes = new HashSet<string>(StringComparer.Ordinal);

    public int UnknownCodeCount { get { return _unknownCodes.Count; } }

    /// 按顺序列出认不出的代码（给日志用）。
    public string[] UnknownCodes
    {
        get
        {
            string[] list = new string[_unknownCodes.Count];
            _unknownCodes.CopyTo(list);
            return list;
        }
    }

    public int Count { get { return _byCode.Count; } }

    /// 加入一条。code 允许带或不带方括号，内部统一成 `[名称]`。
    public void Add(string code, string url)
    {
        if (code == null || url == null) return;
        code = code.Trim();
        if (code.Length == 0 || code.Length > MaxCodeLength) return;
        if (url.Length == 0) return;
        if (code[0] != '[') code = "[" + code;
        if (code[code.Length - 1] != ']') code = code + "]";
        _byCode[code] = url;
    }

    public bool TryGet(string code, out string url)
    {
        if (code == null) { url = null; return false; }
        return _byCode.TryGetValue(code, out url);
    }

    /// 文本里从 start 处（必须是 '['）尝试匹配一个表情代码。
    /// 命中返回 true，并用 end 给出代码之后的下标。
    public bool TryMatchAt(string text, int start, out string url, out int end)
    {
        url = null;
        end = start;
        if (_byCode.Count == 0 || text == null) return false;
        if (start >= text.Length || text[start] != '[') return false;

        int close = text.IndexOf(']', start + 1);
        if (close < 0) return false;
        int len = close - start + 1;
        if (len > MaxCodeLength) return false;

        string code = text.Substring(start, len);
        if (!_byCode.TryGetValue(code, out url))
        {
            // 记一笔「见过但没映射」的代码。只在字典非空时记 —— 字典还没加载时
            // 满屏都是「认不出」，记了也没意义还会刷爆上限。
            // 只在**首次**遇到某个代码时打日志：上限 200，不会刷屏，
            // 但足够回答「这个表情为什么没变图片」—— 看到这行就说明平台字典里确实没有它。
            if (_byCode.Count > 0 && _unknownCodes.Count < 200 && _unknownCodes.Add(code))
                DanmakuHost.Log("表情字典里没有这个代码，按文字显示：" + code
                    + "（字典共 " + _byCode.Count + " 条）");
            return false;
        }
        end = close + 1;
        return true;
    }

    public void Clear() { _byCode.Clear(); }
}
