// --------------------------------------------------------------------------------------------
// 凭据（cookie）相关的回归：结构化存取 + ACL 结论如实反馈。
//
// 评估报告 §3 的两条：
//   ① ACL 方法吞掉所有异常 → 在 FAT/exFAT U 盘、共享盘上「保存成功」但界面仍说「仅当前用户可读」；
//   ② CDP 取回的 cookie 被压成没有 domain/path/secure/expires 的裸串，且混入无关域的 cookie。
// 这里把修复后的行为钉死：按域过滤、按 host 选 cookie、ACL 三态如实回传。
// --------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    /// <summary>CDP `Network.getCookies` 的真实响应形状（字段名照抄 Chrome DevTools Protocol）。</summary>
    private const string CdpCookieSample =
        "{\"id\":1,\"result\":{\"cookies\":["
        + "{\"name\":\"ttwid\",\"value\":\"t1\",\"domain\":\".douyin.com\",\"path\":\"/\",\"expires\":4102444800,\"size\":5,\"httpOnly\":true,\"secure\":true,\"session\":false},"
        + "{\"name\":\"sessionid\",\"value\":\"s1\",\"domain\":\"live.douyin.com\",\"path\":\"/\",\"expires\":0,\"size\":9,\"httpOnly\":true,\"secure\":true,\"session\":true},"
        + "{\"name\":\"tracker\",\"value\":\"x\",\"domain\":\".evil.com\",\"path\":\"/\",\"expires\":4102444800,\"size\":7,\"httpOnly\":false,\"secure\":false,\"session\":false},"
        + "{\"name\":\"stale\",\"value\":\"o\",\"domain\":\".douyin.com\",\"path\":\"/\",\"expires\":1000000000,\"size\":5,\"httpOnly\":false,\"secure\":false,\"session\":false}"
        + "]}}";

    private static Type RecordType() { return DanmakuType("CookieRecord"); }

    private static object NewRecord(string name, string value, string domain, string path, bool secure, bool httpOnly, long expires)
    {
        Type type = RecordType();
        object record = Activator.CreateInstance(type);
        type.GetField("Name").SetValue(record, name);
        type.GetField("Value").SetValue(record, value);
        type.GetField("Domain").SetValue(record, domain);
        type.GetField("Path").SetValue(record, path);
        type.GetField("Secure").SetValue(record, secure);
        type.GetField("HttpOnly").SetValue(record, httpOnly);
        type.GetField("ExpiresUtc").SetValue(record, expires);
        return record;
    }

    private static object NewRecordList(params object[] records)
    {
        Type listType = typeof(List<>).MakeGenericType(RecordType());
        object list = Activator.CreateInstance(listType);
        MethodInfo add = listType.GetMethod("Add");
        foreach (object record in records) add.Invoke(list, new object[] { record });
        return list;
    }

    private static void TestCredentialStore(string launcher)
    {
        Type cookies = DanmakuType("CredentialCookies");
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ---- ① 解析：元数据必须一个都不丢 ----
        object parsed = cookies.GetMethod("ParseCdp", flags).Invoke(null, new object[] { CdpCookieSample });
        int parsedCount = (int)parsed.GetType().GetProperty("Count").GetValue(parsed, null);
        Check(parsedCount == 4, "结构化 cookie：4 条全部解析出来（含无关域，先解析后过滤）");

        Type recordType = RecordType();
        object first = parsed.GetType().GetProperty("Item").GetValue(parsed, new object[] { 0 });
        Check((string)recordType.GetField("Domain").GetValue(first) == ".douyin.com",
            "结构化 cookie：domain 保留（这是原来被压掉的那个字段）");
        Check((bool)recordType.GetField("HttpOnly").GetValue(first), "结构化 cookie：httpOnly 保留");
        Check((bool)recordType.GetField("Secure").GetValue(first), "结构化 cookie：secure 保留");
        Check((long)recordType.GetField("ExpiresUtc").GetValue(first) == 4102444800L, "结构化 cookie：expires 保留");

        // ---- ② 按平台域名白名单过滤：无关域 + 过期项都不许留 ----
        object kept = cookies.GetMethod("FilterByPlatform", flags)
            .Invoke(null, new object[] { parsed, "douyin" });
        int keptCount = (int)kept.GetType().GetProperty("Count").GetValue(kept, null);
        Check(keptCount == 2, "结构化 cookie：只留 douyin.com 域且未过期的（实得 " + keptCount + " 条 / 原 4 条）");
        string joined = CredentialCookiesNames(kept);
        Check(joined == "ttwid,sessionid", "结构化 cookie：留下的正是 ttwid/sessionid（实得 " + joined + "）");
        object none = cookies.GetMethod("FilterByPlatform", flags).Invoke(null, new object[] { parsed, "nosuchplatform" });
        Check((int)none.GetType().GetProperty("Count").GetValue(none, null) == 0,
            "结构化 cookie：不认识的平台一条都不放行（没有白名单=不放行）");

        // ---- ③ 按 host 选 cookie：子域命中、伪装域被拒、secure 不走 http ----
        MethodInfo headerFor = cookies.GetMethod("HeaderForHost", flags);
        string forLive = (string)headerFor.Invoke(null, new object[] { kept, "live.douyin.com", true });
        Check(forLive == "ttwid=t1; sessionid=s1", "按 host 选 cookie：live.douyin.com 拿到两条（实得 " + forLive + "）");
        string forWww = (string)headerFor.Invoke(null, new object[] { kept, "www.douyin.com", true });
        Check(forWww == "ttwid=t1", "按 host 选 cookie：www.douyin.com 只拿到域 cookie（实得 " + forWww + "）");
        string forEvil = (string)headerFor.Invoke(null, new object[] { kept, "evildouyin.com", true });
        Check(forEvil == "", "按 host 选 cookie：evildouyin.com 一条都不给（实得 \"" + forEvil + "\"）");
        string forHttp = (string)headerFor.Invoke(null, new object[] { kept, "live.douyin.com", false });
        Check(forHttp == "", "按 host 选 cookie：secure cookie 不走明文 http（实得 \"" + forHttp + "\"）");

        // ---- ④ 落盘 / 读回：结构化旁文件是原子的、字段不丢 ----
        string dir = Path.Combine(Path.GetTempPath(), "livehub-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string cookieFile = Path.Combine(dir, "douyin-cookies.txt");
        try
        {
            File.WriteAllText(cookieFile, "ttwid=t1; sessionid=s1", Encoding.UTF8);
            object sidecar = cookies.GetMethod("Save", flags).Invoke(null, new object[] { cookieFile, kept });
            Check(sidecar is string && File.Exists((string)sidecar), "结构化 cookie：旁文件落盘（" + sidecar + "）");
            object loaded = cookies.GetMethod("Load", flags).Invoke(null, new object[] { cookieFile });
            int loadedCount = (int)loaded.GetType().GetProperty("Count").GetValue(loaded, null);
            Check(loadedCount == 2, "结构化 cookie：读回 2 条（实得 " + loadedCount + "）");
            object back = loaded.GetType().GetProperty("Item").GetValue(loaded, new object[] { 1 });
            Check((string)recordType.GetField("Domain").GetValue(back) == "live.douyin.com"
                && (bool)recordType.GetField("HttpOnly").GetValue(back)
                && (bool)recordType.GetField("Secure").GetValue(back),
                "结构化 cookie：读回后 domain/httpOnly/secure 都没变");
            var security = File.GetAccessControl((string)sidecar);
            Check(security.AreAccessRulesProtected, "结构化 cookie：旁文件同样收紧了 ACL");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

        // ---- ⑤ ACL 三态：不支持时必须如实说，不能假装保护上了 ----
        Type hubType = hub;
        MethodInfo restrict = hubType.GetMethod("RestrictToCurrentUser",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
            new Type[] { typeof(string), typeof(string).MakeByRefType() }, null);
        Check(restrict != null, "ACL：RestrictToCurrentUser 有带原因输出的重载");
        if (restrict == null) return;

        string aclFile = Path.Combine(Path.GetTempPath(), "livehub-acl-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(aclFile, "SESSDATA=test", Encoding.UTF8);
            object[] argsOk = new object[] { aclFile, null };
            object okOutcome = restrict.Invoke(null, argsOk);
            Check(Convert.ToString(okOutcome) == "Applied",
                "ACL：NTFS 临时文件上返回 Applied（实得 " + okOutcome + "）");

            // 注入「这个位置不支持 ACL」（模拟 FAT/exFAT U 盘、共享盘）
            FieldInfo injector = hubType.GetField("AclFailureInjectorForTest",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Check(injector != null, "ACL：有测试注入点（否则这条路径没法在真机上验）");
            if (injector != null)
            {
                Func<string, string> fake = delegate (string path) { return "模拟：exFAT 卷不支持 Windows ACL"; };
                injector.SetValue(null, fake);
                try
                {
                    object[] argsBad = new object[] { aclFile, null };
                    object badOutcome = restrict.Invoke(null, argsBad);
                    Check(Convert.ToString(badOutcome) == "NotSupported",
                        "ACL：不支持的位置返回 NotSupported，而不是假装成功（实得 " + badOutcome + "）");
                    string note = Convert.ToString(argsBad[1]);
                    Check(note != null && note.IndexOf("ACL", StringComparison.Ordinal) >= 0,
                        "ACL：带回人能看懂的原因（实得 " + note + "）");
                    MethodInfo describe = hubType.GetMethod("DescribeAcl", flags);
                    string text = (string)describe.Invoke(null, new object[] { badOutcome, note });
                    Check(text.IndexOf("不支持权限保护", StringComparison.Ordinal) >= 0,
                        "ACL：界面文案如实说明「保存了但没保护上」（实得 " + text + "）");
                }
                finally { injector.SetValue(null, null); }
            }

            object[] argsMissing = new object[] { Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")), null };
            object missingOutcome = restrict.Invoke(null, argsMissing);
            Check(Convert.ToString(missingOutcome) == "Failed",
                "ACL：文件不存在时返回 Failed（原来静默 return）");
        }
        finally { try { File.Delete(aclFile); } catch (Exception) { } }
    }

    /// <summary>把记录列表里的 name 拼起来，用来断言「留下了哪几条」。</summary>
    private static string CredentialCookiesNames(object list)
    {
        Type recordType = RecordType();
        var builder = new StringBuilder();
        int count = (int)list.GetType().GetProperty("Count").GetValue(list, null);
        for (int i = 0; i < count; i++)
        {
            object record = list.GetType().GetProperty("Item").GetValue(list, new object[] { i });
            if (builder.Length > 0) builder.Append(',');
            builder.Append((string)recordType.GetField("Name").GetValue(record));
        }
        return builder.ToString();
    }
    /// <summary>PlatformLogin.CookieHeaderForHost 的**集成**验证：有结构化旁文件时按 host 精确选，
    /// 没有旁文件时不发送来源未知的裸串。
    /// 这里之所以要改 RuntimeDir：cookie 路径是 Path.Combine(RuntimeDir, ...)，测试进程没跑过 Main。</summary>
    private static void TestCookieHeaderIntegration(string launcher)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo runtimeField = hub.GetField("RuntimeDir", flags);
        string original = (string)runtimeField.GetValue(null);
        string dir = Path.Combine(Path.GetTempPath(), "livehub-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            runtimeField.SetValue(null, dir);
            string cookieFile = Path.Combine(dir, "douyu-cookies.txt");
            File.WriteAllText(cookieFile, "dy_auth=raw; unrelated=zzz", Encoding.UTF8);

            Type cookies = DanmakuType("CredentialCookies");
            Type recordType = RecordType();
            object list = NewRecordList(
                NewRecord("dy_auth", "raw", ".douyu.com", "/", true, true, 4102444800L),
                NewRecord("unrelated", "zzz", ".evil.com", "/", false, false, 4102444800L));
            cookies.GetMethod("Save", flags).Invoke(null, new object[] { cookieFile, list });

            Type login = DanmakuType("PlatformLogin");
            MethodInfo headerFor = login.GetMethod("CookieHeaderForHost", flags);
            string forDouyu = (string)headerFor.Invoke(null, new object[] { "www.douyu.com" });
            Check(forDouyu == "dy_auth=raw",
                "按 host 选 cookie：斗鱼请求只带 douyu.com 域的（实得 \"" + forDouyu + "\"）");
            string forEvil = (string)headerFor.Invoke(null, new object[] { "www.evil.com" });
            Check(forEvil == "", "按 host 选 cookie：无关域一条都不给（实得 \"" + forEvil + "\"）");

            // 删掉旁文件 → 不发送来源未知的裸串；重新登录才能恢复发送
            string sidecar = (string)cookies.GetMethod("SidecarPathForTest", flags).Invoke(null, new object[] { cookieFile });
            File.Delete(sidecar);
            string fallback = (string)headerFor.Invoke(null, new object[] { "www.douyu.com" });
            Check(fallback == "", "缺失元数据时不向网络发送来源不明的裸 cookie");
        }
        finally
        {
            runtimeField.SetValue(null, original);
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
