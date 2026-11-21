using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static partial class LiveHub
{
    private sealed partial class LauncherUi
    {
        private bool updateChecksActive, updateClosing;
        private DateTime nextAutoCheck;
        private string updateTicket, announcedVersion;
        private Process updateDialog;
        private static string UpdateCache { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuboLive", "Updates", "cs"); } }
        private static Version CurrentVersion { get { return typeof(LiveHub).Assembly.GetName().Version; } }

        private Process OpenUpdater(string mode, string ticket)
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "assets", "updater", "Start-Update.ps1");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File " + QuoteWindowsArg(path) + " -Product cs -Mode " + mode);
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.EnvironmentVariables["JUBO_UPDATE_EXE"] = Path.GetFileName(Application.ExecutablePath);
            info.EnvironmentVariables["JUBO_UPDATE_VERSION"] = CurrentVersion.ToString(3);
            using (var owner = Process.GetCurrentProcess()) info.EnvironmentVariables["JUBO_UPDATE_OWNER"] = owner.Id.ToString();
            info.EnvironmentVariables["JUBO_UPDATE_TICKET"] = ticket;
            var original = Environment.GetCommandLineArgs();
            info.EnvironmentVariables["JUBO_UPDATE_ARGS"] = new JavaScriptSerializer().Serialize(new ArraySegment<string>(original, 1, original.Length - 1).ToArray());
            return Process.Start(info);
        }

        private void CheckForUpdates()
        {
            try
            {
                if (updateDialog != null && !updateDialog.HasExited) { status.Text = "检查更新窗口已打开。"; return; }
                if (updateDialog != null) updateDialog.Dispose();
                updateTicket = Guid.NewGuid().ToString("N");
                updateDialog = OpenUpdater("Dialog", updateTicket);
            }
            catch (Exception ex) { status.Text = "打开更新窗口失败：" + ex.Message; }
        }

        private void PollUpdater()
        {
            if (!updateChecksActive || updateClosing) return;
            try
            {
                if (DateTime.UtcNow >= nextAutoCheck)
                {
                    nextAutoCheck = DateTime.UtcNow.AddHours(12);
                    string stamp = Path.Combine(UpdateCache,"last-attempt");
                    if (!File.Exists(Path.Combine(UpdateCache,"auto-off")) && (!File.Exists(stamp) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp)).TotalHours >= 12)) using (OpenUpdater("Auto", Guid.NewGuid().ToString("N"))) { }
                }
                string latest = Path.Combine(UpdateCache,"latest.txt");
                Version version;
                if (File.Exists(latest) && new FileInfo(latest).Length < 32 && Version.TryParse(File.ReadAllText(latest).Trim(), out version)
                    && version > CurrentVersion && announcedVersion != version.ToString() && currentLaunch == null)
                {
                    announcedVersion = version.ToString(); getUpdate.Text = "更新 " + announcedVersion;
                    status.Text = "发现新版 " + announcedVersion + "，可到「播放器与画质」点击更新。";
                }
                if (updateTicket == null || !form.Enabled || currentLaunch != null || closingSession) return;
                string request = Path.Combine(UpdateCache,"requests",updateTicket);
                if (!File.Exists(Path.Combine(request,"ready")) || File.Exists(Path.Combine(request,"cancel"))) return;
                foreach (var session in sessions)
                    if (!session.Ended && session.Process != null && !session.Process.HasExited) return;
                File.WriteAllText(Path.Combine(request,"idle-ok"),"idle");
                updateClosing = true; form.BeginInvoke((MethodInvoker)delegate { form.Close(); });
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (System.ComponentModel.Win32Exception ex) { Diagnostics.Report("检查更新", ex); }
        }
    }
}
