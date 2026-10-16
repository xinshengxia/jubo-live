using System;
using System.IO;

// 每条消息独立预算；超限中止当前连接，让已有重连策略恢复，绝不解析消息残片。
internal static class DanmakuMessageLimits
{
    internal const int MaxWireBytes = 2 * 1024 * 1024;
    internal const int MaxExpandedBytes = 8 * 1024 * 1024;
    internal const int MaxCompressionDepth = 4;

    internal static void CheckAppend(long current, int count, int limit)
    {
        if (count < 0 || current < 0 || current > limit - (long)count)
            throw new InvalidDataException("弹幕消息超过资源上限");
    }

    internal static byte[] ReadExpanded(Stream stream)
    {
        using (var output = new MemoryStream())
        {
            byte[] buffer = new byte[8192]; int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            { CheckAppend(output.Length, read, MaxExpandedBytes); output.Write(buffer, 0, read); }
            return output.ToArray();
        }
    }
}
