using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

internal static partial class HardeningTests
{
    private static void RegisterLogicAuditDanmakuCases()
    {
        TestRunner.Add("弹幕-逻辑回归", "B站 SC 数组撤回与非法项隔离", TestLogicAuditScDelete);
        TestRunner.Add("弹幕-逻辑回归", "运行中调节延迟保持消息顺序", TestLogicAuditDelayChanges);
    }

    private sealed class LogicAuditScSink
    {
        internal object Renderer;
        internal readonly List<long> Deleted = new List<long>();
        public void Delete(long id)
        {
            Deleted.Add(id);
            Renderer.GetType().GetMethod("RemoveSuperChat").Invoke(Renderer, new object[] { id });
        }
    }

    private static void TestLogicAuditScDelete()
    {
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            scene.Sc(101); scene.Sc(202); scene.Sc(303); scene.Sc(4294967297L);
            Type clientType = DanmakuType("BiliDanmakuClient");
            object client = Activator.CreateInstance(clientType, new object[] { "123", null });
            var sink = new LogicAuditScSink { Renderer = scene.Renderer };
            EventInfo deleted = clientType.GetEvent("SuperChatDeleted");
            deleted.AddEventHandler(client, Delegate.CreateDelegate(deleted.EventHandlerType, sink,
                typeof(LogicAuditScSink).GetMethod("Delete")));
            MethodInfo dispatch = clientType.GetMethod("Dispatch", ReviewInstance);
            dispatch.Invoke(client, new object[] {
                "{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\",\"data\":{\"ids\":[101,\"202\",4294967297,null,\"bad\",0,-1,true,1.5,\"9223372036854775808\",101]}}" });
            Check(sink.Deleted.Count == 3 && sink.Deleted[0] == 101 && sink.Deleted[1] == 202 && sink.Deleted[2] == 4294967297L,
                "SC 撤回遍历 64 位与字符串 ID，跳过非法与重复项");
            Check((int)scene.RendererType.GetProperty("SuperChatActiveCount").GetValue(scene.Renderer, null) == 1,
                "SC 数组撤回删除对应卡片并保留其它卡片");
            foreach (string message in new[] {
                "{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\",\"data\":{}}",
                "{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\",\"data\":{\"ids\":303}}",
                "{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\"}" })
                dispatch.Invoke(client, new object[] { message });
            Check(sink.Deleted.Count == 3, "SC 撤回缺失或非数组字段不误删");
            dispatch.Invoke(client, new object[] {
                "{\"cmd\":\"SUPER_CHAT_MESSAGE_DELETE\",\"data\":{\"ids\":[303]}}" });
            Check((int)scene.RendererType.GetProperty("SuperChatActiveCount").GetValue(scene.Renderer, null) == 0,
                "非法撤回之后下一条正常撤回仍可生效");
        }
    }

    // Exercise the real inbox and renderer without constructing or showing a native window.
    private static object LogicAuditOverlay(DanmakuScene scene)
    {
        Type overlay = DanmakuType("DanmakuOverlayForm");
        object form = FormatterServices.GetUninitializedObject(overlay);
        DanmakuScene.Set(form, "_config", scene.Config);
        DanmakuScene.Set(form, "_renderer", scene.Renderer);
        DanmakuScene.Set(form, "_gate", new object());
        foreach (string field in new[] { "_inbox", "_pendingDanmaku" })
            DanmakuScene.Set(form, field, Activator.CreateInstance(overlay.GetField(field, ReviewInstance).FieldType));
        return form;
    }

    private static void LogicAuditPost(object form, string text)
    {
        object item = Activator.CreateInstance(DanmakuType("DanmakuItem"));
        DanmakuScene.Set(item, "Text", text); DanmakuScene.Set(item, "Mode", 1);
        form.GetType().GetMethod("PostDanmaku").Invoke(form, new[] { item });
        form.GetType().GetMethod("DrainInbox", ReviewInstance).Invoke(form, null);
    }

    private static void LogicAuditSetArrival(object form, int index, DateTime arrived)
    {
        IList pending = (IList)DanmakuScene.Get(form, "_pendingDanmaku");
        object item = pending[index]; DanmakuScene.Set(item, "ArrivedAt", arrived); pending[index] = item;
    }

    private static void LogicAuditRelease(object form, DateTime now)
    { form.GetType().GetMethod("ReleasePendingDanmaku", ReviewInstance).Invoke(form, new object[] { now }); }

    private static string LogicAuditFlyingText(DanmakuScene scene, int index)
    { return (string)DanmakuScene.Get(DanmakuScene.Get(scene.Flying[index], "Source"), "Text"); }

    private static void TestLogicAuditDelayChanges()
    {
        DateTime start = DateTime.UtcNow;
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            object form = LogicAuditOverlay(scene);
            DanmakuScene.Set(scene.Config, "DanmakuDelayMs", 5000);
            LogicAuditPost(form, "first"); LogicAuditSetArrival(form, 0, start);
            DanmakuScene.Set(scene.Config, "DanmakuDelayMs", 2000);
            LogicAuditPost(form, "second"); LogicAuditSetArrival(form, 1, start.AddMilliseconds(100));
            LogicAuditRelease(form, start.AddMilliseconds(2500));
            Check(scene.Flying.Count == 2 && ((IList)DanmakuScene.Get(form, "_pendingDanmaku")).Count == 0,
                "延迟从 5 秒降低到 2 秒，原积压与新消息按新延迟准时释放");
            Check(LogicAuditFlyingText(scene, 0) == "first" && LogicAuditFlyingText(scene, 1) == "second",
                "降低延迟保持原始消息顺序");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            object form = LogicAuditOverlay(scene);
            DanmakuScene.Set(scene.Config, "DanmakuDelayMs", 5000);
            LogicAuditPost(form, "old"); LogicAuditSetArrival(form, 0, start);
            DanmakuScene.Set(scene.Config, "DanmakuDelayMs", 0);
            LogicAuditPost(form, "new"); LogicAuditSetArrival(form, 1, start.AddMilliseconds(100));
            LogicAuditRelease(form, start.AddMilliseconds(200));
            Check(scene.Flying.Count == 2 && LogicAuditFlyingText(scene, 0) == "old"
                && LogicAuditFlyingText(scene, 1) == "new", "关闭延迟先释放积压，再显示新消息，不丢失或倒序");
            LogicAuditPost(form, "immediate");
            Check(scene.Flying.Count == 3 && LogicAuditFlyingText(scene, 2) == "immediate",
                "延迟关闭且无积压时仍立即显示");
        }
        using (DanmakuScene scene = new DanmakuScene(1280, 720))
        {
            object form = LogicAuditOverlay(scene);
            DanmakuScene.Set(scene.Config, "DanmakuDelayMs", 2000);
            LogicAuditPost(form, "reload"); LogicAuditSetArrival(form, 0, start);
            object fresh = Activator.CreateInstance(DanmakuType("DanmakuConfig"));
            DanmakuScene.Set(fresh, "DanmakuDelayMs", 5000);
            form.GetType().GetMethod("CopyInto", ReviewStatic).Invoke(null, new[] { scene.Config, fresh });
            LogicAuditRelease(form, start.AddMilliseconds(3000));
            Check(scene.Flying.Count == 0, "配置热重载增加延迟后，积压消息使用新延迟");
            LogicAuditRelease(form, start.AddMilliseconds(5500));
            Check(scene.Flying.Count == 1 && LogicAuditFlyingText(scene, 0) == "reload",
                "增加延迟后到点继续释放，保留积压消息");
        }
    }
}
