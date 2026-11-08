namespace DreamWeave.Launcher.App
{
    /// <summary>
    /// Every player-facing string the launcher shows, in one place. Every text here is owner-approved
    /// unless it is marked <c>// DRAFT</c> (written during the build and awaiting review). Error messages live in <see cref="ErrorMessages"/>, which reads its
    /// texts from here too.
    /// </summary>
    internal static class UiText
    {
        // ---- Fixed texts (owner-approved, verbatim) ----

        // FIXED W1
        public const string WelcomeIntro = "DreamWeave Launcher sets up Asheron's Call on this PC and connects you to the DreamWeave server.";

        // FIXED W2
        public const string WelcomeSources = "It downloads the original game installer and the final game data files from community uploads on mega.nz. The installer also has a backup copy on the Internet Archive. Nothing is downloaded until you choose it on the next screens.";

        // FIXED T1
        public const string TelemetryOptIn = "Send anonymous setup diagnostics (step, result and error code only - no account names, passwords or file paths). Off by default.";

        // FIXED D1
        public const string Disclaimer = "Not affiliated with WB Games or Turbine.";

        // FIXED C1
        public const string ClientExeMismatch = "Your acclient.exe does not match the expected version. The game data step below replaces it with the correct one, so you usually do not need to reinstall the client.";

        // FIXED H1
        public const string InstallerHandoff = "The Asheron's Call installer is now open. Keep the default folder, click through to the end, and untick any option to launch the game or check for updates. This window continues automatically when the install is detected.";

        // FIXED E1
        /// <summary>A download limit that did not come from mega.nz (for example an HTTP 429 from a mirror).</summary>
        public const string SourceBusy = "The download site is limiting downloads right now. Wait a while and click Retry.";

        public const string MegaQuota = "mega.nz has temporarily limited downloads from your connection. This is a mega.nz limit, not a problem with your PC. Wait a while (usually under an hour) and click Retry.";

        // FIXED L1
        public const string FirstTimeHelp = "New to DreamWeave? Type the account name and password you want. The server creates your account the first time you log in. A typo creates a different new account, so type carefully. Account names are not case-sensitive.";

        // ---- Buttons and labels ----

        public const string Next = "Next";
        public const string Back = "Back";
        public const string Cancel = "Cancel";
        public const string Retry = "Retry";
        public const string TryLater = "Try later";
        public const string CopyDetails = "Copy details";
        public const string DetailsCopied = "Details copied to the clipboard.";
        public const string Browse = "Browse...";
        public const string Privacy = "Privacy";
        public const string Close = "Close";
        public const string Play = "Play";
        public const string Starting = "Starting...";
        public const string Settings = "Settings";
        public const string Update = "Update";
        public const string Later = "Later";
        public const string IFinished = "I finished";

        // ---- Wizard ----

        public const string WelcomeTitle = "Welcome to DreamWeave";
        public const string WelcomeNextHeading = "What happens next";
        public const string WelcomeStepFolder = "Choose your Asheron's Call game folder.";
        public const string WelcomeStepDownloads = "Choose which files to download.";
        public const string WelcomeStepLogin = "Log in and play.";
        public const string LocationTitle = "Game folder";
        public const string LocationIntro = "Where is Asheron's Call installed on this PC? If it is not installed yet, keep the default folder.";
        public const string LocationFound = "Asheron's Call was found in this folder.";
        public const string LocationNotInstalled = "Asheron's Call is not installed in this folder yet. It will be installed here.";
        public const string LocationRejectedFormat = "This folder cannot be used because {0}. Choose a folder on a local drive.";
        public const string LocationPickFolder = "Choose the Asheron's Call folder (the one that contains acclient.exe).";
        public const string FreeSpaceOkFormat = "Free space: {0} available, about {1} needed.";
        public const string FreeSpaceLowFormat = "Low disk space: {0} available, but about {1} is needed. Free up space before continuing.";
        public const string FreeSpaceSplitFormat = "Free space: {0} available for downloads (about {1} needed), {2} in the game folder (about {3} needed).";
        public const string FreeSpaceUnknown = "The free space on this drive could not be checked.";
        public const string Checking = "Checking your game files...";
        public const string ComponentsTitle = "What to install";
        public const string DownloadedFromFormat = "Downloaded from: {0}";

        /// <summary>Shown on the progress screen when a download falls back to a mirror ({0} = the mirror's source label).</summary>
        public const string FallbackSourceFormat = "Trying another source: {0}";

        // Progress screen source line while a component downloads ({0} = the source's label).
        public const string DownloadingFromFormat = "Downloading from: {0}";

        // Second line under FallbackSourceFormat ({0} = one of the reasons below, or the failure's own plain description).
        public const string FallbackReasonFormat = "The previous source did not work: {0}";
        public const string FallbackReasonDamaged = "the file it sent was damaged or incomplete.";
        public const string FallbackReasonQuota = "its free download limit has been reached for your connection.";
        public const string FallbackReasonUnusable = "this launcher cannot use that source.";
        public const string FallbackReasonNetwork = "it could not be reached or stopped sending data.";
        public const string FallbackReasonUnavailable = "the file is no longer available there.";
        public const string FallbackReasonUnknown = "it failed.";
        public const string StatusDone = "Done";
        public const string StatusNeedsRepair = "Needs repair";
        public const string StatusWillDownload = "Will download";
        public const string StatusWillApply = "Will apply";
        public const string StatusNotSelected = "Not selected";
        public const string StatusNotApplicable = "Not needed now";
        public const string StatusUnknown = "Could not be checked (a file may be in use)";
        public const string SizeSmall = "under 1 KB";
        public const string RequiredNotSelectedFormat = "\"{0}\" is required. Tick it to continue.";
        public const string ProgressTitle = "Installing";
        public const string StepPending = "Waiting";
        public const string StepRunning = "In progress";
        public const string StepDone = "Done";
        public const string StepSkipped = "Already done";
        public const string StepFailed = "Failed";
        public const string ProgressBytesFormat = "{0} of {1}";
        public const string ProgressSpeedFormat = "{0}/s";
        public const string ProgressEtaFormat = "about {0} left";
        public const string Verifying = "Checking the downloaded file...";

        public const string VerifyingGameFiles = "Checking the installed game files...";

        public const string WaitingForInstallerFiles = "Waiting for the Asheron's Call installer to finish copying files...";
        public const string Installing = "Installing...";
        public const string AskingPermission = "Windows will ask for permission to write to the game folder.";
        public const string HandoffNotDetected = "The install has not been detected yet. Finish the installer, then click \"I finished\" again.";

        public const string StepDownloading = "Downloading";

        public const string StepDownloaded = "Downloaded";

        public const string InstallerStillRunning = "The Asheron's Call installer still seems to be running. Finish the installer, then click \"I finished\".";

        public const string StillRunningFormat = "Still running: {0}";

        public const string CloseGameThenFinished = "Close Asheron's Call, then click \"I finished\".";

        public const string InstallerClosedEarly = "The Asheron's Call installer closed before it finished. Click Retry to run it again.";

        public const string InstallerHoldsGameFile = "A game file is still in use, probably by the Asheron's Call installer. Finish the installer, then click Retry.";
        public const string CancelledResumable = "Stopped. Finished downloads are kept, and the game installer download picks up where it left off next time.";
        public const string DoneTitle = "All set";
        public const string DoneBody = "Asheron's Call is ready. Click Next to log in.";

        // ---- Launcher window ----

        public const string AccountLabel = "Account";
        public const string PasswordLabel = "Password";
        public const string RememberPassword = "Remember password";
        public const string FirstTimeLink = "First time?";
        public const string UpdateOptionalFormat = "A new launcher version ({0}) is available.";
        public const string UpdateMandatoryFormat = "This launcher must be updated to version {0} before you can play.";
        public const string UpdateUnavailable = "This launcher is too old to play on the server and no update could be found. Download the launcher again from the DreamWeave website.";
        public const string Updating = "Updating the launcher...";
        public const string GameStarted = "Asheron's Call is starting.";
        public const string SlotMain = "Main";
        public const string SlotAlt1 = "Alt 1";
        public const string SlotAlt2 = "Alt 2";
        public const string RememberPasswords = "Remember passwords";
        public const string PlayAll = "Play all";
        public const string FillInFirst = "Fill in an account and password first.";
        public const string DuplicateAccount = "This account is already used in another row.";
        public const string NewHereInfo = "New here? There is no sign-up page. Type any account name and password you like and press Play: your first login creates the account. Use the same name and password every time after that.";

        // ---- Header links ----

        public const string LinkWebsite = "Website";
        public const string LinkWiki = "Wiki";
        public const string LinkDiscord = "Discord";
        public const string WebsiteUrl = "https://www.acdreamweave.com";
        public const string WikiUrl = "https://wiki.acdreamweave.com";

        // ---- Settings ----

        public const string SettingsTitle = "Settings";
        public const string SettingsFolder = "Asheron's Call folder";
        public const string SettingsVerify = "Verify files";
        public const string SettingsVerifyOk = "All game files are up to date.";
        public const string SettingsTelemetry = "Send anonymous setup diagnostics";
        public const string SettingsCheckUpdates = "Check for updates";
        public const string SettingsUpToDate = "This launcher is up to date.";
        public const string SettingsUninstall = "Uninstall DreamWeave Launcher...";
        public const string SettingsVersionFormat = "Version {0}";
        public const string SettingsFolderNoClient = "That folder does not contain acclient.exe.";

        // ---- Start-up and self-install ----

        public const string Loading = "Getting the latest download list...";
        public const string SelfInstallPrompt = "Install DreamWeave Launcher on this PC? It adds a Start menu shortcut and an entry in Apps and Features.";
        public const string SelfInstallFailed = "The launcher could not be installed. You can keep using it from where it is.";
        public const string SelfInstallStartFailed = "DreamWeave Launcher was installed but could not be started. Open it from the Start menu.";
        public const string NeedsNewerLauncher = "This launcher is too old to install the current game files. Update the launcher to continue.";

        // ---- Uninstall ----

        public const string UninstallTitle = "Uninstall DreamWeave Launcher";
        public const string UninstallConfirm = "Remove DreamWeave Launcher, its settings and its saved passwords from this PC?";
        public const string UninstallAlsoClient = "Also uninstall the Asheron's Call client"; // FIXED (spec wording)
        public const string UninstallButton = "Uninstall";
        public const string UninstallRunning = "Uninstalling...";
        public const string UninstallDone = "DreamWeave Launcher has been removed.";
        public const string UninstallLeftOver = "Some items could not be removed:";
        public const string UninstallClientNotFound = "The Asheron's Call client's uninstaller was not found, so the client was left in place.";
        public const string UninstallClientDeclined = "The Asheron's Call client was left in place because permission was declined.";
        public const string AlreadyRunningBusy = "Close the open DreamWeave Launcher window first, then try again.";
        public const string StateLockedAtStart = "Another copy of the launcher is busy with its settings. Close it and try again.";

        // ---- Added in the PR #11 review round ----

        public const string StatusUnknownBlockedFormat = "\"{0}\" could not be checked, usually because Asheron's Call is running. Close the game, then click Check again.";
        public const string CheckAgain = "Check again";
        public const string StopInstallPrompt = "Stop the install?";
        public const string CloseBlocked = "The game files are being written right now. Wait for this step to finish before closing.";
        public const string UninstallCredentialsLeft = "Some saved passwords could not be removed. Open Windows Credential Manager and remove entries starting with DreamWeave/.";
        public const string UninstallClientFailed = "The Asheron's Call client's uninstaller did not finish, so the client may still be installed.";
    }
}
