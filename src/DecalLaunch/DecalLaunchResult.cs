using System;

namespace DreamWeave.DecalLaunch
{
    /// <summary>
    /// The exit codes and stderr tokens the helper reports. Both are a stable contract read by the
    /// launcher's Decal start seam (which compiles this same file), so values are explicit and must not
    /// change meaning. Exit code 0 is success; every failure is a distinct non-zero code paired with one
    /// token on stderr.
    /// </summary>
    public static class DecalLaunchResult
    {
        /// <summary>The client was started (its process id is on stdout). Decal may or may not have loaded.</summary>
        public const int ExitSuccess = 0;

        /// <summary>The helper was called with the wrong number of arguments.</summary>
        public const int ExitBadUsage = 2;

        /// <summary>The game folder is missing, or it does not contain acclient.exe.</summary>
        public const int ExitGameFolderInvalid = 3;

        /// <summary>
        /// The Inject.dll argument is not an existing absolute path named Inject.dll, contains a <c>..</c>
        /// segment, or is not the Inject.dll in the folder Decal's own registry entry names.
        /// </summary>
        public const int ExitInjectInvalid = 4;

        /// <summary>acclient.exe, Inject.dll, or a folder above Inject.dll is a junction or link, so nothing was started.</summary>
        public const int ExitReparsePoint = 5;

        /// <summary>Windows could not start acclient.exe for a reason other than elevation or access (no client was created).</summary>
        public const int ExitCreateProcessFailed = 6;

        /// <summary>acclient.exe is marked to run as administrator (CreateProcess error 740); no client was created.</summary>
        public const int ExitElevationRequired = 7;

        /// <summary>Windows denied permission to start acclient.exe (CreateProcess error 5); no client was created.</summary>
        public const int ExitAccessDenied = 8;

        // Stderr tokens. One printed per failure; the success path also prints a token when the client
        // started but Decal injection did not (the client runs without Decal: fail-open).

        /// <summary>Wrong argument count.</summary>
        public const string TokenBadUsage = "bad-usage";

        /// <summary>The game folder is empty or does not exist.</summary>
        public const string TokenGameFolderMissing = "game-folder-missing";

        /// <summary>acclient.exe is not in the game folder.</summary>
        public const string TokenAcclientMissing = "acclient-missing";

        /// <summary>The Inject.dll argument is not an absolute drive path ending in Inject.dll, or it has a <c>..</c> segment.</summary>
        public const string TokenInjectPathInvalid = "inject-path-invalid";

        /// <summary>The Inject.dll file does not exist.</summary>
        public const string TokenInjectMissing = "inject-missing";

        /// <summary>
        /// Decal's registry entry (HKLM SOFTWARE\Decal\Agent AgentPath, 32-bit view) is missing, or the
        /// Inject.dll argument is not the Inject.dll in the folder it names.
        /// </summary>
        public const string TokenInjectNotRegistered = "inject-not-decal-agent";

        /// <summary>acclient.exe itself is a reparse point (the game folder above it may be a link).</summary>
        public const string TokenAcclientReparsePoint = "acclient-is-reparse-point";

        /// <summary>Inject.dll, or any folder between the drive root and Inject.dll, is a reparse point.</summary>
        public const string TokenInjectReparsePoint = "inject-path-has-reparse-point";

        /// <summary>CreateProcess on acclient.exe failed for a reason other than elevation or access.</summary>
        public const string TokenCreateProcessFailed = "create-process-failed";

        /// <summary>CreateProcess on acclient.exe failed with error 740 (it is marked to run as administrator).</summary>
        public const string TokenElevationRequired = "create-process-elevation-required";

        /// <summary>CreateProcess on acclient.exe failed with error 5 (access denied).</summary>
        public const string TokenAccessDenied = "create-process-access-denied";

        /// <summary>The client was created and resumed, but Decal injection failed (it runs without Decal).</summary>
        public const string TokenInjectFailed = "inject-failed";

        private const int ErrorAccessDenied = 5;
        private const int ErrorElevationRequired = 740;

        /// <summary>The exit code and token for a CreateProcess failure with Win32 error <paramref name="win32Error"/>.</summary>
        public static int ForCreateProcessError(int win32Error, out string token)
        {
            switch (win32Error)
            {
                case ErrorElevationRequired:
                    token = TokenElevationRequired;
                    return ExitElevationRequired;
                case ErrorAccessDenied:
                    token = TokenAccessDenied;
                    return ExitAccessDenied;
                default:
                    token = TokenCreateProcessFailed;
                    return ExitCreateProcessFailed;
            }
        }

        /// <summary>The line the helper writes to stdout on success, carrying the started process id.</summary>
        public static string PidLine(int pid)
        {
            return "PID=" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Parses a <see cref="PidLine"/> back to a positive process id, or returns 0 when it does not match.</summary>
        public static int ParsePid(string line)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("PID=", StringComparison.Ordinal))
            {
                return 0;
            }

            return int.TryParse(line.Substring(4).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : 0;
        }
    }
}
