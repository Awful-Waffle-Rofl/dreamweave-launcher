; DreamWeave Launcher per-user installer (Inno Setup 6).
; Build with tools/release/build-setup.ps1, which passes:
;   /DAppVersion=<MAJOR.MINOR.PATCH>   the launcher version (Directory.Build.props)
;   /DExeDir=<folder>                  the folder holding DreamWeaveLauncher.exe and .exe.config
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
PrivilegesRequired=lowest
DefaultDirName={autopf}\DreamWeave Launcher
DisableProgramGroupPage=yes
SetupIconFile=..\src\Launcher\Assets\dreamweave.ico
UninstallDisplayIcon={app}\DreamWeaveLauncher.exe
MinVersion=10.0
WizardStyle=modern
InfoBeforeFile=privacy.txt
OutputBaseFilename=DreamWeaveLauncher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=DreamWeave Launcher
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=DreamWeave
VersionInfoDescription=DreamWeave Launcher Setup
VersionInfoCopyright=Copyright (c) 2026 Awful-Waffle-Rofl

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#ExeDir}\DreamWeaveLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ExeDir}\DreamWeaveLauncher.exe.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DreamWeave Launcher"; Filename: "{app}\DreamWeaveLauncher.exe"
Name: "{autodesktop}\DreamWeave Launcher"; Filename: "{app}\DreamWeaveLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DreamWeaveLauncher.exe"; Description: "{cm:LaunchProgram,DreamWeave Launcher}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Legacy migration: the old self-installing launcher's Start menu shortcut.
Type: files; Name: "{userprograms}\DreamWeave Launcher.lnk"

[Registry]
; Legacy migration: the old self-installing launcher's uninstall entry. The old install folder is the
; same folder as {app}, so the files are simply overwritten.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\DreamWeaveLauncher"; Flags: deletekey

[UninstallDelete]
; Left behind by the launcher's self-updater (until it is retired).
Type: files; Name: "{app}\*.old"
Type: files; Name: "{app}\*.new"

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
    MsgBox('DreamWeave Launcher needs Microsoft .NET Framework 4.8, which was not found on this PC.' + #13#10 + #13#10 +
      'Install it from Windows Update or from microsoft.com/net/download, then run this setup again.',
      mbCriticalError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Exe, Params: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Removes saved passwords and launcher data. The launcher shows its own window unless this is a
    // silent uninstall; the uninstaller waits for it. A failure never stops the uninstall.
    Exe := ExpandConstant('{app}\DreamWeaveLauncher.exe');
    Params := '--cleanup';
    if UninstallSilent() then
      Params := Params + ' --silent';
    try
      if not Exec(Exe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        Log('Launcher cleanup did not start: ' + SysErrorMessage(ResultCode))
      else if ResultCode <> 0 then
        Log('Launcher cleanup exited with code ' + IntToStr(ResultCode));
    except
      Log('Launcher cleanup raised an exception: ' + GetExceptionMessage);
    end;
  end;
end;
