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
// 字体覆盖检查 + 彩色 emoji 精灵图
//
// 为什么需要这两个东西（都是实测出来的）：
//  ① **GDI+ 不做字体回退**。`Graphics.DrawString` / `GraphicsPath.AddString` 只认你给的那个字体，
//     字体里没有的字符（emoji、生僻字、部分符号）直接画成豆腐块 —— 这就是「弹幕变成方块」的原因。
//  ② **GDI 和 GDI+ 都画不出彩色 emoji**。实测四条路径（DrawString / AddString+FillPath /
//     TextRenderer(GDI) / 自己逐字回退），彩色像素**全都是 0**。本机 Segoe UI Emoji 是 COLRv1
//     （paint graph，7.4MB，没有 CBDT/sbix/SVG 位图表），自己解析不划算。
//     → 改成构建期用 Chrome 渲染一张精灵图（`emoji-sheet.png`），运行时查表贴图，**零运行时依赖**。
// --------------------------------------------------------------------------------------------

/// 判断某个字体到底有没有某个字：用 GDI 的 GetGlyphIndicesW（返回 0xFFFF = 缺字形）。
/// 比 FontFamily 的属性靠谱得多，也不受 GDI+ 不回退的影响。
internal static class FontCoverage
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, ushort[] indices, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const uint GgiMarkNonExistingGlyphs = 0x0001;

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, IntPtr> Fonts = new Dictionary<string, IntPtr>();
    private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>();
    private static Graphics _graphics;

    /// 回退链：主字体没有就依次往下找。Segoe UI Emoji 排在前面，保证 emoji 至少形状是对的。
    public static readonly string[] Fallbacks = new string[]
    { "Segoe UI Emoji", "Segoe UI Symbol", "SimSun", "Malgun Gothic", "Segoe UI" };

    public static string PickFamily(char ch, string baseFamily)
    {
        return PickFamily(ch.ToString(), baseFamily);
    }

    /// 按**码点**（不是 UTF-16 码元）逐个判断：代理对必须整个拿去过 GetGlyphIndicesW，
    /// 只传高代理必然判成「缺字形」，然后整段退化成豆腐块（踩过）。
    public static string PickFamily(string text, string baseFamily)
    {
        if (text == null || text.Length == 0) return baseFamily;
        int i = 0;
        while (i < text.Length)
        {
            int length = (char.IsSurrogatePair(text, i)) ? 2 : 1;
            string piece = text.Substring(i, length);
            if (!Covers(baseFamily, piece))
            {
                for (int k = 0; k < Fallbacks.Length; k++)
                    if (Covers(Fallbacks[k], piece)) return Fallbacks[k];
                // 整条链都没有 → 保持主字体（至少位置对，用户也能看出是缺字形）
            }
            i += length;
        }
        return baseFamily;
    }

    public static bool Covers(string family, char ch)
    {
        return Covers(family, ch.ToString());
    }

    public static bool Covers(string family, string piece)
    {
        if (family == null || family.Length == 0) return false;
        string key = family + "\u0001" + piece;
        lock (Gate)
        {
            bool cached;
            if (Cache.TryGetValue(key, out cached)) return cached;
        }
        bool result = Probe(family, piece);
        lock (Gate) { Cache[key] = result; }
        return result;
    }

    private static bool Probe(string family, string piece)
    {
        IntPtr hdc = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            lock (Gate)
            {
                if (_graphics == null) _graphics = Graphics.FromImage(new Bitmap(1, 1));
                if (!Fonts.TryGetValue(family, out font))
                {
                    using (Font created = new Font(family, 20f, FontStyle.Regular, GraphicsUnit.Pixel))
                        font = created.ToHfont();
                    Fonts[family] = font;
                }
                hdc = _graphics.GetHdc();
            }
            old = SelectObject(hdc, font);
            ushort[] indices = new ushort[piece.Length];
            uint result = GetGlyphIndicesW(hdc, piece, piece.Length, indices, GgiMarkNonExistingGlyphs);
            if (result == 0xFFFFFFFF) return false;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] == 0xFFFF) return false;
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            try { if (old != IntPtr.Zero) SelectObject(hdc, old); } catch (Exception) { }
            try { if (hdc != IntPtr.Zero) _graphics.ReleaseHdc(hdc); } catch (Exception) { }
            // font 故意不删：一个字体一个 HFONT，全程复用（数量就几个）
        }
    }
}

