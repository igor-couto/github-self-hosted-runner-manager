#!/usr/bin/env bash
# Run only in tests/uninstall.Dockerfile: all absolute paths are isolated fixtures.
set -euo pipefail
[[ -f /.dockerenv && -f /fixture/uninstall.sh ]] || exit 1
mkdir -p /run/systemd/system /run/lock /etc/systemd/system /etc/runner-room /var/lib/runner-room /srv/runners/project
printf 'preserve configuration\n' >/etc/runner-room/settings.json
printf 'preserve history\n' >/var/lib/runner-room/analytics.json
printf 'preserve runner\n' >/srv/runners/project/.runner
printf 'independent runner unit\n' >/etc/systemd/system/actions.runner.project.service
cat >/usr/local/bin/systemctl <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >>/tmp/service-calls
case "$1" in
    show)
        case "$*" in
            *--property=FragmentPath*) cat /tmp/fragment ;;
            *--property=ActiveState*)
                if [[ -f /tmp/active ]]; then echo active; else echo inactive; fi ;;
            *) exit 1 ;;
        esac ;;
    stop)
        [[ ! -f /tmp/fail-stop ]] || exit 1
        if [[ ! -f /tmp/still-active ]]; then rm -f /tmp/active; fi ;;
    disable) [[ ! -f /tmp/fail-disable ]] || exit 1 ;;
    daemon-reload) : ;;
    *) exit 1 ;;
