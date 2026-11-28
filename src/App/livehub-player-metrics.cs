using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

internal static partial class LiveHub
{
    private static readonly CultureInfo MetricCulture = CultureInfo.InvariantCulture;
    private const string PotUnavailable = "A-V —  ·  丢帧 解码 — / 输出 —  ·  缓存 —（PotPlayer 未提供）";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, UIntPtr command, IntPtr arg, uint flags, uint timeout, out UIntPtr result);

    private delegate bool PotWindowVisitor(IntPtr window, IntPtr context);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    private static extern bool EnumPotWindows(PotWindowVisitor visit, IntPtr context);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint PotWindowOwner(IntPtr window, out uint processId);

    private static bool IsPotPlayerWindow(IntPtr window, int pid)
    {
        if (window == IntPtr.Zero) return false;
        uint owner;
        PotWindowOwner(window, out owner);
        if (owner != (uint)pid) return false;
        var name = new StringBuilder(64);
        if (GetClassNameW(window, name, name.Capacity) == 0) return false;
        string cls = name.ToString();
        return cls == "PotPlayer64" || cls == "PotPlayer";
    }

    private static IntPtr FindPotPlayerWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumPotWindows(delegate(IntPtr window, IntPtr context)
        {
            if (!IsPotPlayerWindow(window, pid)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string PotPlaybackStatus(PlaybackSession session)
    {
        // Query the owned process only, with a bounded wait if its UI is busy.
        try
        {
            if (session.Process == null || session.Process.HasExited) return "状态不可用";
            IntPtr window = FindPotPlayerWindow(session.PlayerProcessId);
            if (window == IntPtr.Zero) return "状态不可用";
            UIntPtr status, pos;
            if (SendMessageTimeoutW(window, 0x400, (UIntPtr)0x5006, IntPtr.Zero, 2, 25, out status) == IntPtr.Zero)
                return "状态不可用";
            string state = status.ToUInt64() == 2 ? "播放中" : status.ToUInt64() == 1 ? "暂停" : "停止或无媒体";
            if ((status.ToUInt64() == 1 || status.ToUInt64() == 2)
                && SendMessageTimeoutW(window, 0x400, (UIntPtr)0x5004, IntPtr.Zero, 2, 25, out pos) != IntPtr.Zero
                && pos.ToUInt64() <= int.MaxValue)
                state += " · 播放位置 " + (pos.ToUInt64() / 1000.0).ToString("0.0", MetricCulture) + " s";
            return state;
        }
        catch (InvalidOperationException) { return "状态不可用"; }
        catch (System.ComponentModel.Win32Exception) { return "状态不可用"; }
    }

    private static string[] ReadMetricSlot(string path, int pid, long now, out long timestamp)
    {
        timestamp = 0;
        if (!File.Exists(path)) return null;
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] data = new byte[256];
                int length = file.Read(data, 0, data.Length);
                if (length == 0 || length == data.Length || data[length-1] != 10) return null;
                string[] fields = Encoding.ASCII.GetString(data, 0, length-1).TrimEnd('\r').Split(' ');
                int sourcePid;
                if (fields.Length != 7 || fields[0] != "v1" || !int.TryParse(fields[2], NumberStyles.None, MetricCulture, out sourcePid)
                    || sourcePid != pid || !long.TryParse(fields[1], NumberStyles.None, MetricCulture, out timestamp)
                    || timestamp > now || timestamp < now - 4) return null;
                return fields;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string MetricNumber(string value, bool signed)
    {
        double number;
        if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, MetricCulture, out number)
            || double.IsNaN(number) || double.IsInfinity(number) || (!signed && number < 0) || Math.Abs(number) > 1e10) return "—";
        return number.ToString(signed ? "+0.00;-0.00;0.00" : "0.00", MetricCulture);
    }

    private static string MetricCount(string value)
    {
        long count;
        return long.TryParse(value, NumberStyles.None, MetricCulture, out count) ? count.ToString(MetricCulture) : "—";
    }

    internal static string ReadMpvMetrics(string statePath, int pid, long now)
    {
        long recent, prior;
        string first = statePath + ".metrics";
        var a = ReadMetricSlot(first + "0", pid, now, out recent);
        var b = ReadMetricSlot(first + "1", pid, now, out prior);
        var fields = a != null && (b == null || recent >= prior) ? a : b;
        if (fields == null) return "mpv 暂无新采样";
        long stamp = ReferenceEquals(fields, a) ? recent : prior;
        string time = DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime.ToString("HH:mm:ss", MetricCulture);
        return "A-V " + MetricNumber(fields[3], true) + " ms  ·  丢帧 解码 " + MetricCount(fields[4])
            + " / 输出 " + MetricCount(fields[5]) + "  ·  缓存 " + MetricNumber(fields[6], false) + " s  ·  采样 " + time;
    }

    private static string PlayerMetricsSummary(PlaybackSession session, bool fresh = false)
    {
        if (session.Ended) return "会话已结束";
        if (session.Player == "potplayer")
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (fresh || session.PotStatus == null || now - session.PotStatusAt >= System.Diagnostics.Stopwatch.Frequency)
            {
                session.PotStatus = PotPlaybackStatus(session);
                session.PotStatusAt = now;
            }
            return "PotPlayer " + session.PotStatus + "  ·  " + PotUnavailable;
        }
        if (session.PlayerProcessId <= 0 || session.Target == null || string.IsNullOrEmpty(session.Target.SessionId)) return "mpv 等待首个采样";
        return ReadMpvMetrics(SessionRoomStatePath(session.Target), session.PlayerProcessId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
