using System;
using System.IO;
using System.Threading;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// Whole-file writes that never leave a torn file behind: data goes to a uniquely named sibling
    /// temp file (<c>&lt;name&gt;.&lt;guid&gt;.tmp</c>, so concurrent writers never share one), is
    /// flushed to disk, then replaces the target (<see cref="File.Replace(string, string, string, bool)"/>
    /// when the target exists, <see cref="File.Move(string, string)"/> otherwise). The replace step
    /// is retried briefly on sharing violations (antivirus scanners and concurrent readers cause
    /// transient ones). Concurrent writers to the SAME target still race on content (last replace
    /// wins); callers that need read-modify-write serialise themselves (see <see cref="State.StateStore"/>).
    /// </summary>
    public static class AtomicFile
    {
        private const int ReplaceAttempts = 10;

        /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="data"/>, creating the folder if needed.</summary>
        /// <exception cref="LauncherException">DiskFull, AccessDenied or Unknown on I/O failure.</exception>
        public static void WriteAllBytes(string path, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            string full = Path.GetFullPath(path);
            string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(full))
                        {
                            File.Replace(tmp, full, null, true);
                        }
                        else
                        {
                            File.Move(tmp, full);
                        }

                        break;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < ReplaceAttempts && !IsDiskFull(ex))
                    {
                        Thread.Sleep(10 * attempt);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                throw LauncherException.FromIo(ex, "writing " + Path.GetFileName(full));
            }
        }

        private static bool IsDiskFull(Exception ex)
        {
            int hr = ex.HResult & 0xFFFF;
            return hr == 0x70 || hr == 0x27;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
