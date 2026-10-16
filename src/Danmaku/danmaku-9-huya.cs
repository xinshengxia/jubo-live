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
// 虎牙弹幕客户端（Web 协议，非开放平台）
//
// 走 `wss://cdnws.api.huya.com`：**无路径、无 query、无子协议、无签名、完全匿名**。
// ⚠️ 网上流传很广的「虎牙要算 MD5 的 wsSecret」是**错的** —— wsSecret 只出现在**推流地址**里
//    （mp.huya.com 返回的 sFlvAntiCode），弹幕这条路上根本没有它。
//
// 三个反直觉的坑（都是实测对照出来的，别照抄老实现）：
//  ① **真正的订阅包是 cmd 16（RegisterGroupReq）不是 cmd 1**。只发 cmd 1 一条弹幕都收不到，
//     服务器也不报错 —— 很多开源实现两个都发，所以很容易误判因果。
//  ② **组名必须用 lChannelId，不能用 lYyid**（= 主播的 yyuid）。用错服务器**静默回空组**、不报错。
//  ③ **cmd 22（V2 推送）也要处理**，6111 这类只走 V2；只认 cmd 7 会漏。
//
// 载荷是腾讯 **Tars/JCE** 二进制（不是 protobuf），规则见 TarsWriter/TarsReader：
//   · 无 varint —— 用的是「类型窄化」：INT32 的值落在 int16 范围就按 INT16 写、落在 int8 就按 INT8、0 只写 ZERO 头
//   · BYTE_ARRAY 是**两个头字节**：BYTES 头 + 一个 INT8 占位 + INT32 长度 + 数据（只读一个头会整体错位）
// --------------------------------------------------------------------------------------------

/// Tars 类型编号
internal static class TarsType
{
    public const int Int8 = 0, Int16 = 1, Int32 = 2, Int64 = 3, Float = 4, Double = 5,
        String1 = 6, String4 = 7, Map = 8, List = 9, StructBegin = 10, StructEnd = 11, Zero = 12, Bytes = 13;
}

internal sealed class TarsWriter
{
    private readonly List<byte> _buffer = new List<byte>(256);

    public byte[] ToArray() { return _buffer.ToArray(); }

    public TarsWriter Head(int tag, int type)
    {
        if (tag < 15) _buffer.Add((byte)(((tag << 4) & 0xF0) | type));
        else { _buffer.Add((byte)(0xF0 | type)); _buffer.Add((byte)tag); }
        return this;
    }

    public TarsWriter Raw(byte[] data) { if (data != null) _buffer.AddRange(data); return this; }

    public TarsWriter I8(int tag, int value)
    {
        if (value == 0) return Head(tag, TarsType.Zero);
        return Head(tag, TarsType.Int8).Raw(new byte[] { (byte)(value & 0xFF) });
    }

    public TarsWriter I16(int tag, int value)
    {
        if (value >= -128 && value <= 127) return I8(tag, value);
        return Head(tag, TarsType.Int16).Raw(BigEndian(value, 2));
    }

    public TarsWriter I32(int tag, int value)
    {
        if (value >= -32768 && value <= 32767) return I16(tag, value);
        return Head(tag, TarsType.Int32).Raw(BigEndian(value, 4));
    }

    public TarsWriter I64(int tag, long value)
    {
        if (value >= int.MinValue && value <= int.MaxValue) return I32(tag, (int)value);
        return Head(tag, TarsType.Int64).Raw(BigEndian(value, 8));
    }

    public TarsWriter Str(int tag, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value == null ? "" : value);
        if (bytes.Length <= 255) Head(tag, TarsType.String1).Raw(new byte[] { (byte)bytes.Length });
        else Head(tag, TarsType.String4).Raw(BigEndian(bytes.Length, 4));
        return Raw(bytes);
    }

    /// BYTES = BYTES 头 + INT8 占位 + INT32 长度 + 数据（**两个头字节**，少写一个就整体错位）
    public TarsWriter Bytes(int tag, byte[] data)
    {
        byte[] payload = data == null ? new byte[0] : data;
        Head(tag, TarsType.Bytes).Head(0, TarsType.Int8).I32(0, payload.Length);
        return Raw(payload);
    }

    public TarsWriter StructBegin(int tag) { return Head(tag, TarsType.StructBegin); }
    public TarsWriter StructEnd() { return Head(0, TarsType.StructEnd); }
    public TarsWriter ListHead(int tag, int count) { return Head(tag, TarsType.List).I32(0, count); }
    public TarsWriter MapHead(int tag, int count) { return Head(tag, TarsType.Map).I32(0, count); }

    private static byte[] BigEndian(long value, int size)
    {
        byte[] bytes = new byte[size];
        for (int i = size - 1; i >= 0; i--) { bytes[i] = (byte)(value & 0xFF); value >>= 8; }
        return bytes;
    }
}

internal sealed class TarsReader
{
    private readonly byte[] _data;
    private int _pos;
    private bool _failed;
    private const int MaxDepth = 64;

    public TarsReader(byte[] data) { _data = data ?? new byte[0]; }
    public int Position { get { return _pos; } }
    public bool AtEnd { get { return _pos >= _data.Length; } }

    // 未知类型可能是解析错位，不能猜测其长度；仅中止当前 reader。
    private bool Abort() { _failed = true; _pos = _data.Length; return false; }
    private bool Require(int count)
    {
        return !_failed && count >= 0 && count <= _data.Length - _pos || Abort();
    }

    public int Head(out int tag)
    {
        tag = -1;
        if (!Require(1)) return -1;
        int head = _data[_pos++];
        tag = head >> 4;
        if (tag == 15)
        {
            if (!Require(1)) { tag = -1; return -1; }
            tag = _data[_pos++];
        }
        return head & 15;
    }

    public int PeekType(out int tag)
    {
        int save = _pos;
        int type = Head(out tag);
        if (!_failed) _pos = save;
        return type;
    }

    public void Skip(int type) { TrySkip(type); }
    public bool TrySkip(int type) { return TrySkipValue(type, 0); }
    private bool Advance(int count)
    {
        if (!Require(count)) return false;
        _pos += count;
        return true;
    }

