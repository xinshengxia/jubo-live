// --------------------------------------------------------------------------------------------
// 2026-09-25：单文件约束解除后本文件**独立参与编译**，所以自带 using。
// （在此之前这些模块是被生成器拼进 live-hub-v2.cs 的，靠宿主文件的 using 才编得过。）
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
// --------------------------------------------------------------------------------------------
// 抖音弹幕客户端
//
// 三个关键点（都是调研阶段真机验证过的）：
//  ① **签名是纯算法，不是 JS 黑盒** —— `signature` = webmssdk 的 `frontierSign({"X-MS-STUB": md5})["X-Bogus"]`，
//     由 MD5 + RC4 + 自定义 base64 组成，约 150 行，**不需要 JS 引擎、不需要浏览器、不需要第三方服务**。
//     实测阴性对照：改 1 个字符 / 不带 / 空值 → 一律被拒（HTTP 200 而不是 101）。
//  ② **只有一层 gzip**（在 PushFrame.payload 上）。有 README 说"双层"，那是误述。
//  ③ **必须判 status==2 才算开播** —— status=4（已结束）同样能拿到 roomId，不判就会连一个死房间。
//
// 解码链路：WS 二进制帧 → PushFrame(field8=payload) → GZipStream → Response(field1=repeated Message)
//           → Message(field1=method, field2=payload) → 按 method 分派
//
// protobuf 只用到 wire type 0(varint) 和 2(length-delimited)，手写 ~60 行就够（见 ProtoReader）。
// --------------------------------------------------------------------------------------------

/// 极简 protobuf 读取器：只支持 varint(0)、64位(1)、length-delimited(2)、32位(5)。
/// 抖音这套消息只用到这些，不值得为它引一个 protobuf 库（也引不了，零 NuGet）。
internal sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _pos;

    public ProtoReader(byte[] data) { _data = data == null ? new byte[0] : data; }
    public bool AtEnd { get { return _pos >= _data.Length; } }
    public int Position { get { return _pos; } }

    public bool TryReadTag(out int field, out int wireType)
    {
        field = 0; wireType = 0;
        if (_pos >= _data.Length) return false;
        ulong tag = unchecked((ulong)ReadVarint());
        if ((tag >> 3) == 0 || (tag >> 3) > 0x1FFFFFFF) throw Invalid("非法字段号");
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        if (wireType != 0 && wireType != 1 && wireType != 2 && wireType != 5) throw Invalid("非法 wire type");
        return true;
    }

    public long ReadVarint()
    {
        ulong result = 0;
        for (int index = 0; index < 10; index++)
        {
            if (_pos >= _data.Length) throw Invalid("varint 截断");
            byte b = _data[_pos++];
            if (index == 9 && b > 1) throw Invalid("varint 溢出");
            result |= (ulong)(b & 0x7F) << (index * 7);
            if ((b & 0x80) == 0) return unchecked((long)result);
        }
        throw Invalid("varint 未终止");
    }

    public byte[] ReadBytes()
    {
        int length = ReadLength();
        byte[] result = new byte[length];
        Buffer.BlockCopy(_data, _pos, result, 0, length);
        _pos += length;
        return result;
    }

    public string ReadString()
    {
        byte[] bytes = ReadBytes();
        return Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: SkipFixed(8); break;
            case 2: { int n = ReadLength(); _pos += n; break; }
            case 5: SkipFixed(4); break;
            default: throw Invalid("非法 wire type");
        }
    }

    private int ReadLength()
    {
        long length = ReadVarint();
        if (length < 0 || length > int.MaxValue || length > _data.Length - _pos) throw Invalid("字段长度超出报文");
        return (int)length;
    }

    private void SkipFixed(int length)
    {
        if (length > _data.Length - _pos) throw Invalid("固定长度字段截断");
        _pos += length;
    }

    private InvalidDataException Invalid(string reason)
    {
        _pos = _data.Length;
        return new InvalidDataException("protobuf：" + reason);
    }

    /// 把接下来的 length-delimited 字段包成一个子 reader
    public ProtoReader ReadMessage()
    {
        return new ProtoReader(ReadBytes());
    }
}

/// 抖音 WSS 签名。算法来自 jwwsjlm/douyinLive 的 Go 实现（纯算法，无 JS）。
/// DouyinSign adaptation: Copyright (c) 2025 今晚打打; MIT License.
/// 上游与完整许可见 THIRD_PARTY_NOTICES.md、licenses/douyinLive-LICENSE.txt。
/// 两个上游公开测试向量见 HardeningTests 的 TestDouyinSign（不是平台官方 SDK）。
internal static class DouyinSign
{
    private const string Alphabet = "Dkdpgh4ZKsQB80/Mfvw36XI1R25+WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe";
    private const int WsModeFlag = 1 << 6;

