; Beacon Setup, made with Inno Setup 6. Build it with installer\build-installer.ps1, which
; publishes the app and hands this script the folder to pack.
;
; What the setup does: a per-user install (no administrator rights) into
; %LOCALAPPDATA%\Programs\Beacon, a Start Menu entry, an entry in "Installed apps" with an
; uninstaller, and the offer to start Beacon. Beacon itself adds and removes its
; start-with-Windows entry, as its "startWithWindows" setting says.
;
; Beacon is not packed with .NET. It needs Microsoft's .NET 10 Desktop Runtime (x64). When that
; is missing, the setup asks before it does anything, and with a yes downloads the runtime from
; Microsoft and runs Microsoft's own installer for it. That is the only connection the setup
; ever makes, and nothing of the owner's is sent with it.
;
; The owner's settings and data (%LOCALAPPDATA%\Beacon, and the data folder named in
; settings.json) are never touched: not by an install over an older one, not by the uninstaller.

#ifndef SourceDir
  #error SourceDir is not defined. Run installer\build-installer.ps1 instead of compiling this file directly.
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputName
  #define OutputName "BeaconSetup"
#endif

; A test build installs under another identity, so that trying the setup in a throwaway folder
; never touches the entry of a real install in "Installed apps", nor Beacon's own registry values.
#ifdef TestBuild
  #define AppId "{{6E0C1F6E-3E0E-4C39-9A53-7B6B0E1D2F11}"
  #define AppName "Beacon (setup test)"