    private bool TrySkipValue(int type, int depth)
    {
        if (_failed || depth > MaxDepth) return Abort();
        int count, tag;
        switch (type)
        {
            case TarsType.Int8: return Advance(1);
            case TarsType.Int16: return Advance(2);
            case TarsType.Int32:
            case TarsType.Float: return Advance(4);
            case TarsType.Int64:
            case TarsType.Double: return Advance(8);
            case TarsType.String1:
                if (!Require(1)) return false;
                count = _data[_pos++];
                return Advance(count);
            case TarsType.String4:
                if (!TryStringLength(out count)) return false;
                return Advance(count);
            case TarsType.Bytes:
                if (Head(out tag) != TarsType.Int8 || tag != 0) return Abort();
                if (!TryCount(out count)) return false;
                return Advance(count);
            case TarsType.StructBegin: return TrySkipToEnd(depth + 1);
            case TarsType.Zero:
            case TarsType.StructEnd: return true;
            case TarsType.Map:
            case TarsType.List:
                if (!TryCount(out count)) return false;
                // 每个元素至少一个头字节；先限制数量，再乘二，避免溢出/空转。
                if (type == TarsType.Map)
                {
                    if (count > (_data.Length - _pos) / 2) return Abort();
                    count *= 2;
                }
                for (int i = 0; i < count; i++)
                {
                    int element = Head(out tag);
                    if (element == TarsType.StructEnd || !TrySkipValue(element, depth + 1)) return Abort();
                }
                return true;
            default: return Abort();
        }
    }

    private bool TrySkipToEnd(int depth)
    {
        if (depth > MaxDepth) return Abort();
        while (!AtEnd)
        {
            int tag;
            int type = Head(out tag);
            if (type == TarsType.StructEnd) return true;
            if (!TrySkipValue(type, depth)) return false;
        }
        return Abort(); // 结构体缺少结束标记。
    }
    public void SkipToEnd() { TrySkipToEnd(0); }

    public bool SkipToTag(int tag)
    {
        while (!AtEnd && !_failed)
        {
            int currentTag;
            int type = PeekType(out currentTag);
            if (type < 0 || type == TarsType.StructEnd) return false;
            if (currentTag >= tag) return currentTag == tag;
            Head(out currentTag);
            if (!TrySkip(type)) return false;
        }
        return false;
    }

    private bool TryNumber(int type, out long value)
    {
        value = 0;
        int size;
        switch (type)
        {
            case TarsType.Zero: return !_failed;
            case TarsType.Int8: size = 1; break;
            case TarsType.Int16: size = 2; break;
            case TarsType.Int32: size = 4; break;
            case TarsType.Int64: size = 8; break;
            default: TrySkip(type); return false;
        }
        if (!Require(size)) return false;
        for (int i = 0; i < size; i++) value = unchecked((value << 8) | _data[_pos++]);
        if (size == 1) value = unchecked((sbyte)value);
        else if (size == 2) value = unchecked((short)value);
        else if (size == 4) value = unchecked((int)value);
        return true;
    }

    private bool TryCount(out int count)
    {
        count = 0;
        int tag;
        int type = Head(out tag);
        long value;
        // 容器数量必须紧跟 tag0 数值，不能搜索到后续字段。
        if (tag != 0 || type != TarsType.Zero && type != TarsType.Int8
            && type != TarsType.Int16 && type != TarsType.Int32) return Abort();
        if (!TryNumber(type, out value) || value < 0 || value > _data.Length - _pos) return Abort();
        count = (int)value;
        return true;
    }

    public int ReadI32()
    {
        long value = Num(0, long.MinValue);
        if (value < int.MinValue || value > int.MaxValue) { Abort(); return 0; }
        return (int)value;
    }

    public long Num(int tag, long fallback)
    {
        if (!SkipToTag(tag)) return fallback;
        int currentTag;
        long value;
        return TryNumber(Head(out currentTag), out value) ? value : fallback;
    }

    private bool TryStringLength(out int count)
    {
        count = 0;
        if (!Require(4)) return false;
        uint value = 0;
        for (int i = 0; i < 4; i++) value = (value << 8) | _data[_pos++];
        if (value > (uint)(_data.Length - _pos)) return Abort();
        count = (int)value;
        return true;
    }

    public string Str(int tag, string fallback)
    {
        if (!SkipToTag(tag)) return fallback;
        int currentTag, count;
        int type = Head(out currentTag);
        if (type == TarsType.String1)
        {
            if (!Require(1)) return fallback;
            count = _data[_pos++];
        }
        else if (type == TarsType.String4)
        {
            if (!TryStringLength(out count)) return fallback;
        }
        else { TrySkip(type); return fallback; }
        if (!Require(count)) return fallback;
        string result = Encoding.UTF8.GetString(_data, _pos, count);
        _pos += count;
        return result;
    }

    public byte[] Bytes(int tag, byte[] fallback)
    {
        if (!SkipToTag(tag)) return fallback;
        int currentTag, count;
        int type = Head(out currentTag);
        if (type != TarsType.Bytes) { TrySkip(type); return fallback; }
        if (Head(out currentTag) != TarsType.Int8 || currentTag != 0) { Abort(); return fallback; }
        if (!TryCount(out count) || !Require(count)) return fallback;
        byte[] result = new byte[count];
        Buffer.BlockCopy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }

    public bool EnterStruct(int tag)
    {
        if (!SkipToTag(tag)) return false;
        int currentTag;
        int type = Head(out currentTag);
        if (type == TarsType.StructBegin) return true;
        TrySkip(type);
        return false;
    }

    public bool EnterList(int tag, out int count)
    {
        count = 0;
        if (!SkipToTag(tag)) return false;
        int currentTag;
        int type = Head(out currentTag);
        if (type != TarsType.List) { TrySkip(type); return false; }
        return TryCount(out count);
    }

    public bool PeekIsStruct(int tag)
    {
        int save = _pos;
        bool result = false;
        if (SkipToTag(tag)) { int currentTag; result = Head(out currentTag) == TarsType.StructBegin; }
        if (!_failed) _pos = save;
        return result;
    }
}


