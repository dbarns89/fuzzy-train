; Inno Setup script. Build the app first:
;   dotnet publish src/VcxDiag.App -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -o artifacts/x86
[Setup]
AppId={{6B0E0C55-6D0B-4E0E-9C55-6F1A5B6E2A11}
AppName=VCX Diag
AppVersion=1.0.0
AppPublisher=VCX Diag contributors
DefaultDirName={autopf}\VCX Diag
DefaultGroupName=VCX Diag
DisableProgramGroupPage=yes
OutputBaseFilename=VcxDiag-Setup
OutputDir=Output
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\src\VcxDiag.App\app.ico
UninstallDisplayIcon={app}\VcxDiag.exe
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: checkedonce

[Files]
Source: "..\artifacts\x86\VcxDiag.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\VCX Diag"; Filename: "{app}\VcxDiag.exe"
Name: "{autodesktop}\VCX Diag"; Filename: "{app}\VcxDiag.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VcxDiag.exe"; Description: "Start VCX Diag"; Flags: nowait postinstall skipifsilent
