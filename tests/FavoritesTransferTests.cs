// --------------------------------------------------------------------------------------------
// 收藏批量导出 / 导入（1.1.4）的离线回归。
//
// 只测纯逻辑：导出 JSON 形状/校验、四种导入格式、合并计数、往返、文件级错误与默认文件名。
// 不启动播放器、不联网、不读真实收藏目录（文件级用例只写临时目录，结束后删除）。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static partial class HardeningTests
{
    private static void RegisterFavoritesTransferCases()
    {
        TestRunner.Add("收藏导入导出", "导出形状：字段名、平台前缀、pretty 与空收藏", TestFavoritesExportShape);
        TestRunner.Add("收藏导入导出", "导出校验：名称/房间/条数上限", TestFavoritesExportValidation);
        TestRunner.Add("收藏导入导出", "导入原版数组：裸数字 B站、链接、名称 trim", TestFavoritesImportLegacyArray);
        TestRunner.Add("收藏导入导出", "导入 Rust target 对象：平台别名与大小写", TestFavoritesImportRustTarget);
        TestRunner.Add("收藏导入导出", "导入旧 map 与结构化 favorites", TestFavoritesImportMapAndStructured);
        TestRunner.Add("收藏导入导出", "合并计数：新增/更新/跳过与文件内重复", TestFavoritesImportMerge);
        TestRunner.Add("收藏导入导出", "往返：导出再导入得到原收藏", TestFavoritesRoundTrip);
        TestRunner.Add("收藏导入导出", "文件级错误：不存在/过大/坏 JSON/无有效条目/非 UTF-8", TestFavoritesImportFileErrors);
        TestRunner.Add("收藏导入导出", "导出文件名格式", TestFavoritesExportFileName);
    }

    // ---------------- 夹具与反射辅助 ----------------

    private static List<KeyValuePair<string, string>> FavoritesList(params string[] pairs)
    {
        if (pairs.Length % 2 != 0) throw new ArgumentException("收藏夹具必须 name/room 成对出现");
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < pairs.Length; i += 2)
            list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
        return list;
    }

    private sealed class FavoritesImportView
    {
        public int Added, Updated, Skipped;
        public List<KeyValuePair<string, string>> Favorites;
    }

    private static FavoritesImportView ReadFavoritesImport(object raw)
    {
        Type type = raw.GetType();
        FavoritesImportView view = new FavoritesImportView();
        view.Added = (int)type.GetField("Added", ReviewInstance).GetValue(raw);
        view.Updated = (int)type.GetField("Updated", ReviewInstance).GetValue(raw);
        view.Skipped = (int)type.GetField("Skipped", ReviewInstance).GetValue(raw);
        view.Favorites = (List<KeyValuePair<string, string>>)type.GetField("Favorites", ReviewInstance).GetValue(raw);
        return view;
    }

    private static FavoritesImportView ImportFavoritesText(string json, List<KeyValuePair<string, string>> existing)
    {
        return ReadFavoritesImport(Call("ImportFavoritesText", json, existing));
    }

    private static FavoritesImportView ImportFavoritesFile(string path, List<KeyValuePair<string, string>> existing)
    {
        return ReadFavoritesImport(Call("ImportFavoritesFile", path, existing));
    }

    private static string ExportFavorites(List<KeyValuePair<string, string>> favorites)
    {
        return (string)Call("ExportFavoritesJson", favorites);
    }

    /// <summary>执行可能抛异常的操作并返回真实异常（反射的 TargetInvocationException 已拆开）；不抛返回 null。</summary>
    private static Exception FavoritesTransferFailure(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
        return null;
    }

    private static bool FavoritesErrorContains(Exception error, Type expectedType, string needle)
    {
        return error != null && expectedType.IsInstanceOfType(error)
            && (needle == null || error.Message.IndexOf(needle, StringComparison.Ordinal) >= 0);
    }

    private static bool FavoritesHas(List<KeyValuePair<string, string>> favorites, string name, string room)
    {
        foreach (KeyValuePair<string, string> entry in favorites)
            if (entry.Key == name && entry.Value == room) return true;
        return false;
    }

    // ---------------- 1. 导出形状 ----------------

    private static void TestFavoritesExportShape()
    {
        List<KeyValuePair<string, string>> favorites = FavoritesList(
            "合成主播A", "123456",
            "合成主播B", "huya:456789",
            "合成主播C", "bili:777777");
        string json = ExportFavorites(favorites);
        Check(json != null && json.StartsWith("[\n", StringComparison.Ordinal) && json.EndsWith("\n", StringComparison.Ordinal),
            "导出：数组以 pretty 缩进开始并以换行结束");
        Check(json.IndexOf("\"name\": \"合成主播A\"", StringComparison.Ordinal) >= 0
            && json.IndexOf("\"room\": \"bili:123456\"", StringComparison.Ordinal) >= 0,
            "导出：name/room 字段名正确且 B站也带 bili: 前缀");
        Check(json.IndexOf("\"room\": \"huya:456789\"", StringComparison.Ordinal) >= 0,
            "导出：其它平台保留 platform:room 前缀");
        Check(json.IndexOf("bili:777777", StringComparison.Ordinal) >= 0,
            "导出：原本写成 bili: 的房间规整为同一前缀");
        List<Dictionary<string, string>> parsed = new JavaScriptSerializer().Deserialize<List<Dictionary<string, string>>>(json);
        Check(parsed != null && parsed.Count == 3 && parsed[0].Count == 2,
            "导出：重新解析得到三条记录且每条只有两个字段");
        Check(parsed != null && parsed[0]["name"] == "合成主播A" && parsed[0]["room"] == "bili:123456"
            && parsed[1]["room"] == "huya:456789" && parsed[2]["room"] == "bili:777777",
            "导出：重新解析后 name/room 与收藏一致");
        Check(json.IndexOf("\n  {\n    \"name\": ", StringComparison.Ordinal) >= 0,
            "导出：缩进稳定为两空格、四空格");
        Check(ExportFavorites(new List<KeyValuePair<string, string>>()) == "[]\n",
            "导出：空收藏恰好是 [] 加换行");
    }

    // ---------------- 2. 导出校验 ----------------

    private static void TestFavoritesExportValidation()
    {
        Exception error = FavoritesTransferFailure(delegate { ExportFavorites(FavoritesList("   ", "123456")); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "没有名称"), "导出：空白名称被拒绝");
        error = FavoritesTransferFailure(delegate { ExportFavorites(FavoritesList("合\n成", "123456")); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "控制字符"), "导出：名称含换行控制字符被拒绝");
        string longName = new string('合', 257);
        error = FavoritesTransferFailure(delegate { ExportFavorites(FavoritesList(longName, "123456")); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "名称过长"), "导出：超过 256 字符的名称被拒绝");
        string maxName = new string('合', 256);
        string maxJson = ExportFavorites(FavoritesList(maxName, "123456"));
        Check(maxJson.IndexOf(maxName, StringComparison.Ordinal) >= 0, "导出：恰好 256 字符的名称仍可导出");
        error = FavoritesTransferFailure(delegate { ExportFavorites(FavoritesList("坏房间", "not-a-room")); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "房间无效"), "导出：无法解析的房间被拒绝");
        error = FavoritesTransferFailure(delegate { ExportFavorites(FavoritesList("坏B站房间", "bili:abc")); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "房间无效"), "导出：带前缀但不符合房间规则被拒绝");
        List<KeyValuePair<string, string>> tooMany = new List<KeyValuePair<string, string>>();
        for (int i = 0; i <= 10000; i++)
            tooMany.Add(new KeyValuePair<string, string>("合成" + i, (100000 + i).ToString()));
        error = FavoritesTransferFailure(delegate { ExportFavorites(tooMany); });
        Check(FavoritesErrorContains(error, typeof(InvalidDataException), "超过导出上限"),
            "导出：10001 条超过 10000 条上限并报中文错误");
        List<KeyValuePair<string, string>> limit = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < 10000; i++)
            limit.Add(new KeyValuePair<string, string>("合成" + i, (100000 + i).ToString()));
        string limitJson = ExportFavorites(limit);
        Check(limitJson != null && limitJson.IndexOf("\"合成9999\"", StringComparison.Ordinal) >= 0,
            "导出：恰好 10000 条仍可导出");
    }

    // ---------------- 3. 原版 C# 数组 ----------------

    private static void TestFavoritesImportLegacyArray()
    {
        string json = "[{\"name\":\"合成主播A\",\"room\":\"123456\"},"
            + "{\"name\":\"  合成主播B  \",\"room\":\"huya:456789\"},"
            + "{\"name\":\"链接主播\",\"room\":\"https://live.bilibili.com/234567\"}]";
        FavoritesImportView view = ImportFavoritesText(json, new List<KeyValuePair<string, string>>());
        Check(view.Added == 3 && view.Updated == 0 && view.Skipped == 0, "导入：原版数组新增 3 条且无跳过");
        Check(view.Favorites.Count == 3, "导入：合并结果保留全部 3 条");
        Check(FavoritesHas(view.Favorites, "合成主播A", "123456"), "导入：裸数字房间按 B站存成裸房间号");
        Check(FavoritesHas(view.Favorites, "合成主播B", "huya:456789"), "导入：其它平台保留前缀且名称 trim 后写入");
        Check(FavoritesHas(view.Favorites, "链接主播", "234567"), "导入：官方直播间链接按平台解析并规整为裸房间号");
    }

    // ---------------- 4. Rust target 对象 ----------------

    private static void TestFavoritesImportRustTarget()
    {
        string json = "["
            + "{\"name\":\"合成RustB站\",\"target\":{\"platform\":\"bili\",\"room\":\"123456\"}},"
            + "{\"name\":\"合成Rust虎牙\",\"target\":{\"platform\":\"HUYA\",\"room\":\"456789\"}},"
            + "{\"name\":\"合成Rust斗鱼\",\"target\":{\"platform\":\"douyu\",\"room\":\"952595\"}},"
            + "{\"name\":\"合成Rust抖音\",\"target\":{\"platform\":\"Douyin\",\"room\":\"123456789012\"}},"
            + "{\"name\":\"别名B站\",\"target\":{\"platform\":\"bilibili\",\"room\":\"234567\"}}]";
        FavoritesImportView view = ImportFavoritesText(json, new List<KeyValuePair<string, string>>());
        Check(view.Added == 5 && view.Updated == 0 && view.Skipped == 0, "导入：Rust 导出 5 条全部识别");
        Check(FavoritesHas(view.Favorites, "合成RustB站", "123456"), "导入：target bili 存成裸房间号");
        Check(FavoritesHas(view.Favorites, "合成Rust虎牙", "huya:456789"), "导入：platform 大写 HUYA 兼容");
        Check(FavoritesHas(view.Favorites, "合成Rust斗鱼", "douyu:952595"), "导入：target douyu 规整为前缀");
        Check(FavoritesHas(view.Favorites, "合成Rust抖音", "douyin:123456789012"), "导入：platform 大小写混合兼容");
        Check(FavoritesHas(view.Favorites, "别名B站", "234567"), "导入：bilibili 别名按 B站处理");
        string bad = "[{\"name\":\"坏平台\",\"target\":{\"platform\":\"kuaishou\",\"room\":\"1\"}},"
            + "{\"name\":\"好条目\",\"target\":{\"platform\":\"bili\",\"room\":\"123456\"}}]";
        FavoritesImportView mixed = ImportFavoritesText(bad, new List<KeyValuePair<string, string>>());
        Check(mixed.Added == 1 && mixed.Skipped == 1, "导入：不认识的平台按非法条目跳过");
    }

    // ---------------- 5. 旧 map / 结构化 ----------------

    private static void TestFavoritesImportMapAndStructured()
    {
        string map = "{\"合成主播M\":\"123456\",\"合成虎牙M\":\"huya:456789\",\"坏值\":123}";
        FavoritesImportView view = ImportFavoritesText(map, new List<KeyValuePair<string, string>>());
        Check(view.Added == 2 && view.Skipped == 1, "导入：旧 map 两条有效、非字符串值跳过");
        Check(FavoritesHas(view.Favorites, "合成主播M", "123456") && FavoritesHas(view.Favorites, "合成虎牙M", "huya:456789"),
            "导入：旧 map 的收藏名与房间正确规整");
        string structured = "{\"favorites\":["
            + "{\"name\":\"合成主播F\",\"room\":\"765432\"},"
            + "{\"name\":\"结构化Rust\",\"target\":{\"platform\":\"douyu\",\"room\":\"952595\"}}],"
            + "\"history\":[{\"Room\":\"1\",\"Name\":\"历史\",\"At\":\"2000-01-01 12:00\"}]}";
        FavoritesImportView structuredView = ImportFavoritesText(structured, new List<KeyValuePair<string, string>>());
        Check(structuredView.Added == 2 && structuredView.Skipped == 0, "导入：结构化 favorites 两种条目都识别");
        Check(FavoritesHas(structuredView.Favorites, "合成主播F", "765432")
            && FavoritesHas(structuredView.Favorites, "结构化Rust", "douyu:952595"),
            "导入：结构化 favorites 只取收藏、忽略 history");
        bool historyLeaked = false;
        foreach (KeyValuePair<string, string> entry in structuredView.Favorites)
            if (entry.Key == "历史") historyLeaked = true;
        Check(!historyLeaked, "导入：history 不会被当成收藏");
    }

    // ---------------- 6. 合并计数 ----------------

    private static void TestFavoritesImportMerge()
    {
        List<KeyValuePair<string, string>> existing = FavoritesList(
            "旧主播", "123456",
            "虎牙旧", "huya:456789");
        string json = "[{\"name\":\"新名A\",\"room\":\"bili:123456\"},"
            + "{\"name\":\"虎牙新名\",\"room\":\"huya:456789\"},"
            + "{\"name\":\"新增斗鱼\",\"room\":\"douyu:952595\"},"
            + "{\"name\":\"\",\"room\":\"123456\"},"
            + "{\"name\":\"坏房间\",\"room\":\"不是房间\"},"
            + "{\"name\":\"新增斗鱼改名\",\"room\":\"douyu:952595\"}]";
        FavoritesImportView view = ImportFavoritesText(json, existing);
        Check(view.Added == 1 && view.Updated == 3 && view.Skipped == 2,
            "合并：新增 1、更新 3（含文件内重复）、跳过 2（实得 新增"
            + view.Added + " 更新" + view.Updated + " 跳过" + view.Skipped + "）");
        Check(view.Favorites.Count == 3, "合并：同平台同房间不产生重复收藏");
        Check(view.Favorites[0].Key == "新名A" && view.Favorites[0].Value == "123456",
            "合并：已存在的收藏原位覆盖名称并规整房间");
        Check(view.Favorites[1].Key == "虎牙新名" && view.Favorites[1].Value == "huya:456789",
            "合并：其它平台已存在收藏也原位覆盖");
        Check(view.Favorites[2].Key == "新增斗鱼改名" && view.Favorites[2].Value == "douyu:952595",
            "合并：文件内重复后者生效且新增在现有之后");
    }

    // ---------------- 7. 往返 ----------------

    private static void TestFavoritesRoundTrip()
    {
        List<KeyValuePair<string, string>> original = FavoritesList(
            "路线\"A\"\\B", "123456",
            "跨平台 主播", "huya:456789",
            "B站裸号", "777777");
        string json = ExportFavorites(original);
        FavoritesImportView view = ImportFavoritesText(json, new List<KeyValuePair<string, string>>());
        Check(view.Added == 3 && view.Updated == 0 && view.Skipped == 0, "往返：导出文件可被导入且计为新增 3 条");
        Check(view.Favorites.Count == 3, "往返：导入后收藏数量与导出前一致");
        int matched = 0;
        foreach (KeyValuePair<string, string> entry in original)
            if (FavoritesHas(view.Favorites, entry.Key, entry.Value)) matched++;
        Check(matched == 3, "往返：名称（含引号/反斜杠）与房间逐条一致");
        FavoritesImportView again = ImportFavoritesText(json, original);
        Check(again.Added == 0 && again.Updated == 3 && again.Skipped == 0,
            "往返：同一文件再次导入计为更新且不新增");
        Check(again.Favorites.Count == 3, "往返：重复导入不产生重复收藏");
    }

    // ---------------- 8. 文件级错误 ----------------

    private static void TestFavoritesImportFileErrors()
    {
        string directory = ReviewDirectory();
        try
        {
            List<KeyValuePair<string, string>> none = new List<KeyValuePair<string, string>>();
            string missing = Path.Combine(directory, "missing.json");
            Exception error = FavoritesTransferFailure(delegate { ImportFavoritesFile(missing, none); });
            Check(error is FileNotFoundException, "文件导入：不存在的文件抛明确异常");
            string tooBig = Path.Combine(directory, "too-big.json");
            File.WriteAllBytes(tooBig, new byte[2 * 1024 * 1024 + 1]);
            error = FavoritesTransferFailure(delegate { ImportFavoritesFile(tooBig, none); });
            Check(FavoritesErrorContains(error, typeof(InvalidDataException), "过大"), "文件导入：超过 2 MiB 被拒绝");
            string badJson = Path.Combine(directory, "bad.json");
            File.WriteAllText(badJson, "{这不是 JSON", new UTF8Encoding(false));
            error = FavoritesTransferFailure(delegate { ImportFavoritesFile(badJson, none); });
            Check(FavoritesErrorContains(error, typeof(InvalidDataException), "JSON"), "文件导入：坏 JSON 给明确异常");
            string noValid = Path.Combine(directory, "no-valid.json");
            File.WriteAllText(noValid, "[{\"name\":\"\",\"room\":\"\"},{\"name\":\"坏房间\",\"room\":\"不是房间\"}]",
                new UTF8Encoding(false));
            error = FavoritesTransferFailure(delegate { ImportFavoritesFile(noValid, none); });
            Check(FavoritesErrorContains(error, typeof(InvalidDataException), "没有有效的收藏条目"),
                "文件导入：没有任何有效条目时抛中文异常");
            string notUtf8 = Path.Combine(directory, "not-utf8.json");
            File.WriteAllBytes(notUtf8, new byte[] { 0x5B, 0xFF, 0xFE, 0x5D });
            error = FavoritesTransferFailure(delegate { ImportFavoritesFile(notUtf8, none); });
            Check(FavoritesErrorContains(error, typeof(InvalidDataException), "UTF-8"), "文件导入：非 UTF-8 文本被拒绝");
            string okPath = Path.Combine(directory, "ok.json");
            File.WriteAllText(okPath, "[{\"name\":\"合成文件主播\",\"room\":\"345678\"}]", new UTF8Encoding(false));
            FavoritesImportView view = ImportFavoritesFile(okPath, none);
            Check(view.Added == 1 && FavoritesHas(view.Favorites, "合成文件主播", "345678"),
                "文件导入：合法文件正常合并");
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---------------- 9. 默认文件名 ----------------

    private static void TestFavoritesExportFileName()
    {
        DateTime when = new DateTime(2026, 10, 31, 9, 5, 7);
        string name = (string)Call("FavoritesExportFileName", when);
        Check(name == "live-hub-favorites-20261031-0905.json", "文件名：按本地时间生成 YYYYMMDD-HHMM");
        Check(Regex.IsMatch(name, @"^live-hub-favorites-\d{8}-\d{4}\.json$"), "文件名：格式固定");
        bool ascii = true;
        foreach (char c in name) if (c > 127) ascii = false;
        Check(ascii && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "文件名：纯 ASCII 且不含非法字符");
        DateTime midnight = new DateTime(2026, 1, 2, 0, 0, 0);
        Check((string)Call("FavoritesExportFileName", midnight) == "live-hub-favorites-20260102-0000.json",
            "文件名：零点补零");
    }
}