internal sealed class HuyaDanmakuClient : IDanmakuClient
{
    public event DanmakuHandler DanmakuReceived;
    // 虎牙**没有 SC 等价物**（协议层查不到任何付费留言消息类型，见技术路线报告 §2.6），
    // 下面两个事件永远不会触发 —— 但 IDanmakuClient 要求有它们，所以显式声明并抑制未使用警告。
    // 刻意**不做**「大额礼物升格成 SC 卡片」的近似：那会让用户以为收到了付费留言。
#pragma warning disable 67
    public event SuperChatHandler SuperChatReceived;
    public event SuperChatDeleteHandler SuperChatDeleted;
#pragma warning restore 67
    public event StatusHandler StatusChanged;
    public event StatusHandler RoomInfo;

    // ---- 平台表情字典 ----
    //
    // 虎牙的表情是**固定表**，不需要任何网络请求 —— 映射硬编码在官方页面的 JS 里。
    // 2026-09-26 实测：从 https://a.msstatic.com/huya/main3/assets.modules-24e6950c_5cd87eb9.js
    // 里抽出 39 条，官方生成规则是：
    //     name = "[" + text + "]"                              → 弹幕文本里就是 "[笑哭]"
    //     url  = "//a.msstatic.com/huya/main/emot_png/" + code + ".png"
    // 抽查 4 个 URL 均 HTTP 200、image/png（1.1~1.3 KB）。
    // 解析与映射规则通过离线回归覆盖。
    //
    // 文本中包含方括号表情代码时，按官方映射替换对应图片。
    private readonly EmoticonDictionary _emoticons = new EmoticonDictionary();
    public EmoticonDictionary Emoticons { get { return _emoticons; } }
    public event Action EmoticonsLoaded;

    private const string HuyaEmoticonBase = "https:" + "//a.msstatic.com/huya/main/emot_png/";

    // ⚠️ 上面那行故意把 "https:" 和 "//..." 拆成两段拼。
    //    原因在测试侧：tests 的 StripCommentsAndStrings 曾经**先剥注释后剥字符串**，
    //    字符串里的 `//` 会被当行注释切掉、留下不配对引号，进而吃掉后面的真实代码。
    //    那个顺序 bug 已经修了，但拆开写能顺手避开同类工具的坑，也让 grep `//` 时更干净。

    /// 固定表情表：`[名称]:图片文件名`。**静态且只读** —— 它不随房间、时间、登录态变化。
    /// 抽成静态是为了让测试能直接验证映射（实例方法得反射构造客户端，没必要）。
    internal static readonly string[] HuyaEmoticonPairs = new string[]
    {
        "大笑:dx", "送花:sh", "偷笑:tx", "大哭:dk", "嘿哈:hh", "666:66",
        "感动:gd", "疑问:yw", "喜欢:xh", "奸笑:jx", "赞:zan", "可爱:ka",
        "傲慢:am", "开心:kx", "拜拜:88", "害羞:hx", "衰:zs", "吐血:pu",
        "嘴馋:zc", "生气:sq", "扶额:fe", "闭嘴:bz", "枯萎:kw", "嘘:xu",
        "笑哭:xk", "流汗:lh", "不看:bk", "哈欠:hq", "调皮:tp", "鬼脸:gl",
        "戳脸:cl", "大哥:dg", "困:kun", "拥抱:yb", "猪头:zt", "骷髅:kl",
        "臭臭:cc", "心动:xd", "刀:dao"
    };

    /// 从**虎牙官方页面收割到的**「名字 → 图片 URL」补充表。
    ///
    /// **为什么要它**：虎牙的表情**不止本地那 39 个**。前端 bundle 里的
    /// `{packageId:"0",name:"本地默认表情",emoticon:[...39 条]}` 只是**本地兜底包**，
    /// 真正的包由服务器下发（`wupui/getExpressionEmoticonPackage`）。
    /// 用户报的 `[满头问号]` 就在服务器包里，本地表没有 → 一直显示成文字。
    ///
    /// **为什么用收割而不是调接口**（这几条都实测过）：
    ///   · 手搓 Tars 报文 → 弹幕长连接：发出去没回包
    ///   · 手搓 Tars 报文 → HTTP 网关 `wup.huya.com`：POST 407（GET 200，同主机 → 服务器要求认证）
    ///   · `?json_func=方法名`：`{"@ret":-4}`（该入口只服务网关自身方法）
    ///   · 页面自带 SDK `hyTafNetWork.sendWup`：`{"bcode":0,"sdkcode":-20001}`，
    ///     且**调用期间没有任何网络请求发出** → 客户端侧拒绝；
    ///     按 `registerWup` 的源码自己注册响应类型后**仍然 -20001**
    ///   · 三个开源实现（hwenjie / BacooTang / biliup）都只处理 uri 1400
    ///
    /// **收割为什么可行**：虎牙页面把表情渲染成
    ///   `<img style="max-width: 56px;" src="…" alt="[满头问号]" data-emo="…">`
    /// —— `alt` 就是表情名。所以**匿名**打开房间页、轮询聊天区带 alt 的 img，
    /// 就能拿到平台自己用的那份映射。脚本：`tasks\…\scripts\harvest-emoticon-map.mjs`
    ///
    /// 格式：`名字|URL`；名字不带方括号（`Add` 会规范化）。
    /// 同名时**这张表覆盖本地 39 条** —— 实测 `[吐血]` 在两边都有但 URL 不同，
    /// 官方页面用的是服务器那份，两边保持一致。
    internal static readonly string[] HuyaHarvestedEmoticons = new string[]
    {
        // 2026-09-27 收割自虎牙官方页面的聊天区 DOM（匿名可访问），逐个验证过 HTTP 200。
        // 注意 [吐血]/[偷笑]/[大哥] 本地 39 条里也有，但官方页面用的是**服务器那份** URL ——
        // 这张表同名覆盖，和官方行为一致。
        "[满头问号]|https://fileserver.cdn.huya.com/web_admin_material_zip_url/92ad65721cac4f79beae2ae9b3d58d09/expressconfig/steam_3.png",
        "[吐血]|https://cdnfile2.msstatic.com/cdnfile/material_manage/web_base_material_16141735632789_pic.png",
        "[偷笑]|https://cdnfile2.msstatic.com/cdnfile/material_manage/web_base_material_15790913779295_pic.png",
        "[大哥]|https://cdnfile2.msstatic.com/cdnfile/material_manage/web_base_material_16141742503895_pic.png",
        "[送花]|https://cdnfile2.msstatic.com/cdnfile/material_manage/web_base_material_16141715844572_pic.png"
    };

