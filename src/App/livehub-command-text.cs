using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 从主 partial 原样搬运；仍在同一 net48 编译单元。
internal static partial class LiveHub
{
    private static string YtdlHookOpts()
    {
        return File.Exists(YtDlp) ? "ytdl_hook-ytdl_path=" + MpvListValue(YtDlp) : "ytdl_hook-verbose=no";
    }

    // mpv 的键值列表按 UTF-8 字节长度引用值，避免路径中的逗号成为分隔符。
    private static string MpvListValue(string value)
    {
        value = value.Replace('\\', '/');
        return "%" + Encoding.UTF8.GetByteCount(value) + "%" + value;
    }

    private static string JoinTitle(string first, string second, string fallback)
    {
        if (first != null && first.Length > 0 && second != null && second.Length > 0) return first + " - " + second;
        if (first != null && first.Length > 0) return first;
        if (second != null && second.Length > 0) return second;
        return fallback;
    }

    // 平台返回的标题不是可信命令行。先去掉控制字符，再按 Windows argv 规则引用为单个参数。
    private static string SafeTitle(string value)
    {
        string title = Regex.Replace(value ?? "", @"[\x00-\x1F\x7F]", " ").Trim();
        return title.Length > 240 ? title.Substring(0, 240) : title;
    }

    private static string QuoteWindowsArg(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value ?? "")
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                result.Append('\\', slashes * 2 + 1);
                result.Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes);
            slashes = 0;
            result.Append(c);
        }
        result.Append('\\', slashes * 2);
        result.Append('"');
        return result.ToString();
    }

    internal static ProcessStartInfo CreateLauncherRestartInfo(string[] commandLineArgs)
    {
        var arguments = new StringBuilder();
        // GetCommandLineArgs includes the executable at index zero. Keep the
        // original arguments before ApplyPathOverrides removes path options.
        for (int i = 1; i < commandLineArgs.Length; i++)
        {
            if (i > 1) arguments.Append(' ');
            arguments.Append(QuoteWindowsArg(commandLineArgs[i]));
        }
        return new ProcessStartInfo(Application.ExecutablePath, arguments.ToString())
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = true
        };
    }

    // 虎牙的主播/房间名：网页端已经不在 HTML 里给配置了，改用手機端公开接口
    // （https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid=... → nick + introduction）
}
