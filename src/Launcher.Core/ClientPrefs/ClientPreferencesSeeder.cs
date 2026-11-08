using System;
using System.IO;
using System.Text;

namespace DreamWeave.Launcher.Core.ClientPrefs
{
    /// <summary>How a <see cref="ClientPreferencesSeeder.Seed"/> call ended.</summary>
    public enum ClientPreferencesSeedStatus
    {
        /// <summary>No preferences file was there, so the windowed default was written.</summary>
        Seeded,

        /// <summary>A preferences file already existed and was left untouched.</summary>
        AlreadyPresent,

        /// <summary>The default could not be written (an IO or permission failure); nothing was changed.</summary>
        Failed,
    }

    /// <summary>The outcome of one seed attempt.</summary>
    public sealed class ClientPreferencesSeedResult
    {
        private ClientPreferencesSeedResult(ClientPreferencesSeedStatus status, string targetPath, Exception error)
        {
            Status = status;
            TargetPath = targetPath;
            Error = error;
        }

        /// <summary>What happened.</summary>
        public ClientPreferencesSeedStatus Status { get; }

        /// <summary>The preferences file the seeder computed, or null when the target could not be determined.</summary>
        public string TargetPath { get; }

        /// <summary>The failure when <see cref="Status"/> is <see cref="ClientPreferencesSeedStatus.Failed"/>, otherwise null.</summary>
        public Exception Error { get; }

        internal static ClientPreferencesSeedResult Seeded(string targetPath) => new ClientPreferencesSeedResult(ClientPreferencesSeedStatus.Seeded, targetPath, null);

        internal static ClientPreferencesSeedResult AlreadyPresent(string targetPath) => new ClientPreferencesSeedResult(ClientPreferencesSeedStatus.AlreadyPresent, targetPath, null);

        internal static ClientPreferencesSeedResult Failed(string targetPath, Exception error) => new ClientPreferencesSeedResult(ClientPreferencesSeedStatus.Failed, targetPath, error);
    }

    /// <summary>
    /// Seeds the Asheron's Call client's own preferences file with a windowed default, but only when
    /// the client has not written one yet. A fresh install defaults to full-screen and often throws a
    /// DirectX error on first launch; writing <c>FullScreen=False</c> ahead of the first start avoids
    /// that. An existing file is never read, overwritten or merged: a returning player's settings are
    /// left exactly as they are.
    /// <para>
    /// The file is the client's own config (<c>UserPreferences.ini</c> under the user's Documents
    /// folder), not a client binary or dat, so this is an ordinary per-user config write. The content
    /// is the full template the client itself produces, verbatim, with <c>[Display] FullScreen=False</c>,
    /// CRLF line endings and no byte-order mark.
    /// </para>
    /// </summary>
    public sealed class ClientPreferencesSeeder
    {
        /// <summary>The subfolder of the user's Documents folder the client keeps its files in.</summary>
        public const string GameFolderName = "Asheron's Call";

        /// <summary>The client's preferences file name.</summary>
        public const string PreferencesFileName = "UserPreferences.ini";

        /// <summary>
        /// The full default preferences file, verbatim as the client writes it, with
        /// <c>FullScreen=False</c> for windowed, CRLF line endings and a trailing CRLF. ASCII only.
        /// </summary>
        internal const string DefaultPreferences =
            "[Render]\r\n" +
            "FieldOfView=90.00\r\n" +
            "SceneryDrawDistance=Medium\r\n" +
            "DisplayAdapter=0\r\n" +
            "LandscapeDetailTextures=False\r\n" +
            "ScreenBrightness=0.00\r\n" +
            "LandscapeTextureDetail=Medium\r\n" +
            "AspectRatio=Normal\r\n" +
            "TextureFiltering=Bilinear\r\n" +
            "LandscapeDrawDistance=Medium\r\n" +
            "GraphicsPerformance=0.00\r\n" +
            "EnvironmentTextureDetail=Medium\r\n" +
            "AutomaticDegrades=True\r\n" +
            "DegradeDistance=50.00\r\n" +
            "MultiPassAlpha=False\r\n" +
            "BuildingDetailTextures=False\r\n" +
            "[Net]\r\n" +
            "ComputeUniquePort=False\r\n" +
            "BindInterface=\r\n" +
            "UserSpecifiedPort=0\r\n" +
            "[Sound]\r\n" +
            "SoundFeatures=Stereo\r\n" +
            "AmbientSoundVolume=1.00\r\n" +
            "PlaySoundOnlyWhenActive=True\r\n" +
            "SoundDisabled=False\r\n" +
            "InterfaceSoundVolume=1.00\r\n" +
            "AmbientSoundDisabled=True\r\n" +
            "InterfaceSoundDisabled=True\r\n" +
            "SoundVolume=1.00\r\n" +
            "[Input]\r\n" +
            "InvertMouseLookYAxis=False\r\n" +
            "KeymapFile=acclient.keymap\r\n" +
            "UseMouseTurning=False\r\n" +
            "MouseLookSmoothingAmount=0.00\r\n" +
            "MouseLookSensitivity=0.25\r\n" +
            "[Camera]\r\n" +
            "AdjustmentSpeed=40.00\r\n" +
            "Stiffness=0.45\r\n" +
            "AlignToSlope=True\r\n" +
            "[Misc]\r\n" +
            "TooltipDelay=0.25\r\n" +
            "TooltipEnable=True\r\n" +
            "[UI]\r\n" +
            "ChatFontSize=Small\r\n" +
            "ChatFontFace=PalatinoLinotype\r\n" +
            "[Display]\r\n" +
            "RefreshRate=Auto\r\n" +
            "Resolution=1024x768\r\n" +
            "FullScreen=False\r\n" +
            "SyncToRefresh=False\r\n" +
            "[International]\r\n" +
            "UseIME=False\r\n";

