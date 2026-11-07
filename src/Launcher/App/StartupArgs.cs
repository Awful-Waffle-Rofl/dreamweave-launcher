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

        private StartupArgs(StartupMode mode, string taskPath)
        {
            Mode = mode;
            TaskPath = taskPath;
        }

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
                return new StartupArgs(StartupMode.Uninstall, null);
            }

            if (string.Equals(first, SelfInstallLayout.UpdatedSwitch, StringComparison.Ordinal))
            {
                return new StartupArgs(StartupMode.Updated, null);
            }

            return new StartupArgs(StartupMode.Invalid, null);
        }
    }
}
