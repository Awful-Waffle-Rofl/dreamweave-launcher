using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>
    /// Classifies mega.nz failures for <see cref="MegaTransport"/>: the API's negative result codes
    /// (from the <c>g</c> command that hands out a download URL), HTTP statuses from the storage
    /// servers (mega answers 509 when the anonymous transfer quota is used up), and exceptions from the
    /// network and the part file. Quota is never retried (it recovers over hours, not seconds);
    /// network-class failures are.
    /// </summary>
    public static class MegaErrorMapper
    {
        private static readonly Dictionary<int, string> ApiNames = new Dictionary<int, string>
        {
            { -1, "EINTERNAL" },
            { -2, "EARGS" },
            { -3, "EAGAIN" },
            { -4, "ERATELIMIT" },
            { -6, "ETOOMANY" },
            { -8, "EEXPIRED" },
            { -9, "ENOENT" },
            { -11, "EACCESS" },
            { -16, "EBLOCKED" },
            { -17, "EOVERQUOTA" },
            { -18, "ETEMPUNAVAIL" },
        };

        /// <summary>Maps a negative mega API result code.</summary>
        public static LauncherException MapApiCode(int code, out bool transient)
        {
            transient = false;
            string name;
            string label = ApiNames.TryGetValue(code, out name) ? name : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (code)
            {
                case -4:
                case -6:
                case -17:
                    return Quota(null);
                case -1:
                case -3:
                case -18:
                    transient = true;
                    return new LauncherException(LauncherError.SourceHttpError, "mega.nz is busy right now (" + label + ").");
                case -8:
                case -9:
                case -11:
                case -16:
                    return new LauncherException(LauncherError.SourceHttpError, "The file is no longer available on mega.nz (" + label + ").");
                default:
                    return new LauncherException(LauncherError.SourceHttpError, "mega.nz refused the request (" + label + ").");
            }
        }

        /// <summary>Maps a non-success HTTP status from the mega API or a storage server.</summary>
        public static LauncherException MapStatus(int status, out bool transient)
        {
            transient = false;
            if (status == 509 || status == 429)
            {
                return Quota(null);
            }

            transient = status >= 500 || status == 408;
            return new LauncherException(LauncherError.SourceHttpError, "mega.nz answered HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>Maps <paramref name="ex"/> to a classified failure and whether a retry could help.</summary>
        public static LauncherException Map(Exception ex, out bool transient)
        {
            Exception e = Unwrap(ex);
            transient = false;

            if (e is TransientDownloadException t)
            {
                transient = true;
                return t.Final;
            }

            if (e is LauncherException le)
            {
                transient = le.Code == LauncherError.NetworkUnavailable;
                return le;
            }

            if (e is UnauthorizedAccessException)
            {
                return LauncherException.FromIo(e, "saving the download");
            }

            if (e is IOException io)
            {
                if (IsNetworkInterruption(io))
                {
                    transient = true;
                    return new LauncherException(LauncherError.NetworkUnavailable, "The connection to mega.nz was interrupted.", e);
                }

                // 0x20 ERROR_SHARING_VIOLATION (antivirus holding the part), 0x50 ERROR_FILE_EXISTS and
                // 0xB7 ERROR_ALREADY_EXISTS are worth a retry; disk full, bad path and device errors fail fast.
                int code = io.HResult & 0xFFFF;
                transient = code == 0x20 || code == 0x50 || code == 0xB7;
                return LauncherException.FromIo(e, "saving the download");
            }

            if (e is HttpRequestException || e is WebException || e is SocketException || e is TimeoutException)
            {
                transient = true;
                return new LauncherException(LauncherError.NetworkUnavailable, "Could not reach mega.nz.", e);
            }

            return new LauncherException(LauncherError.Unknown, "Unexpected error while downloading from mega.nz: " + e.Message, e);
        }

        /// <summary>The quota failure, with the player-facing wording the error table expects.</summary>
        internal static LauncherException Quota(Exception inner)
        {
            return new LauncherException(LauncherError.SourceQuotaExceeded, "mega.nz's free download limit has been reached for your connection. Try again in a few hours.", inner);
        }

        private static bool IsNetworkInterruption(IOException io)
        {
            for (Exception inner = io.InnerException; inner != null; inner = inner.InnerException)
            {
                if (inner is SocketException || inner is WebException || inner is HttpRequestException)
                {
                    return true;
                }
            }

            return false;
        }

        private static Exception Unwrap(Exception ex)
        {
            Exception e = ex;
            while (e is AggregateException agg && agg.InnerExceptions.Count >= 1)
            {
                e = agg.InnerExceptions[0];
            }

            return e;
        }
    }
}
