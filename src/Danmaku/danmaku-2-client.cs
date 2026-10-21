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
// B站 弹幕客户端（握手 → WebSocket → 解包 → 解析）
// --------------------------------------------------------------------------------------------

// 事件委托提到**文件作用域**（原来嵌在 BiliDanmakuClient 里）。
// 因为 IDanmakuClient 要让多个平台共用同一套事件签名 —— 嵌在类里的话
// 别的平台的 client 就引用不到这几个类型了。
internal delegate void DanmakuHandler(DanmakuItem item);
internal delegate void SuperChatHandler(SuperChatItem item);
internal delegate void SuperChatDeleteHandler(long id);
internal delegate void StatusHandler(string text);

internal sealed partial class BiliDanmakuClient : IDanmakuClient, ILiveEventSource
{
    public event DanmakuHandler DanmakuReceived;
    public event SuperChatHandler SuperChatReceived;
    public event SuperChatDeleteHandler SuperChatDeleted;
    public event StatusHandler StatusChanged;
    /// 房间真实信息（真实房号 / 标题 / 主播名 / 是否在播），握手时就会拿到，交给调用方做 UI 与标题。
    public event StatusHandler RoomInfo;

    /// 表情字典拉好时触发。渲染器收到后要丢掉文本位图缓存 —— 换房间表情表就变了。
    /// 拉失败不触发：表情退化成方括号文本，弹幕照常。
    public event Action EmoticonsLoaded;

    private readonly string _roomArg;
    private readonly string _cookieFile;
    private volatile bool _stopping;

    /// 房间表情字典（`[大笑]` 这类方括号代码 → 图片）。内容在 FetchEmoticons() 里原地填充，
    /// 所以渲染器**可以一直持有这个引用**，不必每次重新获取。
    private readonly EmoticonDictionary _emoticons = new EmoticonDictionary();
    public EmoticonDictionary Emoticons { get { return _emoticons; } }

    private string _realRoomId = "0";
    private string _roomTitle = "";
    private string _anchorName = "";
    private int _liveStatus = -1;

