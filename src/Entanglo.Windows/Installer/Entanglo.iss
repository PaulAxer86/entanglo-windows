; Inno Setup script for Entanglo (Windows)
; Build with:  iscc Entanglo.iss
; Output:      Entanglo-Setup-<version>.exe

#define MyAppName       "Entanglo"
#define MyAppVersion    "0.1.60"
#define MyAppPublisher  "Paolo Asara"
#define MyAppURL        "https://entanglo.pages.dev"
#define MyAppExeName    "Entanglo.Windows.exe"
#define PublishDir      "..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{B3C9F2A1-7E4D-4B8F-9A12-A1B2C3D4E5F6}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Entanglo
DefaultGroupName=Entanglo
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=Entanglo-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\Entanglo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=Entanglo.Windows.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Avvia Entanglo all'accesso a Windows"; GroupDescription: "Opzioni di avvio:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Entanglo"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Entanglo"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\Entanglo"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Force-kill any running instance before copying files.
  // CloseApplications=yes tries a graceful close first; this is the hard fallback.
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM Entanglo.Windows.exe 2>nul', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1200);
  Result := '';
end;

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillEntanglo"
