using System;
using System.IO;

namespace DreamWeave.DecalLaunch
{
    /// <summary>One checked set of helper inputs: the full acclient.exe path and the Inject.dll path.</summary>
    public sealed class DecalLaunchInputs
    {
        private DecalLaunchInputs(string acclientPath, string gameFolder, string injectDllPath)
        {
            AcclientPath = acclientPath;
            GameFolder = gameFolder;
            InjectDllPath = injectDllPath;
        }

        /// <summary>The full path to the one executable the helper may start.</summary>
        public string AcclientPath { get; }

        /// <summary>The game folder (acclient.exe's directory), used as the working directory.</summary>
        public string GameFolder { get; }

        /// <summary>The validated, existing Inject.dll path.</summary>
        public string InjectDllPath { get; }

        /// <summary>The one executable name the helper is ever allowed to start.</summary>
        public const string AcclientExe = "acclient.exe";

        /// <summary>The injection library file name the Inject.dll argument must end with.</summary>
        public const string InjectDllName = "Inject.dll";

        /// <summary>
        /// Validates the two arguments without starting anything. On success <paramref name="inputs"/>
        /// holds the checked paths and the method returns <see cref="DecalLaunchResult.ExitSuccess"/>;
        /// otherwise it returns the matching exit code and sets <paramref name="token"/> to the stderr token.
        /// <list type="bullet">
        /// <item>The game folder must exist and contain acclient.exe. The game folder itself may be a link
        /// (a moved install), but acclient.exe must not be a reparse point.</item>
        /// <item>Inject.dll must be an absolute drive path named Inject.dll with no <c>..</c> segment, must
        /// exist, and must be exactly the Inject.dll in the folder Decal's registry entry names
        /// (<paramref name="readAgentPath"/>, which reads HKLM SOFTWARE\Decal\Agent AgentPath in the
        /// 32-bit view), compared case-insensitively after full-path normalisation.</item>
        /// <item>Neither Inject.dll nor any folder between the drive root and it may be a reparse point.</item>
        /// </list>
        /// </summary>
        public static int Validate(string gameFolder, string injectDllPath, Func<string> readAgentPath, out DecalLaunchInputs inputs, out string token)
        {
            if (readAgentPath == null)
            {
                throw new ArgumentNullException(nameof(readAgentPath));
            }

            inputs = null;
            token = null;

            if (string.IsNullOrWhiteSpace(gameFolder) || !DirectoryExistsSafe(gameFolder))
            {
                token = DecalLaunchResult.TokenGameFolderMissing;
                return DecalLaunchResult.ExitGameFolderInvalid;
            }

            string acclient;
            try
            {
                acclient = Path.GetFullPath(Path.Combine(gameFolder, AcclientExe));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                token = DecalLaunchResult.TokenGameFolderMissing;
                return DecalLaunchResult.ExitGameFolderInvalid;
            }

            if (!FileExistsSafe(acclient))
            {
                token = DecalLaunchResult.TokenAcclientMissing;
                return DecalLaunchResult.ExitGameFolderInvalid;
            }

            if (string.IsNullOrWhiteSpace(injectDllPath)
                || !IsAbsoluteDrivePath(injectDllPath)
                || HasDotDotSegment(injectDllPath)
                || !NameIs(injectDllPath, InjectDllName))
            {
                token = DecalLaunchResult.TokenInjectPathInvalid;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            string inject = FullPathOrNull(injectDllPath);
            if (inject == null)
            {
                token = DecalLaunchResult.TokenInjectPathInvalid;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            if (!FileExistsSafe(inject))
            {
                token = DecalLaunchResult.TokenInjectMissing;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            string registered = RegisteredInjectDll(readAgentPath);
            if (registered == null || !string.Equals(registered, inject, StringComparison.OrdinalIgnoreCase))
            {
                token = DecalLaunchResult.TokenInjectNotRegistered;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            if (IsReparsePoint(acclient))
            {
                token = DecalLaunchResult.TokenAcclientReparsePoint;
                return DecalLaunchResult.ExitReparsePoint;
            }

            if (AnyReparsePointFromRoot(inject))
            {
                token = DecalLaunchResult.TokenInjectReparsePoint;
                return DecalLaunchResult.ExitReparsePoint;
            }

            inputs = new DecalLaunchInputs(acclient, Path.GetDirectoryName(acclient), inject);
            return DecalLaunchResult.ExitSuccess;
        }

        /// <summary>The full Inject.dll path Decal's registry entry names, or null when it is absent or unusable.</summary>
        private static string RegisteredInjectDll(Func<string> readAgentPath)
        {
            string agent;
            try
            {
                agent = readAgentPath();
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(agent))
            {
                return null;
            }

            try
            {
                return FullPathOrNull(Path.Combine(agent.Trim(), InjectDllName));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsAbsoluteDrivePath(string path)
        {
            return path.Length >= 3
                && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/');
        }

        private static bool HasDotDotSegment(string path)
        {
            foreach (string segment in path.Split('\\', '/'))
            {
                if (segment.Trim() == "..")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NameIs(string path, string fileName)
        {
            try
            {
                return string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string FullPathOrNull(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static bool DirectoryExistsSafe(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static bool FileExistsSafe(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>True when any folder below the drive root, or the file itself, is a reparse point (fail closed on unreadable attributes).</summary>
        private static bool AnyReparsePointFromRoot(string fullPath)
        {
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            string current = root.TrimEnd('\\', '/');
            foreach (string segment in fullPath.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "\\" + segment;
                if (IsReparsePoint(current))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Unreadable attributes: fail closed by treating it as a reparse point.
                return true;
            }
        }
    }
}
