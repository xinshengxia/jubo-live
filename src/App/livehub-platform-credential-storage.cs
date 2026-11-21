using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// The envelope is self-describing so every reader can handle a partly completed
// migration. The original bytes (including BOM and line endings) are protected.
internal static class PlatformCredentialStorage
{
    private const string Prefix = "JUBO-LOGIN-DPAPI-1\n";
    private static readonly byte[] Header = Encoding.ASCII.GetBytes(Prefix);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JuboLive.CSharp.PlatformLogin.v1");
    private static string Marker(string path)
    { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".jubo-live-credential-protection"); }

    private static bool Wrapped(byte[] data)
    { return data.Length >= Header.Length && Header.SequenceEqual(data.Take(Header.Length)); }

    internal static bool ProtectedMode(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (File.Exists(Marker(path))) return true;
        if (!File.Exists(path)) return false;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            byte[] header = new byte[Header.Length];
            return input.Read(header, 0, header.Length) == header.Length && Wrapped(header);
        }
    }

    private static byte[] ReadFile(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("凭据文件超过大小限制。");
        return File.ReadAllBytes(path);
    }

    private static byte[] Protect(byte[] plain)
    {
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.ASCII.GetBytes(Prefix + Convert.ToBase64String(cipher));
    }

    private static byte[] Open(byte[] stored)
    {
        if (!Wrapped(stored)) return stored;
        var cipher = Convert.FromBase64String(Encoding.ASCII.GetString(stored, Header.Length, stored.Length - Header.Length));
        return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    }

    internal static string ReadText(string path)
    {
        byte[] plain = Open(ReadFile(path));
        try { using (var reader = new StreamReader(new MemoryStream(plain), Encoding.UTF8, true)) return reader.ReadToEnd(); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    private static T Locked<T>(Func<T> action)
    {
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        using (var gate = new Mutex(false, @"Local\JuboLive.PlatformCredentials.v1." + identity.User.Value))
        {
            bool held = false;
            try
            {
                try { held = gate.WaitOne(15000); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("另一进程正在处理平台凭据，请稍后重试。");
                return action();
            }
            finally { if (held) gate.ReleaseMutex(); }
        }
    }

    internal static void WithLock(Action action)
    { Locked(delegate { action(); return true; }); }

    // For migrations and protected writes, require a verified atomic replacement.
    private static void Replace(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        LocalData.RequireOrdinaryPath(path);
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".credential.tmp");
        try
        {
            using (File.Create(temp)) { }
            if (LiveHub.RestrictToCurrentUser(temp) != LiveHub.AclOutcome.Applied)
                throw new UnauthorizedAccessException("此目录无法保护凭据，请使用支持 Windows 权限的本地目录。");
            using (var file = new FileStream(temp, FileMode.Truncate, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            if (File.Exists(path))
            {
                if (LiveHub.RestrictToCurrentUser(path) != LiveHub.AclOutcome.Applied) throw new UnauthorizedAccessException("无法保护原凭据文件。");
                File.Replace(temp, path, null);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static LiveHub.AclOutcome WriteText(string path, string text, out string note)
    {
        string resultNote = "";
        var result = Locked(delegate
        {
            if (!ProtectedMode(path)) return LiveHub.AtomicWritePrivateText(path, text, out resultNote);
            byte[] plain = new UTF8Encoding(false).GetBytes(text);
            try { Replace(path, Protect(plain)); }
            finally { Array.Clear(plain, 0, plain.Length); }
            resultNote = "已使用当前 Windows 用户的 DPAPI 加密。";
            return LiveHub.AclOutcome.Applied;
        });
        note = resultNote; return result;
    }

    internal static string SetMode(string mode, IEnumerable<string> basePaths)
    {
        if (mode != "encrypt" && mode != "restore" && mode != "verify") throw new ArgumentException("存储模式无效");
        return Locked(delegate
        {
            var paths = basePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath)
                .SelectMany(p => new[] { p, p + ".json" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var markers = paths.Select(Marker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var changed = new List<string>();
            var prepared = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            bool encrypt = mode == "encrypt";
            int encrypted = 0, present = 0;
            try
            {
                foreach (string path in paths)
                {
                    LocalData.RequireOrdinaryPath(path);
                    if (!File.Exists(path)) continue;
                    present++;
                    byte[] previous = originals[path] = ReadFile(path);
                    bool wrapped = Wrapped(previous);
                    if (wrapped) encrypted++;
                    byte[] plain = Open(previous);
                    try
                    {
                        if (mode == "verify" || wrapped == encrypt) continue;
                        byte[] next = prepared[path] = encrypt ? Protect(plain) : (byte[])plain.Clone();
                        byte[] check = Open(next);
                        try { if (!check.SequenceEqual(plain)) throw new InvalidDataException("凭据迁移回读不一致。"); }
                        finally { if (!ReferenceEquals(check, next)) Array.Clear(check, 0, check.Length); }
                    }
                    finally { if (!ReferenceEquals(plain, previous)) Array.Clear(plain, 0, plain.Length); }
                }
                if (mode == "verify") return "files=" + present + " encrypted=" + encrypted + " verified=" + present;
                foreach (var pair in prepared)
                {
                    Replace(pair.Key, pair.Value); changed.Add(pair.Key);
                    byte[] saved = ReadFile(pair.Key);
                    try { if (!saved.SequenceEqual(pair.Value)) throw new IOException("凭据落盘校验失败。"); }
                    finally { Array.Clear(saved, 0, saved.Length); }
                }
                foreach (string marker in markers)
                {
                    LocalData.RequireOrdinaryPath(marker);
                    if (File.Exists(marker) == encrypt) continue;
                    originals[marker] = encrypt ? null : File.ReadAllBytes(marker);
                    if (encrypt) Replace(marker, Encoding.ASCII.GetBytes("DPAPI CurrentUser v1\n")); else File.Delete(marker);
                    changed.Add(marker);
                }
                return "files=" + present + " changed=" + prepared.Count + " mode=" + mode;
            }
            catch
            {
                changed.Reverse();
                foreach (string path in changed)
                    if (originals[path] == null) File.Delete(path); else Replace(path, originals[path]);
                throw;
            }
            finally
            {
                foreach (byte[] bytes in originals.Values.Concat(prepared.Values)) if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            }
        });
    }

    internal static string Configure(string mode)
    {
        var paths = new List<string> { LiveHub.Cookie };
        foreach (var platform in PlatformLogin.All) paths.Add(PlatformLogin.CookiesPath(platform));
        return SetMode(mode, paths);
    }
}
