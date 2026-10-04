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
# Reproduce a parent with leftover registration metadata and seven child runners.
# Runner auto-updates replace bin with an absolute symlink to a versioned folder.
mkdir -p "$runners/build-one/bin.2.326.0" "$runners/build-one/bin.2.325.0" "$runners/ordinary-folder"
printf '{"agentName":"pifive2"}\n' >"$runners/.runner"
printf '{"agentName":"test-runner"}\n' >"$runners/build-one/.runner"
cp /bin/sleep "$runners/build-one/bin.2.326.0/Runner.Listener"
cp /bin/sleep "$runners/build-one/bin.2.325.0/Runner.Listener"
ln -s "$runners/build-one/bin.2.326.0" "$runners/build-one/bin"
for number in {2..7}; do
    mkdir -p "$runners/project-$number"
    printf '{"agentName":"project-%s"}\n' "$number" >"$runners/project-$number/.runner"
done
# Nested work folders and symlink aliases must not become additional runners.
mkdir -p "$runners/ordinary-folder/nested"
printf '{"agentName":"nested"}\n' >"$runners/ordinary-folder/nested/.runner"
ln -s "$runners/build-one" "$runners/alias"
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
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '
    .runners | length == 7 and
    all(.[]; .status == "off" and .name != "pifive2") and
    any(.[]; .folder == "build-one" and .name == "test-runner")'
test "$(stat -c %a /etc/runner-room/runner-room.env)" = 600
grep -qx 'User=runner' /etc/systemd/system/runner-room.service
runuser -u runner -- "$runners/build-one/bin/Runner.Listener" 60 &
runner_pid=$!
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '
    .runners | length == 7 and
    any(.[]; .folder == "build-one" and .status == "on" and .pid != null) and
    ([.[] | select(.status == "on")] | length == 1)'
kill "$runner_pid"
wait "$runner_pid" || true
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '.runners | length == 7 and all(.[]; .status == "off")'

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
bash /fixture/install.sh --runners "$runners/build-one" --port 8082 --version v0.1.1
curl -fsS http://127.0.0.1:8082/api/runners | jq -e '
    .runners | length == 1 and .[0].folder == "build-one" and .[0].name == "test-runner"'
systemctl stop runner-room.service
echo 'PASS: validation, checksum, install, parent metadata, seven children, versioned bin symlink, process on/off, single-runner root, rollback, update.'
