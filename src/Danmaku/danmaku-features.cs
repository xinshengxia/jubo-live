using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed partial class DanmakuConfig
{
    public bool ShowScrolling = true;
    public bool ShowTop = true;
    public bool ShowBottom = true;
    public bool ShowReverse = true;
    public bool ShowColored = true;
    public bool ShowAdvancedText = true;
    public bool FixedOverflowToScroll = true;
    public int TopFixedZonePercent = 40;
    public int BottomFixedZonePercent = 40;
    public int MaxFixedPerMode = 6;
    public bool ShrinkWhenCrowded = true;
    public int CrowdedScalePercent = 85;
    public string[] HighlightKeywords = new string[0];
    public string[] PinKeywords = new string[0];
    public int HighlightColor = 0xFFD166;
    public string[] ReservedAreas = new string[0]; // x,y,width,height，播放画面的整数百分比
    public string EmoteSizeMode = "platform";
    public string BorderStyle = "outline";
    public int TextOpacityPercent = 100;
    public bool SmoothDanmaku = true;
    public int QueueWaitMs = 800;
    public int QueueCapacity = 64;
    public int AdmissionPerSecond = 0; // 0 按同屏上限和穿屏时长计算
    public bool ShowGuardEvents = false;
    public bool ShowLiveStats = false;
    public int EventDurationSeconds = 5;
    public int EventMaxVisible = 3;
    public int EventRatePerSecond = 2;

    private void NormalizeFeatures()
    {
        if (HighlightKeywords == null) HighlightKeywords = new string[0];
        if (PinKeywords == null) PinKeywords = new string[0];
        if (ReservedAreas == null) ReservedAreas = new string[0];
        List<string> valid = new List<string>();
        foreach (string area in ReservedAreas)
        {
            RectangleF rect;
            if (valid.Count < 12 && DanmakuRegions.TryParse(area, out rect)) valid.Add(DanmakuRegions.Encode(rect));
        }
        ReservedAreas = valid.ToArray();
        if (EmoteSizeMode != "small" && EmoteSizeMode != "large") EmoteSizeMode = "platform";
        if (BorderStyle != "heavy" && BorderStyle != "shadow") BorderStyle = "outline";
        TextOpacityPercent = Math.Max(10, Math.Min(100, TextOpacityPercent));
        QueueWaitMs = Math.Max(0, Math.Min(1000, QueueWaitMs));
        QueueCapacity = Math.Max(1, Math.Min(200, QueueCapacity));
        AdmissionPerSecond = Math.Max(0, Math.Min(120, AdmissionPerSecond));
        EventDurationSeconds = Math.Max(2, Math.Min(10, EventDurationSeconds));
        EventMaxVisible = Math.Max(1, Math.Min(5, EventMaxVisible));
        EventRatePerSecond = Math.Max(1, Math.Min(5, EventRatePerSecond));
        HighlightColor &= 0xFFFFFF;
        TopFixedZonePercent = Math.Max(10, Math.Min(100, TopFixedZonePercent));
        BottomFixedZonePercent = Math.Max(10, Math.Min(100, BottomFixedZonePercent));
        MaxFixedPerMode = Math.Max(0, Math.Min(60, MaxFixedPerMode));
        CrowdedScalePercent = Math.Max(70, Math.Min(100, CrowdedScalePercent));
    }
}

internal static class DanmakuRegions
{
    internal static bool TryParse(string text, out RectangleF rect)
    {
        rect = RectangleF.Empty;
        string[] values = (text ?? "").Split(',');
        int x, y, width, height;
        if (values.Length != 4 || !int.TryParse(values[0], out x) || !int.TryParse(values[1], out y)
            || !int.TryParse(values[2], out width) || !int.TryParse(values[3], out height) || width <= 0 || height <= 0) return false;
        x = Math.Max(0, Math.Min(99, x)); y = Math.Max(0, Math.Min(99, y));
        width = Math.Min(100 - x, width); height = Math.Min(100 - y, height);
        rect = new RectangleF(x, y, width, height); return true;
    }
    internal static string Encode(RectangleF rect)
    { return ((int)rect.X) + "," + ((int)rect.Y) + "," + ((int)rect.Width) + "," + ((int)rect.Height); }
}

