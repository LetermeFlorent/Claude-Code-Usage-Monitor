; Inno Setup script — Claude Usage Monitor (native build)
#define AppName "Claude Usage Monitor"
#define AppVersion "1.0.0"
#define AppPublisher "perso"
#define AppExe "ClaudeUsageMonitorNative.exe"
#define SrcExe "native\target\release\ClaudeUsageMonitorNative.exe"

[Setup]
AppId={{B7E1B5A1-2C4D-4F6A-9E3B-1A2B3C4D5E6F}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=C:\Users\perso\Downloads
OutputBaseFilename=ClaudeUsageMonitor-Setup
SetupIconFile=app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le bureau"; GroupDescription: "Raccourcis:"
Name: "startup"; Description: "Démarrer avec Windows"; GroupDescription: "Démarrage:"

[Files]
Source: "{#SrcExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ClaudeUsageMonitor"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Lancer {#AppName}"; Flags: nowait postinstall skipifsilent
