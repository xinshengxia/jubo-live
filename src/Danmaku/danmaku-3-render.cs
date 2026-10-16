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
using System.Windows.Forms;
// --------------------------------------------------------------------------------------------
// 渲染：文字位图缓存 + 轨道调度 + 绘制
//
// 为什么每条弹幕先渲染成小位图：描边（AddString + DrawPath）很贵，但**每条弹幕只做一次**，
// 之后每帧只是 DrawImage 平移。实测 100 条同屏、1080p 整帧 ≈ 12ms（30fps 预算 33ms）。
// --------------------------------------------------------------------------------------------
/// <summary>共享位图：**所有权说了算**，不靠时间猜、也不靠事后问。</summary>
///
/// 为什么不是「延迟释放 + 事后问一句」：
///   · 延迟（60 秒）是**猜**，而热门房间一秒能退役几十个 key，隔离区硬上限一撞上就提前放，猜错就炸；
///   · 「问一句还在不在用」也不行 —— GDI+ 对已释放的位图抛的是 `ArgumentException` 而不是
///     `ObjectDisposedException`（dotnet/runtime#29841），**没有任何可靠的活着判据**；
///   · 凡是「共享一个可释放资源 + 事后判断」的方案，都是同一个坑的变体。
///
/// 这里改成**引用计数**（和 `IMemoryCache` 把「缓存负责 Dispose 条目」这条契约拿掉是同一个道理）：
///   位图只有在「缓存不再持有」**并且**「所有借出去的那份都还回来」之后才 Dispose。
/// 于是「手里拿着一张已经被释放的位图」在**结构上不可能发生** —— 不需要安全期，也不需要 InUseCheck。
///
/// 使用契约（必须成对）：
///   · 拿到（缓存命中或新建）时就已经**借给你一份**（leases +1）；
///   · 用完了必须 `Release()`，否则位图不会被释放（GDI 对象泄漏）；
///   · 只要没 Release，哪怕缓存淘汰 / 清空 / 换房间，这张位图照样可用。
internal sealed class SharedBitmap
{
    private readonly Bitmap _bitmap;
    private readonly object _gate = new object();
    private int _leases;      // 借出去几份（使用者数量），不含缓存自己那一份
    private bool _cached;     // 缓存是否还持有
    private bool _freed;

    /// 存活计数（诊断 / 自检用）：创建 +1，真正 Dispose 时 -1。
    private static int _live;
    internal static int LiveCount { get { return _live; } }
    /// 累计释放数（测试用）。
    private static int _freedTotal;
    internal static int FreedTotal { get { return _freedTotal; } }

    internal SharedBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap;
        _cached = true;
        System.Threading.Interlocked.Increment(ref _live);
    }

    /// 底层位图。**只要你还持有这个 SharedBitmap（没 Release），它就一定可用** —— 这是本类的全部意义。
    internal Bitmap Bitmap { get { return _bitmap; } }

    /// 借出去一份。返回自己，方便写 `f.Handle = cache.Get(...)`。
    internal SharedBitmap Acquire()
    {
        lock (_gate) { if (!_freed) _leases++; }
        return this;
    }

    /// 还回来一份。计数归零且缓存也已经放手 → 立刻 Dispose。
    internal void Release()
    {
        lock (_gate)
        {
            if (_leases > 0) _leases--;
            FreeIfDone();
        }
    }

    /// 缓存不再持有这张图（淘汰 / Clear / 换房间）。
    internal void DropFromCache()
    {
        lock (_gate)
        {
            _cached = false;
            FreeIfDone();
        }
    }

    private void FreeIfDone()
    {
        if (_freed || _cached || _leases > 0) return;
        _freed = true;
        System.Threading.Interlocked.Decrement(ref _live);
        System.Threading.Interlocked.Increment(ref _freedTotal);
        try { _bitmap.Dispose(); }
        catch (Exception) { }   // 已经放过 / GDI 状态异常：都不能影响渲染
    }

    /// <summary>测试用：当前还欠着几份没还（应为 0 —— 不为 0 就是使用者忘了 Release）。</summary>
    internal int Leases { get { lock (_gate) { return _leases; } } }
    internal bool Freed { get { lock (_gate) { return _freed; } } }
    internal bool Cached { get { lock (_gate) { return _cached; } } }
}

internal sealed class DanmakuTextCache
{
    // 存的是 SharedBitmap（带引用计数），不是裸 Bitmap —— 见 SharedBitmap 的说明。
    private readonly Dictionary<string, SharedBitmap> _items = new Dictionary<string, SharedBitmap>();
    private readonly List<string> _order = new List<string>();
    private const int Capacity = 600;

    /// 当前缓存里几张（测试用）。
    internal int Count { get { return _items.Count; } }

    /// 彩色 emoji 开关（来自配置）。关掉就走「Segoe UI Emoji 单色字形」那条老路。
    public bool ColorEmoji = true;

    /// 当前房间的平台表情字典（B站 `[大笑]` 这类）。换房间要连同 Clear() 一起换。
    public EmoticonDictionary Emoticons;

    /// 按 URL 取表情图片句柄；拿不到（还在下载）返回 null。
    /// 由渲染器接到它的图片缓存上 —— 这样只下载一次，多条弹幕共用。
    /// ⚠️ 拿到的是**已经借出的一份**：调用方用完必须 Release()。
    public Func<string, SharedBitmap> EmoticonImage;

    /// <summary>取这条文本的位图句柄。**返回时已经借给你一份，用完必须 Release()。**</summary>
    public SharedBitmap Get(string text, Color color, float fontSize, string fontFamily, bool stroke, int strokeWidth)
    {
        if (text == null) text = "";
        // 先分段、再解析图片 —— 因为缓存 key 必须包含「图片有没有下载好」：
        // 否则第一帧（图还没到）渲染出的「纯文本兜底」会被缓存住，图片下载完也永远不会出现。
        List<Segment> segments = Split(text, fontFamily, ColorEmoji, Emoticons);
        SharedBitmap[] images = ResolveImages(segments, EmoticonImage);
        try
        {
            string key = text + "\u0001" + color.ToArgb() + "\u0001" + ((int)(fontSize * 10))
                + "\u0001" + fontFamily + "\u0001" + stroke + "\u0001" + strokeWidth + "\u0001" + ColorEmoji
                + "\u0001" + ImageSignature(segments, images);
            SharedBitmap cached;
            if (_items.TryGetValue(key, out cached)) return cached.Acquire();

            Bitmap bitmap = RenderSegments(segments, images, color, fontSize, fontFamily, stroke, strokeWidth);
            SharedBitmap shared = new SharedBitmap(bitmap);
            _items[key] = shared;
            _order.Add(key);
            while (_order.Count > Capacity)
            {
                // 淘汰 = 「缓存不再持有」，**不是** Dispose：还在被借着的（正在飞的弹幕、正在显示的 SC）
                // 继续有效，等它们 Release 之后才真正释放（见 SharedBitmap）。
                string oldest = _order[0];
                _order.RemoveAt(0);
                SharedBitmap victim;
                if (_items.TryGetValue(oldest, out victim))
                {
                    _items.Remove(oldest);
                    victim.DropFromCache();
                }
            }
            return shared.Acquire();
        }
        finally
        {
            // 表情图只在这次渲染里用了一下，就地还掉（缓存自己那一份仍在缓存手里）
            ReleaseAll(images);
        }
    }

    private static void ReleaseAll(SharedBitmap[] images)
    {
        if (images == null) return;
        for (int i = 0; i < images.Length; i++)
            if (images[i] != null) images[i].Release();
    }

