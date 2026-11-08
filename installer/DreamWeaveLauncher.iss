; DreamWeave Launcher per-user installer (Inno Setup 6).
; Build with tools/release/build-setup.ps1, which passes:
;   /DAppVersion=<MAJOR.MINOR.PATCH>   the launcher version (Directory.Build.props)
;   /DExeDir=<folder>                  the folder holding the build output (every "ship" file in installer/shipped-files.txt)
; The AppId, AppMutex and --cleanup/--silent literals below are pinned to
; src/Launcher.Core/SetupConstants.cs by tests/Launcher.Tests/Cleanup/SetupScriptConsistencyTests.cs.

#ifndef AppVersion
  #error AppVersion is not defined. Build with tools/release/build-setup.ps1 (passes /DAppVersion=...).
#endif
#ifndef ExeDir
  #define ExeDir "..\src\Launcher\bin\Release\net48"
#endif

[Setup]
; A doubled opening brace is a literal brace in Inno, so this AppId is {06DC956B-92EB-4FD7-87A7-89635453F9B7}.
AppId={{06DC956B-92EB-4FD7-87A7-89635453F9B7}
AppName=DreamWeave Launcher
AppVersion={#AppVersion}
AppPublisher=DreamWeave
AppPublisherURL=https://www.acdreamweave.com
AppMutex=DreamWeaveLauncherSetupGuard
; Per-user install, never elevated: the launcher started by the Run entry below therefore runs as the player.
PrivilegesRequired=lowest
DefaultDirName={autopf}\DreamWeave Launcher
; Upgrades: reuse the folder of the previous install (Inno's default; stated so a test can pin it).
UsePreviousAppDir=yes
; ONE-CLICK SETUP: no wizard page is ever shown. Only the progress page and two messages appear: .NET missing
; (InitializeSetup in [Code]) and launcher already running (AppMutex). Each page is skipped by its own directive:
;   DisableWelcomePage      the "Welcome to the Setup Wizard" page (Inno's default is already yes)
;   DisableDirPage          "Select Destination Location"; /DIR= on the command line still sets the folder
;   DisableProgramGroupPage "Select Start Menu Folder"
;   DisableReadyPage        "Ready to Install"
;   DisableFinishedPage     "Setup Completed"; Setup closes as soon as the install finishes
;   DisableStartupPrompt    the "This will install... Do you wish to continue?" prompt (Inno's default is already yes)
; Nothing else may add a page: no Tasks section, no InfoBeforeFile, InfoAfterFile or LicenseFile, no custom pages.
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableStartupPrompt=yes
SetupIconFile=..\src\Launcher\Assets\dreamweave.ico
UninstallDisplayIcon={app}\DreamWeaveLauncher.exe
MinVersion=10.0
; Progress window look: modern, forced dark (the launcher is dark). Forced dark ignores colours set from script,
; so the exact launcher palette cannot be used; the logo sits on the style's own dark panel.
WizardStyle=modern dark
; The DreamWeave logo in the progress window header. Several sizes; Setup picks the best fit for the DPI
; (58, 77, 97, 116, 124, 143 and 159 px are the image-area sizes at 100% to 250%).
WizardSmallImageFile=wizard-small-58.png,wizard-small-77.png,wizard-small-97.png,wizard-small-116.png,wizard-small-124.png,wizard-small-143.png,wizard-small-159.png
OutputBaseFilename=DreamWeaveLauncher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=DreamWeave Launcher
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=DreamWeave
VersionInfoDescription=DreamWeave Launcher Setup
VersionInfoCopyright=Copyright (c) 2026 Awful-Waffle-Rofl

[Files]
; One explicit line per file, no wildcards. Kept equal to the "ship" entries of shipped-files.txt by a test.
Source: "{#ExeDir}\DreamWeaveLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExeDir}\DreamWeaveLauncher.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExeDir}\Launcher.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExeDir}\Newtonsoft.Json.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExeDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Both shortcuts are always created: there is no page on which to untick them.
Name: "{autoprograms}\DreamWeave Launcher"; Filename: "{app}\DreamWeaveLauncher.exe"
Name: "{autodesktop}\DreamWeave Launcher"; Filename: "{app}\DreamWeaveLauncher.exe"

[Run]
; Starts the launcher by itself once the files are installed. No postinstall (that flag is a checkbox on the
; Setup Completed page, which is disabled); skipifsilent keeps /SILENT and /VERYSILENT installs headless;
; runasoriginaluser keeps it unelevated.
Filename: "{app}\DreamWeaveLauncher.exe"; Flags: nowait skipifsilent runasoriginaluser

[InstallDelete]
; Legacy migration: the old self-installing launcher's Start menu shortcut.
Type: files; Name: "{userprograms}\DreamWeave Launcher.lnk"
; Leftovers from the retired self-updater (exe renamed to .old/.new). Exact names only: the player may
; install into a folder holding their own files, and a wildcard (or an 8.3 short-name match) would delete them.
Type: files; Name: "{app}\DreamWeaveLauncher.exe.old"
Type: files; Name: "{app}\DreamWeaveLauncher.exe.new"

[Registry]
; Legacy migration: the old self-installing launcher's uninstall entry. The old install folder is the
; same folder as {app}, so the files are simply overwritten.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\DreamWeaveLauncher"; Flags: deletekey

[UninstallDelete]
; Leftovers from the retired self-updater (exe renamed to .old/.new). Exact names only, never a wildcard.
Type: files; Name: "{app}\DreamWeaveLauncher.exe.old"
Type: files; Name: "{app}\DreamWeaveLauncher.exe.new"

[Code]
const
  // .NET Framework 4.8 is Release 528040 or higher.
  Net48Release = 528040;

function IsNet48Installed(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := Release >= Net48Release;
  if (not Result) and IsWin64 then
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
      Result := Release >= Net48Release;
end;

function InitializeSetup(): Boolean;
begin
  Result := IsNet48Installed();
  if not Result then
    // DRAFT: player-facing wording, pending orchestrator review.
    SuppressibleMsgBox('DreamWeave Launcher needs Microsoft .NET Framework 4.8, which was not found on this PC.' + #13#10 + #13#10 +
      'Install it from Windows Update or from microsoft.com/net/download, then run this setup again.',
      mbCriticalError, MB_OK, IDOK);
end;

// Runs the launcher's cleanup and returns its exit code, or -1 when it could not be started.
// A failure never stops the uninstall.
function RunLauncherCleanup(Params: String): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  try
    if Exec(ExpandConstant('{app}\DreamWeaveLauncher.exe'), Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := ResultCode;
      if ResultCode <> 0 then
        Log('Launcher cleanup (' + Params + ') exited with code ' + IntToStr(ResultCode));
    end
    else
      Log('Launcher cleanup did not start: ' + SysErrorMessage(ResultCode));
  except
    Log('Launcher cleanup raised an exception: ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Removes saved passwords and launcher data. The launcher shows its own window unless this is a
    // silent uninstall; the uninstaller waits for it.
    if UninstallSilent() then
      RunLauncherCleanup('--cleanup --silent')
    else if RunLauncherCleanup('--cleanup') = 120 then
      // 120 = the player cancelled the launcher's window. The uninstall goes ahead anyway, so remove the
      // saved passwords and data without a window rather than leave them behind.
      RunLauncherCleanup('--cleanup --silent');
  end;
end;

// Always runs, with or without a directory page, including for a command-line /DIR=. A non-empty result stops
// Setup with that text as the error. The launcher's cleanup deletes the data folder, which would delete the
// uninstaller mid-run, so installing under it is refused.
// DataDirShown is for display; the lowercased copies are only for the comparison.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, DataDirShown, DataDirLower: String;
begin
  Result := '';
  DataDirShown := ExpandConstant('{localappdata}\DreamWeave');
  Dir := Lowercase(RemoveBackslash(WizardDirValue));
  DataDirLower := Lowercase(RemoveBackslash(DataDirShown));
  if (Dir = DataDirLower) or (Pos(DataDirLower + '\', Dir) = 1) then
  begin
    Result := 'Setup cannot install into ' + DataDirShown + '. Run setup again without choosing that folder.';
    Log(Result);
  end;
end;
