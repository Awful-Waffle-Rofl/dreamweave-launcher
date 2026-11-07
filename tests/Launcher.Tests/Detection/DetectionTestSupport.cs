using System;
using System.Collections.Generic;
using System.IO;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.State;

namespace DreamWeave.Launcher.Tests.Detection
{
    internal sealed class FakeRegistry : IRegistryReader
    {
        // key path -> (value name -> value)
        public Dictionary<string, Dictionary<string, string>> Keys { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public void Set(string keyPath, string name, string value)
        {
            Dictionary<string, string> values;
            if (!Keys.TryGetValue(keyPath, out values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Keys[keyPath] = values;
            }

            values[name] = value;
        }

        public void AddUninstall(string subKey, string displayName, string installLocation)
        {
            string key = AcInstallLocator.UninstallRoot + "\\" + subKey;
            if (displayName != null)
            {
                Set(key, "DisplayName", displayName);
            }

            if (installLocation != null)
            {
                Set(key, "InstallLocation", installLocation);
            }

            if (displayName == null && installLocation == null)
            {
                Keys[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public string GetStringValue(string subKeyPath, string valueName)
        {
            Dictionary<string, string> values;
            string v;
            return Keys.TryGetValue(subKeyPath, out values) && values.TryGetValue(valueName, out v) ? v : null;
        }

        public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
        {
            string prefix = subKeyPath.TrimEnd('\\') + "\\";
            var names = new List<string>();
            foreach (string k in Keys.Keys)
            {
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0)
                {
                    names.Add(k.Substring(prefix.Length));
                }
            }

            return names;
        }
    }

    /// <summary>In-memory file system with injectable failures and call counters.</summary>
    internal sealed class FakeFs : IAcFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _mtimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> OpenFailures { get; } = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);

        public int OpenCount { get; private set; }

        public void AddDir(string path)
        {
            _dirs.Add(path);
        }

        public void SetFile(string path, byte[] content, DateTime? mtimeUtc = null)
        {
            _files[path] = content;
            _mtimes[path] = mtimeUtc ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            string dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                _dirs.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public void Remove(string path)
        {
            _files.Remove(path);
        }

        public bool DirectoryExists(string path)
        {
            return _dirs.Contains(path);
        }

        public bool FileExists(string path)
        {
            return _files.ContainsKey(path);
        }

        public FileStamp GetStamp(string path)
        {
            byte[] b;
            return _files.TryGetValue(path, out b) ? new FileStamp(b.Length, _mtimes[path]) : null;
        }

        public Stream OpenRead(string path)
        {
            OpenCount++;
            Exception ex;
            if (OpenFailures.TryGetValue(path, out ex))
            {
                throw ex;
            }

            byte[] b;
            if (!_files.TryGetValue(path, out b))
            {
                throw new FileNotFoundException("missing", path);
            }

            return new MemoryStream(b, false);
        }
    }

    internal static class DetectionData
    {
        public const string Folder = @"C:\Turbine\Asheron's Call";

        public static byte[] Bytes(string text)
        {
            return System.Text.Encoding.ASCII.GetBytes(text);
        }

        public static string Hash(byte[] data)
        {
            return Hasher.ComputeSha256Hex(data);
        }

        public static StateStore NewState(TempDir dir)
        {
            return new StateStore(Path.Combine(dir.Path, "state.json"));
        }

        public static ManifestComponent Installer(string id, string path, params string[] hashes)
        {
            return new ManifestComponent
            {
                Id = id,
                Kind = ComponentKinds.Installer,
                Size = 1000,
                SatisfiedWhen = new List<SatisfiedWhenRule> { new SatisfiedWhenRule { Path = path, Sha256 = new List<string>(hashes) } },
            };
        }

        public static ManifestComponent Overlay(string id, long size, params ManifestFile[] files)
        {
            return new ManifestComponent { Id = id, Kind = ComponentKinds.ZipOverlay, Size = size, Files = new List<ManifestFile>(files) };
        }

        public static ManifestFile File(string path, byte[] content)
        {
            return new ManifestFile { Path = path, Size = content.Length, Sha256 = Hash(content) };
        }

        public static ManifestComponent Patch(string id, string target, string input, string output)
        {
            return new ManifestComponent { Id = id, Kind = ComponentKinds.BytePatch, Target = target, InputSha256 = input, OutputSha256 = output };
        }

        public static LauncherManifest Manifest(params ManifestComponent[] components)
        {
            return new LauncherManifest { Schema = 1, Components = new List<ManifestComponent>(components) };
        }

        public static string Full(string relative)
        {
            return Folder + "\\" + relative;
        }
    }
}
