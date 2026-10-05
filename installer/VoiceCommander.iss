; Inno Setup 6 script for Voice Commander. Build with scripts\publish.ps1 (it passes /DAppVersion=...).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\target\release\publish"
#endif
#ifndef OutDir
  #define OutDir "..\target\release\bundle"
#endif

[Setup]
AppId={{6B0C4C7E-6A3B-4F5E-9C58-7D4C1E2B9A10}
AppName=Voice Commander
AppVersion={#AppVersion}
AppPublisher=Voice Commander
AppCopyright=Copyright (c) Voice Commander contributors
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Voice Commander
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Voice Commander Setup
UninstallDisplayName=Voice Commander
DefaultDirName={autopf}\Voice Commander
DefaultGroupName=Voice Commander
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=VoiceCommander-{#AppVersion}-Setup
SetupIconFile=..\src\VoiceCommander.App\Resources\app.ico
UninstallDisplayIcon={app}\VoiceCommander.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Voice Commander"; Filename: "{app}\VoiceCommander.exe"
Name: "{autodesktop}\Voice Commander"; Filename: "{app}\VoiceCommander.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VoiceCommander.exe"; Description: "Launch Voice Commander"; Flags: nowait postinstall skipifsilent

; User data (%APPDATA%\VoiceCommander: commands, settings, history, downloaded speech models) is intentionally
; kept on uninstall. The "start with Windows" entry is HKCU\...\Run and is removed by the app when it is switched off.
[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VoiceCommander.exe"; Flags: runhidden; RunOnceId: "KillApp"
