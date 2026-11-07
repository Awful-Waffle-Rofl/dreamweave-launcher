using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace DreamWeave.Launcher.Core.State
{
    /// <summary>
    /// Thread-safe owner of <see cref="LauncherState"/> and its JSON file.
    /// <para>
    /// Every <see cref="Update"/> is a read-modify-write under BOTH an in-process lock and an
    /// exclusive lock file (<c>state.json.lock</c>), so several stores on the same file (two
    /// launcher instances, or two objects in one process) never lose each other's writes: the
    /// file is re-read, the mutation applied, and the result written atomically
    /// (see <see cref="AtomicFile"/>). <see cref="LauncherState.HighestManifestSerial"/> is
    /// monotonic: the saved value is always the max of what was on disk, in memory, and set by
    /// the mutation, so no writer can lower the rollback floor.
    /// </para>
    /// <para>
    /// A missing file starts fresh; an unreadable (corrupt) file found at open is renamed aside to
    /// <c>state.json.corrupt-&lt;utc ticks&gt;</c> and the store starts fresh.
    /// </para>
    /// <para>
    /// Every call does synchronous file I/O and can WAIT (up to the lock timeout, 30 seconds by
    /// default) for another launcher instance to release the lock file, so never call the
    /// constructor, <see cref="Update"/> or <see cref="Open"/> on the UI thread; run them on a
    /// background thread. A timed-out wait throws <see cref="LauncherError.StateLocked"/>.
    /// </para>
    /// </summary>
    public sealed class StateStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        /// <summary>Default wait for another instance to release the lock file.</summary>
        internal static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

        private const int ErrorSharingViolation = 0x20;
        private const int ErrorLockViolation = 0x21;

        private readonly TimeSpan _lockTimeout;
        private readonly object _gate = new object();
        private readonly string _path;
        private readonly string _lockPath;
        private LauncherState _state;

        /// <summary>
        /// Loads (or creates) the state file at <paramref name="stateFilePath"/>. On first run a
        /// new <see cref="LauncherState.InstallId"/> is generated and persisted immediately.
        /// </summary>
        /// <exception cref="LauncherException">When the file cannot be written or the lock cannot be taken.</exception>
        public StateStore(string stateFilePath)
            : this(stateFilePath, DefaultLockTimeout)
        {
        }

        /// <summary>As the public constructor, with an explicit lock-file wait (tests use a short one).</summary>
        internal StateStore(string stateFilePath, TimeSpan lockTimeout)
        {
            _lockTimeout = lockTimeout;
            _path = Path.GetFullPath(stateFilePath);
            _lockPath = _path + ".lock";
            lock (_gate)
            {
                using (AcquireFileLock())
                {
                    _state = LoadOrNew(_path, out bool needsSave);
                    if (_state.InstallId == Guid.Empty)
                    {
                        _state.InstallId = Guid.NewGuid();
                        needsSave = true;
                    }

                    if (needsSave)
                    {
                        Save(_state);
                    }
                }
            }
        }

        /// <summary>Opens the store at <see cref="AppPaths.StateFile"/>.</summary>
        public static StateStore Open(AppPaths paths)
        {
            return new StateStore(paths.StateFile);
        }

        /// <summary>The full path of the backing file.</summary>
        public string FilePath => _path;

        /// <summary>Highest manifest serial accepted so far (as of this store's last load or update).</summary>
        public long HighestManifestSerial => Read(s => s.HighestManifestSerial);

        /// <summary>This install's stable random identifier.</summary>
        public Guid InstallId => Read(s => s.InstallId);

        /// <summary>
        /// Reads a value from this store's in-memory copy under the store lock. Do not let the state
        /// object escape the callback.
        /// </summary>
        public T Read<T>(Func<LauncherState, T> reader)
        {
            lock (_gate)
            {
                return reader(_state);
            }
        }

        /// <summary>
        /// Re-reads the file, applies <paramref name="mutate"/>, and persists atomically, all under
        /// the in-process lock and the lock file. If the mutation throws or the write fails, the
        /// in-memory state is restored to what it was before the call and the exception propagates.
        /// </summary>
        public void Update(Action<LauncherState> mutate)
        {
            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            lock (_gate)
            {
                using (AcquireFileLock())
                {
                    string before = Serialize(_state);
                    try
                    {
                        LauncherState current = TryLoadExisting(_path) ?? _state;
                        long floor = Math.Max(current.HighestManifestSerial, _state.HighestManifestSerial);
                        if (current.InstallId == Guid.Empty)
                        {
                            current.InstallId = _state.InstallId;
                        }

                        _state = current;
                        mutate(_state);
                        _state.HighestManifestSerial = Math.Max(_state.HighestManifestSerial, floor);
                        Save(_state);
                    }
                    catch
                    {
                        _state = Deserialize(before);
                        throw;
                    }
                }
            }
        }

        private void Save(LauncherState state)
        {
            AtomicFile.WriteAllBytes(_path, Utf8NoBom.GetBytes(Serialize(state)));
        }

        private IDisposable AcquireFileLock()
        {
            string dir = Path.GetDirectoryName(_lockPath);
            try
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "creating the launcher data folder");
            }

            var clock = Stopwatch.StartNew();
            var random = new Random(Guid.NewGuid().GetHashCode());
            while (true)
            {
                try
                {
                    return new LockHandle(new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw LauncherException.FromIo(ex, "locking launcher state");
                }
                catch (IOException ex) when (IsSharingOrLockViolation(ex))
                {
                    if (clock.Elapsed > _lockTimeout)
                    {
                        throw new LauncherException(LauncherError.StateLocked, "Another copy of the launcher is holding its settings file locked. Close it and try again.", ex);
                    }

                    Thread.Sleep(random.Next(2, 15));
                }
                catch (IOException ex)
                {
                    // Anything else (disk full, path problems, device errors) will not clear by waiting.
                    throw LauncherException.FromIo(ex, "locking launcher state");
                }
            }
        }

        /// <summary>True only for ERROR_SHARING_VIOLATION (0x20) and ERROR_LOCK_VIOLATION (0x21), the two errors another holder of the lock file produces.</summary>
        internal static bool IsSharingOrLockViolation(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == ErrorSharingViolation || code == ErrorLockViolation;
        }

        private static LauncherState LoadOrNew(string path, out bool needsSave)
        {
            needsSave = false;
            if (!File.Exists(path))
            {
                needsSave = true;
                return new LauncherState();
            }

            string text = ReadText(path);
            try
            {
                LauncherState state = Deserialize(text);
                if (state != null)
                {
                    return state;
                }
            }
            catch (JsonException)
            {
            }

            // Corrupt: keep the evidence, start fresh.
            try
            {
                File.Move(path, path + ".corrupt-" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The atomic save replaces it anyway.
            }

            needsSave = true;
            return new LauncherState();
        }

        /// <summary>The on-disk state, or null when the file is missing or unreadable (the in-memory copy is used then).</summary>
        private static LauncherState TryLoadExisting(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return Deserialize(ReadText(path));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "reading launcher state");
            }
        }

        private static string Serialize(LauncherState state)
        {
            return JsonConvert.SerializeObject(state, JsonDefaults.Create());
        }

        private static LauncherState Deserialize(string text)
        {
            var settings = JsonDefaults.Create();
            settings.DateParseHandling = DateParseHandling.DateTime;
            return JsonConvert.DeserializeObject<LauncherState>(text, settings);
        }

        private sealed class LockHandle : IDisposable
        {
            private FileStream _stream;

            public LockHandle(FileStream stream)
            {
                _stream = stream;
            }

            public void Dispose()
            {
                FileStream s = Interlocked.Exchange(ref _stream, null);
                s?.Dispose();
            }
        }
    }
}
