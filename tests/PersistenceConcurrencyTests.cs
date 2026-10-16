using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

internal static partial class HardeningTests
{
    private static void RegisterPersistenceConcurrencyCases(string launcher, string self)
    {
        TestRunner.Add("持久化", "多窗口收藏合并不丢新增、改名与删除", TestConcurrentFavorites);
        TestRunner.Add("持久化", "多窗口历史合并与最近记录限额", TestConcurrentHistory);
        TestRunner.Add("持久化", "双进程收藏与历史保存互斥", delegate { TestPersistenceProcesses(launcher, self); });
    }

    private static bool FavoriteHas(List<KeyValuePair<string, string>> entries, string room, string name)
    {
        foreach (var entry in entries)
        {
            object target = Call("Parse", entry.Value, "bili");
            if (target != null && (string)LookupGet(target, "Platform") == "bili"
                && (string)LookupGet(target, "Room") == room && entry.Key == name) return true;
        }
        return false;
    }

    private static void LookupFavoriteForTest(List<KeyValuePair<string, string>> entries, string room, string name)
    {
        object candidate = LookupCandidate("bili", room, room);
        LookupSet(candidate, "verification", "verified"); LookupSet(candidate, "user_confirmed", true);
        Check((bool)Call("SaveLookupFavorite", candidate, name, entries), "核验收藏保存成功：" + room);
    }

    private static void TestConcurrentFavorites()
    {
        string dir = ReviewDirectory(); FieldInfo field = hub.GetField("Favorites", Flags); object old = field.GetValue(null);
        try
        {
            string path = Path.Combine(dir, "favorites.json"); field.SetValue(null, path); File.WriteAllText(path, "[]");
            var first = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            var second = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            LookupFavoriteForTest(first, "101", "窗口A"); LookupFavoriteForTest(second, "102", "窗口B");
            var saved = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(FavoriteHas(saved, "101", "窗口A") && FavoriteHas(saved, "102", "窗口B"),
                "两个旧快照顺序新增收藏后保留双方记录");

            var rename = (List<KeyValuePair<string, string>>)Call("CreateFavoritesUpdate", second);
            rename.RemoveAll(delegate(KeyValuePair<string, string> e) { return e.Value == "bili:101"; });
            rename.Add(new KeyValuePair<string, string>("新名称", "bili:101")); Call("SaveFavorites", rename);
            LookupFavoriteForTest(first, "103", "另一个新增");
            saved = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(FavoriteHas(saved, "101", "新名称"), "旧窗口新增其它房间不会撤销另一个窗口的改名");

            var sameName = (List<KeyValuePair<string, string>>)Call("CreateFavoritesUpdate", second);
            Call("MarkFavoriteChanged", sameName, "bili:101"); Call("SaveFavorites", sameName);
            saved = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(FavoriteHas(saved, "101", "窗口A"), "显式保存同一房间的旧名称仍按最后保存生效");

            var deletion = (List<KeyValuePair<string, string>>)Call("CreateFavoritesUpdate", first);
            deletion.RemoveAll(delegate(KeyValuePair<string, string> e) { return (string)LookupGet(Call("Parse", e.Value, "bili"), "Room") == "101"; });
            Call("SaveFavorites", deletion);
            LookupFavoriteForTest(second, "104", "删除后的新增");
            saved = (List<KeyValuePair<string, string>>)Call("LoadFavorites");
            Check(!FavoriteHas(saved, "101", "新名称") && !FavoriteHas(saved, "101", "窗口A")
                && FavoriteHas(saved, "102", "窗口B") && FavoriteHas(saved, "103", "另一个新增")
                && FavoriteHas(saved, "104", "删除后的新增"), "删除只作用选中房间，旧快照不会复活已删除收藏");
            Check(second.Count == saved.Count, "保存后的窗口快照更新为磁盘合并结果");
        }
        finally { field.SetValue(null, old); Directory.Delete(dir, true); }
    }

