using System;
using System.Collections.Generic;
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
        private Process updateWorker;
        private bool installingUpdate, onlineInstall, newerVersion;
        private Label updateResult;
        private ProgressBar updateDownload;
        private Timer updateDownloadTimer;
        private ModernButton checkAgain, installUpdate, localUpdate, cancelUpdate;
        private string UpdateRequest { get { return Path.Combine(UpdateCache, "requests", updateTicket); } }
        private static string UpdateCache { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuboLive", "Updates", "cs"); } }
        private static Version CurrentVersion { get { return typeof(LiveHub).Assembly.GetName().Version; } }

        private Process OpenUpdater(string mode, string ticket, string zip = "", string catalog = "")
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "assets", "updater", "Start-Update.ps1");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File " + QuoteWindowsArg(path) + " -Product cs -Mode " + mode);
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.EnvironmentVariables["JUBO_UPDATE_EXE"] = Path.GetFileName(Application.ExecutablePath);
            info.EnvironmentVariables["JUBO_UPDATE_VERSION"] = CurrentVersion.ToString(3);
            using (var owner = Process.GetCurrentProcess()) info.EnvironmentVariables["JUBO_UPDATE_OWNER"] = owner.Id.ToString();
            info.EnvironmentVariables["JUBO_UPDATE_TICKET"] = ticket;
            info.EnvironmentVariables["JUBO_UPDATE_OFFLINE_ZIP"] = zip;
            info.EnvironmentVariables["JUBO_UPDATE_OFFLINE_CATALOG"] = catalog;
            info.EnvironmentVariables.Remove("JUBO_UPDATE_OWNER_START");
            var original = Environment.GetCommandLineArgs();
            info.EnvironmentVariables["JUBO_UPDATE_ARGS"] = new JavaScriptSerializer().Serialize(new ArraySegment<string>(original, 1, original.Length - 1).ToArray());
            return Process.Start(info);
        }

        private void BuildUpdatesPage()
        {
            var page = new WorkbenchPanel { Name = "workbenchPage_updates", AutoScroll = true, Visible = false };
            workbenchPages.Add("updates", page);
            workbenchHost.Controls.Add(page);
            var heading = WorkbenchLabel("updateVersion", "看直播 C#  ·  当前版本 " + CurrentVersion.ToString(3), false);
            WorkbenchFont(heading, 17, System.Drawing.FontStyle.Bold);
            updateResult = WorkbenchLabel("updateResult", "准备检查正式版更新。", false);
            updateResult.AutoEllipsis = false;
            updateDownload = new ProgressBar { Name = "updateDownloadProgress", AccessibleName = "更新包下载进度",
                Maximum = 1000, Style = ProgressBarStyle.Continuous, Visible = false };
            updateDownloadTimer = new Timer { Interval = 250 };
            updateDownloadTimer.Tick += delegate {
                try { ReadUpdateProgress(); }
                catch (IOException) { ClearDownloadProgress(); }
                catch (UnauthorizedAccessException) { ClearDownloadProgress(); }
            };
            page.Disposed += delegate { updateDownloadTimer.Dispose(); };
            var auto = new ModernCheckBox { Text = "自动检查正式版更新", Checked = !File.Exists(Path.Combine(UpdateCache, "auto-off")) };
            auto.Click += delegate {
                try {
                    Directory.CreateDirectory(UpdateCache);
                    string marker = Path.Combine(UpdateCache, "auto-off");
                    if (auto.Checked) { File.Delete(marker); nextAutoCheck = DateTime.MinValue; }
                    else File.WriteAllText(marker, "off");
                } catch (Exception ex) { auto.Checked = !File.Exists(Path.Combine(UpdateCache, "auto-off")); updateResult.Text = "无法保存设置：" + ex.Message; }
            };
            checkAgain = WorkbenchButton("updateCheck", "重新检查", delegate { RunUpdate(false); });
            installUpdate = WorkbenchButton("updateInstall", "更新", delegate { RunUpdate(true); });
            localUpdate = WorkbenchButton("updateOffline", "从本地安装…", PickUpdatePackage);
            var offlineTitle = WorkbenchLabel("offlineTitle", "离线安装包", false);
            WorkbenchFont(offlineTitle, 15, System.Drawing.FontStyle.Bold);
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            string packageMarker = Path.Combine(root, "portable.marker");
            string packageType = "lite";
            try {
                bool full = File.Exists(packageMarker) ? File.ReadAllText(packageMarker).IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0
                    : !Path.GetFileName(Application.ExecutablePath).Equals("看直播-mpv.exe", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(root, "bin", "mpv", "mpv.exe"));
                if (full) packageType = "full";
            } catch (IOException ex) { Diagnostics.Report("读取安装包类型", ex); }
              catch (UnauthorizedAccessException ex) { Diagnostics.Report("读取安装包类型", ex); }
            var help = WorkbenchLabel("offlineInstructions",
                "从 C# 版同一次发布的附件下载两个文件，也可从另一台电脑拷来。\r\n"
                + "先选以 win-x64-" + packageType + ".zip 结尾的安装包（无需解压），再选签名清单 channel.json。", true);
            help.AutoEllipsis = false;
            var releases = WorkbenchButton("offlineReleases", "打开发布页", delegate {
                try { Process.Start(new ProcessStartInfo("https://github.com/xinshengxia/jubo-live/releases/latest") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception ex) { updateResult.Text = "无法打开浏览器：" + ex.Message; }
            });
            releases.Style = ModernButton.Kind.Subtle;
            cancelUpdate = WorkbenchButton("updateCancel", "取消安装", delegate {
                try {
                    Directory.CreateDirectory(UpdateRequest); File.WriteAllText(Path.Combine(UpdateRequest, "cancel"), "cancel");
                    onlineInstall = false; updateDownloadTimer.Stop(); ClearDownloadProgress(); cancelUpdate.Enabled = false;
                    updateResult.Text = "正在取消安装…";
                }
                catch (IOException ex) { updateResult.Text = ex.Message; }
            });
            page.Controls.AddRange(new Control[] { heading, updateResult, updateDownload, auto, checkAgain, installUpdate, localUpdate, cancelUpdate, offlineTitle, help, releases });
            page.Layout += delegate {
                if (!page.Visible) return;
                int x = Px(18), width = Math.Max(Px(160), page.ClientSize.Width - x * 2);
                heading.SetBounds(x, Px(16), width, Px(32));
                updateResult.SetBounds(x, Px(56), width, Px(66));
                updateDownload.SetBounds(x, Px(111), width, Px(11));
                checkAgain.SetBounds(x, Px(132), Px(90), Px(34));
                installUpdate.SetBounds(x + Px(100), Px(132), Px(84), Px(34));
                cancelUpdate.Bounds = installUpdate.Bounds;
                auto.SetBounds(x, Px(174), width, Px(32));
                offlineTitle.SetBounds(x, Px(223), width, Px(25));
                help.SetBounds(x, Px(253), width, Px(68));
                localUpdate.SetBounds(x, Px(327), Px(118), Px(34));
                releases.SetBounds(x + Px(128), Px(327), Px(100), Px(34));
            };
            SyncUpdateButtons();
        }

        private void SyncUpdateButtons()
        {
            checkAgain.Enabled = updateWorker == null;
            installUpdate.Visible = !installingUpdate;
            installUpdate.Enabled = newerVersion && updateWorker == null;
            cancelUpdate.Visible = installingUpdate;
            cancelUpdate.Enabled = installingUpdate;
            localUpdate.Enabled = !installingUpdate;
        }

        private void CheckForUpdates()
        {
            SelectWorkbenchPage("updates");
            if (updateWorker == null) RunUpdate(false);
        }

        private void PickUpdatePackage()
        {
            using (var pick = new OpenFileDialog { Title = "第 1 步，共 2 步 · 选择 C# 正式版 ZIP 安装包", Filter = "ZIP 文件|*.zip" }) {
                if (pick.ShowDialog(form) != DialogResult.OK) return;
                string zip = pick.FileName;
                pick.InitialDirectory = Path.GetDirectoryName(zip);
                pick.Title = "第 2 步，共 2 步 · 选择同一版本的 channel.json"; pick.Filter = "签名清单|*.json"; pick.FileName = "channel.json";
                if (pick.ShowDialog(form) == DialogResult.OK) RunUpdate(true, zip, pick.FileName);
            }
        }

        private void RunUpdate(bool install, string zip = "", string catalog = "")
        {
            if (installingUpdate || (updateWorker != null && zip.Length == 0)) return;
            if (updateWorker != null) { updateWorker.Dispose(); updateWorker = null; }
            newerVersion = false;
            onlineInstall = false;
            ClearDownloadProgress();
            updateTicket = Guid.NewGuid().ToString("N");
            try {
                updateWorker = OpenUpdater(install ? "Install" : "Check", updateTicket, zip, catalog);
                installingUpdate = install;
                onlineInstall = install && zip.Length == 0;
                updateDownloadTimer.Enabled = onlineInstall;
                updateResult.Text = install ? "正在准备安装并校验签名…" : "正在检查更新，直连失败会尝试备用线路…";
            } catch (Exception ex) { updateResult.Text = "无法启动更新任务：" + ex.Message; }
            SyncUpdateButtons();
        }

        private const string DownloadingStatus = "正在下载更新包…";

        private static bool TryReadDownloadCounts(string file, out long downloaded, out long total)
        {
            downloaded = 0; total = 0;
            try {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > 1024) return false;
                var json = new JavaScriptSerializer { MaxJsonLength = 1024 };
                var fields = json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                object done, size;
                if (fields == null || !fields.TryGetValue("downloaded", out done) || !fields.TryGetValue("total", out size)
                    || !(done is int || done is long) || !(size is int || size is long)) return false;
                downloaded = Convert.ToInt64(done); total = Convert.ToInt64(size);
                return total > 0 && downloaded >= 0 && downloaded <= total;
            } catch (IOException) { return false; }
              catch (UnauthorizedAccessException) { return false; }
              catch (ArgumentException) { return false; }
              catch (InvalidOperationException) { return false; }
        }

        private void ClearDownloadProgress()
        {
            updateDownload.Visible = false;
            updateDownload.Value = 0;
            if (updateResult.Text.StartsWith(DownloadingStatus + " ", StringComparison.Ordinal))
                updateResult.Text = DownloadingStatus;
        }

        private void UpdateDownloadDisplay(string request, string statusText, bool finished)
        {
            long downloaded, total;
            if (!finished && installingUpdate && onlineInstall && statusText == DownloadingStatus
                && !File.Exists(Path.Combine(request, "cancel"))
                && TryReadDownloadCounts(Path.Combine(request, "download.json"), out downloaded, out total))
            {
                int value = (int)(1000m * downloaded / total);
                updateDownload.Value = value;
                updateDownload.Visible = true;
                updateResult.Text = string.Format("{0} {1}%（{2:0.0} / {3:0.0} MiB）",
                    statusText, value / 10, downloaded / 1048576.0, total / 1048576.0);
            }
            else
            {
                ClearDownloadProgress();
                if (statusText != null) updateResult.Text = statusText;
            }
        }

        private void ReadUpdateProgress()
        {
            if (updateWorker == null) return;
            bool finished = updateWorker.HasExited;
            string progress = Path.Combine(UpdateRequest, "status.txt");
            string statusText = File.Exists(progress) ? File.ReadAllText(progress) : null;
            UpdateDownloadDisplay(UpdateRequest, statusText, finished);
            if (!finished) return;
            string verified = Path.Combine(UpdateRequest, "version.txt");
            Version version;
            newerVersion = !installingUpdate && updateWorker.ExitCode == 0 && File.Exists(verified)
                && Version.TryParse(File.ReadAllText(verified).Trim(), out version) && version > CurrentVersion;
            if (!File.Exists(progress)) updateResult.Text = "更新任务已停止。请重新检查。";
            updateWorker.Dispose(); updateWorker = null; installingUpdate = false; onlineInstall = false;
            updateDownloadTimer.Stop();
            SyncUpdateButtons();
        }

        private void PollUpdater()
        {
            if (!updateChecksActive || updateClosing) return;
            try
            {
                ReadUpdateProgress();
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
                    status.Text = "发现新版 " + announcedVersion + "，可点主界面左下角的按钮更新。";
                }
                if (!installingUpdate || !form.Enabled || currentLaunch != null || closingSession) return;
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