esac
SH
chmod +x /usr/local/bin/systemctl
fixture() {
    mkdir -p /opt/runner-room/releases/v1.2.3.fixture/wwwroot
    printf 'binary\n' >/opt/runner-room/releases/v1.2.3.fixture/RunnerRoom
    printf 'web page\n' >/opt/runner-room/releases/v1.2.3.fixture/wwwroot/index.html
    printf 'runner-room-release-v1\0fRunnerRoom\0fwwwroot/index.html\0dwwwroot\0' >/opt/runner-room/releases/v1.2.3.fixture/.runner-room-manifest
    chmod 600 /opt/runner-room/releases/v1.2.3.fixture/.runner-room-manifest
    ln -sfn /opt/runner-room/releases/v1.2.3.fixture /opt/runner-room/current
    cat >/etc/systemd/system/runner-room.service <<'UNIT'
# Managed by the Runner Room installer
WorkingDirectory=/opt/runner-room/current
ExecStart=/opt/runner-room/current/RunnerRoom
UNIT
    printf '/etc/systemd/system/runner-room.service\n' >/tmp/fragment
    : >/tmp/active
    : >/tmp/service-calls
}
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
reject() {
    local expected=$1
    shift
    if bash /fixture/uninstall.sh "$@" >/tmp/uninstall-output 2>&1; then
        cat /tmp/uninstall-output; fail 'Unsafe uninstall was accepted'
    fi
    grep -F "$expected" /tmp/uninstall-output >/dev/null || { cat /tmp/uninstall-output; fail "Missing error: $expected"; }
    test -f /opt/runner-room/releases/v1.2.3.fixture/RunnerRoom
    test -f /etc/systemd/system/runner-room.service
}
fixture
bash /fixture/uninstall.sh --help >/dev/null
reject 'Unknown argument' --purge
reject 'Confirmation requires a terminal'
test -f /tmp/active
# Same lock as install, even when already inside preflight.
flock /run/lock/runner-room-install.lock bash -c 'sleep 3' &
locker=$!
sleep 1
reject 'Another Runner Room install or uninstall' --yes
wait "$locker"
# Root ownership, unit identity and service metadata are mandatory.
sed -i '/Managed by/d' /etc/systemd/system/runner-room.service
reject 'not created by this installer' --yes
fixture
printf '/etc/systemd/system/unrelated.service\n' >/tmp/fragment
reject 'loaded service does not match' --yes
fixture
chmod 666 /etc/systemd/system/runner-room.service
reject 'writable by other users' --yes
chmod 644 /etc/systemd/system/runner-room.service
chown 1234 /opt/runner-room/releases/v1.2.3.fixture
reject 'root-owned path' --yes
chown 0 /opt/runner-room/releases/v1.2.3.fixture
chmod 777 /opt/runner-room/releases
reject 'writable by other users' --yes
chmod 755 /opt/runner-room/releases
ln -sfn /srv/runners /opt/runner-room/current
reject 'outside the release directory' --yes
fixture
# A manifest cannot redirect deletion into runner data, including via an ancestor.
printf 'runner-room-release-v1\0f../../../../srv/runners/project/.runner\0' >/opt/runner-room/releases/v1.2.3.fixture/.runner-room-manifest
reject 'Unsafe path' --yes
fixture
printf 'runner-room-release-v1\0fRunnerRoom' >/opt/runner-room/releases/v1.2.3.fixture/.runner-room-manifest
reject 'truncated' --yes
fixture
mv /opt/runner-room/releases/v1.2.3.fixture/wwwroot /tmp/real-wwwroot
ln -s /tmp/real-wwwroot /opt/runner-room/releases/v1.2.3.fixture/wwwroot
reject 'replaced by a link' --yes
rm /opt/runner-room/releases/v1.2.3.fixture/wwwroot
mv /tmp/real-wwwroot /opt/runner-room/releases/v1.2.3.fixture/wwwroot
test -f /tmp/active
# Failed stop / still-running / failed disable keep all application files.
touch /tmp/fail-stop
reject 'Could not stop Runner Room' --yes
rm /tmp/fail-stop
touch /tmp/still-active
reject 'still active' --yes
rm /tmp/still-active
touch /tmp/fail-disable
reject 'Could not disable Runner Room' --yes
rm /tmp/fail-disable
fixture
# Untracked content and old releases remain; no recursive directory removal.
mkdir -p /opt/runner-room/releases/v1.2.3.fixture/wwwroot/custom-runner /opt/runner-room/releases/v0.9.0.legacy
printf 'preserve nested runner\n' >/opt/runner-room/releases/v1.2.3.fixture/wwwroot/custom-runner/.runner
printf 'preserve legacy binary\n' >/opt/runner-room/releases/v0.9.0.legacy/RunnerRoom
bash /fixture/uninstall.sh --yes >/tmp/uninstall-output
grep -F 'Legacy releases or untracked files remain' /tmp/uninstall-output
test ! -e /etc/systemd/system/runner-room.service
test ! -L /opt/runner-room/current
test ! -f /opt/runner-room/releases/v1.2.3.fixture/RunnerRoom
test ! -f /opt/runner-room/releases/v1.2.3.fixture/wwwroot/index.html
test -f /opt/runner-room/releases/v1.2.3.fixture/wwwroot/custom-runner/.runner
test -f /opt/runner-room/releases/v0.9.0.legacy/RunnerRoom
test -f /etc/runner-room/settings.json
test -f /var/lib/runner-room/analytics.json
test -f /srv/runners/project/.runner
test -f /etc/systemd/system/actions.runner.project.service
if grep -E 'stop.*actions.runner|disable.*actions.runner' /tmp/service-calls; then fail 'Runner service was changed'; fi
bash /fixture/uninstall.sh --yes | grep -F 'already uninstalled'
# Normal uninstall removes empty release directories, even without an active link.
fixture
rm /opt/runner-room/current
rm /opt/runner-room/releases/v1.2.3.fixture/wwwroot/custom-runner/.runner
rmdir /opt/runner-room/releases/v1.2.3.fixture/wwwroot/custom-runner
bash /fixture/uninstall.sh --yes
test ! -d /opt/runner-room/releases/v1.2.3.fixture
test -f /opt/runner-room/releases/v0.9.0.legacy/RunnerRoom
printf 'PASS: confirmation, lock, ownership, unrelated units, outside paths, manifest corruption, symlinks, stop/disable failures, safe removal, data/runner preservation, legacy releases and idempotency.\n'
