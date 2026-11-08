using System;

namespace DreamWeave.DecalLaunch
{
    /// <summary>
    /// The exit codes and stderr tokens the helper reports. Both are a stable contract read by the
    /// launcher's Decal start seam, so values are explicit and must not change meaning. Exit code 0 is
    /// success; every failure is a distinct non-zero code paired with one token on stderr.
    /// </summary>
    public static class DecalLaunchResult
    {
        /// <summary>The client was started (its process id is on stdout). Decal may or may not have loaded.</summary>
        public const int ExitSuccess = 0;

        /// <summary>The helper was called with the wrong number of arguments.</summary>
        public const int ExitBadUsage = 2;

        /// <summary>The game folder is missing, or it does not contain acclient.exe.</summary>
        public const int ExitGameFolderInvalid = 3;

        /// <summary>The Inject.dll path is not an existing absolute path named Inject.dll.</summary>
        public const int ExitInjectInvalid = 4;

        /// <summary>A path passes through a junction or symbolic link, so nothing was started.</summary>
        public const int ExitReparsePoint = 5;

        /// <summary>Windows could not start acclient.exe (no client was created).</summary>
        public const int ExitCreateProcessFailed = 6;

        // Stderr tokens. One printed per failure; the success path also prints a token when the client
        // started but Decal injection did not (the client runs without Decal: fail-open).

        /// <summary>Wrong argument count.</summary>
        public const string TokenBadUsage = "bad-usage";

        /// <summary>The game folder is empty or does not exist.</summary>
        public const string TokenGameFolderMissing = "game-folder-missing";

        /// <summary>acclient.exe is not in the game folder.</summary>
        public const string TokenAcclientMissing = "acclient-missing";

        /// <summary>The Inject.dll argument is not an absolute path ending in Inject.dll.</summary>
        public const string TokenInjectPathInvalid = "inject-path-invalid";

        /// <summary>The Inject.dll file does not exist.</summary>
        public const string TokenInjectMissing = "inject-missing";

        /// <summary>A path component (acclient.exe or Inject.dll) is a reparse point.</summary>
        public const string TokenReparsePoint = "reparse-point";

        /// <summary>CreateProcess on acclient.exe failed.</summary>
        public const string TokenCreateProcessFailed = "create-process-failed";

        /// <summary>The client was created and resumed, but Decal injection failed (it runs without Decal).</summary>
        public const string TokenInjectFailed = "inject-failed";

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
