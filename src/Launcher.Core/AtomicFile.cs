using System;
using System.IO;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// Whole-file writes that never leave a torn file behind: data goes to a sibling temp file,
    /// is flushed to disk, then replaces the target (<see cref="File.Replace(string, string, string, bool)"/>
    /// when the target exists, <see cref="File.Move(string, string)"/> otherwise).
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="data"/>, creating the folder if needed.</summary>
        /// <exception cref="LauncherException">DiskFull, AccessDenied or Unknown on I/O failure.</exception>
        public static void WriteAllBytes(string path, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            string full = Path.GetFullPath(path);
            string tmp = full + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }

                if (File.Exists(full))
                {
                    File.Replace(tmp, full, null, true);
                }
                else
                {
                    File.Move(tmp, full);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                throw LauncherException.FromIo(ex, "writing " + Path.GetFileName(full));
            }
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
