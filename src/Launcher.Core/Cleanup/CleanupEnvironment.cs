using System;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.Core.Cleanup
{
    /// <summary>
    /// Everything <see cref="LauncherCleanup"/> touches, gathered so tests can point each piece at a
    /// temp folder or a fake. <see cref="CreateDefault"/> wires the real implementations.
    /// </summary>
    public sealed class CleanupEnvironment
    {
        /// <summary>The launcher's folders (only the data folder is removed).</summary>
        public AppPaths Paths { get; set; }

        /// <summary>Read-only HKLM (32-bit view) access, used to find the AC client's own uninstaller.</summary>
        public IRegistryReader MachineRegistry { get; set; }

        /// <summary>File operations.</summary>
        public ISelfFileSystem FileSystem { get; set; }

        /// <summary>Process starts (AC client uninstaller).</summary>
        public IProcessLauncher Processes { get; set; }

        /// <summary>Saved passwords; cleanup removes every one.</summary>
        public ICredentialStore Credentials { get; set; }

        /// <summary>The real environment for the current user.</summary>
        public static CleanupEnvironment CreateDefault(AppPaths paths)
        {
            return new CleanupEnvironment
            {
                Paths = paths ?? throw new ArgumentNullException(nameof(paths)),
                MachineRegistry = new RegistryReader(),
                FileSystem = new SelfFileSystem(),
                Processes = new ProcessLauncher(),
                Credentials = new WindowsCredentialStore(),
            };
        }

        internal void Validate()
        {
            if (Paths == null || FileSystem == null || Processes == null || Credentials == null || MachineRegistry == null)
            {
                throw new ArgumentException("Every CleanupEnvironment member must be set.");
            }
        }
    }
}