    /// 测试向量用的那套参数化入口（counter / randomFlag / payloadRandom / keyRandom 全部指定）
    public static string SignWithValues(string xMsStub, int counter, bool randomFlag, int payloadRandom, int keyRandom)
    {
        byte[] stubDigest = Md5(HexToBytes(xMsStub));
        byte[] emptyBodyDigest = Md5(Md5(new byte[0]));

        byte[] payload = new byte[10];
        payload[0] = (byte)(counter & 0x3F);
        payload[1] = 0;
        payload[2] = 1;                                   // environmentCode
        payload[3] = 14;                                  // userBehaviorCode
        payload[4] = emptyBodyDigest[14];
        payload[5] = emptyBodyDigest[15];
        payload[6] = stubDigest[14];
        payload[7] = stubDigest[15];
        payload[8] = (byte)payloadRandom;
        byte checksum = 0;
        for (int i = 0; i < 9; i++) checksum ^= payload[i];
        payload[9] = checksum;

        byte[] raw = new byte[2 + payload.Length];
        raw[0] = (byte)(WsModeFlag | (randomFlag ? 0x10 : 0));
        raw[1] = (byte)keyRandom;
        byte[] encrypted = Rc4(new byte[] { (byte)keyRandom }, payload);
        Buffer.BlockCopy(encrypted, 0, raw, 2, encrypted.Length);
        return CustomBase64(raw);
    }

    private static int _counter;
    private static readonly Random Rng = new Random();

    /// 线上用法：counter 自增、其余随机（每次重连都要重算，不能缓存）
    public static string Sign(string xMsStub)
    {
        _counter = (_counter + 1) & 0x3F;
        return SignWithValues(xMsStub, _counter, Rng.Next(2) == 0, Rng.Next(255), Rng.Next(255));
    }

    public static byte[] Md5(byte[] data)
    {
        using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            return md5.ComputeHash(data);
    }

    public static string Md5Hex(string text)
    {
        byte[] digest = Md5(Encoding.UTF8.GetBytes(text));
        StringBuilder builder = new StringBuilder(32);
        for (int i = 0; i < digest.Length; i++) builder.Append(digest[i].ToString("x2"));
        return builder.ToString();
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        byte[] s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            byte tmp = s[i]; s[i] = s[j]; s[j] = tmp;
        }
        byte[] output = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            byte tmp = s[a]; s[a] = s[b]; s[b] = tmp;
            output[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 0xFF]);
        }
        return output;
    }

    /// 自定义字母表的 base64（**不带 padding**）
    private static string CustomBase64(byte[] data)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < data.Length; i += 3)
        {
            int n = 0;
            for (int k = 0; k < 3; k++) n = (n << 8) | (i + k < data.Length ? data[i + k] : 0);
            builder.Append(Alphabet[(n >> 18) & 63]);
            builder.Append(Alphabet[(n >> 12) & 63]);
            builder.Append(Alphabet[(n >> 6) & 63]);
            builder.Append(Alphabet[n & 63]);
        }
        return builder.ToString();
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

internal sealed class DouyinDanmakuClient : IDanmakuClient
{
    public event DanmakuHandler DanmakuReceived;
    // 抖音**没有 SC 等价物**（已核过全部 610 个 proto message：ChatMessage 里没有金额/时长/背景色
    // 任何字段；PaidLive* 是"买票看播"不是付费留言）。接口要求有这两个事件。
#pragma warning disable 67
    public event SuperChatHandler SuperChatReceived;
    public event SuperChatDeleteHandler SuperChatDeleted;
#pragma warning restore 67
    public event StatusHandler StatusChanged;
    public event StatusHandler RoomInfo;

    // ---- 平台表情字典 ----
    //
    // 抖音也用 `[xxx]` 方括号代码，但有**接口**（2026-09-26 实测 HTTP 200、214 条）：
    //     GET https://www.douyin.com/aweme/v1/web/emoji/list/
    //     → { emoji_list: [ { display_name: "[微笑]", emoji_url: { uri, url_list: [...] } } ] }
    //
    // ⚠️ `url_list` 里的地址**带签名**（x-expires + x-signature）。实测 x-expires=2105784000
    //     = 2036 年，所以等同长期有效 —— 但**不能缓存太久**，签名是服务端发的，
    //     哪天真过期了就得重拉。这里每次连接时重新拉一遍（一次请求，可接受）。
    //
    // 需要 cookie（tz 那份 `runtime\douyin-cookies.txt`，DouyinGuard 管）。
    // 失败只记一行日志：表情退回方括号文本，**不能影响弹幕接收**。
    private readonly EmoticonDictionary _emoticons = new EmoticonDictionary();
    public EmoticonDictionary Emoticons { get { return _emoticons; } }
    public event Action EmoticonsLoaded;

