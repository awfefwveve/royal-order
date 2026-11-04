@echo off
rem ASTREL account server on this PC (api.astrelgames.com via Cloudflare Tunnel -> 127.0.0.1:8080)
rem Keep this window open. Closing it stops the server.
cd /d "%~dp0"
if not exist build\KnifeWar.Backend.dll (
  echo Building...
  dotnet publish src -c Release -o build --nologo -v q
  if errorlevel 1 ( echo BUILD FAILED - send a screenshot of this window to Claude & pause & exit /b 1 )
)
rem Only program files are replaced. server\appsettings.json (real secrets) is never touched.
for %%f in (KnifeWar.Backend.dll KnifeWar.Backend.pdb KnifeWar.Backend.deps.json KnifeWar.Backend.runtimeconfig.json) do copy /y "build\%%f" "server\%%f" >nul
if errorlevel 1 ( echo COPY FAILED - close the other server window first, then run this again & pause & exit /b 1 )
set KW_DEV_AUTH=0
cd server
echo Starting ASTREL server...
dotnet KnifeWar.Backend.dll
pause
