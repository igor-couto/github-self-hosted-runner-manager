#!/usr/bin/env bash
# Sourced by installer-smoke.sh inside its isolated Docker fixture.
set -euo pipefail
: "${runners:?Source this test from installer-smoke.sh.}"
bridge=/var/lib/runner-room-updater
test "$(stat -c %U "$bridge")" = root
test "$(stat -c %a "$bridge")" = 755
test "$(stat -c %U "$bridge/inbox")" = runner
test "$(stat -c %a "$bridge/inbox")" = 700
test "$(stat -c %U /opt/runner-room/installer.sh)" = root
test "$(stat -c %a /opt/runner-room/installer.sh)" = 700
jq -e '.protocol == 1' "$bridge/capability.json"
systemd-analyze verify /etc/systemd/system/runner-room-update.service /etc/systemd/system/runner-room-update.path
grep -qx 'ReadWritePaths=/var/lib/runner-room-updater/inbox' /etc/systemd/system/runner-room.service
cp /etc/runner-room/runner-room.env /tmp/update-original.env
cp /etc/systemd/system/runner-room.service /tmp/update-original.service
cp /etc/runner-room/settings.json /tmp/update-original-settings.json
cp /opt/runner-room/installer.sh /tmp/update-original-installer.sh
cp /opt/runner-room/update-service.sh /tmp/update-original-helper.sh
request_update() {
    printf '%s\n' "$1" >"$bridge/inbox/request"
    chown runner:runner "$bridge/inbox/request"
    bash /opt/runner-room/update-service.sh
}
unchanged_settings() {
    cmp /tmp/update-original.env /etc/runner-room/runner-room.env
    cmp /tmp/update-original.service /etc/systemd/system/runner-room.service
    cmp /tmp/update-original-settings.json /etc/runner-room/settings.json
    test -s /var/lib/runner-room/analytics.json
    test ! -e "$bridge/inbox/request"
    test ! -e "$bridge/processing"
}
old=$(readlink /opt/runner-room/current)
printf 'v0.1.2\n' >/tmp/latest-version
request_update 1234567890abcdef1234567890abcdef
jq -e '.requestId == "1234567890abcdef1234567890abcdef" and .state == "succeeded" and .targetVersion == "v0.1.2"' "$bridge/status.json"
test "$(stat -c %U "$bridge/status.json")" = root
test "$(stat -c %a "$bridge/status.json")" = 644
test "$(readlink /opt/runner-room/current)" != "$old"
curl -fsS http://127.0.0.1:8082/healthz
unchanged_settings
# Repeated requests for the installed version do not restart or replace it.
old=$(readlink /opt/runner-room/current)
old_pid=$(cat /tmp/runner-room.pid)
request_update aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
test "$(readlink /opt/runner-room/current)" = "$old"
test "$(cat /tmp/runner-room.pid)" = "$old_pid"
unchanged_settings
# A release marked latest cannot silently downgrade an existing installation.
printf 'v0.1.1\n' >/tmp/latest-version
if request_update bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb; then echo 'Downgrade accepted'; exit 1; fi
jq -e '.state == "failed"' "$bridge/status.json"
test "$(readlink /opt/runner-room/current)" = "$old"
unchanged_settings
# Corrupt downloads and failed starts preserve settings and the previous release.
printf 'v0.1.3\n' >/tmp/latest-version
cp /fixture/runner-room-linux-x64.tar.gz.sha256 /tmp/update-original.sha256
printf '%064d\n' 0 >/fixture/runner-room-linux-x64.tar.gz.sha256
if request_update cccccccccccccccccccccccccccccccc; then echo 'Corrupt update accepted'; exit 1; fi
cp /tmp/update-original.sha256 /fixture/runner-room-linux-x64.tar.gz.sha256
test "$(readlink /opt/runner-room/current)" = "$old"
unchanged_settings
touch /tmp/fail-next-start
if request_update dddddddddddddddddddddddddddddddd; then echo 'Failed update startup accepted'; exit 1; fi
jq -e '.state == "failed" and .requestId == "dddddddddddddddddddddddddddddddd"' "$bridge/status.json"
test "$(readlink /opt/runner-room/current)" = "$old"
cmp /tmp/update-original-installer.sh /opt/runner-room/installer.sh
cmp /tmp/update-original-helper.sh /opt/runner-room/update-service.sh
for ((attempt=0; attempt<10; attempt++)); do curl -fsS http://127.0.0.1:8082/healthz && break; sleep 1; done
curl -fsS http://127.0.0.1:8082/healthz
unchanged_settings
# A process can start but fail its HTTP health check. Roll back that case too.
printf 'v0.1.3\n' >/tmp/unhealthy-version
if request_update eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee; then echo 'Unhealthy update accepted'; exit 1; fi
rm /tmp/unhealthy-version
test "$(readlink /opt/runner-room/current)" = "$old"
for ((attempt=0; attempt<10; attempt++)); do curl -fsS http://127.0.0.1:8082/healthz && break; sleep 1; done
curl -fsS http://127.0.0.1:8082/healthz
unchanged_settings
# Neither links nor commands nor oversized requests become root instructions.
printf 'preserve me\n' >/tmp/update-victim
ln -s /tmp/update-victim "$bridge/inbox/request"
if bash /opt/runner-room/update-service.sh; then echo 'Symlink request accepted'; exit 1; fi
grep -qx 'preserve me' /tmp/update-victim
if request_update 'touch /tmp/update-command-ran'; then echo 'Command request accepted'; exit 1; fi
test ! -e /tmp/update-command-ran
head -c 4096 /dev/zero >"$bridge/inbox/request"
if bash /opt/runner-room/update-service.sh; then echo 'Oversized request accepted'; exit 1; fi
mkdir "$bridge/inbox/request"
if bash /opt/runner-room/update-service.sh; then echo 'Directory request accepted'; exit 1; fi
unchanged_settings
# Existing explicit-version installs also support archives from before UI updates.
# The UI updater must reject those archives; a deliberate manual install can use
# them, disables the bridge, and still restores the bridge on startup failure.
cp /fixture/runner-room-linux-x64.tar.gz /tmp/update-current.tar.gz
cp /fixture/runner-room-linux-x64.tar.gz.sha256 /tmp/update-current.tar.gz.sha256
tar --exclude='./install.sh' --exclude='./update-service.sh' -czf /fixture/runner-room-linux-x64.tar.gz -C /fixture/package .
(cd /fixture && sha256sum runner-room-linux-x64.tar.gz >runner-room-linux-x64.tar.gz.sha256)
if request_update ffffffffffffffffffffffffffffffff; then echo 'UI update accepted a release without update helpers'; exit 1; fi
test "$(readlink /opt/runner-room/current)" = "$old"
unchanged_settings
touch /tmp/fail-next-start
if bash /fixture/install.sh --runners "$runners" --port 8082 --version v0.0.9; then echo 'Failed legacy startup accepted'; exit 1; fi
test "$(readlink /opt/runner-room/current)" = "$old"
jq -e '.protocol == 1' "$bridge/capability.json"
systemctl is-active --quiet runner-room-update.path
systemctl is-enabled --quiet runner-room-update.path
unchanged_settings
printf '99999999999999999999999999999999\n' >"$bridge/inbox/request"
bash /fixture/install.sh --runners "$runners" --port 8082 --version v0.0.9
test ! -f /opt/runner-room/current/install.sh
test ! -f /opt/runner-room/current/update-service.sh
test ! -e "$bridge/capability.json"
test ! -e "$bridge/inbox/request"
if systemctl is-active --quiet runner-room-update.path; then echo 'Legacy install left updater active'; exit 1; fi
if systemctl is-enabled --quiet runner-room-update.path; then echo 'Legacy install left updater enabled'; exit 1; fi
if grep -q '^ReadWritePaths=.*runner-room-updater' /etc/systemd/system/runner-room.service; then echo 'Legacy install retained updater write access'; exit 1; fi
curl -fsS http://127.0.0.1:8082/healthz
cmp /tmp/update-original-settings.json /etc/runner-room/settings.json
test -s /var/lib/runner-room/analytics.json
cp /tmp/update-current.tar.gz /fixture/runner-room-linux-x64.tar.gz
cp /tmp/update-current.tar.gz.sha256 /fixture/runner-room-linux-x64.tar.gz.sha256
bash /fixture/install.sh --runners "$runners" --port 8082 --version v0.1.2
unchanged_settings
jq -e '.protocol == 1' "$bridge/capability.json"
printf 'PASS: native updates, settings preservation, no-op, downgrade rejection, failures/rollback, untrusted requests and legacy release compatibility.\n'
