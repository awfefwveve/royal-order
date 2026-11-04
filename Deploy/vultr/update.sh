#!/bin/bash
# ROYAL ORDER test server auto update (runs every 2 minutes from cron).
# If https://patch.astrelgames.com/server/version.txt changed: download the bundle, rebuild the backend,
# swap in the new Linux game server and restart. Secrets (/etc/royalorder.env) and the DB are kept.
set -e
BASE="https://patch.astrelgames.com/server"
DIR=/opt/royalorder
LOCK=/run/royalorder-update.lock
exec 9>$LOCK; flock -n 9 || exit 0
# Keep this script itself up to date (next run uses the new copy)
curl -fsS "$BASE/update.sh?t=$(date +%s)" -o /usr/local/bin/royalorder-update.new && [ -s /usr/local/bin/royalorder-update.new ] && chmod +x /usr/local/bin/royalorder-update.new && mv /usr/local/bin/royalorder-update.new /usr/local/bin/royalorder-update || true
NEW=$(curl -fsS "$BASE/version.txt?t=$(date +%s)" | tr -d '\r\n ') || exit 0
[ -n "$NEW" ] || exit 0
OLD=$(cat $DIR/version 2>/dev/null || echo none)
[ "$NEW" = "$OLD" ] && exit 0
exec >> /var/log/royalorder-update.log 2>&1
echo "$(date -Is) update $OLD -> $NEW"
export HOME=/root DOTNET_CLI_HOME=/root DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
W=$(mktemp -d)
curl -fsSL "$BASE/royalorder-server.tar.gz" -o $W/b.tgz
tar xzf $W/b.tgz -C $W
dotnet publish $W/backend-src/KnifeWar.Backend.csproj -c Release -p:UsePostgres=true -o $W/backend-pg
cp $DIR/backend/appsettings.json $W/backend-pg/appsettings.json
python3 - $DIR/backend/appsettings.json <<'PY'
import json, sys
p = sys.argv[1]; c = json.load(open(p, encoding='utf-8'))
c['KnifeWar']['Matchmaking']['FillWithBotsAfterSeconds'] = 0
json.dump(c, open(p, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
cp $DIR/backend/appsettings.json $W/backend-pg/appsettings.json
chmod +x $W/LinuxServer/KnifeWarServer.x86_64
systemctl stop 'royalorder-game@*' royalorder-backend || true
rm -rf $DIR/backend.old $DIR/LinuxServer.old
mv $DIR/backend $DIR/backend.old; mv $W/backend-pg $DIR/backend
mv $DIR/LinuxServer $DIR/LinuxServer.old; mv $W/LinuxServer $DIR/LinuxServer
chown -R royal:royal $DIR
systemctl start royalorder-backend
sleep 3
for u in $(systemctl list-units --all --plain --no-legend 'royalorder-game@*' | awk '{print $1}'); do systemctl start $u; done
echo "$NEW" > $DIR/version
rm -rf $W
echo "$(date -Is) updated to $NEW"
