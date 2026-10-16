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
    internal static string EditorPath()
    {
        try
        {
            string notepad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            return File.Exists(notepad) ? notepad : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>把配置文件交给用户编辑。</summary>
    ///
    /// ⚠️ 为什么不用 `Process.Start("notepad.exe", path)`（2026-09-27 改）：
    ///    那句走 ShellExecute（默认 UseShellExecute=true），要靠**系统文件关联**找记事本 ——
    ///    .json / .txt 可能关联第三方编辑器，商店版记事本也可能经过重定向；
    ///    只要这条链断一节，Windows 就会弹出「你要如何打开这个文件？」选择框，
    ///    而那个框是**死路**：选完也改不到配置，还挡住了直播画面。
    ///    现在两步都不依赖关联：记事本用**绝对路径**直接 CreateProcess（UseShellExecute=false）；
    ///    没有记事本就退到「资源管理器里选中这个文件」，Explorer 同样不会弹选择框。
    ///    返回是否成功；失败时调用方把路径显示给用户，而不是静默什么都没发生。
    internal static bool OpenInEditor(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string editor = EditorPath();
        if (editor != null)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(editor, "\"" + path + "\"");
                start.UseShellExecute = false;     // 关键：不走关联，就不会有选择框
                Process.Start(start);
                return true;
            }
            catch (Exception ex) { Diagnostics.Report("open-editor", ex); }
        }
        try
        {
            ProcessStartInfo fallback = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
            fallback.UseShellExecute = false;
            Process.Start(fallback);
            return true;
        }
        catch (Exception ex) { Diagnostics.Report("open-editor-fallback", ex); }
        return false;
    }

    /// 覆盖层进程注册的「往画面上飘一行字」。非 null 就说明**本进程是弹幕覆盖层**：
    /// 内部错误只记日志 + 飘字，不弹模态框（见 ThreadException 处理器）。
    internal static Action<string> OverlayMessage;

    internal static string PlatformName(string platform)
    {
        return Platforms.Name(platform);
    }

    // 中转地址：本地回环 + 本次会话的 32 位十六进制令牌。不带令牌中转直接 403。
    // 挡的是本地无关进程误连、或用 8 条连接上限把播放挤掉；这**不是**同机恶意进程的边界
    //（会话号在命令行上可见，真要防得住得改用环境变量或继承句柄传令牌）。
    private static string RelayUrl(Target target, string port)
    {
        return "http://127.0.0.1:" + port + "/live.flv?t=" + target.SessionId;
    }

    private static bool RelayRequestAllowed(string requestLine)
    {
        if (RelaySessionId == null) return true;   // 无令牌模式：旧命令行、离线测试直接调 Pump
        Match match = Regex.Match(requestLine ?? "", @"^[A-Za-z]+\s+\S*[?&]t=([0-9a-fA-F]{32})(?:\s|$)");
        return match.Success
            && string.Equals(match.Groups[1].Value, RelaySessionId, StringComparison.OrdinalIgnoreCase);
    }

    internal static void AtomicWriteAllText(string path, string content)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
    }

    private static Target Parse(string value, string selectedPlatform)
    {
        if (value == null) return null;
        string room = value.Trim();
        string platform = selectedPlatform;
        if (room.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Uri uri;
            if (!Uri.TryCreate(room, UriKind.Absolute, out uri) || uri.Scheme != "https") return null;
            string host = uri.Host.ToLowerInvariant();
            if (host == "live.bilibili.com") platform = "bili";
            else if (host == "www.huya.com" || host == "huya.com") platform = "huya";
            else if (host == "www.douyu.com" || host == "douyu.com" || host == "m.douyu.com") platform = "douyu";
            else if (host == "live.douyin.com" || host == "www.douyin.com") platform = "douyin";
            else return null;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            room = parts[0];
            if (platform == "bili" && room == "blanc") room = parts.Length > 1 ? parts[1] : "";
        }
        else
        {
            int colon = room.IndexOf(':');
            if (colon > 0)
            {
                platform = room.Substring(0, colon).ToLowerInvariant();
                room = room.Substring(colon + 1);
            }
        }
        // 房间号规则与站点根地址都来自适配器表（2026-09-27，评估报告 §7）：加平台不用再改 Parse。
        IPlatformAdapter adapter = Platforms.Get(platform);
        if (adapter == null) return null;
        if (!Regex.IsMatch(room, adapter.RoomPattern)) return null;
        return new Target { Platform = platform, Room = room, Url = adapter.BaseUrl + room };
    }

    // ---------------- 收藏 / 历史 ----------------

    private static List<KeyValuePair<string, string>> LoadFavorites()
    {
        return LoadFavoritesCore(false);
    }

    private sealed class FavoriteSnapshot : List<KeyValuePair<string, string>>
    {
        internal readonly List<KeyValuePair<string, string>> Original;
        internal readonly HashSet<string> ChangedRooms = new HashSet<string>();
        internal FavoriteSnapshot(IEnumerable<KeyValuePair<string, string>> entries) : base(entries)
        { Original = new List<KeyValuePair<string, string>>(this); }
    }

    private static List<KeyValuePair<string, string>> CreateFavoritesUpdate(List<KeyValuePair<string, string>> favorites)
    {
        return new FavoriteSnapshot(favorites);
    }

    private static void MarkFavoriteChanged(List<KeyValuePair<string, string>> favorites, string room)
    {
        var snapshot = favorites as FavoriteSnapshot;
        if (snapshot != null) snapshot.ChangedRooms.Add(FavoriteRoomKey(room));
    }

    private static List<KeyValuePair<string, string>> LoadFavoritesCore(bool strict)
    {
        if (File.Exists(Favorites))
        {
            try
            {
                string json = File.ReadAllText(Favorites, Encoding.UTF8);
                var result = new List<KeyValuePair<string, string>>();
                var serializer = new JavaScriptSerializer();
                if (json.TrimStart().StartsWith("["))
                {
                    var entries = serializer.Deserialize<List<Dictionary<string, string>>>(json);
                    if (strict && entries == null) throw new InvalidDataException("收藏文件内容为空");
                    if (entries != null)
                        foreach (var entry in entries)
                        {
                            string name, room;
                            if (entry != null && entry.TryGetValue("name", out name) && entry.TryGetValue("room", out room)
                                && !string.IsNullOrWhiteSpace(name) && Parse(room, "bili") != null)
                                result.Add(new KeyValuePair<string, string>(name, room));
                        }
                }
                else
                {
                    // 旧文件为 {收藏名: 房间号}，读取后首次保存自动迁移成列表。
                    var data = serializer.Deserialize<Dictionary<string, string>>(json);
                    if (strict && data == null) throw new InvalidDataException("收藏文件内容为空");
                    if (data != null)
                        foreach (var entry in data)
                            if (Parse(entry.Value, "bili") != null) result.Add(entry);
                }
                return new FavoriteSnapshot(result);
            }
            catch (Exception ex)
            {
                if (strict) throw;
                MessageBox.Show("收藏文件无法读取：\n" + ex.Message, "看直播", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new FavoriteSnapshot(new List<KeyValuePair<string, string>>());
            }
        }
        // 没有收藏文件 = 全新安装：**给空的**（2026-10-16 改）。
        // 原来这里塞了两条开发期示例（德云色 7777 / LPL赛事 6），发到公开包就成了
        // 「作者预置的收藏」——用户第一次打开看到的应该是自己的空列表。
        return new FavoriteSnapshot(new List<KeyValuePair<string, string>>());
    }

    private static void SaveFavorites(List<KeyValuePair<string, string>> favorites)
    {
        WithDataWriteLock(Favorites, delegate {
            var latest = LoadFavoritesCore(true);
            var snapshot = favorites as FavoriteSnapshot;
            var original = new Dictionary<string, string>();
            if (snapshot != null) foreach (var entry in snapshot.Original) original[FavoriteRoomKey(entry.Value)] = entry.Key;
            var desired = new Dictionary<string, string>();
            foreach (var entry in favorites) desired[FavoriteRoomKey(entry.Value)] = entry.Key;
            latest.RemoveAll(delegate(KeyValuePair<string, string> entry) {
                string key = FavoriteRoomKey(entry.Value);
                return original.ContainsKey(key) && !desired.ContainsKey(key);
            });
            foreach (var entry in favorites)
            {
                string key = FavoriteRoomKey(entry.Value), oldName;
                if (original.TryGetValue(key, out oldName) && oldName == entry.Key && !snapshot.ChangedRooms.Contains(key)) continue;
                latest.RemoveAll(delegate(KeyValuePair<string, string> old) { return FavoriteRoomKey(old.Value) == key; });
                latest.Add(entry);
            }
            var data = new List<Dictionary<string, string>>();
            foreach (var entry in latest)
                data.Add(new Dictionary<string, string> { { "name", entry.Key }, { "room", entry.Value } });
            AtomicWriteAllText(Favorites, new JavaScriptSerializer().Serialize(data));
            favorites.Clear(); favorites.AddRange(latest);
            if (snapshot != null) { snapshot.Original.Clear(); snapshot.Original.AddRange(latest); snapshot.ChangedRooms.Clear(); }
        });
    }

    private static string FavoriteRoomKey(string value)
    {
        Target target = Parse(value, "bili");
        if (target == null) throw new InvalidDataException("收藏房间无效");
        return target.Platform + ":" + target.Room;
    }

    private static void WithDataWriteLock(string path, Action write)
    {
        string key;
        using (SHA256 hash = SHA256.Create())
            key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        using (Mutex mutex = new Mutex(false, "Local\\LiveHub.Data." + key))
        {
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("数据正在被另一个进程保存");
                write();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    private sealed class HistorySnapshot : Dictionary<string, List<HistoryEntry>>
    {
        internal readonly HashSet<string> ChangedRooms = new HashSet<string>();
    }

    // 读取失败的快照可以记录本次播放，但不能在文件恢复可写后覆盖未读出的原历史。
    private sealed class UnreadableHistory : Dictionary<string, List<HistoryEntry>> { }
    private const string HistoryReadFailureMessage = "历史读取失败，本次启动不保存历史；请解除占用或修复文件后重启";

    private static string ReadHistoryText()
    {
        try { return File.ReadAllText(History, Encoding.UTF8); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static Dictionary<string, List<HistoryEntry>> LoadHistory()
    {
        try
        {
            string json = ReadHistoryText();
            if (json != null)
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, List<HistoryEntry>>>(json);
                if (data != null)
                {
                    var clean = new HistorySnapshot();
                    foreach (var pair in data)
                    {
                        if (Platforms.Get(pair.Key) == null || pair.Value == null) continue;
                        var entries = new List<HistoryEntry>();
                        var rooms = new HashSet<string>(StringComparer.Ordinal);
                        foreach (HistoryEntry entry in pair.Value)
                        {
                            if (entry == null) continue;
                            Target target = Parse(entry.Room, pair.Key);
                            if (target == null || target.Platform != pair.Key || !rooms.Add(target.Room)) continue;
                            entries.Add(new HistoryEntry { Room = target.Room, Name = entry.Name, At = entry.At });
                            if (entries.Count >= 15) break;
                        }
                        clean[pair.Key] = entries;
                    }
                    return clean;
                }
                throw new InvalidDataException("历史文件内容为空");
            }
        }
        catch (Exception ex) { Diagnostics.Report("历史读取", ex); return new UnreadableHistory(); }
        return new HistorySnapshot();
    }

    private static bool SaveHistory(Dictionary<string, List<HistoryEntry>> history)
    {
        if (history is UnreadableHistory) return false;
        try
        {
            WithDataWriteLock(History, delegate {
                var latest = LoadHistory();
                if (latest is UnreadableHistory) throw new IOException("无法读取最新历史，已停止保存");
                var snapshot = history as HistorySnapshot;
                foreach (var pair in history)
                    for (int i = pair.Value.Count - 1; i >= 0; i--)
                    {
                        HistoryEntry entry = pair.Value[i];
                        if (snapshot != null && !snapshot.ChangedRooms.Contains(pair.Key + ":" + entry.Room)) continue;
                        AddHistory(latest, Parse(pair.Key + ":" + entry.Room, "bili"), entry.Name);
                    }
                AtomicWriteAllText(History, new JavaScriptSerializer().Serialize(latest));
                history.Clear(); foreach (var pair in latest) history[pair.Key] = pair.Value;
                if (snapshot != null) snapshot.ChangedRooms.Clear();
            });
            return true;
        }
        catch (Exception ex) { Diagnostics.Report("历史保存", ex); return false; }
    }

    private static void AddHistory(Dictionary<string, List<HistoryEntry>> history, Target target, string name)
    {
        List<HistoryEntry> list;
        if (!history.TryGetValue(target.Platform, out list) || list == null)
        {
            list = new List<HistoryEntry>();
            history[target.Platform] = list;
        }
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Room == target.Room) list.RemoveAt(i);
        HistoryEntry entry = new HistoryEntry();
        entry.Room = target.Room;
        entry.Name = name;
        entry.At = DateTime.Now.ToString("MM-dd HH:mm");
        list.Insert(0, entry);
        var snapshot = history as HistorySnapshot;
        if (snapshot != null) snapshot.ChangedRooms.Add(target.Platform + ":" + target.Room);
        while (list.Count > 15)
        {
            if (snapshot != null) snapshot.ChangedRooms.Remove(target.Platform + ":" + list[list.Count - 1].Room);
            list.RemoveAt(list.Count - 1);
        }
    }

}
