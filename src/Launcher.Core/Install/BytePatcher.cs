using System;
using System.IO;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;

namespace DreamWeave.Launcher.Core.Install
{
    /// <summary>
    /// Applies or reverts a byte-patch component (the optional palette-leak fix). It only executes:
    /// whether a patch should be applied is the caller's decision (the component is default-off).
    /// <para>
    /// The target is guarded for the whole operation (opened with <see cref="FileShare.Delete"/>
    /// only, see <see cref="AcWriter"/>), so nothing else can write it between the hash check and
    /// the replace, and a running client is reported as GameRunning. The precondition hash is
    /// computed from the bytes read through that guard, never from the verify cache. The patched
    /// bytes are built in memory and must hash to the expected result; they are written to
    /// <c>&lt;target&gt;.dwtmp</c>, that file is hashed again from disk, and only then does it
    /// atomically replace the target.
    /// </para>
    /// </summary>
    public sealed class BytePatcher
    {
        /// <summary>Largest target this patcher reads into memory (acclient.exe is under 5 MB).</summary>
        public const long MaxTargetBytes = 256L * 1024 * 1024;

        private readonly AppPaths _paths;
        private readonly Downloads.IDiskSpace _disk;

        /// <summary>
        /// Creates a patcher. <paramref name="paths"/> locates the elevated task file when the game
        /// folder is not writable; pass null where no hand-off is possible (the elevated helper
        /// itself), and an unwritable folder is then AccessDenied.
        /// </summary>
        /// <param name="paths">Launcher paths, or null for no hand-off.</param>
        /// <param name="diskSpace">Free-space query; <see cref="Downloads.SystemDiskSpace"/> when null.</param>
        public BytePatcher(AppPaths paths, Downloads.IDiskSpace diskSpace = null)
        {
            _paths = paths;
            _disk = diskSpace ?? new Downloads.SystemDiskSpace();
        }

        /// <summary>Applies the patch: target must hash to <c>inputSha256</c>; result hashes to <c>outputSha256</c>.</summary>
        /// <exception cref="LauncherException">PatchInputMismatch, PatchOutputMismatch, GameRunning, ReparsePointInPath, ManifestInvalid, AccessDenied, DiskFull.</exception>
        public BytePatchResult Apply(ManifestComponent component, string acFolder)
        {
            return Run(component, acFolder, true);
        }

        /// <summary>Reverts the patch: target must hash to <c>outputSha256</c>; the <c>expect</c> bytes are written back and the result hashes to <c>inputSha256</c>.</summary>
        /// <exception cref="LauncherException">As <see cref="Apply"/>.</exception>
        public BytePatchResult Revert(ManifestComponent component, string acFolder)
        {
            return Run(component, acFolder, false);
        }

        private BytePatchResult Run(ManifestComponent c, string acFolder, bool forward)
        {
            if (c == null)
            {
                throw new ArgumentNullException(nameof(c));
            }

            if (c.Kind != ComponentKinds.BytePatch || c.Patches == null || c.Patches.Count == 0
                || !Hasher.IsSha256Hex(c.InputSha256) || !Hasher.IsSha256Hex(c.OutputSha256))
            {
                throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + c.Id + "' is not a usable byte patch.");
            }

            ZipOverlayInstaller.RequireFolder(acFolder);
            string fromHash = forward ? c.InputSha256 : c.OutputSha256;
            string toHash = forward ? c.OutputSha256 : c.InputSha256;
            string what = c.Target;

            string full = AcPathSafety.ResolveForWrite(acFolder, c.Target);
            FileStream guard;
            if (!AcWriter.TryOpenGuard(full, what, out guard))
            {
                return NeedsElevation(c, acFolder, forward);
            }

            if (guard == null)
            {
                throw new LauncherException(LauncherError.PatchInputMismatch, what + " is not installed, so the patch cannot be " + (forward ? "applied" : "removed") + ".");
            }

