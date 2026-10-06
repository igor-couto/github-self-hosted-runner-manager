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
printf '{"agentId":42,"agentName":"test-runner","gitHubUrl":"https://github.com/example/api","poolName":"Default"}\n' >"$runners/build-one/.runner"
printf 'actions.runner.example.api.service\n' >"$runners/build-one/.service"
mkdir -p "$runners/build-one/_diag"
mkdir -p "$runners/build-one/_work"
head -c 128 /dev/zero >"$runners/build-one/_work/workspace-sample"
printf '[2025-07-01 10:03:00Z INFO Terminal] 2025-07-01 10:03:00Z: Job Build API completed with result: Succeeded\n' >"$runners/build-one/_diag/Runner_20250701.log"
history_log="Runner_$(date -u +%Y%m%d-%H%M%S)-utc.log"
history_start=$(date -u -d '2 minutes ago' '+%Y-%m-%d %H:%M:%SZ')
history_end=$(date -u -d '1 minute ago' '+%Y-%m-%d %H:%M:%SZ')
printf '[%s INFO Terminal] Running job: Build API\n[%s INFO Terminal] Job Build API completed with result: Succeeded\n' \
    "$history_start" "$history_end" >"$runners/build-one/_diag/$history_log"
cp /bin/sleep "$runners/build-one/bin.2.326.0/Runner.Listener"
cp /bin/sleep "$runners/build-one/bin.2.326.0/Runner.Worker"
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
    all(.[]; .status == "offline" and .name != "pifive2") and
    any(.[]; .folder == "build-one" and .name == "test-runner")'
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '
    .runners[] | select(.folder == "build-one") |
    .repository == "example/api" and .organization == "example" and
    .version == "2.326.0" and .operatingSystem == "Linux" and .architecture == "X64" and
    .serviceState == "active" and .serviceSubState == "running" and
    .lastJob.name == "Build API" and .lastJob.result == "Succeeded" and
    .gitHub.status == "not_configured"'
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
    any(.[]; .folder == "build-one" and .status == "idle" and .pid != null) and
    ([.[] | select(.status == "idle")] | length == 1))'
runuser -u runner -- "$runners/build-one/bin/Runner.Worker" 60 &
worker_pid=$!
worker_log="Worker_$(date -u +%Y%m%d-%H%M%S)-utc.log"
job_time=$(date -u '+%Y-%m-%d %H:%M:%SZ')
cat >"$runners/build-one/_diag/$worker_log" <<LOG
[$job_time INFO Worker] Job message:
{"jobDisplayName":"Live build","variables":{"secret":{"value":"do-not-expose","isSecret":true}},"contextData":{"github":{"t":2,"d":[{"k":"repository","v":"example/api"},{"k":"workflow","v":"CI"},{"k":"ref","v":"refs/heads/main"},{"k":"run_id","v":"42"}]}}}
[$job_time INFO StepsRunner] Processing step: DisplayName='Compile'
[$job_time WARN ActionRunner] Cache unavailable
[$job_time ERR ActionRunner] Authorization: Bearer do-not-expose
LOG
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '
    .runners[] | select(.folder == "build-one") | .status == "busy" and .processStatus == "running" and .uptimeSeconds > 0 and
    .currentJob.name == "Live build" and .currentJob.workflow == "CI" and .currentJob.steps[0].name == "Compile"'
