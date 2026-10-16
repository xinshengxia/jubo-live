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
// 多平台弹幕接入：斗鱼 / 虎牙 / 抖音
//
// 设计：三个平台各自一个 client，但都实现同一个 IDanmakuClient（事件签名和 B站 的
// BiliDanmakuClient 完全一致），所以**渲染 / SC 卡片 / 设置体系一行都不用改** ——
// DanmakuHost 只把 new BiliDanmakuClient(...) 换成 DanmakuClientFactory.Create(...)。
//
// 本轮先落地**斗鱼**（唯一有真 SC 的平台、协议最简单：无签名/无压缩/无 TLS）。
// 虎牙（Tars 二进制）与抖音（protobuf + 签名）的协议细节分别见
//   danmaku-9-huya.cs 与 danmaku-10-douyin.cs。
// 客户端留了同名占位，后续按同一模式补即可。
// --------------------------------------------------------------------------------------------

/// 三个平台共用的客户端契约。事件签名刻意和 BiliDanmakuClient 保持一致（见 danmaku-2-client.cs）。
internal interface IDanmakuClient
{
    event DanmakuHandler DanmakuReceived;
    event SuperChatHandler SuperChatReceived;
    event SuperChatDeleteHandler SuperChatDeleted;
    event StatusHandler StatusChanged;
    event StatusHandler RoomInfo;
    void Run();
    void Stop();

    // ---- 平台表情字典（`[大笑]` 这类方括号代码 → 图片）----
    //
    // 各平台的机制不同（B站 实测是 GetEmoticons 接口按房间拉；虎牙/斗鱼/抖音 待查），
    // 所以放在接口上、由各平台自己实现：
    //   · 拿到了就原地填进 Emoticons 并触发 EmoticonsLoaded
    //   · 拿不到/没有这个机制 → 返回空字典、永不触发，**不能因此影响弹幕接收**
    // 渲染器持有 Emoticons 的引用即可（内容原地填充），收到 EmoticonsLoaded 后清一次文本缓存。
    EmoticonDictionary Emoticons { get; }
    event Action EmoticonsLoaded;
}

// ============================================================================================
// 斗鱼
//
// 帧格式（真实服务器字节钉死的）：
//   [int32 LE L][int32 LE L][uint16 LE type][byte enc=0][byte res=0][UTF-8 body][0x00]
//   L = 整帧长度 - 4 = 8 + body 字节数
//   客户端发的 type = 689，服务端发的 = 690
//   ⚠️ 接收方向错 1 字节就全盘错位；发送方向服务端容忍 ±4（所以很多开源实现是错的也能跑）
//
// STT 序列化（官方规则）：
//   键值用 @= 分隔、数组用 / 分隔，值里的 @ → @A、/ → @S
//   ⚠️ 解码顺序不能反：先 @S → /，再 @A → @
// ============================================================================================
internal sealed class DouyuDanmakuClient : IDanmakuClient
{
    public event DanmakuHandler DanmakuReceived;
    public event SuperChatHandler SuperChatReceived;
    // 斗鱼**没有删除/撤回消息类型**（B站 有 SUPER_CHAT_MESSAGE_DELETE），所以这个事件永远不会触发 ——
    // SC 卡片只能靠 ExpiresAt 自己到期。接口要求有它，这里显式声明并抑制「未使用」警告。
#pragma warning disable 67
    public event SuperChatDeleteHandler SuperChatDeleted;
#pragma warning restore 67
    public event StatusHandler StatusChanged;
    public event StatusHandler RoomInfo;

    // ---- 平台表情字典 ----
    private const string DouyuEmoticonBase = "https://shark2.douyucdn.cn/front-publish/live-next-player-aside-master/assets/images/";

