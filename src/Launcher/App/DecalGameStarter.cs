using System;
using System.Diagnostics;
using System.IO;
using DreamWeave.DecalLaunch;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Launch;

namespace DreamWeave.Launcher.App
{
    /// <summary>The outcome of a Decal start: the started client, and whether Decal actually loaded.</summary>
    internal sealed class DecalStartResult
    {
        public DecalStartResult(StartedClient client, bool injectionFailed)
        {
            Client = client;
            InjectionFailed = injectionFailed;
        }

        /// <summary>The started client (the caller disposes it). Never null on return.</summary>
        public StartedClient Client { get; }

        /// <summary>True when the client started but Decal could not be loaded (it is running without Decal).</summary>
        public bool InjectionFailed { get; }
    }

    /// <summary>
    /// Starts acclient.exe with the player's Decal through the isolated helper
    /// (<c>DreamWeaveDecalLaunch.exe</c>). This is the only place a process is looked up by id, and that
    /// lookup is never reachable from <see cref="ClientLockReleaser"/> or the client-handle API. A Decal
    /// start never releases the single-instance lock: once Decal's own startup has run, Inject.dll owns
    /// the single-instance check.
    /// </summary>
    internal interface IDecalGameStarter
    {
        /// <summary>True when the player's Decal was detected and the helper is installed beside the launcher.</summary>
        bool Available { get; }

        /// <summary>
        /// Starts acclient.exe from <paramref name="acFolder"/> with <paramref name="args"/> and the
        /// player's Decal. The argument string carries the password and is passed to the helper on
        /// stdin, never on its command line.
        /// </summary>
        /// <exception cref="LauncherException">DecalNotFound, ClientNotFound, ReparsePointInPath, LaunchFailed or DecalInjectFailed.</exception>
        DecalStartResult Start(string acFolder, string args);
    }

    /// <summary>What the helper reported: its exit code and the two captured streams.</summary>
    internal sealed class HelperOutput
    {
        public HelperOutput(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout ?? string.Empty;
            Stderr = stderr ?? string.Empty;
        }

        public int ExitCode { get; }

        public string Stdout { get; }

        public string Stderr { get; }
    }

    /// <summary><see cref="IDecalGameStarter"/> over the real helper process.</summary>
    internal sealed class DecalGameStarter : IDecalGameStarter
    {
        private readonly string _helperExePath;
        private readonly string _injectDllPath;
        private readonly Action<string> _log;
        private readonly Func<string, string, string, HelperOutput> _runHelper;

        /// <summary>Creates a starter that runs the real helper. <paramref name="injectDllPath"/> is the detected Decal Inject.dll.</summary>
        public DecalGameStarter(string helperExePath, string injectDllPath, Action<string> log = null)
            : this(helperExePath, injectDllPath, log, null)
        {
        }

        /// <summary>Creates a starter over the given helper-runner seam (tests).</summary>
        internal DecalGameStarter(string helperExePath, string injectDllPath, Action<string> log, Func<string, string, string, HelperOutput> runHelper)
        {
            _helperExePath = helperExePath;
            _injectDllPath = injectDllPath;
            _log = log ?? (line => { });
            _runHelper = runHelper ?? RunRealHelper;
        }

