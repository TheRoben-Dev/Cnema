; Savefile's Windows installer (Inno Setup 6). Built in CI by .github/workflows/package.yml:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=<published Cnema folder> /DOutputDir=<dist> installer\savefile.iss
;
; It asks whether to install for the current user (no administrator needed, %LOCALAPPDATA%\Programs\Savefile)
; or for everyone (Program Files\Savefile). The app updates itself by running the next installer silently in
; the same mode (UpdateService.RunInstaller), so the AppId below must never change.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumVersion
  #define NumVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Cnema"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{7E6B1C3A-5D2F-4E8B-9A41-2C5D7F1E0B93}
AppName=Savefile
AppVersion={#AppVersion}
AppVerName=Savefile {#AppVersion}
AppPublisher=TheRoben
AppPublisherURL=https://github.com/TheRoben-Dev/Cnema-releases
AppSupportURL=https://github.com/TheRoben-Dev/Cnema-releases
AppUpdatesURL=https://github.com/TheRoben-Dev/Cnema-releases/releases/latest
VersionInfoVersion={#NumVersion}
VersionInfoProductName=Savefile
VersionInfoDescription=Savefile installer
; {autopf} is Program Files for everyone and %LOCALAPPDATA%\Programs for just me.
DefaultDirName={autopf}\Savefile
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Just me or everyone: a choice on the first page, or /CURRENTUSER and /ALLUSERS on the command line.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Savefile-v{#AppVersion}-windows-x64-setup
SetupIconFile=..\src\Cnema\Assets\cnema.ico
UninstallDisplayIcon={app}\Cnema.exe
UninstallDisplayName=Savefile
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; A running Savefile is closed for the install and the uninstall (it lives in the tray, so it's easy to forget).
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\Swedish.isl")
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Folders the app owns completely: cleared first so an update doesn't leave old libraries behind.
Type: filesandordirs; Name: "{app}\libvlc"
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\tools"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Savefile"; Filename: "{app}\Cnema.exe"; Comment: "Some saves you never overwrite."
Name: "{autodesktop}\Savefile"; Filename: "{app}\Cnema.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Cnema.exe"; Description: "{cm:LaunchProgram,Savefile}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ "Start Savefile when I log in" writes a Run value; take it away with the app it points to. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'Cnema', Cmd) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Cmd)) > 0 then
        RegDeleteValue(HKCU, RunKey, 'Cnema');
end;
