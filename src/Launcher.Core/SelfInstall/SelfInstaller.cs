using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace DreamWeave.Launcher.Core.SelfInstall
{
    /// <summary>The outcome of <see cref="SelfInstaller.Install"/>.</summary>
    public sealed class SelfInstallResult
    {
        internal SelfInstallResult(string exePath, bool copied, bool replacedExisting, string leftoverOldPath)
        {
            ExePath = exePath;
            Copied = copied;
            ReplacedExisting = replacedExisting;
            LeftoverOldPath = leftoverOldPath;
        }

        /// <summary>Full path of the installed exe.</summary>
        public string ExePath { get; }

        /// <summary>False when the running exe already WAS the installed exe (registration was refreshed only).</summary>
        public bool Copied { get; }

        /// <summary>True when an earlier installed exe was replaced (upgrade or re-install).</summary>
        public bool ReplacedExisting { get; }

        /// <summary>
        /// The previous exe, renamed to <c>.old</c>, when it could not be deleted because a launcher is
        /// still running from it; null otherwise. A later install, update or uninstall removes it.
        /// </summary>
        public string LeftoverOldPath { get; }
    }

    /// <summary>
    /// Installs the running launcher for the current user: copies the exe into
    /// <see cref="AppPaths.InstallDir"/>, creates a Start-menu shortcut, and registers an HKCU
    /// uninstall entry so it appears in Apps and Features. Needs no administrator rights. Running it
    /// again is safe: it re-copies (upgrade or repair), overwrites the shortcut and rewrites the key.
    /// </summary>
    /// <remarks>
    /// The exe is copied as a BYTE STREAM, not with <see cref="File.Copy(string, string)"/>: File.Copy
    /// (CopyFile) carries NTFS alternate data streams, including the Zone.Identifier "Mark of the Web"
    /// a browser attaches to a download, so the installed copy would keep triggering SmartScreen and
    /// "file came from another computer" prompts. A stream copy writes only the main data stream.
    /// </remarks>
    public sealed class SelfInstaller
    {
        private readonly SelfInstallEnvironment _env;
        private readonly string _version;

        /// <summary>Creates an installer.</summary>
        /// <param name="environment">Folders, registry, shortcut and file-system access.</param>
        /// <param name="version">The running launcher's version (written as DisplayVersion).</param>
        public SelfInstaller(SelfInstallEnvironment environment, string version)
        {
            _env = environment ?? throw new ArgumentNullException(nameof(environment));
            _env.Validate();
            _version = version ?? string.Empty;
        }

        /// <summary>Installs (or re-installs) from the exe at <paramref name="currentExePath"/>.</summary>
        /// <exception cref="LauncherException">
        /// AccessDenied / DiskFull / Unknown on file errors, SizeMismatch if the copy came out short,
        /// UpdateSwapFailed / UpdateRollbackFailed when replacing an installed copy fails,
        /// ShortcutFailed, RegistryWriteFailed.
        /// </exception>
        public SelfInstallResult Install(string currentExePath)
        {
            if (string.IsNullOrEmpty(currentExePath))
            {
                throw new ArgumentException("The running exe path is required.", nameof(currentExePath));
            }

            ISelfFileSystem fs = _env.FileSystem;
            string source = Path.GetFullPath(currentExePath);
            string target = _env.InstalledExePath;
            if (!fs.FileExists(source))
            {
                throw new LauncherException(LauncherError.Unknown, "The launcher file " + source + " could not be found.");
            }

            bool copied = false;
            bool replaced = false;
            string leftover = null;
            if (!SelfInstallLayout.SamePath(source, target))
            {
                replaced = fs.FileExists(target);
                leftover = CopyIntoPlace(fs, source, target);
                copied = true;
            }

            long length;
            using (FileStream s = fs.OpenReadLocked(target))
            {
                length = s.Length;
            }

            _env.Shortcuts.Write(_env.ShortcutPath, target, _env.Paths.InstallDir, LauncherInfo.ProductName);
            _env.UserRegistry.SetValues(_env.UninstallKeyPath, BuildUninstallValues(target, length));
            return new SelfInstallResult(target, copied, replaced, leftover);
        }

        /// <summary>The values written to the uninstall key.</summary>
        internal IReadOnlyDictionary<string, object> BuildUninstallValues(string exePath, long exeLength)
        {
            int sizeKb = (int)Math.Min(int.MaxValue, (exeLength + 1023) / 1024);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "DisplayName", LauncherInfo.ProductName },
                { "DisplayVersion", _version },
                { "Publisher", SelfInstallLayout.Publisher },
                { "InstallLocation", _env.Paths.InstallDir },
                { "UninstallString", "\"" + exePath + "\" " + SelfInstallLayout.UninstallSwitch },
                { "DisplayIcon", exePath + ",0" },
                { "NoModify", 1 },
                { "NoRepair", 1 },
                { "EstimatedSize", sizeKb },
                { "InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            };
        }

        private static string CopyIntoPlace(ISelfFileSystem fs, string source, string target)
        {
            FileStream held = null;
            try
            {
                fs.CreateDirectory(Path.GetDirectoryName(target));
                using (FileStream input = fs.OpenReadLocked(source))
                {
                    string ignoredHash;
                    held = ExeSwap.WriteIncoming(fs, input, target, input.Length, null, CancellationToken.None, "self-install", out ignoredHash);
                }

                string oldPath;
                try
                {
                    oldPath = ExeSwap.Swap(fs, target);
                }
                catch (LauncherException ex)
                {
                    if (ex.Code == LauncherError.UpdateRollbackFailed)
                    {
                        // Keep the incoming file: with the original stuck at .old it may be the only runnable copy.
                        held.Dispose();
                    }
                    else
                    {
                        ExeSwap.Discard(fs, held, target + SelfInstallLayout.NewSuffix);
                    }

                    held = null;
                    throw;
                }

                held.Dispose();
                held = null;
                if (oldPath == null)
                {
                    return null;
                }

                try
                {
                    fs.DeleteFile(oldPath);
                    return null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // An installed launcher is still running from it; it goes on the next install/update/uninstall.
                    return oldPath;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                ExeSwap.Discard(fs, held, target + SelfInstallLayout.NewSuffix);
                throw LauncherException.FromIo(ex, "copying the launcher into " + Path.GetDirectoryName(target));
            }
        }
    }
}
