using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>Arranges for the running exe (which Windows will not let a process delete) to be removed after it exits.</summary>
    public interface ISelfDeleteScheduler
    {
        /// <summary>
        /// Schedules deletion of <paramref name="exePath"/> once it is no longer in use, then removal
        /// of <paramref name="folder"/> if (and only if) it is then empty. Returns false, without
        /// scheduling anything, when the paths cannot be handed to the helper safely.
        /// </summary>
        bool Schedule(string exePath, string folder);
    }

    /// <summary>
    /// <see cref="ISelfDeleteScheduler"/> that writes a small ASCII batch script to %TEMP% and starts
    /// it in a hidden <c>cmd.exe</c>. The script retries <c>del</c> about once a second for up to
    /// <see cref="MaxAttempts"/> attempts (so it does not depend on guessing how long the launcher
    /// takes to exit), then runs a NON-recursive <c>rmdir</c> on the folder (which fails harmlessly
    /// if anything is left in it), then deletes itself.
    /// </summary>
    /// <remarks>
    /// Why this and not a temp copy of the exe: a copy in %TEMP% can delete the install folder, but
    /// it then cannot delete itself either, and the only built-in way to remove a file later
    /// (MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT) needs administrator rights, which this per-user
    /// launcher never has; it would leave a 1.5 MB exe behind on every uninstall. Why not a one-line
    /// <c>ping -n 3 &amp; del</c>: a fixed delay races a slow exit (antivirus scans, a busy disk) and
    /// silently leaves the exe behind. The paths travel as command-line ARGUMENTS (%~1, %~2), never as
    /// script text, so they stay UTF-16 end to end (a script file is read in the OEM code page, which
    /// would corrupt a non-ASCII user name) and the script itself is pure ASCII. A path containing
    /// '%' or '"' is refused, because cmd expands %NAME% on its own command line.
    /// </remarks>
    public sealed class SelfDeleteScheduler : ISelfDeleteScheduler
    {
        /// <summary>How many times the script tries to delete the exe (about one second apart).</summary>
        public const int MaxAttempts = 60;

        private readonly IProcessLauncher _processes;
        private readonly string _scriptFolder;

        /// <summary>Production constructor: scripts go to %TEMP%.</summary>
        public SelfDeleteScheduler(IProcessLauncher processes)
            : this(processes, Path.GetTempPath())
        {
        }

        /// <summary>Constructor for tests: scripts go to <paramref name="scriptFolder"/>.</summary>
        public SelfDeleteScheduler(IProcessLauncher processes, string scriptFolder)
        {
            _processes = processes ?? throw new ArgumentNullException(nameof(processes));
            _scriptFolder = scriptFolder ?? throw new ArgumentNullException(nameof(scriptFolder));
        }

        /// <summary>The batch script, CRLF line endings, ASCII only. Arguments: %1 = exe, %2 = folder.</summary>
        public static string ScriptText
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("@echo off\r\n");
                sb.Append("setlocal\r\n");
                sb.Append("set n=0\r\n");
                sb.Append(":retry\r\n");
                sb.Append("del /f /q \"%~1\" >nul 2>&1\r\n");
                sb.Append("if not exist \"%~1\" goto removed\r\n");
                sb.Append("set /a n+=1\r\n");
                sb.Append("if %n% geq " + MaxAttempts + " goto finish\r\n");
                sb.Append("ping -n 2 127.0.0.1 >nul\r\n");
                sb.Append("goto retry\r\n");
                sb.Append(":removed\r\n");
                sb.Append("rmdir \"%~2\" >nul 2>&1\r\n");
                sb.Append(":finish\r\n");
                sb.Append("(goto) 2>nul & del /f /q \"%~f0\"\r\n");
                return sb.ToString();
            }
        }

        /// <summary>True when the path can be passed to cmd.exe as a quoted argument without being altered.</summary>
        public static bool IsSafeArgument(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.IndexOf('%') < 0
                && path.IndexOf('"') < 0
                && path.IndexOf('\r') < 0
                && path.IndexOf('\n') < 0;
        }

        /// <summary>The start info used for a script at <paramref name="scriptPath"/>.</summary>
        public static ProcessStartInfo BuildStartInfo(string scriptPath, string exePath, string folder)
        {
            // /d: skip AutoRun; /s /c: strip exactly the outer quote pair, keeping the inner quoting.
            string args = "/d /s /c \"\"" + scriptPath + "\" \"" + exePath + "\" \"" + folder + "\"\"";
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
        }

        /// <inheritdoc />
        public bool Schedule(string exePath, string folder)
        {
            if (!IsSafeArgument(exePath) || !IsSafeArgument(folder))
            {
                return false;
            }

            string exe = Path.GetFullPath(exePath);
            string dir = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string script = Path.Combine(Path.GetFullPath(_scriptFolder), "dwl-selfdelete-" + Guid.NewGuid().ToString("N") + ".cmd");
            if (!IsSafeArgument(exe) || !IsSafeArgument(dir) || !IsSafeArgument(script))
            {
                return false;
            }

            try
            {
                File.WriteAllText(script, ScriptText, new ASCIIEncoding());
                _processes.Start(BuildStartInfo(script, exe, dir));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                try
                {
                    File.Delete(script);
                }
                catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException)
                {
                }

                return false;
            }
        }
    }
}
