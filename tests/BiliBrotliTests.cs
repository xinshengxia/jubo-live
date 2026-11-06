using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

// --------------------------------------------------------------------------------------------
// B站弹幕 protover=3（brotli）的离线回归。
//
// 合成向量由本机 Python 3.14 + brotli 1.2.1 一次性生成后以十六进制常量内嵌（运行测试不需要
// Python，也不联网）。生成方式与等价命令见 src/Brotli/README.md；生成步骤：
//   1) JSON 用 ASCII 直写（下面的 BiliBrotliJson），构造普通嵌套帧 = 16 字节大端头(len,16,ver=0,op=5,seq=1) + JSON；
//   2) nested       = brotli.compress(inner, quality=4, lgwin=22)                 → 模拟 ver=3 帧体；
//   3) nested-zlib  = brotli.compress(packet(ver=2, zlib.compress(inner)))        → ver=3 → ver=2 → JSON 嵌套；
//   4) bomb         = brotli.compress(9 MiB 的 0x00, quality=1)                   → 展开预算拒绝；
//   5) half         = brotli.compress(5 MiB 的 0x00, quality=1)                   → 两帧共享 8 MiB 预算。
// 这些压缩字节只用于本测试，没有进入发布产物。
// --------------------------------------------------------------------------------------------
internal static partial class HardeningTests
{
    private const string BiliBrotliNestedHex =
        "1B640000C427E6FDD817E2EEE5C96D4ED04F30CA16990285208839C1D1FF98250882FC62BA9FC143E4E759679835" +
        "2904AC0F8B23BC6E571154413305D5B255D572144111544DEF0BB8BE0FB7D5FCB69E42E8DA029AF7E1B69ADFD653" +
        "BE739D5FD0EFFF01";

    private const string BiliBrotliNestedZlibHex =
        "0B388000000071001000020000000500000001789C636060486510600001562066AC564ACE4D51B2527271F4F30D" +
        "8DF70D7657D251CACC4BCB57B28A8E36D031D43132D5313433373434B330D001F28D8C6375948A2BF34A32524B32" +
        "939574A2CD759482615C85D0E2D422A5D8D85A001BBD194303";

    private const string BiliBrotliBombHex =
        "ABFFFF000002800DE0C0359CD46603B80087E5DBFB00FAFF0F002000D8000E5CC3496D36804B60E13D00F0ED7D00" +
        "FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE01258780F007C7B" +
        "1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD46603B80416DE0300" +
        "DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27B5D9002E8185F7" +
        "00C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5CC3496D36804B60" +
        "E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE0" +
        "1258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD466" +
        "03B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27" +
        "B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5C" +
        "C3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF030008003680" +
        "03D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF00000280" +
        "0DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F0080" +
        "00600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F" +
        "002000D8000E5CC3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FE" +
        "FF03000800368003D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0F" +
        "A0FFFF000002800DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806F" +
        "EF03E8FF3F008000600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0017E0B07C7B" +
        "1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD46603B80416DE0300" +
        "DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27B5D9002E8185F7" +
        "00C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5CC3496D36804B60" +
        "E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE0" +
        "1258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD466" +
        "03B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27" +
        "B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5C" +
        "C3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF030008003680" +
        "03D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF00000280" +
        "0DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F0080" +
        "00600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F" +
        "002000D8000E5CC3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FE" +
        "FF03000800368003D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0F" +
        "A0FFFF000002800DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806F" +
        "EF03E8FF3F008000600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00" +
        "E0DBFB00FAFF0F002000D8000E5CC3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F0" +
        "1E00F8F63E80FEFF03000800368003D770529B0DE0021C966FEF03E8FF3F008000600338700D27B5D9002E8185F7" +
        "00C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5CC3496D36804B60" +
        "E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE0" +
        "1258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD466" +
        "03B80416DE0300DFDE0718";

    private const string BiliBrotliHalfHex =
        "ABFFFF000002800DE0C0359CD46603B80087E5DBFB00FAFF0F002000D8000E5CC3496D36804B60E13D00F0ED7D00" +
        "FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE01258780F007C7B" +
        "1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD46603B80416DE0300" +
        "DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27B5D9002E8185F7" +
        "00C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5CC3496D36804B60" +
        "E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF03000800368003D770529B0DE0" +
        "1258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD466" +
        "03B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27" +
        "B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5C" +
        "C3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FEFF030008003680" +
        "03D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF00000280" +
        "0DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F0080" +
        "00600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F" +
        "002000D8000E5CC3496D36804B60E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63E80FE" +
        "FF03000800368003D770529B0DE01258780F007C7B1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0F" +
        "A0FFFF000002800DE0C0359CD46603B80416DE0300DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806F" +
        "EF03E8FF3F008000600338700D27B5D9002E8185F700C0B7F701F4FF1F004000B0011CB88693DA6C0017E0B07C7B" +
        "1F40FFFF010004001BC0816B38A9CD0670092CBC0700BEBD0FA0FFFF000002800DE0C0359CD46603B80416DE0300" +
        "DFDE07D0FF7F000001C00670E01A4E6AB3015C020BEF01806FEF03E8FF3F008000600338700D27B5D9002E8185F7" +
        "00C0B7F701F4FF1F004000B0011CB88693DA6C0097C0C27B00E0DBFB00FAFF0F002000D8000E5CC3496D36804B60" +
        "E13D00F0ED7D00FDFF070010006C0007AEE1A4361BC025B0F01E00F8F63EC0";

