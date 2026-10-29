#!/bin/bash
# 서버 묶음 만들기: 리눅스 데디 서버 빌드 + 백엔드 → Builds/royalorder-server.tar.gz
# (백엔드 appsettings의 비밀값은 서버가 켜질 때 새로 만들기 때문에 묶음에는 빈 값으로 들어간다)
set -e
cd "$(dirname "$0")/../.."
test -f Builds/LinuxServer/KnifeWarServer.x86_64 || { echo "리눅스 서버 빌드가 없음"; exit 1; }
rm -rf Builds/_serverpack && mkdir -p Builds/_serverpack/backend
cp -r Builds/LinuxServer Builds/_serverpack/LinuxServer
rm -rf Builds/_serverpack/LinuxServer/*DoNotShip*
cp Backend/server/KnifeWar.Backend.dll Backend/server/KnifeWar.Backend.deps.json Backend/server/KnifeWar.Backend.runtimeconfig.json Builds/_serverpack/backend/
cp Backend/src/appsettings.json Builds/_serverpack/backend/appsettings.json
mkdir -p Builds/_serverpack/backend-src
cp Backend/src/*.cs Backend/src/KnifeWar.Backend.csproj Builds/_serverpack/backend-src/
tar czf Builds/royalorder-server.tar.gz -C Builds/_serverpack .
ls -la Builds/royalorder-server.tar.gz
