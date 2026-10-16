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
    private static string ResolveHuyaUrl(Target target)
    {
        string failureReason;
        return ResolveHuyaUrl(target, out failureReason);
    }

    private static string ResolveHuyaUrl(Target target, out string failureReason)
    {
        failureReason = null;
        string pageFailure;
        List<string> candidates = BuildHuyaUrlsFromPage(target.Room, target.Quality, out pageFailure);
        var failures = new List<string>();
        foreach (string candidate in candidates)
        {
            string probeFailure;
            if (UrlResponds(candidate, out probeFailure)) return candidate;
            Uri uri;
            string host = Uri.TryCreate(candidate, UriKind.Absolute, out uri) ? uri.Host : "未知线路";
            failures.Add(host + " " + (probeFailure ?? "没有响应"));
        }
        // 兜底：退回 yt-dlp（至少能拿到 200 的开头）
        if (File.Exists(YtDlp))
        {
            // 页面已经明确没有任何流条目时，四个格式表达式只会重复抓同一空页面；一次 best 足以兜底。
            // 页面有候选但三线探测失败时才保留旧的 14/3/5/best 顺序，避免丢掉某条可用 CDN。
            string[] formats = candidates.Count == 0 ? new string[] { "best" }
                : new string[] { "best[format_id^=14-]", "best[format_id^=3-]", "best[format_id^=5-]", "best" };
            foreach (string format in formats)
            {
                string candidate = FirstUrl(RunCapture(YtDlp, "-g --no-warnings -f \"" + format + "\" \"" + target.Url + "\"", 30000));
                if (!IsSafeRemoteHttpUrl(candidate)) continue;
                string probeFailure;
                if (UrlResponds(candidate, out probeFailure)) return candidate;
                failures.Add("yt-dlp " + (probeFailure ?? "没有响应"));
            }
        }
        failureReason = failures.Count > 0 ? string.Join("；", failures.ToArray())
            : pageFailure ?? "页面没有可用的 FLV 直链";
        return null;
    }

    private static string BuildHuyaUrlFromPage(string room, string quality = "best")
    {
        string failureReason;
        List<string> urls = BuildHuyaUrlsFromPage(room, quality, out failureReason);
        return urls.Count == 0 ? null : urls[0];
    }

    private static List<string> BuildHuyaUrlsFromPage(string room, string quality, out string failureReason)
    {
        failureReason = null;
        var results = new List<string>();
        string observed = null;
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://www.huya.com/" + room);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0";
            request.Accept = "*/*";
            // 关键：不带压缩就只有 15KB 空壳页面，拿不到 hyPlayerConfig
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            string page;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                page = reader.ReadToEnd();
            observed = page;
            int config = page.IndexOf("hyPlayerConfig");
            if (config < 0) { ParserFailure("huya", observed); failureReason = "页面没有播放器配置"; return results; }
            int streamAt = page.IndexOf("stream:", config, StringComparison.Ordinal);
            if (streamAt < 0) { ParserFailure("huya", observed); failureReason = "页面没有流配置"; return results; }
            string json = ExtractJsonObject(page, page.IndexOf('{', streamAt));
            if (json == null) { ParserFailure("huya", observed); failureReason = "页面流配置无法解析"; return results; }

            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object dataValue;
            var data = root != null && root.TryGetValue("data", out dataValue) ? AsArray(dataValue) : null;
            if (data == null || data.Length == 0) { ParserFailure("huya", observed); failureReason = "页面没有可用的 FLV 直链"; return results; }
            var first = data[0] as Dictionary<string, object>;
            if (first == null) { ParserFailure("huya", observed); failureReason = "页面流配置无效"; return results; }
            object listValue;
            var list = first.TryGetValue("gameStreamInfoList", out listValue) ? AsArray(listValue) : null;
            if (list == null || list.Length == 0) { ParserFailure("huya", observed); failureReason = "页面没有可用的 FLV 直链"; return results; }

            // 画质档：iBitRate=0 表示原画(source)，取最大的那档
            int bestRate = 0;
            var rates = new List<int>();
            object multiValue;
            var multi = first.TryGetValue("vMultiStreamInfo", out multiValue) ? AsArray(multiValue) : null;
            if (multi != null)
                foreach (object item in multi)
                {
                    var info = item as Dictionary<string, object>;
                    if (info == null) continue;
                    object rateValue;
                    if (info.TryGetValue("iBitRate", out rateValue))
                    {
                        int rate = Convert.ToInt32(rateValue);
                        if (rate > bestRate) bestRate = rate;
                        if (rate > 0 && !rates.Contains(rate)) rates.Add(rate);
                    }
                }
            rates.Sort();
            if (rates.Count > 0 && NormalizeQuality(quality) == "smooth") bestRate = rates[0];
            else if (rates.Count > 1 && NormalizeQuality(quality) == "high") bestRate = rates[rates.Count / 2];

            // CDN 选择：按实际地址判断（页面里的 sCdnType 不叫 HS，别按名字过滤），
            // 实测只有 hs.flv.huya.com 稳定（al 302 / tx 403），所以带 hs. 的排最前。
            var entries = new List<Dictionary<string, object>>();
            foreach (object item in list)
            {
                var stream = item as Dictionary<string, object>;
                if (stream != null) entries.Add(stream);
            }
            var ordered = new List<Dictionary<string, object>>();
            foreach (var stream in entries)
            {
                string host = Field(stream, "sFlvUrl") ?? "";
                if (host.IndexOf("hs.", StringComparison.OrdinalIgnoreCase) >= 0) ordered.Add(stream);
            }
            foreach (var stream in entries)
            {
                string host = Field(stream, "sFlvUrl") ?? "";
                if (host.IndexOf("hs.", StringComparison.OrdinalIgnoreCase) < 0) ordered.Add(stream);
            }
            foreach (var stream in ordered)
            {
                string url = BuildHuyaStreamUrl(stream, bestRate);
                if (url != null) results.Add(url);
            }
            if (results.Count == 0)
            {
                ParserFailure("huya", observed);
                failureReason = "页面流签名无效";
            }
            return results;
        }
        catch (Exception ex)
        {
            PlatformHealth.Record("huya", "playback", PlatformHealth.Classify(ex));
            Diagnostics.Report("虎牙取流", ex);
            failureReason = ex is WebException && ((WebException)ex).Status == WebExceptionStatus.Timeout
                ? "房间页请求超时" : "房间页请求失败：" + ex.GetType().Name;
            return results;
        }
    }

    // 从 start（指向 '{'）开始做大括号配对，返回完整的 JSON 对象文本（跳过字符串里的括号）
    private static string ExtractJsonObject(string text, int start)
    {
        if (start < 0 || start >= text.Length || text[start] != '{') return null;
        int depth = 0;
        bool inString = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    // 诊断用：列出页面配置里有哪些 CDN
    private static string ListHuyaCdns(string room)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://www.huya.com/" + room);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Proxy = null;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0";
            request.Accept = "*/*";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            string page;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                page = reader.ReadToEnd();
            int config = page.IndexOf("hyPlayerConfig");
            if (config < 0) return "(页面里没有 hyPlayerConfig)";
            int streamAt = page.IndexOf("stream:", config, StringComparison.Ordinal);
            if (streamAt < 0) return "(没有 stream:)";
            string json = ExtractJsonObject(page, page.IndexOf('{', streamAt));
            if (json == null) return "(大括号配对失败)";
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object dataValue;
            var data = root != null && root.TryGetValue("data", out dataValue) ? AsArray(dataValue) : null;
            if (data == null || data.Length == 0) return "(没有 data)";
            var first = data[0] as Dictionary<string, object>;
            object listValue;
            var list = first != null && first.TryGetValue("gameStreamInfoList", out listValue) ? AsArray(listValue) : null;
            if (list == null) return "(没有 gameStreamInfoList)";
            var parts = new List<string>();
            foreach (object item in list)
            {
                var stream = item as Dictionary<string, object>;
                if (stream == null) continue;
                parts.Add(Field(stream, "sCdnType") + "@" + Field(stream, "sFlvUrl"));
            }
            return "共 " + list.Length + " 条: " + string.Join(" | ", parts.ToArray());
        }
        catch (Exception ex)
        {
            return "(异常 " + ex.GetType().Name + ": " + ex.Message + ")";
        }
    }

    private static string BuildHuyaStreamUrl(Dictionary<string, object> stream, int bitRate)
    {
        string flvUrl = Field(stream, "sFlvUrl");
        string streamName = Field(stream, "sStreamName");
        string suffix = Field(stream, "sFlvUrlSuffix");
        string antiCode = Field(stream, "sFlvAntiCode");
        if (flvUrl == null || streamName == null || suffix == null || antiCode == null) return null;

        var query = new Dictionary<string, string>();
        foreach (string pair in antiCode.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        string wsTime, fm, ctype, fs;
        if (!query.TryGetValue("wsTime", out wsTime) || !query.TryGetValue("fm", out fm)) return null;
        if (!query.TryGetValue("ctype", out ctype)) ctype = "huya_live";
        if (!query.TryGetValue("fs", out fs)) fs = "";

        // 以下是 streamlink huya 插件的算法（uid/seqid 都是随机+时间戳，每次请求重新算）
        Random random = new Random();
        long uid = random.Next(12340000, 12349999);
        long convertUid = ((uid << 8) | (uid >> 24)) & 0xFFFFFFFFL;
        long timestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        long seqid = uid + timestamp;
        string prefix;
        try
        {
            prefix = Encoding.UTF8.GetString(Convert.FromBase64String(fm)).Split('_')[0];
        }
        catch (Exception)
        {
            return null;
        }
        string wsSecretHash = Md5Hex(seqid + "|" + ctype + "|100");
        string wsSecret = Md5Hex(prefix + "_" + convertUid + "_" + streamName + "_" + wsSecretHash + "_" + wsTime);

        var final = new List<string>();
        final.Add("wsSecret=" + wsSecret);
        final.Add("wsTime=" + wsTime);
        final.Add("ctype=" + ctype);
        final.Add("fs=" + fs);
        final.Add("seqid=" + seqid);
        final.Add("u=" + convertUid);
        final.Add("sdk_sid=" + timestamp);
        final.Add("ratio=" + bitRate);
        final.Add("t=100");
        final.Add("ver=1");
        final.Add("sv=2401090219");
        final.Add("codec=264");
        string baseUrl = flvUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + flvUrl.Substring(7) : flvUrl;
        string result = baseUrl + "/" + streamName + "." + suffix + "?" + string.Join("&", final.ToArray());
        return IsSafeRemoteHttpUrl(result) ? result : null;
    }

    private static bool UrlResponds(string url)
    {
        string failureReason;
        return UrlResponds(url, out failureReason);
    }

    private static bool UrlResponds(string url, out string failureReason)
    {
        failureReason = null;
        try
        {
            string current = url;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                if (!IsSafeRemoteHttpUrl(current)) { failureReason = "地址未通过安全校验"; return false; }
                // 连接目标钉在刚校验过的那批公网地址上（TOCTOU：校验用哪个地址就连哪个地址）
                using (HttpWebResponse response = OpenRemoteResponse(current, 8000,
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:154.0) Gecko/20100101 Firefox/154.0",
                    "https://www.huya.com/",
                    delegate(HttpWebRequest request)
                    {
                        request.Proxy = null;
                        request.Headers["Origin"] = "https://www.huya.com";
                    }, false))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        string location = response.Headers["Location"];
                        Uri next;
                        if (location == null || !Uri.TryCreate(new Uri(current), location, out next))
                        { failureReason = "HTTP " + status + " 的跳转地址无效"; return false; }
                        current = next.AbsoluteUri;
                        continue;
                    }
                    using (Stream stream = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[1024];
                        stream.Read(buffer, 0, buffer.Length);
                    }
                    // 2xx 都算「有响应」：原来只认 200，CDN 对无 Range 探测回 204/206 时会被误杀。
                    if (status >= 200 && status < 300) return true;
                    failureReason = "HTTP " + status;
                    return false;
                }
            }
            failureReason = "重定向次数过多";
            return false;
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                int status = (int)response.StatusCode;
                if (status == 200) return true;
                failureReason = "HTTP " + status;
            }
            else failureReason = ex.Status == WebExceptionStatus.Timeout ? "请求超时" : "网络错误：" + ex.Status;
            return false;
        }
        catch (Exception ex)
        {
            failureReason = "请求失败：" + ex.GetType().Name;
            return false;
        }
    }

}
