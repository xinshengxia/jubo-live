using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

internal static partial class LiveHub
{
    internal sealed class LookupAvatarCache : IDisposable
    {
        internal Func<string, CancellationToken, byte[]> Download = Http;
        private readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>();
        private readonly HashSet<string> requested = new HashSet<string>();
        private readonly LinkedList<string> recency = new LinkedList<string>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool disposed;
        internal static bool Allowed(string url)
        {
            Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !LookupUrls.Basic(uri)) return false;
            foreach (string domain in new[] { "hdslb.com", "huya.com", "msstatic.com", "douyucdn.cn", "douyinpic.com", "byteimg.com" })
                if (uri.Host == domain || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        internal Bitmap Load(string url, CancellationToken token)
        {
            if (!Allowed(url)) return null;
            byte[] bytes = Download(url, token); token.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length > 262144) return null;
            using (var stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream, true, true))
            {
                if (image.Width > 2048 || image.Height > 2048) return null;
                return new Bitmap(image, 48, 48);
            }
        }
        internal void Preload(IEnumerable<LookupCandidate> candidates, Control owner, CancellationToken token)
        {
            var urls = new List<string>();
            lock (images)
            {
                if (disposed || token.IsCancellationRequested || candidates == null || owner == null) return;
                foreach (LookupCandidate candidate in candidates)
                {
                    if (requested.Count >= 8) break;
                    if (candidate == null || !Allowed(candidate.avatar_url) || images.ContainsKey(candidate.avatar_url)
                        || !requested.Add(candidate.avatar_url)) continue;
                    urls.Add(candidate.avatar_url);
                }
            }
            if (urls.Count == 0) return;
            ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                    {
                        foreach (string url in urls)
                        {
                            if (cancellation.IsCancellationRequested) break;
                            Bitmap bitmap = null;
                            try {
                                bitmap = Load(url, cancellation.Token);
                                lock (images) {
                                    if (disposed || cancellation.IsCancellationRequested || bitmap == null) continue;
                                    // 刷新/多次查询仍能加载新头像；只保留最近使用的 32 张。
                                    if (images.Count >= 32) {
                                        string oldest = recency.First.Value; recency.RemoveFirst();
                                        images[oldest].Dispose(); images.Remove(oldest);
                                    }
                                    images[url] = bitmap; recency.AddLast(url); bitmap = null;
                                }
                                if (!owner.IsDisposed && owner.IsHandleCreated)
                                    owner.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { if (!owner.IsDisposed) owner.Invalidate(); });
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); } // 下次选择可重试。
                            finally { if (bitmap != null) bitmap.Dispose(); }
                        }
                    }
                }
                finally
                {
                    // 包括尚未开始下载的预约；取消和网络失败不能永久占住 URL。
                    lock (images) foreach (string url in urls) requested.Remove(url);
                }
            });
        }
        internal bool Draw(Graphics graphics, string url, Rectangle bounds)
        {
            lock (images) {
                Bitmap bitmap;
                if (disposed || url == null || !images.TryGetValue(url, out bitmap)) return false;
                graphics.DrawImage(bitmap, bounds); recency.Remove(url); recency.AddLast(url); return true;
            }
        }
        private static byte[] Http(string url, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url); request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 2000;
            using (token.Register(delegate { request.Abort(); }))
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                    token.ThrowIfCancellationRequested(); if (output.Length + count > 262144) return null; output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }
        public void Dispose()
        {
            lock (images) {
                if (disposed) return; disposed = true; lifetime.Cancel();
                foreach (Bitmap bitmap in images.Values) bitmap.Dispose(); images.Clear(); recency.Clear();
            }
            // Queued workers may still register against this cancellation source.
        }
    }
}
