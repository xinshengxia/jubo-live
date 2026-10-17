using System;
using System.Collections.Generic;

internal sealed class DanmakuCounters
{
    public long Received, Displayed, Merged, Filtered, Unsupported, NoSpace, Density, QueueFull, QueueExpired, Queued, ReflowRemoved, Shrunk;
    public int Pending;
    internal DanmakuCounters Copy(int pending) { DanmakuCounters copy = (DanmakuCounters)MemberwiseClone(); copy.Pending = pending; return copy; }
}

internal sealed partial class DanmakuRenderer
{
    internal Func<DateTime> Clock = delegate { return DateTime.UtcNow; };
    private DateTime Now { get { return Clock(); } }
    private sealed class AdmissionItem
    {
        internal DanmakuItem Item;
        internal string Key;
        internal int Count = 1, Priority;
        internal DateTime Expires;
    }
    private readonly DanmakuCounters _stats = new DanmakuCounters();
    private readonly List<AdmissionItem> _admission = new List<AdmissionItem>();
    private DateTime _nextAdmission = DateTime.MinValue;
    internal DanmakuCounters GetCounters() { return _stats.Copy(_admission.Count); }
    public string StatisticsText
    {
        get
        {
            return "进入排版器：" + _stats.Received + "　已显示：" + _stats.Displayed + "　合并：" + _stats.Merged
                + "\r\n屏蔽：" + _stats.Filtered + "　不支持：" + _stats.Unsupported
                + "\r\n待显示：" + _admission.Count + "　累计排队：" + _stats.Queued
                + "\r\n舍弃（空间 / 密度）：" + _stats.NoSpace + " / " + _stats.Density
                + "\r\n队列满：" + _stats.QueueFull + "　排队超时：" + _stats.QueueExpired
                + "\r\n重排移除：" + _stats.ReflowRemoved + "　拥挤缩小：" + _stats.Shrunk
                + "\r\n事件（接收 / 显示 / 合并 / 屏蔽 / 限流）：" + EventReceived + " / " + EventDisplayed + " / " + EventMerged + " / " + EventFiltered + " / " + EventDropped;
        }
    }
    private string MergeKey(DanmakuItem item)
    {
        string text = item.Text ?? "";
        if (_config.NormalizeDuplicateText) text = NormalizeMergeText(text);
        return item.Mode + "\u0001" + item.Color + "\u0001" + (item.FontSize > 0 ? item.FontSize : 25)
            + "\u0001" + item.AdvancedText + "\u0001" + BuildLabel(item, false) + "\u0001" + text
            + "\u0001" + (item.IsEmoticon ? item.EmoticonUrl : "");
    }
    private bool MergeActive(string key, int count)
    {
        FlyingDanmaku repeated;
        if (!_config.BlockDuplicate || !_recent.TryGetValue(key, out repeated) || repeated.Handle == null
            || (Now - repeated.CreatedAt).TotalMilliseconds >= _config.DuplicateWindowMs) return false;
        int updated = Math.Min(9999, repeated.RepeatCount + count);
        if (updated != repeated.RepeatCount) { repeated.RepeatCount = updated; repeated.VisualDirty = true; }
        return true;
    }
    private int MessagePriority(DanmakuItem item)
    { return item.GuardLevel > 0 ? 3 : item.Pinned ? 2 : _rules.Any(item.Text, _config.HighlightKeywords) ? 1 : 0; }
    private bool RateBusy(DanmakuItem item)
    { return _config.SmoothDanmaku && Now < _nextAdmission && MessagePriority(item) < 2; }
    private void Admitted()
    {
        _stats.Displayed++;
        double rate = _config.AdmissionPerSecond > 0 ? _config.AdmissionPerSecond : Math.Max(1, _config.MaxOnScreen / Math.Max(2f, _duration));
        _nextAdmission = Now.AddSeconds(1.0 / Math.Min(120, rate));
    }
    private void ReceiveDanmaku(DanmakuItem original)
    {
        if (original == null) return;
        _stats.Received++;
        if (original.Mode < 0 || original.Mode > 7) { _stats.Unsupported++; DroppedCount++; return; }
        if ((original.Mode == 7 && !_config.ShowAdvancedText) || (original.Mode != 7 && IsBlocked(original)))
        { _stats.Filtered++; return; }
        DanmakuItem item = PrepareDanmaku(original);
        if (item == null) { _stats.Unsupported++; DroppedCount++; return; }
        if (original.Mode == 7 && IsBlocked(item)) { _stats.Filtered++; return; }
        string key = MergeKey(item);
        if (MergeActive(key, 1)) { _stats.Merged++; return; }
        if (_config.BlockDuplicate)
            foreach (AdmissionItem pending in _admission)
                if (pending.Key == key && pending.Expires > Now)
                { pending.Count = Math.Min(9999, pending.Count + 1); _stats.Merged++; return; }
        bool density = RateBusy(item);
        if (!density && TryAdmit(item, key, 1)) { Admitted(); return; }
        EnqueueDanmaku(item, key, density);
    }
    private void EnqueueDanmaku(DanmakuItem item, string key, bool density)
    {
        if (_config.QueueWaitMs <= 0)
        { if (density) _stats.Density++; else _stats.NoSpace++; DroppedCount++; return; }
        int priority = MessagePriority(item);
        if (_admission.Count >= _config.QueueCapacity)
        {
            int weakest = 0;
            for (int i = 1; i < _admission.Count; i++) if (_admission[i].Priority < _admission[weakest].Priority) weakest = i;
            if (_admission[weakest].Priority >= priority) { _stats.QueueFull++; DroppedCount++; return; }
            _admission.RemoveAt(weakest); _stats.QueueFull++; DroppedCount++;
        }
        AdmissionItem queued = new AdmissionItem();
        queued.Item = item; queued.Key = key; queued.Priority = priority; queued.Expires = Now.AddMilliseconds(_config.QueueWaitMs);
        _admission.Add(queued); _stats.Queued++;
    }
    private void DrainAdmission(DateTime now)
    {
        for (int i = _admission.Count - 1; i >= 0; i--)
            if (now >= _admission[i].Expires) { _admission.RemoveAt(i); _stats.QueueExpired++; DroppedCount++; }
        List<AdmissionItem> ordered = new List<AdmissionItem>(_admission);
        ordered.Sort(delegate (AdmissionItem a, AdmissionItem b) { int priority = b.Priority.CompareTo(a.Priority); return priority != 0 ? priority : a.Expires.CompareTo(b.Expires); });
        int attempts = 0, admitted = 0;
        foreach (AdmissionItem pending in ordered)
        {
            if (attempts++ >= 8 || admitted >= 2) break;
            if (IsBlocked(pending.Item)) { _admission.Remove(pending); _stats.Filtered++; continue; }
            if (MergeActive(pending.Key, pending.Count)) { _admission.Remove(pending); _stats.Merged++; continue; }
            if (RateBusy(pending.Item)) continue;
            if (!TryAdmit(pending.Item, pending.Key, pending.Count)) continue;
            _admission.Remove(pending); Admitted(); admitted++;
        }
    }
}
