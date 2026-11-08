using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>What happened to one download source attempt.</summary>
    public enum DownloadAttemptOutcome
    {
        /// <summary>The source delivered the verified file.</summary>
        Succeeded,

        /// <summary>A verified copy was already in the staging folder; nothing was fetched.</summary>
        AlreadyPresent,

        /// <summary>The source failed.</summary>
        Failed,

        /// <summary>The player stopped the download.</summary>
        Cancelled,
    }

    /// <summary>One line of the download log.</summary>
    public sealed class DownloadAttemptRecord
    {
        /// <summary>Component (step) id.</summary>
        public string ComponentId { get; set; }

        /// <summary>0 for the primary source, 1.. for alternates.</summary>
        public int SourceIndex { get; set; }

        /// <summary>A <see cref="Manifests.Transports"/> value.</summary>
        public string Transport { get; set; }

        /// <summary>Host name of the source URL only (never the path, query or mega key).</summary>
        public string Host { get; set; }

        /// <summary>The server that actually served the bytes when the transport knows it (mega's storage host), or null.</summary>
        public string StorageHost { get; set; }

        /// <summary>Result of the attempt.</summary>
        public DownloadAttemptOutcome Outcome { get; set; }

        /// <summary>Bytes on disk for this attempt when it ended.</summary>
        public long Bytes { get; set; }

        /// <summary>Wall-clock duration of the attempt.</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>The failure, or null.</summary>
        public Exception Error { get; set; }
    }

    /// <summary>Receives one record per download source attempt. Implementations must never throw.</summary>
    public interface IDownloadLog
    {
        /// <summary>Records <paramref name="record"/>.</summary>
        void Record(DownloadAttemptRecord record);
    }

    /// <summary>
    /// A small rolling text log (normally <see cref="AppPaths.LogFile"/>). When the file would grow past
    /// <see cref="MaxBytes"/> it is renamed to <c>&lt;name&gt;.1</c> (replacing any older one) and a new file
    /// starts, so at most two files exist. It holds component ids, transports, host names, outcomes,
    /// byte counts, durations and exception types and messages; the current user's profile folder is
    /// replaced by <c>%USERPROFILE%</c>, and no account name or password ever reaches it. Every
    /// failure to write is swallowed: logging never fails a download.
    /// </summary>
    public sealed class FileDownloadLog : IDownloadLog
    {
        /// <summary>Default size cap of the live file.</summary>
        public const long DefaultMaxBytes = 1024 * 1024;

        private static readonly object Gate = new object();
        private readonly string _path;
        private readonly string _profile;

        /// <summary>Creates a log writing to <paramref name="path"/>.</summary>
        public FileDownloadLog(string path, long maxBytes = DefaultMaxBytes, string userProfile = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A log path is required.", nameof(path));
            }

            _path = Path.GetFullPath(path);
            MaxBytes = maxBytes;
            _profile = userProfile ?? SafeProfile();
        }

        /// <summary>Size cap of the live file.</summary>
        public long MaxBytes { get; }

        /// <summary>The live file.</summary>
        public string FilePath => _path;

        /// <summary>The one older file kept after a roll-over.</summary>
        public string OldPath => _path + ".1";

        /// <inheritdoc />
        public void Record(DownloadAttemptRecord record)
        {
            if (record == null)
            {
                return;
            }

            try
            {
                Write(Format(record));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException)
            {
            }
        }

        /// <summary>
        /// Writes one free-form line, <c>&lt;time&gt; &lt;kind&gt; &lt;text&gt;</c>, for install decisions worth
        /// explaining later. <paramref name="text"/> gets the same clean-up as a record; never throws.
        /// </summary>
        public void Note(string kind, string text)
        {
            try
            {
                Write(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + Clean(kind) + " " + Clean(text));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException)
            {
            }
        }

        /// <summary>Formats one line (no trailing newline).</summary>
        internal string Format(DownloadAttemptRecord r)
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            sb.Append(" download component=").Append(Clean(r.ComponentId));
            sb.Append(" source=").Append(r.SourceIndex.ToString(CultureInfo.InvariantCulture));
            sb.Append(" transport=").Append(Clean(r.Transport));
            sb.Append(" host=").Append(Clean(r.Host));
            if (!string.IsNullOrEmpty(r.StorageHost))
            {
                sb.Append(" storage=").Append(Clean(r.StorageHost));
            }
            sb.Append(" outcome=").Append(r.Outcome.ToString());
            sb.Append(" bytes=").Append(r.Bytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(" duration=").Append(r.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('s');
            if (r.Duration.TotalSeconds > 0 && r.Bytes > 0)
            {
                sb.Append(" rate=").Append((r.Bytes / r.Duration.TotalSeconds / (1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture)).Append("MB/s");
            }

            if (r.Error != null)
            {
                sb.Append(" error=");
                bool first = true;
                for (Exception e = r.Error; e != null; e = e.InnerException)
                {
                    if (!first)
                    {
                        sb.Append(" <- ");
                    }

                    first = false;
                    sb.Append(e.GetType().FullName);
                    if (e is LauncherException le)
                    {
                        sb.Append('(').Append(le.Code.ToString()).Append(')');
                    }

                    // AggregateException.InnerException is its first inner exception, so the chain follows it.
                    sb.Append(": ").Append(Clean(e.Message));
                }
            }

            return sb.ToString();
        }

        private void Write(string line)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\r\n");
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var info = new FileInfo(_path);
                if (info.Exists && info.Length + bytes.Length > MaxBytes)
                {
                    if (File.Exists(OldPath))
                    {
                        File.Delete(OldPath);
                    }

                    File.Move(_path, OldPath);
                }

                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }

        private string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            string s = value.Replace('\r', ' ').Replace('\n', ' ');
            if (!string.IsNullOrEmpty(_profile))
            {
                int i;
                while ((i = s.IndexOf(_profile, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    s = s.Substring(0, i) + "%USERPROFILE%" + s.Substring(i + _profile.Length);
                }
            }

            return s;
        }

        private static string SafeProfile()
        {
            try
            {
                string p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(p) ? null : p.TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
