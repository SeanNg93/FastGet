; Inno Setup script for FastGet Download Manager.
; Built by build-release.ps1:  ISCC /DAppVersion=1.1.0 /DSourceDir=<publish\app> installer\FastGet.iss

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

#define AppName "FastGet Download Manager"
#define AppExe "FastGet.exe"

[Setup]
AppId={{6F3C2E8A-5B1D-4C7E-9A42-FA57E7D0C1B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SeanNg93
AppPublisherURL=https://github.com/SeanNg93/FastGet
AppSupportURL=https://github.com/SeanNg93/FastGet/issues
AppUpdatesURL=https://github.com/SeanNg93/FastGet/releases
; Per-user install: no admin prompt. Browser integration is per-user (HKCU) as well.
PrivilegesRequired=lowest
DefaultDirName={autopf}\FastGet
DefaultGroupName=FastGet
DisableProgramGroupPage=yes
OutputBaseFilename=FastGet-{#AppVersion}-setup
SetupIconFile=..\assets\FastGet.ico
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app holds this mutex while running; setup asks the user to close it first.
AppMutex=FastGet.DownloadManager
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "browsers"; Description: "Register browser integration (Chrome, Edge, Brave, Firefox)"; GroupDescription: "Integration:"
Name: "startup"; Description: "Start FastGet when I sign in to Windows (runs in the tray)"; GroupDescription: "Integration:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FastGet"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\FastGet browser extensions"; Filename: "{app}\extension"
Name: "{autodesktop}\FastGet"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FastGet"; \
    ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--register-browsers"; StatusMsg: "Registering browser integration..."; \
    Flags: runhidden waituntilterminated; Tasks: browsers
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,FastGet}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\extension"; Description: "Open the browser extension folder"; Flags: shellexec postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-browsers"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowsers"

[UninstallDelete]
; Settings and the download list live in %APPDATA%\FastGet and are kept on purpose; downloaded files are never touched.
Type: filesandordirs; Name: "{app}\extension"
