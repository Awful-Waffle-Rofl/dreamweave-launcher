using System;
using System.IO;

namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// The single exception type launcher code throws for an expected, classified failure.
    /// <see cref="Code"/> drives UI decisions and telemetry; <see cref="Detail"/> is plain English
    /// suitable for showing to the player. <see cref="Exception.Message"/> equals <see cref="Detail"/>.
    /// </summary>
    [Serializable]
    public class LauncherException : Exception
    {
        /// <summary>Creates a classified failure.</summary>
        /// <param name="code">The failure classification (also the telemetry code).</param>
        /// <param name="detail">A plain-English description for the player.</param>
        /// <param name="innerException">The underlying exception, if any.</param>
        public LauncherException(LauncherError code, string detail, Exception innerException = null)
            : base(detail ?? code.ToString(), innerException)
        {
            Code = code;
            Detail = detail ?? code.ToString();
        }

        /// <summary>The failure classification.</summary>
        public LauncherError Code { get; }

        /// <summary>Plain-English description of the failure, for the player.</summary>
        public string Detail { get; }

        /// <summary>
        /// Wraps an arbitrary exception from file I/O as a <see cref="LauncherException"/>, mapping
        /// disk-full and access-denied conditions to their codes and anything else to
        /// <see cref="LauncherError.Unknown"/>. An existing <see cref="LauncherException"/> is returned unchanged.
        /// </summary>
        public static LauncherException FromIo(Exception ex, string what)
        {
            if (ex is LauncherException le)
            {
                return le;
            }

            if (ex is UnauthorizedAccessException)
            {
                return new LauncherException(LauncherError.AccessDenied, "Windows denied access while " + what + ".", ex);
            }

            if (ex is IOException)
            {
                // ERROR_DISK_FULL (0x70) and ERROR_HANDLE_DISK_FULL (0x27), as HRESULTs.
                int hr = ex.HResult & 0xFFFF;
                if (hr == 0x70 || hr == 0x27)
                {
                    return new LauncherException(LauncherError.DiskFull, "The disk is full while " + what + ".", ex);
                }
            }

            return new LauncherException(LauncherError.Unknown, "Unexpected error while " + what + ": " + ex.Message, ex);
        }
    }
}
