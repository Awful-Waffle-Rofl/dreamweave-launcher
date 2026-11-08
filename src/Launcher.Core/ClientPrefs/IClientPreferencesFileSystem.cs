using System.IO;

namespace DreamWeave.Launcher.Core.ClientPrefs
{
    /// <summary>
    /// The small slice of the file system <see cref="ClientPreferencesSeeder"/> needs, behind an
    /// interface so tests can point it at a temp folder or inject a failure without touching the real
    /// disk. Methods throw the raw <see cref="IOException"/> / <see cref="System.UnauthorizedAccessException"/>;
    /// the seeder classifies them and never lets one reach the UI.
    /// </summary>
    public interface IClientPreferencesFileSystem
    {
        /// <summary>True when a file exists at <paramref name="path"/>.</summary>
        bool FileExists(string path);

        /// <summary>Creates <paramref name="path"/> and any missing parents. No-op when it already exists.</summary>
        void CreateDirectory(string path);

        /// <summary>Writes exactly <paramref name="contents"/> to <paramref name="path"/>, creating or overwriting it, with no byte-order mark.</summary>
        void WriteAllBytes(string path, byte[] contents);
    }

    /// <summary><see cref="IClientPreferencesFileSystem"/> over <see cref="System.IO"/>.</summary>
    public sealed class ClientPreferencesFileSystem : IClientPreferencesFileSystem
    {
        /// <inheritdoc />
        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        /// <inheritdoc />
        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        /// <inheritdoc />
        public void WriteAllBytes(string path, byte[] contents)
        {
            // File.WriteAllBytes writes the array verbatim: no encoding, no byte-order mark.
            File.WriteAllBytes(path, contents);
        }
    }
}
