using System;
using System.IO;
using System.Net;
using System.Reflection;

internal static partial class HardeningTests
{
    private static void TestBiliSidecarSaveFailure()
    {
        string dir = ReviewDirectory(); FieldInfo cookie = hub.GetField("Cookie", ReviewStatic);
        object oldCookie = cookie.GetValue(null), oldAcl = hub.GetField("CookieAcl", ReviewStatic).GetValue(null);
        object oldNote = hub.GetField("CookieAclNote", ReviewStatic).GetValue(null);
        try
        {
            string path = Path.Combine(dir, "bili-cookies.txt"); cookie.SetValue(null, path);
            var oldJar = new CookieContainer();
            oldJar.Add(new Cookie("SESSDATA", "synthetic-old", "/", ".bilibili.com") { Secure = true });
            Check((string)Call("SaveCookieFile", oldJar, null) == path, "建立合成 B站凭据及旁文件");
            string originalSidecar = File.ReadAllText(path + ".json");
            var newJar = new CookieContainer();
            newJar.Add(new Cookie("SESSDATA", "synthetic-new", "/", ".bilibili.com") { Secure = true });
            string saved;
            using (FileStream locked = File.Open(path + ".json", FileMode.Open, FileAccess.Read, FileShare.None))
                saved = (string)Call("SaveCookieFile", newJar, null);
            Check(saved == null, "旁文件被占用时向登录调用方返回保存失败");
            Check(File.ReadAllText(path).Contains("synthetic-new"), "按确认的最小修复保留裸 Cookie 先写入的原有顺序");
            Check(File.ReadAllText(path + ".json") == originalSidecar, "旁文件写失败不破坏已有旁文件");
            Check(Directory.GetFiles(dir, "*.credential.tmp").Length == 0, "失败后不残留凭据临时文件");
            Check((string)Call("SaveCookieFile", newJar, null) == path, "解锁后重试保存返回成功");
            Check(File.ReadAllText(path).Contains("synthetic-new") && File.ReadAllText(path + ".json").Contains("synthetic-new"),
                "成功重试后两份凭据均为本次登录数据");
        }
        finally
        {
            cookie.SetValue(null, oldCookie); hub.GetField("CookieAcl", ReviewStatic).SetValue(null, oldAcl);
            hub.GetField("CookieAclNote", ReviewStatic).SetValue(null, oldNote); Directory.Delete(dir, true);
        }
    }
}
