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
        /// otherwise it returns the matching exit code and sets <paramref name="token"/> to the stderr
        /// token. The game folder must exist and contain acclient.exe; Inject.dll must be an existing
        /// absolute path named Inject.dll; neither acclient.exe nor Inject.dll may be a reparse point.
        /// </summary>
        public static int Validate(string gameFolder, string injectDllPath, out DecalLaunchInputs inputs, out string token)
        {
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
                || !IsAbsolute(injectDllPath)
                || !PathEndsWith(injectDllPath, InjectDllName))
            {
                token = DecalLaunchResult.TokenInjectPathInvalid;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            string inject;
            try
            {
                inject = Path.GetFullPath(injectDllPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                token = DecalLaunchResult.TokenInjectPathInvalid;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            if (!FileExistsSafe(inject))
            {
                token = DecalLaunchResult.TokenInjectMissing;
                return DecalLaunchResult.ExitInjectInvalid;
            }

            if (IsReparsePoint(acclient) || IsReparsePoint(inject))
            {
                token = DecalLaunchResult.TokenReparsePoint;
                return DecalLaunchResult.ExitReparsePoint;
            }

            inputs = new DecalLaunchInputs(acclient, Path.GetDirectoryName(acclient), inject);
            return DecalLaunchResult.ExitSuccess;
        }

        private static bool IsAbsolute(string path)
        {
            try
            {
                return Path.IsPathRooted(path)
                    && path.Length >= 3
                    && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                    && path[1] == ':'
                    && (path[2] == '\\' || path[2] == '/');
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool PathEndsWith(string path, string fileName)
        {
            string name;
            try
            {
                name = Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase);
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