runner_id=$(curl -fsS http://127.0.0.1:8080/api/runners | jq -r '.runners[] | select(.folder == "build-one") | .id')
curl -fsS "http://127.0.0.1:8080/api/runners/$runner_id/logs" >/tmp/job-logs.json
jq -e '.excerpt.lines | length == 4 and any(.[]; .message == "[Sensitive diagnostic line omitted]")' /tmp/job-logs.json
if grep -q 'do-not-expose' /tmp/job-logs.json; then echo 'Secret exposed in logs'; exit 1; fi
curl -fsS "http://127.0.0.1:8080/api/runners/$runner_id/logs?file=..%2F.credentials" | jq -e '.excerpt.lines | length == 0'
chmod 000 "$runners/build-one/_diag/$worker_log"
curl -fsS "http://127.0.0.1:8080/api/runners/$runner_id/logs" | jq -e '.excerpt.lines | length == 0'
chmod 644 "$runners/build-one/_diag/$worker_log"
# Background samples must work without polling the runner inventory.
for ((attempt=0; attempt<25; attempt++)); do
    if curl -fsS http://127.0.0.1:8080/api/system >/tmp/system-metrics.json && jq -e '
        (.cores | length > 0 and all(.[]; .percent != null)) and
        (.network | any(.[]; .downloadBytesPerSecond != null)) and
        (.runners | any(.[]; .folder == "build-one" and .processes >= 2 and .workspace.bytes == 128))' /tmp/system-metrics.json >/dev/null; then break; fi
    sleep 1
done
jq -e '(.fileSystems | length > 0) and (.topMemory | length > 0) and
    (.runners | any(.[]; .folder == "build-one" and .processes >= 2 and .workspace.bytes == 128))' /tmp/system-metrics.json
test -s /var/lib/runner-room/monitoring.json
test "$(stat -c %U /var/lib/runner-room/monitoring.json)" = runner
grep -qx 'StateDirectory=runner-room' /etc/systemd/system/runner-room.service
curl -fsS http://127.0.0.1:8080/api/history | jq -e '
    .summary.completed == 1 and .totalJobs == 1 and .jobs[0].name == "Build API" and .jobs[0].durationSeconds == 60'
test -s /var/lib/runner-room/analytics.json
test "$(stat -c %U /var/lib/runner-room/analytics.json)" = runner
kill "$worker_pid"
wait "$worker_pid" || true
kill "$runner_pid"
wait "$runner_pid" || true
sleep 3
curl -fsS http://127.0.0.1:8080/api/runners | jq -e '.runners | length == 7 and all(.[]; .status == "offline" and .currentJob == null)'

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
curl -fsS http://127.0.0.1:8082/api/history | jq -e '.summary.completed == 1 and .totalJobs == 1'
curl -fsS http://127.0.0.1:8082/api/history/export | grep -q 'Build API'
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
mkdir -p /srv/extra-runners/another
printf '{"agentName":"extra-runner"}\n' >/srv/extra-runners/another/.runner
cat >/etc/runner-room/settings.json <<JSON
{
  "RunnersRoots": ["$runners", "$runners/build-one", "/srv/extra-runners", "/missing-runners"],
  "Discovery": { "Recursive": true },
  "RunnerOverrides": [{"Path":"$runners/build-one","DisplayName":"API build","Group":"Backend"}],
  "Alerts": {"CheckIntervalSeconds":15,"Rules":[{"Id":"offline","Kind":"runner-offline","HoldSeconds":0,"Target":"build-one"}]}
}
JSON
systemctl restart runner-room.service
for ((attempt=0; attempt<10; attempt++)); do curl -fsS http://127.0.0.1:8082/healthz && break; sleep 1; done
curl -fsS http://127.0.0.1:8082/api/runners | jq -e '
    .warning != null and (.roots | length == 3) and
    (.runners | length == 9 and any(.[]; .displayName == "API build" and .name == "test-runner" and .group == "Backend"))'
# Real scheduled checks run independently of HTTP reads and preserve incidents across updates.
for ((attempt=0; attempt<10; attempt++)); do
    if curl -fsS http://127.0.0.1:8082/api/alerts >/tmp/alerts.json && jq -e '.active | length == 1' /tmp/alerts.json >/dev/null; then break; fi
    sleep 1
done
jq -e '.checkIntervalSeconds == 15 and .notificationChannel == "Dashboard only" and (.rules | length == 1)' /tmp/alerts.json
alert_id=$(jq -r '.active[0].id' /tmp/alerts.json)
runuser -u runner -- "$runners/build-one/bin/Runner.Listener" 60 &
alert_runner_pid=$!
for ((attempt=0; attempt<22; attempt++)); do
    if curl -fsS http://127.0.0.1:8082/api/alerts | jq -e --arg id "$alert_id" '.history | any(.[]; .id == $id and .state == "resolved")' >/dev/null; then break; fi
    sleep 1
done
curl -fsS http://127.0.0.1:8082/api/alerts | jq -e --arg id "$alert_id" '.history | any(.[]; .id == $id and .state == "resolved")'
kill "$alert_runner_pid"
wait "$alert_runner_pid" || true
test -s /var/lib/runner-room/alerts.json
test "$(stat -c %U /var/lib/runner-room/alerts.json)" = runner
cp /etc/runner-room/settings.json /tmp/original-settings.json
bash /fixture/install.sh --runners "$runners" --port 8082 --version v0.1.1
cmp /tmp/original-settings.json /etc/runner-room/settings.json
curl -fsS http://127.0.0.1:8082/api/alerts | jq -e --arg id "$alert_id" '.history | any(.[]; .id == $id and .state == "resolved")'
source /fixture/access-smoke.sh
systemctl stop runner-room.service
echo 'PASS: installation, discovery, metadata, jobs/logs, process states, metrics, analytics, scheduled alerts/recovery, rollback, updates and settings/history preservation.'
