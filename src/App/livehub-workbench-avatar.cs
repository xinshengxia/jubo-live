using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class LiveHub
{
    // Avatars are presentation-only. The resolved URL and the bounded 48px PNG
    // live under runtime\ so a restart paints the known faces immediately; only
    // selecting a room again re-checks the platform account, and an unchanged
    // URL leaves the cached image untouched.
    internal sealed class WorkbenchAvatarService : IDisposable
    {
        private sealed class Entry
        {
            internal string Url;
            internal DateTime Checked = DateTime.MinValue;
            internal DateTime Retry = DateTime.MinValue;
            internal byte[] Png;
            internal bool PngLoaded;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly HashSet<string> pending = new HashSet<string>();
        private readonly object gate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly LookupVerifier verifier = new LookupVerifier();
        private readonly LookupAvatarCache images = new LookupAvatarCache();
        private readonly string indexPath;
        private readonly string imageDir;
        private bool disposed, loaded;

        internal Func<Target, CancellationToken, string> Resolve;
        internal Func<string, CancellationToken, byte[]> Fetch;

        internal WorkbenchAvatarService()
        {
            Resolve = ResolveAccount;
            Fetch = DownloadImage;
            string root = LiveHub.RuntimeDir;
            if (!string.IsNullOrEmpty(root))
            {
                indexPath = Path.Combine(root, "workbench-avatars.json");
                imageDir = Path.Combine(root, "workbench-avatars");
            }
        }

        internal static string Key(Target target)
        {
            return target == null ? null : target.Platform + ":" + target.Room;
        }

        private string ResolveAccount(Target target, CancellationToken token)
        {
            var request = new LookupCandidate { platform = target.Platform, direct_input = true };
            if (target.Platform == "huya" && !LookupData.Digits(target.Room))
            {
                // Huya aliases (for example "clkimmy") are only verifiable through
                // their official page; the verifier swaps the alias for the numeric room.
                string alias = Platforms.Get("huya").BaseUrl + Uri.EscapeDataString(target.Room ?? "");
                request.profile_url = alias;
                request.sources.Add(new LookupSource { url = alias, title = "Huya alias page",
                    retrieved_at = DateTime.UtcNow.ToString("o"), supports = new[] { "room alias" } });
            }
            else request.room_id = target.Room;
            LookupCandidate result = verifier.VerifyFresh(request, token);
            string url = LookupAvatarCache.Normalize(result.avatar_url);
            return LookupAvatarCache.Allowed(url) ? url : null;
        }

        private byte[] DownloadImage(string url, CancellationToken token)
        {
            using (Bitmap bitmap = images.Load(url, token))
            {
                if (bitmap == null) return null;
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        internal string Get(string key)
        {
            if (key == null) return null;
            EnsureLoaded();
            lock (gate)
            {
                Entry entry;
                return entries.TryGetValue(key, out entry) ? entry.Url : null;
            }
        }

        internal byte[] ImageBytes(string key)
        {
            if (key == null) return null;
            EnsureLoaded();
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry)) return null;
                if (!entry.PngLoaded) { entry.Png = ReadPng(key); entry.PngLoaded = true; }
                return entry.Png;
            }
        }

        // Fill rooms that have no cached image yet. Rooms with a cached image are
        // never resolved here: an avatar only changes when the user selects it.
        internal void Pump(IEnumerable<Target> targets, Control owner, Action<string, string, byte[]> ready)
        {
            if (disposed || targets == null || owner == null || owner.IsDisposed || !owner.IsHandleCreated) return;
            EnsureLoaded();
            var scheduled = new List<Target>();
            lock (gate)
            {
                if (pending.Count >= 3) return;
                var seen = new HashSet<string>();
                foreach (Target target in targets)
                {
                    if (target == null || string.IsNullOrEmpty(target.Room)) continue;
                    string key = Key(target);
                    if (!seen.Add(key) || pending.Contains(key)) continue;
                    Entry entry;
                    entries.TryGetValue(key, out entry);
                    if (entry != null)
                    {
                        if (!entry.PngLoaded) { entry.Png = ReadPng(key); entry.PngLoaded = true; }
                        if (entry.Png != null) continue;
                        if (DateTime.UtcNow < entry.Retry) continue;
                    }
                    pending.Add(key); scheduled.Add(target);
                    if (pending.Count >= 3) break;
                }
            }
            foreach (Target target in scheduled) Queue(target, owner, ready, false);
        }

        // Explicit re-check for one room (the user selected it again). Same URL
        // means "nothing changed": no download, no redraw.
        internal void Refresh(Target target, Control owner, Action<string, string, byte[]> ready)
        {
            if (disposed || target == null || string.IsNullOrEmpty(target.Room)
                || owner == null || owner.IsDisposed || !owner.IsHandleCreated) return;
            EnsureLoaded();
            string key = Key(target);
            lock (gate)
            {
                if (pending.Contains(key)) return;
                Entry entry;
                if (entries.TryGetValue(key, out entry) && (DateTime.UtcNow - entry.Checked).TotalSeconds < 10) return;
                pending.Add(key);
            }
            Queue(target, owner, ready, true);
        }

        private void Queue(Target target, Control owner, Action<string, string, byte[]> ready, bool refresh)
        {
            string key = Key(target), url = null;
            byte[] png = null;
            bool changed = false, resolved = false;
            Target requested = target;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Entry entry;
                    lock (gate) entries.TryGetValue(key, out entry);
                    string cached = entry == null ? null : entry.Url;
                    url = LookupAvatarCache.Normalize((refresh || string.IsNullOrEmpty(cached)) ? Resolve(requested, lifetime.Token) : cached);
                    if (!LookupAvatarCache.Allowed(url)) url = null;
                    if (url == null)
                    {
                        lock (gate) { entry = Touch(key); entry.Retry = DateTime.UtcNow.AddMinutes(refresh ? 1 : 3); }
                        return;
                    }
                    resolved = true;
                    if (entry != null && entry.Url == url && ImageBytes(key) != null)
                    {
                        // Same account image: keep it, only record the successful check.
                        lock (gate) { entry.Checked = DateTime.UtcNow; entry.Retry = DateTime.MinValue; }
                        SaveIndex();
                        return;
                    }
                    png = Fetch(url, lifetime.Token);
                    if (png == null || png.Length == 0)
                    {
                        lock (gate)
                        {
                            entry = Touch(key);
                            if (string.IsNullOrEmpty(entry.Url)) entry.Url = url;
                            entry.Retry = DateTime.UtcNow.AddMinutes(3);
                        }
                        return;
                    }
                    if (!WritePng(key, png)) png = null;
                    lock (gate)
                    {
                        entry = Touch(key);
                        changed = entry.Url != url || entry.Png == null || !SameBytes(entry.Png, png);
                        entry.Url = url; entry.Checked = DateTime.UtcNow; entry.Retry = DateTime.MinValue;
                        entry.Png = png; entry.PngLoaded = true;
                    }
                    SaveIndex();
                }
                catch (OperationCanceledException) { System.Diagnostics.Debug.WriteLine("avatar-cancelled"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
                finally
                {
                    bool notify = changed && resolved && png != null;
                    lock (gate) pending.Remove(key);
                    if (notify && !disposed)
                    {
                        try
                        {
                            owner.BeginInvoke((MethodInvoker)delegate
                            {
                                if (!disposed && !owner.IsDisposed && ready != null) ready(key, url, png);
                            });
                        }
                        catch (InvalidOperationException ex) { System.Diagnostics.Debug.WriteLine("avatar-callback-" + ex.GetType().Name); }
                    }
                }
            });
        }

        private Entry Touch(string key)
        {
            Entry entry;
            if (!entries.TryGetValue(key, out entry)) { entry = new Entry(); entries[key] = entry; }
            return entry;
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private void EnsureLoaded()
        {
            lock (gate)
            {
                if (loaded) return;
                loaded = true;
                if (indexPath == null || !File.Exists(indexPath)) return;
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                    var root = serializer.DeserializeObject(File.ReadAllText(indexPath)) as Dictionary<string, object>;
                    object rawEntries;
                    if (root == null || !root.TryGetValue("entries", out rawEntries)) return;
                    var map = rawEntries as Dictionary<string, object>;
                    if (map == null) return;
                    foreach (var pair in map)
                    {
                        var row = pair.Value as Dictionary<string, object>;
                        if (row == null) continue;
                        object rawUrl, rawChecked;
                        DateTime checkedAt;
                        string url = row.TryGetValue("url", out rawUrl) ? rawUrl as string : null;
                        if (!row.TryGetValue("checked", out rawChecked) || rawChecked == null
                            || !DateTime.TryParse(Convert.ToString(rawChecked, CultureInfo.InvariantCulture),
                                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out checkedAt))
                            checkedAt = DateTime.MinValue;
                        entries[pair.Key] = new Entry { Url = LookupAvatarCache.Normalize(url), Checked = checkedAt };
                    }
                }
                catch (Exception) { entries.Clear(); }
            }
        }

        private void SaveIndex()
        {
            if (indexPath == null || disposed) return;
            var map = new Dictionary<string, object>();
            lock (gate)
            {
                if (entries.Count > 256)
                {
                    var stale = new List<KeyValuePair<string, DateTime>>();
                    foreach (var pair in entries) stale.Add(new KeyValuePair<string, DateTime>(pair.Key, pair.Value.Checked));
                    stale.Sort(delegate(KeyValuePair<string, DateTime> a, KeyValuePair<string, DateTime> b)
                        { return a.Value.CompareTo(b.Value); });
                    for (int i = 0; i < stale.Count - 256; i++) entries.Remove(stale[i].Key);
                }
                foreach (var pair in entries)
                    map[pair.Key] = new Dictionary<string, object> {
                        { "url", pair.Value.Url },
                        { "checked", pair.Value.Checked.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) } };
            }
            try
            {
                var serializer = new JavaScriptSerializer();
                AtomicWriteAllText(indexPath, serializer.Serialize(new Dictionary<string, object> {
                    { "version", 1 }, { "entries", map } }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("avatar-index-" + ex.GetType().Name); }
        }

        private string ImagePath(string key)
        {
            return imageDir == null ? null : Path.Combine(imageDir, Regex.Replace(key, "[^A-Za-z0-9._-]", "_") + ".png");
        }

        private byte[] ReadPng(string key)
        {
            string file = ImagePath(key);
            if (file == null) return null;
            try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
            catch (Exception) { return null; }
        }

        private bool WritePng(string key, byte[] png)
        {
            string file = ImagePath(key);
            if (file == null) return false;
            try
            {
                Directory.CreateDirectory(imageDir);
                string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, png);
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
                return true;
            }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            lifetime.Cancel();
            images.Dispose();
        }
    }

    private sealed partial class LauncherUi
    {
        private readonly WorkbenchAvatarService workbenchAvatars = new WorkbenchAvatarService();

        private void ApplyWorkbenchAvatar(string key, string url, byte[] png)
        {
            if (favoritesBrowser != null) favoritesBrowser.UpdateAvatar(key, url, png);
            if (recentBrowser != null) recentBrowser.UpdateAvatar(key, url, png);
            foreach (HomeSessionRow row in workbenchHomeRows)
                if (row.Target != null && WorkbenchAvatarService.Key(row.Target) == key) row.Avatar.SetAvatar(png);
            // Complete the next bounded batch instead of opening every request at once.
            PumpWorkbenchAvatars();
        }

        private void PumpWorkbenchAvatars()
        {
            if (!workbenchReady || !Application.MessageLoop) return;
            var targets = new List<Target>();
            foreach (var favorite in currentFavorites)
            {
                Target target = Parse(favorite.Value, "bili");
                if (target != null) targets.Add(target);
            }
            foreach (HistoryEntry entry in currentHistory)
            {
                Target target = Parse(platform + ":" + entry.Room, platform);
                if (target != null) targets.Add(target);
            }
            foreach (HomeSessionRow row in workbenchHomeRows) if (row.Active && row.Target != null) targets.Add(row.Target);
            workbenchAvatars.Pump(targets, form, ApplyWorkbenchAvatar);
        }

        private void RefreshWorkbenchAvatar(Target target)
        {
            if (!workbenchReady || !Application.MessageLoop || target == null) return;
            workbenchAvatars.Refresh(target, form, ApplyWorkbenchAvatar);
        }
    }
}