internal sealed class DanmakuRules
{
    private readonly Dictionary<string, Regex> _compiled = new Dictionary<string, Regex>();
    private readonly HashSet<string> _disabled = new HashSet<string>();
    internal void Clear() { _compiled.Clear(); _disabled.Clear(); }
    internal bool Any(string text, string[] rules)
    {
        if (rules == null) return false;
        text = text ?? "";
        foreach (string rule in rules)
            if (!string.IsNullOrEmpty(rule) && !rule.StartsWith("re:", StringComparison.OrdinalIgnoreCase)
                && text.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (string rule in rules)
        {
            if (string.IsNullOrEmpty(rule) || !rule.StartsWith("re:", StringComparison.OrdinalIgnoreCase) || _disabled.Contains(rule)) continue;
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 50) break;
            try
            {
                Regex regex;
                if (!_compiled.TryGetValue(rule, out regex))
                {
                    regex = new Regex(rule.Substring(3), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                    if (_compiled.Count >= 128) _compiled.Clear();
                    _compiled[rule] = regex;
                }
                if (regex.IsMatch(text)) return true;
            }
            catch (ArgumentException) { Disable(rule, "无效表达式"); }
            catch (RegexMatchTimeoutException) { Disable(rule, "匹配超时"); }
        }
        return false;
    }
    private void Disable(string rule, string reason)
    {
        if (_disabled.Count >= 512) return;
        if (_disabled.Add(rule)) DanmakuHost.LogQuiet("弹幕规则已暂时停用：" + reason + "；修改设置后重试");
    }
    internal bool User(DanmakuItem item, string[] rules)
    {
        if (rules == null) return false;
        List<string> names = new List<string>();
        foreach (string rule in rules)
        {
            if (string.IsNullOrEmpty(rule)) continue;
            if (rule.StartsWith("uid:", StringComparison.OrdinalIgnoreCase))
            {
                long uid;
                if (long.TryParse(rule.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out uid) && uid > 0 && item.UserId == uid) return true;
            }
            else if (rule.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            { if (string.Equals(item.UserName, rule.Substring(5), StringComparison.OrdinalIgnoreCase)) return true; }
            else names.Add(rule);
        }
        return Any(item.UserName, names.ToArray());
    }
}

internal sealed partial class DanmakuRenderer
{
    private readonly DanmakuRules _rules = new DanmakuRules();
    private void ResetFeatures() { _rules.Clear(); _nextAdmission = DateTime.MinValue; }
    private void DisposeFeatures() { _rules.Clear(); _admission.Clear(); _events.Clear(); _liveStatus.Clear(); if (_textAlpha != null) _textAlpha.Dispose(); }
    private System.Drawing.Imaging.ImageAttributes _textAlpha;
    private int _lastTextOpacity = -1;
    private void DrawTextBitmap(Graphics graphics, Bitmap bitmap, float x, float y, float width, float height)
    {
        int opacity = Math.Max(10, Math.Min(100, _config.TextOpacityPercent));
        if (opacity == 100)
        {
            if (width == bitmap.Width && height == bitmap.Height) TextBlit.Draw(graphics, bitmap, x, y);
            else graphics.DrawImage(bitmap, x, y, width, height);
            return;
        }
        if (_lastTextOpacity != opacity || _textAlpha == null)
        {
            if (_textAlpha != null) _textAlpha.Dispose();
            _textAlpha = new System.Drawing.Imaging.ImageAttributes();
            System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix(); matrix.Matrix33 = opacity / 100f;
            _textAlpha.SetColorMatrix(matrix); _lastTextOpacity = opacity;
        }
        Rectangle target = new Rectangle((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
        graphics.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, _textAlpha);
    }
    private bool IsBlockedByRules(DanmakuItem item)
    {
        if (item == null || !_config.Enabled || item.UserLevel < _config.MinUserLevel
            || item.MedalLevel < _config.MinMedalLevel) return true;
        if ((item.Mode <= 3 && !item.AdvancedText && !_config.ShowScrolling) || (item.Mode == 4 && !_config.ShowBottom)
            || (item.Mode == 5 && !_config.ShowTop) || (item.Mode == 6 && !_config.ShowReverse)
            || ((item.Mode == 7 || item.AdvancedText) && !_config.ShowAdvancedText)) return true;
        if (!_config.ShowColored && item.Color != 0 && (item.Color & 0xFFFFFF) != 0xFFFFFF) return true;
        return _rules.User(item, _config.BlockUsers) || _rules.Any(item.Text, _config.BlockKeywords);
    }
    private bool HitsReservedArea(FlyingDanmaku flying, float y, float life)
    {
        if (!_eventReserved.IsEmpty && y < _eventReserved.Bottom && y + flying.Height > _eventReserved.Top
            && PathsOverlap(flying.X, flying.Width, flying.Speed, _eventReserved.X, _eventReserved.Width, 0f, life, 6f * _uiScale)) return true;
        foreach (string area in _config.ReservedAreas ?? new string[0])
        {
            RectangleF percent;
            if (!DanmakuRegions.TryParse(area, out percent)) continue;
            RectangleF rect = new RectangleF(percent.X * _width / 100f, percent.Y * _height / 100f,
                percent.Width * _width / 100f, percent.Height * _height / 100f);
            if (y < rect.Bottom && y + flying.Height > rect.Top
                && PathsOverlap(flying.X, flying.Width, flying.Speed, rect.X, rect.Width, 0f, life, 4f * _uiScale)) return true;
        }
        return false;
    }
    private DanmakuItem PrepareDanmaku(DanmakuItem original)
    {
        DanmakuItem item = original;
        if (item != null && item.Mode == 7 && _config.ShowAdvancedText)
        {
            if (item.Text == null || item.Text.Length > 16384) return null;
            try
            {
                object[] data = new JavaScriptSerializer().DeserializeObject(item.Text ?? "") as object[];
                if (data == null || data.Length <= 4 || !(data[4] is string)) return null;
                string text = (string)data[4]; if (text.Length == 0 || text.Length > 1024) return null;
                item = item.Copy(); item.Mode = 1; item.Text = text; item.AdvancedText = true;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        if (item != null && _config.ShowTop && _rules.Any(item.Text, _config.PinKeywords))
        { item = item.Copy(); item.Mode = 5; item.Pinned = true; }
        return item;
    }
}
