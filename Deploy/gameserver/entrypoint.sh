#!/bin/sh
# 환경 변수 → 게임 서버 실행 인자.
# 오케스트레이터(Edgegap 등)가 공개 IP·포트를 환경 변수로 넣어주면 그 값을 쓴다.
#   KW_BACKEND      백엔드 주소 (필수)
#   KW_SECRET       백엔드 ServerSecret (필수)
#   KW_REGION       리전 id (kr, jp, sg, use, usw, euw, euc)
#   KW_PUBLIC_ADDR  클라가 접속할 공개 주소 (없으면 Edgegap의 ARBITRIUM_PUBLIC_IP)
#   KW_PUBLIC_PORT  클라가 접속할 공개 포트 (없으면 7777)
#   KW_PORT         컨테이너 안 포트 (기본 7777)
#   KW_TICK         서버 틱 64 또는 128 (기본 64)
set -e
PORT="${KW_PORT:-7777}"
TICK="${KW_TICK:-64}"
ADDR="${KW_PUBLIC_ADDR:-${ARBITRIUM_PUBLIC_IP:-127.0.0.1}}"
REGION="${KW_REGION:-kr}"

if [ -z "$KW_BACKEND" ] || [ -z "$KW_SECRET" ]; then
  echo "KW_BACKEND, KW_SECRET 환경 변수가 필요합니다" >&2
  exit 1
fi

exec /app/KnifeWarServer.x86_64 -batchmode -nographics -logFile - \
  -kw "server $PORT backend $KW_BACKEND secret $KW_SECRET region $REGION address $ADDR tick $TICK"
