using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.SelfInstall;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.Tests.Detection;
using Microsoft.Win32.SafeHandles;

namespace DreamWeave.Launcher.Tests.SelfInstall
{
    internal sealed class FakeUserRegistry : IUserRegistry
    {
        public Dictionary<string, Dictionary<string, object>> Keys { get; } = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        public bool FailWrites { get; set; }

        public void SetValues(string subKeyPath, IReadOnlyDictionary<string, object> values)
        {
            if (FailWrites)
            {
                throw new LauncherException(LauncherError.RegistryWriteFailed, "fake registry write failure");
            }

            Dictionary<string, object> key;
            if (!Keys.TryGetValue(subKeyPath, out key))
            {
                key = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Keys[subKeyPath] = key;
            }

            foreach (KeyValuePair<string, object> v in values)
            {
                key[v.Key] = v.Value;
            }
        }

        public object GetValue(string subKeyPath, string valueName)
        {
            Dictionary<string, object> key;
            object v;
            return Keys.TryGetValue(subKeyPath, out key) && key.TryGetValue(valueName, out v) ? v : null;
        }

        public bool DeleteKeyTree(string subKeyPath)
        {
            if (FailWrites)
            {
                throw new LauncherException(LauncherError.RegistryWriteFailed, "fake registry delete failure");
            }

            string prefix = subKeyPath + "\\";
            List<string> doomed = Keys.Keys.Where(k => string.Equals(k, subKeyPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string k in doomed)
            {
                Keys.Remove(k);
            }

            return doomed.Count > 0;
        }
    }

    /// <summary>Writes a plain text file whose single line is the target, so tests never create real shell links.</summary>
    internal sealed class FakeShortcutWriter : IShortcutWriter
    {
        public int Writes { get; private set; }

        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            Writes++;
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            File.WriteAllText(shortcutPath, targetPath);
        }

        public string ReadTarget(string shortcutPath)
        {
            return File.Exists(shortcutPath) ? File.ReadAllText(shortcutPath) : null;
        }
    }

    internal sealed class FakeProcessLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();

        public List<ProcessStartInfo> Ran { get; } = new List<ProcessStartInfo>();

        public Func<ProcessStartInfo, Exception> StartFailure { get; set; }

        public Func<ProcessStartInfo, int> RunExitCode { get; set; } = psi => 0;

        public Func<ProcessStartInfo, Exception> RunFailure { get; set; }

