using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Net;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Tunables for <see cref="HttpTransport"/>.</summary>
    public sealed class HttpTransportOptions
    {
        /// <summary>A body read that yields no bytes for this long abandons the connection and retries.</summary>
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Retry budget and back-off.</summary>
        public RetryPolicy Retry { get; set; } = new RetryPolicy();

        /// <summary>
        /// When the server sent neither a strong ETag nor Last-Modified (archive.org sends neither),
        /// resume with a bare <c>Range</c> request and rely on the Content-Range total plus the
        /// manifest's SHA-256 to catch a changed file. When false, such downloads always restart from zero.
        /// </summary>
        public bool ResumeWithoutValidator { get; set; } = true;

        /// <summary>Read buffer size.</summary>
        public int BufferSize { get; set; } = 81920;
    }

    /// <summary>
    /// Resumable HTTP(S) download into <see cref="DownloadRequest.PartPath"/>.
    /// <list type="bullet">
    /// <item>Probes with a <c>Range: bytes=0-0</c> GET (never HEAD: archive.org answers HEAD with 502)
    /// to learn the total size and whether ranges work; a total that differs from the manifest fails
    /// at once as <see cref="LauncherError.SizeMismatch"/>.</item>
    /// <item>Resumes from an existing part only when its sidecar names the same URL, size and hash, with
    /// <c>Range: bytes=N-</c> plus <c>If-Range</c> (strong ETag, else Last-Modified, else none, see
    /// <see cref="HttpTransportOptions.ResumeWithoutValidator"/>). A 200 answer, a changed validator, a
    /// 416, or a Content-Range that does not start at N restarts from zero.</item>
    /// <item>Never writes past the expected size (<see cref="LauncherError.SizeMismatch"/>, not retried).</item>
    /// <item>Times each body read (<see cref="HttpTransportOptions.InactivityTimeout"/>) instead of the whole body.</item>
    /// <item>Retries network errors, timeouts, 408, 429 and 5xx with back-off (Retry-After honoured);
    /// other 4xx fail at once. Exhausted 429 retries surface as <see cref="LauncherError.SourceQuotaExceeded"/>.</item>
    /// <item>Cancellation leaves the part and its sidecar for a later resume.</item>
    /// </list>
    /// </summary>
    public sealed class HttpTransport : IDownloadTransport
    {
        private readonly IHttpStreamClient _client;
        private readonly HttpTransportOptions _options;

        /// <summary>Creates the transport.</summary>
        public HttpTransport(IHttpStreamClient client, HttpTransportOptions options = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options ?? new HttpTransportOptions();
        }

        /// <inheritdoc />
        public string Name => Transports.Http;

        /// <inheritdoc />
        public long GetResumableBytes(DownloadRequest request)
        {
            PartSidecar sidecar = PartSidecar.TryRead(request.SidecarPath);
            if (sidecar == null || !sidecar.Matches(request))
            {
                return 0;
            }

            if (sidecar.IfRangeValidator() == null && !_options.ResumeWithoutValidator)
            {
                return 0;
            }

            long length = PartFiles.LengthOrMissing(request.PartPath);
            return length < 0 || length > request.ExpectedSize ? 0 : length;
        }

        /// <inheritdoc />
        public async Task DownloadToPartAsync(DownloadRequest request, TransferProgress progress, CancellationToken cancellationToken)
        {
            try
            {
                int retriesUsed = 0;
                Probe probe = null;
                while (true)
                {
                    try
                    {
                        if (probe == null)
                        {
                            probe = await ProbeAsync(request, cancellationToken).ConfigureAwait(false);
                        }

                        await AttemptAsync(request, probe, progress, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (TransientDownloadException transient)
                    {
                        if (retriesUsed >= _options.Retry.MaxRetries)
                        {
                            throw transient.Final;
                        }

                        await _options.Retry.WaitAsync(retriesUsed, transient.RetryAfter, cancellationToken).ConfigureAwait(false);
                        retriesUsed++;
                    }
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
            }
        }

        private async Task<Probe> ProbeAsync(DownloadRequest request, CancellationToken cancellationToken)
        {
            using (HttpStreamResponse response = await SendAsync(request.Url, 0, 0, null, cancellationToken).ConfigureAwait(false))
            {
                ThrowForStatus(request, response);
                long? total;
                bool ranges;
                if (response.StatusCode == 206)
                {
                    total = response.ContentRangeTotal;
                    ranges = true;
                }
                else
                {
                    total = response.ContentLength;
                    ranges = false;
                }

                if (total.HasValue && total.Value != request.ExpectedSize)
                {
                    throw SizeMismatch(request, total.Value);
                }

                return new Probe(ranges, response.ETag, response.LastModified);
            }
        }

        private async Task AttemptAsync(DownloadRequest request, Probe probe, TransferProgress progress, CancellationToken cancellationToken)
        {
            PartSidecar sidecar = PartSidecar.TryRead(request.SidecarPath);
            long have = probe.AcceptsRanges ? GetResumableBytes(request) : 0;
            if (have > 0 && ValidatorChanged(sidecar, probe))
            {
                have = 0;
            }

            if (have == request.ExpectedSize)
            {
                progress.Report(have);
                return;
            }

            string ifRange = have > 0 ? sidecar.IfRangeValidator() : null;
            progress.Report(have);

            using (HttpStreamResponse response = await SendAsync(request.Url, have > 0 ? have : (long?)null, null, ifRange, cancellationToken).ConfigureAwait(false))
            {
                long writeFrom;
                if (response.StatusCode == 206)
                {
                    bool rangeOk = have > 0
                        && response.ContentRangeFrom == have
                        && (!response.ContentRangeTo.HasValue || response.ContentRangeTo.Value == request.ExpectedSize - 1)
                        && (!response.ContentRangeTotal.HasValue || response.ContentRangeTotal.Value == request.ExpectedSize);
                    if (!rangeOk)
                    {
                        DiscardPart(request);
                        throw Transient(LauncherError.SourceHttpError, request.Url.Host + " sent an unexpected part of the file.");
                    }

                    writeFrom = have;
                }
                else if (response.StatusCode == 200)
                {
                    // The server ignored the range or the file changed (If-Range failed): start over.
                    writeFrom = 0;
                    if (response.ContentLength.HasValue && response.ContentLength.Value != request.ExpectedSize)
                    {
                        throw SizeMismatch(request, response.ContentLength.Value);
                    }
                }
                else if (response.StatusCode == 416)
                {
                    DiscardPart(request);
                    throw Transient(LauncherError.SourceHttpError, request.Url.Host + " could not resume the download (HTTP 416).");
                }
                else
                {
                    ThrowForStatus(request, response);
                    throw new LauncherException(LauncherError.SourceHttpError, request.Url.Host + " answered HTTP " + response.StatusCode + ".");
                }

                // Truncate the part to the resume point BEFORE writing the new sidecar, so a crash
                // in between can never leave new validators describing old bytes.
                using (FileStream file = OpenPart(request.PartPath, writeFrom))
                {
                    var newSidecar = new PartSidecar
                    {
                        Url = request.Url.AbsoluteUri,
                        ExpectedSha256 = request.ExpectedSha256,
                        ExpectedSize = request.ExpectedSize,
                        ETag = response.ETag ?? (writeFrom > 0 ? sidecar?.ETag : null),
                        LastModified = response.LastModified ?? (writeFrom > 0 ? sidecar?.LastModified : null),
                    };
                    file.Flush(true);
                    newSidecar.Write(request.SidecarPath);

                    Stream body;
                    try
                    {
                        body = await response.OpenBodyAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
                    {
                        throw Transient(LauncherError.NetworkUnavailable, "The connection to " + request.Url.Host + " was interrupted.", ex);
                    }

                    using (body)
                    {
                        long done = writeFrom;
                        progress.Report(done);
                        byte[] buffer = new byte[_options.BufferSize];
                        while (true)
                        {
                            int read = await ReadWithInactivityTimeoutAsync(request, response, body, buffer, cancellationToken).ConfigureAwait(false);
                            if (read <= 0)
                            {
                                break;
                            }

                            if (done + read > request.ExpectedSize)
                            {
                                throw new LauncherException(LauncherError.SizeMismatch, request.Url.Host + " sent more data than the expected " + request.ExpectedSize + " bytes; the download was abandoned.");
                            }

                            try
                            {
                                await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                throw LauncherException.FromIo(ex, "saving the download");
                            }

                            done += read;
                            progress.Report(done);
                        }

                        if (done < request.ExpectedSize)
                        {
                            throw Transient(LauncherError.NetworkUnavailable, request.Url.Host + " closed the connection before the download finished.");
                        }
                    }
                }
            }
        }

        private async Task<int> ReadWithInactivityTimeoutAsync(DownloadRequest request, HttpStreamResponse response, Stream body, byte[] buffer, CancellationToken cancellationToken)
        {
            using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<int> read;
                try
                {
                    read = body.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                }
                catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
                {
                    throw Transient(LauncherError.NetworkUnavailable, "The connection to " + request.Url.Host + " was interrupted.", ex);
                }

                Task first = await Task.WhenAny(read, Task.Delay(_options.InactivityTimeout, delayCts.Token)).ConfigureAwait(false);
                if (first != read)
                {
                    Observe(read);
                    cancellationToken.ThrowIfCancellationRequested();
                    response.Dispose();
                    throw Transient(LauncherError.NetworkUnavailable, request.Url.Host + " stopped sending data.");
                }

                delayCts.Cancel();
                try
                {
                    return await read.ConfigureAwait(false);
                }
                catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
                {
                    throw Transient(LauncherError.NetworkUnavailable, "The connection to " + request.Url.Host + " was interrupted.", ex);
                }
            }
        }

        private async Task<HttpStreamResponse> SendAsync(Uri uri, long? from, long? to, string ifRange, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.GetAsync(uri, from, to, ifRange, cancellationToken).ConfigureAwait(false);
            }
            catch (LauncherException ex) when (ex.Code == LauncherError.NetworkUnavailable)
            {
                throw new TransientDownloadException(ex);
            }
        }

        private static void ThrowForStatus(DownloadRequest request, HttpStreamResponse response)
        {
            int status = response.StatusCode;
            if (status == 200 || status == 206)
            {
                return;
            }

            string host = request.Url.Host;
            if (status == 429)
            {
                throw new TransientDownloadException(
                    new LauncherException(LauncherError.SourceQuotaExceeded, host + " is limiting downloads right now (HTTP 429). Try again later."),
                    response.RetryAfter);
            }

            var error = new LauncherException(LauncherError.SourceHttpError, host + " answered HTTP " + status + ".");
            if (status == 408 || (status >= 500 && status <= 599))
            {
                throw new TransientDownloadException(error, response.RetryAfter);
            }

            throw error;
        }

        private static bool ValidatorChanged(PartSidecar sidecar, Probe probe)
        {
            if (sidecar == null)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(sidecar.ETag) && !string.IsNullOrEmpty(probe.ETag))
            {
                return !string.Equals(sidecar.ETag, probe.ETag, StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(sidecar.LastModified) && !string.IsNullOrEmpty(probe.LastModified))
            {
                return !string.Equals(sidecar.LastModified, probe.LastModified, StringComparison.Ordinal);
            }

            return false;
        }

        private static FileStream OpenPart(string path, long writeFrom)
        {
            try
            {
                var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.SetLength(writeFrom);
                file.Position = writeFrom;
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "opening the partial download");
            }
        }

        private static void DiscardPart(DownloadRequest request)
        {
            PartFiles.Delete(request.PartPath, "discarding the partial download");
            PartFiles.TryDelete(request.SidecarPath);
        }

        private static bool IsNetworkFailure(Exception ex, CancellationToken cancellationToken)
        {
            if (ex is OperationCanceledException)
            {
                return !cancellationToken.IsCancellationRequested;
            }

            return ex is IOException || ex is HttpRequestException || ex is WebException || ex is ObjectDisposedException;
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private static TransientDownloadException Transient(LauncherError code, string detail, Exception inner = null)
        {
            return new TransientDownloadException(new LauncherException(code, detail, inner));
        }

        private static LauncherException SizeMismatch(DownloadRequest request, long actual)
        {
            return new LauncherException(LauncherError.SizeMismatch, "The file on " + request.Url.Host + " is " + actual + " bytes, but " + request.ExpectedSize + " bytes were expected.");
        }

        private sealed class Probe
        {
            public Probe(bool acceptsRanges, string etag, string lastModified)
            {
                AcceptsRanges = acceptsRanges;
                ETag = etag;
                LastModified = lastModified;
            }

            public bool AcceptsRanges { get; }

            public string ETag { get; }

            public string LastModified { get; }
        }
    }
}
