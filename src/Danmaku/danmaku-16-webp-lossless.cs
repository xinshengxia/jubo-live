// ---------------------------------------------------------------------------
// danmaku-16-webp-lossless.cs
//
// 纯托管 WebP 无损（VP8L）解码器：无第三方依赖、无 P/Invoke、无 unsafe。
// 用于在未安装 Microsoft.WebpImageExtension（WIC WebP 编解码器）的 Windows 上
// 解码抖音表情等 VP8L 无损 WebP 图片，供 danmaku-15-ui.cs 的 WebpImage 兜底。
//
// 参考：WebP Lossless Bitstream Specification
// Portions adapted from libwebp: Copyright (c) 2010, Google Inc. All rights reserved.
// BSD terms / patent grant: licenses/libwebp-COPYING.txt and libwebp-PATENTS.txt.
// 来源范围见 THIRD_PARTY_NOTICES.md；不是原生 libwebp DLL 的绑定。
//   https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification
// 行为对齐 libwebp（src/dec/vp8l_dec.c、src/dsp/lossless.c、
// src/utils/huffman_utils.c、src/utils/color_cache_utils.h）。
//
// 语言级别：C# 5（%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe）。
// 线程安全：每次调用使用独立的 Decoder 实例，无共享可变静态字段。
//
// 验证：使用离线语料，并与独立解码器进行逐像素交叉对照。
//
// 与 libwebp 的两处有意差异（都不影响合法文件）：
//   1. 位流读到 payload 末尾之后即判失败（libwebp 会按 0 补齐再读若干位）。
//      因此截断/损坏的文件更倾向于返回 null，而不是解出一张垃圾图。
//   2. 单边尺寸 > 16384 或总像素 > 16M（4096x4096）直接返回 null，
//      避免坏数据声明巨大尺寸造成超大分配。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal static class WebpLossless
{
    // 单边上限 = VP8L 的 14 位宽高上限；总像素上限用于挡住「坏数据声明
    // 巨大尺寸」造成的超大分配（4096 x 4096）。
    private const int MaxDimension = 16384;
    private const long MaxPixels = 16L * 1024 * 1024;

    /// <summary>data 是完整 WebP 容器字节；容器内存在 VP8L 块时为 true。</summary>
    public static bool LooksLikeLossless(byte[] data)
    {
        int offset;
        int length;
        return FindVp8lChunk(data, out offset, out length);
    }

    /// <summary>
    /// 解码 VP8L 无损 WebP。成功返回新的 Format32bppArgb 位图（调用方负责
    /// Dispose）；不是 VP8L / 截断 / 损坏 / 不支持的特性一律返回 null，不抛异常。
    /// </summary>
    public static Bitmap Decode(byte[] data)
    {
        try
        {
            int offset;
            int length;
            if (!FindVp8lChunk(data, out offset, out length)) return null;
            if (length < 5) return null;
            Decoder decoder = new Decoder(data, offset, length);
            return decoder.Run();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- RIFF 容器 ---------------------------------------------------------

    private static bool FindVp8lChunk(byte[] data, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (data == null || data.Length < 20) return false;
        if (data[0] != (byte)'R' || data[1] != (byte)'I' ||
            data[2] != (byte)'F' || data[3] != (byte)'F') return false;
        if (data[8] != (byte)'W' || data[9] != (byte)'E' ||
            data[10] != (byte)'B' || data[11] != (byte)'P') return false;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            long size = (uint)(data[pos + 4] | (data[pos + 5] << 8) |
                               (data[pos + 6] << 16) | (data[pos + 7] << 24));
            if (data[pos] == (byte)'V' && data[pos + 1] == (byte)'P' &&
                data[pos + 2] == (byte)'8' && data[pos + 3] == (byte)'L')
            {
                long avail = data.Length - (pos + 8);
                offset = pos + 8;
                length = (int)(size < avail ? size : avail);
                return length > 0;
            }
            long advance = size + (size & 1) + 8; // 奇数长度块有 1 字节 padding
            if (advance <= 0 || advance > data.Length - pos) return false;
            pos += (int)advance;
        }
        return false;
    }

    // --- 逐通道算术工具（全部对齐 libwebp lossless.c）----------------------

    private static int SubSampleSize(int size, int bits)
    {
        return (size + (1 << bits) - 1) >> bits;
    }

    /// <summary>逐通道模 256 相加（VP8LAddPixels）。</summary>
    private static int AddPixels(int a, int b)
    {
        unchecked
        {
            int ag = (a & (int)0xff00ff00u) + (b & (int)0xff00ff00u);
            int rb = (a & 0x00ff00ff) + (b & 0x00ff00ff);
            return (ag & (int)0xff00ff00u) | (rb & 0x00ff00ff);
        }
    }

    /// <summary>逐通道向下取整平均（Average2）：(((a^b)&0xfefefefe)>>>1) + (a&amp;b)。</summary>
    private static int Average2(int a, int b)
    {
        unchecked
        {
            uint t = (uint)((a ^ b) & (int)0xfefefefeu);
            return (int)((t >> 1) + (uint)(a & b));
        }
    }

    private static int Average3(int a0, int a1, int a2)
    {
        return Average2(Average2(a0, a2), a1);
    }

    private static int Average4(int a0, int a1, int a2, int a3)
    {
        return Average2(Average2(a0, a1), Average2(a2, a3));
    }

    private static int Clip255(int a)
    {
        if (a >= 0 && a < 256) return a;
        return a < 0 ? 0 : 255;
    }

    private static int AddSubtractComponentFull(int a, int b, int c)
    {
        return Clip255(a + b - c);
    }

    private static int ClampedAddSubtractFull(int c0, int c1, int c2)
    {
        unchecked
        {
            int a = AddSubtractComponentFull((int)((uint)c0 >> 24), (int)((uint)c1 >> 24), (int)((uint)c2 >> 24));
            int r = AddSubtractComponentFull((c0 >> 16) & 0xff, (c1 >> 16) & 0xff, (c2 >> 16) & 0xff);
            int g = AddSubtractComponentFull((c0 >> 8) & 0xff, (c1 >> 8) & 0xff, (c2 >> 8) & 0xff);
            int b = AddSubtractComponentFull(c0 & 0xff, c1 & 0xff, c2 & 0xff);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }
    }

    private static int AddSubtractComponentHalf(int a, int b)
    {
        // C 的整数除法向零截断，C# 一致
        return Clip255(a + (a - b) / 2);
    }

    private static int ClampedAddSubtractHalf(int c0, int c1, int c2)
    {
        int ave = Average2(c0, c1);
        unchecked
        {
            int a = AddSubtractComponentHalf((int)((uint)ave >> 24), (int)((uint)c2 >> 24));
            int r = AddSubtractComponentHalf((ave >> 16) & 0xff, (c2 >> 16) & 0xff);
            int g = AddSubtractComponentHalf((ave >> 8) & 0xff, (c2 >> 8) & 0xff);
            int b = AddSubtractComponentHalf(ave & 0xff, c2 & 0xff);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }
    }

    private static int Sub3(int a, int b, int c)
    {
        int pb = b - c;
        int pa = a - c;
        return (pb < 0 ? -pb : pb) - (pa < 0 ? -pa : pa);
    }

    /// <summary>libwebp 的 Select(a=T, b=L, c=TL)。</summary>
    private static int Select(int a, int b, int c)
    {
        unchecked
        {
            int d = Sub3((int)((uint)a >> 24), (int)((uint)b >> 24), (int)((uint)c >> 24)) +
                    Sub3((a >> 16) & 0xff, (b >> 16) & 0xff, (c >> 16) & 0xff) +
                    Sub3((a >> 8) & 0xff, (b >> 8) & 0xff, (c >> 8) & 0xff) +
                    Sub3(a & 0xff, b & 0xff, c & 0xff);
            return (d <= 0) ? a : b;
        }
    }

    // --- 位读取器（LSB first）----------------------------------------------

    private sealed class BitReader
    {
        private readonly byte[] data;
        private readonly int end;
        private readonly long totalBits;
        private int bytePos;
        private long acc;
        private int accBits;
        private long bitPos;
        private bool error;

        public BitReader(byte[] data, int offset, int length)
        {
            this.data = data;
            this.bytePos = offset;
            this.end = offset + length;
            this.totalBits = (long)length * 8;
        }

        public bool Error { get { return error; } }
        public long BitPos { get { return bitPos; } }
        public long TotalBits { get { return totalBits; } }

        /// <summary>查看接下来的 n 位（不消耗）；越界补 0。</summary>
        public int PeekBits(int n)
        {
            while (accBits < n)
            {
                int b = 0;
                if (bytePos < end) b = data[bytePos];
                bytePos++;
                acc |= ((long)b) << accBits;
                accBits += 8;
            }
            return (int)(acc & ((1L << n) - 1));
        }

        public void Consume(int n)
        {
            if (n < 0 || bitPos + n > totalBits) error = true;
            bitPos += n;
            if (n >= 64) { acc = 0; accBits = 0; return; }
            acc >>= n;
            accBits -= n;
            if (accBits < 0) accBits = 0;
        }

        public int ReadBits(int n)
        {
            if (n <= 0) return 0;
            int v = PeekBits(n);
            Consume(n);
            return v;
        }
    }

    // --- 前缀码 ------------------------------------------------------------

    private sealed class HuffTree
    {
        public int SingleSymbol = -1; // >=0：单叶节点，读 0 位
        public int[] Fast;            // 快表项 = symbol | (len << 16)，-1 表示走慢路径
        public int FastBits;
        public int[] Counts;          // [16]
        public int[] Symbols;         // 按（码长, 符号序）排序
    }

    private static int ReverseBits(int value, int bits)
    {
        int r = 0;
        int i;
        for (i = 0; i < bits; i++)
        {
            r = (r << 1) | (value & 1);
            value >>= 1;
        }
        return r;
    }

    // --- 变换 --------------------------------------------------------------

    private sealed class Transform
    {
        public const int Predictor = 0;
        public const int CrossColor = 1;
        public const int SubtractGreen = 2;
        public const int ColorIndexing = 3;

        public int Type;
        public int XSize;   // 读取该变换时的图像宽度
        public int YSize;
        public int Bits;
        public int[] Data;  // 预测模式图 / 颜色变换图 / 展开后的调色板
    }

    private sealed class Plane
    {
        public int[] Pixels;
        public int Width;
        public int Height;
    }

    // --- 解码器 ------------------------------------------------------------

    private sealed class Decoder
    {
        private static readonly int[] CodeLengthCodeOrder = new int[]
        {
            17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        };
        private static readonly int[] CodeLengthExtraBits = new int[] { 2, 3, 7 };
        private static readonly int[] CodeLengthRepeatOffsets = new int[] { 3, 3, 11 };

        // libwebp kCodeToPlane[120]：(yoffset << 4) | (8 - xoffset)
        private static readonly int[] CodeToPlane = new int[]
        {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a,
            0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
            0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45,
            0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
            0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
            0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e,
            0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
            0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d,
            0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70
        };

        private readonly BitReader br;
        private readonly int[] codeLengths = new int[2328]; // 256 + 24 + 2048
        private readonly int[] codeLengthScratch = new int[19];
        private int transformsSeen;
        private long tableBudget = 4L * 1024 * 1024;   // 快表 int 数预算（约 16 MB）
        private long symbolBudget = 8L * 1024 * 1024;  // 符号表 int 数预算

        public Decoder(byte[] data, int offset, int length)
        {
            br = new BitReader(data, offset, length);
        }

        // --- 顶层 ----------------------------------------------------------

        public Bitmap Run()
        {
            if (br.ReadBits(8) != 0x2f) return null;      // VP8L 签名
            int width = br.ReadBits(14) + 1;
            int height = br.ReadBits(14) + 1;
            br.ReadBits(1);                                // alpha_is_used：仅提示
            if (br.ReadBits(3) != 0) return null;          // version 必须为 0
            if (br.Error) return null;
            if (width > MaxDimension || height > MaxDimension) return null;
            if ((long)width * height > MaxPixels) return null;

            Plane plane = DecodeImageStream(width, height, true);
            if (plane == null) return null;
            if (br.Error) return null;
            return ToBitmap(plane);
        }

        private static Bitmap ToBitmap(Plane plane)
        {
            int w = plane.Width;
            int h = plane.Height;
            if (w <= 0 || h <= 0) return null;
            if ((long)w * h > plane.Pixels.Length) return null;

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                             ImageLockMode.WriteOnly,
                                             PixelFormat.Format32bppArgb);
                try
                {
                    if (bd.Stride == w * 4)
                    {
                        Marshal.Copy(plane.Pixels, 0, bd.Scan0, w * h);
                    }
                    else
                    {
                        int y;
                        for (y = 0; y < h; y++)
                        {
                            IntPtr dst = IntPtr.Add(bd.Scan0, y * bd.Stride);
                            Marshal.Copy(plane.Pixels, y * w, dst, w);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                return bmp;
            }
            catch (Exception)
            {
                bmp.Dispose();
                return null;
            }
        }

        // --- 图像流 --------------------------------------------------------

        private Plane DecodeImageStream(int xsize, int ysize, bool isLevel0)
        {
            int curWidth = xsize;
            List<Transform> transforms = new List<Transform>();

            if (isLevel0)
            {
                while (br.ReadBits(1) != 0)
                {
                    if (br.Error) return null;
                    Transform t = ReadTransform(ref curWidth, ysize);
                    if (t == null) return null;
                    transforms.Add(t);
                    if (transforms.Count > 4) return null;
                }
            }
            if (br.Error) return null;

            int colorCacheBits = 0;
            if (br.ReadBits(1) != 0)
            {
                colorCacheBits = br.ReadBits(4);
                if (colorCacheBits < 1 || colorCacheBits > 11) return null;
            }
            if (br.Error) return null;

            HuffTree[][] groups;
            int groupBits;
            int[] huffImage;
            if (!ReadHuffmanCodes(curWidth, ysize, colorCacheBits, isLevel0,
                                  out groups, out groupBits, out huffImage)) return null;

            int[] cache = colorCacheBits > 0 ? new int[1 << colorCacheBits] : null;

            long total = (long)curWidth * ysize;
            if (total <= 0 || total > MaxPixels) return null;
            int[] pixels = new int[(int)total];
            if (!DecodeImageData(pixels, curWidth, ysize, groups, groupBits,
                                 huffImage, cache, colorCacheBits)) return null;

            // 逆变换：按读取顺序的逆序应用
            int i;
            for (i = transforms.Count - 1; i >= 0; i--)
            {
                Transform t = transforms[i];
                if (t.Type == Transform.ColorIndexing)
                {
                    int subWidth = SubSampleSize(t.XSize, t.Bits);
                    int[] expanded = ColorIndexInverse(t, pixels, subWidth, t.XSize, t.YSize);
                    if (expanded == null) return null;
                    pixels = expanded;
                }
                else if (t.Type == Transform.Predictor)
                {
                    if (!PredictorInverse(t, pixels, t.XSize, t.YSize)) return null;
                }
                else if (t.Type == Transform.CrossColor)
                {
                    if (!CrossColorInverse(t, pixels, t.XSize, t.YSize)) return null;
                }
                else if (t.Type == Transform.SubtractGreen)
                {
                    SubtractGreenInverse(pixels, t.XSize * t.YSize);
                }
            }

            Plane plane = new Plane();
            plane.Pixels = pixels;
            plane.Width = transforms.Count > 0 ? transforms[0].XSize : xsize;
            plane.Height = ysize;
            return plane;
        }

        private Transform ReadTransform(ref int xsize, int ysize)
        {
            int type = br.ReadBits(2);
            if (br.Error) return null;
            if ((transformsSeen & (1 << type)) != 0) return null; // 每种变换只能用一次
            transformsSeen |= 1 << type;

            Transform t = new Transform();
            t.Type = type;
            t.XSize = xsize;
            t.YSize = ysize;

            if (type == Transform.Predictor || type == Transform.CrossColor)
            {
                t.Bits = 2 + br.ReadBits(3);
                if (br.Error) return null;
                int sx = SubSampleSize(t.XSize, t.Bits);
                int sy = SubSampleSize(t.YSize, t.Bits);
                Plane sub = DecodeImageStream(sx, sy, false);
                if (sub == null) return null;
                if (sub.Width != sx || sub.Height != sy) return null;
                t.Data = sub.Pixels;
            }
            else if (type == Transform.ColorIndexing)
            {
                int numColors = br.ReadBits(8) + 1;
                if (br.Error) return null;
                int bits = (numColors > 16) ? 0 : (numColors > 4) ? 1 : (numColors > 2) ? 2 : 3;
                xsize = SubSampleSize(t.XSize, bits);
                t.Bits = bits;
                Plane sub = DecodeImageStream(numColors, 1, false);
                if (sub == null) return null;
                if (sub.Width != numColors || sub.Height != 1) return null;
                t.Data = ExpandColorMap(sub.Pixels, numColors, bits);
                if (t.Data == null) return null;
            }
            return t;
        }

        private static int[] ExpandColorMap(int[] src, int numColors, int bits)
        {
            int finalColors = 1 << (8 >> bits);
            if (src.Length < numColors || numColors < 1 || numColors > finalColors) return null;
            int[] map = new int[finalColors];
            map[0] = src[0];
            int i;
            for (i = 1; i < numColors; i++)
            {
                map[i] = AddPixels(map[i - 1], src[i]); // 调色板为差分编码
            }
            // 其余项保持 0x00000000（libwebp ExpandColorMap 的黑尾）
            return map;
        }

        // --- 前缀码读取 ----------------------------------------------------

        private HuffTree BuildTree(int[] lengths, int count)
        {
            int[] counts = new int[16];
            int i;
            int nonZero = 0;
            int lastSymbol = -1;
            for (i = 0; i < count; i++)
            {
                int len = lengths[i];
                if (len < 0 || len > 15) return null;
                if (len != 0)
                {
                    counts[len]++;
                    nonZero++;
                    lastSymbol = i;
                }
            }
            if (nonZero == 0) return null; // 全 0 码长：libwebp 视为错误

            HuffTree tree = new HuffTree();
            if (nonZero == 1)
            {
                tree.SingleSymbol = lastSymbol; // 单叶节点：读 0 位
                return tree;
            }

            long kraft = 0;
            for (i = 1; i <= 15; i++) kraft += (long)counts[i] << (15 - i);
            if (kraft != (1L << 15)) return null; // 必须是满二叉树

            int maxLen = 15;
            while (counts[maxLen] == 0) maxLen--;

            int[] firstIndex = new int[16];
            int total = 0;
            for (i = 1; i <= 15; i++)
            {
                firstIndex[i] = total;
                total += counts[i];
            }
            int[] symbols = new int[total];
            int[] fill = new int[16];
            for (i = 1; i <= 15; i++) fill[i] = firstIndex[i];
            for (i = 0; i < count; i++)
            {
                int len = lengths[i];
                if (len != 0) symbols[fill[len]++] = i;
            }
            tree.Counts = counts;
            tree.Symbols = symbols;

            int fastBits = maxLen < 8 ? maxLen : 8;
            int tableSize = 1 << fastBits;
            if (tableBudget >= tableSize && symbolBudget >= total)
            {
                tableBudget -= tableSize;
                symbolBudget -= total;
                int[] table = new int[tableSize];
                for (i = 0; i < tableSize; i++) table[i] = -1;
                int code = 0;
                int idx = 0;
                int len2;
                for (len2 = 1; len2 <= 15; len2++)
                {
                    int c = counts[len2];
                    int k;
                    for (k = 0; k < c; k++)
                    {
                        int sym = symbols[idx + k];
                        if (len2 <= fastBits)
                        {
                            // 位流 LSB first，快表索引 = 码字的位反转
                            int r = ReverseBits(code, len2);
                            int step = 1 << len2;
                            int j;
                            for (j = r; j < tableSize; j += step) table[j] = sym | (len2 << 16);
                        }
                        code++;
                    }
                    idx += c;
                    code <<= 1;
                }
                tree.Fast = table;
                tree.FastBits = fastBits;
            }
            else
            {
                symbolBudget -= total;
                if (symbolBudget < -16L * 1024 * 1024) return null; // 硬上限
            }
            return tree;
        }

        private int ReadSymbol(HuffTree tree)
        {
            if (tree == null) return -1;
            if (tree.SingleSymbol >= 0) return tree.SingleSymbol;
            if (tree.Fast != null)
            {
                int e = tree.Fast[br.PeekBits(tree.FastBits)];
                if (e >= 0)
                {
                    br.Consume(e >> 16);
                    if (br.Error) return -1;
                    return e & 0xffff;
                }
            }
            int[] counts = tree.Counts;
            int[] symbols = tree.Symbols;
            int code = 0;
            int first = 0;
            int index = 0;
            int len;
            for (len = 1; len <= 15; len++)
            {
                code = (code << 1) | br.ReadBits(1);
                if (br.Error) return -1;
                int cnt = counts[len];
                if (code - first < cnt) return symbols[index + code - first];
                index += cnt;
                first = (first + cnt) << 1;
            }
            return -1;
        }

        private HuffTree ReadHuffmanCode(int alphabetSize)
        {
            int i;
            for (i = 0; i < alphabetSize; i++) codeLengths[i] = 0;

            if (br.ReadBits(1) != 0)
            {
                // --- 简单码长码 ---
                int numSymbols = br.ReadBits(1) + 1;
                int isFirst8 = br.ReadBits(1);
                int sym = br.ReadBits(isFirst8 != 0 ? 8 : 1);
                if (br.Error) return null;
                // 越界符号被忽略（libwebp 写入缓冲区但不进入受限视图）
                if (sym < alphabetSize) codeLengths[sym] = 1;
                if (numSymbols == 2)
                {
                    sym = br.ReadBits(8);
                    if (br.Error) return null;
                    if (sym < alphabetSize) codeLengths[sym] = 1;
                }
            }
            else
            {
                // --- 普通码长码 ---
                int numCodeLengths = 4 + br.ReadBits(4);
                if (br.Error) return null;
                for (i = 0; i < 19; i++) codeLengthScratch[i] = 0;
                for (i = 0; i < numCodeLengths; i++)
                {
                    codeLengthScratch[CodeLengthCodeOrder[i]] = br.ReadBits(3);
                }
                if (br.Error) return null;

                HuffTree clTree = BuildTree(codeLengthScratch, 19);
                if (clTree == null) return null;

                int maxSymbol;
                if (br.ReadBits(1) != 0)
                {
                    int lengthNbits = 2 + 2 * br.ReadBits(3);
                    maxSymbol = 2 + br.ReadBits(lengthNbits);
                    if (br.Error) return null;
                    if (maxSymbol > alphabetSize) return null;
                }
                else
                {
                    maxSymbol = alphabetSize;
                }

                int symbol = 0;
                int prevCodeLen = 8; // DEFAULT_CODE_LENGTH
                while (symbol < alphabetSize)
                {
                    if (maxSymbol-- == 0) break;
                    int codeLen = ReadSymbol(clTree);
                    if (codeLen < 0) return null;
                    if (codeLen < 16)
                    {
                        codeLengths[symbol++] = codeLen;
                        if (codeLen != 0) prevCodeLen = codeLen;
                    }
                    else
                    {
                        int slot = codeLen - 16;
                        if (slot > 2) return null;
                        int repeat = br.ReadBits(CodeLengthExtraBits[slot]) +
                                     CodeLengthRepeatOffsets[slot];
                        if (br.Error) return null;
                        if (symbol + repeat > alphabetSize) return null;
                        int value = (codeLen == 16) ? prevCodeLen : 0;
                        while (repeat-- > 0) codeLengths[symbol++] = value;
                    }
                }
            }
            if (br.Error) return null;
            return BuildTree(codeLengths, alphabetSize);
        }

        // --- 前缀码组 ------------------------------------------------------

        private bool ReadHuffmanCodes(int xsize, int ysize, int colorCacheBits,
                                      bool allowRecursion, out HuffTree[][] groups,
                                      out int groupBits, out int[] huffImage)
        {
            groups = null;
            groupBits = 0;
            huffImage = null;
            int numGroupsMax = 1;

            if (allowRecursion && br.ReadBits(1) != 0)
            {
                int huffmanPrecision = 2 + br.ReadBits(3);
                if (br.Error) return false;
                int hx = SubSampleSize(xsize, huffmanPrecision);
                int hy = SubSampleSize(ysize, huffmanPrecision);
                Plane sub = DecodeImageStream(hx, hy, false);
                if (sub == null) return false;
                if (sub.Width != hx || sub.Height != hy) return false;
                int[] img = sub.Pixels;
                int i;
                for (i = 0; i < img.Length; i++)
                {
                    int g = (img[i] >> 8) & 0xffff; // 红+绿两个字节
                    img[i] = g;
                    if (g + 1 > numGroupsMax) numGroupsMax = g + 1;
                }
                huffImage = img;
                groupBits = huffmanPrecision;
                if (numGroupsMax == 1)
                {
                    huffImage = null;
                    groupBits = 0;
                }
            }
            if (br.Error) return false;

            // 每个前缀码组至少占 20 位（5 棵树 × 4 位）
            long remaining = br.TotalBits - br.BitPos + 64;
            if (numGroupsMax > 65536) return false;
            if (remaining < (long)numGroupsMax * 20) return false;

            groups = new HuffTree[numGroupsMax][];
            int g2;
            for (g2 = 0; g2 < numGroupsMax; g2++)
            {
                HuffTree[] five = new HuffTree[5];
                int j;
                for (j = 0; j < 5; j++)
                {
                    int alphabetSize = (j == 0) ? 256 + 24 : (j == 4) ? 40 : 256;
                    if (j == 0 && colorCacheBits > 0) alphabetSize += 1 << colorCacheBits;
                    HuffTree t = ReadHuffmanCode(alphabetSize);
                    if (t == null) return false;
                    five[j] = t;
                }
                groups[g2] = five;
            }
            return true;
        }

        // --- 像素数据 ------------------------------------------------------

        private bool DecodeImageData(int[] pixels, int width, int height,
                                     HuffTree[][] groups, int groupBits,
                                     int[] huffImage, int[] cache, int cacheBits)
        {
            int count = width * height;
            int mask = (groupBits == 0) ? 0 : (1 << groupBits) - 1;
            int huffWidth = SubSampleSize(width, groupBits);
            bool singleGroup = groups.Length == 1;
            int lenCodeLimit = 256 + 24;
            int cacheLimit = lenCodeLimit + (cacheBits > 0 ? (1 << cacheBits) : 0);
            int cacheShift = 32 - cacheBits;

            HuffTree[] group = groups[0];
            int lastGroupIndex = -1;
            int pos = 0;
            int col = 0;
            int row = 0;

            while (pos < count)
            {
                if (!singleGroup)
                {
                    int gi = (row >> groupBits) * huffWidth + (col >> groupBits);
                    if (gi != lastGroupIndex)
                    {
                        if (gi < 0 || gi >= huffImage.Length) return false;
                        int g = huffImage[gi];
                        if (g < 0 || g >= groups.Length) return false;
                        group = groups[g];
                        lastGroupIndex = gi;
                    }
                }

                int code = ReadSymbol(group[0]);
                if (code < 0) return false;

                if (code < 256)
                {
                    // 字面量：G 已在 code 中，依次读 R、B、A
                    int red = ReadSymbol(group[1]);
                    int blue = ReadSymbol(group[2]);
                    int alpha = ReadSymbol(group[3]);
                    if (red < 0 || blue < 0 || alpha < 0) return false;
                    int argb = (alpha << 24) | (red << 16) | (code << 8) | blue;
                    pixels[pos] = argb;
                    if (cache != null)
                    {
                        uint h = (uint)argb * 0x1e35a7bdu;
                        cache[(int)(h >> cacheShift)] = argb;
                    }
                    pos++;
                    col++;
                    if (col >= width) { col = 0; row++; }
                }
                else if (code < lenCodeLimit)
                {
                    // LZ77 反向引用
                    int length = GetCopyValue(code - 256);
                    int distSymbol = ReadSymbol(group[4]);
                    if (distSymbol < 0) return false;
                    int dist = PlaneCodeToDistance(width, GetCopyValue(distSymbol));
                    if (br.Error) return false;
                    if (dist < 1 || pos < dist || count - pos < length) return false;
                    int p = pos;
                    int i;
                    for (i = 0; i < length; i++)
                    {
                        int argb = pixels[p - dist];
                        pixels[p] = argb;
                        if (cache != null)
                        {
                            uint h = (uint)argb * 0x1e35a7bdu;
                            cache[(int)(h >> cacheShift)] = argb;
                        }
                        p++;
                    }
                    pos = p;
                    col += length;
                    while (col >= width) { col -= width; row++; }
                }
                else if (code < cacheLimit)
                {
                    // 颜色缓存
                    int argb = cache[code - lenCodeLimit];
                    pixels[pos] = argb;
                    uint h = (uint)argb * 0x1e35a7bdu;
                    cache[(int)(h >> cacheShift)] = argb;
                    pos++;
                    col++;
                    if (col >= width) { col = 0; row++; }
                }
                else
                {
                    return false;
                }
                if (br.Error && pos < count) return false;
            }
            return !br.Error;
        }

        private int GetCopyValue(int symbol)
        {
            if (symbol < 4) return symbol + 1;
            int extraBits = (symbol - 2) >> 1;
            int offset = (2 + (symbol & 1)) << extraBits;
            return offset + br.ReadBits(extraBits) + 1;
        }

        private static int PlaneCodeToDistance(int xsize, int planeCode)
        {
            if (planeCode > 120) return planeCode - 120;
            int distCode = CodeToPlane[planeCode - 1];
            int yoffset = distCode >> 4;
            int xoffset = 8 - (distCode & 0xf);
            int dist = yoffset * xsize + xoffset;
            return dist >= 1 ? dist : 1;
        }

        // --- 逆变换（就地，与原实现等价）-----------------------------------

        private static void SubtractGreenInverse(int[] pixels, int count)
        {
            int i;
            for (i = 0; i < count; i++)
            {
                unchecked
                {
                    int argb = pixels[i];
                    int green = (argb >> 8) & 0xff;
                    int rb = (argb & 0x00ff00ff) + (green << 16) + green;
                    pixels[i] = (argb & (int)0xff00ff00u) | (rb & 0x00ff00ff);
                }
            }
        }

        private static bool PredictorInverse(Transform t, int[] pixels, int width, int height)
        {
            int bits = t.Bits;
            int[] modes = t.Data;
            int tilesPerRow = SubSampleSize(width, bits);
            if (modes == null || modes.Length < tilesPerRow * SubSampleSize(height, bits)) return false;
            if ((long)width * height > pixels.Length) return false;

            int x;
            // 第 0 行：首像素用模式 0（黑），其余用模式 1（左）
            unchecked { pixels[0] = AddPixels(pixels[0], (int)0xff000000u); }
            for (x = 1; x < width; x++) pixels[x] = AddPixels(pixels[x], pixels[x - 1]);

            int mask = (1 << bits) - 1;
            int y;
            for (y = 1; y < height; y++)
            {
                int rowOff = y * width;
                int prevRow = rowOff - width;
                int modeRow = (y >> bits) * tilesPerRow;
                // 首像素用模式 2（上）
                pixels[rowOff] = AddPixels(pixels[rowOff], pixels[prevRow]);
                x = 1;
                while (x < width)
                {
                    int mode = (modes[modeRow + (x >> bits)] >> 8) & 0xf;
                    if (mode > 13) return false; // 规范只定义 0..13
                    int xEnd = (x & ~mask) + (1 << bits);
                    if (xEnd > width) xEnd = width;
                    for (; x < xEnd; x++)
                    {
                        int idx = rowOff + x;
                        int left = pixels[idx - 1];
                        int top = pixels[prevRow + x];
                        int topLeft = pixels[prevRow + x - 1];
                        // x == width-1 时 top[1] 落到当前行第 0 个像素（与 libwebp 一致）
                        int topRight = pixels[prevRow + x + 1];
                        int pred;
                        switch (mode)
                        {
                            case 0: unchecked { pred = (int)0xff000000u; } break;
                            case 1: pred = left; break;
                            case 2: pred = top; break;
                            case 3: pred = topRight; break;
                            case 4: pred = topLeft; break;
                            case 5: pred = Average3(left, top, topRight); break;
                            case 6: pred = Average2(left, topLeft); break;
                            case 7: pred = Average2(left, top); break;
                            case 8: pred = Average2(topLeft, top); break;
                            case 9: pred = Average2(top, topRight); break;
                            case 10: pred = Average4(left, topLeft, top, topRight); break;
                            case 11: pred = Select(top, left, topLeft); break;
                            case 12: pred = ClampedAddSubtractFull(left, top, topLeft); break;
                            default: pred = ClampedAddSubtractHalf(left, top, topLeft); break;
                        }
                        pixels[idx] = AddPixels(pixels[idx], pred);
                    }
                }
            }
            return true;
        }

        private static bool CrossColorInverse(Transform t, int[] pixels, int width, int height)
        {
            int bits = t.Bits;
            int[] modes = t.Data;
            int tilesPerRow = SubSampleSize(width, bits);
            if (modes == null || modes.Length < tilesPerRow * SubSampleSize(height, bits)) return false;
            if ((long)width * height > pixels.Length) return false;

            int mask = (1 << bits) - 1;
            int y;
            int idx = 0;
            for (y = 0; y < height; y++)
            {
                int modeRow = (y >> bits) * tilesPerRow;
                int x = 0;
                while (x < width)
                {
                    int code = modes[modeRow + (x >> bits)];
                    int greenToRed = code & 0xff;
                    int greenToBlue = (code >> 8) & 0xff;
                    int redToBlue = (code >> 16) & 0xff;
                    int xEnd = (x & ~mask) + (1 << bits);
                    if (xEnd > width) xEnd = width;
                    for (; x < xEnd; x++)
                    {
                        int argb = pixels[idx + x];
                        int green = (argb >> 8) & 0xff;
                        int red = (argb >> 16) & 0xff;
                        int blue = argb & 0xff;
                        int nr = red + (((sbyte)greenToRed * (sbyte)green) >> 5);
                        nr &= 0xff;
                        int nb = blue + (((sbyte)greenToBlue * (sbyte)green) >> 5);
                        nb += ((sbyte)redToBlue * (sbyte)nr) >> 5;
                        nb &= 0xff;
                        pixels[idx + x] = (argb & unchecked((int)0xff00ff00)) | (nr << 16) | nb;
                    }
                }
                idx += width;
            }
            return true;
        }

        private static int[] ColorIndexInverse(Transform t, int[] src, int subWidth,
                                               int width, int height)
        {
            int[] map = t.Data;
            if (map == null) return null;
            if ((long)width * height > MaxPixels) return null;
            if ((long)subWidth * height > src.Length) return null;
            int[] dst = new int[width * height];
            int bitsPerPixel = 8 >> t.Bits;
            int si = 0;
            int di = 0;
            int y;
            if (t.Bits == 0)
            {
                if (map.Length < 256) return null;
                for (y = 0; y < height; y++)
                {
                    int x;
                    for (x = 0; x < width; x++)
                    {
                        dst[di++] = map[(src[si++] >> 8) & 0xff];
                    }
                }
            }
            else
            {
                int perByte = 1 << t.Bits;
                int bitMask = (1 << bitsPerPixel) - 1;
                if (map.Length < (1 << bitsPerPixel)) return null; // 索引只取 bitsPerPixel 位
                for (y = 0; y < height; y++)
                {
                    int x = 0;
                    for (; x + perByte <= width; x += perByte)
                    {
                        int packed = (src[si++] >> 8) & 0xff;
                        int k;
                        for (k = 0; k < perByte; k++)
                        {
                            dst[di++] = map[packed & bitMask];
                            packed >>= bitsPerPixel;
                        }
                    }
                    if (x < width)
                    {
                        int packed2 = (src[si++] >> 8) & 0xff;
                        for (; x < width; x++)
                        {
                            dst[di++] = map[packed2 & bitMask];
                            packed2 >>= bitsPerPixel;
                        }
                    }
                }
            }
            return dst;
        }
    }
}