    private string _wbiMixin = "";
    private DateTime _wbiFetchedAt = DateTime.MinValue;
    private string _token;
    private List<string> _hosts = new List<string>();
    private long _uid;
    private string _buvid = "";

    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly int[] MixinTab = new int[]
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    };

    private readonly CookieContainer _jar = new CookieContainer();

    public BiliDanmakuClient(string room, string cookieFile)
    {
        _roomArg = room;
        _cookieFile = cookieFile;
        LoadCookies();
    }

    public string RealRoomId { get { return _realRoomId; } }
    public string RoomTitle { get { return _roomTitle; } }
    public string AnchorName { get { return _anchorName; } }
    public int LiveStatus { get { return _liveStatus; } }

    private void Say(string text)
    {
        StatusHandler handler = StatusChanged;
        if (handler != null) handler(text);
    }

    private void Info(string text)
    {
        StatusHandler handler = RoomInfo;
        if (handler != null) handler(text);
    }

    public void Stop()
    {
        _stopping = true;
    }

    // ------------------------------------------------------------------ 主循环
    public void Run()
    {
        int attempt = 0;
        while (!_stopping)
        {
            try
            {
                if (attempt == 0) Handshake();
                RunOnce();
                attempt = 0;                       // 正常断开（服务端换了节点之类）→ 立刻重连一次
            }
            catch (Exception ex)
            {
                attempt++;
                if (!_stopping) { PlatformHealth.Record("bili", "danmaku", PlatformHealth.Classify(ex)); Say("弹幕连接中断：" + Describe(ex)); }
            }
            if (_stopping) break;
            // 指数退避 1s → 2s → 4s → … 上限 30s；认证失败会重跑 Handshake 换 token
            int wait = (int)Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
            for (int i = 0; i < wait * 10 && !_stopping; i++) Thread.Sleep(100);
            if (attempt >= 2)
            {
                try { Handshake(); attempt = 0; }
                catch (Exception ex) { if (!_stopping) Say("重新握手失败：" + Describe(ex)); }
            }
        }
    }

    /// Task.Wait() 会把真实原因包进 AggregateException，直接 ToString 出来只有一句
    /// 「发生一个或多个错误」，排查时等于没说。这里把最内层的原因挖出来。
    internal static string Describe(Exception ex)
    {
        AggregateException aggregate = ex as AggregateException;
        if (aggregate != null && aggregate.InnerExceptions.Count > 0)
        {
            Exception first = aggregate.InnerExceptions[0];
            string inner = Describe(first);
            return first.GetType().Name + (inner.Length > 0 && inner != first.Message ? "：" + inner : "：" + first.Message);
        }
        if (ex.InnerException != null)
        {
            string inner = Describe(ex.InnerException);
            return ex.GetType().Name + "：" + inner;
        }
        return ex.GetType().Name + "：" + ex.Message;
    }

    // ------------------------------------------------------------------ 握手三步
    public void Handshake()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        string buvid = CookieValue("buvid3");
        if (buvid == null || buvid.Length == 0)
        {
            try { HttpGet("https://www.bilibili.com/", "https://www.bilibili.com/"); } catch (Exception) { }
            buvid = CookieValue("buvid3");
        }
        _buvid = buvid == null ? "" : buvid;

        // ① 房间信息（顺便拿真实房号；这个接口 live-hub 本来就在调，不需要签名）
        Dictionary<string, object> info = Json(HttpGet(
            "https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + _roomArg,
            "https://live.bilibili.com/"));
        Dictionary<string, object> data = Dict(info == null ? null : Val(info, "data"));
        if (data != null)
        {
            string rid = Str(data, "room_id");
            if (rid != null && rid != "0") _realRoomId = rid;
            _roomTitle = Str(data, "title");
            _liveStatus = Int(data, "live_status", -1);
        }
        if (_realRoomId == "0") _realRoomId = _roomArg;
        try
        {
            Dictionary<string, object> anchor = Json(HttpGet(
                "https://api.live.bilibili.com/live_user/v1/UserInfo/get_anchor_in_room?roomid=" + _realRoomId,
                "https://live.bilibili.com/"));
            Dictionary<string, object> ad = Dict(anchor == null ? null : Val(anchor, "data"));
            Dictionary<string, object> ai = Dict(ad == null ? null : Val(ad, "info"));
            if (ai != null) _anchorName = Str(ai, "uname");
        }
        catch (Exception) { }
        Info("真实房号 " + _realRoomId
            + "｜" + (_roomTitle == null ? "" : _roomTitle)
            + "｜" + (_liveStatus == 1 ? "直播中" : _liveStatus == 2 ? "轮播中" : "未开播"));

        // ①.5 房间表情字典 —— 必须在真实房号之后。
        FetchEmoticons();

        // ② 登录态：带了 uid 却配着失效 token 会被服务端踢，所以先问清楚
        _uid = 0;
        try
        {
            Dictionary<string, object> nav = Json(HttpGet(
                "https://api.bilibili.com/x/web-interface/nav", "https://www.bilibili.com/"));
            Dictionary<string, object> nd = Dict(nav == null ? null : Val(nav, "data"));
            if (nd != null)
            {
                PlatformHealth.Record("bili", "login", Bool(nd, "isLogin") ? "ready" : "auth-required");
                if (Bool(nd, "isLogin"))
                {
                    string mid = Str(nd, "mid");
                    long parsed;
                    if (mid != null && long.TryParse(mid, out parsed)) _uid = parsed;
                    Dictionary<string, object> wi = Dict(Val(nd, "wbi_img"));
                    if (wi != null)
                    {
                        string imgUrl = Str(wi, "img_url");
                        string subUrl = Str(wi, "sub_url");
                        if (imgUrl != null && subUrl != null)
                            _wbiMixin = MixinKey(Path.GetFileNameWithoutExtension(imgUrl)
                                + Path.GetFileNameWithoutExtension(subUrl));
                        _wbiFetchedAt = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (Exception) { }
        if (_wbiMixin.Length == 0 || (DateTime.UtcNow - _wbiFetchedAt).TotalHours >= 8) _wbiMixin = "";

        // ③ 弹幕服务器 + token（**强制 wbi 签名**，不签名稳定 -352）
        if (_wbiMixin.Length == 0)
        {
            try { FetchWbiOnly(); } catch (Exception) { }
        }
        if (_wbiMixin.Length == 0) throw new InvalidOperationException("拿不到 wbi 签名密钥（nav 失败）");

        string url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo"
            + Sign("id=" + Uri.EscapeDataString(_realRoomId) + "&type=0");
        Dictionary<string, object> conf = Json(HttpGet(url, "https://live.bilibili.com/"));
        int code = conf == null ? -1 : Int(conf, "code", -1);
        if (code == -352)
        {
            _wbiMixin = "";                    // 签名过期 → 立刻重取一次再试
            FetchWbiOnly();
            url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo"
                + Sign("id=" + Uri.EscapeDataString(_realRoomId) + "&type=0");
            conf = Json(HttpGet(url, "https://live.bilibili.com/"));
            code = conf == null ? -1 : Int(conf, "code", -1);
        }
        if (code != 0) throw new InvalidOperationException("getDanmuInfo 失败 code=" + code);

        Dictionary<string, object> cd = Dict(Val(conf, "data"));
        _token = Str(cd, "token");
        _hosts = new List<string>();
        System.Collections.IList hosts = List(Val(cd, "host_list"));
        if (hosts != null)
            for (int i = 0; i < hosts.Count; i++)
            {
                Dictionary<string, object> h = Dict(hosts[i]);
                string host = Str(h, "host");
                if (host != null && host.Length > 0 && !_hosts.Contains(host)) _hosts.Add(host);
            }
        if (_hosts.Count == 0) _hosts.Add("broadcastlv.chat.bilibili.com");
        Say("握手完成：uid=" + _uid + "，服务器 " + _hosts.Count + " 个");
    }

    /// 拉取当前房间的**表情字典**并把 `[名称]` → 图片 URL 填进 _emoticons。
    ///
    /// 背景（用户反馈）：弹幕文本里的 `[汤圆]` `[大笑]` 是**表情代码**，网页播放器自己查表
    /// 换成图片；我们原来只把文本原样画出来。这跟下面 HandleDanmaku 里处理的 `dmType == 1`
    /// （服务端**直接给图片 URL** 的大表情）**是两套机制** —— 别混淆。
    ///
    /// ⚠️ 以下参数必须符合平台要求：
    ///   · room_id 必须是**真实房间号**：传显示号 7777 → -500；传真实号 545068 → code 0
    ///   · platform 必须是 `pc`：传 `web` → 「参数错误，平台来源错误」
    ///
    /// 失败只记一行日志：表情退化回方括号文本，**不能影响弹幕接收**。
    private void FetchEmoticons()
    {
        try
        {
            string url = "https://api.live.bilibili.com/xlive/web-ucenter/v2/emoticon/GetEmoticons"
                + "?platform=pc&room_id=" + Uri.EscapeDataString(_realRoomId);
            Dictionary<string, object> root = Json(HttpGet(url, "https://live.bilibili.com/"));
            if (root == null)
            {
                DanmakuHost.Log("表情字典：接口没返回可解析的 JSON，表情按文本显示");
                return;
            }
            if (Int(root, "code", -1) != 0)
            {
                // ⚠️ -101 = 账号未登录。这是**用户能自己解决**的情况（扫码登录一次就行），
                //    但原来只写状态栏、日志里一个字都没有 —— 用户报「表情不显示」时
                //    事后完全查不到原因。所有失败分支都必须落日志。
                int code = Int(root, "code", -1);
                DanmakuHost.Log("表情字典没拿到：code=" + code
                    + (code == -101 ? "（账号未登录 —— 用「B站扫码」登录一次即可）" : "")
                    + "，表情按文本显示");
                Say("表情字典没拿到（code=" + code + "），表情按文本显示");
                return;
            }

            Dictionary<string, object> data = Dict(Val(root, "data"));
            System.Collections.IList packages = List(Val(data, "data"));
            if (packages == null)
            {
                DanmakuHost.Log("表情字典：响应里没有 data.data 列表，表情按文本显示");
                return;
            }

            foreach (object packageObj in packages)
            {
                Dictionary<string, object> package = Dict(packageObj);
                if (package == null) continue;
                System.Collections.IList items = List(Val(package, "emoticons"));
                if (items == null) continue;
                foreach (object itemObj in items)
                {
                    Dictionary<string, object> item = Dict(itemObj);
                    if (item == null) continue;
                    // emoji 字段**本身就带方括号**（实测值形如 "[大笑]"），Add 里会规范化
                    _emoticons.Add(Str(item, "emoji"), Str(item, "url"));
                }
            }

            if (_emoticons.Count > 0)
            {
                // ⚠️ 打到**日志**里，不能只走状态栏。用户报「某个表情不显示」时，
                //    第一个要回答的问题就是「字典拿到几条、里面到底有没有那个代码」，
                //    而状态栏一闪就过去了、事后查不到。
                string probeUrl;
                DanmakuHost.Log("表情字典 " + _emoticons.Count + " 个"
                    + "｜有[大笑]=" + (_emoticons.TryGet("[大笑]", out probeUrl) ? "是" : "否")
                    + "｜有[虎年]=" + (_emoticons.TryGet("[虎年]", out probeUrl) ? "是" : "否"));
                Say("表情字典 " + _emoticons.Count + " 个");
                Action handler = EmoticonsLoaded;
                if (handler != null) handler();
            }
            else
            {
                // 一个都没解出来（字段结构变了 / 全是空包）—— 同样必须留痕，
                // 否则和「没登录」一样是**完全静默**的失败。
                DanmakuHost.Log("表情字典解出来 0 条（响应里有 " + packages.Count
                    + " 个包），表情按文本显示");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Report("bili-emoticon", ex);
            Say("表情字典拉取失败，表情按文本显示");
        }
    }

    private void FetchWbiOnly()
    {
        Dictionary<string, object> nav = Json(HttpGet(
            "https://api.bilibili.com/x/web-interface/nav", "https://www.bilibili.com/"));
        Dictionary<string, object> nd = Dict(nav == null ? null : Val(nav, "data"));
        Dictionary<string, object> wi = Dict(nd == null ? null : Val(nd, "wbi_img"));
        if (wi == null) return;
        string imgUrl = Str(wi, "img_url");
        string subUrl = Str(wi, "sub_url");
        if (imgUrl == null || subUrl == null) return;
        _wbiMixin = MixinKey(Path.GetFileNameWithoutExtension(imgUrl) + Path.GetFileNameWithoutExtension(subUrl));
        _wbiFetchedAt = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------ 一条 WebSocket 连接的生命周期
    private void RunOnce()
    {
        string host = _hosts[_hostIndex % _hosts.Count];
        _hostIndex++;
        using (ClientWebSocket ws = new ClientWebSocket())
        {
            try { ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com"); }
            catch (Exception) { }
            // 实测：net48 的 ClientWebSocketOptions 用 WebHeaderCollection，User-Agent 是受限标头，
            // SetRequestHeader 会抛 ArgumentException。不设 UA 也能正常认证收包，所以不设。

            ManualResetEventSlim done = new ManualResetEventSlim(false);
            Exception failure = null;

            Uri uri = new Uri("wss://" + host + ":443/sub");
            Say("连接 " + host + " …");
            ws.ConnectAsync(uri, CancellationToken.None).Wait(10000);

            string auth = "{\"uid\":" + _uid
                + ",\"roomid\":" + _realRoomId
                + ",\"protover\":2"
                + ",\"platform\":\"web\",\"type\":2"
                + ",\"buvid\":\"" + _buvid + "\""
                + (_token == null ? "" : ",\"key\":\"" + _token + "\"") + "}";
            SendPacket(ws, 7, Encoding.UTF8.GetBytes(auth));

            // 收包线程
            Thread receiver = new Thread(delegate ()
            {
                byte[] buffer = new byte[16 * 1024];
                try
                {
                    while (!_stopping && ws.State == WebSocketState.Open)
                    {
                        byte[] packet;
                        try { packet = DanmakuSocket.ReceiveMessage(ws, buffer); }
                        catch (Exception ex) { failure = ex; break; }
                        if (packet == null) break;
                        Feed(packet);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            receiver.IsBackground = true;
            receiver.Start();

            // 心跳：25 秒一次；超过 60 秒不发会被服务端断开
            DateTime lastBeat = DateTime.MinValue;
            while (!_stopping && ws.State == WebSocketState.Open && !done.IsSet)
            {
                if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 25)
                {
                    try { SendPacket(ws, 2, Encoding.UTF8.GetBytes("[object Object]")); }
                    catch (Exception ex) { failure = ex; break; }
                    lastBeat = DateTime.UtcNow;
                }
                done.Wait(200);
            }

            try { ws.Abort(); } catch (Exception) { }
            done.Wait(2000);
            if (failure != null) throw failure;
        }
    }

    private int _hostIndex;

    private static void SendPacket(ClientWebSocket ws, int operation, byte[] body)
    {
        byte[] packet = new byte[16 + body.Length];
        WriteBe32(packet, 0, packet.Length);
        WriteBe16(packet, 4, 16);
        WriteBe16(packet, 6, 1);
        WriteBe32(packet, 8, operation);
        WriteBe32(packet, 12, 1);
        Array.Copy(body, 0, packet, 16, body.Length);
        ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None)
            .Wait(8000);
    }

    // ------------------------------------------------------------------ 解包
    private void Feed(byte[] chunk)
    {
        int remaining = DanmakuMessageLimits.MaxExpandedBytes;
        FeedBounded(chunk, 0, ref remaining);
    }

    private void FeedBounded(byte[] chunk, int depth, ref int remaining)
    {
        if (depth > DanmakuMessageLimits.MaxCompressionDepth) throw new InvalidDataException("弹幕压缩嵌套过深");
        DanmakuMessageLimits.CheckAppend(0, chunk.Length, remaining);
        remaining -= chunk.Length;
        int offset = 0;
        while (offset + 16 <= chunk.Length)
        {
            int packLen = Be32(chunk, offset);
            int headerLen = Be16(chunk, offset + 4);
            int ver = Be16(chunk, offset + 6);
            int op = Be32(chunk, offset + 8);
            if (headerLen < 16 || packLen < headerLen || packLen > chunk.Length - offset)
                throw new InvalidDataException("弹幕包头长度无效");
            byte[] body = new byte[packLen - headerLen];
            Array.Copy(chunk, offset + headerLen, body, 0, body.Length);
            offset += packLen;

            if (op == 8)
            {
                string reply = Encoding.UTF8.GetString(body);
                if (reply.IndexOf("\"code\":0") < 0) { PlatformHealth.Record("bili", "danmaku", "auth-required"); throw new System.Security.Authentication.AuthenticationException("认证被拒：" + PlatformHealth.ResponseSummary(reply)); }
                PlatformHealth.Record("bili", "danmaku", "ready");
                continue;
            }
            if (op != 5) continue;

            if (ver == 2) FeedBounded(Inflate(body), depth + 1, ref remaining);
            else if (ver == 3) Say("服务端改用了 brotli（ver=3），本版本解不了 —— 需要给 net48 补 BrotliSharpLib");
            else if (ver == 0 || ver == 1) Dispatch(Encoding.UTF8.GetString(body));
        }
    }

    internal static byte[] Inflate(byte[] zlib)
    {
        if (zlib == null || zlib.Length < 2) throw new InvalidDataException("zlib 数据不完整");
        using (MemoryStream input = new MemoryStream(zlib, 2, zlib.Length - 2))   // 跳过 2 字节 zlib 头
        using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
            return DanmakuMessageLimits.ReadExpanded(deflate);
    }

    private void Dispatch(string json)
    {
        Dictionary<string, object> root;
        try { root = Json(json); }
        catch (Exception) { return; }
        if (root == null) return;
        string cmd = Str(root, "cmd");
        if (cmd == null) return;
        int colon = cmd.IndexOf(':');
        if (colon > 0) cmd = cmd.Substring(0, colon);

        try
        {
            if (cmd == "DANMU_MSG") HandleDanmaku(root);
            else if (cmd == "SUPER_CHAT_MESSAGE") HandleSuperChat(root);
            else if (cmd == "SUPER_CHAT_MESSAGE_DELETE") HandleSuperChatDelete(root);
            else HandleLiveEvent(cmd, root);
        }
        catch (Exception) { }
    }

    private void HandleDanmaku(Dictionary<string, object> root)
    {
        System.Collections.IList info = List(Val(root, "info"));
        if (info == null || info.Count < 3) return;
        System.Collections.IList head = List(info[0]);
        if (head == null) return;

        DanmakuItem item = new DanmakuItem();
        item.Text = ToStr(info.Count > 1 ? info[1] : null);
        item.Mode = AtInt(head, 1, 1);
        item.FontSize = AtInt(head, 2, 25);
        item.Color = AtInt(head, 3, 16777215);
        item.TimestampMs = AtLong(head, 4, 0);
        item.Rnd = AtLong(head, 5, 0);
        int dmType = AtInt(head, 12, 0);
        item.IsEmoticon = dmType == 1;
        if (item.IsEmoticon)
        {
            Dictionary<string, object> emo = Dict(head.Count > 13 ? head[13] : null);
            if (emo != null)
            {
                item.EmoticonUrl = Str(emo, "url");
                item.EmoticonWidth = Int(emo, "width", 0);
                item.EmoticonHeight = Int(emo, "height", 0);
                object bulge = Val(emo, "bulge_display");
                item.LargeEmoticon = ToStr(bulge) == "1" || string.Equals(ToStr(bulge), "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        System.Collections.IList user = List(info.Count > 2 ? info[2] : null);
        if (user != null)
        {
            item.UserId = AtLong(user, 0, 0);
            item.UserName = ToStr(user.Count > 1 ? user[1] : null);
        }
        System.Collections.IList medal = List(info.Count > 3 ? info[3] : null);
        if (medal != null && medal.Count >= 5)
        {
            item.MedalLevel = AtInt(medal, 0, 0);
            item.MedalName = ToStr(medal[1]);
            item.MedalColor = AtInt(medal, 4, 0);
        }
        System.Collections.IList level = List(info.Count > 4 ? info[4] : null);
        if (level != null) item.UserLevel = AtInt(level, 0, 0);

        // 新结构里带头像与舰队等级
        Dictionary<string, object> mode = Dict(head.Count > 15 ? head[15] : null);
        if (mode != null)
        {
            Dictionary<string, object> mu = Dict(Val(mode, "user"));
            if (mu != null)
            {
                Dictionary<string, object> guard = Dict(Val(mu, "guard"));
                if (guard != null) item.GuardLevel = Int(guard, "level", 0);
                Dictionary<string, object> baseInfo = Dict(Val(mu, "base"));
                if (baseInfo != null && (item.UserName == null || item.UserName.Length == 0))
                    item.UserName = Str(baseInfo, "name");
            }
        }
        if (item.GuardLevel == 0 && info.Count > 7) item.GuardLevel = ToInt(info[7], 0);

        if (item.Text == null) item.Text = "";
        if (item.IsEmoticon && (item.EmoticonUrl == null || item.EmoticonUrl.Length == 0)) item.IsEmoticon = false;

        DanmakuHandler handler = DanmakuReceived;
        if (handler != null) handler(item);
    }

    private void HandleSuperChat(Dictionary<string, object> root)
    {
        Dictionary<string, object> data = Dict(Val(root, "data"));
        if (data == null) return;
        SuperChatItem item = new SuperChatItem();
        item.Id = Long(data, "id", 0);
        item.Price = Int(data, "price", 0);
        item.DurationSec = Int(data, "time", 60);
        item.StartTime = Long(data, "start_time", 0);
        item.EndTime = Long(data, "end_time", 0);
        item.Message = Str(data, "message");
        item.MessageTrans = Str(data, "message_trans");
        Dictionary<string, object> ui = Dict(Val(data, "user_info"));
        if (ui != null)
        {
            item.UserName = Str(ui, "uname");
            item.Face = Str(ui, "face");
            item.FaceFrame = Str(ui, "face_frame");
            item.UserLevel = Int(ui, "user_level", 0);
            item.GuardLevel = Int(ui, "guard_level", 0);
            item.NameColor = HexColor(Str(ui, "name_color"), item.NameColor);
        }
        Dictionary<string, object> medal = Dict(Val(data, "medal_info"));
        if (medal != null)
        {
            item.MedalLevel = Int(medal, "medal_level", 0);
            item.MedalName = Str(medal, "medal_name");
        }
        item.BackgroundColor = HexColor(Str(data, "background_color"), item.BackgroundColor);
        item.BackgroundBottomColor = HexColor(Str(data, "background_bottom_color"), item.BackgroundBottomColor);
        item.BackgroundColorStart = HexColor(Str(data, "background_color_start"), item.BackgroundColorStart);
        item.BackgroundColorEnd = HexColor(Str(data, "background_color_end"), item.BackgroundColorEnd);
        item.BackgroundPriceColor = HexColor(Str(data, "background_price_color"), item.BackgroundPriceColor);
        item.MessageFontColor = HexColor(Str(data, "message_font_color"), item.MessageFontColor);

        // 展示时长以服务端为准（实测 ¥2→5s、¥30→60s）；end_time 缺失就用 ArrivedAt+time 兜底
        DateTime now = DateTime.UtcNow;
        if (item.EndTime > item.StartTime && item.StartTime > 0)
        {
            long elapsed = ToUnixSeconds(now) - item.StartTime;
            if (elapsed < 0) elapsed = 0;
            item.ExpiresAt = now.AddSeconds(Math.Max(3, item.DurationSec - elapsed));
        }
        else item.ExpiresAt = now.AddSeconds(Math.Max(3, item.DurationSec));

        SuperChatHandler handler = SuperChatReceived;
        if (handler != null) handler(item);
    }

    private void HandleSuperChatDelete(Dictionary<string, object> root)
    {
        Dictionary<string, object> data = Dict(Val(root, "data"));
        System.Collections.IList ids = List(Val(data, "ids"));
        SuperChatDeleteHandler handler = SuperChatDeleted;
        if (handler == null || ids == null) return;
        var seen = new HashSet<long>();
        foreach (object value in ids)
        {
            long id;
            if (long.TryParse(ToStr(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                && id > 0 && seen.Add(id)) handler(id);
        }
    }

    // ------------------------------------------------------------------ HTTP / 签名 / JSON 小工具
    private string HttpGet(string url, string referer)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Timeout = 8000;
        request.ReadWriteTimeout = 8000;
        request.Proxy = null;
        request.UserAgent = Ua;
        request.CookieContainer = _jar;
        if (referer != null) request.Referer = referer;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Headers["Accept-Encoding"] = "gzip, deflate";
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private void LoadCookies()
    {
        try
        {
            if (_cookieFile == null || !File.Exists(_cookieFile)) return;
            foreach (string line in File.ReadAllLines(_cookieFile))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 7) continue;
                try
                {
                    string domain = parts[0].Trim();
                    string path = parts[2].Trim();
                    if (path.Length == 0) path = "/";
                    Cookie cookie = new Cookie(parts[5].Trim(), parts[6].Trim(), path, domain.TrimStart('.'));
                    cookie.Domain = domain;
                    _jar.Add(cookie);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    internal string CookieValue(string name)
    {
        try
        {
            foreach (Cookie cookie in _jar.GetCookies(new Uri("https://www.bilibili.com/")))
                if (string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase)) return cookie.Value;
        }
        catch (Exception) { }
        return null;
    }

    internal static string MixinKey(string shuffled)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < MixinTab.Length; i++)
            if (MixinTab[i] < shuffled.Length) builder.Append(shuffled[MixinTab[i]]);
        return builder.ToString();
    }

    /// 传入形如 "a=1&b=2"，返回追加了 wts 与 w_rid 的查询串。
    internal string Sign(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string piece in query.Split('&'))
        {
            if (piece.Length == 0) continue;
            int eq = piece.IndexOf('=');
            if (eq < 0) pairs.Add(new KeyValuePair<string, string>(piece, ""));
            else pairs.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(piece.Substring(0, eq)),
                Uri.UnescapeDataString(piece.Substring(eq + 1))));
        }
        pairs.Add(new KeyValuePair<string, string>("wts",
            ToUnixSeconds(DateTime.UtcNow).ToString(CultureInfo.InvariantCulture)));
        pairs.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
        { return string.CompareOrdinal(a.Key, b.Key); });

        StringBuilder canonical = new StringBuilder();
        for (int i = 0; i < pairs.Count; i++)
        {
            string value = Filter(pairs[i].Value);
            if (canonical.Length > 0) canonical.Append('&');
            canonical.Append(Uri.EscapeDataString(pairs[i].Key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        string rid = Md5(canonical.ToString() + _wbiMixin);
        return "?" + canonical + "&w_rid=" + rid;
    }

    private static string Filter(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
            if ("!'()*".IndexOf(value[i]) < 0) builder.Append(value[i]);
        return builder.ToString();
    }

    internal static string Md5(string text)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }
    }

    internal static long ToUnixSeconds(DateTime time)
    {
        return (long)(time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    internal static Dictionary<string, object> Json(string text)
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
    }

    internal static object Val(Dictionary<string, object> node, string key)
    {
        object value;
        if (node == null || !node.TryGetValue(key, out value)) return null;
        return value;
    }

    internal static Dictionary<string, object> Dict(object node)
    {
        return node as Dictionary<string, object>;
    }

    internal static System.Collections.IList List(object node)
    {
        return node as System.Collections.IList;
    }

    internal static string Str(Dictionary<string, object> node, string key) { return ToStr(Val(node, key)); }
    internal static int Int(Dictionary<string, object> node, string key, int fallback)
    { return ToInt(Val(node, key), fallback); }
    internal static long Long(Dictionary<string, object> node, string key, long fallback)
    { return ToLong(Val(node, key), fallback); }
    internal static bool Bool(Dictionary<string, object> node, string key)
    { object v = Val(node, key); return v is bool && (bool)v; }

    internal static string ToStr(object value)
    {
        if (value == null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal static int ToInt(object value, int fallback)
    {
        if (value == null) return fallback;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return fallback; }
    }

    internal static long ToLong(object value, long fallback)
    {
        if (value == null) return fallback;
        try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return fallback; }
    }

    internal static int AtInt(System.Collections.IList list, int index, int fallback)
    {
        if (list == null || index < 0 || index >= list.Count) return fallback;
        return ToInt(list[index], fallback);
    }

    internal static long AtLong(System.Collections.IList list, int index, long fallback)
    {
        if (list == null || index < 0 || index >= list.Count) return fallback;
        return ToLong(list[index], fallback);
    }

    internal static Color HexColor(string text, Color fallback)
    {
        if (text == null) return fallback;
        text = text.Trim();
        if (text.Length == 0 || text[0] != '#') return fallback;
        if (text.Length != 7 && text.Length != 9) return fallback;
        try
        {
            int r = int.Parse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(r, g, b);
        }
        catch (Exception) { return fallback; }
    }

    internal static int Be32(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    internal static int Be16(byte[] data, int offset)
    {
        return (data[offset] << 8) | data[offset + 1];
    }

    internal static void WriteBe32(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24); data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8); data[offset + 3] = (byte)value;
    }

    internal static void WriteBe16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8); data[offset + 1] = (byte)value;
    }
}
