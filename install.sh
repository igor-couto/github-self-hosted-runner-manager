#!/usr/bin/env bash
# Managed by the Runner Room installer
# Install a self-contained GitHub release. Keep execution at the end so a partial
# curl download cannot begin an installation.
set -Eeuo pipefail

main() {
    local repo=igor-couto/github-self-hosted-runner-manager
    local runners='' port=8080 service_user='' version=latest update_only=0
    # EXIT traps can run after Bash unwinds function locals on an error. Keep
    # transaction state in this installer's process scope for reliable rollback.
    declare -g prefix=/opt/runner-room config=/etc/runner-room/runner-room.env
    declare -g unit=/etc/systemd/system/runner-room.service previous='' work_dir=''
    declare -g changed=0 completed=0 was_active=0 was_enabled=0
    declare -g bridge_changed=0 path_was_active=0 path_was_enabled=0
    declare -g update_request='' updater=/var/lib/runner-room-updater target_version=''
    local rid asset base expected actual release_dir has_updater=0 updater_write_path=''

    die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
    usage() {
        cat <<'HELP'
Runner Room installer (Linux with systemd, x64, ARM32, or ARM64)

  sudo bash install.sh --runners /path/to/runners [options]

  --runners PATH    Parent runner folder, or a single runner folder (required)
  --port NUMBER     Web port, 1024–65535 (default: 8080)
  --user USER       Linux account that runs the runners (default: folder owner)
  --version TAG     Release to install (default: latest)
  --help            Show this help

  --update          Update an existing installation without changing its settings

Re-running the same command updates the application and reapplies these settings.
No .NET SDK/runtime, Git, Docker, or GitHub token is required.
Uninstall later: sudo bash /opt/runner-room/current/uninstall.sh
Settings, history, and runner installations are preserved when uninstalling.
HELP
    }
    while (($#)); do
        case "$1" in
            --help|-h) usage; return ;;
            --update) update_only=1; shift ;;
            --runners|--port|--user|--version|--request-id)
                if (($# < 2)) || [[ -z ${2:-} || ${2:-} == --* ]]; then
                    die "Missing value for $1."
                fi
                case "$1" in
                    --runners) runners=$2 ;;
                    --port) port=$2 ;;
                    --user) service_user=$2 ;;
                    --version) version=$2 ;;
                    --request-id) update_request=$2 ;;
                esac
                shift 2 ;;
            *) die "Unknown argument: $1. Use --help." ;;
        esac
    done
    [[ $(uname -s) == Linux ]] || die 'This installer requires Linux.'
    [[ $EUID == 0 ]] || die 'Run the installer with sudo.'
    umask 022
    [[ -z $update_request || $update_request =~ ^[a-f0-9]{32}$ ]] || die 'Invalid update request.'
    [[ -z $update_request || $update_only == 1 ]] || die 'An update request requires --update.'
    if ((update_only)); then
        [[ -z $runners && -z $service_user && $port == 8080 && $version == latest ]] || die '--update preserves existing settings and installs the latest release.'
        [[ -f $unit && ! -L $unit && -f $config && ! -L $config ]] || die 'A native Runner Room installation is required.'
        grep -qx '# Managed by the Runner Room installer' "$unit" || die 'The existing service is not installer-managed.'
        [[ $(stat -c %u -- "$unit") == 0 && $(stat -c %u -- "$config") == 0 ]] || die 'Installation settings must be root-owned.'
        (( (8#$(stat -c %a -- "$unit") & 8#022) == 0 && (8#$(stat -c %a -- "$config") & 8#022) == 0 )) || die 'Installation settings must not be writable by other users.'
        [[ $(systemctl show runner-room.service --property=FragmentPath --value) == "$unit" ]] || die 'The loaded service does not match this installation.'
        service_user=$(systemctl show runner-room.service --property=User --value)
        local configured_url
        configured_url=$(sed -n 's/^ASPNETCORE_URLS="http:\/\/0\.0\.0\.0:\([0-9][0-9]*\)"$/\1/p' "$config")
        [[ $configured_url =~ ^[0-9]{4,5}$ ]] || die 'The installed dashboard port could not be read. Re-run the ordinary installer.'
        port=$configured_url
    else
        [[ -n $runners && -d $runners ]] || die 'Supply --runners with an existing directory.'
    fi
    [[ $port =~ ^[0-9]{4,5}$ ]] || die 'Port must be between 1024 and 65535.'
    ((10#$port >= 1024 && 10#$port <= 65535)) || die 'Port must be between 1024 and 65535.'
    port=$((10#$port))
    [[ $version == latest || $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][a-zA-Z0-9.-]+)?$ ]] || die 'Version must be a release tag such as v0.1.0.'
    for command in curl tar sha256sum systemctl runuser flock realpath stat getent getconf; do
        command -v "$command" >/dev/null || die "Required Linux utility is missing: $command."
    done
    [[ -d /run/systemd/system ]] || die 'A running systemd host is required. Install directly on your Linux server.'
    case "$(uname -m)" in
        x86_64)
            [[ $(getconf LONG_BIT) == 64 ]] || die 'x64 requires a 64-bit Linux userspace.'
            rid=linux-x64 ;;
        armv7l|armv8l) rid=linux-arm ;;
        aarch64|arm64)
            # A Pi can have a 64-bit kernel with a 32-bit OS/userspace.
            case "$(getconf LONG_BIT)" in
                32) rid=linux-arm ;;
                64) rid=linux-arm64 ;;
                *) die 'Could not determine the ARM userspace architecture.' ;;
            esac ;;
        *) die 'Supported architectures: x64, ARM32 (ARMv7 or newer), and ARM64. ARMv6 is not supported.' ;;
    esac
    [[ ! -f /etc/alpine-release ]] || die 'These releases require glibc Linux (such as Ubuntu or Debian), not Alpine/musl.'
    if ((!update_only)); then
        runners=$(realpath -- "$runners")
        [[ $runners != *$'\n'* && $runners != *$'\r'* ]] || die 'Runner folder cannot contain newline characters.'
        if [[ -z $service_user ]]; then
            service_user=$(stat -c %U -- "$runners")
            [[ $service_user != root ]] || die 'The folder is owned by root. Supply --user with the account that runs your runners.'
        fi
    fi
    [[ $service_user =~ ^[a-zA-Z_][a-zA-Z0-9_.-]*\$?$ ]] || die 'The service user must be an existing Linux account.'
    getent passwd "$service_user" >/dev/null || die 'The service user must be an existing Linux account.'
    if ((!update_only)); then
        runuser -u "$service_user" -- test -r "$runners" || die "$service_user cannot read the runner directory."
        runuser -u "$service_user" -- test -x "$runners" || die "$service_user cannot traverse the runner directory."
    fi
    # Serialize installs; never replace an unrelated service or a non-managed directory.
    exec 9>/run/lock/runner-room-install.lock
    flock -n 9 || die 'Another Runner Room installation is in progress.'
    if [[ -f $unit ]] && ! grep -qx '# Managed by the Runner Room installer' "$unit"; then
        die 'runner-room.service already exists and was not created by this installer. Back up and remove that service first.'
    fi
    for directory in /opt "$prefix" "$prefix/releases" /var /var/lib "$updater" /etc /etc/runner-room /etc/systemd /etc/systemd/system; do
        [[ ! -L $directory ]] || die "$directory must not be a symbolic link."
        if [[ -e $directory ]]; then
            [[ -d $directory && $(stat -c %u -- "$directory") == 0 ]] || die "$directory must be a root-owned directory."
            (( (8#$(stat -c %a -- "$directory") & 8#022) == 0 )) || die "$directory must not be writable by other users."
        fi
    done
    for managed in "$prefix/installer.sh" "$prefix/update-service.sh" /etc/systemd/system/runner-room-update.service /etc/systemd/system/runner-room-update.path; do
        if [[ -e $managed || -L $managed ]]; then
            [[ -f $managed && ! -L $managed && $(stat -c %u -- "$managed") == 0 ]] || die "Untrusted updater file: $managed"
            (( (8#$(stat -c %a -- "$managed") & 8#022) == 0 )) || die "Updater file is writable by other users: $managed"
            grep -qx '# Managed by the Runner Room installer' "$managed" || die "An unrelated updater file already exists: $managed"
        fi
    done
    if [[ -e $updater/capability.json || -L $updater/capability.json ]]; then
        [[ -f $updater/capability.json && ! -L $updater/capability.json && $(stat -c %u -- "$updater/capability.json") == 0 ]] || die 'The updater capability is not a root-owned regular file.'
        (( (8#$(stat -c %a -- "$updater/capability.json") & 8#022) == 0 )) || die 'The updater capability is writable by other users.'
        grep -qx '{"protocol":1}' "$updater/capability.json" || die 'The updater capability is not recognized.'
    fi
    [[ ! -e $prefix/current || -L $prefix/current ]] || die "$prefix/current must be an installer-managed symbolic link."
    work_dir=$(mktemp -d /tmp/runner-room-install.XXXXXXXX)
    progress() {
        [[ -n $update_request ]] || return 0
        local temporary
        temporary=$(mktemp "$updater/status.XXXXXXXX")
        printf '{"requestId":"%s","state":"%s","message":"%s","updatedAt":"%s","targetVersion":"%s"}\n' \
            "$update_request" "$1" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$target_version" >"$temporary"
        chmod 644 "$temporary"
        mv -Tf -- "$temporary" "$updater/status.json"
    }
    cleanup() {
        local exit_code=$?
        trap - EXIT
        if ((changed && !completed)); then
            printf 'Installation failed; restoring the previous installation.\n' >&2
            systemctl stop runner-room.service >/dev/null 2>&1 || true
            rm -f -- "$prefix/current.next"
            if [[ -n $previous ]]; then
                ln -sfn -- "$previous" "$prefix/current"
            else
                rm -f -- "$prefix/current"
            fi
            if [[ -f $work_dir/old.env ]]; then cp -- "$work_dir/old.env" "$config"; else rm -f -- "$config"; fi
            if [[ -f $work_dir/old.service ]]; then
                cp -- "$work_dir/old.service" "$unit"
            else
                systemctl disable runner-room.service >/dev/null 2>&1 || true
                rm -f -- "$unit"
            fi
            if ((bridge_changed)); then
                systemctl stop runner-room-update.path >/dev/null 2>&1 || true
                local bridge_file
                for bridge_file in "$prefix/installer.sh" "$prefix/update-service.sh" /etc/systemd/system/runner-room-update.service /etc/systemd/system/runner-room-update.path "$updater/capability.json"; do
                    if [[ -f $work_dir/bridge-${bridge_file##*/} ]]; then cp -- "$work_dir/bridge-${bridge_file##*/}" "$bridge_file";
                    else rm -f -- "$bridge_file"; fi
                done
            fi
            systemctl daemon-reload || true
            if ((bridge_changed)); then
                if ((path_was_enabled)); then systemctl enable runner-room-update.path >/dev/null 2>&1 || true;
                else systemctl disable runner-room-update.path >/dev/null 2>&1 || true; fi
                if ((path_was_active)); then systemctl start runner-room-update.path >/dev/null 2>&1 || true; fi
            fi
            if ((was_enabled)); then systemctl enable runner-room.service >/dev/null 2>&1 || true;
            else systemctl disable runner-room.service >/dev/null 2>&1 || true; fi
            if ((was_active)); then systemctl start runner-room.service || true; fi
        fi
        if ((exit_code != 0)) && [[ -n $update_request ]]; then
            progress failed 'The update failed. Check the updater service log; the dashboard may need attention.' || true
        fi
        # work_dir is the exact private directory returned by mktemp above.
        [[ -z $work_dir ]] || rm -rf -- "$work_dir"
        flock -u 9 || true
        exit "$exit_code"
    }
    trap cleanup EXIT
    progress checking 'Checking the latest Runner Room release.'
    if [[ $version == latest ]]; then
        local redirected
        redirected=$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --retry-max-time 120 --max-time 120 --connect-timeout 15 \
            --output /dev/null --write-out '%{url_effective}' "https://github.com/$repo/releases/latest") || die 'No published release could be downloaded. The maintainer must publish a release first.'
        version=${redirected##*/}
        [[ $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][a-zA-Z0-9.-]+)?$ ]] || die 'Could not resolve the latest release tag.'
    fi
    target_version=$version
    if ((update_only)); then
        [[ $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || die 'The latest release is not a stable version.'
        local installed_version=''
        if [[ -f $prefix/current/.runner-room-version ]]; then
            installed_version=$(cat "$prefix/current/.runner-room-version")
        fi
        if [[ $installed_version == "$version" ]]; then
            completed=1
            progress succeeded 'Runner Room is already on the latest release.'
            printf 'Runner Room is already on %s.\n' "$version"
            return
        fi
        if [[ $installed_version =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] &&
            [[ $(printf '%s\n%s\n' "$installed_version" "$version" | sort -V | tail -n 1) != "$version" ]]; then
            die 'The installed version is newer than the latest release; no downgrade was performed.'
        fi
    fi
    asset="runner-room-$rid.tar.gz"
    base="https://github.com/$repo/releases/download/$version"
    printf 'Downloading Runner Room %s for %s…\n' "$version" "$rid"
    progress downloading 'Downloading and checking the latest release.'
    for file in "$asset" "$asset.sha256"; do
        curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 2 --retry-max-time 120 --max-time 120 --connect-timeout 15 \
            --output "$work_dir/$file" "$base/$file" || die "Could not download $file. Check that release $version has Linux assets."
    done
    expected=$(awk 'NR==1 {print $1}' "$work_dir/$asset.sha256")
    actual=$(sha256sum "$work_dir/$asset"); actual=${actual%% *}
    [[ $expected =~ ^[a-fA-F0-9]{64}$ && ${expected,,} == "$actual" ]] || die 'Download checksum does not match; installation was not changed.'
    progress installing 'Preparing the new application. Your settings and runners are preserved.'
    # Validate archive paths/types before extracting as root.
    tar -tzf "$work_dir/$asset" >"$work_dir/entries"
    while IFS= read -r file; do
        case "$file" in /*|..|../*|*/../*|*/..) die 'Unsafe path in release archive.' ;; esac
    done <"$work_dir/entries"
    tar -tvzf "$work_dir/$asset" >"$work_dir/types"
    awk 'substr($1,1,1) != "-" && substr($1,1,1) != "d" {exit 1}' "$work_dir/types" || die 'Unexpected link or special file in release archive.'
    install -d -m 755 "$prefix/releases" /etc/runner-room
    release_dir=$(mktemp -d "$prefix/releases/$version.XXXXXXXX")
    chmod 755 "$release_dir"
    tar -xzf "$work_dir/$asset" -C "$release_dir" --no-same-owner --no-same-permissions
    [[ -f $release_dir/RunnerRoom && -f $release_dir/wwwroot/index.html ]] || die 'The release does not contain the application and web page.'
    if [[ -f $release_dir/install.sh && -f $release_dir/update-service.sh ]]; then
        has_updater=1
        updater_write_path=ReadWritePaths=/var/lib/runner-room-updater/inbox
    elif ((update_only)); then
        die 'The release does not contain the update helper; the installed application was kept.'
    fi
    printf '%s\n' "$version" >"$release_dir/.runner-room-version"
    # Record exactly what this release owns. Uninstall never recursively deletes
    # release directories: files subsequently added by users remain untouched.
    find "$release_dir" -type d -exec chmod go-w {} +
    find "$release_dir" -type f -exec chmod go-w {} +
    # Build outside the scanned tree, then install the completed ownership record.
    {
        printf 'runner-room-release-v1\0'
        find "$release_dir" -mindepth 1 -depth ! -path "$release_dir/.runner-room-manifest" -printf '%y%P\0'
    } >"$work_dir/release-manifest"
    install -m 600 -- "$work_dir/release-manifest" "$release_dir/.runner-room-manifest"
    chmod 755 "$release_dir/RunnerRoom"
    runuser -u "$service_user" -- "$release_dir/RunnerRoom" --check-runtime || die 'The bundled runtime cannot start. Use a supported glibc Linux distribution with its standard native libraries.'
    [[ ! -f $config ]] || cp -- "$config" "$work_dir/old.env"
    [[ ! -f $unit ]] || cp -- "$unit" "$work_dir/old.service"
    [[ ! -L $prefix/current ]] || previous=$(readlink -- "$prefix/current")
    systemctl is-active --quiet runner-room.service && was_active=1
    systemctl is-enabled --quiet runner-room.service && was_enabled=1
    for bridge_file in "$prefix/installer.sh" "$prefix/update-service.sh" /etc/systemd/system/runner-room-update.service /etc/systemd/system/runner-room-update.path "$updater/capability.json"; do
        [[ ! -f $bridge_file ]] || cp -- "$bridge_file" "$work_dir/bridge-${bridge_file##*/}"
    done
    systemctl is-active --quiet runner-room-update.path && path_was_active=1
    systemctl is-enabled --quiet runner-room-update.path && path_was_enabled=1
    changed=1
    if ((!update_only)); then
    # EnvironmentFile uses quoted values, not shell evaluation. Escape its two special characters.
    local escaped=${runners//\\/\\\\}
    escaped=${escaped//\"/\\\"}
    printf 'RunnersRoot="%s"\nASPNETCORE_URLS="http://0.0.0.0:%s"\n' "$escaped" "$port" >"$config"
    chmod 600 "$config"
    cat >"$unit" <<UNIT
# Managed by the Runner Room installer
[Unit]
Description=Runner Room dashboard
After=network.target

[Service]
Type=simple
User=$service_user
WorkingDirectory=/opt/runner-room/current
ExecStart=/opt/runner-room/current/RunnerRoom
EnvironmentFile=/etc/runner-room/runner-room.env
Restart=on-failure
RestartSec=5
StateDirectory=runner-room
StateDirectoryMode=0750
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=read-only
$updater_write_path

[Install]
WantedBy=multi-user.target
UNIT
    chmod 644 "$unit"
    fi
    # Only the fixed request inbox is writable by the dashboard. The helper,
    # capability and progress files remain owned by root.
    if ((has_updater)); then
    install -d -m 755 "$updater"
    bridge_changed=1
    [[ ! -L $updater/inbox && ( ! -e $updater/inbox || -d $updater/inbox ) ]] || die 'The updater inbox is not a real directory.'
    install -d -m 700 -o "$service_user" "$updater/inbox"
    for helper in installer.sh update-service.sh; do
        local source=$helper
        [[ $helper != installer.sh ]] || source=install.sh
        install -m 700 -- "$release_dir/$source" "$prefix/$helper.next"
        mv -Tf -- "$prefix/$helper.next" "$prefix/$helper"
    done
    cat >"$work_dir/update.service" <<'UNIT'
# Managed by the Runner Room installer
[Unit]
Description=Update the Runner Room dashboard
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/bin/bash /opt/runner-room/update-service.sh
TimeoutStartSec=15min
UMask=0077
UNIT
    cat >"$work_dir/update.path" <<'UNIT'
# Managed by the Runner Room installer
[Unit]
Description=Watch for Runner Room application updates

[Path]
PathExists=/var/lib/runner-room-updater/inbox/request
Unit=runner-room-update.service

[Install]
WantedBy=multi-user.target
UNIT
    install -m 644 "$work_dir/update.service" /etc/systemd/system/runner-room-update.service
    install -m 644 "$work_dir/update.path" /etc/systemd/system/runner-room-update.path
    printf '{"protocol":1}\n' >"$work_dir/capability.json"
    install -m 644 "$work_dir/capability.json" "$updater/capability.next"
    mv -Tf -- "$updater/capability.next" "$updater/capability.json"
    else
        # Older releases predate UI updates. Keep explicit --version installs
        # working, but disable the bridge and discard its pending request before
        # starting a dashboard that cannot manage or explain those requests.
        bridge_changed=1
        if [[ -f /etc/systemd/system/runner-room-update.path ]]; then
            systemctl stop runner-room-update.path
            systemctl disable runner-room-update.path
        fi
        if [[ -f /etc/systemd/system/runner-room-update.service ]]; then systemctl stop runner-room-update.service; fi
        if ((was_active)); then systemctl stop runner-room.service; fi
        rm -f -- "$updater/capability.json"
        [[ ! -L $updater/inbox && ( ! -e $updater/inbox || -d $updater/inbox ) ]] || die 'The updater inbox is not a real directory.'
        if [[ -f $updater/inbox/request || -L $updater/inbox/request ]]; then rm -f -- "$updater/inbox/request"; fi
    fi
    ln -s -- "$release_dir" "$prefix/current.next"
    mv -Tf -- "$prefix/current.next" "$prefix/current"
    systemctl daemon-reload
    systemctl enable runner-room.service
    if ((has_updater)); then systemctl enable --now runner-room-update.path; fi
    progress restarting 'Restarting the dashboard and checking its health.'
    systemctl restart runner-room.service
    local healthy=0
    for ((attempt=0; attempt<20; attempt++)); do
        if systemctl is-active --quiet runner-room.service && curl --noproxy '*' -fsS --max-time 2 "http://127.0.0.1:$port/healthz" >/dev/null 2>&1; then
            healthy=1; break
        fi
        sleep 1
    done
    if ((!healthy)); then
        journalctl -u runner-room.service -n 15 --no-pager >&2 || true
        die 'The dashboard did not start successfully.'
    fi
    completed=1
    progress succeeded 'Runner Room was updated successfully.'
    local address
    address=$(hostname -I 2>/dev/null || true); address=${address%% *}
    printf '\nRunner Room is running.\n  Dashboard: http://%s:%s\n  Runners:   %s\n  User:      %s\n  Settings:  %s\n' \
        "${address:-YOUR_SERVER_IP}" "$port" "$runners" "$service_user" "$config"
    printf '\nIf a firewall is enabled, allow this port from your LAN. Configure Access in settings.json to require login; otherwise LAN access is open.\n'
    if [[ -f $release_dir/uninstall.sh ]]; then
        printf 'Uninstall: sudo bash /opt/runner-room/current/uninstall.sh\n'
    fi
    cleanup
}

main "$@"
