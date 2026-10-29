#!/bin/bash
# ROYAL ORDER test server auto setup (Vultr startup script; pick it when deploying a server)
# Runs once on first boot: install .NET + PostgreSQL, download server bundle, build backend with PostgreSQL,
# generate secrets, start backend + 2 game servers. Destroy the server after testing (test data goes with it).
# (ASCII only: Vultr startup scripts accept Latin1 characters only)
set -e
BUNDLE_URL="https://patch.astrelgames.com/server/royalorder-server.tar.gz"
GAME_SERVERS=2            # matches running at once (12 players each)
TICK=64
exec > /var/log/royalorder-setup.log 2>&1

export DEBIAN_FRONTEND=noninteractive HOME=/root DOTNET_CLI_HOME=/root DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
apt-get update
apt-get install -y dotnet-sdk-8.0 postgresql curl python3 ca-certificates

mkdir -p /opt/royalorder && cd /opt/royalorder
curl -fsSL "$BUNDLE_URL" -o bundle.tar.gz
tar xzf bundle.tar.gz && rm bundle.tar.gz
chmod +x LinuxServer/KnifeWarServer.x86_64

# Database: same PostgreSQL as production (later: switch connection string to a managed DB)
DB_PASS=$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 32)
sudo -u postgres psql -c "create user royal with password '$DB_PASS';"
sudo -u postgres psql -c "create database royalorder owner royal;"

# Build backend with PostgreSQL support (packages are downloaded on the server)
dotnet publish backend-src/KnifeWar.Backend.csproj -c Release -p:UsePostgres=true -o backend-pg
cp backend/appsettings.json backend-pg/appsettings.json
rm -rf backend && mv backend-pg backend
useradd -r -s /usr/sbin/nologin royal || true
chown -R royal:royal /opt/royalorder

PUBLIC_IP=$(curl -fsS http://169.254.169.254/latest/meta-data/public-ipv4 || curl -fsS https://api.ipify.org)
TOKEN_SECRET=$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)
SERVER_SECRET=$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)
ADMIN_KEY=$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 24)

# Backend config: kr ping URL -> this server, test matchmaking (fill with bots after 20s)
python3 - "$PUBLIC_IP" <<'PY'
import json, sys
ip = sys.argv[1]
p = '/opt/royalorder/backend/appsettings.json'
c = json.load(open(p, encoding='utf-8'))
k = c['KnifeWar']
k['TokenSecret'] = ''; k['ServerSecret'] = ''   # provided via environment
k['DataDir'] = '/opt/royalorder/data'
for r in k['Regions']:
    r['PingUrl'] = f'http://{ip}:8080/ping' if r['Id'] == 'kr' else ''
k['Matchmaking'].update({'MinHumansToStart': 1, 'FillWithBotsAfterSeconds': 20})
json.dump(c, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY

cat > /etc/royalorder.env <<ENV
KW_URLS=http://+:8080
KW_TOKEN_SECRET=$TOKEN_SECRET
KW_SERVER_SECRET=$SERVER_SECRET
KW_ADMIN_KEY=$ADMIN_KEY
KW_DB=Host=127.0.0.1;Database=royalorder;Username=royal;Password=$DB_PASS
DOTNET_CLI_HOME=/tmp
ENV
chmod 600 /etc/royalorder.env
echo "Admin key (X-Admin-Key): $ADMIN_KEY" > /root/royalorder-admin.txt

cat > /etc/systemd/system/royalorder-backend.service <<UNIT
[Unit]
Description=ROYAL ORDER backend
After=network-online.target
[Service]
User=royal
WorkingDirectory=/opt/royalorder/backend
EnvironmentFile=/etc/royalorder.env
ExecStart=/usr/bin/dotnet /opt/royalorder/backend/KnifeWar.Backend.dll
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
UNIT

cat > /etc/systemd/system/royalorder-game@.service <<UNIT
[Unit]
Description=ROYAL ORDER game server %i
After=royalorder-backend.service
[Service]
User=royal
WorkingDirectory=/opt/royalorder/LinuxServer
ExecStart=/opt/royalorder/LinuxServer/KnifeWarServer.x86_64 -batchmode -nographics -logFile /opt/royalorder/game-%i.log -kw "server %i backend http://127.0.0.1:8080 secret $SERVER_SECRET region kr address $PUBLIC_IP tick $TICK"
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now royalorder-backend
sleep 3
for i in $(seq 0 $((GAME_SERVERS-1))); do systemctl enable --now royalorder-game@$((7777+i)); done

# Firewall: backend 8080/tcp, game 7777-7786/udp, ssh 22
if command -v ufw >/dev/null; then
  ufw allow 22/tcp; ufw allow 8080/tcp; ufw allow 7777:7786/udp; ufw --force enable
fi
echo "ROYAL ORDER ready: http://$PUBLIC_IP:8080"
