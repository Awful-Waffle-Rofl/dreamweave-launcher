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

        /// <summary>
        /// Creates <paramref name="path"/> and writes exactly <paramref name="contents"/> to it, with no
        /// byte-order mark. The create is exclusive: when the file already exists it throws an
        /// <see cref="IOException"/> (ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS) and the existing file is
        /// never opened or truncated, so an existing file can never be overwritten here.
        /// </summary>
        void WriteNew(string path, byte[] contents);
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
        public void WriteNew(string path, byte[] contents)
        {
            // FileMode.CreateNew never opens or truncates an existing file: it throws
            // ERROR_FILE_EXISTS, so the never-overwrite guarantee is enforced by the OS, not by a
            // prior existence check that could race. The bytes are written verbatim (no encoding, no BOM).
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush();
            }
        }
    }
}
