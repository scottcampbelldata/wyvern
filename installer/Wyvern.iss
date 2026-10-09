; Wyvern Setup, made with Inno Setup 6. Build it with installer\build-installer.ps1, which
; publishes the app and hands this script the folder to pack.
;
; What the setup does: a per-user install (no administrator rights) into
; %LOCALAPPDATA%\Programs\Wyvern, a Start Menu entry, an entry in "Installed apps" with an
; uninstaller, and the offer to start Wyvern. Wyvern itself adds and removes its
; start-with-Windows entry, as its "startWithWindows" setting says.
;
; Wyvern is not packed with .NET. It needs Microsoft's .NET 10 Desktop Runtime (x64). When that
; is missing, the setup asks before it does anything, and with a yes downloads the runtime from
; Microsoft and runs Microsoft's own installer for it. That is the only connection the setup
; ever makes, and nothing of the owner's is sent with it.
;
; The owner's settings and data (%LOCALAPPDATA%\Wyvern, and the data folder named in
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
  #define OutputName "WyvernSetup"
#endif

; A test build installs under another identity, so that trying the setup in a throwaway folder
; never touches the entry of a real install in "Installed apps", nor Wyvern's own registry values.
#ifdef TestBuild
  #define AppId "{{41F0846C-B926-4E36-A927-FA0CC3F68821}"
  #define AppName "Wyvern (setup test)"
#else
  #define AppId "{{AA755B59-31A3-4A2E-A03E-628073CB4252}"
  #define AppName "Wyvern"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\Wyvern
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
SetupIconFile={#SourcePath}\..\src\Wyvern.App\Wyvern.ico
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
UninstallDisplayIcon={app}\Wyvern.exe
; Wyvern is asked to quit by its owner (see PrepareToInstall), so that it closes in its own
; way. It is not closed for him.
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[InstallDelete]
; An older install made by copying a published folder had these beside the program.
Type: files; Name: "{app}\*.pdb"

[Tasks]
Name: "desktopicon"; Description: "Put a Wyvern icon on the desktop"

[Icons]
; The Start Menu: Wyvern, and its uninstaller beside it. And the desktop.
Name: "{group}\{#AppName}"; Filename: "{app}\Wyvern.exe"; WorkingDir: "{app}"; Comment: "Keyboard launcher"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Comment: "Remove Wyvern from this PC"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Wyvern.exe"; WorkingDir: "{app}"; Comment: "Keyboard launcher"; Tasks: desktopicon

[Run]
Filename: "{app}\Wyvern.exe"; WorkingDir: "{app}"; Description: "Start Wyvern now"; Flags: nowait postinstall skipifsilent
; An update that Wyvern started itself: the new Wyvern is started without asking, in the place
; of the one that quit for it.
Filename: "{app}\Wyvern.exe"; WorkingDir: "{app}"; Flags: nowait; Check: StartedByWyvern

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

// True when Wyvern itself started this setup, to update itself: it says so with /WYVERNUPDATE=1.
// Wyvern checks the setup program against a signed list before it starts it, and quits right
// after it has.
function StartedByWyvern(): Boolean;
begin
  Result := ExpandConstant('{param:WYVERNUPDATE|0}') = '1';
end;

// Waits for a Wyvern that is quitting for its own update, for up to a minute. False when it is
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

// Asks the owner to quit Wyvern for as long as it is running. False when he would rather stop.
function AskedToQuit(const FileName: String): Boolean;
begin
  Result := True;
  while InUse(FileName) do
  begin
    if SuppressibleMsgBox('Wyvern is running.' + #13#10#13#10 +
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
    'Setup is downloading Microsoft''s .NET 10 Desktop Runtime, which Wyvern needs.', nil);
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

// Before anything is installed: the runtime Wyvern needs. Asked on the last page before the
// install begins, so that saying no leaves this PC exactly as it was.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Opened: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DesktopRuntimeInstalled() then
    Exit;

  case SuppressibleMsgBox(
    'Wyvern needs Microsoft''s .NET 10 Desktop Runtime (x64), which is not installed on this PC.' + #13#10#13#10 +
    'Download it from Microsoft now (about 60 MB) and install it? Setup connects to Microsoft for this download and for nothing else.' + #13#10#13#10 +
    'Yes: download and install it, then install Wyvern.' + #13#10 +
    'No: open Microsoft''s download page and stop here. Install the runtime yourself, then run this setup again.',
    mbConfirmation, MB_YESNOCANCEL, IDCANCEL) of
    IDYES:
      begin
        Result := InstallDesktopRuntime();
        if not Result then
          SuppressibleMsgBox('The .NET Desktop Runtime was not installed, so Wyvern has not been installed. Nothing on this PC was changed by this setup.',
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

// An install over a Wyvern that is running: its files cannot be replaced until it has quit.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if StartedByWyvern() then
  begin
    // Nobody is there to ask: Wyvern is closing by itself, and is waited for.
    if not WaitedForQuit(ExpandConstant('{app}\Wyvern.exe')) then
      Result := 'Wyvern is still running, so it was not replaced. Nothing was changed.';
  end
  else if not AskedToQuit(ExpandConstant('{app}\Wyvern.exe')) then
    Result := 'Wyvern is still running, so it was not replaced. Nothing was changed.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := AskedToQuit(ExpandConstant('{app}\Wyvern.exe'));
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
    // What Wyvern wrote for itself while it ran: its start-with-Windows entry, when it is the
    // entry of this install and not of another copy, and its name on the list of programs that
    // Windows leaves out of error reporting.
    if RegQueryStringValue(HKCU, RunKey, 'Wyvern', Started)
      and (Pos(Lowercase(ExpandConstant('{app}\')), Lowercase(Started)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'Wyvern');
    RegDeleteValue(HKCU, ErrorReportingKey, 'Wyvern.exe');
  end;
#endif

  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox('Wyvern has been removed.' + #13#10#13#10 +
      'Your settings and data were left where they are: ' + ExpandConstant('{localappdata}\Wyvern') +
      ', and the data folder named in settings.json if you named one. Delete them yourself if you no longer want them.',
      mbInformation, MB_OK, IDOK);
end;
