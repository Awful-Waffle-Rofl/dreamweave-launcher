using System;
using System.IO;
using System.Threading;

namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>Small file helpers for <c>.part</c> files and sidecars.</summary>
    internal static class PartFiles
    {
        /// <summary>Length of <paramref name="path"/>, or -1 when it does not exist.</summary>
        public static long LengthOrMissing(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }

        /// <summary>Deletes a file if present, retrying briefly on sharing violations (antivirus); never throws.</summary>
        public static bool TryDelete(string path)
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                Thread.Sleep(20 * attempt);
            }

            return false;
        }

        /// <summary>Deletes a file if present; throws a classified <see cref="LauncherException"/> on failure.</summary>
        public static void Delete(string path, string what)
        {
            if (TryDelete(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, what);
            }
        }
    }
}
