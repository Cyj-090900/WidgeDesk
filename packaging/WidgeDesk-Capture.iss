; User-scope x64 installer for WidgeDesk.
;
; Requires Inno Setup 7. SetupArchitecture and the x64os architecture identifiers below are
; Inno Setup 7 syntax; Inno Setup 6 rejects SetupArchitecture with "Unrecognized [Setup]
; section directive". The GitHub workflow downloads Inno Setup 7 from its official release
; rather than using Chocolatey, which only carries version 6.
;
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#if AppIdValue != "WidgeDesk.Independent"
  #error This installer must use the standard WidgeDesk registration.
#endif
#ifndef ProductVersion
  #error ProductVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageBaseName
  #error PackageBaseName is required.
#endif
#define StageDirectory GetEnv("WIDGEDESK_PACKAGE_STAGE")
#if StageDirectory == ""
  #error WIDGEDESK_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=WidgeDesk
AppVersion={#ProductVersion}
AppVerName=WidgeDesk {#ProductVersion}
DefaultDirName={localappdata}\Programs\WidgeDesk
DefaultGroupName=WidgeDesk
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
; Windows 10 2004 is the floor: WDA_EXCLUDEFROMCAPTURE (self-exclusion) starts there.
MinVersion=10.0.19041
UninstallDisplayName=WidgeDesk
UninstallDisplayIcon={app}\WidgeDesk.exe
SetupIconFile=..\src\WidgeDesk.App\Assets\WidgeDesk.ico
AppPublisher=WidgeDesk
VersionInfoProductName=WidgeDesk
VersionInfoProductVersion={#FileVersion}
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#FileVersion}
VersionInfoProductTextVersion={#ProductVersion}
OutputBaseFilename={#PackageBaseName}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Excludes: "Data\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDirectory}\WidgeDesk.ico"; DestDir: "{app}"; DestName: "WidgeDesk-{#ProductVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WidgeDesk"; Filename: "{app}\WidgeDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\WidgeDesk-{#ProductVersion}.ico"
Name: "{autodesktop}\WidgeDesk"; Filename: "{app}\WidgeDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\WidgeDesk-{#ProductVersion}.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\WidgeDesk.exe"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: IsWidgeDeskUpdate

[Code]
function IsWidgeDeskUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WIDGEDESKUPDATE|0}') = '1';
end;
