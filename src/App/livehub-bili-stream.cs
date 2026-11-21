using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    // Bilibili's room_init and getRoomPlayInfo endpoints, also used by jubo-live-rust.
    private static Dictionary<string, object> BiliPlaybackJson(string endpoint, List<CookieRecord> cookies)
    {
        var uri = new Uri("https://api.live.bilibili.com/" + endpoint);
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.AllowAutoRedirect = false;
        request.Proxy = null;
        request.Timeout = request.ReadWriteTimeout = 5000;
        request.UserAgent = "Mozilla/5.0";
        request.Referer = "https://live.bilibili.com/";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        var header = CredentialCookies.HeaderForUri(cookies, uri);
        if (!string.IsNullOrEmpty(header)) request.Headers["Cookie"] = header;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            if (response.StatusCode != HttpStatusCode.OK) return null;
            var json = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (json.Length + count > 1024 * 1024) return null;
                json.Append(buffer, 0, count);
            }
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json.ToString());
        }
    }

    private static IEnumerable<Dictionary<string, object>> BiliRows(Dictionary<string, object> parent, string key)
    {
        object raw;
        if (parent == null || !parent.TryGetValue(key, out raw)) yield break;
        foreach (object value in AsArray(raw) ?? new object[0])
        {
            var item = value as Dictionary<string, object>;
            if (item != null) yield return item;
        }
    }

    internal static string BiliPlaybackUrl(Dictionary<string, object> root, bool flvOnly)
    {
        if (Field(root, "code") != "0") return null;
        var play = NestedDict(NestedDict(NestedDict(root, "data"), "playurl_info"), "playurl");
        foreach (string preferred in flvOnly ? new[] { "flv" } : new[] { "flv", "fmp4", "ts" })
        foreach (var stream in BiliRows(play, "stream"))
        foreach (var format in BiliRows(stream, "format"))
        {
            if (Field(format, "format_name") != preferred) continue;
            foreach (var codec in BiliRows(format, "codec"))
            {
                string path = Field(codec, "base_url");
                if (Field(codec, "codec_name") != "avc" || string.IsNullOrEmpty(path) || !path.StartsWith("/")) continue;
                foreach (var route in BiliRows(codec, "url_info"))
                {
                    Uri host;
                    if (!Uri.TryCreate(Field(route, "host"), UriKind.Absolute, out host) || host.AbsolutePath != "/") continue;
                    string url = host.GetLeftPart(UriPartial.Authority) + path + Field(route, "extra");
                    if (IsSafeRemoteHttpUrl(url)) return url;
                }
            }
        }
        return null;
    }

    internal static string TryBiliPlaybackUrl(string room, string quality, string cookieFile, bool flvOnly = false)
    {
        if (!Regex.IsMatch(room ?? "", @"\A[0-9]{1,20}\z")) return null;
        try
        {
            var cookies = CredentialCookies.FilterByPlatform(CredentialCookies.Load(cookieFile), "bili");
            var init = BiliPlaybackJson("room/v1/Room/room_init?id=" + room, cookies);
            var data = NestedDict(init, "data");
            string canonical = Field(data, "room_id"), state = Field(data, "live_status");
            if (Field(init, "code") != "0" || (state != "1" && state != "2") || !Regex.IsMatch(canonical ?? "", @"\A[0-9]{1,20}\z")) return null;
            string qn = quality == "smooth" ? "250" : quality == "high" ? "400" : "10000";
            return BiliPlaybackUrl(BiliPlaybackJson("xlive/web-room/v2/index/getRoomPlayInfo?room_id=" + canonical
                + "&protocol=0,1&format=0,1,2&codec=0&platform=web&ptype=8&qn=" + qn, cookies), flvOnly);
        }
        catch (WebException) { return null; }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