    /// 斗鱼固定表情表：`名称:文件名`（前缀统一，见 DouyuEmoticonBase）。
    /// 2026-09-26 从官方前端 JS 抽出（douyu-js-30.js）—— 三段拼起来的：
    ///   ① webpack 资源表 `"./wanpi.png":"f7deb"`
    ///   ② 各模块导出真实 URL `"f7deb":function(e,t){e.exports="https://.../wanpi_20e1a62.png"}`
    ///   ③ 名称表 `l={wanpi:"顽皮", ...}`
    /// 实测样本：`[顽皮]`（房间 100）。抽查 6 个 URL 均 HTTP 200 / image/png。
    /// 注意 `[梗已老实]` 这类**不在表里** —— 是服务端下发的新表情，会退回按文本显示。
    internal static readonly string[] DouyuEmoticonPairs = new string[]
    {
            "挨揍:aizou_233d6d5.png",
            "爱你:aini_a5ddae2.png",
            "奥利给:aoligei_31c928c.png",
            "白妹:baimei_cacf6bf.png",
            "拜托:baituo_c245f1b.png",
            "搬砖:banzhuan_d7f5046.png",
            "半遮面:banzhemian_8398018.png",
            "棒棒哒:bangbangda_8c71346.png",
            "宝宝:baobao_f96ccbf.png",
            "暴躁:baozao_13bfa14.png",
            "爆米花:baomihua_5d06296.png",
            "爆头:baotou_fd9f460.png",
            "被揍:beizou_0a5dcae.png",
            "被揍了:beizoule_d293f04.png",
            "比心:bixin_3d646bc.png",
            "闭嘴:bizui_6de2c1a.png",
            "憋说了:bieshuole_857bd72.png",
            "别毒奶:biedunai_f7f0d3b.png",
            "冰冻:bingdong_f9a60fc.png",
            "啵一个:boyige_8c92e3c.png",
            "不高兴:bugaoxing_69a7506.png",
            "不会吧:buhuiba_2daf56a.png",
            "财迷:caimi_470d018.png",
            "财神:caishen_8daad7a.png",
            "彩虹屁:caihongpi_9f9aacd.png",
            "草帽:caomao_b112004.png",
            "沉江:chenjiang_476b14a.png",
            "沉默:chenmo_a3e540a.png",
            "吃瓜:chigua_3c5cf0b.png",
            "吃瓜瓜:chiguagua_e899cfc.png",
            "吃惊:chijing_eccbf4e.png",
            "冲冲冲:chongchongchong_adaebe4.png",
            "冲鸭:chongya_ecd9639.png",
            "瞅瞅:chouchou_6174193.png",
            "锤子:chuizi_8e9f00b.png",
            "呲牙:ciya_cf0bfa7.png",
            "打劫:dajie_fcbb1b8.png",
            "打扰啦:daraola_390c6bb.png",
            "打扰了:daraole_a5759cc.png",
            "大鹌鹑:daanchun_edc3df6.png",
            "大喊:dahan_2f1197c.png",
            "大吼:dahou_ae463bf.png",
            "大叫:dajiao_87764e2.png",
            "大哭:daku_33bef26.png",
            "大马猴:damahou_dea6856.png",
            "大怒:danu_8e1ef86.png",
            "大气:daqi_23d6549.png",
            "大笑:daxiao_6c47e04.png",
            "逮捕:daibu_79a7c66.png",
            "刀刀刀:daodaodao_91401a2.png",
            "得意:deyi_13e47f1.png",
            "点赞:dianzan_dd6d5cf.png",
            "电击:dianji_3a49e4d.png",
            "调戏:tiaoxi_db30442.png",
            "顶呱呱:dingguagua_be7be27.png",
            "毒奶:dunai_9d43a6b.png",
            "发火:fahuo_feb5f60.png",
            "发狂:fakuang_e8b0fe2.png",
            "发怒:fanu_a495dd1.png",
            "反向烟:fanxiangyan_598ea74.png",
            "犯困:fankun_899c2ad.png",
            "肥宅:feizhai_0b4a9da.png",
            "愤怒:fennu_347668b.png",
            "扶墙:fuqiang_62332e4.png",
            "富贵饼:fuguibing_61f3914.png",
            "尬笑:gaxiao_7be0ed0.png",
            "尴尬:ganga_81fe320.png",
            "干杯:ganbei_285a5a8.png",
            "搞起:gaoqi_1e976db.png",
            "鸽了:gele_485007a.png",
            "隔离:geli_bf005f6.png",
            "给力嗷:geiliao_4b7e33b.png",
            "给爷冲:geiyechong_06dfdd3.png",
            "跟我来:genwolai_df982c8.png",
            "狗骨头:gougutou_12b4a87.png",
            "呱呱:guagua_bfc425e.png",
            "鼓掌:guzhang_92d5111.png",
            "鬼脸:guilian_8ff7c06.png",
            "哈喽:halou_7f3f7ed.png",
            "害怕:haipa_06a4846.png",
            "害羞:haixiu_af4869a.png",
            "憨憨:hanhan_eaef6ef.png",
            "好困:haokun_6d2c36c.png",
            "好色:haose_7c06b19.png",
            "好样的:haoyangde_2278fd9.png",
            "喝酒:hejiu_de1bb24.png",
            "嘿嘿嘿:heiheihei_b9e7abe.png",
            "哼:heng_5af96ca.png",
            "猴子:houzi_60b12c4.png",
            "护目镜:humujing_3d21d2a.png",
            "花屏:huaping_a50182f.png",
            "惶恐:huangkong_335afa8.png",
            "加油:jiayou_bd595a6.png",
            "加油啦:jiayoula_fcd9cd4.png",
            "僵尸:jiangshi_1b1ddae.png",
            "僵硬:jiangying_fffcdd6.png",
            "教科书:jiaokeshu_aaae255.png",
            "金牌:jinpai_a203913.png",
            "紧张:jinzhang_34a271e.png",
            "惊恐:jingkong_f13e2ef.png",
            "惊吓:jingxia_e562d67.png",
            "惊讶:jingya_afad4b7.png",
            "囧:jiong_4e7409a.png",
            "纠结:jiujie_1c190f2.png",
            "就这:jiuzhe_06834a8.png",
            "绝望:juewang_814d3d2.png",
            "开车:kaiche_76fddd9.png",
            "开心:kaixin_48bc14e.png",
            "可怜:kelian_94d7798.png",
            "恐惧:kongju_3e82fd1.png",
            "抠鼻孔:koubikong_f657eed.png",
            "口香糖:kouxiangtang_2a39913.png",
            "口罩:kouzhao_3c001e0.png",
            "哭:ku_e8925ea.png",
            "哭哭:kuku_a53b578.png",
            "酷酷的:kukude_a82c3b0.png",
            "困:kun_af9abc8.png",
            "辣眼睛:layanjing_4e42ecc.png",
            "篮球:lanqiu_38696bc.png",
            "乐呵:lehe_d4f8c0a.png",
            "雷击:leiji_9caeb0a.png",
            "冷:leng_c7f4173.png",
            "裂开:liekai_0586d68.png",
            "绿了:lvle_547dfaa.png",
            "略略略:lueluelue_3ed3916.png",
            "卖萌:maimeng_beabdab.png",
            "冒金光:maojinguang_a0d4c83.png",
            "没电了:meidianle_43af043.png",
            "没话说:meihuashuo_185fef8.png",
            "没问题:meiwenti_10e13f0.png",
            "萌化了:menghuale_f487d8d.png",
            "面膜:mianmo_79f97bd.png",
            "喵喵:miaomiao_ca4b1a8.png",
            "藐视:miaoshi_7a17616.png",
            "摸鱼:moyu_2312600.png",
            "奶茶:naicha_a9be78d.png",
            "奈斯:naisi_52035ac.png",
            "内伤:neishang_8d61783.png",
            "你瞅啥:nichousha_b321a84.png",
            "柠檬精:ningmengjing_3d86a78.png",
            "凝视:ningshi_aebc56b.png",
            "牛啤:niupi_c6b2a00.png",
            "扭捏:niunie_07f79c0.png",
            "怒:nu_9906980.png",
            "呕:ou_7daf6f4.png",
            "呕吐:outu_e3b4563.png",
            "喷火:penhuo_fad6380.png",
            "喷血:penxue_8145671.png",
            "喷子:penzi_d8b15f7.png",
            "膨胀:pengzhang_44b82a0.png",
            "拼了:pinle_de6cb4e.png",
            "期待:qidai_0cae4e2.png",
            "起立:qili_6f7420a.png",
            "气氛:qifen_50bbb00.png",
            "气哭:qiku_8fe158e.png",
            "潜水:qianshui_54ef2e5.png",
            "潜泳:qianyong_af06c05.png",
            "欠扁:qianbian_2e59b59.png",
            "欠揍:qianzou_0dac040.png",
            "亲亲:qinqin_2327d1d.png",
            "请表演:qingbiaoyan_03d4243.png",
            "庆祝:qingzhu_4e9364d.png",
            "骚猪:saozhu_55aa768.png",
            "傻了:shale_bdca416.png",
            "啥情况:shaqingkuang_ba241ae.png",
            "闪亮:shanliang_e13689c.png",
            "伤感:shanggan_1f58082.png",
            "伤心:shangxin_5c53586.png",
            "上课:shangke_2c5b877.png",
            "社会人:shehuiren_781f5c8.png",
            "什么:shenme_2850187.png",
            "升天:shengtian_1e94200.png",
            "生气:shengqi_28bea26.png",
            "圣诞帽:shengdanmao_8baa3ba.png",
            "胜利:shengli_2e7d119.png",
            "失望:shiwang_91d86a5.png",
            "石化:shihua_6a72740.png",
            "示爱:shiai_652439c.png",
            "受伤:shoushang_3b88780.png",
            "舒服了:shufule_bfdd21a.png",
            "睡觉:shuijiao_8d8d3df.png",
            "思考:sikao_19ec1f8.png",
            "死机:siji_090f6fb.png",
            "他来了:talaile_e847a7d.png",
            "抬杠:taigang_cdb7c94.png",
            "太蓝了:tailanle_d6b0c90.png",
            "太南了:tainanle_2cf2da4.png",
            "讨厌:taoyan_c4825d5.png",
            "天降正义:tianjiangzhengyi_488cc82.png",
            "天呐:tianna_7b5b643.png",
            "舔屏:tianping_86dd8a2.png",
            "痛哭:tongku_c33ee74.png",
            "偷偷笑:toutouxiao_a1d5cc8.png",
            "偷笑:touxiao_10bf7c0.png",
            "投币:toubi_bfc050c.png",
            "土块:tukuai_40eed66.png",
            "哇:wa_6ecafde.png",
            "哇哦:wao_12d9b15.png",
            "哇哇哭:wawaku_87e514d.png",
            "顽皮:wanpi_20e1a62.png",
            "晚安:wanan_c4d502f.png",
            "微笑:weixiao_e487fdb.png",
            "委屈:weiqu_46422f4.png",
            "我方了:wofangle_93e4b08.png",
            "我可:woke_f31754a.png",
            "我来了:wolaile_09f861d.png",
            "我去:woqu_42b2979.png",
            "我最棒:wozuibang_31ee22b.png",
            "无聊:wuliao_dc3ac4f.png",
            "无奈:wunai_fdab1c8.png",
            "无语:wuyu_f52e4bd.png",
            "嘻嘻:xixi_9687edb.png",
            "下饭:xiafan_9054fd0.png",
            "吓一跳:xiayitiao_dd6d2e7.png",
            "嫌弃:xianqi_a501bcc.png",
            "小恶魔:xiaoemo_a2be991.png",
            "谢谢啦:xiexiela_f2ceb2f.png",
            "心碎:xinsui_9f6eee0.png",
            "熊猫眼:xiongmaoyan_c05ea37.png",
            "羞涩:xiuse_c3a7b03.png",
            "嘘:xu_d1ffaed.png",
            "旭宝宝:xubaobao_3dc3a79.png",
            "压力大:yalida_c8630e8.png",
            "咬:yao_bc25dfe.png",
            "要抱抱:yaobaobao_8383cf3.png",
            "爷:ye_b5315ce.png",
            "疑惑:yihuo_ee678d3.png",
            "疑问:yiwen_515a3d6.png",
            "硬化:yinghua_13b580a.png",
            "优秀:youxiu_4575222.png",
            "幽灵:youling_9e4361b.png",
            "有毒:youdu_b3f75ca.png",
            "有意思:youyisi_302d755.png",
            "原谅:yuanliang_e802e0a.png",
            "晕倒:yundao_5409f2e.png",
            "晕了:yunle_5a1f2b4.png",
            "晕眩:yunxuan_74d77bd.png",
            "再见:zaijian_704c0ab.png",
            "赞一个:zanyige_c75588b.png",
            "炸鱼:zhayu_864b2ad.png",
            "张嘴:zhangzui_6c0cd44.png",
            "招财猫:zhaocaimao_2c898ff.png",
            "真的么:zhendeme_d5dc347.png",
            "真香:zhenxiang_f59448a.png",
            "阵亡:zhenwang_386f42c.png",
            "震惊:zhenjing_7c2e817.png",
            "指责:zhize_0c5c59c.png",
            "住口:zhukou_cb74ab5.png",
            "注视你:zhushini_cbc6070.png",
            "足球:zuqiu_c47c4e3.png",
    };

