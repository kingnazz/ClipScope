; Values are supplied by scripts/package-rc.ps1 from Version.props.
#ifndef Payload
  #error Payload is required
#endif
[Setup]
AppId={#AppId}
AppName=ClipScope
AppVersion={#AppVersion}
AppVerName=ClipScope {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/kingnazz/ClipScope
AppSupportURL=https://github.com/kingnazz/ClipScope/issues
DefaultDirName={localappdata}\Programs\ClipScope
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
SetupMutex=Local\ClipScope.Setup.v1
OutputDir={#Output}
OutputBaseFilename=ClipScope-{#AppVersion}-win-x64-setup
SetupIconFile={#Payload}\Assets\ClipScope.ico
UninstallDisplayIcon={app}\ClipScope.App.exe
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductName=ClipScope
VersionInfoDescription=ClipScope per-user installer
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
TimeStampsInUTC=yes
LicenseFile={#Payload}\licenses\WindowsAppSDK-license.txt

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\ClipScope"; Filename: "{app}\ClipScope.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\ClipScope.ico"; AppUserModelID: "{#AppId}"
Name: "{userdesktop}\ClipScope"; Filename: "{app}\ClipScope.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\ClipScope.ico"; Tasks: desktopicon; AppUserModelID: "{#AppId}"

[Run]
Filename: "{app}\ClipScope.App.exe"; Description: "Launch ClipScope"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  PreviousFiles: TArrayOfString;
  WasStartupEnabled, PurgeUserData: Boolean;

function PostMessageW(Wnd: HWND; Msg: Cardinal; WParam, LParam: LongInt): Boolean;
  external 'PostMessageW@user32.dll stdcall';
function GetWindowThreadProcessId(Wnd: HWND; var ProcessId: Cardinal): Cardinal;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function OpenProcess(Access: Cardinal; Inherit: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function CloseClipScope(): Boolean;
var Wnd: HWND; Pid: Cardinal; Handle: THandle; Attempt: Integer;
begin
  Result := True;
  Wnd := FindWindowByClassName('ClipScope.NativeHost.v1');
  if Wnd = 0 then begin
    if not CheckForMutexes('Local\ClipScope.Instance.v1') then exit;
    for Attempt := 1 to 120 do begin
      Sleep(25);
      Wnd := FindWindowByClassName('ClipScope.NativeHost.v1');
      if Wnd <> 0 then break;
    end;
    if Wnd = 0 then begin Result := False; exit; end;
  end;
  GetWindowThreadProcessId(Wnd, Pid);
  Handle := OpenProcess($00100000, False, Pid);
  if Handle = 0 then begin Result := False; exit; end;
  try
    PostMessageW(Wnd, $8005, 0, 0);
    Result := WaitForSingleObject(Handle, 10000) = 0;
  finally
    CloseHandle(Handle);
  end;
end;

function HasReparsePoint(Path: String): Boolean;
var Parent: String; Attributes: Cardinal;
begin
  Result := False;
  while Path <> '' do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin Result := True; exit; end;
    Parent := ExtractFileDir(Path);
    if Parent = Path then exit;
    Path := Parent;
  end;
end;

function SafeRelativeFile(Relative: String): Boolean;
var Full, Root: String;
begin
  if Relative = '' then begin Result := False; exit; end;
  Root := AddBackslash(ExpandConstant('{app}'));
  Full := ExpandFileName(Root + Relative);
  Result := (Pos(':', Relative) = 0) and
    (Relative[1] <> '\') and (Relative[1] <> '/') and
    (CompareText(Copy(Full, 1, Length(Root)), Root) = 0) and
    not HasReparsePoint(Full);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var StartupValue: String;
begin
  Result := '';
  if HasReparsePoint(ExpandConstant('{app}')) then begin Result := 'The installation directory must not be a junction or symbolic link.'; exit; end;
  if not CloseClipScope() then begin Result := 'Exit ClipScope from its notification-area icon, finish any Settings save, then retry installation.'; exit; end;
  WasStartupEnabled := RegQueryStringValue(HKCU, RunKey, 'ClipScope', StartupValue) and (StartupValue <> '');
  LoadStringsFromFile(ExpandConstant('{app}\install-files.txt'), PreviousFiles);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var CurrentFiles: TArrayOfString; I, J: Integer; Found: Boolean;
begin
  if CurStep <> ssPostInstall then exit;
  if WasStartupEnabled then
    RegWriteStringValue(HKCU, RunKey, 'ClipScope', '"' + ExpandConstant('{app}\ClipScope.App.exe') + '" --background');
  LoadStringsFromFile(ExpandConstant('{app}\install-files.txt'), CurrentFiles);
  { Remove only files owned by the prior payload that no longer ship. Never wipe
    the install directory or touch the separate user-data directory on upgrade. }
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do begin
    Found := False;
    for J := 0 to GetArrayLength(CurrentFiles) - 1 do
      if CompareText(PreviousFiles[I], CurrentFiles[J]) = 0 then Found := True;
    if not Found and SafeRelativeFile(PreviousFiles[I]) then DeleteFile(ExpandConstant('{app}\') + PreviousFiles[I]);
  end;
end;

function TreeContainsReparsePoint(Path: String): Boolean;
var Entry: TFindRec; Child: String;
begin
  Result := HasReparsePoint(Path);
  if Result then exit;
  if FindFirst(AddBackslash(Path) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Child := AddBackslash(Path) + Entry.Name;
          if (Entry.Attributes and $400) <> 0 then begin Result := True; exit; end;
          if (Entry.Attributes and 16) <> 0 then
            if TreeContainsReparsePoint(Child) then begin Result := True; exit; end;
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseClipScope();
  if not Result then begin
    if not UninstallSilent then MsgBox('Exit ClipScope and finish any Settings save before uninstalling.', mbError, MB_OK);
    exit;
  end;
  PurgeUserData := ExpandConstant('{param:PURGEUSERDATA|0}') = '1';
  if not UninstallSilent and not PurgeUserData then
    PurgeUserData := MsgBox('Clipboard history, settings and images are kept by default.' + #13#10 +
      'Permanently delete this user''s ClipScope data as well? This cannot be undone.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var StartupValue, Data: String;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  if RegQueryStringValue(HKCU, RunKey, 'ClipScope', StartupValue) and
    (CompareText(StartupValue, '"' + ExpandConstant('{app}\ClipScope.App.exe') + '" --background') = 0) then
    RegDeleteValue(HKCU, RunKey, 'ClipScope');
  if PurgeUserData then begin
    Data := ExpandConstant('{localappdata}\ClipScope');
    if DirExists(Data) then begin
      if TreeContainsReparsePoint(Data) then
        Log('User-data removal refused: a reparse point was found. Data was preserved.')
      else DelTree(Data, True, True, True);
    end;
  end;
end;
