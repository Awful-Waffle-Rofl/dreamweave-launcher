using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.SelfInstall;

namespace DreamWeave.Launcher.Core.Update
{
    /// <summary>The outcome of a successful <see cref="SelfUpdater.ApplyAsync"/>.</summary>
    public sealed class SelfUpdateResult
    {
        internal SelfUpdateResult(SemVersion newVersion, string exePath)
        {
            NewVersion = newVersion;
            ExePath = exePath;
        }

        /// <summary>The version now in place (and already started).</summary>
        public SemVersion NewVersion { get; }

        /// <summary>The exe path that now holds the new version.</summary>
        public string ExePath { get; }
    }

    /// <summary>
    /// Replaces the running launcher with the version the signed manifest offers, then starts it.
    /// <list type="number">
    /// <item>Refuses unless <see cref="UpdateChecker"/> says an update is installable (never a downgrade).</item>
    /// <item>Downloads into <c>StagingDir\launcher-update\</c> (component id <see cref="ComponentId"/>); the
    /// download service verifies size and SHA-256 against the manifest.</item>
    /// <item>Opens the staged file with <see cref="FileShare.Read"/> and keeps it open: nobody can write,
    /// rename or delete it from here on. When the manifest names an <c>authenticodeSubject</c>, the
    /// signature is checked through that handle.</item>
    /// <item>Copies it through that handle to <c>&lt;exe&gt;.new</c>, hashing the bytes AS THEY ARE WRITTEN
    /// and comparing size and SHA-256 again; the <c>.new</c> handle allows only rename, so the
    /// verified bytes are exactly the ones moved into place.</item>
    /// <item>Swaps (<c>exe -&gt; exe.old</c>, <c>exe.new -&gt; exe</c>) and starts the new exe with
    /// <c>--updated</c>. Any failure, including failing to start the new exe, restores the original.</item>
    /// </list>
    /// After a successful return the caller must exit promptly; the new process deletes <c>.old</c>
    /// via <see cref="CompleteUpdate"/>.
    /// </summary>
    public sealed class SelfUpdater
    {
        /// <summary>The download component id, and so the staging folder name.</summary>
        public const string ComponentId = "launcher-update";

        private readonly AppPaths _paths;
        private readonly SemVersion _running;
        private readonly string _exePath;
        private readonly IDownloader _downloader;
        private readonly IAuthenticodeVerifier _authenticode;
        private readonly ISelfFileSystem _fs;
        private readonly IProcessLauncher _processes;

        /// <summary>Creates an updater.</summary>
        /// <param name="paths">Launcher folders (for the staging folder).</param>
        /// <param name="runningVersion">The running launcher's version.</param>
        /// <param name="currentExePath">Full path of the running exe (the file that gets replaced).</param>
        /// <param name="downloader">Normally <see cref="DownloadService"/>.</param>
        /// <param name="authenticode">Normally <see cref="WinTrustVerifier"/>.</param>
        /// <param name="fileSystem">Normally <see cref="SelfFileSystem"/>.</param>
        /// <param name="processes">Normally <see cref="ProcessLauncher"/>.</param>
        public SelfUpdater(AppPaths paths, SemVersion runningVersion, string currentExePath, IDownloader downloader, IAuthenticodeVerifier authenticode, ISelfFileSystem fileSystem, IProcessLauncher processes)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _running = runningVersion ?? throw new ArgumentNullException(nameof(runningVersion));
            if (string.IsNullOrEmpty(currentExePath))
            {
                throw new ArgumentException("The running exe path is required.", nameof(currentExePath));
            }

            _exePath = Path.GetFullPath(currentExePath);
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _authenticode = authenticode ?? throw new ArgumentNullException(nameof(authenticode));
            _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        }

