using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DreamWeave.Launcher.Core.Detection;

namespace DreamWeave.Launcher.Core.Launch
{
    /// <summary>Starts a process; the seam that lets tests avoid launching the real client.</summary>
    public interface IProcessStarter
    {
        /// <summary>
        /// Starts the process and returns it with its CreateProcess handle (the caller disposes it).
        /// May throw <see cref="Win32Exception"/>.
        /// </summary>
        StartedClient Start(ProcessStartInfo startInfo);
    }

    /// <summary><see cref="IProcessStarter"/> over <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
    public sealed class SystemProcessStarter : IProcessStarter
    {
        /// <inheritdoc />
        public StartedClient Start(ProcessStartInfo startInfo)
        {
            Process p = Process.Start(startInfo);
            if (p == null)
            {
                throw new Win32Exception(0, "The process did not start.");
            }

            try
            {
                // The handle CreateProcess returned (full access to the child), kept open by p until the
                // StartedClient is disposed, so the id can never be reused under it.
                return new StartedClient(p.Id, p.Handle, p);
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Starts acclient.exe. Never kills, signals or otherwise touches acclient processes it did not start, and
    /// never requests elevation. The argument string contains the account password in clear text and
    /// is therefore readable by other processes of the same Windows user; see Docs/launching.md.
    /// </summary>
    public sealed class GameLauncher
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const int ErrorElevationRequired = 740;

        private readonly IProcessStarter starter;
        private readonly IAcFileSystem fileSystem;

        /// <summary>Creates a launcher over the real file system and process table.</summary>
        public GameLauncher()
            : this(new SystemProcessStarter(), new RealAcFileSystem())
        {
        }

        /// <summary>Creates a launcher over the given seams (tests).</summary>
        public GameLauncher(IProcessStarter starter, IAcFileSystem fileSystem)
        {
            this.starter = starter ?? throw new ArgumentNullException(nameof(starter));
            this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        /// <summary>
        /// Starts <c>acclient.exe</c> from <paramref name="acFolder"/> with <paramref name="args"/>
        /// and the folder as working directory. Returns the started client (the caller disposes it).
        /// </summary>
        /// <exception cref="LauncherException">ClientNotFound, AccessDenied, ClientRequiresElevation or LaunchFailed.</exception>
        public StartedClient Launch(string acFolder, string args)
        {
            string exe = AcPathSafety.Resolve(acFolder, AcInstallLocator.ClientExeName);
            if (!fileSystem.FileExists(exe))
            {
                throw new LauncherException(LauncherError.ClientNotFound, "acclient.exe was not found in the game folder.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
            };

            try
            {
                return starter.Start(psi);
            }
            catch (Win32Exception ex)
            {
                // Deliberately omits the arguments: they contain the password.
                switch (ex.NativeErrorCode)
                {
                    case ErrorFileNotFound:
                    case ErrorPathNotFound:
                        throw new LauncherException(LauncherError.ClientNotFound, "Windows could not find acclient.exe to start it.", ex);
                    case ErrorAccessDenied:
                        throw new LauncherException(LauncherError.AccessDenied, "Windows denied permission to start acclient.exe.", ex);
                    case ErrorElevationRequired:
                        throw new LauncherException(LauncherError.ClientRequiresElevation, "acclient.exe needs to run as administrator. Turn off 'Run this program as an administrator' in its Properties > Compatibility tab, then try again.", ex);
                    default:
                        throw new LauncherException(LauncherError.LaunchFailed, "Windows could not start acclient.exe (error " + ex.NativeErrorCode + ").", ex);
                }
            }
        }
    }
}
