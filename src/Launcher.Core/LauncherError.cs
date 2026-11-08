namespace DreamWeave.Launcher.Core
{
    /// <summary>
    /// Failure classification shared by every launcher subsystem. The integer value of each
    /// member is also the telemetry error code, so values are explicit and STABLE: never
    /// renumber or reuse a value; add new members with new numbers. Values are grouped by
    /// decade (1x network, 2x integrity, 3x manifest, 4x local disk, 5x install, 6x patch,
    /// 7x launch, 9x control flow, 10x launcher update and cleanup).
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

        /// <summary>A user name, password, host, port, or launch template was rejected before it could reach the game client's command line.</summary>
        InvalidCredentials = 70,

        /// <summary>acclient.exe was not found in the game folder (or could not be started because it vanished).</summary>
        ClientNotFound = 71,

        /// <summary>Windows refused to start acclient.exe for a reason other than access or elevation.</summary>
        LaunchFailed = 72,

        /// <summary>acclient.exe is marked to require administrator rights and the launcher does not run elevated.</summary>
        ClientRequiresElevation = 73,

        /// <summary>The Windows Credential Manager could not be used (API failure or unavailable).</summary>
        CredentialStoreUnavailable = 74,

        /// <summary>
        /// The client started, but the player's Decal could not be loaded into it (it runs without Decal).
        /// Never thrown: it is only recorded as the error of a succeeded <c>launch-decal</c> step.
        /// </summary>
        DecalInjectFailed = 75,

        /// <summary>A Decal launch was asked for, but the player's Decal Inject.dll was not found (or is not Decal's registered one); no client was started.</summary>
        DecalNotFound = 76,

        /// <summary>The Decal launch helper could not be run or gave no usable answer; no client was started by the launcher.</summary>
        DecalHelperFailed = 77,

        /// <summary>The Decal launch helper did not finish in time. The client may still open late, so the player is told to check before trying again.</summary>
        DecalHelperTimeout = 78,

        /// <summary>The operation was cancelled by the player.</summary>
        Cancelled = 90,

        // 8x: install executors (installer runner, zip overlay, byte patcher, elevated copy).

        /// <summary>A file in the game folder is in use (normally a running game client), so nothing was changed.</summary>
        GameRunning = 80,

        /// <summary>The game folder is not writable for the current user; the work must be handed to an elevated helper.</summary>
        ElevationRequired = 81,

        /// <summary>A path under the game folder passes through a junction or symbolic link, so it was not written.</summary>
        ReparsePointInPath = 82,

        /// <summary>A downloaded archive is unusable: an unsafe or duplicate entry name, or a required file is absent.</summary>
        ArchiveInvalid = 83,

        /// <summary>A byte patch produced bytes whose SHA-256 differs from the manifest's output hash; the target was not changed.</summary>
        PatchOutputMismatch = 84,

        /// <summary>A <c>--place-verified</c> request is malformed, or names or stages something the verified manifest does not allow.</summary>
        ElevatedTaskInvalid = 85,

        /// <summary>An unclassified failure.</summary>
        Unknown = 99,

        // 10x: launcher update and cleanup (self-install and self-update are retired; the Update* codes below are never emitted
        // any more, but stay reserved: never reuse these numbers for a new meaning).

        /// <summary>RETIRED, never emitted: a launcher update's Authenticode signature did not verify, or its signer is not the one the manifest names.</summary>
        UpdateSignatureInvalid = 100,

        /// <summary>RETIRED, never emitted: the offered launcher update is not newer than the running launcher (a downgrade or the same version), so it was refused.</summary>
        UpdateNotNewer = 101,

        /// <summary>RETIRED, never emitted: swapping in a launcher update failed; the previous launcher was restored and is unchanged.</summary>
        UpdateSwapFailed = 102,

        /// <summary>RETIRED, never emitted: swapping in a launcher update failed AND restoring the previous launcher failed; the install needs repair (re-download the launcher).</summary>
        UpdateRollbackFailed = 103,

        /// <summary>A version string is not a valid semantic version.</summary>
        VersionInvalid = 104,

        // 105 and 106 are RETIRED with self-install (the launcher no longer writes a shortcut or a registry key).
        // They stay reserved: never reuse these numbers for a new meaning.
        /// <summary>RETIRED, never emitted: the Start-menu shortcut could not be created or removed.</summary>
        ShortcutFailed = 105,

        /// <summary>RETIRED, never emitted: the per-user uninstall registration (HKCU) could not be written or removed.</summary>
        RegistryWriteFailed = 106,

        /// <summary>Uninstall finished but could not remove everything; the result lists what was left behind.</summary>
        UninstallIncomplete = 107,

        /// <summary>Installed game files no longer match after the install finished (another installer was probably still writing them).</summary>
        GameFilesChanged = 108,

        /// <summary>A game file was still in use when the game data step ran right after the game installer: the installer was probably still running.</summary>
        InstallerStillRunning = 109,

        /// <summary>The game installer this session started was closed (or aborted) before the install was detected.</summary>
        InstallerClosedEarly = 110,
    }
}