    /// 把固定表填进字典。静态：测试可直接调用验证映射，不用构造客户端。
    internal static void FillDouyuEmoticons(EmoticonDictionary dict)
    {
        for (int i = 0; i < DouyuEmoticonPairs.Length; i++)
        {
            int colon = DouyuEmoticonPairs[i].IndexOf(':');
            if (colon <= 0) continue;
            dict.Add(DouyuEmoticonPairs[i].Substring(0, colon),
                DouyuEmoticonBase + DouyuEmoticonPairs[i].Substring(colon + 1));
        }
    }

    /// 建固定表情表。放循环外只建一次 —— 没有网络、不会失败。
    private void BuildEmoticons()
    {
        if (_emoticons.Count > 0) return;
        FillDouyuEmoticons(_emoticons);
        Action handler = EmoticonsLoaded;
        if (handler != null) handler();
    }

    private readonly EmoticonDictionary _emoticons = new EmoticonDictionary();
    public EmoticonDictionary Emoticons { get { return _emoticons; } }
    public event Action EmoticonsLoaded;

    private const string BarrageHost = "danmuproxy.douyu.com";
    private const int BarragePort = 8601;
    private const ushort TypeClientToServer = 689;
    private const ushort TypeServerToClient = 690;
    private const int HeartbeatSeconds = 40;

