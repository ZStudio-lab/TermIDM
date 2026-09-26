#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be supplied by build-installer.ps1
#endif
#ifndef AssetDir
  #error AssetDir must be supplied by build-installer.ps1
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{8D5C4D4B-1397-42BB-A8B1-5E8638832287}
AppName=TermIDM
AppVersion={#AppVersion}
AppPublisher=zstudio-lab
AppPublisherURL=https://zstudio-lab.github.io/TermIDM/
AppSupportURL=https://github.com/zstudio-lab/TermIDM/issues
AppUpdatesURL=https://github.com/zstudio-lab/TermIDM/releases/latest
DefaultDirName={localappdata}\Programs\TermIDM
DefaultGroupName=TermIDM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#AssetDir}\termidm.ico
UninstallDisplayIcon={app}\TermIDM.exe
UninstallDisplayName=TermIDM
LicenseFile={#ProjectRoot}\LICENSE
OutputDir=.
OutputBaseFilename=TermIDM-Setup-v{#AppVersion}-Windows-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoCompany=zstudio-lab
VersionInfoProductName=TermIDM
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=TermIDM Windows download manager setup
VersionInfoCopyright=Copyright (C) 2026 zstudio-lab

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#ReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TermIDM"; Filename: "{app}\TermIDM.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\TermIDM"; Filename: "{app}\TermIDM.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TermIDM.exe"; Description: "Launch TermIDM"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
