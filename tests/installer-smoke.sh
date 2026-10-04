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
memory_total=$(awk '/^MemTotal:/ {printf "%.0f\n", $2 * 1024}' /proc/meminfo)
disk_total=$(df -B1 --output=size "$runners" | tail -n 1 | tr -d ' ')
curl -fsS http://127.0.0.1:8080/api/runners | jq -e --argjson memory "$memory_total" --argjson disk "$disk_total" '
    .system | .logicalProcessors > 0 and .uptimeSeconds > 0 and
    .memory.totalBytes == $memory and .memory.usedBytes >= 0 and
    (.memory.usedBytes + .memory.availableBytes == .memory.totalBytes) and
    .disk.totalBytes == $disk and .disk.usedPercent >= 0 and .disk.usedPercent <= 100 and
    .disk.availableBytes >= 0 and .disk.availableBytes <= .disk.totalBytes'
test "$(stat -c %a /etc/runner-room/runner-room.env)" = 600
grep -qx 'User=runner' /etc/systemd/system/runner-room.service
runuser -u runner -- "$runners/build-one/bin/Runner.Listener" 60 &
runner_pid=$!
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '
    (.system.cpuUsagePercent | type == "number" and . >= 0 and . <= 100) and
    (.runners | length == 7 and
    any(.[]; .folder == "build-one" and .status == "on" and .pid != null) and
    ([.[] | select(.status == "on")] | length == 1))'
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
# /dev/shm is a separate tmpfs. Disk readings must follow the runner path,
# including a symlink, instead of always reporting the root filesystem.
mkdir -p /dev/shm/runner-room-disk
printf '{"agentName":"separate-disk"}\n' >/dev/shm/runner-room-disk/.runner
chown -R runner:runner /dev/shm/runner-room-disk
ln -s /dev/shm/runner-room-disk /srv/runner-disk-link
bash /fixture/install.sh --runners /srv/runner-disk-link --port 8082 --version v0.1.1
disk_total=$(df -B1 --output=size /dev/shm/runner-room-disk | tail -n 1 | tr -d ' ')
curl -fsS http://127.0.0.1:8082/api/runners | jq -e --argjson disk "$disk_total" '
    .root == "/dev/shm/runner-room-disk" and .system.disk.totalBytes == $disk'
systemctl stop runner-room.service
echo 'PASS: installation, discovery, process status, system metrics, separate filesystem, rollback, update.'