    private const string BiliBrotliJson =
        "{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16711680,0,123],\"synthetic\",[7,\"Synthetic User\"]]}";

    private static void RegisterBiliBrotliCases()
    {
        TestRunner.Add("弹幕-协议", "B站 ver3 brotli 帧解出 DANMU_MSG", TestBiliBrotliVer3Decode);
        TestRunner.Add("弹幕-协议", "B站 ver3 brotli 嵌套 ver2 帧仍可解析", TestBiliBrotliNestedVer2);
        TestRunner.Add("弹幕-协议", "B站 ver3 brotli 有界解压：炸弹/截断/垃圾拒绝", TestBiliBrotliBounded);
        TestRunner.Add("弹幕-协议", "B站 ver3 多帧共享弹幕展开预算", TestBiliBrotliSharedBudget);
        TestRunner.Add("弹幕-协议", "B站 认证包仍请求 protover=2", TestBiliBrotliAuthPayload);
    }

    private static byte[] BiliBrotliFromHex(string hex)
    {
        if (hex == null || (hex.Length & 1) != 0) throw new InvalidDataException("测试向量不是偶数长度十六进制");
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        return bytes;
    }

    /// 按 B站 WS 帧格式（16 字节大端头：len/16/ver/op=5/seq=1）构造帧；与 Python 向量的
    /// struct.pack('>IHHII', 16+len, 16, ver, 5, 1) 完全一致（含 sequence=1）。
    private static byte[] BiliBrotliPacket(byte[] body, int version)
    {
        byte[] packet = new byte[16 + body.Length];
        Type clientType = DanmakuType("BiliDanmakuClient");
        CallStatic(clientType, "WriteBe32", new object[] { packet, 0, packet.Length });
        CallStatic(clientType, "WriteBe16", new object[] { packet, 4, 16 });
        CallStatic(clientType, "WriteBe16", new object[] { packet, 6, version });
        CallStatic(clientType, "WriteBe32", new object[] { packet, 8, 5 });
        CallStatic(clientType, "WriteBe32", new object[] { packet, 12, 1 });
        Array.Copy(body, 0, packet, 16, body.Length);
        return packet;
    }

    private sealed class BiliBrotliDanmakuSink
    {
        internal readonly List<object> Items = new List<object>();
        // 事件委托类型是 internal DanmakuHandler(DanmakuItem)：这里用 object 参数靠 .NET 的
        // 宽松委托绑定接住（本测试的 sink 只读取字段，不需要强类型）。
        public void OnDanmaku(object item) { Items.Add(item); }
    }

    private static object BiliBrotliClient(out Type clientType, out MethodInfo feed)
    {
        clientType = DanmakuType("BiliDanmakuClient");
        object client = Activator.CreateInstance(clientType, new object[] { "7777", null });
        feed = clientType.GetMethod("Feed", ReviewInstance);
        return client;
    }

    private static BiliBrotliDanmakuSink BiliBrotliAttachSink(object client, Type clientType)
    {
        BiliBrotliDanmakuSink sink = new BiliBrotliDanmakuSink();
        EventInfo received = clientType.GetEvent("DanmakuReceived");
        received.AddEventHandler(client, Delegate.CreateDelegate(received.EventHandlerType, sink,
            typeof(BiliBrotliDanmakuSink).GetMethod("OnDanmaku")));
        return sink;
    }

    private static void TestBiliBrotliVer3Decode()
    {
        byte[] compressed = BiliBrotliFromHex(BiliBrotliNestedHex);
        byte[] expected = BiliBrotliPacket(Encoding.ASCII.GetBytes(BiliBrotliJson), 0);

        // 1) 解码函数本身：brotli 帧体解出的字节必须等于“同一 JSON 的普通嵌套帧”。
        byte[] decoded = (byte[])CallStatic(DanmakuType("BiliDanmakuClient"), "DecompressBrotli", new object[] { compressed });
        Check(decoded.Length == expected.Length, "B站 ver3：解压长度与嵌套帧一致");
        bool same = decoded.Length == expected.Length;
        for (int i = 0; same && i < expected.Length; i++) same = decoded[i] == expected[i];
        Check(same, "B站 ver3：解压字节与嵌套帧逐字节一致");

        // 2) 生产解包路径：op=5 ver=3 帧经 Feed 递归解出，并派发成 DanmakuItem。
        Type clientType; MethodInfo feed;
        object client = BiliBrotliClient(out clientType, out feed);
        BiliBrotliDanmakuSink sink = BiliBrotliAttachSink(client, clientType);
        feed.Invoke(client, new object[] { BiliBrotliPacket(compressed, 3) });
        Check(sink.Items.Count == 1, "B站 ver3：Feed 解出并派发 1 条弹幕");
        if (sink.Items.Count == 1)
        {
            object item = sink.Items[0];
            Type itemType = item.GetType();
            Check((string)itemType.GetField("Text").GetValue(item) == "synthetic", "B站 ver3：弹幕文本正确");
            Check((string)itemType.GetField("UserName").GetValue(item) == "Synthetic User", "B站 ver3：用户名正确");
            Check((int)itemType.GetField("Color").GetValue(item) == 16711680, "B站 ver3：颜色字段正确");
        }
        clientType.GetMethod("Stop").Invoke(client, null);
    }

