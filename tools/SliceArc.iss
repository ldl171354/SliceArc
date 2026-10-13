; SliceArc Inno Setup script
; Produces a single installer EXE that installs SliceArc to %LOCALAPPDATA%\Programs\SliceArc,
; registers right-click context menu (HKCU), and provides an uninstaller.
; No admin rights required.

#define MyAppVersion "0.4.2"
#define MyAppName "超速解压缩"
#define MyAppExeName "SliceArc.App.exe"
#define MyAppPublisher "LD"

[Setup]
AppId={{7F3A1C2E-9D4B-4E6F-A8C1-2B3D4E5F6071}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Install dir keeps the internal "SliceArc" name so existing installs upgrade in place.
DefaultDirName={localappdata}\Programs\SliceArc
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=SliceArcSetup-{#MyAppVersion}-x64
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
SetupIconFile=..\src\SliceArc.App\Assets\SliceArc.ico
WizardStyle=modern

[Files]
Source: "..\dist\portable\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Windows 11 modern context menu: COM extension (framework-dependent) +
; pre-signed sparse MSIX + its public certificate. Registered by --register.
Source: "..\dist\portable\SliceArc.ShellExt.comhost.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\portable\SliceArc.ShellExt.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\portable\SliceArc.ShellExt.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\portable\SliceArc.ShellExt.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\portable\SliceArc.ContextMenu.msix"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\portable\slicearc-sparse.cer"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register"; Flags: runhidden

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister"; Flags: runhidden

[UninstallDelete]
Type: files; Name: "{app}\SliceArc.settings.json"
Type: files; Name: "{app}\SliceArc.log"
