namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// Failure classification shared by every launcher subsystem. The integer value of each
    /// member is also the telemetry error code, so values are explicit and STABLE: never
    /// renumber or reuse a value; add new members with new numbers. Values are grouped by
    /// decade (1x network, 2x integrity, 3x manifest, 4x local disk, 5x install, 6x patch,
    /// 9x control flow).
    /// </summary>
    public enum LauncherError
    {
        /// <summary>No error.</summary>
        None = 0,

        /// <summary>The network could not be reached (DNS failure, connection refused, timeout).</summary>
        NetworkUnavailable = 10,

        /// <summary>A download source answered with a non-success HTTP status.</summary>
        SourceHttpError = 11,

        /// <summary>A download source refused because of a quota or rate limit (mega.nz transfer quota, HTTP 429).</summary>
        SourceQuotaExceeded = 12,

        /// <summary>A response body was larger than the caller's cap and was abandoned.</summary>
        ResponseTooLarge = 13,

        /// <summary>A file's SHA-256 did not match the value the manifest pins.</summary>
        HashMismatch = 20,

        /// <summary>A file's length did not match the value the manifest pins.</summary>
        SizeMismatch = 21,

        /// <summary>The manifest signature is missing, malformed, from an unknown key, or does not verify.</summary>
        ManifestSignatureInvalid = 30,

        /// <summary>A correctly signed manifest has a lower serial than one already accepted.</summary>
        ManifestRollback = 31,

        /// <summary>
        /// A correctly signed manifest uses a schema this launcher does not understand.
        /// Callers block downloads but still allow launching from the cached manifest.
        /// </summary>
        ManifestSchemaUnsupported = 32,

        /// <summary>No source produced a usable manifest (the cached copy may still have been used).</summary>
        ManifestUnavailable = 33,

        /// <summary>A correctly signed manifest could not be parsed into the schema-1 model.</summary>
        ManifestInvalid = 34,

        /// <summary>The disk is full.</summary>
        DiskFull = 40,

        /// <summary>The operating system denied access to a file or folder.</summary>
        AccessDenied = 41,

        /// <summary>The launcher's state file is held locked by another launcher instance and the wait timed out.</summary>
        StateLocked = 42,

        /// <summary>The player declined the UAC elevation prompt.</summary>
        ElevationDeclined = 50,

        /// <summary>An external installer exited with a failure code.</summary>
        InstallerFailed = 51,

        /// <summary>A byte-patch target did not have the expected input hash or bytes.</summary>
        PatchInputMismatch = 60,

        /// <summary>The operation was cancelled by the player.</summary>
        Cancelled = 90,

        /// <summary>An unclassified failure.</summary>
        Unknown = 99,
    }
}