    /// 换表情字典时调用（见 Clear 的实现）：旧 key 里的图片签名已无意义，留着只白占额度。
    /// 把图片段解析成位图。测量与绘制两趟必须用**同一批**，否则「下载刚好在这一瞬完成」
    /// 会让两趟算出不同宽度、整行错位。
    private static SharedBitmap[] ResolveImages(List<Segment> segments, Func<string, SharedBitmap> lookup)
    {
        SharedBitmap[] images = new SharedBitmap[segments.Count];
        if (lookup == null) return images;
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].IsImage) continue;
            try { images[i] = lookup(segments[i].ImageUrl); }
            catch (Exception) { images[i] = null; }   // 拿不到就当文本兜底，不让一条弹幕拖垮整帧
        }
        return images;
    }

    /// 这条文本里还有没有「已识别为表情、但图片**尚未下载好**」的段。
    ///
    /// 存在的理由（用户反馈「[虎年] 还是文字」的根因）：
    /// 位图是在弹幕**创建时**渲染并**存到弹幕对象上**的（每帧只 DrawImage，不重算 ——
    /// 这是性能设计，100 条同屏整帧才 12ms）。但表情图片是**异步下载**的：
    /// 创建那一刻图还没到 → `ResolveImages` 给 null → 位图就画成了字面量 `[虎年]`，
    /// 之后**永远不会重画**。于是**首次出现**的表情（图还没进缓存）全部停在文字上；
    /// 而常见的 `[大笑]` 第二次出现时缓存里已经有了，看起来就是「有的行有的不行」。
    ///
    /// 顺带一提：`ImageSignature` 那套「就绪指纹进 key」只有在**再次调用 Get** 时才有用，
    /// 而旧代码根本没有第二次调用 —— 指纹白设计了。
    ///
    /// 注意它会**顺手踢一次下载**（`EmoticonImage` → `TryGet` 的语义），
    /// 所以返回 false 时就说明图片已经全在缓存里了。
    public bool HasPendingImage(string text, string fontFamily)
    {
        if (Emoticons == null || EmoticonImage == null || text == null) return false;
        if (text.IndexOf('[') < 0) return false;      // 绝大多数弹幕在这里就返回了
        List<Segment> segments = Split(text, fontFamily, ColorEmoji, Emoticons);
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].IsImage) continue;
            // 这里只是「问一声图到了没」，借出来的那一份当场还掉（不还就是 GDI 泄漏）
            SharedBitmap probe = EmoticonImage(segments[i].ImageUrl);
            if (probe == null) return true;
            probe.Release();
        }
        return false;
    }

    /// 图片段的「就绪状态」指纹：同一个 URL 在「已下载 / 未下载」下必须是两个不同的 key。
    private static string ImageSignature(List<Segment> segments, SharedBitmap[] images)
    {
        int count = 0;
        for (int i = 0; i < segments.Count; i++) if (segments[i].IsImage) count++;
        if (count == 0) return "";
        StringBuilder sb = new StringBuilder(count * 8);
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].IsImage) continue;
            sb.Append(images[i] != null ? '1' : '0');
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 分段
    //
    // 一段要么是「能贴精灵图的彩色 emoji」，要么是「用某个字体画的一串文字」。
    // 分开的理由见 danmaku-7-emoji.cs 顶部的说明：GDI+ 不回退、也不画彩色 emoji。
    private sealed class Segment
    {
        public bool IsEmoji;
        public int CodePoint;
        public bool Variant;      // 原文这个码点后面跟了 U+FE0F（要彩色呈现）
        public int FlagSecond = -1;    // 国旗：第二个区域指示符（-1 = 不是国旗）
        public int Sequence = -1;      // ZWJ 组合 / 键帽：序列段里的偏移（-1 = 不是序列）
        public string Text;
        public string Family;

        // 平台表情图片段（B站 `[大笑]` 这类）。与 IsEmoji 的区别：
        // IsEmoji 走**本地精灵图**（EmojiSheet），这里是**网络图片**（平台表情字典给的 URL）。
        public bool IsImage;
        public string ImageUrl;
    }

    private static List<Segment> Split(string text, string fontFamily, bool colorEmoji)
    {
        return Split(text, fontFamily, colorEmoji, null);
    }

    private static List<Segment> Split(string text, string fontFamily, bool colorEmoji,
        EmoticonDictionary emoticons)
    {
        List<Segment> list = new List<Segment>();
        StringBuilder buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            // ⓪ 平台表情代码（B站 `[大笑]` / `[汤圆]`）。必须放在最前面：
            //    否则 '[' 会被当成普通字符进 buffer，之后再也切不出来。
            //    这是**文本里夹代码**的机制，跟服务端直接给 URL 的「大表情弹幕」无关。
            if (emoticons != null && text[i] == '[')
            {
                string emoUrl;
                int emoEnd;
                if (emoticons.TryMatchAt(text, i, out emoUrl, out emoEnd))
                {
                    if (buffer.Length > 0) { list.Add(TextSegment(buffer.ToString(), fontFamily)); buffer.Length = 0; }
                    Segment image = new Segment();
                    image.IsImage = true;
                    image.ImageUrl = emoUrl;
                    // 图片还没下载好时用它兜底显示 —— 宁可先看到 `[大笑]` 字面量，
                    // 也不要留一块空白（用户会更困惑）。
                    image.Text = text.Substring(i, emoEnd - i);
                    image.Family = fontFamily;
                    list.Add(image);
                    i = emoEnd;
                    continue;
                }
            }

            bool pair = char.IsSurrogatePair(text, i);
            int codePoint = pair ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            int unit = pair ? 2 : 1;
            // 后面跟 U+FE0F（变体选择符 16）= 明确要求彩色呈现。❤ 与 ❤️ 就靠它区分：
            // 前者是文字型（黑白轮廓），后者才是彩色 emoji。
            bool hasVs = (i + unit < text.Length && text[i + unit] == '\uFE0F');
            // 变体选择符本身没有字形，不管走哪条路都要吃掉，否则会被画成一个豆腐
            int advance = unit + (hasVs ? 1 : 0);

            if (colorEmoji)
            {
                // ① ZWJ 组合 / 键帽：必须整串，拆开就散架（👨‍👩‍👧 会变成三个人）
                int sequenceEnd;
                int sequence = EmojiSheet.MatchCluster(text, i, out sequenceEnd);
                if (sequence >= 0)
                {
                    if (buffer.Length > 0) { list.Add(TextSegment(buffer.ToString(), fontFamily)); buffer.Length = 0; }
                    Segment combined = new Segment();
                    combined.IsEmoji = true;
                    combined.Sequence = sequence;
                    list.Add(combined);
                    i = sequenceEnd;
                    continue;
                }

                // ② 肤色修饰符（👍🏽 的后半截）：精灵图里它是**色块**，单独画会在弹幕里留个色块，
                //    所以直接丢掉（按基础 emoji 渲染）。精灵图没做 肤色×基字符 的组合格。
                if (EmojiSheet.IsSkinTone(codePoint)) { i += advance; continue; }

                // ③ 国旗要**成对**：两个区域指示符连在一起才是旗子，单渲染一个是「带框字母」
                if (EmojiSheet.IsRegionalIndicator(codePoint) && i + unit + 1 < text.Length)
                {
                    int j = i + unit;
                    bool pair2 = char.IsSurrogatePair(text, j);
                    int second = pair2 ? char.ConvertToUtf32(text[j], text[j + 1]) : text[j];
                    Rectangle flagCell;
                    if (pair2 && EmojiSheet.TryGetFlagCell(codePoint, second, out flagCell))
                    {
                        if (buffer.Length > 0) { list.Add(TextSegment(buffer.ToString(), fontFamily)); buffer.Length = 0; }
                        Segment flag = new Segment();
                        flag.IsEmoji = true;
                        flag.CodePoint = codePoint;
                        flag.FlagSecond = second;
                        list.Add(flag);
                        i += unit * 2;
                        continue;
                    }
                }
            }

            Rectangle cell;
            bool isEmoji = false, variant = false;
            if (colorEmoji)
            {
                if (hasVs && EmojiSheet.TryGetCell(codePoint, true, out cell)) { isEmoji = true; variant = true; }
                else if (EmojiSheet.TryGetCell(codePoint, false, out cell)) isEmoji = true;
            }
            if (isEmoji)
            {
                if (buffer.Length > 0) { list.Add(TextSegment(buffer.ToString(), fontFamily)); buffer.Length = 0; }
                Segment emoji = new Segment();
                emoji.IsEmoji = true;
                emoji.CodePoint = codePoint;
                emoji.Variant = variant;
                list.Add(emoji);
            }
            else buffer.Append(text, i, unit);
            i += advance;
        }
        if (buffer.Length > 0) list.Add(TextSegment(buffer.ToString(), fontFamily));
        if (list.Count == 0) list.Add(TextSegment(" ", fontFamily));
        return list;
    }

    private static Segment TextSegment(string content, string baseFamily)
    {
        Segment segment = new Segment();
        segment.Text = content;
        segment.Family = FontCoverage.PickFamily(content, baseFamily);
        return segment;
    }

    // ---------------------------------------------------------------- 渲染
    public static Bitmap Render(string text, Color color, float fontSize, string fontFamily,
        bool stroke, int strokeWidth)
    {
        return Render(text, color, fontSize, fontFamily, stroke, strokeWidth, true);
    }

    public static Bitmap Render(string text, Color color, float fontSize, string fontFamily,
        bool stroke, int strokeWidth, bool colorEmoji)
    {
        return Render(text, color, fontSize, fontFamily, stroke, strokeWidth, colorEmoji, null, null);
    }

    public static Bitmap Render(string text, Color color, float fontSize, string fontFamily,
        bool stroke, int strokeWidth, bool colorEmoji,
        EmoticonDictionary emoticons, Func<string, SharedBitmap> emoticonImage)
    {
        if (text == null) text = "";
        List<Segment> segments = Split(text, fontFamily, colorEmoji, emoticons);
        SharedBitmap[] images = ResolveImages(segments, emoticonImage);
        try { return RenderSegments(segments, images, color, fontSize, fontFamily, stroke, strokeWidth); }
        finally
        {
            // 借出来的表情图当场还掉：渲染只在这一瞬需要它们
            for (int i = 0; i < images.Length; i++) if (images[i] != null) images[i].Release();
        }
    }

    private static Bitmap RenderSegments(List<Segment> segments, SharedBitmap[] images,
        Color color, float fontSize, string fontFamily, bool stroke, int strokeWidth)
    {
        float emojiSize = fontSize * 1.18f;
        // 平台表情图片的高度。比 emojiSize 略大一点：那些图自带留白，跟文字并排时
        // 按 1.18 会显得比汉字矮一截（emojiSize 是对着精灵图的紧密裁切调的）。
        float imageSize = fontSize * 1.30f;
        int padding = stroke ? strokeWidth + 2 : 2;

        // ---- 量一遍：总宽 + 行高 ----
        float totalWidth = 0f, lineHeight = 0f;
        SizeF[] sizes = new SizeF[segments.Count];
        using (Bitmap probe = new Bitmap(1, 1))
        using (Graphics measure = Graphics.FromImage(probe))
        {
            for (int i = 0; i < segments.Count; i++)
            {
                Segment segment = segments[i];
                if (segment.IsImage && images[i] != null)
                {
                    // 等比缩放到 imageSize 高。宽高都做下限保护，畸形图不至于把位图撑爆。
                    float h = imageSize;
                    float w = images[i].Bitmap.Width * (h / Math.Max(1, images[i].Bitmap.Height));
                    if (w < 1f) w = 1f;
                    if (w > imageSize * 4f) w = imageSize * 4f;   // 极端长条图截断宽度
                    sizes[i] = new SizeF(w, h);
                }
                else if (segment.IsEmoji)
                {
                    sizes[i] = new SizeF(emojiSize, emojiSize);
                }
                else
                {
                    using (Font font = new Font(segment.Family, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        SizeF measured = measure.MeasureString(segment.Text, font);
                        sizes[i] = new SizeF(measured.Width, measured.Height);
                    }
                }
                totalWidth += sizes[i].Width;
                if (sizes[i].Height > lineHeight) lineHeight = sizes[i].Height;
            }
        }
        int width = (int)Math.Ceiling(totalWidth) + padding * 2;
        int height = (int)Math.Ceiling(lineHeight) + padding * 2;
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float penWidth = strokeWidth * Math.Max(1.0f, fontSize * 0.05f);
            float x = padding;
            for (int i = 0; i < segments.Count; i++)
            {
                Segment segment = segments[i];
                float y = padding + (lineHeight - sizes[i].Height) / 2f;   // 段内垂直居中
                if (segment.IsImage && images[i] != null)
                {
                    g.DrawImage(images[i].Bitmap, new RectangleF(x, y, sizes[i].Width, sizes[i].Height));
                }
                else if (segment.IsEmoji)
                {
                    Rectangle source;
                    bool hasCell;
                    if (segment.Sequence >= 0) hasCell = EmojiSheet.TryGetSequenceCell(segment.Sequence, out source);
                    else if (segment.FlagSecond >= 0) hasCell = EmojiSheet.TryGetFlagCell(segment.CodePoint, segment.FlagSecond, out source);
                    else hasCell = EmojiSheet.TryGetCell(segment.CodePoint, segment.Variant, out source);
                    if (hasCell && EmojiSheet.Sheet != null)
                        g.DrawImage(EmojiSheet.Sheet,
                            new RectangleF(x, y + (sizes[i].Height - emojiSize) / 2f, emojiSize, emojiSize),
                            source, GraphicsUnit.Pixel);
                }
                else
                {
                    using (Font font = new Font(segment.Family, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        if (stroke && strokeWidth > 0)
                        {
                            using (GraphicsPath path = new GraphicsPath())
                            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                            {
                                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                                // emSize 传 font.Size：Graphics 是 96 DPI 的位图，像素单位下两者一致
                                path.AddString(segment.Text, font.FontFamily, (int)font.Style, font.Size,
                                    new PointF(x, y), format);
                                // 描边宽度**必须随字号缩放**：固定 2px 在 24px 字号下会把汉字笔画之间的缝填满，
                                // 整个字糊成一块色块（实测踩过）。
                                using (Pen pen = new Pen(Color.FromArgb(255, 0, 0, 0), penWidth))
                                {
                                    pen.LineJoin = LineJoin.Round;
                                    g.DrawPath(pen, path);
                                }
                                using (SolidBrush brush = new SolidBrush(color)) g.FillPath(brush, path);
                            }
                        }
                        else
                        {
                            using (SolidBrush brush = new SolidBrush(color))
                                g.DrawString(segment.Text, font, brush, x, y);
                        }
                    }
                }
                x += sizes[i].Width;
            }
        }
        return bitmap;
    }

    /// 换表情字典/换房间时调用：旧 key 里的图片签名已无意义，留着只白占额度。
    /// 只是「缓存不再持有」：**正在飞的弹幕手里那份照样有效**，等它 Release 才释放。
    public void Clear()
    {
        foreach (SharedBitmap shared in _items.Values) shared.DropFromCache();
        _items.Clear();
        _order.Clear();
    }
}

