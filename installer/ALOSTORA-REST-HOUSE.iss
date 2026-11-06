#define MyAppName "ALOSTORA REST HOUSE"
#define MyAppVersion "1.0.0"
#define MyAppExeName "Alostora.RestHouse.exe"

[Setup]
AppId={{A4B2B5A9-4A7D-4C37-9C6C-202611060001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=ALOSTORA
DefaultDirName={autopf}\ALOSTORA REST HOUSE
DefaultGroupName=ALOSTORA REST HOUSE
OutputDir=..\installer-output
OutputBaseFilename=ALOSTORA-REST-HOUSE-Setup
SetupIconFile=..\src\Alostora.RestHouse\Assets\alostora.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "إنشاء أيقونة على سطح المكتب"; GroupDescription: "اختصارات إضافية:"; Flags: unchecked

[Files]
Source: "..\src\Alostora.RestHouse\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ALOSTORA REST HOUSE"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ALOSTORA REST HOUSE"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "تشغيل ALOSTORA REST HOUSE"; Flags: postinstall nowait skipifsilent