    /// 斗鱼 col 是**调色板索引不是 RGB**（官方文档只写了「0 = 默认颜色」没公开表）。
    /// 下表取自两个独立实现（AllLive 的 C# 与 pure_live_TV 的 Dart）给出的**完全相同**的 RGB。
    /// AllLive: Copyright (c) 2021 xiaoyaocz, MIT；许可/参考范围见 THIRD_PARTY_NOTICES.md。
    private static Color DouyuColor(string col)
    {
        int index;
        if (!int.TryParse(col, out index)) index = 0;
        switch (index)
        {
            case 1: return Color.FromArgb(255, 255, 0, 0);          // 红
            case 2: return Color.FromArgb(255, 30, 135, 240);       // 蓝
            case 3: return Color.FromArgb(255, 122, 200, 75);       // 绿
            case 4: return Color.FromArgb(255, 255, 127, 0);        // 橙
            case 5: return Color.FromArgb(255, 155, 57, 244);       // 紫
            case 6: return Color.FromArgb(255, 255, 105, 180);      // 粉
            default: return Color.FromArgb(255, 255, 255, 255);     // 白（含 0）
        }
    }

    private readonly string _roomInput;
    private string _realRoomId = "";
    private TcpClient _tcp;
    private NetworkStream _stream;
    private volatile bool _stopping;
    private readonly object _connectionGate = new object();
    private readonly object _sendGate = new object();
    private ManualResetEventSlim _connectionStopped;
    private Thread _heartbeat;

    public DouyuDanmakuClient(string room) { _roomInput = room; }

    public string RealRoomId { get { return _realRoomId; } }

    public void Stop()
    {
        _stopping = true;
        CloseQuietly();
    }

