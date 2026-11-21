using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

internal static partial class LiveHub
{
    private const string YtdlpVersion = "2026.10.26";
    private const long YtdlpSize = 23575744;
    private const string YtdlpSha = "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d";

    private static void ProbeSelectedTool(string tool, string path)
    {
        if (tool == "mpv") { RequireMpvOpenAl(path); return; }
        if (tool == "potplayer")
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if ((info.ProductName ?? "").IndexOf("PotPlayer", StringComparison.OrdinalIgnoreCase) < 0 || string.IsNullOrEmpty(info.FileVersion))
                throw new InvalidDataException("未识别出 PotPlayer 的程序版本。");
            return;
        }
        int exit; bool truncated;
        string version = RunCaptureBounded(path, "--ignore-config --version", 10000, 8192, out exit, out truncated).Trim();
        if (exit != 0 || truncated || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{4}\.\d{2}\.\d{2}[\w.\-]*$"))
            throw new InvalidDataException("未识别出 yt-dlp 的程序版本。");
    }

    private static bool MatchesYtdlpRelease(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != YtdlpSize) return false;
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), YtdlpSha, StringComparison.OrdinalIgnoreCase);
    }

    internal static string DownloadYtdlp(string directory, CancellationToken cancel)
    {
        directory = Path.Combine(directory, "tools", "yt-dlp-" + YtdlpVersion);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "yt-dlp.exe");
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (!File.Exists(target))
            {
                var uri = new Uri("https://github.com/yt-dlp/yt-dlp/releases/download/" + YtdlpVersion + "/yt-dlp.exe");
                var watch = Stopwatch.StartNew();
                for (int redirects = 0; redirects <= 5; redirects++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
                        || !(uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"))
                        throw new IOException("官方地址发生了不支持的跳转。");
                    var request = (HttpWebRequest)WebRequest.Create(uri);
                    request.AllowAutoRedirect = false;
                    request.Timeout = request.ReadWriteTimeout = 15000;
                    request.UserAgent = "jubo-live";
                    using (cancel.Register(request.Abort))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                        { uri = new Uri(uri, response.Headers["Location"]); continue; }
                        if (response.StatusCode != HttpStatusCode.OK || (response.ContentLength >= 0 && response.ContentLength != YtdlpSize))
                            throw new IOException("官方下载大小与版本清单不符。");
                        using (var input = response.GetResponseStream())
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[65536];
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                if (watch.Elapsed.TotalSeconds > 180 || output.Length + count > YtdlpSize)
                                    throw new IOException("下载超出大小或时间限制。");
                                output.Write(buffer, 0, count);
                            }
                        }
                        break;
                    }
                }
                if (!MatchesYtdlpRelease(temp)) throw new IOException("yt-dlp 下载完整性校验失败。");
                cancel.ThrowIfCancellationRequested();
                try { File.Move(temp, target); }
                catch (IOException) { if (!File.Exists(target)) throw; }
            }
            if (!MatchesYtdlpRelease(target)) throw new IOException("工具目录已有不同的文件，请选择已有程序或其他路径。");
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
