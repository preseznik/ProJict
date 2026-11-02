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

[InstallDelete]
Type: files; Name: "{app}\ProJict.exe.old-*"

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
// Copying starts: ProJict.exe is moved aside (a running one can be renamed, not replaced), so nothing starts ProJict while
// its other files are replaced; the new ProJict.exe comes last. The moved one goes at the end, or with the next install
// ([InstallDelete]) if something still runs it.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe, Aside: String;
begin
  Exe := ExpandConstant('{app}\ProJict.exe');
  if CurStep = ssInstall then
  begin
    Aside := Exe + '.old-' + GetDateTimeString('yyyymmddhhnnss', #0, #0);
    if FileExists(Exe) then RenameFile(Exe, Aside);
  end
  else if CurStep = ssPostInstall then
    DelTree(ExpandConstant('{app}\ProJict.exe.old-*'), False, True, False);
end;
