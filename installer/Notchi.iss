#define MyAppName "Notchi"
#define MyAppExeName "Notchi.exe"

#ifndef MyAppVersion
  #define MyAppVersion "2.1.0"
#endif

#ifndef MyAppVersionNumeric
  #define MyAppVersionNumeric "2.1.0.0"
#endif

[Setup]
AppId={{D3A8B6E2-9F4C-4C1A-9B7E-2F6A1C8D5E10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=risuunava
AppPublisherURL=https://github.com/risuunava/Notchi
AppSupportURL=https://github.com/risuunava/Notchi/issues
AppUpdatesURL=https://github.com/risuunava/Notchi/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\assets\icon.ico
AppMutex=Notchi.SingleInstance
VersionInfoVersion={#MyAppVersionNumeric}
VersionInfoProductName=Notchi
VersionInfoCompany=risuunava
VersionInfoDescription=Notchi installer
OutputDir=..\publish
OutputBaseFilename=NotchiSetup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
