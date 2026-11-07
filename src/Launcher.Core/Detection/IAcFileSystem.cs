using System;
using System.IO;

namespace DreamWeave.Launcher.Core.Detection
{
    /// <summary>Size and last-write time of a file at one moment.</summary>
    public sealed class FileStamp
    {
        /// <summary>Creates a stamp.</summary>
        public FileStamp(long size, DateTime lastWriteUtc)
        {
            Size = size;
            LastWriteUtc = lastWriteUtc;
        }

        /// <summary>Length in bytes.</summary>
        public long Size { get; }

        /// <summary>Last-write time, UTC.</summary>
        public DateTime LastWriteUtc { get; }
    }

    /// <summary>
    /// The read-only slice of the file system that detection and verification need. Nothing here
    /// can write, so detection can never modify the game folder.
    /// </summary>
    public interface IAcFileSystem
    {
        /// <summary>True when a directory exists at <paramref name="path"/>.</summary>
        bool DirectoryExists(string path);

        /// <summary>True when a file exists at <paramref name="path"/>.</summary>
        bool FileExists(string path);

        /// <summary>The file's stamp, or null when no such file exists. May throw IO exceptions for other failures.</summary>
        FileStamp GetStamp(string path);

        /// <summary>Opens the file for sequential reading without blocking anyone else (a running game client keeps working).</summary>
        Stream OpenRead(string path);
    }

    /// <summary><see cref="IAcFileSystem"/> over <see cref="System.IO"/>.</summary>
    public sealed class RealAcFileSystem : IAcFileSystem
    {
        /// <inheritdoc />
        public bool DirectoryExists(string path)
        {
            return Directory.Exists(path);
        }

        /// <inheritdoc />
        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        /// <inheritdoc />
        public FileStamp GetStamp(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new FileStamp(info.Length, info.LastWriteTimeUtc);
        }

        /// <inheritdoc />
        public Stream OpenRead(string path)
        {
            // ReadWrite|Delete: the owner's running clients must be unaffected, and we must not fail
            // just because a client has the file open.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        }
    }
}
