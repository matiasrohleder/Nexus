; Inno Setup script for the Nexus Windows installer.
; Built by packaging/windows/build.ps1, which passes AppVersion, SourceDir, OutputDir.
; Per-user install by default (no admin prompt); the wizard offers "install for all users".

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{EC1F3EC5-44B9-4F20-AD9F-51693392F658}
AppName=Nexus
AppVersion={#AppVersion}
AppVerName=Nexus {#AppVersion}
AppPublisher=Matías D. Rohleder
AppPublisherURL=https://github.com/matiasrohleder/Nexus
AppSupportURL=https://github.com/matiasrohleder/Nexus/issues
AppUpdatesURL=https://github.com/matiasrohleder/Nexus/releases
DefaultDirName={autopf}\Nexus
DefaultGroupName=Nexus
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Nexus-{#AppVersion}-windows-x64-setup
SetupIconFile=..\..\Assets\nexus.ico
UninstallDisplayIcon={app}\Nexus.exe
UninstallDisplayName=Nexus
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Nexus
VersionInfoDescription=Nexus Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades: remove the previous version's files so no stale DLLs linger.
; User data lives in %APPDATA%\Nexus and the Credential Manager — never touched.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Nexus"; Filename: "{app}\Nexus.exe"
Name: "{autodesktop}\Nexus"; Filename: "{app}\Nexus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Nexus.exe"; Description: "{cm:LaunchProgram,Nexus}"; Flags: nowait postinstall skipifsilent
