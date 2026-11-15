; Whatts setup wizard (Inno Setup 6). Build it with ..\build.ps1, which publishes the app first
; and passes the version in from Whatts.csproj.

#define AppName "Whatts"
#define AppExe "Whatts.exe"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define RuntimeFile "windowsdesktop-runtime-10-win-x64.exe"

[Setup]
AppId={{6F3D2A9C-8E41-4B7A-9C55-2E1D7B4A0F93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; Per-user install: no admin prompt (only the .NET runtime, if missing, asks for one).
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\assets\Whatts.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in (keeps the Win+Shift+W hotkey ready)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart

[Run]
Filename: "{tmp}\{#RuntimeFile}"; Parameters: "/install /passive /norestart"; StatusMsg: "Installing the .NET Desktop Runtime..."; Flags: shellexec waituntilterminated; Check: RuntimeDownloaded
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DownloadPage: TDownloadWizardPage;
  Downloaded: Boolean;

function RuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function RuntimeDownloaded: Boolean;
begin
  Result := Downloaded;
end;

// Ask a running copy to exit cleanly (WM_CLOSE) so files can be replaced and its tray icon goes away.
procedure CloseRunningApp;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Sleep(1000);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

// On "Install": if the .NET 10 Desktop Runtime is missing, offer to download it from Microsoft.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or Downloaded or RuntimeInstalled then
    Exit;
  if SuppressibleMsgBox('{#AppName} needs the Microsoft .NET 10 Desktop Runtime, which isn''t installed.' + #13#10#13#10 +
       'Download it from Microsoft (about 60 MB) and install it now?', mbConfirmation, MB_YESNO, IDYES) <> IDYES then
    Exit;
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Downloaded := True;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Unticking "start when I sign in" on an upgrade should also remove an earlier setting.
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}');
end;

function InitializeUninstall: Boolean;
begin
  CloseRunningApp;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The app's own "Start with Windows" toggle may have set this even if setup didn't.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}');
end;
