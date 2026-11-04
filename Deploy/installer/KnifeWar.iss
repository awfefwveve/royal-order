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
; 원클릭에 가깝게: 설치 위치·준비 화면 생략
DisableDirPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
; 게임이 켜져 있으면 끄라고 안내 (게임이 같은 이름의 뮤텍스를 잡고 있음)
AppMutex=ROYALORDER_RUNNING
CloseApplications=yes

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

[Registry]
; 홈페이지 PLAY NOW → royalorder:// 주소로 게임 실행 (게임도 켜질 때 자기 위치로 다시 등록함)
Root: HKCU; Subkey: "Software\Classes\royalorder"; ValueType: string; ValueName: ""; ValueData: "URL:ROYAL ORDER"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\royalorder"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\royalorder\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyExe}"",0"
Root: HKCU; Subkey: "Software\Classes\royalorder\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyExe}"" ""%1"""

[Run]
Filename: "{app}\{#MyExe}"; Description: "{cm:LaunchProgram,{#MyName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\_update"
