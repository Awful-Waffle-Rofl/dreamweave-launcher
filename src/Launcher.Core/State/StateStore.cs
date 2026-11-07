using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace DreamWeave.Launcher.Core.State
{
    /// <summary>
    /// Thread-safe owner of <see cref="LauncherState"/> and its JSON file. Every
    /// <see cref="Update"/> rewrites the file atomically (see <see cref="AtomicFile"/>).
    /// A missing file starts fresh; an unreadable (corrupt) file is renamed aside to
    /// <c>state.json.corrupt-&lt;utc ticks&gt;</c> and the store starts fresh.
    /// </summary>
    public sealed class StateStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private readonly string _path;
        private LauncherState _state;

        /// <summary>
        /// Loads (or creates) the state file at <paramref name="stateFilePath"/>. On first run a
        /// new <see cref="LauncherState.InstallId"/> is generated and persisted immediately.
        /// </summary>
        /// <exception cref="LauncherException">When the file cannot be written.</exception>
        public StateStore(string stateFilePath)
        {
            _path = Path.GetFullPath(stateFilePath);
            _state = LoadOrNew(_path, out bool needsSave);
            if (_state.InstallId == Guid.Empty)
            {
                _state.InstallId = Guid.NewGuid();
                needsSave = true;
            }

            if (needsSave)
            {
                SaveLocked();
            }
        }

        /// <summary>Opens the store at <see cref="AppPaths.StateFile"/>.</summary>
        public static StateStore Open(AppPaths paths)
        {
            return new StateStore(paths.StateFile);
        }

        /// <summary>The full path of the backing file.</summary>
        public string FilePath => _path;

        /// <summary>Highest manifest serial accepted so far.</summary>
        public long HighestManifestSerial => Read(s => s.HighestManifestSerial);

        /// <summary>This install's stable random identifier.</summary>
        public Guid InstallId => Read(s => s.InstallId);

        /// <summary>Reads a value under the store lock. Do not let the state object escape the callback.</summary>
        public T Read<T>(Func<LauncherState, T> reader)
        {
            lock (_gate)
            {
                return reader(_state);
            }
        }

        /// <summary>
        /// Mutates the state under the store lock and persists it atomically. If the write fails
        /// the in-memory state is rolled back to the last persisted version and the exception propagates.
        /// </summary>
        public void Update(Action<LauncherState> mutate)
        {
            lock (_gate)
            {
                string before = Serialize(_state);
                mutate(_state);
                try
                {
                    SaveLocked();
                }
                catch
                {
                    _state = Deserialize(before);
                    throw;
                }
            }
        }

        private void SaveLocked()
        {
            AtomicFile.WriteAllBytes(_path, Utf8NoBom.GetBytes(Serialize(_state)));
        }

        private static LauncherState LoadOrNew(string path, out bool needsSave)
        {
            needsSave = false;
            if (!File.Exists(path))
            {
                needsSave = true;
                return new LauncherState();
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw LauncherException.FromIo(ex, "reading launcher state");
            }

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
                // The atomic save below replaces it anyway.
            }

            needsSave = true;
            return new LauncherState();
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
    }
}