// --------------------------------------------------------------------------------------------
// 头像 / 表情图片：后台下载 + LRU 缓存；下载好之前先用占位（绝不让首帧等网络）
// --------------------------------------------------------------------------------------------
internal sealed class DanmakuImageCache : IDisposable
{
    internal Func<string, bool> ValidateUrl = LiveHub.IsSafeRemoteHttpUrl;
    internal Func<int> TickCount = delegate { return Environment.TickCount; };
    // 同样存 SharedBitmap：淘汰只是「缓存放手」，正在画它的人不受影响（见 SharedBitmap）。
    private readonly Dictionary<string, SharedBitmap> _items = new Dictionary<string, SharedBitmap>();
    private readonly List<string> _order = new List<string>();
    private readonly HashSet<string> _pending = new HashSet<string>();
    private readonly object _gate = new object();
    private const int Capacity = 200;
    internal const long MaxCacheBytes = 64L * 1024 * 1024;
    internal const int MaxConcurrentDownloads = 4;
    private static readonly Semaphore DownloadSlots = new Semaphore(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private long _cacheBytes;
    private bool _disposed;
    internal long CacheBytes { get { lock (_gate) { return _cacheBytes; } } }

    /// 单张表情图的大小上限。评估报告 §4：原来响应体**无限制读进内存**，
    /// 一个被投毒 / 出错的 URL 就能让弹幕进程吃掉几百 MB。
    internal const int MaxBytes = 4 * 1024 * 1024;
    /// 解码后的边长上限（防「小文件解出巨图」的解码炸弹）。
    internal const int MaxDimension = 2048;
    /// 失败退避梯度（秒）：30 秒 → 2 分钟 → 10 分钟。
    /// 原来失败后**立刻允许下一条弹幕重下**，一个坏 URL 会被同屏几十条弹幕反复重试。
    internal static readonly int[] BackoffSeconds = new int[] { 30, 120, 600 };

    private readonly Dictionary<string, long> _retryAfter = new Dictionary<string, long>();  // url -> TickCount
    private readonly Dictionary<string, int> _failCount = new Dictionary<string, int>();
    private long _requestCount;

    /// 缓存里的图片数（测试用）
    public int Count { get { lock (_gate) { return _items.Count; } } }
    /// 真正发起过的下载次数（测试用它证明「退避期内不再发请求」）
    internal long RequestCount { get { lock (_gate) { return _requestCount; } } }
    /// 当前处于退避期的 URL 数
    internal int BackoffCount { get { lock (_gate) { return _retryAfter.Count; } } }

    /// 第 failCount 次失败后要退避多久（纯函数，测试直接钉它）。
    internal static int BackoffFor(int failCount)
    {
        if (failCount <= 1) return BackoffSeconds[0];
        if (failCount == 2) return BackoffSeconds[1];
        return BackoffSeconds[2];
    }

    /// <summary>取图。命中则**借出一份**（用完必须 Release），没命中则踢一次后台下载并返回 null。</summary>
    public SharedBitmap TryGet(string url)
    {
        if (url == null || url.Length == 0) return null;
        lock (_gate)
        {
            if (_disposed) return null;
            SharedBitmap cached;
            if (_items.TryGetValue(url, out cached)) return cached.Acquire();
            // URL 来自直播弹幕协议，属于远端不可信输入。只在准备发起网络请求时拦截；
            // 已由诊断入口放进缓存的对象仍可正常命中。
            if (_pending.Contains(url)) return null;
            // 退避期内直接返回 null：既不重下，也不会让界面卡在「加载中」
            long until;
            if (_retryAfter.TryGetValue(url, out until) && unchecked(TickCount() - (int)until) < 0) return null;
            if (!LiveHub.IsSafeRemoteHttpUrlSyntax(url)) return null;
            if (!DownloadSlots.WaitOne(0)) return null; // 没预约的 URL 下帧重试，不堆无界线程池任务。
            _pending.Add(url);
        }
        ThreadPool.QueueUserWorkItem(delegate {
            Bitmap bitmap = null;
            try { if (ValidateUrl(url)) bitmap = Download(url); }
            catch (Exception ex) { Diagnostics.Report("emoticon-image", ex); }
            try { Complete(url, bitmap); }
            finally { DownloadSlots.Release(); }
        });
        return null;
    }

    /// <summary>同步下载一次并入库（测试 / 诊断用；产品路径走线程池）。返回入库后的句柄（**不借出**）。</summary>
    internal SharedBitmap FetchNow(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (_gate)
        {
            if (_disposed) return null;
            long until;
            if (_retryAfter.TryGetValue(url, out until) && unchecked(TickCount() - (int)until) < 0) return null;
            _pending.Add(url);
        }
        return Complete(url, Download(url));
    }

    /// <summary>下载结果入库 / 记失败退避。返回句柄（失败为 null，**不借出**）。</summary>
    private SharedBitmap Complete(string url, Bitmap bitmap)
    {
        lock (_gate)
        {
            _pending.Remove(url);
            if (_disposed) { if (bitmap != null) bitmap.Dispose(); return null; }
            if (bitmap == null)
            {
                int count;
                _failCount.TryGetValue(url, out count);
                count = Math.Min(count + 1, BackoffSeconds.Length);
                _failCount[url] = count;
                // 截止 tick 按 int 回绕，比较时使用差值；退避最长十分钟。
                _retryAfter[url] = unchecked(TickCount() + BackoffFor(count) * 1000);
                return null;
            }
            _retryAfter.Remove(url);
            _failCount.Remove(url);
            SharedBitmap replaced;
            if (_items.TryGetValue(url, out replaced))
            {
                _cacheBytes -= (long)replaced.Bitmap.Width * replaced.Bitmap.Height * 4;
                _order.Remove(url); replaced.DropFromCache();
            }
            SharedBitmap shared = new SharedBitmap(bitmap);
            _items[url] = shared;
            _order.Add(url);
            _cacheBytes += (long)bitmap.Width * bitmap.Height * 4;
            while (_order.Count > Capacity || _cacheBytes > MaxCacheBytes)
            {
                string oldest = _order[0];
                _order.RemoveAt(0);
                SharedBitmap victim;
                if (_items.TryGetValue(oldest, out victim))
                {
                    _items.Remove(oldest);
                    _cacheBytes -= (long)victim.Bitmap.Width * victim.Bitmap.Height * 4;
                    // 淘汰 = 缓存放手；还在被借着的（正在画的那一帧）照样有效
                    victim.DropFromCache();
                }
            }
            return shared;
        }
    }

    /// <summary>取表情图响应，**逐跳**校验重定向（和上游中转同一套判据）。
    /// 入口 TryGet 已经校验过初始地址，这里复核的是重定向产生的新地址；
    /// 内部的 FetchNow 测试路径允许直接给回环地址（本地测试服务器要用）。</summary>
    private static HttpWebResponse OpenImageUpstream(string url)
    {
        string current = url;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (redirect > 0 && !LiveHub.IsSafeRemoteHttpUrl(current))
                throw new InvalidDataException("表情图重定向到不安全地址，已拒绝");
            // 连接目标钉在已校验的公网地址上，Host 头/SNI/证书名校验仍是原域名（TOCTOU 封口）；
            // 入口 TryGet 校验过初始地址，内部的 FetchNow 测试路径允许直接给本地服务器地址。
            // Referer 按**图片自己的域**给：原来不管哪个平台都发 B站 的 Referer，
            // 抖音/虎牙 的 CDN 有防盗链时就会 403（评估报告 §4）。跳转后按新地址重算。
            HttpWebResponse response = LiveHub.OpenRemoteResponse(current, 6000, "Mozilla/5.0",
                RefererFor(current), null, redirect == 0);
            int status = (int)response.StatusCode;
            if (status < 300 || status >= 400) return response;
            string location = response.Headers["Location"];
            response.Close();
            Uri next;
            if (location == null || !Uri.TryCreate(new Uri(current), location, out next))
                throw new InvalidDataException("表情图收到无效重定向");
            current = next.AbsoluteUri;
        }
        throw new InvalidDataException("表情图重定向次数过多");
    }

    private Bitmap Download(string url)
    {
        lock (_gate) { _requestCount++; }
        try
        {
            // ⚠️ OpenImageUpstream 里**不写 `request.Proxy = null`**：那等于强制绕过系统/IE 代理，
            //    在只能通过代理出网的机器上，所有表情图都会静默下载失败（评估报告 §4）。
            using (HttpWebResponse response = OpenImageUpstream(url))
            {
                if (response.ContentLength > MaxBytes)
                {
                    Diagnostics.Report("emoticon-image", new InvalidOperationException(
                        "图片超过大小上限（" + response.ContentLength + " 字节 > " + MaxBytes + "）：" + url));
                    return null;
                }
                using (Stream stream = response.GetResponseStream())
                {
                    byte[] data = ReadCapped(stream, MaxBytes);
                    if (data == null)
                    {
                        Diagnostics.Report("emoticon-image", new InvalidOperationException(
                            "图片响应体超过 " + (MaxBytes / 1024 / 1024) + " MB 上限，已丢弃：" + url));
                        return null;
                    }
                    Bitmap bitmap = DecodeImage(data);
                    if (bitmap == null) return null;
                    if (bitmap.Width > MaxDimension || bitmap.Height > MaxDimension)
                    {
                        Diagnostics.Report("emoticon-image", new InvalidOperationException(
                            "图片尺寸超过上限（" + bitmap.Width + "x" + bitmap.Height + "）：" + url));
                        bitmap.Dispose();
                        return null;
                    }
                    return bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            // ⚠️ 原来是空 catch —— 图片下载失败**完全静默**，表现出来就是
            //    「弹幕里的表情一直是文字」，而日志里查不到任何线索。
            //    失败只影响这一张图、不能拖垮弹幕流，所以照旧吞异常，但必须留痕。
            Diagnostics.Report("emoticon-image", ex);
            return null;
        }
    }

    /// <summary>解码图片字节。WebP 走 WebpImage（内置 VP8L 解码器 / WIC 分层），其余交给 GDI+。</summary>
    internal static Bitmap DecodeImage(byte[] data)
    {
        if (!DanmakuImageHeader.IsWithinLimit(data)) return null;
        // ⚠️ WebP 不能交给 `Image.FromStream`：它会抛 `ArgumentException: 参数无效`，
        //    而抖音的表情图**全是 WebP**（B站 的是 PNG，不受影响）。
        if (WebpImage.LooksLikeWebp(data)) return WebpImage.Decode(data);
        try
        {
            using (MemoryStream memory = new MemoryStream(data))
            using (Image image = Image.FromStream(memory))
                return new Bitmap(image);
        }
        catch (Exception ex)
        {
            Diagnostics.Report("emoticon-image-decode", ex);
            return null;
        }
    }

    /// <summary>带上限的读取：超过 limit 返回 null（**不再无限制读进内存**）。</summary>
    internal static byte[] ReadCapped(Stream stream, int limit)
    {
        using (MemoryStream memory = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + read > limit) return null;
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
    }

    /// <summary>按图片所在域给出 Referer（域匹配用「相等或子域」，避免被 evildouyin.com 骗到）。</summary>
    internal static string RefererFor(string url)
    {
        string host = "";
        string scheme = "https";
        try
        {
            Uri uri = new Uri(url);
            host = uri.Host.ToLowerInvariant();
            scheme = uri.Scheme;
        }
        catch (Exception) { return "https://live.bilibili.com/"; }

        if (HostIs(host, "douyin.com") || HostIs(host, "douyinpic.com") || HostIs(host, "douyinstatic.com")
            || HostIs(host, "douyincdn.com")) return "https://live.douyin.com/";
        if (HostIs(host, "huya.com") || HostIs(host, "huyacdn.com") || HostIs(host, "msstatic.com"))
            return "https://www.huya.com/";
        if (HostIs(host, "douyu.com") || HostIs(host, "douyucdn.cn") || HostIs(host, "douyucdn2.cn"))
            return "https://www.douyu.com/";
        if (HostIs(host, "bilibili.com") || HostIs(host, "hdslb.com") || HostIs(host, "bilivideo.com"))
            return "https://live.bilibili.com/";
        // 认不出的域：给自己的 origin，别把 B站 的 Referer 发给第三方
        return scheme + "://" + host + "/";
    }

    private static bool HostIs(string host, string domain)
    {
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (SharedBitmap image in _items.Values) image.DropFromCache();
            _items.Clear(); _order.Clear(); _retryAfter.Clear(); _failCount.Clear(); _cacheBytes = 0;
        }
    }
}

// --------------------------------------------------------------------------------------------
// 一条正在飞的弹幕
// --------------------------------------------------------------------------------------------
internal sealed class FlyingDanmaku
{
    public DanmakuItem Source;
    /// 渲染好的位图句柄。**只要这条弹幕还在列表里，它就一直有效** ——
    /// 缓存淘汰 / 换房间都只是「缓存放手」，不会把这条弹幕正在用的位图释放掉（见 SharedBitmap）。
    /// 摘掉这条弹幕时必须 Release()（见 DanmakuRenderer.ForgetFlying）。
    public SharedBitmap Handle;
    public float X;
    public float Y;
    public float Width;
    public float Height;
    public float EmoteBox;     // 表情弹幕的图片边长（0 = 不是表情）
    public float Speed;        // px/s，向左
    public int Track;
    public bool Fixed;         // 顶部 / 底部
    public DateTime FixedUntil;
    // —— 表情图片是**异步下载**的，这几个字段用于「图到了再补画一次」——
    //    见 DanmakuTextCache.HasPendingImage 的说明：位图只在创建时渲染一次并存这里，
    //    不补画的话首次出现的表情会永久停在文字兜底。
    public string Label;       // 渲染用的完整文本（重取位图时要原样再传一遍）
    public Color TextColor;
    public float FontSize;
    public bool EmoteSettled;  // 图片已全部就位（或本来就没表情）→ 不必再检查
}

// --------------------------------------------------------------------------------------------
// 弹幕调度 + 绘制
// --------------------------------------------------------------------------------------------
internal sealed class DanmakuRenderer : IDisposable
{
    private readonly DanmakuConfig _config;
    private readonly DanmakuTextCache _textCache = new DanmakuTextCache();
    private readonly DanmakuImageCache _imageCache = new DanmakuImageCache();
    private readonly List<FlyingDanmaku> _flying = new List<FlyingDanmaku>();
    private readonly List<SuperChatItem> _superChats = new List<SuperChatItem>();
    private readonly Dictionary<string, DateTime> _recent = new Dictionary<string, DateTime>();

