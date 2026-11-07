namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Every player-facing string the launcher shows, in one place. Texts marked <c>// FIXED</c> are
    /// owner-approved and used verbatim; texts marked <c>// DRAFT</c> were written during the build
    /// and are awaiting review. Error messages live in <see cref="ErrorMessages"/>, which reads its
    /// texts from here too.
    /// </summary>
    internal static class UiText
    {
        // ---- Fixed texts (owner-approved, verbatim) ----

        // FIXED W1
        public const string WelcomeIntro = "DreamWeave Launcher sets up Asheron's Call on this PC and connects you to the DreamWeave server.";

        // FIXED W2
        public const string WelcomeSources = "It downloads the original game installer from the Internet Archive and the final game data files from a community upload on mega.nz. Nothing is downloaded until you choose it on the next screens.";

        // FIXED T1
        public const string TelemetryOptIn = "Send anonymous setup diagnostics (step, result and error code only - no account names, passwords or file paths). Off by default.";

        // FIXED D1
        public const string Disclaimer = "Not affiliated with WB Games or Turbine.";

        // FIXED C1
        public const string ClientExeMismatch = "Your acclient.exe does not match the expected version. The game data step below replaces it with the correct one, so you usually do not need to reinstall the client.";

        // FIXED H1
        public const string InstallerHandoff = "The Asheron's Call installer is now open. Keep the default folder, click through to the end, and untick any option to launch the game or check for updates. This window continues automatically when the install is detected.";

        // FIXED E1
        public const string MegaQuota = "mega.nz has temporarily limited downloads from your connection. This is a mega.nz limit, not a problem with your PC. Wait a while (usually under an hour) and click Retry.";

        // FIXED L1
        public const string FirstTimeHelp = "New to DreamWeave? Type the account name and password you want. The server creates your account the first time you log in. A typo creates a different new account, so type carefully. Account names are not case-sensitive.";

        // ---- Buttons and labels ----

        public const string Next = "Next"; // DRAFT
        public const string Back = "Back"; // DRAFT
        public const string Cancel = "Cancel"; // DRAFT
        public const string Retry = "Retry"; // DRAFT
        public const string TryLater = "Try later"; // DRAFT
        public const string CopyDetails = "Copy details"; // DRAFT
        public const string DetailsCopied = "Details copied to the clipboard."; // DRAFT
        public const string Browse = "Browse..."; // DRAFT
        public const string Privacy = "Privacy"; // DRAFT
        public const string Close = "Close"; // DRAFT
        public const string Play = "Play"; // DRAFT
        public const string Starting = "Starting..."; // DRAFT
        public const string Settings = "Settings"; // DRAFT
        public const string Update = "Update"; // DRAFT
        public const string Later = "Later"; // DRAFT
        public const string IFinished = "I finished"; // DRAFT

        // ---- Wizard ----

        public const string WelcomeTitle = "Welcome"; // DRAFT
        public const string LocationTitle = "Game folder"; // DRAFT
        public const string LocationIntro = "Where is Asheron's Call installed on this PC? If it is not installed yet, keep the default folder."; // DRAFT
        public const string LocationFound = "Asheron's Call was found in this folder."; // DRAFT
        public const string LocationNotInstalled = "Asheron's Call is not installed in this folder yet. It will be installed here."; // DRAFT
        public const string LocationRejectedFormat = "This folder cannot be used because {0}. Choose a folder on a local drive."; // DRAFT
        public const string LocationPickFolder = "Choose the Asheron's Call folder (the one that contains acclient.exe)."; // DRAFT
        public const string FreeSpaceOkFormat = "Free space: {0} available, about {1} needed."; // DRAFT
        public const string FreeSpaceLowFormat = "Low disk space: {0} available, but about {1} is needed. Free up space before continuing."; // DRAFT
        public const string FreeSpaceSplitFormat = "Free space: {0} available for downloads (about {1} needed), {2} in the game folder (about {3} needed)."; // DRAFT
        public const string FreeSpaceUnknown = "The free space on this drive could not be checked."; // DRAFT
        public const string Checking = "Checking your game files..."; // DRAFT
        public const string ComponentsTitle = "What to install"; // DRAFT
        public const string DownloadedFromFormat = "Downloaded from: {0}"; // DRAFT (prefix wording fixed by spec)
        public const string StatusDone = "Done"; // DRAFT
        public const string StatusNeedsRepair = "Needs repair"; // DRAFT
        public const string StatusWillDownload = "Will download"; // DRAFT
        public const string StatusWillApply = "Will apply"; // DRAFT
        public const string StatusNotSelected = "Not selected"; // DRAFT
        public const string StatusNotApplicable = "Not needed now"; // DRAFT
        public const string StatusUnknown = "Could not be checked (a file may be in use)"; // DRAFT
        public const string SizeSmall = "a few bytes"; // DRAFT
        public const string RequiredNotSelectedFormat = "\"{0}\" is required. Tick it to continue."; // DRAFT
        public const string ProgressTitle = "Installing"; // DRAFT
        public const string StepPending = "Waiting"; // DRAFT
        public const string StepRunning = "In progress"; // DRAFT
        public const string StepDone = "Done"; // DRAFT
        public const string StepSkipped = "Nothing to do"; // DRAFT
        public const string StepFailed = "Failed"; // DRAFT
        public const string ProgressBytesFormat = "{0} of {1}"; // DRAFT
        public const string ProgressSpeedFormat = "{0}/s"; // DRAFT
        public const string ProgressEtaFormat = "about {0} left"; // DRAFT
        public const string Verifying = "Checking the downloaded file..."; // DRAFT
        public const string Installing = "Installing..."; // DRAFT
        public const string AskingPermission = "Windows will ask for permission to write to the game folder."; // DRAFT
        public const string HandoffNotDetected = "The install has not been detected yet. Finish the installer, then click \"I finished\" again."; // DRAFT
        public const string CancelledResumable = "Stopped. Downloads already started will continue where they left off next time."; // DRAFT
        public const string DoneTitle = "All set"; // DRAFT
        public const string DoneBody = "Asheron's Call is ready. Click Next to log in."; // DRAFT

        // ---- Launcher window ----

        public const string AccountLabel = "Account"; // DRAFT
        public const string PasswordLabel = "Password"; // DRAFT
        public const string RememberPassword = "Remember password"; // DRAFT
        public const string FirstTimeLink = "First time?"; // DRAFT
        public const string UpdateOptionalFormat = "A new launcher version ({0}) is available."; // DRAFT
        public const string UpdateMandatoryFormat = "This launcher must be updated to version {0} before you can play."; // DRAFT
        public const string UpdateUnavailable = "This launcher is too old to play on the server and no update could be found. Download the launcher again from the DreamWeave website."; // DRAFT
        public const string Updating = "Updating the launcher..."; // DRAFT
        public const string GameStarted = "Asheron's Call is starting."; // DRAFT

        // ---- Settings ----

        public const string SettingsTitle = "Settings"; // DRAFT
        public const string SettingsFolder = "Asheron's Call folder"; // DRAFT
        public const string SettingsVerify = "Verify files"; // DRAFT
        public const string SettingsVerifyOk = "All game files are up to date."; // DRAFT
        public const string SettingsTelemetry = "Send anonymous setup diagnostics"; // DRAFT
        public const string SettingsCheckUpdates = "Check for updates"; // DRAFT
        public const string SettingsUpToDate = "This launcher is up to date."; // DRAFT
        public const string SettingsUninstall = "Uninstall DreamWeave Launcher..."; // DRAFT
        public const string SettingsVersionFormat = "Version {0}"; // DRAFT
        public const string SettingsFolderNoClient = "That folder does not contain acclient.exe."; // DRAFT

        // ---- Start-up and self-install ----

        public const string Loading = "Getting the latest download list..."; // DRAFT
        public const string SelfInstallPrompt = "Install DreamWeave Launcher on this PC? It adds a Start menu shortcut and an entry in Apps and Features, and needs no administrator rights."; // DRAFT
        public const string SelfInstallFailed = "The launcher could not be installed. You can keep using it from where it is."; // DRAFT
        public const string SelfInstallStartFailed = "DreamWeave Launcher was installed but could not be started. Open it from the Start menu."; // DRAFT
        public const string NeedsNewerLauncher = "This launcher is too old to install the current game files. Update the launcher to continue."; // DRAFT

        // ---- Uninstall ----

        public const string UninstallTitle = "Uninstall DreamWeave Launcher"; // DRAFT
        public const string UninstallConfirm = "Remove DreamWeave Launcher, its settings and its saved passwords from this PC?"; // DRAFT
        public const string UninstallAlsoClient = "Also uninstall the Asheron's Call client"; // FIXED (spec wording)
        public const string UninstallButton = "Uninstall"; // DRAFT
        public const string UninstallRunning = "Uninstalling..."; // DRAFT
        public const string UninstallDone = "DreamWeave Launcher has been removed."; // DRAFT
        public const string UninstallLeftOver = "Some items could not be removed:"; // DRAFT
        public const string UninstallClientNotFound = "The Asheron's Call client's uninstaller was not found, so the client was left in place."; // DRAFT
        public const string UninstallClientDeclined = "The Asheron's Call client was left in place because permission was declined."; // DRAFT
        public const string AlreadyRunningBusy = "Close the open DreamWeave Launcher window first, then try again."; // DRAFT
        public const string StateLockedAtStart = "Another copy of the launcher is busy with its settings. Close it and try again."; // DRAFT
    }
}
