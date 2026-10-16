using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;

internal static class DanmakuSocket
{
    internal static byte[] ReceiveMessage(WebSocket socket, byte[] chunk)
    {
        using (var message = new MemoryStream())
        {
            while (true)
            {
                WebSocketReceiveResult result = socket.ReceiveAsync(new ArraySegment<byte>(chunk), CancellationToken.None).Result;
                if (result.MessageType == WebSocketMessageType.Close) return null;
                try { DanmakuMessageLimits.CheckAppend(message.Length, result.Count, DanmakuMessageLimits.MaxWireBytes); }
                catch (InvalidDataException) { socket.Abort(); throw; }
                message.Write(chunk, 0, result.Count);
                if (result.EndOfMessage) return message.ToArray();
            }
        }
    }

    // 捕获本次连接，不从客户端的可变 _socket 重新取值。
    // 失败时只 Abort 这条连接，唤醒挂起的 ReceiveAsync，让 Run 负责重连。
    internal static void Send(ClientWebSocket socket, byte[] packet, object gate)
    {
        lock (gate)
        {
            try
            {
                if (socket == null || socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("弹幕连接已关闭");
                using (CancellationTokenSource cts = new CancellationTokenSource(5000))
                    socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, cts.Token).Wait();
            }
            catch
            {
                Abort(socket);
                throw;
            }
        }
    }

    internal static void Abort(ClientWebSocket socket)
    {
        if (socket == null) return;
        try { socket.Abort(); }
        catch (ObjectDisposedException) { return; } // Stop 已完成关闭。
    }
}

internal sealed class HeartbeatAckWatch
{
    internal const int TimeoutMs = 15000;
    private readonly object _gate = new object();
    private bool _pending;
    private int _sentAt;
    private int _missed;
    private readonly int _timeoutMs;

    internal HeartbeatAckWatch() : this(TimeoutMs) { }
    internal HeartbeatAckWatch(int timeoutMs)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
        _timeoutMs = timeoutMs;
    }

    internal void Begin(int tick)
    {
        lock (_gate) { _pending = true; _sentAt = tick; }
    }
    internal void Acknowledge()
    {
        lock (_gate)
        {
            // 迟到的心跳回包也证明连接又有响应，清除连续失败。
            _pending = false;
            _missed = 0;
        }
    }
    internal bool ShouldReconnect(int tick)
    {
        lock (_gate)
        {
            if (_pending && unchecked(tick - _sentAt) >= _timeoutMs)
            {
                _pending = false;
                _missed++;
            }
            return _missed >= 2;
        }
    }
}

internal static class PlayerFollowPolicy
{
    internal const int WindowGraceMs = 30000;
    internal static bool ShouldExit(bool? processAlive, int missingMs)
    {
        // 权限不足/查询失败 = 未知，不作为退出证据。
        return processAlive == false || missingMs >= WindowGraceMs;
    }
}
