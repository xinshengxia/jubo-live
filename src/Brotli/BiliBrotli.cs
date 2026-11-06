using System;
using System.IO;
using size_t = BrotliSharpLib.Brotli.SizeT;
using BrotliDecoderState = BrotliSharpLib.Brotli.BrotliDecoderStateStruct;

namespace BrotliSharpLib
{
    /// <summary>
    /// BrotliSharpLib 解码子集的本地封层：只用于 B站弹幕 protover=3（ver=3 帧体是原始 brotli 流）的有界解压。
    ///
    /// 上游：master131/BrotliSharpLib，MIT，commit f0f587c8d7c96a48df04ce1bb80ff77232061e0d。
    /// 本地只保留解码路径（Decode/* 与共享 Constants/Structures/Helpers/Detect），去掉了压缩门面
    /// （Brotli.cs 的 CompressBuffer、BrotliStream.cs）与仅编码使用的 Tables.cs 静态构造器。
    /// 解压循环按上游 Brotli.DecompressBuffer 的调用顺序改写为：分块输出 + live-hub 展开预算。
    ///
    /// 许可原文：licenses/BrotliSharpLib-LICENSE.txt（master131 MIT）、
    /// licenses/brotli-reference-LICENSE.txt（Google Brotli 作者 MIT，上游 Brotli-LICENSE）。
    /// 来源、文件映射与本地补丁见 src/Brotli/README.md、THIRD_PARTY_NOTICES.md、licenses/SOURCES.json。
    /// </summary>
    internal static class BiliBrotli
    {
        /// <summary>
        /// 每次输出最多取 64 KiB，配合展开预算在超限时立即失败，避免一次性按解压后大小分配。
        /// </summary>
        internal const int ChunkBytes = 64 * 1024;

        /// <summary>
        /// 解压一个完整的 brotli 流（对应一个 ver=3 帧体）。展开长度超过 maxOutput 时抛
        /// <see cref="InvalidDataException"/>；输入截断、损坏同样抛 InvalidDataException。
        /// 与上游 BrotliSharpLib 相同：流成功结束后忽略残留输入字节（B站 ver=3 帧为单流）。
        /// 调用方负责 null/长度校验的语义（与 BiliDanmakuClient.Inflate 对齐）。
        /// </summary>
        internal static unsafe byte[] Decompress(byte[] input, int offset, int length, int maxOutput)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (offset < 0 || length < 0 || offset > input.Length - length)
                throw new ArgumentOutOfRangeException("offset");
            if (maxOutput < 0) throw new ArgumentOutOfRangeException("maxOutput");

            BrotliDecoderState state = Brotli.BrotliCreateDecoderState();
            Brotli.BrotliDecoderStateInit(ref state);
            try
            {
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] writeBuf = new byte[ChunkBytes];
                    fixed (byte* inBuffer = input)
                    {
                        fixed (byte* outBuffer = writeBuf)
                        {
                            size_t availableIn = length;
                            byte* nextIn = inBuffer + offset;
                            byte* nextOut = outBuffer;
                            size_t availableOut = writeBuf.Length;
                            size_t total = 0;
                            Brotli.BrotliDecoderResult result;
                            do
                            {
                                result = Brotli.BrotliDecoderDecompressStream(
                                    ref state, &availableIn, &nextIn, &availableOut, &nextOut, &total);
                                int produced = writeBuf.Length - (int)availableOut;
                                if (produced > 0)
                                {
                                    if (output.Length > maxOutput - (long)produced)
                                        throw new InvalidDataException("brotli 弹幕展开超过预算");
                                    output.Write(writeBuf, 0, produced);
                                    availableOut = writeBuf.Length;
                                    nextOut = outBuffer;
                                }
                                if (result == Brotli.BrotliDecoderResult.BROTLI_DECODER_RESULT_NEEDS_MORE_INPUT)
                                    throw new InvalidDataException("brotli 数据不完整");
                                if (result == Brotli.BrotliDecoderResult.BROTLI_DECODER_RESULT_ERROR)
                                    throw new InvalidDataException("brotli 解压失败（error_code=" + state.error_code + "）");
                            }
                            while (result != Brotli.BrotliDecoderResult.BROTLI_DECODER_RESULT_SUCCESS);
                        }
                    }
                    return output.ToArray();
                }
            }
            finally
            {
                Brotli.BrotliDecoderStateCleanup(ref state);
            }
        }
    }
}