    private FlyingDanmaku[] _trackLast = new FlyingDanmaku[0];
    private float _trackHeight = 33f;
    private float _duration = 8f;      // 一条弹幕从右边缘到完全离开左边缘的秒数（B站 是固定时长，不是固定速度）
    private float _uiScale = 1f;       // 画面越大字越大：clientHeight / ScaleReferenceHeight
    private float _lineFactor = 1.33f;  // 位图高度 / 字号（resize 时实测，用来卡字号上限）
    private float _maxSize = 60f;       // 单条弹幕的最大字号，保证位图不超过一条轨道
    internal int DroppedCount;          // 因为没有空闲轨道而丢掉的条数（重叠换来的可读性不值）
    private int _width = 1280;
    private int _height = 720;
    private int _areaHeight = 720;

    public string TransientText;
    public DateTime TransientUntil = DateTime.MinValue;

    public DanmakuRenderer(DanmakuConfig config)
    {
        _config = config;
        // 平台表情图片复用现有的图片缓存（下载 + LRU）—— 这样一条表情在多条弹幕之间只下载一次。
        // TryGet 在没下载好时返回 null 并**顺手踢一次下载**，正是我们想要的语义。
        _textCache.EmoticonImage = delegate (string url) { return _imageCache.TryGet(url); };   // 借出的一份由调用方 Release
    }

    /// 接入当前房间的表情字典（`[大笑]` 这类方括号代码 → 图片）。
    ///
    /// 字典是按房间的（B站 有「房间专属表情」包），所以换房间必须重新调用；
    /// 同时要丢掉文本位图缓存 —— 老缓存里的 key 带着旧的「图片就绪指纹」，留着只会白占额度。
    public void SetEmoticons(EmoticonDictionary emoticons)
    {
        _textCache.Emoticons = emoticons;
        _textCache.Clear();
    }

    public int ActiveCount { get { return _flying.Count; } }
    public void Dispose()
    {
        foreach (FlyingDanmaku flying in _flying) if (flying.Handle != null) flying.Handle.Release();
        _flying.Clear(); _textCache.Clear(); _imageCache.Dispose(); _superChats.Clear();
    }
    public int SuperChatActiveCount { get { return _superChats.Count; } }
    public float UiScale { get { return _uiScale; } }

    /// 画面上「标准字号」对应的像素大小：B站 服务端给 25px，再乘用户字号设置与画面缩放。
    private float BaseFontSize()
    {
        return 25f * (_config.FontScalePercent / 100f) * _uiScale;
    }



    /// 配置被热重载时调用：字号 / 行距 / 速度 / 区域都要重算，缓存的文字位图作废。
    public void ApplyConfig()
    {
        _width = 0;
        _height = 0;
        _textCache.ColorEmoji = _config.ShowColorEmoji;
        _textCache.Clear();
    }

    private int _displayDpi = 96;
    private bool _displayChanged;
    internal void SetDisplayDpi(int dpi)
    {
        dpi = Math.Max(48, Math.Min(960, dpi));
        if (dpi != _displayDpi) { _displayDpi = dpi; _displayChanged = true; }
    }
    private Color SurfaceColor(Color color)
    {
        return !_config.SolidText ? color : Color.FromArgb(
            (int)Math.Round(color.A * _config.OpacityPercent / 100.0), color.R, color.G, color.B);
    }

    public void Resize(int width, int height)
    {
        if (width < 16) width = 16;
        if (height < 16) height = 16;
        if (width == _width && height == _height && !_displayChanged) return;
        int previousWidth = _width, previousHeight = _height;
        bool displayChanged = _displayChanged; _displayChanged = false;
        _width = width;
        _height = height;
        _areaHeight = Math.Max(24, (int)(height * _config.DisplayAreaPercent / 100.0));
        // 防挡字幕（B站 同名选项）：底部留 20% 不放弹幕，给字幕和播放器 UI 让位。
        // mpv 的字幕默认贴在画面底部，弹幕压上去会看不清 —— 所以这一项默认开。
        if (_config.AntiBlockSubtitle && _config.DisplayAreaPercent > 40)
            _areaHeight = Math.Max(24, (int)(_areaHeight * 0.8));
        // 字号随播放画面等比缩放：画面越高字越大，弹幕相对画面的观感才恒定。
        // 基准默认 720 —— 也就是「25px 标准字号是 720p 下的观感」。
        float previousScale = _uiScale;
        _uiScale = _config.ScaleWithWindow
            ? (float)height / Math.Max(180, _config.ScaleReferenceHeight)
            : _displayDpi / 96f;
        if (_uiScale < 0.45f) _uiScale = 0.45f;
        if (_uiScale > 4.5f) _uiScale = 4.5f;
        if (displayChanged || Math.Abs(_uiScale - previousScale) > 0.001f)
        {
            _textCache.Clear();
            float ratio = _uiScale / Math.Max(0.01f, previousScale);
            foreach (FlyingDanmaku f in _flying)
            {
                f.FontSize *= ratio; f.EmoteBox *= ratio;
                SharedBitmap fresh = _textCache.Get(f.Label, f.TextColor, f.FontSize,
                    _config.FontFamily, _config.StrokeEnabled, _config.StrokeWidth);
                SharedBitmap old = f.Handle; f.Handle = fresh;
                if (old != null) old.Release();
                f.Width = fresh.Bitmap.Width + f.EmoteBox;
                f.Height = Math.Max(fresh.Bitmap.Height, f.EmoteBox);
                if (previousWidth > 0) f.X *= (float)width / previousWidth;
                if (previousHeight > 0) f.Y *= (float)height / previousHeight;
            }
        }
        // B站 的弹幕是**固定时长**穿过整屏（所以越长的弹幕跑得越快），速度设置改的是这个时长。
        // 实测这样出来的观感才像 B站；用固定像素速度会让长弹幕显得拖沓。
        _duration = 8f / (_config.SpeedPercent / 100f);
        if (_duration < 2f) _duration = 2f;
        // 轨道高度必须**实测**，不能按字号乘一个猜出来的系数。
        // 踩过：先用 baseSize*1.32 估，但 GDI+ 实测行高约是字号的 1.33 倍、再加描边内边距，
        // 结果**正常字号的弹幕位图就已经比轨道高** → 上下两轨天然重叠 2~3px。
        // emoji 框比字更高，所以取两者较大值。
        int pad = _config.StrokeEnabled ? _config.StrokeWidth + 2 : 2;
        float textBox;
        using (Bitmap probe = new Bitmap(1, 1))
        using (Graphics measure = Graphics.FromImage(probe))
        using (Font font = new Font(_config.FontFamily, BaseFontSize(), FontStyle.Regular, GraphicsUnit.Pixel))
            textBox = measure.MeasureString("汉Hg", font).Height + pad * 2f;
        _lineFactor = textBox / Math.Max(1f, BaseFontSize());     // 位图高度 ≈ 字号 × 这个系数
        float emojiBox = BaseFontSize() * 1.18f + 6f;
        _trackHeight = Math.Max(textBox, emojiBox) * (_config.LineGapPercent / 100f);
        // 单条弹幕的字号上限：服务端可能给更大字号（B站 的舰长弹幕等），照单全收就会比轨道高、
        // 压到上下两轨上。这里压到「位图正好装得进一条轨道」为止。
        _maxSize = _trackHeight / Math.Max(1.05f, _lineFactor);
        if (_maxSize < 10f) _maxSize = 10f;
        int tracks = (int)Math.Floor(_areaHeight / _trackHeight);
        if (tracks < 1) tracks = 1;
        if (tracks > 60) tracks = 60;
        _trackLast = new FlyingDanmaku[tracks];
    }

    public void ShowTransient(string text)
    {
        TransientText = text;
        TransientUntil = DateTime.UtcNow.AddSeconds(2.2);
    }