    private const string DouyinEmojiApi = "https://www.douyin.com/aweme/v1/web/emoji/list/";

    /// 拉抖音表情字典。放在房间解析之前或之后都行 —— 它不依赖房间。
    private void FetchEmoticons()
    {
        try
        {
            string body;
            using (HttpWebResponse response = LiveHub.OpenScopedGet(DouyinEmojiApi, 15000, UserAgent, "https://www.douyin.com/",
                delegate(HttpWebRequest request, Uri uri)
                {
                    request.Accept = "application/json, text/plain, */*";
                    request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
                }))
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                body = reader.ReadToEnd();

            // 这里不做 wbi/签名 —— 实测直接 GET 就通（带 cookie）。若哪天要签名了，
            // 表现会是 status_code != 0 或 emoji_list 为空，那时再补。
            //
            // ⚠️ 下面这些 Json/Val/Dict/List/Str/ToStr 是**通用** JSON 辅助，
            //    只是恰好定义在 BiliDanmakuClient 里（internal static）。
            //    不是 B站 专有逻辑 —— 这里直接用，别为此再抄一份实现。
            Dictionary<string, object> root = BiliDanmakuClient.Json(body);
            System.Collections.IList list = root == null
                ? null : BiliDanmakuClient.List(BiliDanmakuClient.Val(root, "emoji_list"));
            if (list == null)
            {
                // ⚠️ 必须落日志，不能只走 Info（那是个事件、状态栏一闪就过去了）。
                //    用户报「表情不显示」时，事后唯一能查的就是这行。
                DanmakuHost.Log("抖音表情字典没拿到：响应里没有 emoji_list（脱敏摘要："
                    + PlatformHealth.ResponseSummary(body) + "）");
                Info("抖音表情字典没拿到，表情按文本显示");
                return;
            }

            int before = _emoticons.Count;
            FillDouyinEmoticons(_emoticons, body);

            if (_emoticons.Count > before)
            {
                string probeUrl;
                DanmakuHost.Log("抖音表情字典 " + _emoticons.Count + " 个（新增 "
                    + (_emoticons.Count - before) + "）｜有[微笑]="
                    + (_emoticons.TryGet("[微笑]", out probeUrl) ? "是" : "否"));
                Info("抖音表情字典 " + _emoticons.Count + " 个");
                Action handler = EmoticonsLoaded;
                if (handler != null) handler();
            }
            else
            {
                DanmakuHost.Log("抖音表情字典解出来 0 条（响应 " + (body == null ? 0 : body.Length)
                    + " 字符、emoji_list " + list.Count + " 项），表情按文本显示");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Report("douyin-emoticon", ex);
            Info("抖音表情字典拉取失败，表情按文本显示");
        }
    }

    /// 把 `emoji/list` 的响应解析进字典。
    ///
    /// 抽成**静态**以便使用最小响应夹具离线验证，不依赖网络。
    ///
    /// 响应形如：
    ///   { "emoji_list": [ { "display_name": "[微笑]",
    ///                       "emoji_url": { "uri": "...", "url_list": ["https://...", ...] } } ] }
    /// 取 `url_list[0]`：实测第一个就可用，且签名 x-expires 到 2036 年。
    internal static void FillDouyinEmoticons(EmoticonDictionary dict, string json)
    {
        // 这里不做 wbi/签名 —— 实测直接 GET 就通（带 cookie）。若哪天要签名了，
        // 表现会是 emoji_list 为空，那时再补。
        //
        // ⚠️ 下面这些 Json/Val/Dict/List/Str/ToStr 是**通用** JSON 辅助，
        //    只是恰好定义在 BiliDanmakuClient 里（internal static）。
        //    不是 B站 专有逻辑 —— 这里直接用，别为此再抄一份实现。
        Dictionary<string, object> root = BiliDanmakuClient.Json(json);
        System.Collections.IList list = root == null
            ? null : BiliDanmakuClient.List(BiliDanmakuClient.Val(root, "emoji_list"));
        if (list == null) return;

        foreach (object itemObj in list)
        {
            Dictionary<string, object> item = BiliDanmakuClient.Dict(itemObj);
            if (item == null) continue;
            string name = BiliDanmakuClient.Str(item, "display_name");
            Dictionary<string, object> emojiUrl =
                BiliDanmakuClient.Dict(BiliDanmakuClient.Val(item, "emoji_url"));
            if (name == null || emojiUrl == null) continue;
            System.Collections.IList urls =
                BiliDanmakuClient.List(BiliDanmakuClient.Val(emojiUrl, "url_list"));
            if (urls == null || urls.Count == 0) continue;
            dict.Add(name, BiliDanmakuClient.ToStr(urls[0]));
        }
    }

    private const string SdkVersion = "1.0.15";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    /// 实测 webcast100 与 webcast5 都可连；一个不通就换下一个
    private static readonly string[] Hosts = new string[]
    {
        "webcast100-ws-web-lq.douyin.com",
        "webcast5-ws-web-lf.douyin.com",
        "webcast5-ws-web-lq.douyin.com"
    };

    private readonly string _webRid;
    private string _roomId = "";
    private string _userUniqueId = "";
    private string _ttwid = "";
    private string _anchor = "";
    private bool _live;

    private ClientWebSocket _socket;
    private volatile bool _stopping;
    private HeartbeatAckWatch _heartbeatWatch = new HeartbeatAckWatch(5000);

    public DouyinDanmakuClient(string room) { _webRid = room; }

    public void Stop()
    {
        _stopping = true;
        try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }
    }

