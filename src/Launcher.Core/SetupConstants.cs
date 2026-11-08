namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// The values the launcher and its setup program (Inno Setup) must agree on. The setup script is
    /// checked against this class, so change a value here only together with the script.
    /// </summary>
    public static class SetupConstants
    {
        /// <summary>
        /// The setup program's AppId. Inno Setup names its uninstall key <c>{AppId}_is1</c>; the braces
        /// are part of the id.
        /// </summary>
        public const string SetupAppId = "{06DC956B-92EB-4FD7-87A7-89635453F9B7}";

        /// <summary>Named mutex the running launcher holds so the setup program's AppMutex can detect it.</summary>
        public const string SetupMutexName = "DreamWeaveLauncherSetupGuard";

        /// <summary>Command-line switch the setup's uninstaller runs to remove saved passwords and launcher data.</summary>
        public const string CleanupSwitch = "--cleanup";

        /// <summary>With <see cref="CleanupSwitch"/>: no window, never touches the Asheron's Call client, always exits 0.</summary>
        public const string SilentSwitch = "--silent";

        /// <summary>
        /// Exit code of a plain <c>--cleanup</c> (no <c>--handover</c>, as the setup's uninstaller runs it) when the
        /// player cancels at the confirmation. The uninstaller then runs <c>--cleanup --silent</c> instead, because
        /// the uninstall goes ahead regardless and must not leave saved passwords and data behind.
        /// </summary>
        public const int CleanupCancelledExitCode = 120;

        /// <summary>Folder name the setup installs into.</summary>
        public const string InstallFolderName = "DreamWeave Launcher";

        /// <summary>The HKCU key (relative to the hive) where Inno Setup records a per-user install's uninstall command.</summary>
        public const string SetupUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SetupAppId + "_is1";
    }
}