#else
  #define AppId "{{B5A7C0E2-4D3F-4E7A-9C1B-2F6A8D9E0B47}"
  #define AppName "Beacon"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\Beacon
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
SetupIconFile={#SourcePath}\..\src\Beacon.App\Beacon.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Beacon.exe
; Beacon is asked to quit by its owner (see PrepareToInstall), so that it closes in its own
; way. It is not closed for him.
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[InstallDelete]
; An older install made by copying a published folder had these beside the program.
Type: files; Name: "{app}\*.pdb"

[Tasks]
Name: "desktopicon"; Description: "Put a Beacon icon on the desktop"

[Icons]
; The Start Menu: Beacon, and its uninstaller beside it. And the desktop.
Name: "{group}\{#AppName}"; Filename: "{app}\Beacon.exe"; WorkingDir: "{app}"; Comment: "Keyboard launcher"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Comment: "Remove Beacon from this PC"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Beacon.exe"; WorkingDir: "{app}"; Comment: "Keyboard launcher"; Tasks: desktopicon

[Run]
Filename: "{app}\Beacon.exe"; WorkingDir: "{app}"; Description: "Start Beacon now"; Flags: nowait postinstall skipifsilent
; An update that Beacon started itself: the new Beacon is started without asking, in the place
; of the one that quit for it.
Filename: "{app}\Beacon.exe"; WorkingDir: "{app}"; Flags: nowait; Check: StartedByBeacon

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RuntimePage = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  RuntimeFile = 'windowsdesktop-runtime-win-x64.exe';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ErrorReportingKey = 'Software\Microsoft\Windows\Windows Error Reporting\ExcludedApplications';

var
  DownloadPage: TDownloadWizardPage;

// True when a .NET 10 Desktop Runtime for x64 is on this PC: Microsoft's installer puts each
// version into a folder of its own under Program Files\dotnet\shared.
function DesktopRuntimeInstalled(): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

// True while the file cannot be opened for writing, which is so while the program is running.
// (Renaming would not tell: Windows lets a running program's file be renamed.)
function InUse(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not FileExists(FileName) then
    Exit;
  try
    Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

// True when Beacon itself started this setup, to update itself: it says so with /BEACONUPDATE=1.
// Beacon checks the setup program against a signed list before it starts it, and quits right
// after it has.
function StartedByBeacon(): Boolean;
begin
  Result := ExpandConstant('{param:BEACONUPDATE|0}') = '1';
end;

// Waits for a Beacon that is quitting for its own update, for up to a minute. False when it is
// still running after that.
function WaitedForQuit(const FileName: String): Boolean;
var
  Tries: Integer;
begin
  Tries := 0;
  while InUse(FileName) and (Tries < 120) do
  begin
    Sleep(500);
    Tries := Tries + 1;
  end;
  Result := not InUse(FileName);
end;

// Asks the owner to quit Beacon for as long as it is running. False when he would rather stop.
function AskedToQuit(const FileName: String): Boolean;
begin
  Result := True;
  while InUse(FileName) do
  begin
    if SuppressibleMsgBox('Beacon is running.' + #13#10#13#10 +
      'Quit it first: right-click its icon in the notification area (by the clock) and choose Quit. Then press OK.',
      mbInformation, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage('Getting the .NET Desktop Runtime',
    'Setup is downloading Microsoft''s .NET 10 Desktop Runtime, which Beacon needs.', nil);
end;

// Downloads Microsoft's installer for the runtime and runs it. That installer asks Windows for
// administrator rights by itself. True when the runtime is there afterwards.
function InstallDesktopRuntime(): Boolean;
var
  ExitCode: Integer;
begin
  Result := False;
  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, RuntimeFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  if ShellExec('', ExpandConstant('{tmp}\' + RuntimeFile), '/install /passive /norestart', '',
    SW_SHOW, ewWaitUntilTerminated, ExitCode) then
    Result := DesktopRuntimeInstalled();
end;

// Before anything is installed: the runtime Beacon needs. Asked on the last page before the
// install begins, so that saying no leaves this PC exactly as it was.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Opened: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DesktopRuntimeInstalled() then
    Exit;

  case SuppressibleMsgBox(
    'Beacon needs Microsoft''s .NET 10 Desktop Runtime (x64), which is not installed on this PC.' + #13#10#13#10 +
    'Download it from Microsoft now (about 60 MB) and install it? Setup connects to Microsoft for this download and for nothing else.' + #13#10#13#10 +
    'Yes: download and install it, then install Beacon.' + #13#10 +
    'No: open Microsoft''s download page and stop here. Install the runtime yourself, then run this setup again.',
    mbConfirmation, MB_YESNOCANCEL, IDCANCEL) of
    IDYES:
      begin
        Result := InstallDesktopRuntime();
        if not Result then
          SuppressibleMsgBox('The .NET Desktop Runtime was not installed, so Beacon has not been installed. Nothing on this PC was changed by this setup.',
            mbError, MB_OK, IDOK);
      end;
    IDNO:
      begin
        ShellExec('open', RuntimePage, '', '', SW_SHOWNORMAL, ewNoWait, Opened);
        Result := False;
      end;
  else
    Result := False;
  end;
end;

// An install over a Beacon that is running: its files cannot be replaced until it has quit.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if StartedByBeacon() then
  begin
    // Nobody is there to ask: Beacon is closing by itself, and is waited for.
    if not WaitedForQuit(ExpandConstant('{app}\Beacon.exe')) then
      Result := 'Beacon is still running, so it was not replaced. Nothing was changed.';
  end
  else if not AskedToQuit(ExpandConstant('{app}\Beacon.exe')) then
    Result := 'Beacon is still running, so it was not replaced. Nothing was changed.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := AskedToQuit(ExpandConstant('{app}\Beacon.exe'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
#ifndef TestBuild
var
  Started: String;
#endif
begin
#ifndef TestBuild
  if CurUninstallStep = usUninstall then
  begin
    // What Beacon wrote for itself while it ran: its start-with-Windows entry, when it is the
    // entry of this install and not of another copy, and its name on the list of programs that
    // Windows leaves out of error reporting.
    if RegQueryStringValue(HKCU, RunKey, 'Beacon', Started)
      and (Pos(Lowercase(ExpandConstant('{app}\')), Lowercase(Started)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'Beacon');
    RegDeleteValue(HKCU, ErrorReportingKey, 'Beacon.exe');
  end;
#endif

  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox('Beacon has been removed.' + #13#10#13#10 +
      'Your settings and data were left where they are: ' + ExpandConstant('{localappdata}\Beacon') +
      ', and the data folder named in settings.json if you named one. Delete them yourself if you no longer want them.',
      mbInformation, MB_OK, IDOK);
end;
