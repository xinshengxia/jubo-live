using System.Text.RegularExpressions;

internal enum HuyaLiveState { Unknown, Live, Offline, Replay }

// 启动器与弹幕客户端共用。未识别/请求失败必须是 Unknown，不能误报下播。
internal static class HuyaRoomState
{
    internal static HuyaLiveState ParseApiStatus(string status)
    {
        switch ((status ?? "").Trim().ToUpperInvariant())
        {
            case "ON": return HuyaLiveState.Live;
            case "OFF": return HuyaLiveState.Offline;
            case "REPLAY": return HuyaLiveState.Replay;
            default: return HuyaLiveState.Unknown;
        }
    }

    internal static HuyaLiveState FromRoomState(RoomStateKind state)
    {
        if (state == RoomStateKind.Live) return HuyaLiveState.Live;
        if (state == RoomStateKind.Offline) return HuyaLiveState.Offline;
        if (state == RoomStateKind.Replay) return HuyaLiveState.Replay;
        return HuyaLiveState.Unknown;
    }

    internal static HuyaLiveState Parse(string html)
    {
        Match state = Regex.Match(html ?? "", @"\bclass\s*=\s*[""'][^""']*\bliveStatus-(on|replay|off)\b",
            RegexOptions.IgnoreCase);
        if (!state.Success) return HuyaLiveState.Unknown;
        switch (state.Groups[1].Value.ToLowerInvariant())
        {
            case "on": return HuyaLiveState.Live;
            case "off": return HuyaLiveState.Offline;
            case "replay": return HuyaLiveState.Replay;
            default: return HuyaLiveState.Unknown;
        }
    }

    internal static string Suffix(HuyaLiveState state)
    {
        if (state == HuyaLiveState.Offline) return "（未开播）";
        if (state == HuyaLiveState.Replay) return "（回放中）";
        return "";
    }
}
