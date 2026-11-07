; Inno Setup script for ProJict. Built by build.ps1, which passes /DAppVersion=x.y.z.
; /DTestInstall builds a test installer instead: its own AppId and name, nothing in the registry, no shortcuts, so it
; can be run into a scratch folder (/DIR=) without touching the real install; /DTestData=<folder> is its ProJict's data.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define PublishDir "..\artifacts\publish"

[Setup]
#ifdef TestInstall
AppId={{6B1E4D2A-58C7-4F0E-9A43-3C5D7E9F1A2B}
AppName=ProJict Test
CreateUninstallRegKey=no
Uninstallable=no
UsePreviousAppDir=no
#else
AppId={{F0C7FC25-C930-40BB-A60D-DB6F340B133C}
AppName=ProJict
#endif
AppVersion={#AppVersion}
AppVerName=ProJict {#AppVersion}
AppPublisher=preseznik
AppPublisherURL=https://github.com/preseznik/projict
AppSupportURL=https://github.com/preseznik/projict/issues
AppUpdatesURL=https://github.com/preseznik/projict/releases
VersionInfoVersion={#AppVersion}
; Per-user install: no admin rights needed, and updates install without prompts.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ProJict
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
#ifdef TestInstall
OutputBaseFilename=ProJict-Setup-Test-{#AppVersion}
#else
OutputBaseFilename=ProJict-Setup-{#AppVersion}
#endif
SetupIconFile=..\src\ProJict.App\Assets\ProJict.ico
UninstallDisplayIcon={app}\ProJict.exe
UninstallDisplayName=ProJict
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; While Setup runs, nothing starts ProJict (ProJict.exe, its old --mcp and --watch-chat, ProJictBridge.exe check this
; mutex: SetupLock in ProJict.Core), so its files can be replaced; Setup starts ProJict itself at the end.
SetupMutex=ProJictSetupRunning
; Also closes what doesn't answer when asked: agents' windowless ProJict.exe --mcp and --watch-chat (they hold no data).
CloseApplications=force
; Every ProJict process runs ProJict.exe, so watching it (not all ~400 files) finds them all, and fast even while old
; agents keep starting it.
CloseApplicationsFilter=ProJict.exe,ProJictBridge.exe
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; Excludes: "\ProJict.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; ProJict.exe last: the old one is moved aside when copying starts (CurStepChanged), so meanwhile there's nothing to start
; (agents set up the old way keep trying) and nothing loads half-replaced files.
Source: "{#PublishDir}\ProJict.exe"; DestDir: "{app}"; Flags: ignoreversion

; Older ProJict.exe.old-* (from an install that couldn't delete its own) go in CurStepChanged, before this one's is made:
; an [InstallDelete] entry would also delete the one just moved aside, so a failed install couldn't put it back.

[UninstallDelete]
Type: files; Name: "{app}\ProJict.exe.old-*"

#ifndef TestInstall
[Icons]
Name: "{autoprograms}\ProJict"; Filename: "{app}\ProJict.exe"
Name: "{autodesktop}\ProJict"; Filename: "{app}\ProJict.exe"; Tasks: desktopicon

[Run]
; --after-setup: Setup still holds its lock for a moment, which keeps other starts out.
Filename: "{app}\ProJict.exe"; Parameters: "--after-setup"; Description: "{cm:LaunchProgram,ProJict}"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer silently; start ProJict again afterwards.
Filename: "{app}\ProJict.exe"; Parameters: "--after-setup"; Flags: nowait; Check: WizardSilent
#else
  #ifdef TestData
[Run]
Filename: "{app}\ProJict.exe"; Parameters: "--after-setup --background --data ""{#TestData}"""; Flags: nowait
  #endif
#endif

[Code]
const
  SYNCHRONIZE = $00100000;
  WAIT_TIMEOUT = $00000102;

function OpenProcess(Access: Cardinal; Inherit: Boolean; Pid: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  // ProJict.exe moved aside when copying started ('' when there was none), and whether the new one is in place.
  Aside: String;
  Installed: Boolean;

// Whether a ProJict window process runs (not agents' windowless --mcp or --watch-chat ones, which Setup closes itself).
function MainProJictRunning(): Boolean;
var
  Locator, Service, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''ProJict.exe'' AND NOT CommandLine LIKE ''%--mcp%'' AND NOT CommandLine LIKE ''%--watch-chat%''');
    Result := Found.Count > 0;
  except
    Result := False;
  end;
end;

// An in-app update runs Setup silently while ProJict is still saving and quitting. Setup holds its lock from the start
// (nothing starts ProJict again), then waits here until ProJict has exited, before anything tries to close or replace its
// files (#2a293aff: closing it while it was still saving failed, and Setup gave up). Newer ProJicts pass /WAITPID=<their
// process>; older ones don't, so a silent Setup also waits for ProJict's window process to go.
function InitializeSetup(): Boolean;
var
  Pid, Waited: Integer;
  Process: THandle;
begin
  Result := True;
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid > 0 then
  begin
    Process := OpenProcess(SYNCHRONIZE, False, Pid);
    if Process <> 0 then
    begin
      if WaitForSingleObject(Process, 60000) = WAIT_TIMEOUT then
        Log('ProJict (process ' + IntToStr(Pid) + ') was still running after 60 seconds; closing it the usual way.');
      CloseHandle(Process);
    end;
  end;
#ifndef TestInstall
  // (A test install runs beside the real ProJict, so it doesn't wait for it.)
  if WizardSilent() then
  begin
    Waited := 0;
    while MainProJictRunning() and (Waited < 60000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
    if Waited > 0 then Log('Waited ' + IntToStr(Waited) + ' ms for ProJict to exit.');
  end;
#endif
end;

// Copying starts: ProJict.exe is moved aside (a running one can be renamed, not replaced), so nothing starts ProJict while
// its other files are replaced; the new ProJict.exe comes last. The moved one goes at the end, or with the next install if
// something still runs it.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\ProJict.exe');
  if CurStep = ssInstall then
  begin
    DelTree(ExpandConstant('{app}\ProJict.exe.old-*'), False, True, False);
    Aside := '';
    if FileExists(Exe) then
    begin
      Aside := Exe + '.old-' + GetDateTimeString('yyyymmddhhnnss', #0, #0);
      if not RenameFile(Exe, Aside) then Aside := '';
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    Installed := True;
    DelTree(ExpandConstant('{app}\ProJict.exe.old-*'), False, True, False);
  end;
end;

// Setup ends. When it didn't finish (a file in use, cancelled, an error), the files it replaced are rolled back, but not
// ProJict.exe moved aside by hand: it's put back, so a failed update never leaves ProJict uninstalled (#2a293aff). An
// in-app update starts that ProJict again, as the finished one would have been.
procedure DeinitializeSetup();
var
  Exe: String;
  Code: Integer;
begin
  if Installed then Exit;
  Exe := ExpandConstant('{app}\ProJict.exe');
  if (Aside <> '') and not FileExists(Exe) and FileExists(Aside) then
  begin
    if RenameFile(Aside, Exe) then Log('Setup did not finish: put the previous ProJict.exe back.')
    else Log('Setup did not finish, and the previous ProJict.exe could not be put back from ' + Aside + '.');
  end;
#ifndef TestInstall
  if WizardSilent() and FileExists(Exe) and not MainProJictRunning() then
    Exec(Exe, '--after-setup', '', SW_SHOWNORMAL, ewNoWait, Code);
#endif
end;
