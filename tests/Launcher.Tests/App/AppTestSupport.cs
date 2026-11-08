using System.Collections.Generic;
using System.Diagnostics;
using DreamWeave.Launcher.Core.Detection;
using DreamWeave.Launcher.Core.Manifests;
using DreamWeave.Launcher.Core.Cleanup;

namespace DreamWeave.Launcher.Tests.App
{
    /// <summary>Hand-built manifests and statuses shaped like manifest/manifest.sample.json.</summary>
    internal static class AppFixtures
    {
        public const string ClientExe = "acclient.exe";

        public static ManifestComponent Installer(bool required = true)
        {
            return new ManifestComponent
            {
                Id = "ac-client",
                Kind = ComponentKinds.Installer,
                Required = required,
                DefaultSelected = true,
                Title = "Asheron's Call client",
                SourceLabel = "Original Turbine installer",
                Transport = Transports.Http,
                Url = "https://example.test/ac1install.exe",
                Size = 598884909,
                Sha256 = new string('A', 64),
                SatisfiedWhen = new List<SatisfiedWhenRule> { new SatisfiedWhenRule { Path = ClientExe, Sha256 = new List<string> { new string('B', 64) } } },
            };
        }

        public static ManifestComponent Overlay(bool includesClientExe = true)
        {
            var c = new ManifestComponent
            {
                Id = "eor-dats",
                Kind = ComponentKinds.ZipOverlay,
                Required = true,
                DefaultSelected = true,
                Title = "End-of-Retail game data",
                SourceLabel = "Community archive (mega.nz)",
                Transport = Transports.Mega,
                Url = "https://mega.nz/file/x#y",
                Size = 507898112,
                Sha256 = new string('C', 64),
                Files = new List<ManifestFile> { new ManifestFile { Path = "client_portal.dat", Size = 10, Sha256 = new string('D', 64) } },
            };
            if (includesClientExe)
            {
                c.Files.Add(new ManifestFile { Path = ClientExe, Size = 10, Sha256 = new string('B', 64) });
            }

            return c;
        }

        public static ManifestComponent Patch(bool defaultSelected = false)
        {
            return new ManifestComponent
            {
                Id = "palette-leak-fix",
                Kind = ComponentKinds.BytePatch,
                Required = false,
                DefaultSelected = defaultSelected,
                Title = "Palette memory leak fix (optional)",
                SourceLabel = "Two 3-byte changes",
                Target = ClientExe,
                InputSha256 = new string('B', 64),
                OutputSha256 = new string('E', 64),
                Patches = new List<BytePatch> { new BytePatch { Offset = 1, Expect = "FF", Replace = "90" } },
            };
        }

        public static LauncherManifest Manifest(params ManifestComponent[] components)
        {
            return new LauncherManifest
            {
                Schema = 1,
                Serial = 1,
                Server = new ServerInfo { Name = "DreamWeave", Host = "play.example.test", Port = 9000 },
                Launch = new LaunchInfo(),
                Launcher = new LauncherUpdateInfo { Version = "1.0.0", MinVersion = "1.0.0", Url = string.Empty, Sha256 = string.Empty },
                Components = new List<ManifestComponent>(components),
            };
        }

        public static LauncherManifest Sample()
        {
            return Manifest(Installer(), Overlay(), Patch());
        }

        public static ComponentStatus Status(string id, ComponentState state, params FileDetail[] files)
        {
            return new ComponentStatus(id, "x", state, files, 0, null);
        }

        public static FileDetail File(string path, FileState state)
        {
            return new FileDetail(path, state, null, null, null, null);
        }
    }

    /// <summary>A scripted process list for <see cref="DreamWeave.Launcher.Core.Install.InstallerProcessWatch"/>; empty by default.</summary>
    internal sealed class FakeProcessTable : DreamWeave.Launcher.Core.Install.IProcessTable
    {
        private readonly object _gate = new object();
        private List<DreamWeave.Launcher.Core.Install.ProcessEntry> _entries = new List<DreamWeave.Launcher.Core.Install.ProcessEntry>();

        /// <summary>When true every snapshot fails, as a Toolhelp error would.</summary>
        public bool Throw { get; set; }

        /// <summary>How many snapshots were taken.</summary>
        public int Snapshots { get; private set; }

        /// <summary>Replaces the list: (pid, parent pid, image name) triples.</summary>
        public void Set(params (int Pid, int Parent, string Name)[] entries)
        {
            lock (_gate)
            {
                _entries = new List<DreamWeave.Launcher.Core.Install.ProcessEntry>();
                foreach (var e in entries)
                {
                    _entries.Add(new DreamWeave.Launcher.Core.Install.ProcessEntry(e.Pid, e.Parent, e.Name));
                }
            }
        }

        public IReadOnlyList<DreamWeave.Launcher.Core.Install.ProcessEntry> Snapshot()
        {
            lock (_gate)
            {
                Snapshots++;
                if (Throw)
                {
                    throw new System.ComponentModel.Win32Exception(299);
                }

                return _entries.ToArray();
            }
        }
    }

    /// <summary>Records start infos instead of starting anything.</summary>
    internal sealed class FakeProcessLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();

        public List<ProcessStartInfo> Ran { get; } = new List<ProcessStartInfo>();

        public int ExitCode { get; set; }

        public System.Exception Throw { get; set; }

        public void Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
        }

        public int Run(ProcessStartInfo startInfo)
        {
            Ran.Add(startInfo);
            if (Throw != null)
            {
                throw Throw;
            }

            return ExitCode;
        }
    }
}