    /// 把收割表填进字典（同名覆盖本地 39 条，和官方页面行为一致）。
    internal static void FillHarvestedEmoticons(EmoticonDictionary dict)
    {
        for (int i = 0; i < HuyaHarvestedEmoticons.Length; i++)
        {
            int bar = HuyaHarvestedEmoticons[i].IndexOf('|');
            if (bar <= 0) continue;
            dict.Add(HuyaHarvestedEmoticons[i].Substring(0, bar),
                HuyaHarvestedEmoticons[i].Substring(bar + 1));
        }
    }
    /// 把固定表填进字典。静态：测试可以直接调用它验证映射，不用构造客户端。
    internal static void FillHuyaEmoticons(EmoticonDictionary dict)
    {
        for (int i = 0; i < HuyaEmoticonPairs.Length; i++)
        {
            int colon = HuyaEmoticonPairs[i].IndexOf(':');
            if (colon <= 0) continue;
            dict.Add(HuyaEmoticonPairs[i].Substring(0, colon),
                HuyaEmoticonBase + HuyaEmoticonPairs[i].Substring(colon + 1) + ".png");
        }
    }

    /// 建表情表。在 Run() 开头调一次即可 —— 没有网络、不会失败。
    ///
    /// 顺序很重要：**先填本地 39 条，再填收割表** —— 后者同名覆盖前者，
    /// 和官方页面行为一致（实测 `[吐血]` 两边都有但 URL 不同，官方用的是服务器那份）。
    private void BuildEmoticons()
    {
        if (_emoticons.Count > 0) return;
        FillHuyaEmoticons(_emoticons);
        FillHarvestedEmoticons(_emoticons);
        Action handler = EmoticonsLoaded;
        if (handler != null) handler();
    }

    private const string WsUrl = "wss://cdnws.api.huya.com";
    private const int HeartbeatSeconds = 60;

    private readonly string _roomInput;
    private long _lChannelId;
    private long _lYyid;
    private string _anchor = "";
    private string _title = "";
    private HuyaLiveState _liveState;

    private ClientWebSocket _socket;
    private volatile bool _stopping;
    private volatile bool _subscribed;   // 订阅成功判据（cmd17 回显非空组）；仅用于日志
    private HeartbeatAckWatch _heartbeatWatch = new HeartbeatAckWatch();

    public HuyaDanmakuClient(string room) { _roomInput = room; }

