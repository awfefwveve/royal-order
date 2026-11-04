@echo off
chcp 65001 >nul
rem ASTREL 계정 서버를 이 PC에서 켭니다. api.astrelgames.com 은 Cloudflare Tunnel 로 이 PC(127.0.0.1:8080)에 연결됩니다.
rem 이 창을 닫으면 서버가 꺼집니다.
cd /d "%~dp0"
echo [1/2] 최신 코드로 빌드 중...
dotnet publish src -c Release -o build --nologo -v q
if errorlevel 1 ( echo 빌드 실패. 이 창을 캡처해서 Claude에게 보내 주세요. & pause & exit /b 1 )
rem 진짜 비밀값이 든 server\appsettings.json 은 덮어쓰지 않고, 프로그램 파일만 바꿉니다.
for %%f in (KnifeWar.Backend.dll KnifeWar.Backend.pdb KnifeWar.Backend.deps.json KnifeWar.Backend.runtimeconfig.json) do copy /y "build\%%f" "server\%%f" >nul
echo [2/2] 서버 켜는 중... (이 창은 켜 두세요)
set KW_DEV_AUTH=0
cd server
dotnet KnifeWar.Backend.dll appsettings.json
pause