        /// <inheritdoc />
        public bool Available
        {
            get
            {
                try
                {
                    return !string.IsNullOrEmpty(_helperExePath)
                        && !string.IsNullOrEmpty(_injectDllPath)
                        && File.Exists(_helperExePath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <inheritdoc />
        public DecalStartResult Start(string acFolder, string args)
        {
            if (string.IsNullOrWhiteSpace(acFolder))
            {
                throw new LauncherException(LauncherError.ClientNotFound, "No game folder.");
            }

            HelperOutput output;
            try
            {
                output = _runHelper(acFolder, _injectDllPath, args ?? string.Empty);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is InvalidOperationException)
            {
                // The arguments are never logged: they carry the password.
                Note("decal helper did not run type=" + ex.GetType().Name);
                throw new LauncherException(LauncherError.DecalInjectFailed, "The Decal helper could not be started.", ex);
            }

            return Interpret(output);
        }

        /// <summary>
        /// Maps the helper's exit code and streams to a result or a <see cref="LauncherException"/>.
        /// Pure: no process is started. On a successful start the process is looked up by id here only.
        /// </summary>
        internal DecalStartResult Interpret(HelperOutput output)
        {
            if (output.ExitCode == DecalLaunchResult.ExitSuccess)
            {
                int pid = DecalLaunchResult.ParsePid(FirstLine(output.Stdout));
                if (pid <= 0)
                {
                    Note("decal helper reported success without a pid");
                    throw new LauncherException(LauncherError.LaunchFailed, "The Decal helper did not report a started client.");
                }

                bool injectionFailed = ContainsToken(output.Stderr, DecalLaunchResult.TokenInjectFailed);
                Note(injectionFailed
                    ? "decal started pid=" + pid + " but Decal did not load"
                    : "decal started pid=" + pid);

                Process process;
                try
                {
                    process = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    // The client exited between its start and this lookup: nothing to own, but it did start.
                    return new DecalStartResult(new StartedClient(pid, IntPtr.Zero), injectionFailed);
                }

                return new DecalStartResult(new StartedClient(pid, IntPtr.Zero, process), injectionFailed);
            }

            string token = FirstLine(output.Stderr).Trim();
            Note("decal helper failed exit=" + output.ExitCode + " token=" + Sanitize(token));
            throw new LauncherException(TokenToError(token), MessageFor(token));
        }

        private static LauncherError TokenToError(string token)
        {
            switch (token)
            {
                case DecalLaunchResult.TokenGameFolderMissing:
                case DecalLaunchResult.TokenAcclientMissing:
                    return LauncherError.ClientNotFound;
                case DecalLaunchResult.TokenInjectPathInvalid:
                case DecalLaunchResult.TokenInjectMissing:
                    return LauncherError.DecalNotFound;
                case DecalLaunchResult.TokenReparsePoint:
                    return LauncherError.ReparsePointInPath;
                case DecalLaunchResult.TokenCreateProcessFailed:
                    return LauncherError.LaunchFailed;
                default:
                    return LauncherError.DecalInjectFailed;
            }
        }

        private static string MessageFor(string token)
        {
            switch (token)
            {
                case DecalLaunchResult.TokenGameFolderMissing:
                case DecalLaunchResult.TokenAcclientMissing:
                    return "acclient.exe was not found in the game folder.";
                case DecalLaunchResult.TokenInjectPathInvalid:
                case DecalLaunchResult.TokenInjectMissing:
                    return "The player's Decal Inject.dll was not found.";
                case DecalLaunchResult.TokenReparsePoint:
                    return "A path to the client or to Decal passes through a junction or link.";
                case DecalLaunchResult.TokenCreateProcessFailed:
                    return "Windows could not start acclient.exe.";
                default:
                    return "The Decal helper could not start the client.";
            }
        }

        private HelperOutput RunRealHelper(string acFolder, string injectDllPath, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _helperExePath,
                Arguments = QuoteArg(acFolder) + " " + QuoteArg(injectDllPath),
                WorkingDirectory = Path.GetDirectoryName(_helperExePath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();

                // The password goes on stdin only.
                using (StreamWriter stdin = process.StandardInput)
                {
                    stdin.Write(args);
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new HelperOutput(process.ExitCode, stdout, stderr);
            }
        }

        /// <summary>
        /// Wraps <paramref name="arg"/> for a Windows command line (CommandLineToArgvW rules): backslashes
        /// before the closing quote are doubled so a path ending in a backslash does not escape the quote.
        /// </summary>
        internal static string QuoteArg(string arg)
        {
            arg = arg ?? string.Empty;
            var sb = new System.Text.StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', (backslashes * 2) + 1);
                    backslashes = 0;
                }
                else if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? text : text.Substring(0, nl);
        }

        private static bool ContainsToken(string stderr, string token)
        {
            if (string.IsNullOrEmpty(stderr))
            {
                return false;
            }

            foreach (string line in stderr.Split('\n'))
            {
                if (line.Trim() == token)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Sanitize(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return "none";
            }

            var sb = new System.Text.StringBuilder();
            foreach (char c in token)
            {
                sb.Append(c >= 'a' && c <= 'z' || c == '-' ? c : '?');
                if (sb.Length >= 32)
                {
                    break;
                }
            }

            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private void Note(string text)
        {
            try
            {
                _log(text);
            }
            catch (Exception)
            {
                // Logging never fails a launch.
            }
        }
    }
}
