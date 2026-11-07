using System;
using System.IO;
using System.Threading.Tasks;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// Headers of a streaming GET plus a way to open its body. Disposing it abandons the
    /// connection, which also unblocks a pending body read.
    /// </summary>
    public sealed class HttpStreamResponse : IDisposable
    {
        private readonly Func<Task<Stream>> _openBody;
        private readonly IDisposable _owner;

        /// <summary>Creates a response snapshot (used by <see cref="HttpStreamClient"/> and by test fakes).</summary>
        public HttpStreamResponse(
            int statusCode,
            Uri finalUri,
            long? contentLength,
            long? contentRangeFrom,
            long? contentRangeTo,
            long? contentRangeTotal,
            string etag,
            string lastModified,
            TimeSpan? retryAfter,
            Func<Task<Stream>> openBody,
            IDisposable owner)
        {
            StatusCode = statusCode;
            FinalUri = finalUri;
            ContentLength = contentLength;
            ContentRangeFrom = contentRangeFrom;
            ContentRangeTo = contentRangeTo;
            ContentRangeTotal = contentRangeTotal;
            ETag = etag;
            LastModified = lastModified;
            RetryAfter = retryAfter;
            _openBody = openBody ?? (() => Task.FromResult<Stream>(new MemoryStream(new byte[0])));
            _owner = owner;
        }

        /// <summary>HTTP status code of the final (non-redirect) response.</summary>
        public int StatusCode { get; }

        /// <summary>The URI that produced this response, after redirects.</summary>
        public Uri FinalUri { get; }

        /// <summary>Content-Length of the body, or null when not declared.</summary>
        public long? ContentLength { get; }

        /// <summary>First byte position from Content-Range, or null.</summary>
        public long? ContentRangeFrom { get; }

        /// <summary>Last byte position (inclusive) from Content-Range, or null.</summary>
        public long? ContentRangeTo { get; }

        /// <summary>Complete length from Content-Range (the number after the slash), or null when absent or <c>*</c>.</summary>
        public long? ContentRangeTotal { get; }

        /// <summary>Raw ETag header (quotes and any <c>W/</c> prefix kept), or null.</summary>
        public string ETag { get; }

        /// <summary>Raw Last-Modified header, or null.</summary>
        public string LastModified { get; }

        /// <summary>Retry-After as a delay (delta-seconds or an HTTP date converted to a delay), or null.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>True for 2xx.</summary>
        public bool IsSuccess => StatusCode >= 200 && StatusCode <= 299;

        /// <summary>Opens the body stream. Call at most once.</summary>
        public Task<Stream> OpenBodyAsync()
        {
            return _openBody();
        }

        /// <summary>Releases the connection.</summary>
        public void Dispose()
        {
            _owner?.Dispose();
        }
    }
}
