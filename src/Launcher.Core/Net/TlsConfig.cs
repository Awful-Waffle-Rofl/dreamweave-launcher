using System;
using System.Net;

namespace DreamWeave.Launcher.Core.Net
{
    /// <summary>
    /// Makes sure TLS 1.2 is available to <see cref="ServicePointManager"/>-based networking on
    /// .NET Framework 4.8. On Windows 8 and later with the default (SystemDefault) setting the
    /// OS already negotiates TLS 1.2/1.3, so nothing is changed. On Windows 7 (OS version 6.1),
    /// where SChannel does not enable TLS 1.2 for clients by default, or when some other code has
    /// pinned an explicit protocol list without TLS 1.2, TLS 1.2 is added.
    /// </summary>
    public static class TlsConfig
    {
        private static readonly object Gate = new object();

        /// <summary>Idempotent; safe to call from any thread.</summary>
        public static void EnsureModernTls()
        {
            lock (Gate)
            {
                SecurityProtocolType current = ServicePointManager.SecurityProtocol;
                bool systemDefault = current == SecurityProtocolType.SystemDefault;
                Version os = Environment.OSVersion.Version;
                bool preWin8 = os.Major < 6 || (os.Major == 6 && os.Minor < 2);

                if (systemDefault && !preWin8)
                {
                    return;
                }

                if ((current & SecurityProtocolType.Tls12) == 0)
                {
                    ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
                }
            }
        }
    }
}