            using (guard)
            {
                if (guard.Length > MaxTargetBytes)
                {
                    throw new LauncherException(LauncherError.PatchInputMismatch, what + " is not the file this patch was made for.");
                }

                byte[] data = new byte[guard.Length];
                try
                {
                    int done = 0;
                    while (done < data.Length)
                    {
                        int read = guard.Read(data, done, data.Length - done);
                        if (read <= 0)
                        {
                            throw new LauncherException(LauncherError.PatchInputMismatch, what + " changed while it was being read.");
                        }

                        done += read;
                    }
                }
                catch (IOException ex)
                {
                    throw LauncherException.FromIo(ex, "reading " + what);
                }

                string current = Hasher.ComputeSha256Hex(data);
                if (Hasher.FixedTimeEquals(current, toHash))
                {
                    return new BytePatchResult(InstallOutcome.NothingToDo, null, null);
                }

                if (!Hasher.FixedTimeEquals(current, fromHash))
                {
                    throw new LauncherException(LauncherError.PatchInputMismatch, what + " is not the file this patch was made for, so it was left unchanged.");
                }

                foreach (BytePatch p in c.Patches)
                {
                    byte[] before = forward ? p.GetExpectBytes() : p.GetReplaceBytes();
                    byte[] after = forward ? p.GetReplaceBytes() : p.GetExpectBytes();
                    if (before.Length == 0 || before.Length != after.Length)
                    {
                        throw new LauncherException(LauncherError.ManifestInvalid, "Component '" + c.Id + "' has a patch whose expect/replace differ in length.");
                    }

                    if (p.Offset < 0 || p.Offset > data.Length - before.Length)
                    {
                        throw new LauncherException(LauncherError.PatchInputMismatch, what + " is too short for this patch, so it was left unchanged.");
                    }

                    for (int i = 0; i < before.Length; i++)
                    {
                        if (data[p.Offset + i] != before[i])
                        {
                            throw new LauncherException(LauncherError.PatchInputMismatch, what + " does not have the expected bytes at offset " + p.Offset + ", so it was left unchanged.");
                        }
                    }

                    Buffer.BlockCopy(after, 0, data, (int)p.Offset, after.Length);
                }

                if (!Hasher.FixedTimeEquals(Hasher.ComputeSha256Hex(data), toHash))
                {
                    throw new LauncherException(LauncherError.PatchOutputMismatch, "The patched " + what + " would not match the published result, so it was left unchanged.");
                }

                if (!AcWriter.CanCreateIn(Path.GetDirectoryName(full)))
                {
                    guard.Dispose();
                    return NeedsElevation(c, acFolder, forward);
                }

                AcWriter.RequireFreeSpace(_disk, Path.GetDirectoryName(full), data.Length, "in the game folder");

                string tmp = AcWriter.WriteTemp(acFolder, c.Target, data);
                try
                {
                    string written;
                    try
                    {
                        written = Hasher.ComputeFileSha256Hex(tmp);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw LauncherException.FromIo(ex, "checking the patched " + what);
                    }

                    if (!Hasher.FixedTimeEquals(written, toHash))
                    {
                        throw new LauncherException(LauncherError.PatchOutputMismatch, "The patched " + what + " did not read back correctly, so it was left unchanged.");
                    }

                    try
                    {
                        AcWriter.Commit(acFolder, c.Target, c.Target + AcWriter.TempSuffix);
                    }
                    catch (IOException ex) when (AcWriter.IsInUse(ex))
                    {
                        throw AcWriter.GameRunning(what, ex);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw LauncherException.FromIo(ex, "replacing " + what);
                    }
                }
                catch
                {
                    AcWriter.TryDelete(tmp);
                    throw;
                }

                return new BytePatchResult(InstallOutcome.Completed, null, null);
            }
        }

        private BytePatchResult NeedsElevation(ManifestComponent c, string acFolder, bool forward)
        {
            if (_paths == null)
            {
                throw new LauncherException(LauncherError.AccessDenied, "Windows denied access to " + c.Target + ".");
            }

            var task = new ElevatedTask { AppRoot = _paths.Root, AcFolder = acFolder };
            task.Items.Add(new ElevatedTaskItem { ComponentId = c.Id, Operation = forward ? ElevatedOperations.PatchApply : ElevatedOperations.PatchRevert });
            return new BytePatchResult(InstallOutcome.ElevationRequired, task, task.Save(_paths));
        }
    }
}
