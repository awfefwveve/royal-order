# 황실 칼전 데디 서버 (리눅스 헤드리스 빌드)
# 빌드 전: 유니티 메뉴 KnifeWar > 멀티 > 리눅스 데디 서버 빌드 → Builds/LinuxServer/
# 빌드:   docker build -f Deploy/gameserver/Dockerfile -t knifewar-gameserver .   (프로젝트 루트에서)
# 실행:   docker run -p 7777:7777/udp -e KW_BACKEND=https://api.example.com -e KW_SECRET=... -e KW_REGION=kr -e KW_PUBLIC_ADDR=1.2.3.4 knifewar-gameserver
FROM ubuntu:22.04

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates libgl1 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd -m game

WORKDIR /app
COPY Builds/LinuxServer/ /app/
COPY Deploy/gameserver/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh /app/KnifeWarServer.x86_64 && chown -R game /app

USER game
EXPOSE 7777/udp
ENTRYPOINT ["/app/entrypoint.sh"]
