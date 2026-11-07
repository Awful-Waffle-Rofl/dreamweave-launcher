using System;
using DreamWeave.Launcher.Core.SelfInstall;

namespace DreamWeave.Launcher.App
{
    /// <summary>How the process was asked to run.</summary>
    internal enum StartupMode
    {
        /// <summary>No switch: the normal interactive launcher.</summary>
        Normal = 0,

        /// <summary><c>--elevated-copy &lt;task&gt;</c>: the elevated helper. No UI.</summary>
        ElevatedCopy = 1,

        /// <summary><c>--uninstall</c>: the uninstall confirmation, then removal.</summary>
        Uninstall = 2,

        /// <summary><c>--updated</c>: a freshly swapped-in launcher; finish the update, then start normally.</summary>
        Updated = 3,

        /// <summary>A switch this launcher does not know, or <c>--elevated-copy</c> without its task path.</summary>
        Invalid = 4,
    }

    /// <summary>Parsed command line.</summary>
    internal sealed class StartupArgs
    {
        /// <summary>Switch that runs the elevated copy helper.</summary>
        public const string ElevatedCopySwitch = "--elevated-copy";

        /// <summary>
        /// Second argument of <c>--uninstall</c> when the launcher's own Settings started it: the
        /// starting window is about to exit, so the mutex is waited for instead of refused at once.
        /// </summary>
        public const string HandoverSwitch = "--handover";

        /// <summary>How long a hand-over (<c>--updated</c>, <c>--uninstall --handover</c>) waits for the previous launcher's mutex.</summary>
        public static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(30);

        private StartupArgs(StartupMode mode, string taskPath, bool handover = false)
        {
            Mode = mode;
            TaskPath = taskPath;
            IsHandover = handover;
        }

        /// <summary>
        /// True when the previous launcher is exiting on purpose and will release the mutex:
        /// <c>--updated</c>, and <c>--uninstall --handover</c>.
        /// </summary>
        public bool IsHandover { get; }

        /// <summary>
        /// How long to wait for the mutex: zero for a normal start (hand over to the open window) and for a
        /// plain <c>--uninstall</c> (an open window is reported at once); <see cref="HandoverWait"/> for a hand-over.
        /// </summary>
        public TimeSpan MutexWait => IsHandover ? HandoverWait : TimeSpan.Zero;

        /// <summary>The selected mode.</summary>
        public StartupMode Mode { get; }

        /// <summary><see cref="StartupMode.ElevatedCopy"/> only: the task file path.</summary>
        public string TaskPath { get; }

        /// <summary>
        /// Whether this mode takes the single-instance mutex. The elevated helper never does (its parent
        /// holds it and is waiting for it); <c>--updated</c> and <c>--uninstall</c> wait a bounded time for
        /// the previous holder to release it instead of handing over to it.
        /// </summary>
        public bool UsesMutex => Mode == StartupMode.Normal || Mode == StartupMode.Updated || Mode == StartupMode.Uninstall;

        /// <summary>Whether a held mutex means "activate the running launcher and exit" (normal start only).</summary>
        public bool HandsOverToRunningInstance => Mode == StartupMode.Normal;

        /// <summary>Parses <paramref name="args"/>. Only the first argument selects a mode.</summary>
        public static StartupArgs Parse(string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(args[0]))
            {
                return new StartupArgs(StartupMode.Normal, null);
            }

            string first = args[0];
            if (string.Equals(first, ElevatedCopySwitch, StringComparison.Ordinal))
            {
                return args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1])
                    ? new StartupArgs(StartupMode.ElevatedCopy, args[1])
                    : new StartupArgs(StartupMode.Invalid, null);
            }

            if (string.Equals(first, SelfInstallLayout.UninstallSwitch, StringComparison.Ordinal))
            {
                bool handover = args.Length >= 2 && string.Equals(args[1], HandoverSwitch, StringComparison.Ordinal);
                return new StartupArgs(StartupMode.Uninstall, null, handover);
            }

            if (string.Equals(first, SelfInstallLayout.UpdatedSwitch, StringComparison.Ordinal))
            {
                return new StartupArgs(StartupMode.Updated, null, true);
            }

            return new StartupArgs(StartupMode.Invalid, null);
        }
    }
}
