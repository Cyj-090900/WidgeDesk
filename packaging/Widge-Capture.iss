; User-scope x64 installer for Widge.
;
; Requires Inno Setup 7. SetupArchitecture and the x64os architecture identifiers below are
; Inno Setup 7 syntax; Inno Setup 6 rejects SetupArchitecture with "Unrecognized [Setup]
; section directive". The GitHub workflow downloads Inno Setup 7 from its official release
; rather than using Chocolatey, which only carries version 6.
;
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#if AppIdValue != "Widge.Independent"
  #error This installer must use the standard Widge registration.
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
#define StageDirectory GetEnv("WIDGE_PACKAGE_STAGE")
#if StageDirectory == ""
  #error WIDGE_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=Widge
AppVersion={#ProductVersion}
AppVerName=Widge {#ProductVersion}
DefaultDirName={localappdata}\Programs\Widge
DefaultGroupName=Widge
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
UninstallDisplayName=Widge
UninstallDisplayIcon={app}\Widge.exe
SetupIconFile=..\src\Widge.App\Assets\Widge.ico
AppPublisher=Widge
VersionInfoProductName=Widge
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
Source: "{#StageDirectory}\Widge.ico"; DestDir: "{app}"; DestName: "Widge-{#ProductVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Widge"; Filename: "{app}\Widge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Widge-{#ProductVersion}.ico"
Name: "{autodesktop}\Widge"; Filename: "{app}\Widge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Widge-{#ProductVersion}.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Widge.exe"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: IsWidgeUpdate

[Code]
function IsWidgeUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WIDGEUPDATE|0}') = '1';
end;
