namespace DreamWeave.Launcher.Core.Downloads
{
    /// <summary>A verified download: the file at <see cref="Path"/> has exactly the expected size and SHA-256.</summary>
    public sealed class DownloadResult
    {
        /// <summary>Creates a result.</summary>
        public DownloadResult(string path, long size, string sha256, bool alreadyPresent)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
            AlreadyPresent = alreadyPresent;
        }

        /// <summary>Final path of the verified file.</summary>
        public string Path { get; }

        /// <summary>Length in bytes.</summary>
        public long Size { get; }

        /// <summary>SHA-256 as 64 uppercase hex characters (computed by <see cref="Hasher"/>).</summary>
        public string Sha256 { get; }

        /// <summary>True when the verified file was already in place and nothing was downloaded.</summary>
        public bool AlreadyPresent { get; }
    }
}