    private void Status(string text) { DanmakuHost.Log(text); StatusHandler handler = StatusChanged; if (handler != null) handler(text); }
    private void Info(string text) { StatusHandler handler = RoomInfo; if (handler != null) handler(text); }

    private void Sleep(int ms)
    {
        int left = ms;
        while (left > 0 && !_stopping) { Thread.Sleep(Math.Min(250, left)); left -= 250; }
    }

    private static string Describe(Exception ex)
    {
        if (ex == null) return "（没有拿到具体异常）";     // 别让日志自己抛空引用盖住真正的原因
        AggregateException aggregate = ex as AggregateException;
        if (aggregate != null && aggregate.InnerExceptions.Count > 0) ex = aggregate.InnerExceptions[0];
        // 状态会落日志；异常 Message 可能包含签名 URL 或远端内容。
        return PlatformHealth.Classify(ex) + " / " + ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("x");
    }

    public void Run()
    {
        FetchEmoticons();   // 表情字典：一次请求，失败只影响表情显示、不影响弹幕
        int attempt = 0;
        while (!_stopping)
        {
            attempt++;
            try
            {
                if (_roomId.Length == 0 && !FetchRoom()) { Sleep(10000); continue; }
                Connect();
                attempt = 0;
                ReadLoop();
            }
            catch (Exception ex)
            {
                if (!_stopping) { PlatformHealth.Record("douyin", "danmaku", PlatformHealth.Classify(ex)); if (!_stopping) Status("抖音弹幕连接中断：" + Describe(ex)); }
            }
            finally { CloseQuietly(); _roomId = ""; }
            if (_stopping) break;
            int wait = Math.Min(30, 3 * attempt);
            if (wait < 3) wait = 3;
            Status("抖音：" + wait + " 秒后重连…");
            Sleep(wait * 1000);
        }
    }

    // ------------------------------------------------------------------ 房间信息

    /// 与播放共用房间请求及结构化解析，保留本次响应的 ttwid 供握手使用。
    internal bool FetchRoom()
    {
        try
        {
            _roomId = "";
            _userUniqueId = "";
            _anchor = "";
            _ttwid = SavedTtwid(PlatformLogin.CookieHeaderForUri(new Uri("https://live.douyin.com/" + _webRid)));
            LiveHub.DouyinRoom structured = LiveHub.FetchDouyinDanmakuRoom(_webRid);
            if (structured == null || string.IsNullOrEmpty(structured.RoomId))
            {
                if (structured != null) PlatformHealth.Record("douyin", "danmaku", "parse-error");
                Status("抖音：未取得房间信息，将重试 IPv4 / 域名 / 代理路径；详见本地平台状态");
                return false;
            }
            _roomId = structured.RoomId;
            _anchor = structured.Owner;
            _live = structured.Status == 2;
            _userUniqueId = structured.UserUniqueId;
            if (!string.IsNullOrEmpty(structured.Ttwid)) _ttwid = structured.Ttwid;
            if (string.IsNullOrEmpty(_userUniqueId))
            {
                Random seed = new Random();
                StringBuilder digits = new StringBuilder("76");
                for (int d = 0; d < 17; d++) digits.Append(seed.Next(10));
                _userUniqueId = digits.ToString();
            }
            Info("抖音 " + _webRid + (string.IsNullOrEmpty(_anchor) ? "" : " · " + _anchor)
                + (_live ? "" : "（未开播）"));
            if (!_live) Status("抖音：房间未开播，仍会保持连接等待");
            return true;
        }
        catch (Exception ex)
        {
            PlatformHealth.Record("douyin", "danmaku", PlatformHealth.Classify(ex));
            Status("抖音：取房间失败 " + Describe(ex));
            return false;
        }
    }

