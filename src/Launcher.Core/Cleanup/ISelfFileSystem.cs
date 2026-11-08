using System;
using System.Collections.Generic;
using System.IO;

namespace DreamWeave.Launcher.Core.Cleanup
{
    /// <summary>
    /// The file operations cleanup performs, behind an interface so
    /// tests can inject a failure at any step. Production implementation: <see cref="SelfFileSystem"/>.
    /// Methods throw the raw <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>;
    /// callers classify them.
    /// </summary>
    public interface ISelfFileSystem
    {
        /// <summary>True when a file exists at <paramref name="path"/>.</summary>
        bool FileExists(string path);

        /// <summary>True when a folder exists at <paramref name="path"/>.</summary>
        bool DirectoryExists(string path);

        /// <summary>Deletes a file (clearing a read-only attribute first). No-op when it is absent.</summary>
        void DeleteFile(string path);

        /// <summary>
        /// Deletes a folder tree WITHOUT following reparse points: a junction or symbolic link found
        /// anywhere in the tree (including <paramref name="folder"/> itself) is removed as a link and
        /// its target is never entered. <paramref name="preserveFile"/>, when non-null and inside the
        /// tree, is left in place along with the folders that contain it. Failures are appended to
        /// <paramref name="failures"/> and the walk continues. No-op when the folder is absent.
        /// </summary>
        void DeleteTree(string folder, string preserveFile, ICollection<string> failures);
    }

    /// <summary><see cref="ISelfFileSystem"/> over the real file system.</summary>
    public sealed class SelfFileSystem : ISelfFileSystem
    {
        /// <inheritdoc />
        public bool FileExists(string path) => File.Exists(path);

        /// <inheritdoc />
        public bool DirectoryExists(string path) => Directory.Exists(path);

        /// <inheritdoc />
        public void DeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }

        /// <inheritdoc />
        public void DeleteTree(string folder, string preserveFile, ICollection<string> failures)
        {
            if (failures == null)
            {
                throw new ArgumentNullException(nameof(failures));
            }

            FileAttributes attrs;
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                attrs = File.GetAttributes(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                failures.Add(folder + ": " + ex.Message);
                return;
            }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                // A junction or directory symlink: remove the link itself, never what it points at.
                Try(() => Directory.Delete(folder, false), folder, failures);
                return;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                failures.Add(folder + ": " + ex.Message);
                return;
            }

            foreach (string entry in entries)
            {
                FileAttributes a;
                try
                {
                    a = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    failures.Add(entry + ": " + ex.Message);
                    continue;
                }

                if ((a & FileAttributes.Directory) != 0)
                {
                    DeleteTree(entry, preserveFile, failures);
                }
                else if (preserveFile == null || !CleanupLayout.SamePath(entry, preserveFile))
                {
                    string file = entry;
                    Try(() => DeleteFile(file), file, failures);
                }
            }

            if (preserveFile != null && CleanupLayout.IsInside(preserveFile, folder))
            {
                return;
            }

            Try(() => Directory.Delete(folder, false), folder, failures);
        }

        private static void Try(Action action, string what, ICollection<string> failures)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                failures.Add(what + ": " + ex.Message);
            }
        }
    }
}
