using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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
        /// <exception cref="LauncherException">DecalNotFound, ClientNotFound, ReparsePointInPath, ClientRequiresElevation, AccessDenied,
        /// LaunchFailed (including a helper that did not finish in time) or DecalHelperFailed. A client that started without Decal is
        /// not an exception: it is reported through <see cref="DecalStartResult.InjectionFailed"/>.</exception>
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
        private readonly int _backstopMs;

        /// <summary>How long the launcher waits for the helper before giving up, in milliseconds.</summary>
        internal const int DefaultBackstopMs = 30000;

        /// <summary>Extra time the outer guard gives a helper-runner to honour its own backstop before the launcher stops waiting.</summary>
        private const int GraceMs = 1000;

        /// <summary>Creates a starter that runs the real helper. <paramref name="injectDllPath"/> is the detected Decal Inject.dll.</summary>
        public DecalGameStarter(string helperExePath, string injectDllPath, Action<string> log = null)
            : this(helperExePath, injectDllPath, log, null)
        {
        }

        /// <summary>Creates a starter over the given helper-runner seam (tests).</summary>
        internal DecalGameStarter(string helperExePath, string injectDllPath, Action<string> log, Func<string, string, string, HelperOutput> runHelper, int backstopMs = DefaultBackstopMs)
        {
            _backstopMs = backstopMs > 0 ? backstopMs : DefaultBackstopMs;
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

            HelperOutput output = RunWithBackstop(acFolder, args ?? string.Empty);

            return Interpret(output);
        }

        /// <summary>
        /// Runs the helper-runner on its own task and waits at most the backstop (plus a short grace so a
        /// runner that honours the backstop itself reports its own timeout first). A helper that does not
        /// finish is NOT killed: the launcher only stops waiting and reports LaunchFailed.
        /// </summary>
        private HelperOutput RunWithBackstop(string acFolder, string args)
        {
            Task<HelperOutput> run = Task.Run(() => _runHelper(acFolder, _injectDllPath, args));
            try
            {
                if (!run.Wait(_backstopMs + GraceMs))
                {
                    return Timeout();
                }

                return run.Result;
            }
            catch (AggregateException agg)
            {
                Exception ex = agg.InnerException ?? agg;
                if (ex is TimeoutException)
                {
                    return Timeout();
                }

                if (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is InvalidOperationException)
                {
                    // The arguments are never logged: they carry the password.
                    Note("decal helper did not run type=" + ex.GetType().Name);
                    throw new LauncherException(LauncherError.DecalHelperFailed, "The Decal helper could not be run.", ex);
                }

                throw;
            }
        }

        private HelperOutput Timeout()
        {
            Note("decal helper did not finish within " + _backstopMs + " ms");
            throw new LauncherException(LauncherError.LaunchFailed, "The Decal helper did not finish in time.");
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
                case DecalLaunchResult.TokenInjectNotRegistered:
                    return LauncherError.DecalNotFound;
                case DecalLaunchResult.TokenAcclientReparsePoint:
                case DecalLaunchResult.TokenInjectReparsePoint:
                    return LauncherError.ReparsePointInPath;
                case DecalLaunchResult.TokenCreateProcessFailed:
                    return LauncherError.LaunchFailed;
                case DecalLaunchResult.TokenElevationRequired:
                    return LauncherError.ClientRequiresElevation;
                case DecalLaunchResult.TokenAccessDenied:
                    return LauncherError.AccessDenied;
                default:
                    // Bad usage, an unknown token, or no token at all: the helper did not start a client.
                    return LauncherError.DecalHelperFailed;
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
                case DecalLaunchResult.TokenInjectNotRegistered:
                    return "The player's Decal Inject.dll was not found.";
                case DecalLaunchResult.TokenAcclientReparsePoint:
                case DecalLaunchResult.TokenInjectReparsePoint:
                    return "A path to the client or to Decal passes through a junction or link.";
                case DecalLaunchResult.TokenCreateProcessFailed:
                    return "Windows could not start acclient.exe.";
                case DecalLaunchResult.TokenElevationRequired:
                    return "acclient.exe needs to run as administrator.";
                case DecalLaunchResult.TokenAccessDenied:
                    return "Windows denied permission to start acclient.exe.";
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

                // The password goes on stdin only. The helper may exit before reading it; that write
                // failure is ignored so its real token and exit code are still read below.
                WriteStdin(process.StandardInput, args);

                // Both streams are read concurrently so a full pipe on one cannot stall the other.
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                Task outTask = Pump(process.StandardOutput, stdout);
                Task errTask = Pump(process.StandardError, stderr);

                if (!process.WaitForExit(_backstopMs))
                {
                    // Not killed: the launcher only stops waiting.
                    throw new TimeoutException();
                }

                // The streams normally end with the process; bound the wait in case a child holds a pipe open.
                Task.WaitAll(new[] { outTask, errTask }, 5000);
                return new HelperOutput(process.ExitCode, Snapshot(stdout), Snapshot(stderr));
            }
        }

        /// <summary>Writes the argument string to the helper's stdin and closes it, ignoring a broken pipe from a helper that already exited.</summary>
        internal static void WriteStdin(TextWriter stdin, string args)
        {
            try
            {
                using (stdin)
                {
                    stdin.Write(args);
                }
            }
            catch (IOException)
            {
                // The helper exited before reading: its token and exit code are what matter.
            }
        }

        private static Task Pump(TextReader reader, StringBuilder into)
        {
            return Task.Run(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lock (into)
                        {
                            into.Append(line).Append('\n');
                        }
                    }
                }
                catch (IOException)
                {
                    // The pipe closed under us: keep what was read.
                }
            });
        }

        private static string Snapshot(StringBuilder sb)
        {
            lock (sb)
            {
                return sb.ToString();
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