/// 彩色 emoji 精灵图。
///
/// 格子尺寸与区段表**从清单文件 `emoji-sheet.txt` 读**（生成器写的那份），
/// 所以不会出现「图重新生成了、代码里的区段没跟着改」导致的错位。
/// 清单不存在时用下面这份内置默认值（和生成器的默认参数一致）。
///
/// 两段结构：先「普通格」，再「变体格」（码点 + U+FE0F）。
/// 变体段是必要的：❤(U+2764) 默认是文字型、画出来是黑白轮廓，❤️(U+2764 U+FE0F) 才是彩色。
internal sealed class EmojiSheet
{
    private static int _cell = 40;
    private static int _columns = 48;
    private static int _plainCount = 3114;
    private static int _flagStart = 3828;
    private static int _flagCount = 676;
    private static int _flagBase = 0x1F1E6;
    private static int _seqStart = 3828;
    private static int _seqCount = 0;
    /// 键 = 序列（**已去掉 U+FE0F**），值 = 相对 _seqStart 的偏移。
    /// 去掉 FE0F 是为了让「带变体选择符」和「不带」的写法都能命中同一格。
    private static readonly Dictionary<string, int> Sequences = new Dictionary<string, int>();
    private static int[] _plainFrom = DefaultPlainFrom;
    private static int[] _plainTo = DefaultPlainTo;
    private static int[] _vsFrom = DefaultVsFrom;
    private static int[] _vsTo = DefaultVsTo;

    private static readonly int[] DefaultPlainFrom = new int[]
    { 0x2600, 0x2B00, 0x2300, 0x1F000, 0x1F100, 0x1F300, 0x1F600, 0x1F680, 0x1F900, 0x1FA70,
      0x00A9, 0x00AE, 0x203C, 0x2049, 0x2122, 0x2139, 0x3030, 0x303D, 0x3297, 0x3299 };
    private static readonly int[] DefaultPlainTo = new int[]
    { 0x27BF, 0x2BFF, 0x23FF, 0x1F0FF, 0x1F2FF, 0x1F5FF, 0x1F64F, 0x1F6FF, 0x1F9FF, 0x1FAFF,
      0x00A9, 0x00AE, 0x203C, 0x2049, 0x2122, 0x2139, 0x3030, 0x303D, 0x3297, 0x3299 };
    private static readonly int[] DefaultVsFrom = new int[]
    { 0x2600, 0x2B00, 0x00A9, 0x00AE, 0x203C, 0x2049, 0x2122, 0x2139, 0x3030, 0x303D, 0x3297, 0x3299 };
    private static readonly int[] DefaultVsTo = new int[]
    { 0x27BF, 0x2BFF, 0x00A9, 0x00AE, 0x203C, 0x2049, 0x2122, 0x2139, 0x3030, 0x303D, 0x3297, 0x3299 };

    private static Bitmap _sheet;
    private static bool _tried;
    private static string _path;

    /// 指到 emoji-sheet.png；同目录下的 emoji-sheet.txt 会被自动读作清单。
    ///
    /// ⚠️ 路径**变了就要把「试过了」的闩解开**（2026-09-27 修）：
    ///    `EnsureLoaded` 是一次性闩（`if (_tried) return;`），而 `Available` / `CellSize` / `Sheet`
    ///    任何一处被读到都会先把闩扣上。于是「还没 SetPath 就被问过一次」或者「第一次指到一张
    ///    不存在的图」之后，**再怎么 SetPath 都不会加载了** —— 现象是彩色 emoji 整段失效且毫无提示。
    ///    实测就是这么被抓到的：新加的位图生命周期用例先建了个渲染器（顺手读了一次 CellSize），
    ///    后面「精灵图能加载」那条用例就永远红了。同一路径重复设置仍然只加载一次（性能不变）。
    public static void SetPath(string path)
    {
        if (!string.Equals(path, _path, StringComparison.Ordinal))
        {
            _tried = false;
            _sheet = null;
        }
        _path = path;
    }

    public static bool Available
    {
        get { EnsureLoaded(); return _sheet != null; }
    }

    public static Bitmap Sheet { get { EnsureLoaded(); return _sheet; } }

    public static int CellSize { get { EnsureLoaded(); return _cell; } }
    public static int Columns { get { EnsureLoaded(); return _columns; } }

