#!/usr/bin/env bash
set -euo pipefail
bash /fixture/platforms.sh
# Mock only release downloads and service supervision, never the actual app.
# Loopback curl requests always use the real curl binary.
mv /usr/local/bin/curl /usr/local/bin/release-curl
cat >/usr/local/bin/curl <<'SH'
#!/usr/bin/env bash
for arg in "$@"; do
    if [[ $arg == https://github.com/* ]]; then exec /usr/local/bin/release-curl "$@"; fi
done
exec /usr/bin/curl "$@"
SH
chmod +x /usr/local/bin/curl
runners='/srv/runner folders'
mkdir -p "$runners/build-one/bin" "$runners/ordinary-folder"
printf '{"agentName":"test-runner"}\n' >"$runners/build-one/.runner"
cp /bin/sleep "$runners/build-one/bin/Runner.Listener"
chown -R runner:runner "$runners"
install_app() { bash /fixture/install.sh --runners "$runners" "$@"; }
bash /fixture/install.sh --help >/dev/null
if bash /fixture/install.sh --runners "$runners" --port 80; then echo 'Invalid port accepted'; exit 1; fi
cp /fixture/runner-room-linux-x64.tar.gz.sha256 /fixture/good.sha256
printf '%064d\n' 0 >/fixture/runner-room-linux-x64.tar.gz.sha256
if install_app; then echo 'Corrupt checksum accepted'; exit 1; fi
test ! -e /opt/runner-room/current
cp /fixture/good.sha256 /fixture/runner-room-linux-x64.tar.gz.sha256

install_app
systemd-analyze verify /etc/systemd/system/runner-room.service
curl -fsS http://127.0.0.1:8080/api/runners | grep -F '"status":"off"'
test "$(stat -c %a /etc/runner-room/runner-room.env)" = 600
grep -qx 'User=runner' /etc/systemd/system/runner-room.service
runuser -u runner -- "$runners/build-one/bin/Runner.Listener" 60 &
runner_pid=$!
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | grep -F '"status":"on"'
kill "$runner_pid"
wait "$runner_pid" || true
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | grep -F '"status":"off"'

old=$(readlink /opt/runner-room/current)
cp /etc/runner-room/runner-room.env /tmp/original.env
touch /tmp/fail-next-start
if install_app --version v0.1.1 --port 8082; then echo 'Failed startup accepted'; exit 1; fi
test ! -e /tmp/fail-next-start
test "$(readlink /opt/runner-room/current)" = "$old"
cmp /tmp/original.env /etc/runner-room/runner-room.env
for ((attempt=0; attempt<10; attempt++)); do curl -fsS http://127.0.0.1:8080/healthz && break; sleep 1; done
curl -fsS http://127.0.0.1:8080/healthz
install_app --version v0.1.1 --port 8082
test "$(readlink /opt/runner-room/current)" != "$old"
curl -fsS http://127.0.0.1:8082/healthz
systemctl stop runner-room.service
echo 'PASS: validation, checksum, install, runtime, static service config, process on/off, update rollback, update.'
