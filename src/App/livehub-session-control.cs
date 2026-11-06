using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal static partial class LiveHub
{
    /// 记录「本程序拉起的是哪个可执行文件」：相对文件名要按启动工作目录解析成绝对路径，
    /// 否则停止会话时拿 cwd 去比对会误判（拒绝关闭自己的播放器）。
    private static string PlayerProcessExecutable(Process process)
    {
        if (process == null || process.StartInfo == null) throw new InvalidOperationException("没有播放器进程信息");
        string file = process.StartInfo.FileName;
        if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("播放器路径为空");
        if (!Path.IsPathRooted(file))
        {
            string directory = process.StartInfo.WorkingDirectory;
            if (!string.IsNullOrEmpty(directory)) file = Path.Combine(directory, file);
        }
        return Path.GetFullPath(file);
    }

    // Operates only on a Process captured by this launcher's own launch path.
    // Start time and executable identity prevent PID reuse from targeting another app.
    private static bool StopOwnedPlayback(PlaybackSession session, out string error)
    {
        error = null;
        if (session == null || session.Process == null) { error = "没有可停止的本程序会话。"; return false; }
        try
        {
            Process process = session.Process;
            if (process.HasExited) { session.Ended = true; return true; }
            if (session.PlayerStartedUtc == DateTime.MinValue || session.PlayerProcessId == 0
                || string.IsNullOrEmpty(session.PlayerExecutable)
                || process.Id != session.PlayerProcessId
                || process.StartTime.ToUniversalTime() != session.PlayerStartedUtc
                || !string.Equals(Path.GetFullPath(process.MainModule.FileName), session.PlayerExecutable, StringComparison.OrdinalIgnoreCase))
            { error = "会话进程身份无法确认，未关闭任何程序。"; return false; }
            process.CloseMainWindow();
            if (!process.WaitForExit(2000))
            {
                if (process.StartTime.ToUniversalTime() != session.PlayerStartedUtc)
                { error = "会话进程已经变化，停止操作已取消。"; return false; }
                process.Kill();
                if (!process.WaitForExit(3000)) { error = "播放器尚未退出，请从播放器窗口关闭后重试。"; return false; }
            }
            session.Ended = true;
            return true;
        }
        catch (InvalidOperationException) { session.Ended = true; return true; }
        catch (Exception ex) { error = "停止会话失败：" + ex.GetType().Name; return false; }
    }

    private sealed partial class LauncherUi
    {
        private PlaybackSession SelectedPlaybackSession()
        {
            return sessionsList.SelectedItems.Count == 0 ? null : sessionsList.SelectedItems[0].Tag as PlaybackSession;
        }

        private void UpdateSessionActions()
        {
            PlaybackSession session = SelectedPlaybackSession();
            // A new launch during the stop worker can steal the pending resync:
            // serialize both entry points until the selected player has exited.
            openMpv.Enabled = openPot.Enabled = !closingSession && currentLaunch == null;
            stopSession.Enabled = !closingSession && session != null && !session.Ended;
            resyncSession.Enabled = !closingSession && currentLaunch == null && session != null && !session.Ended;
            // 重新打开保留原语义：会话结束后仍可用（就是「重开刚才的房间」）。
            reopen.Enabled = !closingSession && currentLaunch == null && session != null;
            UpdateSessionSyncDiagnostics();
        }

        private void CloseSelectedPlayback(bool resync)
        {
            PlaybackSession session = SelectedPlaybackSession();
            if (session == null || closingSession || currentLaunch != null) return;
            closingSession = true; UpdateSessionActions();
            status.Text = resync ? "正在重新同步：关闭此会话后以原选项重新打开，期间可能短暂停顿或跳过直播内容…" : "正在停止选中会话…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error; bool stopped = StopOwnedPlayback(session, out error);
                try
                {
                    form.BeginInvoke((MethodInvoker)delegate
                    {
                        closingSession = false;
                        if (!stopped) { status.Text = error; UpdateSessionActions(); return; }
                        RefreshSessions();
                        if (resync)
                        {
                            input.Text = session.Target.Platform + ":" + session.Target.Room;
                            qualityBox.SelectedIndex = session.Target.Quality == "smooth" ? 2 : session.Target.Quality == "high" ? 1 : 0;
                            bufferBox.SelectedIndex = session.Target.BufferMode == "low" ? 1 : 0;
                            danmakuBox.SelectedIndex = session.Target.DanmakuMode == "on" ? 1 : session.Target.DanmakuMode == "off" ? 2 : 0;
                            Launch(session.Player);
                        }
                        else status.Text = "已停止选中会话；其他播放器不受影响。";
                        UpdateSessionActions();
                    });
                }
                catch (InvalidOperationException)
                {
                    // 窗体在回调前已关闭：只复位防重入标志，不再触碰已释放控件。
                    closingSession = false;
                }
            });
        }
    }
}
