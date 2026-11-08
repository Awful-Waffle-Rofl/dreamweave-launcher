using System;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Cleanup;

namespace DreamWeave.Launcher.App
{
    /// <summary>How the process was asked to run.</summary>
    internal enum StartupMode
    {
        /// <summary>No switch: the normal interactive launcher.</summary>
        Normal = 0,

        /// <summary><c>--elevated-copy &lt;task&gt;</c>: the elevated helper. No UI.</summary>
        ElevatedCopy = 1,

        /// <summary>
        /// <c>--cleanup [--silent]</c>: remove saved passwords and launcher data (and, if asked, the AC client).
        /// Run by the setup program's uninstaller, or by Settings when no setup uninstaller is registered.
        /// </summary>
        Cleanup = 2,

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
        /// Extra argument of <c>--cleanup</c> when the launcher's own Settings started it: the
        /// starting window is about to exit, so the mutex is waited for instead of refused at once.
        /// </summary>
        public const string HandoverSwitch = "--handover";

        /// <summary>How long a hand-over (<c>--updated</c>, <c>--cleanup --handover</c>) waits for the previous launcher's mutex.</summary>
        public static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(30);

        /// <summary>How long <c>--cleanup --silent</c> waits for the previous launcher to release the single-instance mutex.</summary>
        public static readonly TimeSpan SilentWait = TimeSpan.FromSeconds(15);

        private StartupArgs(StartupMode mode, string taskPath, bool handover = false, bool silent = false)
        {
            Mode = mode;
            TaskPath = taskPath;
            IsHandover = handover;
            IsSilent = silent;
        }

        /// <summary><c>--cleanup --silent</c>: no window, no AC client removal, exit code 0.</summary>
        public bool IsSilent { get; }

        /// <summary>
        /// True when the previous launcher is exiting on purpose and will release the mutex:
        /// <c>--updated</c>, and <c>--cleanup --handover</c>.
        /// </summary>
        public bool IsHandover { get; }

        /// <summary>
        /// How long to wait for the mutex: zero for a normal start (hand over to the open window) and for a
        /// plain <c>--cleanup</c> (an open window is reported at once); <see cref="SilentWait"/> for <c>--cleanup --silent</c>;
        /// <see cref="HandoverWait"/> for a hand-over.
        /// </summary>
        public TimeSpan MutexWait => IsSilent ? SilentWait : IsHandover ? HandoverWait : TimeSpan.Zero;

        /// <summary>The selected mode.</summary>
        public StartupMode Mode { get; }

        /// <summary><see cref="StartupMode.ElevatedCopy"/> only: the task file path.</summary>
        public string TaskPath { get; }

        /// <summary>
        /// Whether this mode takes the single-instance mutex. The elevated helper never does (its parent
        /// holds it and is waiting for it); <c>--updated</c> and <c>--cleanup</c> wait a bounded time for
        /// the previous holder to release it instead of handing over to it.
        /// </summary>
        public bool UsesMutex => Mode == StartupMode.Normal || Mode == StartupMode.Updated || Mode == StartupMode.Cleanup;

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

            // The old --uninstall switch is gone (the setup program owns uninstall); it falls through to Invalid.
            if (string.Equals(first, SetupConstants.CleanupSwitch, StringComparison.Ordinal))
            {
                bool handover = false;
                bool silent = false;
                for (int i = 1; i < args.Length; i++)
                {
                    if (string.Equals(args[i], HandoverSwitch, StringComparison.Ordinal))
                    {
                        handover = true;
                    }
                    else if (string.Equals(args[i], SetupConstants.SilentSwitch, StringComparison.Ordinal))
                    {
                        silent = true;
                    }
                    else
                    {
                        return new StartupArgs(StartupMode.Invalid, null);
                    }
                }

                return new StartupArgs(StartupMode.Cleanup, null, handover, silent);
            }

            if (string.Equals(first, CleanupLayout.UpdatedSwitch, StringComparison.Ordinal))
            {
                return new StartupArgs(StartupMode.Updated, null, true);
            }

            return new StartupArgs(StartupMode.Invalid, null);
        }
    }
}
