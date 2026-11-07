using System;
using System.IO;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>
    /// Resolves manifest-relative paths under the Asheron's Call folder, refusing anything that
    /// could escape it or name a Windows device. The same lexical rules run twice: at manifest
    /// load (ManifestParser.Validate calls <see cref="CheckRelativePath"/>, so a bad manifest is
    /// rejected whole) and again here at use time as defense in depth. <see cref="Resolve"/> does
    /// not follow or detect junctions/symlinks (reading is safe either way); every WRITE goes
    /// through <see cref="ResolveForWrite"/>, which refuses them.
    /// </summary>
    public static class AcPathSafety
    {
        private const string ExtendedPrefix = "\\\\?\\";
        private const string ExtendedUncPrefix = "\\\\?\\UNC\\";

        private static readonly string[] ReservedBaseNames =
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            // Windows also treats the superscript digit forms (U+00B9, U+00B2, U+00B3) of COM1-3 and LPT1-3 as devices.
            "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        };

        /// <summary>
        /// Full path of <paramref name="relativePath"/> under <paramref name="acFolder"/>.
        /// </summary>
        /// <exception cref="LauncherException">ManifestInvalid when the path is unsafe.</exception>
        public static string Resolve(string acFolder, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(acFolder))
            {
                throw new ArgumentException("The game folder is required.", nameof(acFolder));
            }

            string reason = Check(relativePath);
            if (reason != null)
            {
                throw Reject(relativePath, reason);
            }

            string root = EnsureTrailingSeparator(Path.GetFullPath(acFolder));
            string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(relativePath, "it resolves outside the game folder");
            }

            return full;
        }

        /// <summary>
        /// As <see cref="Resolve"/>, for a path about to be WRITTEN: additionally walks every segment
        /// below <paramref name="acFolder"/> that currently exists (each folder on the way, and the
        /// file itself) and refuses the path if any of them is a reparse point (junction, symbolic
        /// link, mount point). The game folder itself may be a link (a moved install), so it is not
        /// checked. The check is a snapshot: writers call this again immediately before each open
        /// for write and before each replace.
        /// </summary>
        /// <exception cref="LauncherException">
        /// ManifestInvalid when the path is lexically unsafe; ReparsePointInPath when a segment is a
        /// reparse point; AccessDenied or Unknown when a segment's attributes cannot be read (fail closed).
        /// </exception>
        public static string ResolveForWrite(string acFolder, string relativePath)
        {
            string full = Resolve(acFolder, relativePath);
            RefuseReparsePointsBelow(Path.GetFullPath(acFolder), full, "'" + relativePath + "' was not written");
            return full;
        }

        /// <summary>
        /// Walks every segment of <paramref name="fullPath"/> BELOW <paramref name="rootFolder"/>
        /// (not the root itself) that currently exists and throws when one is a reparse point.
        /// Segments that do not exist end the walk (nothing deeper can exist).
        /// </summary>
        /// <param name="rootFolder">Absolute folder the walk starts under; <paramref name="fullPath"/> must lie inside it.</param>
        /// <param name="fullPath">Absolute path to check.</param>
        /// <param name="what">Start of the error sentence, for example <c>'x.dat' was not written</c>.</param>
        /// <exception cref="LauncherException">ReparsePointInPath; AccessDenied or Unknown when attributes cannot be read (fail closed).</exception>
        internal static void RefuseReparsePointsBelow(string rootFolder, string fullPath, string what)
        {
            string root = EnsureTrailingSeparator(rootFolder);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The path is not under the folder.", nameof(fullPath));
            }

            string current = root.Substring(0, root.Length - 1);
            foreach (string segment in fullPath.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "\\" + segment;
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw LauncherException.FromIo(ex, "checking " + current);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new LauncherException(
                        LauncherError.ReparsePointInPath,
                        what + " because '" + ToDisplayPath(current) + "' is a junction or link. Remove the link or use a normal folder.");
                }
            }
        }

        /// <summary>
        /// Walks every segment of <paramref name="fullPath"/> from its drive root (the file or
        /// folder itself included) and throws when one is a reparse point.
        /// </summary>
        /// <exception cref="LauncherException">As <see cref="RefuseReparsePointsBelow"/>.</exception>
        internal static void RefuseReparsePointsInPath(string fullPath, string what)
        {
            string full = Path.GetFullPath(fullPath);
            RefuseReparsePointsBelow(Path.GetPathRoot(full), full, what);
        }

        /// <summary>
        /// Why <paramref name="path"/> is not an absolute path on a local fixed drive (<c>X:\...</c>),
        /// or null when it is. Refuses UNC (<c>\\server\share</c>), device and extended-length forms
        /// (<c>\\.\</c>, <c>\\?\</c>), relative and drive-relative paths, and removable, network,
        /// optical and RAM drives. Used for every location an ELEVATED process accepts from a task file.
        /// </summary>
        public static string CheckLocalFixedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "it is empty";
            }

            if (IsNetworkPath(path))
            {
                return "it is a network path";
            }

            if (path.StartsWith("\\", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal))
            {
                return "it is a device or extended-length path";
            }

            bool driveForm = path.Length >= 3
                && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/');
            if (!driveForm)
            {
                return "it is not an absolute path on a drive";
            }

            try
            {
                DriveType driveType = new DriveInfo(path.Substring(0, 1)).DriveType;
                if (driveType == DriveType.Network)
                {
                    return "it is a network path (a mapped network drive)";
                }

                if (driveType != DriveType.Fixed)
                {
                    return "it is not on a local fixed drive";
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return "its drive could not be checked";
            }

            return null;
        }

        /// <summary>
        /// True for a UNC path (<c>\\server\share</c>, or the extended-length form <c>\\?\UNC\server\share</c>).
        /// Device paths (<c>\\.\</c>) and <c>\\?\X:\</c> paths are not network paths.
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // Windows accepts either separator, so //server/share and \/server are UNC too.
            string p = path.Replace('/', '\\');
            if (!p.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return false;
            }

            if (p.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !p.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
                && !p.StartsWith("\\\\.\\", StringComparison.Ordinal);
        }

        /// <summary>
        /// A path as the player should read it: <c>\\?\UNC\server\share</c> becomes <c>\\server\share</c> and a
        /// bare <c>\\?\</c> prefix is dropped. Any other path is returned unchanged.
        /// </summary>
        public static string ToDisplayPath(string path)
        {
            return StripExtendedPrefix(path);
        }

        /// <summary>
        /// Drops the extended-length prefix so the path can be checked as an ordinary one:
        /// <c>\\?\UNC\server\share</c> becomes <c>\\server\share</c> and a bare <c>\\?\</c> is removed.
        /// Any other path is returned unchanged.
        /// </summary>
        public static string StripExtendedPrefix(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "\\\\" + path.Substring(ExtendedUncPrefix.Length);
            }

            if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            {
                return path.Substring(ExtendedPrefix.Length);
            }

            return path;
        }

        /// <summary>True when <paramref name="relativePath"/> passes every lexical rule (see <see cref="Resolve"/>).</summary>
        public static bool IsSafeRelativePath(string relativePath)
        {
            return Check(relativePath) == null;
        }

        /// <summary>
        /// The first lexical rule <paramref name="relativePath"/> breaks, in plain words, or null when
        /// it is acceptable. Shared with the manifest parser so load-time and use-time rules cannot drift.
        /// </summary>
        internal static string CheckRelativePath(string relativePath)
        {
            return Check(relativePath);
        }

        /// <summary>The first rule the path breaks, in plain words, or null when it is acceptable.</summary>
        private static string Check(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0)
            {
                return "it is empty";
            }

            foreach (char c in path)
            {
                if (c < ' ')
                {
                    return "it contains a control character";
                }

                if (c == ':')
                {
                    return "it is drive-qualified or names a stream";
                }

                if (c == '*' || c == '?')
                {
                    return "it contains a wildcard";
                }

                if (c == '<' || c == '>' || c == '"' || c == '|')
                {
                    return "it contains a character Windows does not allow in file names";
                }
            }

            if (path[0] == '\\' || path[0] == '/')
            {
                return "it is rooted";
            }

            foreach (string segment in path.Split('\\', '/'))
            {
                if (segment.Length == 0)
                {
                    return "it has an empty segment";
                }

                if (segment == "..")
                {
                    return "it climbs out of the folder with '..'";
                }

                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ')
                {
                    return "a segment ends with a dot or a space";
                }

                int dot = segment.IndexOf('.');
                string baseName = (dot < 0 ? segment : segment.Substring(0, dot)).TrimEnd(' ');
                foreach (string reserved in ReservedBaseNames)
                {
                    if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
                    {
                        return "a segment names a Windows device (" + reserved + ")";
                    }
                }
            }

            return null;
        }

        private static string EnsureTrailingSeparator(string folder)
        {
            string trimmed = folder.TrimEnd('\\', '/');
            return trimmed + "\\";
        }

        private static LauncherException Reject(string path, string reason)
        {
            return new LauncherException(LauncherError.ManifestInvalid, "The manifest names a file path that is not allowed ('" + path + "'): " + reason + ".");
        }
    }
}