    public void Run()
    {
        BuildEmoticons();   // 固定表，无网络、不会失败 —— 放循环外只建一次
        int attempt = 0;
        while (!_stopping)
        {
            attempt++;
            try
            {
                if (!ResolveRoom()) { Sleep(10000); continue; }
                Connect();
                attempt = 0;
                ReadLoop();
            }
            catch (Exception ex)
            {
                if (!_stopping) { PlatformHealth.Record("douyu", "danmaku", PlatformHealth.Classify(ex)); if (!_stopping) Status("斗鱼弹幕连接中断：" + Describe(ex)); }
            }
            finally { CloseQuietly(); }
            if (_stopping) break;
            // 指数退避，封顶 30 秒（第一次失败快重试，避免房间刚开播时干等）
            int wait = Math.Min(30, 3 * attempt);
            if (wait < 3) wait = 3;
            Status("斗鱼： " + wait + " 秒后重连…");
            Sleep(wait * 1000);
        }
    }

    private void Sleep(int ms)
    {
        int left = ms;
        while (left > 0 && !_stopping) { Thread.Sleep(Math.Min(250, left)); left -= 250; }
    }

    private void Status(string text) { StatusHandler handler = StatusChanged; if (handler != null) handler(text); }
    private void Info(string text) { StatusHandler handler = RoomInfo; if (handler != null) handler(text); }

    private static string Describe(Exception ex)
    {
        AggregateException aggregate = ex as AggregateException;
        if (aggregate != null && aggregate.InnerExceptions.Count > 0) ex = aggregate.InnerExceptions[0];
        return ex.Message;
    }

    // ------------------------------------------------------------------ 房间信息

    /// 短号必须换算成真实房间号。斗鱼自定义短号（如 /yyf）会 302，纯数字短号也可能不是真实 rid。
    /// 最稳的是 m.douyu.com 的 room/info 接口；它挂了就退回 betard，再不行就拿输入当房间号。
    private bool ResolveRoom()
    {
        if (_realRoomId.Length > 0) return true;
        string rid = null, anchor = null, title = null, status = null;

        string api = TryGet("https://m.douyu.com/api/room/info?rid=" + Uri.EscapeDataString(_roomInput),
            "https://m.douyu.com/" + _roomInput);
        if (api != null) rid = JsonField(api, "\"rid\"");

        string betard = TryGet("https://www.douyu.com/betard/" + Uri.EscapeDataString(_roomInput),
            "https://www.douyu.com/" + _roomInput);
        if (betard != null)
        {
            // ⚠️ 必须**只在 "room":{ 这个对象里**找 room_id。
            //    全文搜第一个 "room_id" 会命中别处的嵌套字段（第一版探针就把 9999 解成了 453751，
            //    连到一个不存在的房间，白等 35 秒一条弹幕都没有）
            int at = betard.IndexOf("\"room\":{", StringComparison.Ordinal);
            int from = at >= 0 ? at : 0;
            string roomId = JsonField(betard, "\"room_id\"", from);
            if (roomId != null) rid = roomId;
            anchor = JsonString(betard, "\"nickname\"", from);
            title = JsonString(betard, "\"room_name\"", from);
            status = JsonField(betard, "\"show_status\"", from);
        }

        if (rid == null) rid = _roomInput;
        _realRoomId = rid;
        Info("斗鱼 " + rid + (anchor == null ? "" : " · " + anchor)
            + (title == null ? "" : " · " + title)
            + (status == "2" ? "（未开播）" : ""));
        if (status == "2") Status("斗鱼：房间未开播，仍会保持连接等待");
        return true;
    }

    private static string TryGet(string url, string referer)
    {
        try { return LiveHub.HttpGetText(url, referer); }
        catch (Exception) { return null; }
    }

    /// 从 JSON 里取数字/字符串字段的**原始文本**（不引 ConvertFrom-Json 那一套，够用且不引依赖）
    private static string JsonField(string text, string key) { return JsonField(text, key, 0); }

    private static string JsonField(string text, string key, int from)
    {
        int at = text.IndexOf(key, from, StringComparison.Ordinal);
        if (at < 0) return null;
        int colon = text.IndexOf(':', at);
        if (colon < 0) return null;
        int start = colon + 1;
        while (start < text.Length && text[start] == ' ') start++;
        if (start < text.Length && text[start] == '"') start++;
        int end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-')) end++;
        if (end == start) return null;
        return text.Substring(start, end - start);
    }

    private static string JsonString(string text, string key, int from)
    {
        int at = text.IndexOf(key, from, StringComparison.Ordinal);
        if (at < 0) return null;
        int colon = text.IndexOf(':', at);
        if (colon < 0) return null;
        int start = colon + 1;
        while (start < text.Length && text[start] == ' ') start++;
        if (start >= text.Length || text[start] != '"') return null;
        start++;
        int end = start;
        while (end < text.Length && text[end] != '"')
        {
            if (text[end] == '\\') end++;      // 跳过转义
            end++;
        }
        if (end >= text.Length) return null;
        return text.Substring(start, end - start);
    }

    // ------------------------------------------------------------------ 连接

