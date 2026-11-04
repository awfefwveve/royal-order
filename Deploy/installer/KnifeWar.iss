; ROYAL ORDER (ASTREL) — 처음 설치 파일 (Inno Setup 6, 무료: https://jrsoftware.org/isinfo.php)
; 1) 유니티 메뉴 KnifeWar > 배포 > 출시 빌드 + 패치 만들기
; 2) 보통은 유니티 메뉴 "KnifeWar > 배포 > 한 번에"가 이 파일을 자동으로 컴파일해서 올린다 (Inno Setup 설치 필요)
; 관리자 권한 없이 사용자 폴더(%LocalAppData%\Programs)에 설치 → 게임이 스스로 업데이트 파일을 덮어쓸 수 있다.
; 브랜드가 정해지면 MyPublisher·MyName·AppId(새 GUID)·아이콘을 바꾼다.

#define MyName "ROYAL ORDER"
#define MyPublisher "ASTREL"
; 유니티 배포 메뉴가 /DMyVersion=버전 으로 넘겨준다
#ifndef MyVersion
  #define MyVersion "0.1.1"
#endif
#define MyExe "KnifeWar.exe"

[Setup]
AppId={{9B62733B-15A3-40A7-B393-8133DFC86CEA}
AppName={#MyName}
AppVersion={#MyVersion}
AppPublisher={#MyPublisher}
DefaultDirName={localappdata}\Programs\ASTREL\RoyalOrder
DefaultGroupName={#MyName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=RoyalOrderSetup-{#MyVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyExe}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\..\Builds\Release\{#MyVersion}\game\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyName}"; Filename: "{app}\{#MyExe}"
Name: "{userdesktop}\{#MyName}"; Filename: "{app}\{#MyExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyExe}"; Description: "{cm:LaunchProgram,{#MyName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\_update"
