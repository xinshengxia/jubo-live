using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static partial class HardeningTests
{
    private static void TestReversibleLoginStorage()
    {
        string dir = ReviewDirectory();
        string path = Path.Combine(dir, "fixture.txt"), side = path + ".json";
        var type = DanmakuType("PlatformCredentialStorage");
        var mode = type.GetMethod("SetMode", Flags);
        string[] files = { path, Path.Combine(dir, "unused.txt") };
        Action<string> change = value => mode.Invoke(null, new object[] { value, files });
        const string synthetic = "synthetic-login-fixture";
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(synthetic + "\r\n")).ToArray();
        try
        {
            File.WriteAllBytes(path, original);
            File.WriteAllText(side, "{\"cookies\":[]}");
            change("encrypt"); byte[] first = File.ReadAllBytes(path);
            Check(!first.SequenceEqual(original) && !Encoding.ASCII.GetString(first).Contains(synthetic), "加密后不含合成明文");
            change("encrypt"); change("verify");
            Check(first.SequenceEqual(File.ReadAllBytes(path)), "重复迁移不重写已有密文");
            Check((string)type.GetMethod("ReadText", Flags).Invoke(null, new object[] { path }) == synthetic + "\r\n", "同用户解密并按原编码读取");
            string leasePath;
            var lease = DanmakuType("ResolverCookie");
            using (var jar = (IDisposable)Activator.CreateInstance(lease, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { path, Path.Combine(dir, "leases") }, null))
            {
                leasePath = (string)lease.GetProperty("FileName", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(jar, null);
                Check(leasePath != path && File.ReadAllText(leasePath) == synthetic + "\r\n", "外部解析器仅获限时兼容文件");
            }
            Check(!File.Exists(leasePath), "外部解析结束后删除兼容文件");
            string unused = files[1];
            type.GetMethod("WriteText", Flags).Invoke(null, new object[] { unused, synthetic, null });
            Check(File.ReadAllText(unused).StartsWith("JUBO-LOGIN-DPAPI-1"), "首次保存的新平台也按加密策略写入");
            byte[] valid = File.ReadAllBytes(side);
            File.WriteAllText(side, "JUBO-LOGIN-DPAPI-1\ninvalid");
            bool failed = false;
            try { change("restore"); } catch (TargetInvocationException) { failed = true; }
            Check(failed && File.ReadAllBytes(path).SequenceEqual(first), "凭据损坏时先于写入停止");
            File.WriteAllBytes(side, valid);
            change("restore"); change("restore");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && File.ReadAllText(side) == "{\"cookies\":[]}" && File.ReadAllText(unused)==synthetic,
                "还原保留原 BOM、换行、结构化旁文件与后来保存的账号数据");
        }
        finally { Directory.Delete(dir, true); }
    }
}
