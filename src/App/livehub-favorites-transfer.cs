// --------------------------------------------------------------------------------------------
// 收藏批量导出 / 导入（1.1.4）。
//
// 纯逻辑，不依赖任何 WinForms 控件，便于离线回归直接断言：
//   · 导出格式与「看直播」原版收藏数组兼容：[{"name":"…","room":"平台:房间"}]，
//     B站也写成 bili:房间号，Rust 版和本版收藏数组都能读回来；
//   · 导入同时接受四种历史格式（原版数组 / Rust target 对象 / 旧 map / {"favorites":[…]}]）；
//   · 校验规则与 Rust 版 favorites_transfer.rs 对齐：名称 trim 后 1..256、无控制字符，
//     房间必须能被平台适配器解析；文件 2 MiB、条目 10000 条上限。
//
// 这里不碰用户数据：文件路径由界面选择、读写由调用方决定；本文件只做字符串/对象转换。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    internal const int FavoritesTransferMaxBytes = 2 * 1024 * 1024;
    internal const int FavoritesTransferMaxEntries = 10000;
    internal const int FavoritesTransferMaxNameChars = 256;

    /// <summary>一次导入的结果：新增/更新/跳过计数 + 合并后的完整收藏（现有在前、新增在后、更新保持原位）。</summary>
    internal sealed class FavoritesImportResult
    {
        internal int Added;
        internal int Updated;
        internal int Skipped;
        internal List<KeyValuePair<string, string>> Favorites;
    }

    /// <summary>
    /// 收藏 → 纯 JSON 数组文本。输出 name/room 对象的 pretty 缩进 + 末尾换行：
    /// 空收藏得到 "[]\n"；room 一律带平台前缀（B站也写 bili:房间号），
    /// 这样 Rust 版和本版的导入都能原样读回。字符串转义交给 JavaScriptSerializer。
    /// </summary>
    internal static string ExportFavoritesJson(IEnumerable<KeyValuePair<string, string>> favorites)
    {
        List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
        if (favorites != null) entries.AddRange(favorites);
        if (entries.Count > FavoritesTransferMaxEntries)
        {
            throw new InvalidDataException("收藏数量超过导出上限（" + entries.Count + " 条，最多 "
                + FavoritesTransferMaxEntries + " 条）");
        }
        List<KeyValuePair<string, string>> output = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> entry in entries)
        {
            Target target = Parse(entry.Value, "bili");
            if (target == null) throw new InvalidDataException("收藏「" + (entry.Key == null ? "" : entry.Key) + "」的房间无效，无法导出");
            string name = entry.Key == null ? "" : entry.Key.Trim();
            if (name.Length == 0) throw new InvalidDataException("存在没有名称的收藏，无法导出");
            if (name.Length > FavoritesTransferMaxNameChars) throw new InvalidDataException("收藏「" + name + "」的名称过长，无法导出");
            bool control = false;
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsControl(name[i])) { control = true; break; }
            }
            if (control) throw new InvalidDataException("存在名称包含控制字符的收藏，无法导出");
            output.Add(new KeyValuePair<string, string>(name, target.Platform + ":" + target.Room));
        }
        if (output.Count == 0) return "[]\n";
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        StringBuilder text = new StringBuilder();
        text.Append("[\n");
        for (int i = 0; i < output.Count; i++)
        {
            if (i > 0) text.Append(",\n");
            text.Append("  {\n");
            text.Append("    \"name\": ").Append(serializer.Serialize(output[i].Key)).Append(",\n");
            text.Append("    \"room\": ").Append(serializer.Serialize(output[i].Value)).Append("\n");
            text.Append("  }");
        }
        text.Append("\n]\n");
        return text.ToString();
    }

    /// <summary>保存对话框的默认文件名：live-hub-favorites-YYYYMMDD-HHMM.json（纯 ASCII）。</summary>
    internal static string FavoritesExportFileName(DateTime now)
    {
        return "live-hub-favorites-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".json";
    }

    /// <summary>
    /// 解析导入文本并合并到现有收藏。四种格式：
    /// ① [{"name","room"}]（原版 C# 数组，裸数字按 B站）；
    /// ② [{"name","target":{"platform","room"}}]（Rust 版导出，platform 接受 bili/bilibili/huya/douyu/douyin，大小写不敏感）；
    /// ③ {"名称":"房间"}（旧 map）；
    /// ④ {"favorites":[…]}（结构化，history 忽略）。
    /// 非法条目跳过并计入 Skipped；一个有效条目都没有时抛中文异常。
    /// </summary>
    internal static FavoritesImportResult ImportFavoritesText(string json, IEnumerable<KeyValuePair<string, string>> existing)
    {
        if (json != null && json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
        if (json == null || json.Trim().Length == 0) throw new InvalidDataException("收藏文件内容为空。");
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > FavoritesTransferMaxBytes)
        {
            throw new InvalidDataException("收藏文件过大（" + bytes + " 字节，最多 2 MiB）。");
        }
        int skipped;
        int rawCount;
        List<KeyValuePair<string, string>> imported = ParseFavoritesImport(json, out skipped, out rawCount);
        if (rawCount > FavoritesTransferMaxEntries)
        {
            throw new InvalidDataException("收藏数量超过导入上限（" + rawCount + " 条，最多 "
                + FavoritesTransferMaxEntries + " 条）。");
        }
        if (imported.Count == 0) throw new InvalidDataException("收藏文件里没有有效的收藏条目。");

        FavoritesImportResult result = new FavoritesImportResult();
        result.Skipped = skipped;
        result.Favorites = new List<KeyValuePair<string, string>>();
        Dictionary<string, int> positions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (existing != null)
        {
            foreach (KeyValuePair<string, string> entry in existing)
            {
                Target target = Parse(entry.Value, "bili");
                if (target != null)
                {
                    string key = target.Platform + ":" + target.Room;
                    if (!positions.ContainsKey(key)) positions[key] = result.Favorites.Count;
                }
                result.Favorites.Add(entry);
            }
        }
        foreach (KeyValuePair<string, string> entry in imported)
        {
            Target target = Parse(entry.Value, "bili");
            string key = target.Platform + ":" + target.Room;
            int position;
            if (positions.TryGetValue(key, out position))
            {
                result.Favorites[position] = entry;
                result.Updated++;
            }
            else
            {
                positions[key] = result.Favorites.Count;
                result.Favorites.Add(entry);
                result.Added++;
            }
        }
        return result;
    }

    /// <summary>
    /// 从本地文件导入：不存在 / 超过 2 MiB / 非 UTF-8 都抛明确异常，由界面显示。
    /// 文件只在调用方给定路径上读取，不碰真实收藏目录。
    /// </summary>
    internal static FavoritesImportResult ImportFavoritesFile(string path, IEnumerable<KeyValuePair<string, string>> existing)
    {
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("没有选择要导入的收藏文件。");
        if (!File.Exists(path)) throw new FileNotFoundException("收藏文件不存在：" + path, path);
        long length = new FileInfo(path).Length;
        if (length > FavoritesTransferMaxBytes)
        {
            throw new InvalidDataException("收藏文件过大（" + length + " 字节，最多 2 MiB）。");
        }
        string json;
        try
        {
            json = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("收藏文件不是有效的 UTF-8 文本。");
        }
        return ImportFavoritesText(json, existing);
    }

    // ---------------- 内部解析 ----------------

    private static List<KeyValuePair<string, string>> ParseFavoritesImport(string json, out int skipped, out int rawCount)
    {
        skipped = 0;
        rawCount = 0;
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        // 文件被限制在 2 MiB，与 serializer 默认 MaxJsonLength 重合；显式放宽，边界文件不会误报。
        serializer.MaxJsonLength = int.MaxValue;
        object root;
        try { root = serializer.DeserializeObject(json); }
        catch (Exception ex) { throw new InvalidDataException("收藏文件不是有效的 JSON：" + ex.Message); }
        if (root == null) throw new InvalidDataException("收藏文件内容为空。");

        List<KeyValuePair<string, string>> valid = new List<KeyValuePair<string, string>>();
        IDictionary<string, object> map = root as IDictionary<string, object>;
        if (map != null)
        {
            object favorites;
            object history;
            bool structured = map.TryGetValue("favorites", out favorites) || map.TryGetValue("history", out history);
            if (structured)
            {
                object favoritesValue = null;
                map.TryGetValue("favorites", out favoritesValue);
                if (favoritesValue == null) return valid;
                IList items = favoritesValue as IList;
                if (items == null) throw new InvalidDataException("收藏文件里的 favorites 不是列表。");
                rawCount = items.Count;
                for (int i = 0; i < items.Count; i++)
                {
                    KeyValuePair<string, string> parsed;
                    if (TryParseFavoritesItem(items[i], out parsed)) valid.Add(parsed);
                    else skipped++;
                }
                return valid;
            }
            // 旧格式 {收藏名: 房间号}；值不是字符串的条目跳过。
            rawCount = map.Count;
            foreach (KeyValuePair<string, object> pair in map)
            {
                string room = pair.Value as string;
                if (room == null) { skipped++; continue; }
                Target target = Parse(room.Trim(), "bili");
                string name;
                if (target == null || !ValidFavoriteName(pair.Key, out name)) { skipped++; continue; }
                valid.Add(new KeyValuePair<string, string>(name, CanonicalFavoriteValue(target)));
            }
            return valid;
        }

        IList array = root as IList;
        if (array == null) throw new InvalidDataException("收藏文件不是可识别的收藏格式。");
        rawCount = array.Count;
        for (int i = 0; i < array.Count; i++)
        {
            KeyValuePair<string, string> parsed;
            if (TryParseFavoritesItem(array[i], out parsed)) valid.Add(parsed);
            else skipped++;
        }
        return valid;
    }

    private static bool TryParseFavoritesItem(object item, out KeyValuePair<string, string> parsed)
    {
        parsed = new KeyValuePair<string, string>();
        IDictionary<string, object> map = item as IDictionary<string, object>;
        if (map == null) return false;
        string rawName;
        if (!FavoritesJsonString(map, "name", out rawName)) return false;
        string name;
        if (!ValidFavoriteName(rawName, out name)) return false;
        Target target;
        object targetValue;
        if (map.TryGetValue("target", out targetValue))
        {
            target = ParseFavoritesJsonTarget(targetValue);
        }
        else
        {
            string room;
            if (!FavoritesJsonString(map, "room", out room)) return false;
            target = Parse(room.Trim(), "bili");
        }
        if (target == null) return false;
        parsed = new KeyValuePair<string, string>(name, CanonicalFavoriteValue(target));
        return true;
    }

    private static Target ParseFavoritesJsonTarget(object value)
    {
        IDictionary<string, object> map = value as IDictionary<string, object>;
        if (map == null) return null;
        string platform;
        string room;
        if (!FavoritesJsonString(map, "platform", out platform)) return null;
        if (!FavoritesJsonString(map, "room", out room)) return null;
        string normalized = NormalizeFavoritesPlatform(platform);
        if (normalized == null) return null;
        string trimmed = room.Trim();
        if (trimmed.Length == 0) return null;
        Target target = Parse(normalized + ":" + trimmed, "bili");
        return target != null && target.Platform == normalized ? target : null;
    }

    private static string NormalizeFavoritesPlatform(string platform)
    {
        if (platform == null) return null;
        string normalized = platform.Trim().ToLowerInvariant();
        if (normalized == "bilibili") normalized = "bili";
        return Platforms.Known(normalized) ? normalized : null;
    }

    private static bool FavoritesJsonString(IDictionary<string, object> map, string key, out string value)
    {
        value = null;
        object raw;
        if (!map.TryGetValue(key, out raw)) return false;
        value = raw as string;
        return value != null;
    }

    /// <summary>名称 trim 后 1..256 个字符且不含控制字符；通过时 trimmed 为写入用的名称。</summary>
    private static bool ValidFavoriteName(string name, out string trimmed)
    {
        trimmed = name == null ? "" : name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > FavoritesTransferMaxNameChars) return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsControl(trimmed[i])) return false;
        }
        return true;
    }

    /// <summary>收藏文件里的房间写法：B站存裸房间号（与现有 favorites.json 一致），其它平台存 platform:room。</summary>
    private static string CanonicalFavoriteValue(Target target)
    {
        return target.Platform == "bili" ? target.Room : target.Platform + ":" + target.Room;
    }
}