    // ---------------------------------------------------------------- 过滤
    public bool IsBlocked(DanmakuItem item)
    {
        if (!_config.Enabled) return true;
        if (item.UserLevel < _config.MinUserLevel) return true;
        if (_config.MinMedalLevel > 0 && item.MedalLevel < _config.MinMedalLevel) return true;
        string text = item.Text == null ? "" : item.Text;
        for (int i = 0; i < _config.BlockUsers.Length; i++)
        {
            string rule = _config.BlockUsers[i];
            if (rule == null || rule.Length == 0) continue;
            if (item.UserName != null && item.UserName.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        for (int i = 0; i < _config.BlockKeywords.Length; i++)
        {
            string rule = _config.BlockKeywords[i];
            if (rule == null || rule.Length == 0) continue;
            if (rule.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, rule.Substring(3),
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
                }
                catch (Exception) { }
            }
            else if (text.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// 断线重连后服务端可能补发；同一内容 5 秒内只显示一条。
    private bool IsDuplicate(DanmakuItem item)
    {
        DateTime now = DateTime.UtcNow;
        if (_recent.Count > 400) _recent.Clear();
        string key = item.Text;
        DateTime last;
        if (_recent.TryGetValue(key, out last) && (now - last).TotalSeconds < 5) return true;
        _recent[key] = now;
        return false;
    }

    // ---------------------------------------------------------------- 入队
    public void AddDanmaku(DanmakuItem item)
    {
        if (_flying.Count >= _config.MaxOnScreen) return;
        if (_config.BlockDuplicate && IsDuplicate(item)) return;

        float baseSize = BaseFontSize();
        float size = item.FontSize > 0
            ? item.FontSize * (_config.FontScalePercent / 100f) * _uiScale
            : baseSize;
        if (size < 8f) size = baseSize;
        if (size > _maxSize) size = _maxSize;     // 别超过一条轨道的高度，否则会压到上下轨

        float emoteBox = 0f;
        Color color = ColorFromInt(item.Color);
        if (item.IsEmoticon && _config.ShowEmoticon)
        {
            emoteBox = size * 1.7f;
            SharedBitmap kick = _imageCache.TryGet(item.EmoticonUrl);   // 只是踢一次下载
            if (kick != null) kick.Release();                          // 借出来的那份当场还
        }
        string label = BuildLabel(item, true);
        if (label.Length == 0) label = " ";
        SharedBitmap bitmap = _textCache.Get(label, color, size, _config.FontFamily,
            _config.StrokeEnabled, _config.StrokeWidth);

        FlyingDanmaku flying = new FlyingDanmaku();
        flying.Source = item;
        flying.Handle = bitmap;        // 借出的一份：这条弹幕摘掉时还（ForgetFlying）
        flying.EmoteBox = emoteBox;
        flying.Width = bitmap.Bitmap.Width + emoteBox;
        flying.Height = Math.Max(bitmap.Bitmap.Height, emoteBox);
        // 记下重取位图所需的一切 + 先判断一次「有没有表情图还没下载好」。
        // 有的话这一帧画出来的是文字兜底，等图到了要补画（见 Draw 里的逻辑）。
        flying.Label = label;
        flying.TextColor = color;
        flying.FontSize = size;
        flying.EmoteSettled = !_textCache.HasPendingImage(label, _config.FontFamily);

        if (item.Mode == 4 || item.Mode == 5)
        {
            flying.Fixed = true;
            flying.FixedUntil = DateTime.UtcNow.AddSeconds(4);
            flying.X = Math.Max(0f, (_width - flying.Width) / 2f);
            int slot = FindFixedSlot(item.Mode);
            flying.Track = slot;
            float slotHeight = Math.Max(flying.Height, size * 1.5f);
            flying.Y = item.Mode == 5
                ? slot * slotHeight
                : _areaHeight - (slot + 1) * slotHeight;
            if (flying.Y < 0) flying.Y = 0;
        }
        else
        {
            // 固定时长 → 越长的弹幕越快（和 B站 一致）
            flying.Speed = (_width + flying.Width) / _duration;
            int track = FindTrack(flying);
            if (track < 0)
            {
                // 没有空闲轨道：丢弃，宁可少一条也不重叠。
                // ⚠️ 必须把已经借出的位图还了 —— 2026-09-27 改成所有权模型时这里漏了，
                //    而「轨道全忙」在热门房间是**常态**（DroppedCount 就是数它的），
                //    漏还等于每丢一条就漏一张位图（不再崩，但会稳定积 GDI 对象）。
                DroppedCount++;
                ReleaseHandle(flying);
                return;
            }
            flying.Track = track;
            flying.X = _width + 4;
            flying.Y = track * _trackHeight;
            if (flying.Y + flying.Height > _areaHeight)
                flying.Y = Math.Max(0f, _areaHeight - flying.Height);
            if (track >= 0 && track < _trackLast.Length) _trackLast[track] = flying;
        }
        _flying.Add(flying);
    }

    /// 找一条放得下的滚动轨道；**没有就返回 -1（调用方丢弃这条）**。
    ///
    /// 判据（和 B站 / danmaku2ass 一致）：① 该轨道上最后那条已经完全进入屏幕并留出间距；
    /// ② 新的在旧的离开屏幕之前**不会追上**它。
    ///
    /// 追尾判据用**闭式解**而不是分情况讨论：
    ///   新弹幕左边缘 x_new(t) = _width - cs·t，旧的右边缘 x_old(t) = lastRight - ls·t。
    ///   要求在整个 [0, 旧的完全离开] 区间内 x_new(t) ≥ x_old(t) + gap。
    ///   两者间距随时间单调收窄（当 cs > ls），最紧的时刻就是旧弹幕刚离开的瞬间 t* = lastRight / ls，
    ///   此时 x_old = 0，于是条件化简成一行：  **cs · t* ≤ _width - gap**
    /// （cs ≤ ls 时该式自然成立，不必特判。）
    private int FindTrack(FlyingDanmaku candidate)
    {
        if (_trackLast.Length == 0) return 0;
        const float gap = 14f;
        for (int i = 0; i < _trackLast.Length; i++)
        {
            FlyingDanmaku last = _trackLast[i];
            if (last == null) return i;
            float lastRight = last.X + last.Width;
            if (lastRight > _width - gap) continue;                 // 还没完全进来
            float lastExitIn = lastRight / last.Speed;              // 旧的完全离开左边还要多久
            if (candidate.Speed * lastExitIn > _width - gap) continue;   // 会追尾
            return i;
        }
        // 全忙 —— **丢掉这条，不硬塞**。
        // 以前是「挑最闲的一条塞进去」，结果屏幕上叠成一团、整屏都读不了；
        // 丢一条只少一条，B站 在弹幕密集时也是这么做的。
        return -1;
    }

    private int FindFixedSlot(int mode)
    {
        int used = 0;
        for (int i = 0; i < _flying.Count; i++)
            if (_flying[i].Fixed && _flying[i].Source.Mode == mode && _flying[i].Track >= used)
                used = _flying[i].Track + 1;
        return used;
    }

    private string BuildLabel(DanmakuItem item, bool includeText)
    {
        StringBuilder builder = new StringBuilder();
        if (_config.ShowGuard && item.GuardLevel > 0)
            builder.Append(item.GuardLevel == 1 ? "[总督] " : item.GuardLevel == 2 ? "[提督] " : "[舰长] ");
        if (_config.ShowMedal && item.MedalName != null && item.MedalName.Length > 0)
            builder.Append('[').Append(item.MedalName).Append(item.MedalLevel).Append("] ");
        if (_config.ShowUserLevel && item.UserLevel > 0)
            builder.Append('U').Append(item.UserLevel).Append(' ');
        if (_config.ShowUserName && item.UserName != null && item.UserName.Length > 0)
            builder.Append(item.UserName).Append(": ");
        if (includeText) builder.Append(item.Text);
        return builder.ToString();
    }

    internal static Color ColorFromInt(int value)
    {
        if (value == 0) return Color.White;
        return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    /// SC 保留条数的**硬顶**（不暴露给用户，与「滚动阈值 ScMaxStack」是两回事）。
    /// 覆盖层是常驻进程，完全不封顶的话挂一整天会越攒越多 —— 每张还缓存位图。
    internal const int SuperChatHardCap = 200;

    public void AddSuperChat(SuperChatItem item)
    {
        // 同一条 SC 重复到达（重连补发）只留一条
        for (int i = 0; i < _superChats.Count; i++)
            if (item.Id != 0 && _superChats[i].Id == item.Id) _superChats.RemoveAt(i);
        _superChats.Add(item);
        // ⚠️ 2026-09-28 语义变更：`ScMaxStack` 现在表示**滚动阈值**（超过几张就启用滚动），
        //    **不再是淘汰上限**。设计意图（用户确认）：
        //      ≤ N 张 → 正常堆叠，全都看得见；
        //      > N 张 → **一张都不丢**，可视区只留前 N 张那么高，其余用滚轮翻（见 DrawSuperChats）。
        //    所以这里只留一个 200 的**硬顶**：覆盖层是常驻进程，完全不封顶的话挂一整天会越攒越多
        //    （每张还缓存位图）。这个硬顶不暴露给用户，和「滚动阈值」是两回事。
        while (_superChats.Count > SuperChatHardCap) _superChats.RemoveAt(0);
        _scScroll = 0;      // 来了新的就回到顶部，保证看得见最新的

    }

    public void RemoveSuperChat(long id)
    {
        for (int i = _superChats.Count - 1; i >= 0; i--)
            if (_superChats[i].Id == id) _superChats.RemoveAt(i);
    }

    public void ClearSuperChats() { _superChats.Clear(); }

    // ---------------------------------------------------------------- 推进
    public void Advance(DateTime now, float dt)
    {
        for (int i = _flying.Count - 1; i >= 0; i--)
        {
            FlyingDanmaku f = _flying[i];
            if (f.Fixed)
            {
                if (now >= f.FixedUntil) ForgetFlying(i);
                continue;
            }
            f.X -= f.Speed * dt;
            if (f.X + f.Width < 0) ForgetFlying(i);
        }
        for (int i = _superChats.Count - 1; i >= 0; i--)
            if (now >= _superChats[i].ExpiresAt) _superChats.RemoveAt(i);
    }

    // ---------------------------------------------------------------- 绘制
    public void Draw(Graphics g)
    {
        for (int i = 0; i < _flying.Count; i++)
        {
            FlyingDanmaku f = _flying[i];
            // ⚠️ 「图到了补画一次」—— 用户反馈 `[虎年]` 一直显示成文字，根因就在这里：
            //    位图是弹幕**创建时**渲染的，那一刻表情图片通常还没下载完 → 画成字面量，
            //    而位图存在这条弹幕上、之后只 DrawImage 不重算 → **永远不会变成图片**。
            //    所以只要还没就位，每帧问一次；一到位就重取位图换上。
            if (!f.EmoteSettled && !_textCache.HasPendingImage(f.Label, _config.FontFamily))
            {
                SharedBitmap refreshed = _textCache.Get(f.Label, f.TextColor, f.FontSize,
                    _config.FontFamily, _config.StrokeEnabled, _config.StrokeWidth);
                if (refreshed != null && !object.ReferenceEquals(refreshed, f.Handle))
                {
                    SharedBitmap previous = f.Handle;
                    f.Handle = refreshed;         // 借新的
                    if (previous != null) previous.Release();   // 还旧的（这一条再也不用它了）
                    f.Width = refreshed.Bitmap.Width + f.EmoteBox;
                    f.Height = Math.Max(refreshed.Bitmap.Height, f.EmoteBox);
                }
                else if (refreshed != null)
                {
                    refreshed.Release();          // 同一张：多借的那份当场还掉
                }
                f.EmoteSettled = true;
            }
            if (f.X > _width || f.X + f.Width < 0) continue;
            float textX = f.X;
            // 兜底（不再是主要手段）：所有权已经保证「手里的位图一定有效」，
            // 这里只防 GDI+ 别的意外（显存不足之类）——**任何**一条弹幕画不出来都只丢它自己，
            // 不许把整帧 / 整个覆盖层带走。
            SharedBitmap emote = null;
            try
            {
                if (f.EmoteBox > 0f)
                {
                    emote = _imageCache.TryGet(f.Source.EmoticonUrl);   // 借出一份
                    if (emote != null)
                    {
                        float box = f.EmoteBox;
                        float top = f.Y + (f.Height - box) / 2f;
                        g.DrawImage(emote.Bitmap, f.X, top, box, box);
                    }
                    textX += f.EmoteBox;
                }
                TextBlit.Draw(g, f.Handle.Bitmap, textX, f.Y);
            }
            catch (Exception ex)
            {
                DropUndrawable(f, ex);
            }
            finally
            {
                if (emote != null) emote.Release();     // 表情图这一帧用完就还
            }
        }
        if (_config.ScMode != "off") DrawSuperChats(g);
        DrawTransient(g);
    }

    /// 这条弹幕画不出来（位图失效 / GDI+ 异常）：从列表里摘掉，并**留一次痕**。
    /// 只报第一次 —— 一个坏位图会连续失败 30 帧，每帧写一次日志会把日志刷爆。
    private void DropUndrawable(FlyingDanmaku flying, Exception error)
    {
        ForgetFlying(flying);
        if (_undrawableReported) return;
        _undrawableReported = true;
        Diagnostics.Report("danmaku-draw", error);
    }

    private bool _undrawableReported;

    /// 摘掉一条弹幕：**把借来的位图还回去**。
    /// 所有从 _flying 里移除的地方都必须走这里 —— 漏一处的后果是 GDI 对象只增不减（泄漏），
    /// 而不是崩溃（崩溃那条路已经被所有权堵死了）。
    private void ForgetFlying(int index)
    {
        // ⚠️ 别调 `ForgetFlying(_flying[index])` 再 RemoveAt(index)：那个重载自己就 Remove 了，
        //    列表已经变短，RemoveAt(index) 立刻越界 —— 2026-09-27 被自己的回归抓到。
        FlyingDanmaku flying = _flying[index];
        ReleaseHandle(flying);
        _flying.RemoveAt(index);
    }

    private void ForgetFlying(FlyingDanmaku flying)
    {
        if (flying == null) return;
        ReleaseHandle(flying);
        _flying.Remove(flying);
    }

    private static void ReleaseHandle(FlyingDanmaku flying)
    {
        if (flying.Handle != null)
        {
            flying.Handle.Release();
            flying.Handle = null;
        }
    }

    private void DrawTransient(Graphics g)
    {
        if (TransientText == null || DateTime.UtcNow >= TransientUntil) return;
        using (Font font = new Font(_config.FontFamily, 20f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        using (SolidBrush back = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
        {
            SizeF size = g.MeasureString(TransientText, font);
            float x = (_width - size.Width) / 2f;
            g.FillRectangle(back, x - 12, 8, size.Width + 24, size.Height + 10);
            g.DrawString(TransientText, font, brush, x, 13);
        }
    }

    private float _scScroll;      // 已经往下卷过的像素（0 = 从最新那张开始看）
    private bool _scScrollable;   // 这一帧 SC 是否超出一屏（供覆盖层决定要不要吃鼠标）

    /// SC 是否超出一屏。
    /// 覆盖层用它来决定 WM_NCHITTEST 放不放行 —— 放行的判据是
    /// 「能点（ScClickable）**或**需要滚」，两者互不牵连：
    /// 关掉点击只是想别误触，不代表不想滚轮翻看。
    public bool SuperChatScrollable { get { return _scScrollable; } }

    /// 滚轮：delta > 0（往前滚）看**更新**的卡片，delta < 0 看更早的。
    /// 覆盖层在 WM_NCHITTEST 里把 SC 区域放行给自己，所以滚轮能收到。
    public void ScrollSuperChats(int delta)
    {
        _scScroll -= delta * 0.6f;
        if (_scScroll < 0f) _scScroll = 0f;
    }

    /// SC 可视区的**上边**（纯函数，便于离线测试四个角）。
    ///
    /// · 顶部两个角（tl/tr）：固定从上边距 12 开始往下排；
    /// · 底部两个角（bl/br）：**贴在画面下边** —— 上边由下边倒推。
    ///
    /// ⚠️ 2026-09-28 修：旧代码的 bottom 分支算出来恒等于「margin」（≈画面顶部），
    ///    可视高度又几乎是整屏 —— 于是「左下/右下」和「左上/右上」完全一样，
    ///    四个角实际只有左右生效。这个函数把那条规则固定下来并让测试能锁住它。
    internal static float ScViewTop(bool bottom, float height, float margin, float viewHeight)
    {
        return bottom ? Math.Max(6f, height - margin - viewHeight) : 12f;
    }

    /// SC 内容的起始 y（纯函数）。
    /// 内容比可视区矮时：顶部角从 viewTop 往下排；**底部角贴着下边** ——
    /// 这样新卡片到达时整组是「往上顶」的，符合底部角的直觉。
    internal static float ScContentTop(bool bottom, float viewTop, float viewHeight, float total)
    {
        if (bottom && total < viewHeight) return viewTop + (viewHeight - total);
        return viewTop;
    }

    /// SC 角标（badge 模式）的 y（纯函数）。同样分上下。
    internal static float ScBadgeY(bool bottom, float height, float margin, float badgeHeight)
    {
        return bottom ? Math.Max(6f, height - margin - badgeHeight) : 12f;
    }

    private void DrawSuperChats(Graphics g)
    {
        _scHits.Clear();
        _scScrollable = false;
        if (_superChats.Count == 0) return;
        // SC 的真实尺寸与配色规则照搬 B站 全屏 SC 挂件（components/ScList/index.less + index.tsx）：
        //   卡片宽 270px、字号 12px、圆角 6px；顶部区 background_color + background_bottom_color 描边、
        //   正文区 background_bottom_color + message_font_color 文字；卡片**下方**另有一条 4px 圆角进度条。
        float scale = _config.ScScalePercent / 100f * _uiScale;
        float width = 270f * scale;
        float margin = 10f * scale;                      // .sc 的 margin-right
        bool left = _config.ScCorner == "tl" || _config.ScCorner == "bl";
        bool bottom = _config.ScCorner == "br" || _config.ScCorner == "bl";
        float x = left ? margin : _width - width - margin;

        if (_config.ScMode == "badge")
        {
            _scScrollable = false;
            // ⚠️ 2026-09-28 修：这里原来把 Y 写死成 12f，于是「左下/右下」的角标也贴在画面顶部 ——
            //    四个角只有左右生效。现在按 bottom 取上边距或下边距。
            float badgeH = 40f * scale;
            float badgeY = ScBadgeY(bottom, _height, margin, badgeH);
            DrawScBadge(g, x, badgeY, width, scale);
            if (_config.ScClickable) AddScHit(new RectangleF(x, badgeY - 12f, width, 12f + badgeH), 0);
            return;
        }

        // 先量一遍（**不画**），再按可视区裁剪着画。
        // 量宽是逐字符查精灵图的（O(n²)），所以高度**缓存在卡片自己身上** ——
        // 放开数量上限后每帧重量几十张会把帧率拖垮。
        Graphics measure = MeasureGraphics();
        int count = _superChats.Count;
        float gap = 4f * scale;
        float[] heights = new float[count];
        float total = 0f;
        for (int k = 0; k < count; k++)
        {
            SuperChatItem item = _superChats[count - 1 - k];
            if (item.CachedHeight <= 0f || Math.Abs(item.CachedScale - scale) > 0.001f)
            {
                item.CachedHeight = _config.ScMode == "compact"
                    ? MeasureScCompact(item, width, scale)
                    : MeasureScFull(item, x, width, scale, measure);
                item.CachedScale = scale;
            }
            heights[k] = item.CachedHeight;
            total += heights[k] + gap;
        }

        // 可视区：上留 12、下留 margin。
        //
        // ⚠️ 2026-09-28 修：原来 bottom 分支配的是
        //      viewTop    = Math.Max(6f, _height - margin - (_height - 2f*margin))   // ≈ margin（画面顶部）
        //      viewHeight = _height - margin - viewTop                                // ≈ 整屏
        //    也就是**左下/右下与左上/右上的垂直位置完全一样** —— 四个角实际只有「左/右」生效，
        //    用户设置的「左下」「右下」看不出区别。这是这一版要修的。
        float viewHeight = Math.Max(40f, _height - 12f - margin);

        // ★ 滚动阈值（ScMaxStack）：**超过 N 张就启用滚动** —— 一张都不丢，
        //   只是把可视高度收到「前 N 张」那么多，剩下的用滚轮翻。
        //   这是用户确认的设计：「同时超过 6 条的时候启用滚动」。
        //   注意判据是**张数**，不是「有没有放不下」：张数超了就收，即使此刻屏幕还够高。
        int threshold = _config.ScMaxStack;
        float capHeight = 0f;
        if (threshold > 0 && count > threshold)
            for (int k = 0; k < threshold && k < count; k++) capHeight += heights[k] + gap;
        viewHeight = ScViewHeightFor(count, threshold, viewHeight, capHeight);

        // 顶部两个角：可视区从画面顶部往下算；底部两个角：**贴在画面下边**
        float viewTop = ScViewTop(bottom, _height, margin, viewHeight);

        float maxScroll = Math.Max(0f, total - viewHeight);
        _lastMaxScroll = maxScroll;      // 拖动滚动条时要把比例换算回像素
        _scScrollable = maxScroll > 0.5f;
        if (_scScroll > maxScroll) _scScroll = maxScroll;
        if (_scScroll < 0f) _scScroll = 0f;

        // 裁剪到可视区，滚出去的卡片就不会画到画面外
        Region saved = g.Clip;
        g.SetClip(new RectangleF(x - 4f, viewTop, width + 8f, viewHeight), CombineMode.Intersect);

        // 内容比可视区矮时：顶部角从上往下排；底部角**贴着下边**（新卡片到达会把整组往上顶）
        float contentTop = ScContentTop(bottom, viewTop, viewHeight, total);
        float cursor = contentTop - _scScroll;
        float stackTop = Math.Max(viewTop, cursor);
        float stackBottom = viewTop;
        for (int k = 0; k < count; k++)
        {
            float h = heights[k];
            if (cursor + h >= viewTop && cursor <= viewTop + viewHeight)   // 只画看得见的
            {
                SuperChatItem item = _superChats[count - 1 - k];
                if (_config.ScMode == "compact") DrawScCompact(g, item, x, cursor, width, scale);
                else DrawScFull(g, item, x, cursor, width, scale, measure);
                if (_config.ScClickable)
                {
                    float closeSize = 16f * scale;
                    float closeX = x + width - closeSize - 6f * scale;
                    float closeY = cursor + 6f * scale;
                    DrawCloseButton(g, closeX, closeY, closeSize);
                    AddScHit(new RectangleF(closeX - 3f * scale, closeY - 3f * scale,
                        closeSize + 6f * scale, closeSize + 6f * scale), item.Id);
                }
                if (cursor + h > stackBottom) stackBottom = cursor + h;
            }
            cursor += h + gap;
        }
        g.Clip = saved;

        if (_config.ScClickable && stackBottom > stackTop)
            AddScHit(new RectangleF(x, stackTop, width, stackBottom - stackTop), 0);

        // 滚动条：超出可视区时画一条**可以拖的**滚动条 —— 不然用户根本不知道下面还有，
        // 而且只能靠滚轮（用户明确要求「有个滚动条，我可以控制滚动看」）。
        //
        // ⚠️ 2026-09-28：原来只是一根 3px 的装饰条、不参与命中，光标在它上面时
        //    WM_NCHITTEST 会把它放行给下面的播放器，根本拖不到。
        //    现在：加宽到 8px、记录轨道/滑块矩形，并在 HitTestScBar 里放行与拖动。
        _scBarTrack = RectangleF.Empty;
        _scBarThumb = RectangleF.Empty;
        if (maxScroll > 0.5f)
        {
            float trackY = viewTop, trackH = viewHeight;
            float thumbH = Math.Max(24f * scale, trackH * (viewHeight / total));
            float thumbY = trackY + (trackH - thumbH) * (_scScroll / maxScroll);
            float barW = 8f * scale;
            float barX = left ? x - (barW + 2f * scale) : x + width + 2f * scale;
            _scBarTrack = new RectangleF(barX, trackY, barW, trackH);
            _scBarThumb = new RectangleF(barX, thumbY, barW, thumbH);
            using (SolidBrush track = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                g.FillRectangle(track, _scBarTrack);
            using (SolidBrush thumb = new SolidBrush(Color.FromArgb(_scBarDragging ? 235 : 190, 255, 255, 255)))
                g.FillRectangle(thumb, _scBarThumb);
        }
        // 拖动中但布局变了（比如来了新 SC 导致 maxScroll 变 0），把拖动状态收掉，别留悬空
        if (_scBarDragging && _scBarThumb.Height <= 0f) _scBarDragging = false;
    }

    // ---- 滚动条的命中与拖动（供覆盖层在鼠标消息里调用）----

    private RectangleF _scBarTrack = RectangleF.Empty;
    private RectangleF _scBarThumb = RectangleF.Empty;
    private bool _scBarDragging;
    private float _scBarGrabOffset;      // 按下点相对滑块顶部的偏移，拖动时保持不跳

    /// 光标是否落在滚动条上（轨道或滑块都算）—— 覆盖层用它决定「放不放行给自己」。
    public bool HitTestScBar(int x, int y)
    {
        if (_scBarThumb.Height <= 0f) return false;
        // 横向再放宽 4px：8px 的条对鼠标还是偏细，给它一点容错
        RectangleF hit = _scBarTrack;
        hit.X -= 4f; hit.Width += 8f;
        return x >= hit.X && x <= hit.X + hit.Width && y >= hit.Y && y <= hit.Y + hit.Height;
    }

    /// 按下：命中滑块就进入拖动；命中轨道就先把滑块跳过去（像原生滚动条那样），再进入拖动。
    /// 返回 true 表示这次按下被滚动条吃掉了（覆盖层据此不要再当点击处理）。
    public bool ScBarMouseDown(int x, int y)
    {
        if (!HitTestScBar(x, y)) return false;
        float thumbY = _scBarThumb.Y, thumbH = _scBarThumb.Height;
        if (y < thumbY || y > thumbY + thumbH)
        {
            // 点在轨道上：滑块中心跳到鼠标处
            _scBarGrabOffset = thumbH / 2f;
            ScBarDragTo(y);
        }
        else
        {
            _scBarGrabOffset = y - thumbY;
        }
        _scBarDragging = true;
        return true;
    }

    /// 拖动：把滑块顶部映射回 _scScroll。
    public void ScBarDragTo(int y)
    {
        _scScroll = ScBarScrollFor(y, _scBarGrabOffset, _scBarTrack.Y, _scBarTrack.Height,
            _scBarThumb.Height, _lastMaxScroll);
    }

    /// 拖动映射（纯函数，便于离线测试）：
    /// 鼠标 y → 扣掉按下时的抓取偏移 → 在轨道行程里归一化到 [0,1] → 乘最大滚动量。
    internal static float ScBarScrollFor(float y, float grabOffset, float trackY, float trackH,
                                        float thumbH, float maxScroll)
    {
        float travel = trackH - thumbH;
        if (travel <= 0f || maxScroll <= 0f) return 0f;
        float ratio = ((y - grabOffset) - trackY) / travel;
        if (ratio < 0f) ratio = 0f;
        if (ratio > 1f) ratio = 1f;
        return ratio * maxScroll;
    }

    /// 滚动阈值收窄（纯函数，便于离线测试）：
    /// **张数 > 阈值** 就把可视高度收到「前 N 张」那么高（其余靠滚动看），
    /// 否则保持自然高度。阈值 <= 0 表示不按张数收窄。
    internal static float ScViewHeightFor(int count, int threshold, float naturalViewHeight, float capHeight)
    {
        if (threshold > 0 && count > threshold && capHeight > 0f && capHeight < naturalViewHeight)
            return Math.Max(40f, capHeight);
        return naturalViewHeight;
    }

    public void ScBarMouseUp() { _scBarDragging = false; }

    public bool ScBarDragging { get { return _scBarDragging; } }

    /// 上一帧算出的最大滚动量（拖动时把比例换算回像素要用它）
    private float _lastMaxScroll;

    /// 卡片右上角的关闭按钮（照 B站 挂件的 close.svg：一个半透明圆底 + 白色叉）
    private void DrawCloseButton(Graphics g, float x, float y, float size)
    {
        using (SolidBrush back = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillEllipse(back, x, y, size, size);
        using (Pen pen = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, size * 0.12f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            float pad = size * 0.32f;
            g.DrawLine(pen, x + pad, y + pad, x + size - pad, y + size - pad);
            g.DrawLine(pen, x + size - pad, y + pad, x + pad, y + size - pad);
        }
    }

    private Graphics _measureGraphics;

    private Graphics MeasureGraphics()
    {
        if (_measureGraphics == null)
            _measureGraphics = Graphics.FromImage(new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb));
        return _measureGraphics;
    }

    private void DrawScBadge(Graphics g, float x, float y, float width, float scale)
    {
        SuperChatItem newest = _superChats[_superChats.Count - 1];
        string text = "SC ×" + _superChats.Count + "  " + Shorten(newest.Message, 12);
        using (Font font = new Font(_config.FontFamily, 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(newest.MessageFontColor))
        {
            SizeF size = g.MeasureString(text, font);
            float w = size.Width + 20 * scale, h = size.Height + 10 * scale;
            using (GraphicsPath path = Rounded(new RectangleF(x + width - w, y, w, h), h / 2f))
            using (SolidBrush back = new SolidBrush(newest.BackgroundBottomColor)) g.FillPath(back, path);
            g.DrawString(text, font, brush, x + width - w + 10 * scale, y + 5 * scale);
        }
    }

    private float MeasureScCompact(SuperChatItem item, float width, float scale)
    {
        return 36f * scale;
    }

    /// 折叠态：一行一条 —— B站 的配色规则不变，只是压成一条。
    private float DrawScCompact(Graphics g, SuperChatItem item, float x, float y, float width, float scale)
    {
        float height = 36f * scale;
        using (GraphicsPath path = Rounded(new RectangleF(x, y, width, height), 6f * scale))
        using (SolidBrush back = new SolidBrush(SurfaceColor(item.BackgroundColor))) g.FillPath(back, path);
        using (Pen border = new Pen(item.BackgroundBottomColor, Math.Max(1f, 1f * scale)))
        using (GraphicsPath path = Rounded(new RectangleF(x, y, width, height), 6f * scale))
            g.DrawPath(border, path);

        float pad = 6f * scale;
        float avatar = height - pad * 2;
        if (_config.ScShowAvatar) DrawAvatar(g, item, x + pad, y + pad, avatar);
        else avatar = 0;
        float textX = x + pad + (avatar > 0 ? avatar + pad : 0);

        using (Font headFont = new Font(_config.FontFamily, 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font bodyFont = new Font(_config.FontFamily, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush nameBrush = new SolidBrush(ReadableTextColor(item.NameColor, item.BackgroundColor)))
        using (SolidBrush bodyBrush = new SolidBrush(ReadableTextColor(item.BackgroundBottomColor, item.BackgroundColor)))
        {
            string head = (item.UserName == null ? "" : item.UserName) + " " + item.Price + "元 ";
            SizeF headSize = g.MeasureString(head, headFont);
            float lineY = y + (height - headSize.Height) / 2f;
            g.DrawString(head, headFont, nameBrush, textX, lineY);
            // 右边要给 × 关闭按钮留位置，否则正文会被压在叉下面
            float reserved = _config.ScClickable ? 22f * scale : 0f;
            float room = width - (textX - x) - headSize.Width - pad * 2 - reserved;
            int maxChars = Math.Max(4, (int)(room / (12f * scale * 0.62f)));
            g.DrawString(Shorten(item.Message, maxChars), bodyFont, bodyBrush, textX + headSize.Width, lineY);
        }
        return height;
    }

    /// 完整卡片的几何量：量一次、画一次都用它，避免「量高度时先画了一遍」把草稿留在画布上。
    private sealed class ScLayout
    {
        public float TopHeight, PadX, PadY, FontSize, Avatar, TextX, TextWidth, LineHeight,
            ContentHeight, ProgressHeight, ProgressGap, ProgressMargin, CardHeight, TotalHeight;
        public string[] Lines;
    }

    /// 完整卡片 —— 结构与配色严格照 B站 全屏 SC 挂件：
    ///   .top-container  背景 background_color，1px 描边 background_bottom_color，
    ///                   内含 30px 圆形头像（+34px 头像框）、昵称（name_color）、"{price}元"
    ///   .content        背景 background_bottom_color，文字 message_font_color，line-height 20px
    ///   .progress-inner 卡片下方独立的一条：白色轨道 + background_bottom_color 的进度，从右往左消退
    private ScLayout LayoutScFull(SuperChatItem item, float x, float width, float scale, Graphics measure)
    {
        ScLayout layout = new ScLayout();
        layout.FontSize = 12f * scale;                   // .sc font-size: 12px
        layout.TopHeight = 50f * scale;                  // .top-container height
        layout.PadX = 10f * scale;
        layout.PadY = 8f * scale;
        layout.Avatar = 30f * scale;
        layout.LineHeight = 20f * scale;                 // .content line-height: 20px
        layout.ProgressHeight = 4f * scale;
        layout.ProgressGap = 4f * scale;                 // .sc 的 margin-bottom
        layout.ProgressMargin = 10f * scale;
        layout.TextX = x + layout.PadX + (_config.ScShowAvatar ? layout.Avatar + 6f * scale : 0f);
        layout.TextWidth = Math.Max(30f, x + width - layout.PadX - layout.TextX);
        layout.Lines = WrapLinesRich(item.Message, layout.TextWidth, layout.FontSize);
        layout.ContentHeight = Math.Max(38f * scale, layout.Lines.Length * layout.LineHeight + layout.PadY * 2);
        layout.CardHeight = layout.TopHeight + layout.ContentHeight;
        layout.TotalHeight = layout.CardHeight + layout.ProgressGap + layout.ProgressHeight + layout.ProgressMargin;
        return layout;
    }

    private float MeasureScFull(SuperChatItem item, float x, float width, float scale, Graphics measure)
    {
        return LayoutScFull(item, x, width, scale, measure).TotalHeight;
    }

    private float DrawScFull(Graphics g, SuperChatItem item, float x, float y, float width, float scale, Graphics measure)
    {
        ScLayout layout = LayoutScFull(item, x, width, scale, measure);
        float fontSize = layout.FontSize;
        float topHeight = layout.TopHeight;
        float padX = layout.PadX, padY = layout.PadY;
        float avatar = layout.Avatar;

        using (Font nameFont = new Font(_config.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font priceFont = new Font(_config.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font bodyFont = new Font(_config.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            string userName = item.UserName == null ? "" : item.UserName;

            // ---- .top-container ----
            RectangleF topRect = new RectangleF(x, y, width, topHeight);
            using (GraphicsPath path = Rounded(topRect, 6f * scale))
            using (SolidBrush back = new SolidBrush(SurfaceColor(item.BackgroundColor))) g.FillPath(back, path);
            using (Pen border = new Pen(item.BackgroundBottomColor, Math.Max(1f, 1f * scale)))
            using (GraphicsPath path = Rounded(topRect, 6f * scale))
                g.DrawPath(border, path);

            if (_config.ScShowAvatar)
                DrawAvatar(g, item, x + padX, y + (topHeight - avatar) / 2f, avatar, 34f * scale);

            using (SolidBrush nameBrush = new SolidBrush(ReadableTextColor(item.NameColor, item.BackgroundColor)))
                g.DrawString(userName, nameFont, nameBrush, layout.TextX, y + padY + 2f * scale);
            using (SolidBrush priceBrush = new SolidBrush(ReadableTextColor(Color.FromArgb(51, 51, 51), item.BackgroundColor)))
                g.DrawString(item.Price + "元", priceFont, priceBrush,
                    layout.TextX, y + padY + 2f * scale + nameFont.GetHeight(g) + 2f * scale);

            // ---- .content（圆角只有下面两个角）----
            RectangleF contentRect = new RectangleF(x, y + topHeight, width, layout.ContentHeight);
            using (GraphicsPath path = new GraphicsPath())
            {
                float d = 6f * scale;
                path.AddArc(contentRect.X, contentRect.Bottom - d * 2, d * 2, d * 2, 90, 90);
                path.AddArc(contentRect.Right - d * 2, contentRect.Bottom - d * 2, d * 2, d * 2, 0, 90);
                path.AddLine(contentRect.Right, contentRect.Bottom - d, contentRect.Right, contentRect.Y);
                path.AddLine(contentRect.Right, contentRect.Y, contentRect.X, contentRect.Y);
                path.AddLine(contentRect.X, contentRect.Y, contentRect.X, contentRect.Bottom - d);
                path.CloseFigure();
                using (SolidBrush back = new SolidBrush(SurfaceColor(item.BackgroundBottomColor))) g.FillPath(back, path);
            }
            // 走**和弹幕同一套渲染**：emoji 贴精灵图、字体里没有的字自动换字体。
            // 原来这里是裸 g.DrawString —— emoji 一律画成豆腐块（用户实测反馈：SC 里出现「口」）。
            for (int i = 0; i < layout.Lines.Length; i++)
            {
                SharedBitmap line = _textCache.Get(layout.Lines[i], item.MessageFontColor, fontSize,
                    _config.FontFamily, false, 0);
                try { TextBlit.Draw(g, line.Bitmap, x + padX, y + topHeight + padY + i * layout.LineHeight); }
                finally { line.Release(); }      // 这一帧用完就还（下一帧再借，缓存命中是常数开销）
            }

            // ---- .progress-inner：卡片下方、白色轨道、从右往左消退 ----
            float progressY = y + layout.CardHeight + layout.ProgressGap;
            using (GraphicsPath path = Rounded(
                new RectangleF(x, progressY, width, layout.ProgressHeight), layout.ProgressHeight / 2f))
            using (SolidBrush track = new SolidBrush(Color.White)) g.FillPath(track, path);
            float remain = (float)Math.Max(0, (item.ExpiresAt - DateTime.UtcNow).TotalSeconds);
            float total = Math.Max(1f, item.DurationSec);
            float ratio = Math.Min(1f, remain / total);
            if (ratio > 0.001f)
            {
                using (GraphicsPath path = Rounded(
                    new RectangleF(x, progressY, width * ratio, layout.ProgressHeight), layout.ProgressHeight / 2f))
                using (SolidBrush bar = new SolidBrush(SurfaceColor(item.BackgroundBottomColor))) g.FillPath(bar, path);
            }
            return layout.TotalHeight;
        }
    }

    private void DrawAvatar(Graphics g, SuperChatItem item, float x, float y, float size)
    {
        DrawAvatar(g, item, x, y, size, 0f);
    }

    /// B站 的头像是**正圆**（30px 盒子 + 17px 圆角），外面还可能套一个 34px 的头像框。
    private void DrawAvatar(Graphics g, SuperChatItem item, float x, float y, float size, float frameSize)
    {
        SharedBitmap avatar = _imageCache.TryGet(item.Face);   // 借出一份，退出前还（try/finally）
        try
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(x, y, size, size);
                if (avatar != null)
                {
                    GraphicsState state = g.Save();
                    g.SetClip(path);
                    g.DrawImage(avatar.Bitmap, x, y, size, size);
                    g.Restore(state);
                }
                else
                {
                    using (SolidBrush placeholder = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                        g.FillPath(placeholder, path);
                    using (Font font = new Font(_config.FontFamily, size * 0.5f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush brush = new SolidBrush(item.BackgroundBottomColor))
                    using (StringFormat format = new StringFormat())
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        string initial = item.UserName != null && item.UserName.Length > 0
                            ? item.UserName.Substring(0, 1) : "?";
                        g.DrawString(initial, font, brush, new RectangleF(x, y, size, size), format);
                    }
                }
            }
            if (frameSize > size && item.FaceFrame != null && item.FaceFrame.Length > 0)
            {
                SharedBitmap frame = _imageCache.TryGet(item.FaceFrame);
                if (frame != null)
                {
                    float offset = (frameSize - size) / 2f;
                    try { g.DrawImage(frame.Bitmap, x - offset, y - offset, frameSize, frameSize); }
                    finally { frame.Release(); }
                }
            }
        }
        finally
        {
            if (avatar != null) avatar.Release();
        }
    }

    /// 文字颜色对比度保护：B站 全屏 SC 挂件在 name_color 与底色对比不足时，
    /// 会在深/浅之间挑一个对比度更高的（utils/color.ts 的 getReadableTextColor）。
    internal static Color ReadableTextColor(Color text, Color background)
    {
        if (ContrastRatio(text, background) >= 4.5) return text;
        Color dark = Color.FromArgb(51, 51, 51);
        Color light = Color.White;
        return ContrastRatio(dark, background) >= ContrastRatio(light, background) ? dark : light;
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb), darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Channel(int value)
    {
        double v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    // ---------------------------------------------------------------- 点击命中
    //
    // 覆盖层默认是「非 SC 区域全部点击穿透」（WM_NCHITTEST 返回 HTTRANSPARENT），
    // 只有这几块矩形会吃掉鼠标事件：卡片本体（点一下切换形态）与卡片右上角的 ×（点一下关掉这条）。
    public sealed class ScHit
    {
        public RectangleF Rect;
        public long DismissId;      // 0 = 点它是切换形态；非 0 = 点它是关掉这条 SC
    }

    private readonly List<ScHit> _scHits = new List<ScHit>();

    public IList<ScHit> ScHits { get { return _scHits; } }

    /// 返回命中的区域；没命中返回 null（调用方据此决定要不要放行给下面的播放器）。
    public ScHit HitTestSc(int x, int y)
    {
        for (int i = _scHits.Count - 1; i >= 0; i--)
            if (_scHits[i].Rect.Contains(x, y)) return _scHits[i];
        return null;
    }

    private void AddScHit(RectangleF rect, long dismissId)
    {
        ScHit hit = new ScHit();
        hit.Rect = rect;
        hit.DismissId = dismissId;
        _scHits.Add(hit);
    }

    internal static GraphicsPath Rounded(RectangleF rect, float radius)    {
        GraphicsPath path = new GraphicsPath();
        float d = radius * 2;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Shorten(string text, int max)
    {
        if (text == null) return "";
        text = text.Replace("\r", " ").Replace("\n", " ");
        if (text.Length <= max) return text;
        return text.Substring(0, Math.Max(1, max - 1)) + "…";
    }

    /// 按宽度折行（对应 CSS 的 word-break: break-word），返回每一行。
    /// 不设行数上限会和 B站 一样把卡片撑很高，所以留一个 8 行的兜底，免得一条超长留言把整个画面盖住。
    private static string[] WrapLines(string text, float width, Font font, Graphics g)
    {
        if (text == null) text = "";
        string flat = text.Replace("\r", "").Replace("\n", " ");
        List<string> lines = new List<string>();
        StringBuilder current = new StringBuilder();
        for (int i = 0; i < flat.Length; i++)
        {
            // ⚠️ 代理对**不能拆** —— 逐码元累加会把 emoji 的高低位代理劈到两行，
            //    劈开的高位代理单独渲染必然是豆腐块。踩过（SC 卡片上看到一个「口」）。
            if (char.IsHighSurrogate(flat[i]) && i + 1 < flat.Length && char.IsLowSurrogate(flat[i + 1]))
            {
                current.Append(flat[i]).Append(flat[i + 1]);
                i++;
            }
            else current.Append(flat[i]);
            if (g.MeasureString(current.ToString(), font).Width >= width)
            {
                lines.Add(current.ToString());
                current.Length = 0;
                if (lines.Count >= 8) return lines.ToArray();
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add("");
        return lines.ToArray();
    }

    /// 走文字缓存量一行有多宽（借出 → 量 → 立刻还，不留租约）。
    private float MeasureWidth(string text, float fontSize)
    {
        SharedBitmap probe = _textCache.Get(text, Color.Black, fontSize, _config.FontFamily, false, 0);
        try { return probe.Bitmap.Width; }
        finally { probe.Release(); }
    }

    /// SC 正文折行：**宽度必须用和绘制同一套渲染去量**。
    /// 直接 DrawString 的话 emoji 会画成豆腐块；而且 GDI+ 不回退字体、也不画彩色 emoji
    /// （详见 danmaku-7-emoji.cs 顶部），所以量宽和画都必须走 _textCache。
    private string[] WrapLinesRich(string text, float width, float fontSize)
    {
        if (text == null) text = "";
        string flat = text.Replace("\r", "").Replace("\n", " ");
        List<string> lines = new List<string>();
        StringBuilder current = new StringBuilder();
        for (int i = 0; i < flat.Length; i++)
        {
            if (char.IsHighSurrogate(flat[i]) && i + 1 < flat.Length && char.IsLowSurrogate(flat[i + 1]))
            {
                current.Append(flat[i]).Append(flat[i + 1]);
                i++;
            }
            else current.Append(flat[i]);
            if (MeasureWidth(current.ToString(), fontSize) >= width)
            {
                lines.Add(current.ToString());
                current.Length = 0;
                if (lines.Count >= 8) return lines.ToArray();
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add("");
        return lines.ToArray();
    }
}