        private readonly string _documentsFolder;
        private readonly IClientPreferencesFileSystem _fileSystem;
        private readonly Action<string> _log;

        /// <summary>
        /// Creates a seeder.
        /// </summary>
        /// <param name="documentsFolder">The user's Documents folder (production:
        /// <c>Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)</c>); tests pass a temp folder.</param>
        /// <param name="fileSystem">The file-system seam; the real one by default.</param>
        /// <param name="log">Receives one launcher.log line about the outcome (never throws, never credentials).</param>
        public ClientPreferencesSeeder(string documentsFolder, IClientPreferencesFileSystem fileSystem = null, Action<string> log = null)
        {
            _documentsFolder = documentsFolder;
            _fileSystem = fileSystem ?? new ClientPreferencesFileSystem();
            _log = log ?? (line => { });
        }

        /// <summary>The bytes written when the file is absent: the template in ASCII, CRLF, no byte-order mark.</summary>
        internal static byte[] DefaultPreferenceBytes()
        {
            // Encoding.ASCII emits no preamble, so there is never a byte-order mark.
            return Encoding.ASCII.GetBytes(DefaultPreferences);
        }

        /// <summary>
        /// The preferences file this seeder targets
        /// (<c>&lt;Documents&gt;\Asheron's Call\UserPreferences.ini</c>), or null when the Documents folder
        /// is not known.
        /// </summary>
        public string TargetPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_documentsFolder))
                {
                    return null;
                }

                return Path.Combine(_documentsFolder, GameFolderName, PreferencesFileName);
            }
        }

        /// <summary>
        /// Writes the windowed default when, and only when, no preferences file exists yet. Never
        /// overwrites or merges an existing file, and never throws: an IO or permission failure is
        /// reported as <see cref="ClientPreferencesSeedStatus.Failed"/> with nothing changed.
        /// </summary>
        public ClientPreferencesSeedResult Seed()
        {
            // target is computed INSIDE the try: Path.Combine can throw on a malformed Documents path,
            // and that must become a Failed result, never an exception out of here.
            string target = null;
            try
            {
                target = TargetPath;
                if (target == null)
                {
                    Note("client-prefs skipped: the Documents folder could not be determined");
                    return ClientPreferencesSeedResult.Failed(null, new DirectoryNotFoundException("The Documents folder could not be determined."));
                }

                // Fast path: skip the work when the client already wrote its file. The exclusive create
                // below is the real guard against overwriting, so this check only being a hint is fine.
                if (_fileSystem.FileExists(target))
                {
                    Note("client-prefs already present at " + target);
                    return ClientPreferencesSeedResult.AlreadyPresent(target);
                }

                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                {
                    _fileSystem.CreateDirectory(folder);
                }

                _fileSystem.WriteNew(target, DefaultPreferenceBytes());
                Note("client-prefs seeded windowed default at " + target);
                return ClientPreferencesSeedResult.Seeded(target);
            }
            catch (IOException ex) when (IsFileAlreadyExists(ex))
            {
                // The file appeared between the fast-path check and the exclusive create (a client or a
                // second launcher instance wrote it first). The existing file was NOT touched.
                Note("client-prefs already present (create lost the race) at " + target);
                return ClientPreferencesSeedResult.AlreadyPresent(target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is ArgumentException || ex is PathTooLongException)
            {
                Note("client-prefs failed type=" + ex.GetType().Name);
                return ClientPreferencesSeedResult.Failed(target, ex);
            }
        }

        private const int ErrorFileExists = 80;
        private const int ErrorAlreadyExists = 183;

        /// <summary>True when <paramref name="ex"/> is the "the file already exists" IOException an exclusive create throws.</summary>
        private static bool IsFileAlreadyExists(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == ErrorFileExists || code == ErrorAlreadyExists;
        }

        private void Note(string text)
        {
            try
            {
                _log(text);
            }
            catch (Exception)
            {
                // Logging never fails a launch.
            }
        }
    }
}
