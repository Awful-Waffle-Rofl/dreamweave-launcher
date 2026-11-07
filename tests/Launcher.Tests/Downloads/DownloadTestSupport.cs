using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Net;

namespace DreamWeave.Launcher.Tests.Downloads
{
    /// <summary>One request as the fake server saw it.</summary>
    internal sealed class SeenRequest
    {
        public string Method { get; set; }

        public string Path { get; set; }

        public string Range { get; set; }

        public string IfRange { get; set; }
    }

    /// <summary>
    /// Loopback <see cref="HttpListener"/> server. Each request is handed to the handler with its
    /// zero-based index; the handler writes the whole response (or aborts it).
    /// </summary>
    internal sealed class FakeHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<HttpListenerContext, int, Task> _handler;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentQueue<SeenRequest> _seen = new ConcurrentQueue<SeenRequest>();
        private readonly Task _loop;
        private int _count = -1;

        public FakeHttpServer(Func<HttpListenerContext, int, Task> handler)
        {
            _handler = handler;
            int port = FreePort();
            BaseUri = new Uri("http://localhost:" + port + "/");
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public Uri BaseUri { get; }

        public Uri FileUri => new Uri(BaseUri, "file.bin");

        /// <summary>Cancelled when the server is disposed, so stalling handlers can end.</summary>
        public CancellationToken Stopping => _stop.Token;

        public IReadOnlyList<SeenRequest> Requests => _seen.ToArray();

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                int index = Interlocked.Increment(ref _count);
                _seen.Enqueue(new SeenRequest
                {
                    Method = ctx.Request.HttpMethod,
                    Path = ctx.Request.Url.AbsolutePath,
                    Range = ctx.Request.Headers["Range"],
                    IfRange = ctx.Request.Headers["If-Range"],
                });

                Task ignored = Task.Run(async () =>
                {
                    try
                    {
                        await _handler(ctx, index).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        try
                        {
                            ctx.Response.Abort();
                        }
                        catch (Exception)
                        {
                        }
                    }
                });
            }
        }
    }

    /// <summary>Response helpers for <see cref="FakeHttpServer"/> handlers.</summary>
    internal static class Serve
    {
        /// <summary>
        /// A well-behaved range server: honours <c>bytes=a-</c> / <c>bytes=a-b</c>, honours If-Range
        /// against <paramref name="etag"/> (a mismatch serves the full body with 200), else 200 full.
        /// </summary>
        public static async Task FileAsync(HttpListenerContext ctx, byte[] content, string etag = "\"v1\"", bool honourRanges = true, string lastModified = null)
        {
            HttpListenerResponse resp = ctx.Response;
            if (etag != null)
            {
                resp.Headers["ETag"] = etag;
            }

            if (lastModified != null)
            {
                resp.Headers["Last-Modified"] = lastModified;
            }

            long from = 0;
            long to = content.Length - 1;
            bool partial = false;
            string range = ctx.Request.Headers["Range"];
            string ifRange = ctx.Request.Headers["If-Range"];
            bool ifRangeOk = ifRange == null || ifRange == etag || (lastModified != null && ifRange == lastModified);
            if (honourRanges && range != null && ifRangeOk && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                string[] parts = range.Substring(6).Split('-');
                from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    to = Math.Min(to, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                }

                if (from > content.Length - 1)
                {
                    resp.StatusCode = 416;
                    resp.Headers["Content-Range"] = "bytes */" + content.Length;
                    resp.ContentLength64 = 0;
                    resp.Close();
                    return;
                }

                partial = true;
            }

            resp.Headers["Accept-Ranges"] = honourRanges ? "bytes" : "none";
            if (partial)
            {
                resp.StatusCode = 206;
                resp.Headers["Content-Range"] = "bytes " + from + "-" + to + "/" + content.Length;
            }
            else
            {
                resp.StatusCode = 200;
            }

            long count = to - from + 1;
            resp.ContentLength64 = count;
            await resp.OutputStream.WriteAsync(content, (int)from, (int)count).ConfigureAwait(false);
            resp.Close();
        }

        /// <summary>Sends headers for the full body (200) but only the first <paramref name="sendBytes"/> bytes, then aborts the connection.</summary>
        public static async Task TruncatedAsync(HttpListenerContext ctx, byte[] content, int sendBytes, string etag = "\"v1\"")
        {
            HttpListenerResponse resp = ctx.Response;
            if (etag != null)
            {
                resp.Headers["ETag"] = etag;
            }

            resp.StatusCode = 200;
            resp.ContentLength64 = content.Length;
            await resp.OutputStream.WriteAsync(content, 0, sendBytes).ConfigureAwait(false);
            await resp.OutputStream.FlushAsync().ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            resp.Abort();
        }

        /// <summary>Sends headers and <paramref name="sendBytes"/> bytes, then stalls until <paramref name="release"/> fires.</summary>
        public static async Task StallAsync(HttpListenerContext ctx, byte[] content, int sendBytes, CancellationToken release, string etag = "\"v1\"")
        {
            HttpListenerResponse resp = ctx.Response;
            if (etag != null)
            {
                resp.Headers["ETag"] = etag;
            }

            resp.StatusCode = 200;
            resp.ContentLength64 = content.Length;
            await resp.OutputStream.WriteAsync(content, 0, sendBytes).ConfigureAwait(false);
            await resp.OutputStream.FlushAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, release).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            resp.Abort();
        }

        public static Task StatusAsync(HttpListenerContext ctx, int status, string retryAfter = null, string description = null)
        {
            HttpListenerResponse resp = ctx.Response;
            resp.StatusCode = status;
            if (description != null)
            {
                resp.StatusDescription = description;
            }

            if (retryAfter != null)
            {
                resp.Headers["Retry-After"] = retryAfter;
            }

            resp.ContentLength64 = 0;
            resp.Close();
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeDiskSpace : IDiskSpace
    {
        public FakeDiskSpace(long free)
        {
            Free = free;
        }

        public long Free { get; set; }

        public int Calls { get; private set; }

        public long GetFreeBytes(string directory)
        {
            Calls++;
            return Free;
        }
    }

    internal sealed class ListProgress : IProgress<ProgressReport>
    {
        private readonly object _gate = new object();
        private readonly List<ProgressReport> _reports = new List<ProgressReport>();
        private readonly Action<ProgressReport> _onReport;

        public ListProgress(Action<ProgressReport> onReport = null)
        {
            _onReport = onReport;
        }

        public List<ProgressReport> Reports
        {
            get
            {
                lock (_gate)
                {
                    return _reports.ToList();
                }
            }
        }

        public List<ProgressReport> Downloading => Reports.Where(r => r.Message == TransferProgress.DownloadingMessage).ToList();

        public void Report(ProgressReport value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }

            _onReport?.Invoke(value);
        }
    }

    internal static class DownloadFixtures
    {
        public static byte[] Content(int length, int seed = 1234)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        /// <summary>A retry policy that records waits instead of sleeping.</summary>
        public static RetryPolicy InstantRetry(List<TimeSpan> waits)
        {
            return new RetryPolicy
            {
                Delay = (d, ct) =>
                {
                    lock (waits)
                    {
                        waits.Add(d);
                    }

                    return Task.CompletedTask;
                },
            };
        }

        public static HttpStreamClient Client()
        {
            return new HttpStreamClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, true, TimeSpan.FromSeconds(10));
        }

        public static LauncherError Run(Func<Task> action)
        {
            try
            {
                action().GetAwaiter().GetResult();
                return LauncherError.None;
            }
            catch (LauncherException ex)
            {
                return ex.Code;
            }
        }
    }
}
