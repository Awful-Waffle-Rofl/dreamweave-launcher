using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// The per-user single-instance lock: a named mutex in the session namespace whose name carries
    /// the user's SID, plus a registered window message the second start broadcasts so the first
    /// window can bring itself to the front.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const int HwndBroadcast = 0xFFFF;
        private const int AsfwAny = -1;

        private Mutex _mutex;
        private bool _owned;

        private SingleInstance(Mutex mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        /// <summary>The mutex name for the current user.</summary>
        public static string MutexName => @"Local\DreamWeaveLauncher-" + UserKey;

        /// <summary>The window message the running instance listens for.</summary>
        public static int ActivateMessage { get; } = RegisterWindowMessage("DreamWeaveLauncher-Activate-" + UserKey);

        /// <summary>True when this process holds the lock.</summary>
        public bool Owned => _owned;

        private static string UserKey
        {
            get
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return id.User?.Value ?? Environment.UserName;
                }
            }
        }

        /// <summary>
        /// Tries to take the lock, waiting up to <paramref name="wait"/> (zero: do not wait). An
        /// abandoned mutex (a previous launcher crashed) counts as taken.
        /// </summary>
        public static SingleInstance Acquire(TimeSpan wait)
        {
            var mutex = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            return new SingleInstance(mutex, owned);
        }

        /// <summary>Asks the running instance to show its window.</summary>
        public static void SignalRunningInstance()
        {
            // Let the running instance take the foreground; Windows otherwise refuses it.
            AllowSetForegroundWindow(AsfwAny);
            PostMessage(new IntPtr(HwndBroadcast), ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Releases the lock early (before starting another copy that must take it).</summary>
        public void Release()
        {
            if (_owned)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned by this thread: nothing to release.
                }

                _owned = false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Release();
            _mutex?.Dispose();
            _mutex = null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