    private void Connect()
    {
        Status("斗鱼：正在连接弹幕服务器…");
        TcpClient tcp = new TcpClient();
        lock (_connectionGate)
        {
            if (_stopping) { tcp.Close(); throw new OperationCanceledException(); }
            _tcp = tcp;
        }
        IAsyncResult connect = tcp.BeginConnect(BarrageHost, BarragePort, null, null);
        using (WaitHandle wait = connect.AsyncWaitHandle)
            if (!wait.WaitOne(10000)) throw new Exception("连接 " + BarrageHost + " 超时");
        tcp.EndConnect(connect);
        tcp.NoDelay = true;
        tcp.SendTimeout = 5000;
        NetworkStream stream = tcp.GetStream();
        lock (_connectionGate)
        {
            if (_stopping) { stream.Close(); throw new OperationCanceledException(); }
            _stream = stream;
        }

        Send("type@=loginreq/roomid@=" + _realRoomId + "/");
        Thread.Sleep(300);
        Send("type@=joingroup/gid@=-9999/rid@=" + _realRoomId + "/");
        Thread.Sleep(300);
        PlatformHealth.Record("douyu", "danmaku", "ready");
        Status("斗鱼：已进入房间 " + _realRoomId);

        StartHeartbeat(stream, HeartbeatSeconds * 1000);
    }

    private void StartHeartbeat(NetworkStream stream, int intervalMs)
    {
        lock (_connectionGate)
        {
            if (_stopping) return;
            ManualResetEventSlim stopped = new ManualResetEventSlim(false);
            Thread heartbeat = new Thread(delegate () { HeartbeatLoop(stream, stopped, intervalMs); });
            heartbeat.IsBackground = true;
            heartbeat.Name = "douyu-heartbeat";
            _connectionStopped = stopped;
            _heartbeat = heartbeat;
            heartbeat.Start();
        }
    }

    private void HeartbeatLoop(NetworkStream stream, ManualResetEventSlim stopped, int intervalMs)
    {
        try
        {
            while (!_stopping && !stopped.Wait(intervalMs)) SendToStream(stream, "type@=mrkl/");
        }
        catch (Exception)
        {
            // 唤醒这条连接的阻塞接收，让接收线程进入重连；绝不关闭新连接。
            try { stream.Close(); } catch (Exception) { }
        }
        finally { stopped.Dispose(); }
    }

    private void CloseQuietly()
    {
        NetworkStream stream;
        TcpClient tcp;
        ManualResetEventSlim stopped;
        Thread heartbeat;
        lock (_connectionGate)
        {
            stream = _stream; tcp = _tcp; stopped = _connectionStopped; heartbeat = _heartbeat;
            _stream = null; _tcp = null; _connectionStopped = null; _heartbeat = null;
        }
        try { if (stopped != null) stopped.Set(); } catch (ObjectDisposedException) { }
        try { if (stream != null) stream.Close(); } catch (Exception) { }
        try { if (tcp != null) tcp.Close(); } catch (Exception) { }
        if (heartbeat != null && heartbeat != Thread.CurrentThread && heartbeat.IsAlive) heartbeat.Join(2000);
        else if (heartbeat == null && stopped != null) stopped.Dispose();
    }

    /// STT 帧：[int32 LE L][int32 LE L][uint16 LE type][byte enc][byte res][body]
    /// L = 8 + body 字节数（body 含结尾的 \0）
    private void Send(string payload)
    {
        NetworkStream stream;
        lock (_connectionGate) { stream = _stream; }
        if (stream == null) return;
        SendToStream(stream, payload);
    }