    private static string FirstMatch(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string SavedTtwid(string cookies)
    {
        Match match = Regex.Match(cookies ?? "", @"(?:^|;\s*)ttwid=([^;]+)");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string Nonce()
    {
        Random random = new Random();
        StringBuilder builder = new StringBuilder(21);
        const string digits = "0123456789abcdef";
        for (int i = 0; i < 21; i++) builder.Append(digits[random.Next(16)]);
        return builder.ToString();
    }

    /// 签名 URL 的完整参数表（照调研脚本逐项对齐）。
    /// ⚠️ signature **含 `+` 和 `/`**，必须 percent-encode，否则连接直接失败。
    internal string BuildUrl(string host)
    {
        string joined = string.Join(",", new string[]
        {
            "live_id=1", "aid=6383", "version_code=180800",
            "webcast_sdk_version=" + SdkVersion,
            "room_id=" + _roomId, "sub_room_id=", "sub_channel_id=",
            "did_rule=3", "user_unique_id=" + _userUniqueId,
            "device_platform=web", "device_type=", "ac=", "identity=audience"
        });
        string stub = DouyinSign.Md5Hex(joined);
        string signature = DouyinSign.Sign(stub);

        long now = DouyuDanmakuClient.ToUnixMs(DateTime.UtcNow);
        long sec = now / 1000;
        long baseValue = sec * 4294967296L;
        Random random = new Random();
        string cursor = "t-" + now + "_r-" + (baseValue + random.Next(65536))
            + "_d-1_u-1_h-" + (baseValue + (long)(random.NextDouble() * 4294967295L));
        string internalExt = "internal_src:dim"
            + "|wss_push_room_id:" + _roomId
            + "|wss_push_did:" + _userUniqueId
            + "|first_req_ms:" + now + "|fetch_time:" + now + "|seq:1|wss_info:0-" + now + "-0-0"
            + "|wrds_v:" + (baseValue + random.Next(65536));

        StringBuilder url = new StringBuilder();
        url.Append("wss://").Append(host).Append("/webcast/im/push/v2/?");
        url.Append("app_name=douyin_web&version_code=180800");
        url.Append("&webcast_sdk_version=").Append(SdkVersion);
        url.Append("&update_version_code=").Append(SdkVersion);
        url.Append("&compress=gzip&device_platform=web&cookie_enabled=true");
        url.Append("&screen_width=1920&screen_height=1080");
        url.Append("&browser_language=zh-CN&browser_platform=Win32&browser_name=Mozilla");
        url.Append("&browser_version=").Append(Uri.EscapeDataString(UserAgent.Substring(UserAgent.IndexOf("Mozilla/", StringComparison.Ordinal) + 8)));
        url.Append("&browser_online=true&tz_name=Asia/Shanghai");
        url.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        url.Append("&internal_ext=").Append(Uri.EscapeDataString(internalExt));
        url.Append("&host=https://live.douyin.com&aid=6383&live_id=1&did_rule=3");
        url.Append("&endpoint=live_pc&support_wrds=1");
        url.Append("&user_unique_id=").Append(_userUniqueId);
        url.Append("&im_path=/webcast/im/fetch/&identity=audience");
        url.Append("&need_persist_msg_count=15&insert_task_id=&live_reason=");
        url.Append("&room_id=").Append(_roomId);
        url.Append("&heartbeatDuration=0");
        url.Append("&signature=").Append(Uri.EscapeDataString(signature));
        return url.ToString();
    }

    // ------------------------------------------------------------------ 连接

    private void Connect()
    {
        Exception last = null;
        for (int i = 0; i < Hosts.Length; i++)
        {
            try
            {
                Status("抖音：正在连接 " + Hosts[i] + " …");
                _socket = new ClientWebSocket();
                // ⚠️ net48 里 Cookie 属于受限头，SetRequestHeader 会抛 ArgumentException —— 走 Options.Cookies
                _socket.Options.Cookies = new CookieContainer();
                _socket.Options.Cookies.Add(new Uri("https://live.douyin.com/"),
                    new Cookie("ttwid", _ttwid, "/", ".douyin.com"));
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (CancellationTokenSource cts = new CancellationTokenSource(12000))
                    _socket.ConnectAsync(new Uri(BuildUrl(Hosts[i])), cts.Token).Wait();
                _lastFrameTick = Environment.TickCount;
                _frameCount = 0;
                _heartbeatWatch = new HeartbeatAckWatch(5000);
        PlatformHealth.Record("douyin", "danmaku", "ready");
                Status("抖音：已连接");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                CloseQuietly();
            }
        }
        throw new Exception("所有 webcast 节点都连不上（签名被拒或网络不通）：" + Describe(last));
    }

    private void CloseQuietly()
    {
        try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }
        _socket = null;
    }

    /// ⚠️ 发送现在有**两个线程**（接收循环回 ack、心跳线程发 hb），
    ///    而 ClientWebSocket 同一时刻只允许一个挂起的 SendAsync —— 必须串行化，
    ///    否则偶发 InvalidOperationException 会被调用方的 catch 吞掉，静默丢心跳/丢 ack。
    private readonly object _sendGate = new object();

    private void Send(byte[] packet)
    {
        DanmakuSocket.Send(_socket, packet, _sendGate);
    }

    /// 心跳 = `3a 02 68 62`（PushFrame{payload_type:"hb"}）。
    /// ⚠️ net48 的 ClientWebSocket 发不了自定义 PING 帧，所以走 Binary 帧（已被 C# 项目验证可行）。
    internal static byte[] HeartbeatPacket()
    {
        return new byte[] { 0x3A, 0x02, 0x68, 0x62 };
    }

    /// ack = PushFrame{log_id, payload_type:"ack", payload:utf8(internalExt)}
    internal static byte[] AckPacket(long logId, string internalExt)
    {
        ProtoWriter writer = new ProtoWriter();
        writer.Varint(2, logId);
        writer.String(7, "ack");
        writer.Bytes(8, Encoding.UTF8.GetBytes(internalExt ?? ""));
        return writer.ToArray();
    }

    // ------------------------------------------------------------------ 收包与解码

    private void ReadLoop()
    {
        byte[] chunk = new byte[128 * 1024];
        string internalExt = null;
        long lastLogId = 0;

        // ⚠️ 心跳走**独立线程**，绝不能再拿「带超时的 CTS 去取消 ReceiveAsync」——
        //    这与 2026-09-30 修掉的虎牙是**同一个缺陷**（那份有完整的隔离复现与线上实测）。
        //    本文件的「致病四行」与修复前的 danmaku-9-huya.cs 逐字相同：
        //      · ClientWebSocket **一旦取消 ReceiveAsync 就把整条连接置为 Aborted**；
        //      · 之后每次接收都立刻抛 AggregateException(...)，被 `catch (AggregateException) { }`
        //        无声吞掉 → 死循环：不重连、不报错、弹幕再也不来，还满负荷烧一个核。
        //    实测（未开播房间 60 秒）：CPU 时间 ≥ 墙钟时间，且报告里全程没有任何
        //    「服务端关闭了连接 / N 秒后重连…」—— 静默死亡。
        //    开播房间的消息密度较高，可能掩盖这个问题，
        //    但只要出现一次 ≥3 秒空档就会中招。
        ClientWebSocket socket = _socket;
        HeartbeatAckWatch watch = _heartbeatWatch;
        Thread heartbeat = new Thread(delegate () { HeartbeatLoop(socket, watch); });
        heartbeat.IsBackground = true;
        heartbeat.Name = "douyin-heartbeat";
        heartbeat.Start();

        while (!_stopping)
        {
            byte[] frame;
            try
            {
                frame = DanmakuSocket.ReceiveMessage(socket, chunk);
                if (frame == null)
                {
                    Status("抖音：服务端关闭了连接");
                    return;
                }
            }
            catch (AggregateException ex)
            {
                // 连接真的断了就把异常抛给 Run()（它会重连）—— 不能再无声吞掉：
                // 「吞掉」正是上面那个 bug 的后半段（死循环 + 永不重连）。
                Exception inner = ex.InnerExceptions.Count > 0 ? ex.InnerExceptions[0] : ex;
                if (!IsSocketOpen(socket)) throw inner;
                Status("抖音：接收出错（" + Describe(inner) + "），继续");
                Sleep(200);
                continue;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            if (frame.Length == 0) continue;

            try
            {
                // 观测用：只记「刚收到过帧」，不参与任何判断
                _lastFrameTick = Environment.TickCount;
                _frameCount++;
                HandleFrame(frame, ref internalExt, ref lastLogId);
                if (internalExt != null && lastLogId != 0)
                {
                    try { DanmakuSocket.Send(socket, AckPacket(lastLogId, internalExt), _sendGate); }
                    catch (Exception ex)
                    {
                        if (!_stopping) Status("抖音：确认回包发送失败，重连：" + Describe(ex));
                        throw;
                    }
                    internalExt = null;
                }
            }
            catch (Exception ex)
            {
                if (!IsSocketOpen(socket)) throw;
                Status("抖音：解析出错 " + ex.Message);
            }
        }
    }

    /// 心跳间隔（秒）。原来靠接收循环每 3 秒醒一次来发 —— 那个「醒」正是把连接打死的元凶。
    private const int HeartbeatSeconds = 10;

    /// socket 是否还活着（Aborted / Closed 都算死）
    private static bool IsSocketOpen(ClientWebSocket socket)
    {
        try { return socket != null && socket.State == WebSocketState.Open; }
        catch (Exception) { return false; }
    }

    /// 心跳独立线程：只发心跳、不碰接收 —— 所以接收端可以安全地无限期阻塞。
    ///
    /// 静默帧观测仍不重连。仅检查已发送心跳的明确回包 payload_type=hb。
    /// 2026-09-30 使用项目 Cookie 测两个房间：各 7 发 7 回，延迟约 0.04–0.07 秒。
    /// 回包期限 5 秒短于发送间隔 10 秒，连续两次缺回包才终止本连接。
    private void HeartbeatLoop(ClientWebSocket socket, HeartbeatAckWatch watch)
    {
        int lastBeat = Environment.TickCount;
        DateTime? lastWarn = null;   // null = 从没报过 → 第一次静默到阈值就报
        while (!_stopping)
        {
            Sleep(1000);              // 1 秒一跳只为能及时看时钟；心跳节奏仍是 HeartbeatSeconds
            if (_stopping || socket != _socket || !IsSocketOpen(socket)) return;
            DateTime now = DateTime.UtcNow;
            int tick = Environment.TickCount;
            if (watch.ShouldReconnect(tick))
            {
                DanmakuHost.Log("抖音：连续两次心跳在 5 秒内未收到 hb 回包，主动重连");
                DanmakuSocket.Abort(socket);
                return;
            }
            if (unchecked(tick - lastBeat) >= HeartbeatSeconds * 1000)
            {
                lastBeat = tick;
                watch.Begin(tick);
                try { DanmakuSocket.Send(socket, HeartbeatPacket(), _sendGate); }
                catch (Exception ex)
                {
                    if (!_stopping) DanmakuHost.Log("抖音：心跳发送失败，重连：" + Describe(ex));
                    return;
                }
            }
            int silentMs = unchecked(Environment.TickCount - _lastFrameTick);   // 回绕安全
            if (SilenceWatch.ShouldWarn(silentMs, lastWarn, now))
            {
                lastWarn = now;
                DanmakuHost.Log(SilenceWatch.Describe("抖音", silentMs, _frameCount));
            }
        }
    }

    /// 上次收到任何帧的时刻（Environment.TickCount，接收线程写、心跳线程读；int 读写天然原子）
    private volatile int _lastFrameTick = Environment.TickCount;

    /// 本次连接累计收到的帧数（仅用于日志）
    private int _frameCount;

    /// PushFrame → gzip → Response → 每条 Message
    internal void HandleFrame(byte[] frame, ref string internalExt, ref long lastLogId)
    {
        ProtoReader outer = new ProtoReader(frame);
        byte[] payload = null;
        long logId = 0;
        string payloadType = null;
        int field, wire;
        while (outer.TryReadTag(out field, out wire))
        {
            if (field == 8 && wire == 2) payload = outer.ReadBytes();
            else if (field == 7 && wire == 2) payloadType = outer.ReadString();
            else if (field == 2 && wire == 0) logId = outer.ReadVarint();
            else outer.Skip(wire);
        }
        lastLogId = logId;
        if (payloadType == "hb") { _heartbeatWatch.Acknowledge(); return; }
        if (payload == null || payload.Length == 0) return;

        // ⚠️ 只有**一层** gzip（在 payload 上）。容错：不是 gzip 就直接当 protobuf 用
        byte[] decoded = payload;
        if (payload.Length > 2 && payload[0] == 0x1F && payload[1] == 0x8B)
            decoded = Gunzip(payload);

        ProtoReader response = new ProtoReader(decoded);
        while (response.TryReadTag(out field, out wire))
        {
            if (field == 1 && wire == 2)
            {
                byte[] messageBytes = response.ReadBytes();
                try { HandleMessage(messageBytes); }
                catch (Exception) { }
            }
            else if (field == 5 && wire == 2) internalExt = response.ReadString();
            else if (field == 9 && wire == 0) response.ReadVarint();
            else response.Skip(wire);
        }
    }

    private static byte[] Gunzip(byte[] data)
    {
        using (MemoryStream input = new MemoryStream(data))
        using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            return DanmakuMessageLimits.ReadExpanded(gzip);
    }

    private void HandleMessage(byte[] messageBytes)
    {
        ProtoReader message = new ProtoReader(messageBytes);
        string method = null;
        byte[] payload = null;
        int field, wire;
        while (message.TryReadTag(out field, out wire))
        {
            if (field == 1 && wire == 2) method = message.ReadString();
            else if (field == 2 && wire == 2) payload = message.ReadBytes();
            else message.Skip(wire);
        }
        // ⚠️ 实测大房间里电商 + 进场消息占 >90%（进场 298 条 vs 弹幕 2 条 / 20 秒）
        //    → 只认白名单，其余**尽早丢**，别浪费解析
        if (method == "WebcastChatMessage" && payload != null) HandleChat(payload);
    }

    /// ChatMessage { Common common=1, User user=2, string content=3, ... }
    /// User     { uint64 id=1, ..., string nickname=3, ..., uint32 level=6, Image avatar_thumb=9 { url_list=1 } }
    internal DanmakuItem ParseChat(byte[] payload)
    {
        ProtoReader reader = new ProtoReader(payload);
        string content = null;
        string nickname = null;
        long userId = 0;
        int level = 0;
        int field, wire;
        while (reader.TryReadTag(out field, out wire))
        {
            if (field == 3 && wire == 2) content = reader.ReadString();
            else if (field == 2 && wire == 2)
            {
                ProtoReader user = reader.ReadMessage();
                int userField, userWire;
                while (user.TryReadTag(out userField, out userWire))
                {
                    if (userField == 1 && userWire == 0) userId = user.ReadVarint();
                    else if (userField == 3 && userWire == 2) nickname = user.ReadString();
                    else if (userField == 6 && userWire == 0) level = (int)user.ReadVarint();
                    else if (userField == 9 && userWire == 2) FirstUrl(user.ReadMessage());   // 头像（弹幕不显示，读了就丢）
                    else user.Skip(userWire);
                }
            }
            else reader.Skip(wire);
        }
        if (string.IsNullOrEmpty(content)) return null;

        DanmakuItem item = new DanmakuItem();
        item.Text = content;
        item.UserName = nickname;
        item.UserId = userId;
        item.UserLevel = level;
        item.Color = 0xFFFFFF;
        item.Mode = 1;
        item.FontSize = 25;
        item.TimestampMs = DouyuDanmakuClient.ToUnixMs(DateTime.UtcNow);
        return item;
    }

    /// Image { repeated string url_list = 1; }
    private static string FirstUrl(ProtoReader image)
    {
        int field, wire;
        while (image.TryReadTag(out field, out wire))
        {
            if (field == 1 && wire == 2) return image.ReadString();
            image.Skip(wire);
        }
        return null;
    }

    private void HandleChat(byte[] payload)
    {
        DanmakuHandler handler = DanmakuReceived;
        if (handler == null) return;
        DanmakuItem item = ParseChat(payload);
        if (item != null) handler(item);
    }
}

/// 极简 protobuf 写入器（只用来拼心跳/ack 这种极小的包）
internal sealed class ProtoWriter
{
    private readonly List<byte> _buffer = new List<byte>(64);
    public byte[] ToArray() { return _buffer.ToArray(); }

    private void Tag(int field, int wireType) { WriteVarint((long)((field << 3) | wireType)); }

    public ProtoWriter Varint(int field, long value)
    {
        Tag(field, 0);
        WriteVarint(value);
        return this;
    }

    public ProtoWriter String(int field, string value)
    {
        return Bytes(field, Encoding.UTF8.GetBytes(value == null ? "" : value));
    }

    public ProtoWriter Bytes(int field, byte[] data)
    {
        Tag(field, 2);
        WriteVarint(data.Length);
        _buffer.AddRange(data);
        return this;
    }

    private void WriteVarint(long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80) { _buffer.Add((byte)(v | 0x80)); v >>= 7; }
        _buffer.Add((byte)v);
    }
}