        public void Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            Exception ex = StartFailure?.Invoke(startInfo);
            if (ex != null)
            {
                throw ex;
            }
        }

        public int Run(ProcessStartInfo startInfo)
        {
            Ran.Add(startInfo);
            Exception ex = RunFailure?.Invoke(startInfo);
            if (ex != null)
            {
                throw ex;
            }

            return RunExitCode(startInfo);
        }
    }

    internal sealed class FakeSelfDeleteScheduler : ISelfDeleteScheduler
    {
        public List<Tuple<string, string>> Calls { get; } = new List<Tuple<string, string>>();

        public bool Result { get; set; } = true;

        public bool Schedule(string exePath, string folder)
        {
            Calls.Add(Tuple.Create(exePath, folder));
            return Result;
        }
    }

    internal sealed class FakeCredentialStore : ICredentialStore
    {
        public SortedDictionary<string, string> Saved { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public bool FailList { get; set; }

        public void Save(string account, string password) => Saved[account] = password;

        public string Get(string account)
        {
            string p;
            return Saved.TryGetValue(account, out p) ? p : null;
        }

        public bool Delete(string account) => Saved.Remove(account);

        public IReadOnlyList<string> ListAccounts()
        {
            if (FailList)
            {
                throw new LauncherException(LauncherError.CredentialStoreUnavailable, "fake store unavailable");
            }

            return Saved.Keys.ToList();
        }
    }

    /// <summary>The real file system with a hook that can fail any Move or DeleteFile, recording every Move.</summary>
    internal sealed class FaultyFileSystem : ISelfFileSystem
    {
        private readonly SelfFileSystem _real = new SelfFileSystem();

        public List<Tuple<string, string>> Moves { get; } = new List<Tuple<string, string>>();

        /// <summary>Called before each Move with (1-based move number, source, destination); return an exception to fail it.</summary>
        public Func<int, string, string, Exception> MoveFailure { get; set; }

        public Func<string, Exception> DeleteFailure { get; set; }

        public bool FileExists(string path) => _real.FileExists(path);

        public bool DirectoryExists(string path) => _real.DirectoryExists(path);

        public void CreateDirectory(string path) => _real.CreateDirectory(path);

        public FileStream OpenReadLocked(string path) => _real.OpenReadLocked(path);

        public FileStream CreateHeldForMove(string path) => _real.CreateHeldForMove(path);

        public void Move(string sourcePath, string destinationPath)
        {
            Moves.Add(Tuple.Create(sourcePath, destinationPath));
            Exception ex = MoveFailure?.Invoke(Moves.Count, sourcePath, destinationPath);
            if (ex != null)
            {
                throw ex;
            }

            _real.Move(sourcePath, destinationPath);
        }

        public void DeleteFile(string path)
        {
            Exception ex = DeleteFailure?.Invoke(path);
            if (ex != null)
            {
                throw ex;
            }

            _real.DeleteFile(path);
        }

        public void DeleteTree(string folder, string preserveFile, ICollection<string> failures) => _real.DeleteTree(folder, preserveFile, failures);
    }

    /// <summary>Writes scripted bytes to the request's destination, as a successful download would.</summary>
    internal sealed class FakeDownloader : IDownloader
    {
        private readonly byte[] _bytes;

        public FakeDownloader(byte[] bytes)
        {
            _bytes = bytes;
        }

        public List<DownloadRequest> Requests { get; } = new List<DownloadRequest>();

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath));
            File.WriteAllBytes(request.DestinationPath, _bytes);
            return Task.FromResult(new DownloadResult(request.DestinationPath, _bytes.Length, Hasher.ComputeSha256Hex(_bytes), false));
        }
    }

    internal sealed class FakeAuthenticode : IAuthenticodeVerifier
    {
        public List<string> Calls { get; } = new List<string>();

        public bool Fail { get; set; }

        public void Verify(string path, FileStream heldFile, string expectedSubject)
        {
            Calls.Add(expectedSubject);
            if (Fail)
            {
                throw new LauncherException(LauncherError.UpdateSignatureInvalid, "fake signature failure");
            }
        }
    }

    /// <summary>A temp root with AppPaths, a fake Start menu and every fake wired into a <see cref="SelfInstallEnvironment"/>.</summary>
    internal sealed class SelfInstallFixture : IDisposable
    {
        private readonly TempDir _temp = new TempDir();

        public SelfInstallFixture()
        {
            Paths = new AppPaths(Path.Combine(_temp.Path, "LocalAppData"));
            Environment = new SelfInstallEnvironment
            {
                Paths = Paths,
                UserRegistry = Registry,
                MachineRegistry = MachineRegistry,
                Shortcuts = Shortcuts,
                ProgramsFolder = Path.Combine(_temp.Path, "StartMenu", "Programs"),
                FileSystem = FileSystem,
                Processes = Processes,
                Credentials = Credentials,
                SelfDelete = SelfDelete,
            };
        }

        public string Root => _temp.Path;

        public AppPaths Paths { get; }

        public SelfInstallEnvironment Environment { get; }

        public FakeUserRegistry Registry { get; } = new FakeUserRegistry();

        public FakeRegistry MachineRegistry { get; } = new FakeRegistry();

        public FakeShortcutWriter Shortcuts { get; } = new FakeShortcutWriter();

        public FaultyFileSystem FileSystem { get; } = new FaultyFileSystem();

        public FakeProcessLauncher Processes { get; } = new FakeProcessLauncher();

        public FakeCredentialStore Credentials { get; } = new FakeCredentialStore();

        public FakeSelfDeleteScheduler SelfDelete { get; } = new FakeSelfDeleteScheduler();

        /// <summary>Writes a fake "downloaded" launcher exe outside the install folder and returns its path.</summary>
        public string WriteDownloadedExe(byte[] content, string name = "DreamWeaveLauncher (1).exe")
        {
            string dir = Path.Combine(Root, "Downloads");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose() => _temp.Dispose();
    }

    /// <summary>NTFS alternate data streams via CreateFileW (.NET Framework rejects ':' in FileStream paths).</summary>
    internal static class AlternateStreams
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint CreateAlways = 2;
        private const uint OpenExisting = 3;

        public static void Write(string path, string streamName, string content)
        {
            using (SafeFileHandle h = CreateFileW(path + ":" + streamName, GenericWrite, 0, IntPtr.Zero, CreateAlways, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                using (var fs = new FileStream(h, FileAccess.Write))
                {
                    byte[] b = Encoding.ASCII.GetBytes(content);
                    fs.Write(b, 0, b.Length);
                }
            }
        }

        public static bool Exists(string path, string streamName)
        {
            using (SafeFileHandle h = CreateFileW(path + ":" + streamName, GenericRead, 1, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                return !h.IsInvalid;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}