    private static void TestBiliBrotliNestedVer2()
    {
        Type clientType; MethodInfo feed;
        object client = BiliBrotliClient(out clientType, out feed);
        BiliBrotliDanmakuSink sink = BiliBrotliAttachSink(client, clientType);
        // ver=3 帧体解出的是一个 ver=2（zlib）帧，再由既有 zlib 路径解出 JSON —— 证明递归复用同一预算/深度。
        feed.Invoke(client, new object[] { BiliBrotliPacket(BiliBrotliFromHex(BiliBrotliNestedZlibHex), 3) });
        Check(sink.Items.Count == 1, "B站 ver3→ver2 嵌套帧仍能解出 1 条弹幕");
        if (sink.Items.Count == 1)
            Check((string)sink.Items[0].GetType().GetField("Text").GetValue(sink.Items[0]) == "synthetic",
                "B站 ver3→ver2 嵌套帧文本正确");
        clientType.GetMethod("Stop").Invoke(client, null);
    }

    private static void TestBiliBrotliBounded()
    {
        Type clientType = DanmakuType("BiliDanmakuClient");
        bool refused = false;
        try { CallStatic(clientType, "DecompressBrotli", new object[] { BiliBrotliFromHex(BiliBrotliBombHex) }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
        Check(refused, "B站 ver3：解压炸弹超过 8 MiB 展开预算被拒绝");

        byte[] nested = BiliBrotliFromHex(BiliBrotliNestedHex);
        byte[] truncated = new byte[nested.Length - 3];
        Array.Copy(nested, truncated, truncated.Length);
        refused = false;
        try { CallStatic(clientType, "DecompressBrotli", new object[] { truncated }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
        Check(refused, "B站 ver3：截断的 brotli 流被拒绝");

        refused = false;
        try { CallStatic(clientType, "DecompressBrotli", new object[] { Encoding.ASCII.GetBytes("not-brotli") }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
        Check(refused, "B站 ver3：非 brotli 垃圾字节被拒绝");

        refused = false;
        try { CallStatic(clientType, "DecompressBrotli", new object[] { new byte[0] }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
        Check(refused, "B站 ver3：空帧体被拒绝");
    }

    private static void TestBiliBrotliSharedBudget()
    {
        Type clientType; MethodInfo feed;
        object client = BiliBrotliClient(out clientType, out feed);
        byte[] half = BiliBrotliFromHex(BiliBrotliHalfHex);
        // 每个 ver=3 帧各展开 5 MiB；单帧在 8 MiB 上限内，但两帧共享同一 remaining → 第二帧超预算。
        byte[] bundle = Join(BiliBrotliPacket(half, 3), BiliBrotliPacket(half, 3));
        bool refused = false;
        try { feed.Invoke(client, new object[] { bundle }); }
        catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
        Check(refused, "B站 ver3：多个 brotli 帧共享 8 MiB 展开预算，超限拒绝");
        clientType.GetMethod("Stop").Invoke(client, null);
    }

    private static void TestBiliBrotliAuthPayload()
    {
        Type clientType = DanmakuType("BiliDanmakuClient");
        object client = Activator.CreateInstance(clientType, new object[] { "7777", null });
        clientType.GetField("_uid", ReviewInstance).SetValue(client, 12345L);
        clientType.GetField("_realRoomId", ReviewInstance).SetValue(client, "545068");
        clientType.GetField("_buvid", ReviewInstance).SetValue(client, "synthetic-buvid");
        clientType.GetField("_token", ReviewInstance).SetValue(client, "synthetic-token");
        string payload = (string)clientType.GetMethod("AuthPayload", ReviewInstance).Invoke(client, null);
        string expected = "{\"uid\":12345,\"roomid\":545068,\"protover\":2,\"platform\":\"web\",\"type\":2,"
            + "\"buvid\":\"synthetic-buvid\",\"key\":\"synthetic-token\"}";
        Check(payload == expected, "B站 认证包内容不变且 protover=2（ver3 只做防御性解码）");
        clientType.GetMethod("Stop").Invoke(client, null);
    }
}