    public void Stop()
    {
        _stopping = true;
        try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }
    }

    private void Status(string text) { StatusHandler handler = StatusChanged; if (handler != null) handler(text); }
    private void Info(string text) { StatusHandler handler = RoomInfo; if (handler != null) handler(text); }

    private void Sleep(int ms)
    {
        int left = ms;
        while (left > 0 && !_stopping) { Thread.Sleep(Math.Min(250, left)); left -= 250; }
    }

    internal static string HexOf(byte[] data, int max)
    {
        if (data == null) return "(null)";
        StringBuilder builder = new StringBuilder();
        int count = Math.Min(max, data.Length);
        for (int i = 0; i < count; i++) builder.Append(data[i].ToString("x2"));
        return builder.ToString();
    }
    private static string Describe(Exception ex)
    {
        AggregateException aggregate = ex as AggregateException;
        if (aggregate != null && aggregate.InnerExceptions.Count > 0) ex = aggregate.InnerExceptions[0];
        WebSocketException ws = ex as WebSocketException;
        if (ws != null && ws.InnerException != null) return ws.InnerException.Message;
        return ex.Message;
    }

    public void Run()
    {
        BuildEmoticons();   // 固定表，无网络、不会失败 —— 放循环外，只建一次
        int attempt = 0;
        while (!_stopping)
        {
            attempt++;
            try
            {
                if (_lChannelId == 0 && !ResolveRoom()) { Sleep(10000); continue; }
                Connect();
                Send(CommandRegisterGroup(_lChannelId));
                _heartbeatWatch.Begin(Environment.TickCount);
                Send(CommandHeartbeat(_lYyid, _lChannelId, _lChannelId));
                // ⚠️ 在这里请求表情包，**不要**挂在「订阅回显」那条路里：
                //    实测那条路不一定走到（弹幕照样能进来，但 cmd 17 的回显没解析成功），
                //    挂那儿会出现「表情包请求静默没发出去」。发完订阅包就发，一定执行。
                RequestEmoticonPackages();
                _subscribed = false;
                attempt = 0;
                ReadLoop();
            }
            catch (Exception ex)
            {
                if (!_stopping) { PlatformHealth.Record("huya", "danmaku", PlatformHealth.Classify(ex)); if (!_stopping) Status("虎牙弹幕连接中断：" + Describe(ex)); }
            }
            finally { CloseQuietly(); }
            if (_stopping) break;
            int wait = Math.Min(30, 3 * attempt);
            if (wait < 3) wait = 3;
            Status("虎牙：" + wait + " 秒后重连…");
            Sleep(wait * 1000);
        }
    }

    // ------------------------------------------------------------------ 房间信息

    /// lYyid = 主播 yyuid（PC 页 hyPlayerConfig.yyid / privateHost）
    /// lChannelId = 房间频道 id（PC 页 hyPlayerConfig.uid / liveChannel）—— **订阅组必须用这个**
    private bool ResolveRoom()
    {
        string page = TryGet("https://www.huya.com/" + Uri.EscapeDataString(_roomInput), null);
        // 房间页会把一部分重播标成 liveStatus-on；与启动器共用手机接口优先的四态判据。
        // 查询失败才退回已经下载好的页面，不能再把 REPLAY 文案误报成直播中。
        string stateNote;
        RoomStateKind roomState = LiveHub.CheckRoomState("huya", _roomInput, out stateNote);
        _liveState = roomState == RoomStateKind.Unknown
            ? HuyaRoomState.Parse(page) : HuyaRoomState.FromRoomState(roomState);
        if (page != null)
        {
            long yyid = JsonLong(page, "\"yyid\":");
            if (yyid == 0) yyid = JsonLong(page, "\"privateHost\":");
            long channel = JsonLong(page, "\"uid\":");
            if (channel == 0) channel = JsonLong(page, "\"liveChannel\":");
            _lYyid = yyid;
            _lChannelId = channel;
        }
        // 页面解析兜底：m.huya.com 的移动版页面上有明确的 lChannelId / lYyid
        if (_lChannelId == 0 || _lYyid == 0)
        {
            string mobile = TryGet("https://m.huya.com/" + Uri.EscapeDataString(_roomInput), null);
            if (mobile != null)
            {
                if (_lChannelId == 0) _lChannelId = JsonLong(mobile, "\"lChannelId\":");
                if (_lYyid == 0) _lYyid = JsonLong(mobile, "\"lYyid\":");
            }
        }
        // 手机接口仅补充昵称和频道 ID；短号错误响应不能覆盖网页状态。
        string cache = TryGet("https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid="
            + Uri.EscapeDataString(_roomInput), null);
        if (cache != null)
        {
            _anchor = JsonString(cache, "\"nick\":");
            if (_lChannelId == 0) _lChannelId = JsonLong(cache, "\"lChannelId\":");
            if (_lYyid == 0) _lYyid = JsonLong(cache, "\"lYyid\":");
        }

        if (_lChannelId == 0)
        {
            Status("虎牙：拿不到房间 " + _roomInput + " 的 lChannelId，10 秒后重试");
            return false;
        }
        Info("虎牙 " + _roomInput + (string.IsNullOrEmpty(_anchor) ? "" : " · " + _anchor)
            + (string.IsNullOrEmpty(_title) ? "" : " · " + _title)
            + HuyaRoomState.Suffix(_liveState));
        return true;
    }

    private static string TryGet(string url, string referer)
    {
        try { return LiveHub.HttpGetText(url, referer); }
        catch (Exception) { return null; }
    }

    private static long JsonLong(string text, string key)
    {
        int at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return 0;
        int start = at + key.Length;
        while (start < text.Length && (text[start] == ' ' || text[start] == '"')) start++;
        int end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        if (end == start) return 0;
        long value;
        return long.TryParse(text.Substring(start, end - start), out value) ? value : 0;
    }

    private static string JsonString(string text, string key)
    {
        int at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + key.Length;
        while (start < text.Length && text[start] == ' ') start++;
        if (start >= text.Length || text[start] != '"') return null;
        start++;
        int end = start;
        while (end < text.Length && text[end] != '"')
        {
            if (text[end] == '\\') end++;
            end++;
        }
        if (end >= text.Length) return null;
        string raw = text.Substring(start, end - start);
        // 虎牙的 JSON 里中文是 \uXXXX 转义的，不还原就会在界面上显示成 "Zz1tai\u59ff\u6001"
        return Regex.Replace(raw, @"\\u([0-9a-fA-F]{4})", delegate (Match m)
        {
            return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString();
        });
    }

    // ------------------------------------------------------------------ 连接

    private void Connect()
    {
        Status("虎牙：正在连接弹幕服务器…");
        _socket = new ClientWebSocket();
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using (CancellationTokenSource cts = new CancellationTokenSource(12000))
            _socket.ConnectAsync(new Uri(WsUrl), cts.Token).Wait();
        _lastFrameTick = Environment.TickCount;
        _frameCount = 0;
        _heartbeatWatch = new HeartbeatAckWatch();
        PlatformHealth.Record("huya", "danmaku", "ready");
        Status("虎牙：已连接，正在订阅 chat:" + _lChannelId);
    }

    private void CloseQuietly()
    {
        try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }
        _socket = null;
    }

    private readonly object _sendGate = new object();
    private void Send(byte[] packet) { DanmakuSocket.Send(_socket, packet, _sendGate); }

    // ------------------------------------------------------------------ 组包

    /// cmd 16 EWSCmdC2S_RegisterGroupReq
    /// ⚠️ 这才是**真正的订阅包**；cmd 1（RegisterReq）对弹幕不起作用，服务器不报错但也不推。
    /// ⚠️ 组名必须用 **lChannelId**；用 lYyid 服务器会**静默回空组**。
    internal static byte[] CommandRegisterGroup(long lChannelId)
    {
        TarsWriter groups = new TarsWriter();
        groups.ListHead(0, 2);
        groups.Str(0, "live:" + lChannelId);
        groups.Str(0, "chat:" + lChannelId);
        groups.Str(1, "");                    // sToken：匿名留空

        TarsWriter command = new TarsWriter();
        command.I32(0, 16);                   // iCmdType
        command.Bytes(1, groups.ToArray());   // vData
        return command.ToArray();
    }

    /// cmd 3 EWSCmd_WupReq —— 心跳 WUP{onlineui, OnUserHeartBeat}
    internal static byte[] CommandHeartbeat(long lYyid, long lChannelId, long lSubChannelId)
    {
        TarsWriter body = new TarsWriter();
        body.StructBegin(0);                  // tUserInfo
        body.I64(0, lYyid);
        body.Str(1, "");
        body.Str(2, "");
        body.Str(3, "adr_wap");               // sHuYaUA
        body.Str(4, "");
        body.I32(5, 0);
        body.StructEnd();
        body.I64(1, lChannelId);
        body.I64(2, lSubChannelId);
        body.I64(3, 0);
        body.I64(4, lYyid);
        body.I8(5, 1);
        body.I32(6, 1);
        body.I32(7, 0);
        body.I32(8, 0);
        body.I32(9, 0);
        body.I32(10, 0);

        TarsWriter request = new TarsWriter();
        request.I16(1, 3);                    // iVersion
        request.I8(2, 0);                     // cPacketType
        request.I32(3, 0);                    // iMessageType
        request.I32(4, 0);                    // iRequestId
        request.Str(5, "onlineui");           // sServantName
        request.Str(6, "OnUserHeartBeat");    // sFuncName
        TarsWriter map = new TarsWriter();
        map.MapHead(0, 1).Str(0, "tReq").Bytes(1, body.ToArray());
        request.Bytes(7, map.ToArray());      // sBuffer
        request.I32(8, 0);                    // iTimeout
        request.MapHead(9, 0);                // context
        request.MapHead(10, 0);               // status

        byte[] payload = request.ToArray();
        byte[] framed = new byte[4 + payload.Length];
        int total = 4 + payload.Length;
        framed[0] = (byte)((total >> 24) & 0xFF);
        framed[1] = (byte)((total >> 16) & 0xFF);
        framed[2] = (byte)((total >> 8) & 0xFF);
        framed[3] = (byte)(total & 0xFF);
        Buffer.BlockCopy(payload, 0, framed, 4, payload.Length);

        TarsWriter command = new TarsWriter();
        command.I32(0, 3);
        command.Bytes(1, framed);
        return command.ToArray();
    }

    // ------------------------------------------------------------------ 收包

    private void ReadLoop()
    {
        byte[] chunk = new byte[64 * 1024];

        // ⚠️ 心跳必须走**独立线程**，绝不能再拿「带超时的 CTS 去取消 ReceiveAsync」——
        //    那是「安静房间永远没有弹幕」的根因（2026-09-30 隔离复现）：
        //      · ClientWebSocket **一旦取消 ReceiveAsync 就把整条连接置为 Aborted**；
        //      · 之后每次接收都立刻抛 AggregateException(...)，原来被 `catch (AggregateException) { }`
        //        无声吞掉 → 死循环：不重连、不报错、弹幕再也没有，还空转烧满一个核。
        //    隔离实验（服务器每 5 秒发一条 = 安静房间，30 秒窗口）：
        //      旧写法  收到 0 条，finalState=Aborted
        //      新写法  收到 7 条，finalState=Open
        //    热门房间为什么看不出来：帧间隔 < 3 秒，超时永远不触发（实测 102411 ≈ 82 条/分）。
        ClientWebSocket socket = _socket;
        HeartbeatAckWatch watch = _heartbeatWatch;
        Thread heartbeat = new Thread(delegate () { HeartbeatLoop(socket, watch); });
        heartbeat.IsBackground = true;
        heartbeat.Name = "huya-heartbeat";
        heartbeat.Start();

        while (!_stopping)
        {
            byte[] buffer;
            try
            {
                buffer = DanmakuSocket.ReceiveMessage(socket, chunk);
                if (buffer == null)
                {
                    Status("虎牙：服务端关闭了连接");
                    return;
                }
            }
            catch (AggregateException ex)
            {
                // 连接真的断了就把异常抛给 Run()（它会重连）—— 不能再无声吞掉：
                // 「吞掉」正是上面那个 bug 的后半段（死循环 + 永不重连）。
                Exception inner = ex.InnerExceptions.Count > 0 ? ex.InnerExceptions[0] : ex;
                if (!IsSocketOpen(socket)) throw inner;
                Status("虎牙：接收出错（" + Describe(inner) + "），继续");
                Sleep(200);
                continue;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            if (buffer.Length == 0) continue;

            try { Dispatch(buffer, buffer.Length); }
            catch (Exception ex) { Status("虎牙：解析出错 " + ex.Message); }
        }
    }

    /// socket 是否还活着（Aborted / Closed 都算死）
    private static bool IsSocketOpen(ClientWebSocket socket)
    {
        try { return socket != null && socket.State == WebSocketState.Open; }
        catch (Exception) { return false; }
    }

    /// 心跳独立线程：只发心跳、不碰接收 —— 所以接收端可以安全地无限期阻塞。
    ///
    /// 静默帧观测仍只记日志；只有已发送的心跳连续两次缺 cmd4 才主动重连。
    /// 2026-09-30：102411/a16789 按生产 60 秒间隔各实测 3 发 3 回，延迟 0–0.1 秒。
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
                DanmakuHost.Log("虎牙：连续两次心跳在 15 秒内未收到 cmd4，主动重连");
                DanmakuSocket.Abort(socket);
                return;
            }
            if (unchecked(tick - lastBeat) >= HeartbeatSeconds * 1000)
            {
                lastBeat = tick;
                watch.Begin(tick);
                try { DanmakuSocket.Send(socket, CommandHeartbeat(_lYyid, _lChannelId, _lChannelId), _sendGate); }
                catch (Exception ex)
                {
                    if (!_stopping) DanmakuHost.Log("虎牙：心跳发送失败，重连：" + Describe(ex));
                    return;
                }
            }
            int silentMs = unchecked(Environment.TickCount - _lastFrameTick);   // 回绕安全
            if (SilenceWatch.ShouldWarn(silentMs, lastWarn, now))
            {
                lastWarn = now;
                DanmakuHost.Log(SilenceWatch.Describe("虎牙", silentMs, _frameCount));
            }
        }
    }

    /// 上次收到任何帧的时刻（Environment.TickCount，接收线程写、心跳线程读；int 读写天然原子）
    private volatile int _lastFrameTick = Environment.TickCount;

    /// 本次连接累计收到的帧数（仅用于日志）
    private int _frameCount;

    internal void Dispatch(byte[] data, int length)
    {
        // 观测用：只记「刚收到过帧」，不参与任何判断
        _lastFrameTick = Environment.TickCount;
        _frameCount++;
        byte[] slice = new byte[length];
        Buffer.BlockCopy(data, 0, slice, 0, length);
        TarsReader command = new TarsReader(slice);
        int cmdType = (int)command.Num(0, -1);
        if (cmdType == 4) { _heartbeatWatch.Acknowledge(); return; }
        byte[] vData = command.Bytes(1, null);
        if (vData == null) return;

        // 临时诊断：把收到的 cmd 类型报出来
        switch (cmdType)
        {
            case 0:                       // 空包噪声（150 秒实测 347 个），直接丢
                return;
            case 7:                       // V1 推送主通道
                HandlePush(new TarsReader(vData));
                return;
            case 22:                      // V2 推送：{sGroupId, list<{iUri, sMsg, lMsgId}>}
                HandlePushV2(new TarsReader(vData));
                return;
            case 17:                      // RegisterGroupRsp：**回显成功加入的组 = 订阅成功判据**
                HandleRegisterGroupRsp(new TarsReader(vData));
                return;
            default:
                // 别的 cmd 各记一次 —— 我们发出去的 WUP 请求（cmd 2）如果走这条连接回来，
                // 就会以某个 cmd 出现。不知道是哪个号时，先把「有哪些」记下来。
                if (_seenCmds.Add(cmdType) && _seenCmds.Count <= 30)
                    DanmakuHost.Log("虎牙：收到未处理的 cmd=" + cmdType + "（" + vData.Length
                        + " 字节）头部 " + HexHead(vData, 24));
                return;
        }
    }

    /// 收过的 cmd 类型（每个只记一次）
    private readonly HashSet<int> _seenCmds = new HashSet<int>();

    /// 报文头 16 进制（只用于日志诊断）
    private static string HexHead(byte[] data, int count)
    {
        if (data == null) return "";
        StringBuilder sb = new StringBuilder();
        int n = Math.Min(count, data.Length);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
        return sb.ToString();
    }

    private void HandleRegisterGroupRsp(TarsReader reader)
    {
        // ⚠️ 组列表在 **tag1**：报文开头是 `0c`（tag0/ZERO 的 iResult），然后 `19`（tag1/LIST）。
        // 一开始按 tag0 解，结果一条弹幕都收不到 —— EnterList 直接返回 false 静默退出了。
        int count;
        if (!reader.EnterList(1, out count)) return;
        List<string> groups = new List<string>();
        for (int i = 0; i < count; i++)
        {
            // ⚠️ **不要**在这里再 Head() 一次。list<string> 的每个元素本身就是
            //    `Str(0, 值)` 的完整编码（头 + 长度 + 数据）；先吃一次头会让紧随其后的
            //    `Str(0, "")` 把「长度字节」当成新头读 → 撞出 Tars 未定义类型（实测 14/15），
            //    组名永远解析成空串，而且每条连接白抛一个异常（日志里那句
            //    「收到未定义的类型」就是它，跟弹幕本身无关）。
            //    2026-09-30 用真实报文对照过（虎牙 a16789）：
            //      原始 0c 19 00 01 06 0f + "chat:1271256459"（共 21 字节）
            //      带多余 Head → ""，并报未知类型 15
            //      去掉多余 Head → "chat:1271256459"，无未知类型
            groups.Add(reader.Str(0, ""));
        }
        if (groups.Count == 0)
        {
            // 实测：组名用错 id 时服务器**静默回空列表**、不报错 —— 这是最容易踩的坑
            Status("虎牙：订阅返回空组，lChannelId 可能取错了（当前 " + _lChannelId + "）");
            return;
        }
        _subscribed = true;
        Status("虎牙：订阅成功 " + string.Join(",", groups.ToArray()) + (_subscribed ? "" : ""));
        RequestEmoticonPackages();   // 订阅成功后才请求表情包
    }

    // ---------------------------------------------------------------- 表情包（WUP RPC）
    //
    // 虎牙的表情**不止那 39 个**。前端 bundle 里的
    //     { packageId:"0", name:"本地默认表情", emoticon:[...39 条] }
    // 只是**本地兜底包**；真正的包由服务器下发：
    //     a.Z.send("wupui","getExpressionEmoticonPackage", req, GetExpressionEmoticonPackageRsp)
    // 用户报的 `[满头问号]` `[癫狂]` `[牛的]` `[itai3]` 都在服务器那些包里。
    //
    // 结构体（从 bundle 的 Tars 定义里抽出来的，字段标签一一对应）：
    //   GetExpressionEmoticonPackageReq  {0:tId(UserId), 1:lPid(i64), 2:iGid(i32), 3:iSceneType(i32)}
    //   GetExpressionEmoticonPackageRsp  {0:sMessage(str), 1:vPackage(vec), 2:vAvailable(vec<str>), 3:vTabInfo(vec)}
    //   ExpressionEmoticonPackage        {0:sPackageId, 1:sName, 2:iType, 3:sCoverUrl,
    //                                     4:vEmoticon(vec), 5:vUnavailEmoticon(vec), 6:iIsLocked, 7:sLockedMsg}
    //   ExpressionEmoticon               {0:sId, 1:sName, 2:sEscape, 3:sUrl, 4:sExtraUrl, 5:iType, ...}
    //   UserId                           {0:lUid(i64), 1:sGuid, 2:sToken, 3:sHuYaUA, 4:sCookie, ...}
    //
    // ⚠️ 走**弹幕长连接**（cmd 2 = WUP 请求）而不是 HTTP 网关：实测 `https://wup.huya.com`
    //    从本机一律 407（被代理挡），而这条长连接是已经跑通的。
    // ⚠️ 整条链路**必须能失败**：拉不到就保持那 39 条，绝不影响弹幕接收。
    private int _wupRequestId = 1;

    private void RequestEmoticonPackages()
    {
        try
        {
            TarsWriter req = new TarsWriter();
            req.StructBegin(0);                    // tId: UserId
            req.I64(0, _lYyid);                    // lUid
            req.Str(1, "");                        // sGuid
            req.Str(2, "");                        // sToken（匿名看播没有 token，留空）
            req.Str(3, "webh5");                   // sHuYaUA
            req.Str(4, "");                        // sCookie
            req.I32(5, 0);                         // iTokenType
            req.Str(6, "");                        // sDeviceInfo
            req.Str(7, "");                        // sQIMEI
            req.StructEnd();
            req.I64(1, 0);                         // lPid
            req.I32(2, 0);                         // iGid
            req.I32(3, 1);                         // iSceneType = LiveRoom

            TarsWriter wup = new TarsWriter();
            wup.I16(1, 3);                         // iVersion
            wup.I8(2, 0);                          // cPacketType（普通请求）
            wup.I32(3, 0);                         // iMessageType（0 = 请求）
            wup.I32(4, _wupRequestId);             // iRequestId
            wup.Str(5, "wupui");                   // sServantName
            wup.Str(6, "getExpressionEmoticonPackage");
            wup.Bytes(7, req.ToArray());           // sBuffer
            wup.I32(8, 5000);                      // iTimeout
            wup.MapHead(9, 0);                     // context（空）
            wup.MapHead(10, 0);                    // status（空）

            TarsWriter cmd = new TarsWriter();
            cmd.I32(0, 2);                         // iCmdType = WUP 请求
            cmd.I32(1, _wupRequestId);
            cmd.Bytes(2, wup.ToArray());
            Send(cmd.ToArray());
            DanmakuHost.Log("虎牙：已请求表情包（getExpressionEmoticonPackage，reqId=" + _wupRequestId + "）");
            _wupRequestId++;
        }
        catch (Exception ex)
        {
            Diagnostics.Report("huya-emoticon-req", ex);
            DanmakuHost.Log("虎牙：表情包请求发送失败（" + ex.GetType().Name + "），保持内置 39 条");
        }
    }

    /// cmd 7：WSPushMessage{ePushType tag0, iUri tag1, sMsg tag2, iProtocolType tag3}
    private void HandlePush(TarsReader reader)
    {
        long uri = reader.Num(1, -1);
        byte[] message = reader.Bytes(2, null);
        if (message == null) return;
        HandleUri((int)uri, message);
    }

    private void HandlePushV2(TarsReader reader)
    {
        int count;
        if (!reader.EnterList(1, out count)) return;
        for (int i = 0; i < count; i++)
        {
            int tag;
            reader.Head(out tag);                 // 每个元素是个结构体
            TarsReader item = reader;
            long uri = item.Num(0, -1);
            byte[] message = item.Bytes(1, null);
            item.Num(2, 0);                       // lMsgId
            if (message != null) HandleUri((int)uri, message);
            // 跳出这个结构体
            item.SkipToEnd();
        }
    }

    private void HandleUri(int uri, byte[] message)
    {
        if (uri == 1400) { HandleMessageNotice(message); return; }
        // 6501 礼物 / 6110 6111 进场 / 8006 在线数 —— 都不渲染。
        // 虎牙没有 SC 等价物，所以不做「大额礼物升格成 SC 卡片」这种近似：
        // 那会让用户以为收到了付费留言（详见技术路线报告 §2.6）。
        //
        // ⚠️ 但**必须把这些 uri 记下来**：虎牙除了 uri 1400（普通弹幕）之外还有别的
        //    承载内容的通道 —— 官方示例里就有 `getExpressionEmoticonNotice`
        //    且注释写着「大表情，梗」。我们只处理 1400，所以**大表情/梗整条通道是空的**。
        //    用户报的 `[满头问号]` 极可能就走那条（内联 `[xxx]` 表里确实没有它，
        //    但那只证明了「不在内联表里」，证明不了「平台没有这个表情」——
        //    我上次就是在这里下错了结论）。
        //    这里把见过的 uri 各记一次（上限 40），下次直接看日志就知道该补哪个。
        if (_seenUris.Add(uri) && _seenUris.Count <= 40)
        {
            DanmakuHost.Log("虎牙：收到未处理的 uri=" + uri + "（" + message.Length
                + " 字节）—— 若不是已知的礼物/进场类，可能是有内容的通道，值得解析");
            DumpUnknownUri(uri, message);
        }
    }

    /// 把没见过 uri 的**原始报文体**落盘，供离线分析「这条通道里到底是什么」。
    ///
    /// 为什么需要它：虎牙官方示例（`huya-ext/miniapp`）里明写大表情走
    /// `getExpressionEmoticonNotice`（注释「大表情，梗」），但那是 **JSON** 协议；
    /// 我们用的是 **Tars** 协议（数字 uri）。查了三个开源实现
    /// （hwenjie/huya_danmu、BacooTang/huya-danmu、biliup-start/VideoPlayer）
    /// **也都只处理 1400**，没有现成的 uri 对照表 —— 只能实测反推。
    ///
    /// 落盘而不是只打日志：报文体是二进制 Tars，日志里没法看；落盘后能反复用
    /// 不同姿势解析，不用每次重跑一小时。
    private static void DumpUnknownUri(int uri, byte[] message)
    {
        try
        {
            string dir = System.IO.Path.Combine(LiveHub.RuntimeDir, "huya-uri");
            System.IO.Directory.CreateDirectory(dir);
            // 上限 512 KB：原来 16 KB 把 **uri 6220（表情包下发）** 截断了 ——
            // 那个报文实际大于 16 KB，截断后只看得到开头两个 URL，
            // 差点因此错过「表情包就在这条连接上」这个事实。
            int n = Math.Min(message.Length, 512 * 1024);
            byte[] head = new byte[n];
            Buffer.BlockCopy(message, 0, head, 0, n);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, uri + ".bin"), head);
        }
        catch (Exception ex) { Diagnostics.Report("huya-uri-dump", ex); }
    }

    /// 见过的 uri（每个只记一次，别刷屏）
    private readonly HashSet<int> _seenUris = new HashSet<int>();

    /// uri 1400 MessageNotice —— 弹幕
    ///   tag0 tUserInfo{ tag2 sNickName, tag4 sAvatarUrl, tag5 iNobleLevel }
    ///   tag3 sContent（正文）
    ///   tag5 ContentFormat{ tag0 iFontColor }
    internal DanmakuItem ParseMessageNotice(byte[] message)
    {
        TarsReader reader = new TarsReader(message);

        DanmakuItem item = new DanmakuItem();
        if (reader.PeekIsStruct(0))
        {
            reader.EnterStruct(0);
            item.UserName = reader.Str(2, null);
            item.GuardLevel = (int)reader.Num(5, 0);      // iNobleLevel（贵族等级）
            // ⚠️ **必须吃掉 STRUCTEND** 再出去。
            //    少这一步的话游标停在结构体内部，后面取 sContent(tag3) 会先在 tUserInfo 里
            //    找到 tag3（iGender）——拿到的不是弹幕正文；更常见的是直接撞上 STRUCTEND 返回 null，
            //    结果**一条弹幕都渲染不出来**（真实踩过：DANMU=0 但包明明收到了）。
            reader.SkipToEnd();
        }
        item.Text = reader.Str(3, null);
        if (string.IsNullOrEmpty(item.Text)) return null;

        // ContentFormat 是可选的（很多弹幕没有），用 PeekIsStruct 判在不在
        TarsReader format = new TarsReader(message);
        if (format.PeekIsStruct(5))
        {
            format.EnterStruct(5);
            long color = format.Num(0, 0xFFFFFF);
            if (color > 0) item.Color = (int)(color & 0xFFFFFF);
        }
        if (item.Color == 0) item.Color = 0xFFFFFF;

        item.Mode = 1;                 // 虎牙的 iShowMode 语义不明确，一律当滚动
        item.FontSize = 25;
        item.TimestampMs = DouyuDanmakuClient.ToUnixMs(DateTime.UtcNow);
        return item;
    }

    private void HandleMessageNotice(byte[] message)
    {
        DanmakuHandler handler = DanmakuReceived;
        if (handler == null) return;
        DanmakuItem item = ParseMessageNotice(message);
        if (item != null) handler(item);
    }
}