    private void SendToStream(NetworkStream stream, string payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload + "\0");
        int length = 8 + body.Length;
        byte[] frame = new byte[4 + length];
        WriteInt32(frame, 0, length);
        WriteInt32(frame, 4, length);
        frame[8] = (byte)(TypeClientToServer & 0xFF);
        frame[9] = (byte)((TypeClientToServer >> 8) & 0xFF);
        frame[10] = 0;
        frame[11] = 0;
        Buffer.BlockCopy(body, 0, frame, 12, body.Length);
        lock (_sendGate)
        {
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private void ReadLoop()
    {
        byte[] header = new byte[12];
        while (!_stopping)
        {
            if (!ReadExact(header, 12)) break;
            int length = BitConverter.ToInt32(header, 0);
            // sanity check：脏数据/长度异常直接断开，别在里面死循环
            if (length < 9 || length > 300000) throw new Exception("帧长度异常：" + length);
            ushort type = BitConverter.ToUInt16(header, 8);
            int bodyLength = length - 8;
            byte[] buffer = new byte[bodyLength];
            if (!ReadExact(buffer, bodyLength)) break;
            string body = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
            if (type == TypeServerToClient) Dispatch(body);
        }
    }

    private bool ReadExact(byte[] buffer, int count)
    {
        NetworkStream stream = _stream;
        if (stream == null) return false;
        int offset = 0;
        while (offset < count)
        {
            int read;
            try { read = stream.Read(buffer, offset, count - offset); }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    // ------------------------------------------------------------------ 消息分发

    private void Dispatch(string body)
    {
        string type = SttGet(body, "type");

        // ⚠️ 服务端会主动下发 pingreq（官方文档里没有这个类型），必须回 mrkl，否则可能收不到推送
        if (type == "pingreq") { try { Send("type@=mrkl/"); } catch (Exception) { } return; }

        // ⚠️ comm_chatmsg 的 type **不在第一个字段**（顺序是 btype/chatmsg/…/type），
        //    所以不能靠 StartsWith 预筛；而且它内嵌的 chatmsg 里还有一个 type@=chatmsg，
        //    **必须先判 comm_chatmsg 再判 chatmsg**，否则一条 SC 会被当普通弹幕渲染两遍。
        if (type == "comm_chatmsg") { HandleCommChat(body); return; }
        if (type == "chatmsg") { HandleChat(body); return; }
        // uenter（进房）实测占 71%，默认直接丢；dgb/spbc/其他一律忽略
    }

    private void HandleChat(string body)
    {
        DanmakuHandler handler = DanmakuReceived;
        if (handler == null) return;
        DanmakuItem item = BuildDanmaku(body);
        if (item != null) handler(item);
    }

    /// chatmsg / 内嵌 chatmsg 通用：body 是 STT 串，直接取字段
    private DanmakuItem BuildDanmaku(string body)
    {
        string text = SttGet(body, "txt");
        if (text == null || text.Length == 0) return null;
        DanmakuItem item = new DanmakuItem();
        item.Text = text;
        item.UserName = SttGet(body, "nn");
        item.Color = DouyuColor(SttGet(body, "col")).ToArgb() & 0xFFFFFF;
        item.Mode = 1;                    // 斗鱼**没有弹幕位置字段**，一律当滚动
        item.FontSize = 25;               // 与 B站 标准字号对齐，实际大小由配置的字号档位决定
        item.UserId = LongOf(SttGet(body, "uid"));
        item.UserLevel = IntOf(SttGet(body, "level"));
        item.MedalName = SttGet(body, "bnn");
        item.MedalLevel = IntOf(SttGet(body, "bl"));
        item.Rnd = LongOf(SttGet(body, "cid"));
        long cst = LongOf(SttGet(body, "cst"));
        item.TimestampMs = cst > 0 ? cst : ToUnixMs(DateTime.UtcNow);
        // 头像这里不取：DanmakuItem 没有头像字段，而且弹幕本来也不显示头像
        // （斗鱼的 ic 只在 SC 卡片上有用，见 HandleCommChat 里的 Face）
        return item;
    }

    /// 斗鱼的 SC = 「高能弹幕」：comm_chatmsg + btype@=voiceDanmu
    ///   cprice = 价格（**单位分**）  cet = 持续秒数
    ///   chatmsg = **嵌套的一整条 STT 串**，nn/txt/ic 都在里面，要再解一层
    /// 注意 cprice / cet 都可能为 0（实测 fantasyIsland 就是 0/0），**必须靠 btype 判定**
    private void HandleCommChat(string body)
    {
        string btype = SttGet(body, "btype");
        if (btype != "voiceDanmu") return;              // fantasyIsland / pandora 等一律不是 SC

        string nested = SttGet(body, "chatmsg");
        string inner = nested == null ? body : nested;  // 嵌套串解一层后仍是 STT，直接在上面取字段

        SuperChatHandler handler = SuperChatReceived;
        if (handler == null) return;

        SuperChatItem item = new SuperChatItem();
        int price = IntOf(SttGet(body, "cprice"));
        int realPrice = IntOf(SttGet(body, "crealPrice"));
        if (realPrice > price) price = realPrice;       // 两者关系不明，取大的更保险
        item.Price = price / 100;                        // 分 → 元
        item.DurationSec = IntOf(SttGet(body, "cet"));
        if (item.DurationSec <= 0) item.DurationSec = 60;

        item.Message = SttGet(inner, "txt");
        item.UserName = SttGet(inner, "nn");
        item.UserLevel = IntOf(SttGet(inner, "level"));
        item.MedalName = SttGet(inner, "bnn");
        item.MedalLevel = IntOf(SttGet(inner, "bl"));
        string face = SttGet(inner, "ic");
        if (!string.IsNullOrEmpty(face)) item.Face = "https://apic.douyucdn.cn/upload/" + face + "_small.jpg";

        // 斗鱼**不给 SC 配色**（B站 是服务端下发 6 种颜色），按价格分档本地配。
        // 蓝紫档取自 pure_live_TV 的 #c1c1ff / #292a60。
        ApplyDouyuScColors(item, item.Price);

        long now = LongOf(SttGet(body, "now"));
        item.StartTime = now > 0 ? now : ToUnixMs(DateTime.UtcNow);
        item.EndTime = item.StartTime + item.DurationSec * 1000L;
        item.Id = LongOf(SttGet(body, "vrid"));
        if (item.Id == 0) item.Id = item.StartTime ^ (long)item.Price << 8 ^ item.UserName.GetHashCode();
        item.ExpiresAt = DateTime.UtcNow.AddSeconds(item.DurationSec);
        handler(item);

        // 斗鱼**没有删除/撤回消息类型** → 卡片只能靠 ExpiresAt 自己到期
        // （渲染层的 ScDurationScalePercent 也在算，这里不用额外做什么；
        //   留这个注释是提醒后来者：不要去找「删除消息」，找不到的）
    }

    /// 按价格分档配色。档位是**本地约定**，不是服务端数据。
    private static void ApplyDouyuScColors(SuperChatItem item, int price)
    {
        if (price >= 1000)      // ≥1000 元
        {
            item.BackgroundColor = Color.FromArgb(255, 255, 234, 214);
            item.BackgroundBottomColor = Color.FromArgb(255, 150, 40, 20);
            item.MessageFontColor = Color.FromArgb(255, 90, 20, 10);
            item.NameColor = Color.FromArgb(255, 120, 30, 10);
        }
        else if (price >= 100)
        {
            item.BackgroundColor = Color.FromArgb(255, 255, 225, 232);
            item.BackgroundBottomColor = Color.FromArgb(255, 190, 40, 80);
            item.MessageFontColor = Color.FromArgb(255, 120, 20, 50);
            item.NameColor = Color.FromArgb(255, 160, 30, 70);
        }
        else if (price >= 50)
        {
            item.BackgroundColor = Color.FromArgb(255, 255, 240, 219);
            item.BackgroundBottomColor = Color.FromArgb(255, 200, 120, 20);
            item.MessageFontColor = Color.FromArgb(255, 130, 70, 0);
            item.NameColor = Color.FromArgb(255, 170, 100, 20);
        }
        else                    // 基础档：蓝紫（参考 pure_live_TV）
        {
            item.BackgroundColor = Color.FromArgb(255, 193, 193, 255);
            item.BackgroundBottomColor = Color.FromArgb(255, 41, 42, 96);
            item.MessageFontColor = Color.FromArgb(255, 245, 245, 255);
            item.NameColor = Color.FromArgb(255, 225, 225, 255);
        }
    }

    // ------------------------------------------------------------------ STT 解析

    /// 从 STT 串里取一个键的值（**已反转义**）。
    ///
    /// ⚠️ 不能用 Split("@=") —— 值里可能出现未转义的 "@="，Split 会把后半截丢掉。
    /// ⚠️ 找值结尾时要**跳过转义对**（@S / @A），否则值里转义过的 "/" 会被当成数组分隔符。
    internal static string SttGet(string body, string key)
    {
        if (body == null) return null;
        string needle = key + "@=";
        int at = 0;
        while (true)
        {
            at = body.IndexOf(needle, at, StringComparison.Ordinal);
            if (at < 0) return null;
            // 必须是「字段起点」：要么在串首，要么前一个字符是 '/'。
            // ⚠️ 不合法时**必须继续往后找**，不能直接返回 null ——
            //    因为 "btype@=" 里就含有 "type@="、"xcet@=" 里含有 "cet@="，
            //    停在第一个误匹配上的话 comm_chatmsg 的 type 永远取不到。
            //    （这是回归测试抓到的真 bug，不是假想）
            if (at == 0 || body[at - 1] == '/') break;
            at += 1;
        }
        int start = at + needle.Length;
        int end = start;
        while (end < body.Length)
        {
            char c = body[end];
            if (c == '/') break;
            if (c == '@' && end + 1 < body.Length) { end += 2; continue; }   // 跳过转义对
            end++;
        }
        return Unescape(body.Substring(start, end - start));
    }

    /// 反转义：**先 @S → / 再 @A → @**（编码时是先 @ 后 /，解码必须倒过来）
    internal static string Unescape(string value)
    {
        if (value == null) return null;
        return value.Replace("@S", "/").Replace("@A", "@");
    }

    internal static int IntOf(string value)
    {
        int parsed;
        if (value == null || !int.TryParse(value, out parsed)) return 0;
        return parsed;
    }

    internal static long LongOf(string value)
    {
        long parsed;
        if (value == null || !long.TryParse(value, out parsed)) return 0;
        return parsed;
    }

    internal static long ToUnixMs(DateTime time)
    {
        return (long)(time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }
}

// ============================================================================================
// 客户端工厂
// ============================================================================================
internal static class DanmakuClientFactory
{
    /// 这个平台有没有接进来（用来给 --danmaku 做参数校验 / 给界面提示）
    public static bool IsSupported(string platform)
    {
        return platform == "bili" || platform == "douyu" || platform == "huya" || platform == "douyin";
    }

    public static string[] SupportedPlatforms()
    {
        return new string[] { "bili", "douyu", "huya", "douyin" };
    }

    public static IDanmakuClient Create(string platform, string room)
    {
        if (platform == "bili") return new BiliDanmakuClient(room, LiveHub.Cookie);
        if (platform == "douyu") return new DouyuDanmakuClient(room);
        if (platform == "huya") return new HuyaDanmakuClient(room);
        if (platform == "douyin") return new DouyinDanmakuClient(room);
        throw new NotSupportedException("暂不支持这个平台的弹幕：" + platform);
    }
}