        /// <summary>Downloads, verifies, swaps in and starts the manifest's launcher.</summary>
        /// <exception cref="LauncherException">
        /// <see cref="LauncherError.UpdateNotNewer"/> (downgrade or same version, nothing done),
        /// <see cref="LauncherError.ManifestInvalid"/> (no usable download), any <see cref="IDownloader"/> error,
        /// <see cref="LauncherError.SizeMismatch"/> / <see cref="LauncherError.HashMismatch"/> /
        /// <see cref="LauncherError.UpdateSignatureInvalid"/> (nothing swapped),
        /// <see cref="LauncherError.UpdateSwapFailed"/> (original restored),
        /// <see cref="LauncherError.UpdateRollbackFailed"/> (original could not be restored), or an I/O code.
        /// </exception>
        public async Task<SelfUpdateResult> ApplyAsync(LauncherManifest manifest, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            LauncherUpdateInfo info = manifest.Launcher;
            UpdateCheckResult check = UpdateChecker.Check(info, _running);
            if (check.Available == null || check.Available <= _running)
            {
                throw new LauncherException(LauncherError.UpdateNotNewer, "The offered launcher version (" + (check.Available?.ToString() ?? "none") + ") is not newer than this one (" + _running + "); it was not installed.");
            }

            if (!check.CanUpdate)
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "The manifest does not publish a usable launcher download.");
            }

            string staged = Path.Combine(DownloadRequest.ComponentFolder(_paths.StagingDir, ComponentId), SelfInstallLayout.ExeFileName);
            var request = new DownloadRequest(ComponentId, Transports.Http, new Uri(info.Url, UriKind.Absolute), info.Size, info.Sha256, staged);
            DownloadResult downloaded = await _downloader.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false);

            FileStream heldStaged = null;
            FileStream heldNew = null;
            string newPath = _exePath + SelfInstallLayout.NewSuffix;
            try
            {
                heldStaged = _fs.OpenReadLocked(downloaded.Path);
                if (heldStaged.Length != info.Size)
                {
                    throw new LauncherException(LauncherError.SizeMismatch, "The downloaded launcher is " + heldStaged.Length + " bytes, but " + info.Size + " bytes were expected.");
                }

                if (info.AuthenticodeSubject != null)
                {
                    _authenticode.Verify(downloaded.Path, heldStaged, info.AuthenticodeSubject);
                    heldStaged.Position = 0;
                }

                string written;
                heldNew = ExeSwap.WriteIncoming(_fs, heldStaged, _exePath, info.Size, progress, cancellationToken, ComponentId, out written);
                if (!Hasher.FixedTimeEquals(written, info.Sha256))
                {
                    throw new LauncherException(LauncherError.HashMismatch, "The launcher update failed its integrity check and was not installed.");
                }

                ExeSwap.Swap(_fs, _exePath);
            }
            catch (Exception ex)
            {
                if (ex is LauncherException le && le.Code == LauncherError.UpdateRollbackFailed)
                {
                    // Keep the incoming file: with the original stuck at .old it may be the only runnable copy.
                    heldNew?.Dispose();
                }
                else
                {
                    ExeSwap.Discard(_fs, heldNew, newPath);
                }

                heldNew = null;
                if (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw LauncherException.FromIo(ex, "installing the launcher update");
                }

                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw new LauncherException(LauncherError.Cancelled, "Cancelled.", ex);
                }

                throw;
            }
            finally
            {
                heldNew?.Dispose();
                heldStaged?.Dispose();
            }

            try
            {
                _processes.Start(new ProcessStartInfo(_exePath, SelfInstallLayout.UpdatedSwitch)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(_exePath),
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                ExeSwap.Revert(_fs, _exePath);
                throw new LauncherException(LauncherError.UpdateSwapFailed, "The new launcher could not be started (" + ex.Message + "); the previous one was restored.", ex);
            }

            TryDeleteStaged(downloaded.Path);
            return new SelfUpdateResult(check.Available, _exePath);
        }

        /// <summary>
        /// Run by the new process when started with <c>--updated</c>: deletes the previous exe
        /// (<c>&lt;exe&gt;.old</c>) and any stray <c>&lt;exe&gt;.new</c>, retrying while the old process finishes
        /// exiting. Returns true when no <c>.old</c> remains. Never throws for a file error.
        /// </summary>
        public static bool CompleteUpdate(string currentExePath, ISelfFileSystem fileSystem, TimeSpan wait)
        {
            if (fileSystem == null)
            {
                throw new ArgumentNullException(nameof(fileSystem));
            }

            string exe = Path.GetFullPath(currentExePath);
            string oldPath = exe + SelfInstallLayout.OldSuffix;
            TryDelete(fileSystem, exe + SelfInstallLayout.NewSuffix);
            Stopwatch clock = Stopwatch.StartNew();
            while (true)
            {
                if (TryDelete(fileSystem, oldPath))
                {
                    return true;
                }

                if (clock.Elapsed >= wait)
                {
                    return false;
                }

                Thread.Sleep(100);
            }
        }

        private static bool TryDelete(ISelfFileSystem fs, string path)
        {
            try
            {
                fs.DeleteFile(path);
                return !fs.FileExists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void TryDeleteStaged(string path)
        {
            TryDelete(_fs, path);
        }
    }
}
