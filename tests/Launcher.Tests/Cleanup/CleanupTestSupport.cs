using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DreamWeave.Launcher.Core;
using DreamWeave.Launcher.Core.Credentials;
using DreamWeave.Launcher.Core.Downloads;
using DreamWeave.Launcher.Core.Cleanup;
using DreamWeave.Launcher.Core.Update;
using DreamWeave.Launcher.Tests.Detection;

namespace DreamWeave.Launcher.Tests.Cleanup
{
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

    /// <summary>The real file system with a hook that can fail any DeleteFile or DeleteTree.</summary>
    internal sealed class FaultyFileSystem : ISelfFileSystem
    {
        private readonly SelfFileSystem _real = new SelfFileSystem();

        public Func<string, Exception> DeleteFailure { get; set; }

        public bool FileExists(string path) => _real.FileExists(path);

        public bool DirectoryExists(string path) => _real.DirectoryExists(path);

        public void DeleteFile(string path)
        {
            Exception ex = DeleteFailure?.Invoke(path);
            if (ex != null)
            {
                throw ex;
            }

            _real.DeleteFile(path);
        }

        /// <summary>When set, a non-null result for a folder is recorded as a failure (and the folder is left alone).</summary>
        public Func<string, string> TreeFailure { get; set; }

        public void DeleteTree(string folder, string preserveFile, ICollection<string> failures)
        {
            string failure = TreeFailure?.Invoke(folder);
            if (failure != null)
            {
                failures.Add(failure);
                return;
            }

            _real.DeleteTree(folder, preserveFile, failures);
        }
    }

    /// <summary>A temp root with AppPaths and every fake wired into a <see cref="CleanupEnvironment"/>.</summary>
    internal sealed class CleanupFixture : IDisposable
    {
        private readonly TempDir _temp = new TempDir();

        public CleanupFixture()
        {
            Paths = new AppPaths(Path.Combine(_temp.Path, "LocalAppData"));
            Environment = new CleanupEnvironment
            {
                Paths = Paths,
                MachineRegistry = MachineRegistry,
                FileSystem = FileSystem,
                Processes = Processes,
                Credentials = Credentials,
            };
        }

        public string Root => _temp.Path;

        public AppPaths Paths { get; }

        public CleanupEnvironment Environment { get; }

        public FakeRegistry MachineRegistry { get; } = new FakeRegistry();

        public FaultyFileSystem FileSystem { get; } = new FaultyFileSystem();

        public FakeProcessLauncher Processes { get; } = new FakeProcessLauncher();

        public FakeCredentialStore Credentials { get; } = new FakeCredentialStore();

        public void Dispose() => _temp.Dispose();
    }
}
