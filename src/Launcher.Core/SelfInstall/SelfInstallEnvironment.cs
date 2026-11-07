using System;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>
    /// Everything self-install and uninstall touch, gathered so tests can point each piece at a
    /// temp folder or a fake. <see cref="CreateDefault"/> wires the real implementations.
    /// </summary>
    public sealed class SelfInstallEnvironment
    {
        /// <summary>The launcher's folders (install and data).</summary>
        public AppPaths Paths { get; set; }

        /// <summary>HKCU access for the uninstall key.</summary>
        public IUserRegistry UserRegistry { get; set; }

        /// <summary>Read-only HKLM (32-bit view) access, used to find the AC client's own uninstaller.</summary>
        public IRegistryReader MachineRegistry { get; set; }

        /// <summary>Shortcut creation.</summary>
        public IShortcutWriter Shortcuts { get; set; }

        /// <summary>The per-user Start-menu Programs folder the shortcut goes in.</summary>
        public string ProgramsFolder { get; set; }

        /// <summary>File operations.</summary>
        public ISelfFileSystem FileSystem { get; set; }

        /// <summary>Process starts (AC client uninstaller).</summary>
        public IProcessLauncher Processes { get; set; }

        /// <summary>Saved passwords; uninstall removes every one.</summary>
        public ICredentialStore Credentials { get; set; }

        /// <summary>Removes the running exe after it exits.</summary>
        public ISelfDeleteScheduler SelfDelete { get; set; }

        /// <summary>The uninstall key, relative to HKCU. Defaults to <see cref="SelfInstallLayout.UninstallKeyPath"/>.</summary>
        public string UninstallKeyPath { get; set; } = SelfInstallLayout.UninstallKeyPath;

        /// <summary>Full path of the Start-menu shortcut.</summary>
        public string ShortcutPath => System.IO.Path.Combine(ProgramsFolder, SelfInstallLayout.ShortcutFileName);

        /// <summary>Full path of the installed exe.</summary>
        public string InstalledExePath => SelfInstallLayout.InstalledExePath(Paths);

        /// <summary>The real environment for the current user.</summary>
        public static SelfInstallEnvironment CreateDefault(AppPaths paths)
        {
            var processes = new ProcessLauncher();
            return new SelfInstallEnvironment
            {
                Paths = paths ?? throw new ArgumentNullException(nameof(paths)),
                UserRegistry = new UserRegistry(),
                MachineRegistry = new RegistryReader(),
                Shortcuts = new ShellLinkWriter(),
                ProgramsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                FileSystem = new SelfFileSystem(),
                Processes = processes,
                Credentials = new WindowsCredentialStore(),
                SelfDelete = new SelfDeleteScheduler(processes),
            };
        }

        internal void Validate()
        {
            if (Paths == null || UserRegistry == null || Shortcuts == null || string.IsNullOrEmpty(ProgramsFolder) || FileSystem == null
                || Processes == null || Credentials == null || SelfDelete == null || MachineRegistry == null || string.IsNullOrEmpty(UninstallKeyPath))
            {
                throw new ArgumentException("Every SelfInstallEnvironment member must be set.");
            }
        }
    }
}
