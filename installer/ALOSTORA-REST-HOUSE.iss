#define AppName "ALOSTORA REST HOUSE"
#define AppVersion "1.0.0"
#define PublishDir GetEnv("ALOSTORA_PUBLISH_DIR")
#define OutputDir GetEnv("ALOSTORA_OUTPUT_DIR")

[Setup]
AppId={{A91B8C4E-3E1D-4F90-9E13-0A057E5A0001}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ALOSTORA
SetupIconFile=..\src\Alostora.RestHouse\Assets\alostora.ico
DefaultDirName={autopf}\ALOSTORA REST HOUSE
DefaultGroupName=ALOSTORA REST HOUSE
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=ALOSTORA-REST-HOUSE-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Alostora.RestHouse.exe
SetupLogging=yes

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Dirs]
Name: "{commonappdata}\AlostoraRestHouse"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Install-ALOSTORA-REST-HOUSE.ps1,Uninstall-ALOSTORA-REST-HOUSE.ps1"

[Icons]
Name: "{autoprograms}\ALOSTORA REST HOUSE"; Filename: "{app}\Alostora.RestHouse.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ALOSTORA REST HOUSE"; Filename: "{app}\Alostora.RestHouse.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Alostora.RestHouse.exe"; Description: "تشغيل ALOSTORA REST HOUSE الآن"; Flags: postinstall nowait skipifsilent unchecked
