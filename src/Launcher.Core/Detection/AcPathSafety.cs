using System;
using System.IO;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>
    /// Resolves manifest-relative paths under the Asheron's Call folder, refusing anything that
    /// could escape it or name a Windows device. The same lexical rules run twice: at manifest
    /// load (ManifestParser.Validate calls <see cref="CheckRelativePath"/>, so a bad manifest is
    /// rejected whole) and again here at use time as defense in depth. Does not follow or detect
    /// junctions/symlinks.
    /// </summary>
    public static class AcPathSafety
    {
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