    private static void EnsureLoaded()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (_path == null || !File.Exists(_path)) return;
            LoadManifest(Path.ChangeExtension(_path, ".txt"));
            // 直接按文件读，别锁住文件（用户可能想换一张）
            byte[] bytes = File.ReadAllBytes(_path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
                _sheet = new Bitmap(image);
        }
        catch (Exception) { _sheet = null; }
    }

    private static void LoadManifest(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key == "CELL") _cell = int.Parse(value);
                else if (key == "COLUMNS") _columns = int.Parse(value);
                else if (key == "PLAINCOUNT") _plainCount = int.Parse(value);
                else if (key == "FLAGSTART") _flagStart = int.Parse(value);
                else if (key == "FLAGCOUNT") _flagCount = int.Parse(value);
                else if (key == "FLAGBASE") _flagBase = Convert.ToInt32(value, 16);
                else if (key == "SEQSTART") _seqStart = int.Parse(value);
                else if (key == "SEQCOUNT") _seqCount = int.Parse(value);
                else if (key == "SEQ") ParseSequences(value);
                else if (key == "PLAIN") { int[] f, t; ParseRanges(value, out f, out t); if (f != null) { _plainFrom = f; _plainTo = t; } }
                else if (key == "VS") { int[] f, t; ParseRanges(value, out f, out t); if (f != null) { _vsFrom = f; _vsTo = t; } }
            }
        }
        catch (Exception) { /* 清单坏了就用内置默认值，不影响出图 */ }
    }

    private static void ParseRanges(string text, out int[] from, out int[] to)
    {
        from = null;            // out 参数必须在每个 return 之前有确定赋值
        to = null;
        List<int> starts = new List<int>();
        List<int> ends = new List<int>();
        foreach (string piece in text.Split(','))
        {
            string item = piece.Trim();
            if (item.Length == 0) continue;
            int dash = item.IndexOf('-');
            if (dash < 0) { int single = Convert.ToInt32(item, 16); starts.Add(single); ends.Add(single); }
            else
            {
                starts.Add(Convert.ToInt32(item.Substring(0, dash), 16));
                ends.Add(Convert.ToInt32(item.Substring(dash + 1), 16));
            }
        }
        if (starts.Count == 0) return;      // 解析不出来 → 返回 null，调用方保留原值
        from = starts.ToArray();
        to = ends.ToArray();
    }

    /// 码点 → 精灵图里的格子。`variant` = 原文里这个码点后面跟了 U+FE0F。
    public static bool TryGetCell(int codePoint, bool variant, out Rectangle source)
    {
        source = new Rectangle();
        EnsureLoaded();
        if (_sheet == null) return false;
        int index = -1;
        if (variant) index = IndexOf(_vsFrom, _vsTo, codePoint);
        if (index < 0) index = IndexOf(_plainFrom, _plainTo, codePoint);
        else index += _plainCount;          // 变体段排在普通段后面
        if (index < 0) return false;
        return CellAt(index, out source);
    }

    /// 这个码点在精灵图里有没有「变体格」——决定原文的 FE0F 该不该被吃掉。
    public static bool HasVariant(int codePoint)
    {
        EnsureLoaded();
        return IndexOf(_vsFrom, _vsTo, codePoint) >= 0;
    }

    /// 国旗：两个区域指示符**成对**才是旗子，单渲染一个是「带框字母」。
    /// 精灵图里 26x26 种组合都渲染了，这里按 (a,b) 直接算格子。
    public static bool IsRegionalIndicator(int codePoint)
    {
        return codePoint >= _flagBase && codePoint <= _flagBase + 25;
    }

    public static bool TryGetFlagCell(int first, int second, out Rectangle source)
    {
        source = new Rectangle();
        EnsureLoaded();
        if (_sheet == null || _flagCount <= 0) return false;   // 精灵图没做国旗段（Windows 画不出旗子，默认不做）
        if (!IsRegionalIndicator(first) || !IsRegionalIndicator(second)) return false;
        int index = _flagStart + (first - _flagBase) * 26 + (second - _flagBase);
        return CellAt(index, out source);
    }

    /// 序列段里的第 offset 格（ZWJ 组合 / 键帽）。
    public static bool TryGetSequenceCell(int offset, out Rectangle source)
    {
        source = new Rectangle();
        EnsureLoaded();
        if (_sheet == null || offset < 0 || offset >= _seqCount) return false;
        return CellAt(_seqStart + offset, out source);
    }

    /// 肤色修饰符（U+1F3FB..U+1F3FF）。精灵图里它们是**色块**，单独渲染会在弹幕里留下一个色块，
    /// 所以 `👍🏽` 这种我们丢掉修饰符、按 `👍` 渲染（精灵图没做 肤色×基字符 的组合格）。
    public static bool IsSkinTone(int codePoint)
    {
        return codePoint >= 0x1F3FB && codePoint <= 0x1F3FF;
    }

    /// 序列段：`👨‍👩‍👧` 这种是三个码点用 ZWJ（U+200D）连起来的，拆开渲染就成三个独立的人；
    /// 键帽 `1️⃣` 是 `0031 FE0F 20E3`，拆开也散架。所以整串查表。
    ///
    /// **键的构造必须和 MatchCluster 完全对称**：两边都丢掉连接符 U+200D 和变体选择符 U+FE0F，
    /// 只留「实体」码点（键帽的 U+20E3 要留）。之前这里漏丢了 U+200D，
    /// 于是清单键里有 ZWJ、运行时键里没有，永远匹配不上 —— 表现就是 `🏳️‍🌈` 只画出白旗。
    private static void ParseSequences(string text)
    {
        Sequences.Clear();
        string[] entries = text.Split(',');
        for (int index = 0; index < entries.Length; index++)
        {
            string entry = entries[index].Trim();
            if (entry.Length == 0) continue;
            StringBuilder key = new StringBuilder();
            foreach (string part in entry.Split('-'))
            {
                int codePoint = Convert.ToInt32(part.Trim(), 16);
                if (codePoint == 0xFE0F || codePoint == 0x200D) continue;   // 连接符/变体符不参与匹配
                key.Append(char.ConvertFromUtf32(codePoint));
            }
            if (key.Length > 0) Sequences[key.ToString()] = index;
        }
    }

    /// 从 start 开始收一个「簇」（ZWJ 串联 / 键帽 / 后面跟变体选择符或肤色），
    /// 然后**从长到短**试前缀能不能对上序列表。对上了返回该序列在序列段里的偏移，
    /// `end` 给出应该跳到哪（连带吃掉尾部的 FE0F）。
    /// 对不上返回 -1，调用方按普通的逐码点逻辑走。
    ///
    /// 收簇时 U+200D（连接符）和 U+FE0F（变体符）都**只当分隔**、不进键 —— 与 ParseSequences 对称。
    public static int MatchCluster(string text, int start, out int end)
    {
        end = start;
        EnsureLoaded();
        if (_sheet == null || _seqCount <= 0 || Sequences.Count == 0) return -1;
        if (start >= text.Length) return -1;

        List<int> codePoints = new List<int>();
        List<int> ends = new List<int>();
        int i = start;
        bool afterZwj = false;
        while (i < text.Length)
        {
            bool pair = char.IsSurrogatePair(text, i);
            int codePoint = pair ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            int unit = pair ? 2 : 1;
            if (codePoint == 0x200D) { i += unit; afterZwj = true; continue; }   // ZWJ：只当连接符
            if (codePoint == 0xFE0F) { i += unit; continue; }                   // 变体选择符
            if (codePoint >= 0x1F3FB && codePoint <= 0x1F3FF) { i += unit; continue; }   // 肤色修饰符
            if (codePoint == 0x20E3)                                            // 键帽：直接续在后面
            {
                codePoints.Add(codePoint); i += unit; ends.Add(i); afterZwj = false; continue;
            }
            // 普通码点：只有紧跟在一个 ZWJ 后面才算同一个簇，否则簇到此为止
            if (codePoints.Count > 0 && !afterZwj) break;
            codePoints.Add(codePoint);
            i += unit;
            ends.Add(i);
            afterZwj = false;
        }

        for (int take = codePoints.Count; take >= 2; take--)
        {
            StringBuilder key = new StringBuilder();
            for (int k = 0; k < take; k++) key.Append(char.ConvertFromUtf32(codePoints[k]));
            int offset;
            if (!Sequences.TryGetValue(key.ToString(), out offset)) continue;
            int stop = ends[take - 1];
            while (stop < text.Length && text[stop] == '\uFE0F') stop++;
            end = stop;
            return offset;
        }
        return -1;
    }

    private static bool CellAt(int index, out Rectangle source)
    {
        source = new Rectangle();
        if (index < 0) return false;
        int column = index % _columns;
        int row = index / _columns;
        int x = column * _cell;
        int y = row * _cell;
        if (x + _cell > _sheet.Width || y + _cell > _sheet.Height) return false;
        source = new Rectangle(x, y, _cell, _cell);
        return true;
    }

    private static int IndexOf(int[] from, int[] to, int codePoint)
    {
        int offset = 0;
        for (int i = 0; i < from.Length && i < to.Length; i++)
        {
            if (codePoint >= from[i] && codePoint <= to[i]) return offset + (codePoint - from[i]);
            offset += to[i] - from[i] + 1;
        }
        return -1;
    }
}