    private static void TestConcurrentHistory()
    {
        string dir = ReviewDirectory(); FieldInfo field = hub.GetField("History", Flags); object old = field.GetValue(null);
        try
        {
            string path = Path.Combine(dir, "history.json"); field.SetValue(null, path);
            File.WriteAllText(path, "{\"bili\":[{\"Room\":\"100\",\"Name\":\"原历史\"}]}");
            object first = Call("LoadHistory"), second = Call("LoadHistory");
            Call("AddHistory", first, Call("Parse", "bili:101", "bili"), "窗口A");
            Check((bool)Call("SaveHistory", first), "窗口A保存历史");
            Call("AddHistory", second, Call("Parse", "bili:102", "bili"), "窗口B");
            Check((bool)Call("SaveHistory", second), "窗口B保存历史");
            IList entries = (IList)((IDictionary)Call("LoadHistory"))["bili"];
            Check(entries.Count == 3 && (string)LookupGet(entries[0], "Room") == "102"
                && (string)LookupGet(entries[1], "Room") == "101" && (string)LookupGet(entries[2], "Room") == "100",
                "两个旧历史快照保存后按保存先后保留双方及原记录");
            for (int room = 200; room < 220; room++)
                Call("AddHistory", first, Call("Parse", "bili:" + room, "bili"), "连续播放");
            Check((bool)Call("SaveHistory", first), "批量本次记录合并保存成功");
            entries = (IList)((IDictionary)Call("LoadHistory"))["bili"];
            Check(entries.Count == 15 && (string)LookupGet(entries[0], "Room") == "219"
                && (string)LookupGet(entries[14], "Room") == "205", "合并仍保留最近十五条且顺序正确");
        }
        finally { field.SetValue(null, old); Directory.Delete(dir, true); }
    }

    private static int RunPersistenceWriter(string[] args)
    {
        try
        {
            hub = Assembly.LoadFrom(args[1]).GetType("LiveHub", true);
            string kind = args[2], path = args[3], room = args[4];
            hub.GetField(kind == "favorites" ? "Favorites" : "History", Flags).SetValue(null, path);
            object snapshot = Call(kind == "favorites" ? "LoadFavorites" : "LoadHistory");
            File.WriteAllText(args[5], "ready"); Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(args[6]) && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
            if (!File.Exists(args[6])) return 2;
            if (kind == "favorites")
            {
                ((List<KeyValuePair<string, string>>)snapshot).Add(new KeyValuePair<string, string>(room, "bili:" + room));
                Call("SaveFavorites", snapshot);
            }
            else
            {
                Call("AddHistory", snapshot, Call("Parse", "bili:" + room, "bili"), room);
                if (!(bool)Call("SaveHistory", snapshot)) return 3;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 4; }
    }

    private static void TestPersistenceProcesses(string launcher, string self)
    {
        string dir = ReviewDirectory();
        try
        {
            foreach (string kind in new[] { "favorites", "history" })
            {
                string path = Path.Combine(dir, kind + ".json"), go = Path.Combine(dir, kind + ".go");
                File.WriteAllText(path, kind == "favorites" ? "[]" : "{}");
                string readyA = path + ".a", readyB = path + ".b";
                string common = "--persistence-writer " + (string)Call("QuoteWindowsArg", launcher) + " " + kind
                    + " " + (string)Call("QuoteWindowsArg", path);
                using (Process a = Process.Start(new ProcessStartInfo(self, common + " 101 " + (string)Call("QuoteWindowsArg", readyA)
                    + " " + (string)Call("QuoteWindowsArg", go)) { UseShellExecute = false, CreateNoWindow = true }))
                using (Process b = Process.Start(new ProcessStartInfo(self, common + " 102 " + (string)Call("QuoteWindowsArg", readyB)
                    + " " + (string)Call("QuoteWindowsArg", go)) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    while ((!File.Exists(readyA) || !File.Exists(readyB)) && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                    bool ready = File.Exists(readyA) && File.Exists(readyB); File.WriteAllText(go, "go");
                    Check(a.WaitForExit(8000) && b.WaitForExit(8000) && ready && a.ExitCode == 0 && b.ExitCode == 0,
                        kind + " 两个独立进程从同一快照并发保存成功");
                }
                string json = File.ReadAllText(path);
                Check(json.Contains("101") && json.Contains("102"), kind + " 并发保存保留双方记录");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
