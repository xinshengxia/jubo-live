using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static partial class LiveHub
{
    private static void RunPotPlayerWatcher(string platform, string room, string sessionId, int pid, long startTicks, bool watchRoom)
    {
        try
        {
            using (Process player = Process.GetProcessById(pid))
            {
                if (player.HasExited || player.StartTime.ToUniversalTime().Ticks != startTicks) return;
                if (watchRoom)
                {
                    // The room lookup can block. Keep window events on their own queue
                    // while the existing watcher polls and clears the room status file.
                    var keys = new Thread(new ThreadStart(delegate { WatchPotPlayerKeys(player); }));
                    keys.IsBackground = true;
                    keys.SetApartmentState(ApartmentState.STA);
                    keys.Start();
                    RunRoomWatcher(platform, room, sessionId, pid);
                }
                else WatchPotPlayerKeys(player);
            }
        }
        catch (ArgumentException) { } // The player can exit before the watcher attaches.
        catch (InvalidOperationException) { }
        catch (Win32Exception ex) { Diagnostics.Report("PotPlayer 窗口监控", ex); }
    }

    private static void WatchPotPlayerKeys(Process player)
    {
        try { using (var key = new PotPlayerRefreshKey(player)) Application.Run(); }
        catch (Win32Exception ex) { Diagnostics.Report("PotPlayer 刷新键", ex); }
        catch (InvalidOperationException) { }
    }

    private sealed class PotPlayerRefreshKey : NativeWindow, IDisposable
    {
        private const int ForegroundChanged = 3, WmHotkey = 0x0312, WmCommand = 0x0111;
        // PotPlayer menu command "Reopen current/last file" (also available as Ctrl+Y).
        private const int ReopenCurrentFile = 10385;
        private const uint ControlNoRepeat = 0x4002, KeyR = 0x52;
        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint msg, IntPtr wparam, IntPtr lparam);
        private readonly Process player;
        private readonly WinEventProc callback;
        private readonly IntPtr hook;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private IntPtr registeredFor;
        private int registrationId;

        internal PotPlayerRefreshKey(Process owner)
        {
            player = owner;
            CreateHandle(new CreateParams { Caption = "LiveHub.PotPlayerRefresh", Parent = new IntPtr(-3) });
            callback = delegate { RefreshOwnership(GetForegroundWindow()); };
            hook = SetWinEventHook(ForegroundChanged, ForegroundChanged, IntPtr.Zero, callback, 0, 0, 0);
            timer.Interval = hook == IntPtr.Zero ? 50 : 250;
            timer.Tick += delegate {
                if (player.HasExited) Application.ExitThread();
                else RefreshOwnership(GetForegroundWindow());
            };
            RefreshOwnership(GetForegroundWindow());
            timer.Start();
        }

        private void RefreshOwnership(IntPtr foreground)
        {
            if (foreground != IntPtr.Zero && foreground == registeredFor) return;
            if (registeredFor != IntPtr.Zero)
            {
                UnregisterHotKey(Handle, registrationId);
                registeredFor = IntPtr.Zero;
            }
            if (!IsPotPlayerWindow(foreground, player.Id)) return;
            // Give each registration a new ID so a queued press from before a
            // foreground switch cannot be replayed when this player regains focus.
            registrationId = registrationId % 0xbfff + 1;
            if (RegisterHotKey(Handle, registrationId, ControlNoRepeat, KeyR)) registeredFor = foreground;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
            {
                Dispatch(message.WParam.ToInt32(), GetForegroundWindow());
                return;
            }
            base.WndProc(ref message);
        }

        private void Dispatch(int id, IntPtr current)
        {
            if (id == registrationId && current != IntPtr.Zero &&
                current == registeredFor && IsPotPlayerWindow(current, player.Id))
                PostMessageW(current, WmCommand, new IntPtr(ReopenCurrentFile), IntPtr.Zero);
        }

        public void Dispose()
        {
            timer.Dispose();
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            if (registeredFor != IntPtr.Zero) UnregisterHotKey(Handle, registrationId);
            DestroyHandle();
        }
    }
}
