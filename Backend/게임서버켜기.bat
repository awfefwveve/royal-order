@echo off
rem ROYAL ORDER game server on this PC (custom rooms / matches). Keep this window open.
rem   Double-click        : for playing on this PC only (address 127.0.0.1)
rem   "...friends.bat"    : friends can join over the internet (needs UDP 7777 port forwarding on the router)
cd /d "%~dp0"
set GAME=%LOCALAPPDATA%\Programs\ASTREL\RoyalOrder\KnifeWar.exe
if not exist "%GAME%" ( echo Install ROYAL ORDER from the ASTREL launcher first. & pause & exit /b 1 )
for /f "usebackq delims=" %%s in (`powershell -NoProfile -Command "(Get-Content 'server\appsettings.json' -Raw | ConvertFrom-Json).KnifeWar.ServerSecret"`) do set KWSECRET=%%s
set ADDR=127.0.0.1
if /i "%~1"=="public" for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Invoke-RestMethod https://api.ipify.org"`) do set ADDR=%%i
echo Game server starting: region kr, players connect to %ADDR%:7777
echo Log: %~dp0game_server.log
"%GAME%" -batchmode -nographics -logFile "%~dp0game_server.log" -kw "server 7777 backend http://127.0.0.1:8080 secret %KWSECRET% region kr address %ADDR%"
echo Game server stopped.
pause
