using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>
    /// Runs a hash-verified installer (the AC client's InstallShield setup) from the staging folder.
    /// <para>
    /// The staging folder is user-writable and the installer normally runs ELEVATED, so the bytes
    /// that run must be the bytes that were verified. <see cref="Start"/> therefore opens the staged
    /// file with <see cref="FileShare.Read"/> (nobody may write, delete or rename it), hashes it
    /// THROUGH THAT HANDLE, and keeps the handle open until the process has been created. Once the
    /// process exists Windows itself refuses writes to its mapped image, so the handle is released.
    /// </para>
    /// <para>
    /// The installer's own exit is NOT the completion signal: an InstallShield self-extractor can
    /// exit early and leave a child process doing the work. <see cref="InstallerProcess.WaitForInstallAsync(Func{string}, TimeSpan, CancellationToken)"/>
    /// polls detection instead.
    /// </para>
    /// </summary>
    public sealed class InstallerRunner
    {
        /// <summary>Win32 ERROR_CANCELLED: the player answered No on the UAC prompt.</summary>
        public const int ErrorCancelled = 1223;

        private readonly IProcessStarter _starter;

        /// <summary>Creates a runner; <paramref name="starter"/> defaults to <see cref="ShellProcessStarter"/>.</summary>
        public InstallerRunner(IProcessStarter starter = null)
        {
            _starter = starter ?? new ShellProcessStarter();
        }

        /// <summary>
        /// Verifies <paramref name="stagedInstallerPath"/> against <paramref name="component"/>'s size
        /// and SHA-256 under a deny-write handle, then starts it (verb <c>runas</c> when the component
        /// asks for elevation) with <paramref name="stagingDir"/> as the working directory.
        /// </summary>
        /// <exception cref="LauncherException">
        /// SizeMismatch / HashMismatch (nothing is started); ElevationDeclined (UAC answered No);
        /// InstallerFailed (Windows could not start it, or it lives next to a DLL); ReparsePointInPath
        /// (the staged file is a link); ManifestInvalid (not an installer component); Cancelled.
        /// </exception>
        public InstallerProcess Start(ManifestComponent component, string stagedInstallerPath, string stagingDir, IProgress<ProgressReport> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (component == null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (component.Kind != ComponentKinds.Installer)
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + component.Id + "' is not an installer.");
            }

            if (!Hasher.IsSha256Hex(component.Sha256))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + component.Id + "' has no valid SHA-256.");
            }

            if (string.IsNullOrWhiteSpace(stagedInstallerPath) || string.IsNullOrWhiteSpace(stagingDir))
            {
                throw new ArgumentException("The staged installer and staging folder are required.");
            }

            string exe = Path.GetFullPath(stagedInstallerPath);
            string workDir = Path.GetFullPath(stagingDir);
            RefuseLink(exe);
            RefuseDllNeighbours(exe);

            FileStream held;
            try
            {
                // FileShare.Read: others may read (ShellExecute must), nobody may write, delete or rename.
                held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "opening the downloaded installer");
            }

            try
            {
                if (held.Length != component.Size)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "The downloaded installer is " + held.Length + " bytes; expected " + component.Size + ". It will be downloaded again.");
                }

                string hash;
                try
                {
                    hash = Hasher.ComputeSha256Hex(held, held.Length, progress, cancellationToken, "verify:" + component.Id);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }

                if (!Hasher.FixedTimeEquals(hash, component.Sha256))
                {
                    throw new LauncherException(LauncherError.HashMismatch, "The downloaded installer failed its integrity check, so it was not run. It will be downloaded again.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.");
                }

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = workDir,
                    Arguments = component.Args ?? string.Empty,
                };
                if (component.Elevate)
                {
                    psi.Verb = "runas";
                }

                IStartedProcess process;
                try
                {
                    process = _starter.Start(psi);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    throw new LauncherException(LauncherError.ElevationDeclined, "Administrator permission is needed to install Asheron's Call, and the request was declined.", ex);
                }
                catch (Win32Exception ex)
                {
                    throw new LauncherException(LauncherError.InstallerFailed, "Windows could not start the installer: " + ex.Message, ex);
                }

                return new InstallerProcess(component, process);
            }
            finally
            {
                // Held until here: the process (if any) now exists and maps the verified image.
                held.Dispose();
            }
        }

        private static void RefuseLink(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "opening the downloaded installer");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new LauncherException(LauncherError.ReparsePointInPath, "The downloaded installer is a link, not a file, so it was not run.");
            }
        }

        /// <summary>
        /// An elevated installer loads DLLs from its own folder first, and the staging folder is
        /// user-writable; refuse to elevate next to any DLL a third party could have planted.
        /// </summary>
        private static void RefuseDllNeighbours(string exe)
        {
            string dir = Path.GetDirectoryName(exe);
            string[] dlls;
            try
            {
                dlls = Directory.GetFiles(dir, "*.dll");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "checking the installer's folder");
            }

            if (dlls.Length > 0)
            {
                throw new LauncherException(LauncherError.InstallerFailed, "The installer's folder contains " + Path.GetFileName(dlls[0]) + ", which the installer could load with administrator rights. It was not run.");
            }
        }
    }

    /// <summary>A started installer. Dispose to release the process handle (the installer keeps running).</summary>
    public sealed class InstallerProcess : IDisposable
    {
        private readonly IStartedProcess _process;

        internal InstallerProcess(ManifestComponent component, IStartedProcess process)
        {
            Component = component;
            _process = process;
        }

        /// <summary>The installer component that was started.</summary>
        public ManifestComponent Component { get; }

        /// <summary>
        /// True once the process Windows started has exited. An InstallShield self-extractor may exit
        /// before the install finishes, so this is a hint for the UI ("the installer window closed"),
        /// never proof of completion. False when there is no process handle.
        /// </summary>
        public bool HasExited => _process != null && _process.HasExited;

        /// <summary>Exit code of the started process once it has exited, otherwise null.</summary>
        public int? ExitCode => _process?.ExitCode;

        /// <summary>
        /// Polls detection until the client is installed: each poll locates the game folder
        /// (registry entries appear when the installer finishes) and evaluates the installer
        /// component; returns the folder once it is Satisfied (acclient.exe present with an
        /// accepted hash).
        /// </summary>
        /// <param name="locator">Finds the game folder.</param>
        /// <param name="evaluator">Checks the component's satisfiedWhen rules.</param>
        /// <param name="manifest">The verified manifest the component belongs to.</param>
        /// <param name="savedFolder">Returns the folder saved in state (may return null); read on every poll.</param>
        /// <param name="pollInterval">Delay between polls.</param>
        /// <param name="cancellationToken">Stops waiting (the installer keeps running).</param>
        /// <exception cref="LauncherException">Cancelled; ManifestInvalid when a manifest path is unsafe.</exception>
        public Task<string> WaitForInstallAsync(AcInstallLocator locator, ComponentEvaluator evaluator, LauncherManifest manifest, Func<string> savedFolder, TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            if (locator == null)
            {
                throw new ArgumentNullException(nameof(locator));
            }

            if (evaluator == null)
            {
                throw new ArgumentNullException(nameof(evaluator));
            }

            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            return WaitForInstallAsync(
                () =>
                {
                    AcInstallLocation where = locator.LocateFrom(savedFolder?.Invoke());
                    if (!where.Found)
                    {
                        return null;
                    }

                    ComponentStatus status = evaluator.Evaluate(Component, manifest, where.Folder, null, cancellationToken);
                    return status.State == ComponentState.Satisfied ? where.Folder : null;
                },
                pollInterval,
                cancellationToken);
        }

        /// <summary>
        /// Polls <paramref name="detect"/> every <paramref name="pollInterval"/> until it returns a
        /// non-null folder, which is returned. I/O failures inside a poll count as "not yet".
        /// </summary>
        /// <exception cref="LauncherException">Cancelled; anything else <paramref name="detect"/> throws as a LauncherException.</exception>
        public static async Task<string> WaitForInstallAsync(Func<string> detect, TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            if (detect == null)
            {
                throw new ArgumentNullException(nameof(detect));
            }

            if (pollInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pollInterval));
            }

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Stopped waiting for the installer.");
                }

                string folder;
                try
                {
                    folder = detect();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    folder = null;
                }

                if (folder != null)
                {
                    return folder;
                }

                try
                {
                    await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Stopped waiting for the installer.", ex);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _process?.Dispose();
        }
    }
}
