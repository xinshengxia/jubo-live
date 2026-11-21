using System;
using System.IO;
using System.Text;

// A private Netscape file exists only while the external resolver needs it.
// The lock lets the next launcher clean up even after an abnormal process exit.
internal sealed class ResolverCookie : IDisposable
{
    private FileStream lease;
    private string guard;
    internal string FileName { get; private set; }

    private static string DirectoryName()
    { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuboLive", "CSharpResolver"); }

    internal static void CleanAbandoned()
    { CleanDirectory(DirectoryName()); }

    private static void CleanDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        LocalData.RequireOrdinaryPath(directory);
        foreach (string name in Directory.GetFiles(directory, "*.lock"))
        {
            Guid id;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out id)) continue;
            string jar = Path.ChangeExtension(name, ".txt");
            try
            {
                LocalData.RequireOrdinaryPath(name); LocalData.RequireOrdinaryPath(jar);
                using (new FileStream(name, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    if (File.Exists(jar)) File.Delete(jar);
                File.Delete(name);
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xffff;
                if (code != 2 && code != 32 && code != 33) Diagnostics.Report("清理备用解析器临时文件", ex);
            }
            catch (UnauthorizedAccessException ex) { Diagnostics.Report("临时凭据文件权限", ex); }
        }
    }

    internal ResolverCookie(string stored) : this(stored, DirectoryName()) { }

    internal ResolverCookie(string stored, string directory)
    {
        if (!File.Exists(stored)) return;
        if (!PlatformCredentialStorage.ProtectedMode(stored)) { FileName = stored; return; }
        LocalData.RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        CleanDirectory(directory);
        guard = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".lock");
        lease = new FileStream(guard, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        FileName = Path.ChangeExtension(guard, ".txt");
        try
        {
            using (File.Create(FileName)) { }
            if (LiveHub.RestrictToCurrentUser(FileName) != LiveHub.AclOutcome.Applied)
                throw new UnauthorizedAccessException("无法创建当前用户专用的解析凭据文件。");
            File.WriteAllText(FileName, PlatformCredentialStorage.ReadText(stored), new UTF8Encoding(false));
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (lease == null) return;
        try { File.Delete(FileName); }
        finally { lease.Dispose(); lease = null; }
        File.Delete(guard);
    }
}
