using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>Options for <see cref="SelfUninstaller.Uninstall"/>.</summary>
    public sealed class UninstallOptions
    {
        /// <summary>Full path of the running exe (normally the installed one, started with <c>--uninstall</c>).</summary>
        public string CurrentExePath { get; set; }

        /// <summary>
        /// Also run the Asheron's Call client's OWN uninstaller (its UninstallString from the HKLM
        /// uninstall key). The launcher never deletes the client's files itself. Default false.
        /// </summary>
        public bool RemoveAcClient { get; set; }
    }

    /// <summary>What happened to the AC client when <see cref="UninstallOptions.RemoveAcClient"/> was set.</summary>
    public enum AcClientUninstallOutcome
    {
        /// <summary>The option was not set; the client was not touched.</summary>
        NotRequested = 0,

        /// <summary>No uninstall entry for the client was found.</summary>
        NotFound = 1,

        /// <summary>The client's uninstaller ran and exited with code 0.</summary>
        Completed = 2,

        /// <summary>The client's uninstaller could not be started or exited with a non-zero code.</summary>
        Failed = 3,

        /// <summary>The player declined the UAC prompt for the client's uninstaller.</summary>
        Declined = 4,
    }

    /// <summary>The outcome of <see cref="SelfUninstaller.Uninstall"/>. Uninstall is best-effort: it never stops at the first failure.</summary>
    public sealed class UninstallResult
    {
        internal UninstallResult(IReadOnlyList<string> failures, bool selfDeleteScheduled, AcClientUninstallOutcome acClient, int? acClientExitCode, int credentialsRemoved, IReadOnlyList<string> leftoverFolders, bool credentialsLeftOver)
        {
            Failures = failures;
            SelfDeleteScheduled = selfDeleteScheduled;
            AcClient = acClient;
            AcClientExitCode = acClientExitCode;
            CredentialsRemoved = credentialsRemoved;
            LeftoverFolders = leftoverFolders ?? new string[0];
            CredentialsLeftOver = credentialsLeftOver;
        }

        /// <summary>
        /// The launcher folders (<see cref="AppPaths.DataDir"/>, <see cref="AppPaths.InstallDir"/>) that
        /// could not be fully removed, as full paths, for the player to delete by hand. Structured
        /// data for the UI: unlike <see cref="Failures"/> it never carries exception text or account names.
        /// </summary>
        public IReadOnlyList<string> LeftoverFolders { get; }

        /// <summary>True when at least one saved password could not be listed or removed.</summary>
        public bool CredentialsLeftOver { get; }

        /// <summary>Plain-English descriptions of everything that could not be removed. Empty on full success.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>True when everything was removed (or, for the running exe, is scheduled for removal).</summary>
        public bool Complete => Failures.Count == 0;

        /// <summary>
        /// <see cref="LauncherError.None"/> on full success, otherwise <see cref="LauncherError.UninstallIncomplete"/>
        /// (for telemetry and the UI).
        /// </summary>
        public LauncherError Code => Complete ? LauncherError.None : LauncherError.UninstallIncomplete;

        /// <summary>True when the running exe lives in the install folder and a helper will delete it after this process exits. The caller should exit promptly.</summary>
        public bool SelfDeleteScheduled { get; }

        /// <summary>What happened to the AC client.</summary>
        public AcClientUninstallOutcome AcClient { get; }

        /// <summary>Exit code of the client's uninstaller, when it ran.</summary>
        public int? AcClientExitCode { get; }

        /// <summary>How many saved passwords were removed.</summary>
        public int CredentialsRemoved { get; }
    }

    /// <summary>
    /// Removes everything <see cref="SelfInstaller"/> and the launcher created, and NOTHING else:
    /// the Start-menu shortcut (only if it still points at our exe), the HKCU uninstall key, every
    /// saved <c>DreamWeave/*</c> password, <see cref="AppPaths.DataDir"/> and <see cref="AppPaths.InstallDir"/>.
    /// Folder deletion never follows junctions or symbolic links. The running exe cannot delete
    /// itself, so when it lives in the install folder it is handed to an <see cref="ISelfDeleteScheduler"/>.
    /// </summary>
    public sealed class SelfUninstaller
    {
        /// <summary>Leaf name <see cref="AppPaths.InstallDir"/> must have; anything else is refused as a corrupted path.</summary>
        internal const string InstallLeaf = "DreamWeave Launcher";

        /// <summary>Leaf name <see cref="AppPaths.DataDir"/> must have; anything else is refused as a corrupted path.</summary>
        internal const string DataLeaf = "DreamWeave";

        private readonly SelfInstallEnvironment _env;

        /// <summary>Creates an uninstaller.</summary>
        public SelfUninstaller(SelfInstallEnvironment environment)
        {
            _env = environment ?? throw new ArgumentNullException(nameof(environment));
            _env.Validate();
        }

        /// <summary>Uninstalls. Never throws for a removal failure; see <see cref="UninstallResult.Failures"/>.</summary>
        public UninstallResult Uninstall(UninstallOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var failures = new List<string>();
            string installDir = Path.GetFullPath(_env.Paths.InstallDir);
            string dataDir = Path.GetFullPath(_env.Paths.DataDir);
            if (!string.Equals(Path.GetFileName(installDir), InstallLeaf, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(dataDir), DataLeaf, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Refusing to uninstall from unexpected folders (" + installDir + ", " + dataDir + ").");
            }

            AcClientUninstallOutcome ac = AcClientUninstallOutcome.NotRequested;
            int? acExit = null;
            if (options.RemoveAcClient)
            {
                ac = RunAcClientUninstaller(failures, out acExit);
            }

            int before = failures.Count;
            int creds = RemoveCredentials(failures);
            bool credentialsLeft = failures.Count > before;
            RemoveShortcut(failures);
            RemoveRegistration(failures);

            var leftovers = new List<string>();
            before = failures.Count;
            _env.FileSystem.DeleteTree(dataDir, null, failures);
            if (failures.Count > before)
            {
                leftovers.Add(dataDir);
            }

            string running = string.IsNullOrEmpty(options.CurrentExePath) ? null : Path.GetFullPath(options.CurrentExePath);
            bool runningInside = running != null && SelfInstallLayout.IsInside(running, installDir);
            before = failures.Count;
            _env.FileSystem.DeleteTree(installDir, runningInside ? running : null, failures);
            bool installLeft = failures.Count > before;

            bool scheduled = false;
            if (runningInside)
            {
                scheduled = _env.SelfDelete.Schedule(running, installDir);
                if (!scheduled)
                {
                    failures.Add(running + ": could not arrange for the launcher to delete itself; delete this file after the launcher closes.");
                    installLeft = true;
                }
            }

            if (installLeft)
            {
                leftovers.Add(installDir);
            }

            return new UninstallResult(failures, scheduled, ac, acExit, creds, leftovers, credentialsLeft);
        }

        /// <summary>Splits an UninstallString into the program and its arguments.</summary>
        internal static void SplitCommandLine(string commandLine, out string program, out string arguments)
        {
            string s = (commandLine ?? string.Empty).Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = s.IndexOf('"', 1);
                if (close < 0)
                {
                    program = s.Substring(1);
                    arguments = string.Empty;
                    return;
                }

                program = s.Substring(1, close - 1);
                arguments = s.Substring(close + 1).Trim();
                return;
            }

            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0 && (exe + 4 == s.Length || s[exe + 4] == ' '))
            {
                program = s.Substring(0, exe + 4);
                arguments = s.Substring(exe + 4).Trim();
                return;
            }

            int space = s.IndexOf(' ');
            program = space < 0 ? s : s.Substring(0, space);
            arguments = space < 0 ? string.Empty : s.Substring(space + 1).Trim();
        }

        /// <summary>The client's UninstallString: the known-GUID key first, else the single entry whose DisplayName is exactly "Asheron's Call".</summary>
        internal string FindAcClientUninstallString()
        {
            IRegistryReader reg = _env.MachineRegistry;
            string byGuid = reg.GetStringValue(AcInstallLocator.UninstallRoot + "\\" + AcInstallLocator.UninstallGuid, "UninstallString");
            if (!string.IsNullOrWhiteSpace(byGuid))
            {
                return byGuid;
            }

            string found = null;
            foreach (string name in reg.GetSubKeyNames(AcInstallLocator.UninstallRoot))
            {
                string key = AcInstallLocator.UninstallRoot + "\\" + name;
                string display = reg.GetStringValue(key, "DisplayName");
                if (display == null || !string.Equals(display.Replace((char)0x2019, '\'').Trim(), "Asheron's Call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string cmd = reg.GetStringValue(key, "UninstallString");
                if (string.IsNullOrWhiteSpace(cmd))
                {
                    continue;
                }

                if (found != null)
                {
                    // Ambiguous: never guess which program to run.
                    return null;
                }

                found = cmd;
            }

            return found;
        }

        private AcClientUninstallOutcome RunAcClientUninstaller(List<string> failures, out int? exitCode)
        {
            exitCode = null;
            string command = FindAcClientUninstallString();
            if (command == null)
            {
                return AcClientUninstallOutcome.NotFound;
            }

            string program, arguments;
            SplitCommandLine(command, out program, out arguments);
            try
            {
                // ShellExecute so the client's uninstaller can raise its own UAC prompt.
                var psi = new ProcessStartInfo(program, arguments) { UseShellExecute = true };
                exitCode = _env.Processes.Run(psi);
                if (exitCode == 0)
                {
                    return AcClientUninstallOutcome.Completed;
                }

                failures.Add("The Asheron's Call uninstaller exited with code " + exitCode + ".");
                return AcClientUninstallOutcome.Failed;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the player said no to UAC. Their choice, not a failure.
                return AcClientUninstallOutcome.Declined;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                failures.Add("The Asheron's Call uninstaller could not be started: " + ex.Message);
                return AcClientUninstallOutcome.Failed;
            }
        }

        private int RemoveCredentials(List<string> failures)
        {
            int removed = 0;
            IReadOnlyList<string> accounts;
            try
            {
                accounts = _env.Credentials.ListAccounts();
            }
            catch (LauncherException ex)
            {
                failures.Add("Saved passwords could not be listed: " + ex.Detail);
                return 0;
            }

            foreach (string account in accounts)
            {
                try
                {
                    if (_env.Credentials.Delete(account))
                    {
                        removed++;
                    }
                }
                catch (LauncherException ex)
                {
                    failures.Add("The saved password for '" + account + "' could not be removed: " + ex.Detail);
                }
            }

            return removed;
        }

        private void RemoveShortcut(List<string> failures)
        {
            string lnk = _env.ShortcutPath;
            try
            {
                if (!_env.FileSystem.FileExists(lnk))
                {
                    return;
                }

                // Only remove the shortcut if it is ours (points into our install folder). An
                // unreadable target is treated as ours: the name is ours and we created it.
                string target = _env.Shortcuts.ReadTarget(lnk);
                if (target != null && target.Length > 0 && !SelfInstallLayout.IsInside(target, _env.Paths.InstallDir))
                {
                    return;
                }

                _env.FileSystem.DeleteFile(lnk);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                failures.Add(lnk + ": " + ex.Message);
            }
        }

        private void RemoveRegistration(List<string> failures)
        {
            try
            {
                _env.UserRegistry.DeleteKeyTree(_env.UninstallKeyPath);
            }
            catch (LauncherException ex)
            {
                failures.Add(ex.Detail);
            }
        }
    }
}
