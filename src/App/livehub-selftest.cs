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
    // 不弹窗、不播放，只校验解析与三端启动计划（退出码 0 = 通过）。
    private static bool SelfTest()
    {
        bool ok = Parse("7777", "bili").Url == "https://live.bilibili.com/7777"
            && Parse("https://www.huya.com/660000", "bili").Url == "https://www.huya.com/660000"
            && Parse("https://www.douyu.com/952595", "bili").Url == "https://www.douyu.com/952595"
            && Parse("https://evil.example/7777", "bili") == null
            && Parse("x&calc", "huya") == null
            && Parse("douyu:952595", "bili").Platform == "douyu"
            && Parse("douyu:952595", "bili").Room == "952595"
            && Parse("https://live.douyin.com/723910165684", "bili").Platform == "douyin"
            && Parse("https://live.douyin.com/723910165684", "bili").Url == "https://live.douyin.com/723910165684"
            && Parse("douyin:723910165684", "bili").Room == "723910165684"
            && Parse("https://live.douyin.com/abc", "bili") == null
            && Parse("https://evil.example/douyin", "douyin") == null;
        if (!ok) return false;
        try
        {
            ProcessStartInfo bili = BuildStart(Parse("7777", "bili"), "mpv");
            ProcessStartInfo huya = BuildStart(Parse("https://www.huya.com/660000", "bili"), "mpv");
            ProcessStartInfo douyu = BuildStart(Parse("https://www.douyu.com/952595", "bili"), "mpv");
            ProcessStartInfo douyin = BuildStart(Parse("https://live.douyin.com/723910165684", "bili"), "mpv");
            ProcessStartInfo pot = BuildStart(Parse("https://www.douyu.com/952595", "bili"), "potplayer");
            WriteSelfTestReport(bili, huya, douyu, douyin, pot);
            return Path.GetFileName(bili.FileName) == "mpv.exe"
                && Path.GetFileName(huya.FileName) == "mpv.exe"
                && Path.GetFileName(douyu.FileName) == "mpv.exe"
                && Path.GetFileName(pot.FileName) == "PotPlayerMini64.exe"
                && pot.Arguments.Contains("/new")
                && bili.Arguments.Contains("live-title.lua")
                && bili.Arguments.Contains("--load-scripts=no")
                && huya.Arguments.Contains("live-title.lua")
                && douyu.Arguments.Contains("live-title.lua")
                && douyu.Arguments.Contains("live_title_bitrate_file=")
                && douyu.Arguments.Contains("127.0.0.1")
                && Path.GetFileName(douyin.FileName) == "mpv.exe"
                && douyin.Arguments.Contains("live-title.lua")
                && douyin.Arguments.Contains("--ytdl=no")
                && douyin.Arguments.Contains("live_title_bitrate_file=")
                && douyin.Arguments.Contains("127.0.0.1")
                && huya.Arguments.Contains("127.0.0.1")
                && huya.Arguments.Contains("live_title_bitrate_file=")
                && huya.Arguments.Contains("--ytdl=no");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(RuntimeDir, "selftest.txt"), "EXCEPTION " + ex); } catch (Exception) { }
            return false;
        }
    }

    private static void WriteSelfTestReport(ProcessStartInfo bili, ProcessStartInfo huya, ProcessStartInfo douyu, ProcessStartInfo douyin, ProcessStartInfo pot)
    {
        {
            var report = new List<string>();
            report.Add("BUILD=" + BuildInfo.Text);
            report.Add("BILI  = " + bili.FileName + " || " + bili.Arguments);
            report.Add("HUYA  = " + huya.FileName + " || " + huya.Arguments);
            report.Add("DOUYU = " + douyu.FileName + " || " + douyu.Arguments);
            report.Add("DOUYIN = " + douyin.FileName + " || " + douyin.Arguments);
            report.Add("POT   = " + pot.FileName + " || " + pot.Arguments);
            report.Add("YTDLP-EXISTS=" + File.Exists(YtDlp) + "  LUA-EXISTS=" + File.Exists(LiveTitleScript) + "  PORTABLE=" + Portable
            + "  VARIANT=" + (PortableLite ? "lite" : "full") + "  MPV-BUNDLED=" + File.Exists(Mpv) + "  POTPLAYER-BUNDLED=" + File.Exists(PotPlayer));
            // 路径解析来源（2026-09-27，评估报告 §8）：这台机器到底用了哪个程序、从哪找到的。
            // 「改了配置没生效」这类问题从此可以直接看这份报告，不用猜。
            report.AddRange(DescribeResolvedPaths());
            File.WriteAllLines(Path.Combine(RuntimeDir, "selftest.txt"), report, new UTF8Encoding(false));
        }
    }
}
