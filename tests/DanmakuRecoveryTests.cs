using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void RegisterDanmakuRecoveryCases()
    {
        TestRunner.Add("弹幕-协议", "Tars 类型不匹配与损坏长度", TestTarsRecovery);
        TestRunner.Add("弹幕-房间", "虎牙网页状态与未知状态文案", TestHuyaRoomState);
        TestRunner.Add("弹幕-窗口", "窗口恢复宽限与播放器退出", TestPlayerFollow);
        TestRunner.Add("弹幕-连接", "发送失败只终止故障连接", TestFailedSocketSend);
        TestRunner.Add("弹幕-连接", "虎牙心跳缺回包与恢复", TestHeartbeatAckWatch);
        TestRunner.Add("弹幕-抖音", "保存的 Cookie 提供 WSS ttwid", delegate
        {
            Type client = DanmakuType("DouyinDanmakuClient");
            Check((string)CallStatic(client, "SavedTtwid", "x=1; ttwid=test-token; z=2") == "test-token", "抖音：读取中间位置 ttwid");
            Check((string)CallStatic(client, "SavedTtwid", "ttwid=first; x=2") == "first", "抖音：读取首项 ttwid");
            Check((string)CallStatic(client, "SavedTtwid", "badttwid=x") == "", "抖音：Cookie 名必须精确匹配");
        });
    }

    private static object Tars(byte[] bytes)
    {
        return Activator.CreateInstance(DanmakuType("TarsReader"), new object[] { bytes });
    }
    private static object ReadTars(object reader, string method, params object[] args)
    {
        return reader.GetType().GetMethod(method).Invoke(reader, args);
    }
    private static void TestTarsRecovery()
    {
        // 目标 tag 命中未知类型：三条旧异常路径都必须返回调用方的缺省值。
        foreach (int type in new int[] { 14, 15 })
        {
            byte[] bytes = new byte[] { (byte)type, 0xAA };
            Check((long)ReadTars(Tars(bytes), "Num", 0, 77L) == 77, "Tars：Num 命中未知类型返回 fallback " + type);
            Check((string)ReadTars(Tars(bytes), "Str", 0, "fallback") == "fallback", "Tars：Str 命中未知类型返回 fallback " + type);
            byte[] sentinel = new byte[] { 9 };
            Check(Object.ReferenceEquals(ReadTars(Tars(bytes), "Bytes", 0, sentinel), sentinel), "Tars：Bytes 命中未知类型返回 fallback " + type);
        }
        // 已知但不匹配的类型可以安全跳过；下一字段仍然能读。
        object mismatch = Tars(new byte[] { 0x06, 1, 65, 0x10, 7 });
        Check((long)ReadTars(mismatch, "Num", 0, 99L) == 99, "Tars：已知不匹配类型返回 fallback");
        Check((long)ReadTars(mismatch, "Num", 1, 99L) == 7, "Tars：跳过已知类型后可读取后续字段");

        foreach (byte[] bytes in new byte[][] {
            new byte[] { 0x00 }, new byte[] { 0x01, 1 }, new byte[] { 0x02, 1, 2, 3 },
            new byte[] { 0x03, 1, 2 }, new byte[] { 0xF0 },
            new byte[] { 0x06 }, new byte[] { 0x06, 8, 65 },
            new byte[] { 0x07, 0xFF, 0xFF, 0xFF, 0xFF },
            new byte[] { 0x0D, 0x00, 0x00, 0xFF }, // 负数 Bytes 长度
            new byte[] { 0x0D, 0x01, 0x0C },       // Bytes 占位类型错误
            new byte[] { 0x09, 0x02, 0x7F, 0xFF, 0xFF, 0xFF },
            new byte[] { 0x08, 0x02, 0x7F, 0xFF, 0xFF, 0xFF },
            new byte[] { 0x0A, 0x00, 1 }           // 缺少结构体结束
        })
        {
            object reader = Tars(bytes);
            int tag = 0;
            object[] headArgs = new object[] { tag };
            int type = (int)ReadTars(reader, "Head", headArgs);
            Check(!(bool)ReadTars(reader, "TrySkip", type), "Tars：损坏报文安全中止 " + BitConverter.ToString(bytes));
            Check((bool)reader.GetType().GetProperty("AtEnd").GetValue(reader, null), "Tars：失败游标收敛到末尾");
        }
        Check((long)ReadTars(Tars(new byte[] { 0x02 }), "Num", 0, 71L) == 71, "Tars：截断数值使用 fallback");
        Check((string)ReadTars(Tars(new byte[] { 0x06, 255 }), "Str", 0, "safe") == "safe", "Tars：截断字符串使用 fallback");
        Check(ReadTars(Tars(new byte[] { 0x0D, 0, 0, 127 }), "Bytes", 0, null) == null, "Tars：截断字节数组使用 fallback");
        var nested = new List<byte>();
        for (int i = 0; i < 100; i++) nested.Add(0x0A);
        for (int i = 0; i < 100; i++) nested.Add(0x0B);
        object deep = Tars(nested.ToArray());
        ReadTars(deep, "Head", new object[] { 0 });
        Check(!(bool)ReadTars(deep, "TrySkip", 10), "Tars：深层嵌套受到限制");

        // 外层帧可以丢弃坏消息，但后续合法消息照常发出；损坏颜色不能丢正文。
        Type clientType = DanmakuType("HuyaDanmakuClient");
        object client = Activator.CreateInstance(clientType, new object[] { "test" });
        MethodInfo parse = clientType.GetMethod("ParseMessageNotice", BindingFlags.NonPublic | BindingFlags.Instance);
        object item = parse.Invoke(client, new object[] { new byte[] { 0x36, 2, 79, 75, 0x5A, 0x0F, 0x0B } });
        Check(item != null && (string)item.GetType().GetField("Text").GetValue(item) == "OK", "Tars：损坏可选颜色保留正文");
        Check(parse.Invoke(client, new object[] { new byte[] { 0x36, 127 } }) == null, "Tars：损坏正文仅丢当前消息");
        Check(parse.Invoke(client, new object[] { new byte[] { 0x36, 2, 79, 75 } }) != null, "Tars：坏消息后下一条仍可解析");
    }

    private static void TestHuyaRoomState()
    {
        Type state = DanmakuType("HuyaRoomState");
        foreach (string kind in new string[] { "on", "off", "replay" })
        {
            string expected = kind == "on" ? "Live" : kind == "off" ? "Offline" : "Replay";
            object result = CallStatic(state, "Parse", "<span class='room liveStatus-" + kind + " active'>x</span>");
            Check(result.ToString() == expected, "虎牙状态：识别 " + kind);
            string suffix = (string)CallStatic(state, "Suffix", result);
            Check(suffix == (kind == "on" ? "" : kind == "off" ? "（未开播）" : "（回放中）"), "虎牙状态：文案 " + kind);
        }
        foreach (string page in new string[] { null, "", "{\"status\":422}", "<script>var x='liveStatus-off'</script>" })
        {
            object unknown = CallStatic(state, "Parse", page);
            Check(unknown.ToString() == "Unknown", "虎牙状态：缺失/错误响应为未知");
            Check((string)CallStatic(state, "Suffix", unknown) == "", "虎牙状态：未知不显示未开播");
        }
    }

    private static void TestPlayerFollow()
    {
        Type policy = DanmakuType("PlayerFollowPolicy");
        Check(!(bool)CallStatic(policy, "ShouldExit", true, 7000), "窗口：消失 7 秒不会退出");
        Check(!(bool)CallStatic(policy, "ShouldExit", true, 29999), "窗口：30 秒前继续等待");
        Check((bool)CallStatic(policy, "ShouldExit", true, 30000), "窗口：30 秒宽限结束退出");
        Check((bool)CallStatic(policy, "ShouldExit", false, 0), "窗口：播放器已退出立即收工");
        Check(!(bool)CallStatic(policy, "ShouldExit", null, 500), "窗口：进程查询未知不是退出证据");
    }

    private static void TestFailedSocketSend()
    {
        Type sender = DanmakuType("DanmakuSocket");
        using (var oldSocket = new ClientWebSocket())
        using (var newSocket = new ClientWebSocket())
        {
            bool threw = false;
            try { CallStatic(sender, "Send", oldSocket, new byte[] { 1 }, new object()); }
            catch (TargetInvocationException) { threw = true; }
            Check(threw, "发送：连接非 Open 明确返回失败");
            Check(oldSocket.State == WebSocketState.Aborted || oldSocket.State == WebSocketState.Closed,
                "发送：故障连接被终止（" + oldSocket.State + "）");
            Check(newSocket.State == WebSocketState.None, "发送：旧连接失败不影响新连接");
        }
    }

    private static void TestHeartbeatAckWatch()
    {
        Type type = DanmakuType("HeartbeatAckWatch");
        object watch = Activator.CreateInstance(type, true);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo begin = type.GetMethod("Begin", flags), ack = type.GetMethod("Acknowledge", flags);
        MethodInfo check = type.GetMethod("ShouldReconnect", flags);
        begin.Invoke(watch, new object[] { 0 });
        Check(!(bool)check.Invoke(watch, new object[] { 14999 }), "心跳：回包期限前继续等待");
        Check(!(bool)check.Invoke(watch, new object[] { 15000 }), "心跳：单次缺回包不重连");
        Check(!(bool)check.Invoke(watch, new object[] { 30000 }), "心跳：同一超时不会重复累计");
        begin.Invoke(watch, new object[] { 60000 });
        Check((bool)check.Invoke(watch, new object[] { 75000 }), "心跳：连续两次缺回包重连");
        ack.Invoke(watch, null);
        Check(!(bool)check.Invoke(watch, new object[] { 76000 }), "心跳：收到迟到回包解除连续失败");
        begin.Invoke(watch, new object[] { int.MaxValue - 5000 });
        Check(!(bool)check.Invoke(watch, new object[] { unchecked(int.MaxValue - 5000 + 15000) }), "心跳：计时回绕仍只算一次失败");

        // cmd4 没有 vData 时也必须确认，不能被 Bytes(null) 的早退吞掉。
        Type clientType = DanmakuType("HuyaDanmakuClient");
        object client = Activator.CreateInstance(clientType, new object[] { "test" });
        clientType.GetField("_heartbeatWatch", flags).SetValue(client, watch);
        clientType.GetMethod("Dispatch", flags).Invoke(client, new object[] { new byte[] { 0x00, 4 }, 2 });
        begin.Invoke(watch, new object[] { 90000 });
        Check(!(bool)check.Invoke(watch, new object[] { 105000 }), "心跳：cmd4 实际分发清除此前失败");

        Type dyType = DanmakuType("DouyinDanmakuClient");
        object dy = Activator.CreateInstance(dyType, new object[] { "test" });
        object dyWatch = dyType.GetField("_heartbeatWatch", flags).GetValue(dy);
        begin.Invoke(dyWatch, new object[] { 0 });
        Check(!(bool)check.Invoke(dyWatch, new object[] { 5000 }), "抖音心跳：第一次 5 秒缺回包不重连");
        // 与真实 hb 包相同的 payload_type，故意不附 payload，验证分派早退路径。
        dyType.GetMethod("HandleFrame", flags).Invoke(dy, new object[] { new byte[] { 0x3A, 2, 104, 98 }, null, 0L });
        begin.Invoke(dyWatch, new object[] { 10000 });
        Check(!(bool)check.Invoke(dyWatch, new object[] { 15000 }), "抖音心跳：hb 实际分发清除失败");
        begin.Invoke(dyWatch, new object[] { 20000 });
        Check((bool)check.Invoke(dyWatch, new object[] { 25000 }), "抖音心跳：两个 10 秒周期缺回包触发恢复");
    }
}
