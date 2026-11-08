using System.Threading;
using DreamWeave.Launcher.Core;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Holds the named mutex <see cref="SetupConstants.SetupMutexName"/> for the life of the process so the
    /// setup program's AppMutex check can tell the launcher is running. It is separate from the
    /// per-user single-instance lock. The mutex is never owned, only kept in existence.
    /// </summary>
    internal static class SetupGuard
    {
        private static readonly object Gate = new object();
        private static Mutex _mutex;

        /// <summary>True while this process holds the mutex.</summary>
        public static bool IsHeld
        {
            get
            {
                lock (Gate)
                {
                    return _mutex != null;
                }
            }
        }

        /// <summary>Creates (or opens) the named mutex and keeps the handle. Idempotent.</summary>
        public static void Hold()
        {
            lock (Gate)
            {
                if (_mutex == null)
                {
                    _mutex = new Mutex(false, SetupConstants.SetupMutexName);
                }
            }
        }

        /// <summary>Closes the handle (before handing over to the setup's uninstaller, which waits for the mutex to go). Idempotent.</summary>
        public static void Release()
        {
            lock (Gate)
            {
                _mutex?.Dispose();
                _mutex = null;
            }
        }
    }
}