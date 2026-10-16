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
    private const string DouyuDid = "10000000000000000000000000001501";

    // 照着 streamlink 的 douyu 插件实现：getEncryption 拿签名参数 → getH5PlayV1 拿流地址
    // 斗鱼 multirates → 「选中的档位 rate」+「该档标称 kbps」。
    // 抽成纯函数是为了能离线回归：2026-09-20 就是在这段栽过一次 ——
    // ① `bit` 的单位是 **kbps**（实测：原画1080P60=8456、蓝光4M=4000、超清=2000，
    //    「蓝光4M」这名字和 4000 对得上），曾误当成 bits/s 又除了 1000，标题里出现过 `4kbps`；
    // ② rate=0 是**原画**那一档，旧过滤条件 `rate > 0` 把它整个排除，于是原画路径永远拿不到码率；
    // ③ 原画**不能**混进参与排序的列表 —— 那个列表是拿来挑「高清/流畅」档位的（取中间/最小），
    //    多一个元素会把「高清优先」从 2000(超清) 变成 4000(蓝光4M)，等于悄悄改错档位。
    // 返回 0 表示「不用再发第二次请求」（原画，或没有可用档位）。
    private static int PickDouyuRate(object multirates, string quality, out int kbps)
    {
        kbps = 0;
        string wanted = NormalizeQuality(quality);
        var rates = new List<KeyValuePair<int, int>>();   // (bit, rate)，只含 rate>0 的转码档
        int originalBit = 0;
        foreach (object item in AsArray(multirates) ?? new object[0])
        {
            var entry = item as Dictionary<string, object>;
            if (entry == null) continue;
            int rate, bit;
            if (!int.TryParse(Field(entry, "rate"), out rate)
                || !int.TryParse(Field(entry, "bit"), out bit) || bit <= 0) continue;
            if (rate == 0) { originalBit = bit; continue; }
            rates.Add(new KeyValuePair<int, int>(bit, rate));
        }
        rates.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return a.Key.CompareTo(b.Key); });
        if (wanted == "best")
        {
            kbps = originalBit;   // 只在响应里真列了 rate=0 时才非 0；没列就不猜
            return 0;
        }
        if (rates.Count == 0) return 0;
        int index = wanted == "smooth" ? 0 : rates.Count / 2;
        kbps = rates[index].Key;
        return rates[index].Value;
    }

    private static string ResolveDouyuUrl(string room, string quality = "best")
    {
        int kbps;
        return ResolveDouyuUrl(room, quality, out kbps);
    }

    // kbps：这一档的码率，来自接口响应里的 multirates[].bit（bits/s）。
    // 斗鱼 FLV 的 onMetaData 不带 videodatarate，但接口白送这个字段 ——
    // 于是标题与 --streaminfo 不用为了码率再去读一遍流。
    private static string ResolveDouyuUrl(string room, string quality, out int kbps)
    {
        kbps = 0;
        string observed = null;
        try
        {
            HttpWebRequest encryption = (HttpWebRequest)WebRequest.Create(
                "https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=" + DouyuDid);
            encryption.Timeout = 6000;
            encryption.ReadWriteTimeout = 6000;
            encryption.Proxy = null;
            encryption.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            string encryptionJson;
            int ts = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            using (HttpWebResponse response = (HttpWebResponse)encryption.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                encryptionJson = reader.ReadToEnd();
                try
                {
                    ts = (int)(DateTime.Parse(response.Headers["Date"]).ToUniversalTime()
                        - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                }
                catch (Exception) { }
            }
            observed = encryptionJson;
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(encryptionJson);
            object errorValue, dataValue;
            if (root == null || !root.TryGetValue("error", out errorValue) || Convert.ToInt32(errorValue) != 0) return ParserFailure("douyu", observed);
            var data = root.TryGetValue("data", out dataValue) ? dataValue as Dictionary<string, object> : null;
            if (data == null) return ParserFailure("douyu", observed);
            string key = Field(data, "key");
            string randStr = Field(data, "rand_str");
            string encData = Field(data, "enc_data");
            if (key == null || randStr == null || encData == null) return ParserFailure("douyu", observed);
            int encTime = Convert.ToInt32(data["enc_time"]);
            int isSpecial = Convert.ToInt32(data["is_special"]);
            string auth = DouyuAuth(room, ts, key, randStr, encTime, isSpecial);

            string body = "enc_data=" + Uri.EscapeDataString(encData)
                + "&tt=" + ts
                + "&did=" + DouyuDid
                + "&auth=" + auth
                + "&cdn=&rate=0&hevc=0&fa=0&ive=0";
            HttpWebRequest play = (HttpWebRequest)WebRequest.Create("https://www.douyu.com/lapi/live/getH5PlayV1/" + room);
            play.Method = "POST";
            play.ContentType = "application/x-www-form-urlencoded";
            play.Timeout = 6000;
            play.ReadWriteTimeout = 6000;
            play.Proxy = null;
            play.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            byte[] payload = Encoding.UTF8.GetBytes(body);
            play.ContentLength = payload.Length;
            using (Stream stream = play.GetRequestStream()) stream.Write(payload, 0, payload.Length);
            string playJson;
            using (HttpWebResponse response = (HttpWebResponse)play.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                playJson = reader.ReadToEnd();
            observed = playJson;
            var playRoot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(playJson);
            if (playRoot == null || !playRoot.TryGetValue("error", out errorValue) || Convert.ToInt32(errorValue) != 0) return ParserFailure("douyu", observed);
            var playData = playRoot.TryGetValue("data", out dataValue) ? dataValue as Dictionary<string, object> : null;
            if (playData == null) return ParserFailure("douyu", observed);
            // 2026-09-20：multirates 以前只在选「高清/流畅」时才解析，于是「原画」路径拿不到码率，
            // 标题与 --streaminfo 只能显示缺字段的 0。现在顺带读出来。
            // ⚠️ 注意两个坑：① rate=0 是**原画**那一档，原来的过滤条件 `rate > 0` 把它整个排除了；
            //    ② 原画不能混进 rates 列表 —— 那个列表是拿来挑「高清/流畅」档位的（取中间/最小），
            //    多一个元素会把「高清优先」从 2000 变成 4000（改错档位）。所以原画单独用 originalBit 记。
            object ratesValue;
            playData.TryGetValue("multirates", out ratesValue);
            string wanted = NormalizeQuality(quality);
            int selectedRate = PickDouyuRate(ratesValue, wanted, out kbps);
            if (wanted != "best" && selectedRate > 0)
            {
                int selected = selectedRate;
                byte[] qualityPayload = Encoding.UTF8.GetBytes(body.Replace("&rate=0&", "&rate=" + selected + "&"));
                HttpWebRequest qualityRequest = (HttpWebRequest)WebRequest.Create("https://www.douyu.com/lapi/live/getH5PlayV1/" + room);
                qualityRequest.Method = "POST";
                qualityRequest.ContentType = "application/x-www-form-urlencoded";
                qualityRequest.Timeout = 6000;
                qualityRequest.ReadWriteTimeout = 6000;
                qualityRequest.Proxy = null;
                qualityRequest.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                qualityRequest.ContentLength = qualityPayload.Length;
                using (Stream stream = qualityRequest.GetRequestStream()) stream.Write(qualityPayload, 0, qualityPayload.Length);
                using (HttpWebResponse response = (HttpWebResponse)qualityRequest.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var selectedRoot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    object selectedData;
                    if (selectedRoot != null && selectedRoot.TryGetValue("data", out selectedData)
                        && selectedData is Dictionary<string, object>) playData = (Dictionary<string, object>)selectedData;
                }
            }
            string rtmpUrl = Field(playData, "rtmp_url");
            string rtmpLive = Field(playData, "rtmp_live");
            if (rtmpUrl == null || rtmpLive == null) return ParserFailure("douyu", observed);
            string result = rtmpUrl + "/" + rtmpLive;
            return IsSafeRemoteHttpUrl(result) ? result : null;
        }
        catch (Exception ex)
        {
            PlatformHealth.Record("douyu", "playback", PlatformHealth.Classify(ex));
            Diagnostics.Report("斗鱼取流", ex);
            return null;
        }
    }

    // JavaScriptSerializer 里 JSON 数组不一定是 object[]，统一转一下
    private static object[] AsArray(object value)
    {
        object[] array = value as object[];
        if (array != null) return array;
        if (value == null || value is string) return null;
        var enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null) return null;
        var result = new List<object>();
        foreach (object item in enumerable) result.Add(item);
        return result.ToArray();
    }

    private static string Field(Dictionary<string, object> data, string name)
    {
        object value;
        if (data != null && data.TryGetValue(name, out value) && value != null) return value.ToString();
        return null;
    }

    private static string DouyuAuth(string rid, int ts, string key, string randStr, int encTime, int isSpecial)
    {
        string suffix = isSpecial == 1 ? "" : rid + ts;
        string f = randStr;
        for (int i = 0; i < encTime; i++) f = Md5Hex(f + key);
        return Md5Hex(f + key + suffix);
    }

    private static string Md5Hex(string text)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            StringBuilder builder = new StringBuilder();
            foreach (byte b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }



}
