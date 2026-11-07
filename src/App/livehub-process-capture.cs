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
    private static string FirstUrl(string text)
    {
        if (text == null) return null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("http://") || line.StartsWith("https://")) return line;
        }
        return null;
    }

    // 现有调用方（取 yt-dlp/Streamlink 输出）语义保持不变：不限输出、只按超时结束、不检查退出码。
    private static string RunCapture(string exe, string args, int timeoutMs)
    {
        int exitCode;
        bool truncated;
        return RunCaptureBounded(exe, args, timeoutMs, 0, out exitCode, out truncated);
    }

    // 有界捕获：与 RunCapture 同一套「并发排空 stdout/stderr + 超时自杀」逻辑，额外提供
    // 输出上限（字符数，0 = 不限）与退出码。探测外部程序时用它，防止任意程序把启动器内存撑爆。
    private static string RunCaptureBounded(string exe, string args, int timeoutMs, int maxChars, out int exitCode, out bool truncated)
    {
        exitCode = -1;
        truncated = false;
        ProcessStartInfo psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        using (Process p = new Process())
        {
            p.StartInfo = psi;
            var stdout = new StringBuilder();
            bool overflow = false;
            p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (stdout)
                {
                    // 超限后继续排空管道（不然子进程会卡在写满的管道上），但不再占用内存。
                    if (maxChars > 0 && stdout.Length + e.Data.Length + 1 > maxChars) { overflow = true; return; }
                    stdout.AppendLine(e.Data);
                }
            };
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { }; // 必须并发排空 stderr
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch (Exception) { }
                try { p.WaitForExit(5000); } catch (Exception) { }
                throw new InvalidOperationException("解析超时：" + Path.GetFileName(exe));
            }
            p.WaitForExit(); // 等待异步输出事件处理完毕
            exitCode = p.ExitCode;
            string output;
            lock (stdout) { truncated = overflow; output = stdout.ToString(); }
            return output;
        }
    }

    // B站：查一下主播名（公开接口、匿名、不读 Cookie），用作「主播 - 标题」的前缀。
}
