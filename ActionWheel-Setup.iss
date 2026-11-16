; ==============================================
; Action Wheel - Installer Script for Inno Setup
; ==============================================
; Download Inno Setup: https://jrsoftware.org/isdl.php
; ==============================================

#define MyAppName "Action Wheel"
#ifndef MyAppVersion
#define MyAppVersion "2.8.0"
#endif
#define MyAppPublisher "KoneForge"
#define MyAppURL "https://konexforge.com/"
#define MyAppExeName "Action Wheel.exe"
#ifndef MyAppSourcePath
#define MyAppSourcePath "bin\Publish\win-x64"
#endif

; Build.ps1 passes the exact publish output through /DMyAppSourcePath so an
; installer cannot silently fall back to an unrelated older x64 build.
#if !FileExists(AddBackslash(MyAppSourcePath) + MyAppExeName)
#error "Action Wheel.exe was not found in MyAppSourcePath. Publish the application before building the installer."
#endif

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE.txt
OutputDir=Installer
OutputBaseFilename=ActionWheel-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Run at Windows startup"; GroupDescription: "Other options:"; Flags: unchecked

[Files]
Source: "{#MyAppSourcePath}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourcePath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  if Version.Major < 10 then
  begin
    MsgBox('Action Wheel requires Windows 10 or later.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
