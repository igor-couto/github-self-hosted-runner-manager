#!/usr/bin/env bash
# Install a self-contained GitHub release. Keep execution at the end so a partial
# curl download cannot begin an installation.
set -Eeuo pipefail

main() {
    local repo=igor-couto/github-self-hosted-runner-manager
    local runners='' port=8080 service_user='' version=latest
    # EXIT traps can run after Bash unwinds function locals on an error. Keep
    # transaction state in this installer's process scope for reliable rollback.
    declare -g prefix=/opt/runner-room config=/etc/runner-room/runner-room.env
    declare -g unit=/etc/systemd/system/runner-room.service previous='' work_dir=''
    declare -g changed=0 completed=0 was_active=0 was_enabled=0
    local rid asset base expected actual release_dir

    die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
    usage() {
        cat <<'HELP'
Runner Room installer (Linux with systemd, x64 or ARM64)

  sudo bash install.sh --runners /path/to/runners [options]

  --runners PATH    Parent runner folder, or a single runner folder (required)
  --port NUMBER     Web port, 1024–65535 (default: 8080)
  --user USER       Linux account that runs the runners (default: folder owner)
  --version TAG     Release to install (default: latest)
  --help            Show this help

Re-running the same command updates the application and reapplies these settings.
No .NET SDK/runtime, Git, Docker, or GitHub token is required.
HELP
    }
    while (($#)); do
        case "$1" in
            --help|-h) usage; return ;;
            --runners|--port|--user|--version)
                (($# >= 2)) && [[ -n $2 && $2 != --* ]] || die "Missing value for $1."
                case "$1" in
                    --runners) runners=$2 ;;
                    --port) port=$2 ;;
                    --user) service_user=$2 ;;
                    --version) version=$2 ;;
                esac
                shift 2 ;;
            *) die "Unknown argument: $1. Use --help." ;;
        esac
    done
    [[ $(uname -s) == Linux ]] || die 'This installer requires Linux.'
    [[ $EUID == 0 ]] || die 'Run the installer with sudo.'
    umask 022
    [[ -n $runners && -d $runners ]] || die 'Supply --runners with an existing directory.'
    [[ $port =~ ^[0-9]{4,5}$ ]] || die 'Port must be between 1024 and 65535.'
    ((10#$port >= 1024 && 10#$port <= 65535)) || die 'Port must be between 1024 and 65535.'
    port=$((10#$port))
    [[ $version == latest || $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][a-zA-Z0-9.-]+)?$ ]] || die 'Version must be a release tag such as v0.1.0.'
    for command in curl tar sha256sum systemctl runuser flock realpath stat getent; do
        command -v "$command" >/dev/null || die "Required Linux utility is missing: $command."
    done
    [[ -d /run/systemd/system ]] || die 'A running systemd host is required. Install directly on your Linux server.'
    case "$(uname -m)" in
        x86_64) rid=linux-x64 ;;
        aarch64|arm64) rid=linux-arm64 ;;
        *) die 'Supported architectures: x86_64 and ARM64.' ;;
    esac
    [[ ! -f /etc/alpine-release ]] || die 'These releases require glibc Linux (such as Ubuntu or Debian), not Alpine/musl.'
    runners=$(realpath -- "$runners")
    [[ $runners != *$'\n'* && $runners != *$'\r'* ]] || die 'Runner folder cannot contain newline characters.'
    if [[ -z $service_user ]]; then
        service_user=$(stat -c %U -- "$runners")
        [[ $service_user != root ]] || die 'The folder is owned by root. Supply --user with the account that runs your runners.'
    fi
    [[ $service_user =~ ^[a-zA-Z_][a-zA-Z0-9_.-]*\$?$ ]] || die 'The service user must be an existing Linux account.'
    getent passwd "$service_user" >/dev/null || die 'The service user must be an existing Linux account.'
    runuser -u "$service_user" -- test -r "$runners" || die "$service_user cannot read the runner directory."
    runuser -u "$service_user" -- test -x "$runners" || die "$service_user cannot traverse the runner directory."
    # Serialize installs; never replace an unrelated service or a non-managed directory.
    exec 9>/run/lock/runner-room-install.lock
    flock -n 9 || die 'Another Runner Room installation is in progress.'
    if [[ -f $unit ]] && ! grep -qx '# Managed by the Runner Room installer' "$unit"; then
        die 'runner-room.service already exists and was not created by this installer. Back up and remove that service first.'
    fi
    [[ ! -e $prefix/current || -L $prefix/current ]] || die "$prefix/current must be an installer-managed symbolic link."
    work_dir=$(mktemp -d /tmp/runner-room-install.XXXXXXXX)
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
            systemctl daemon-reload || true
            if ((was_enabled)); then systemctl enable runner-room.service >/dev/null 2>&1 || true;
            else systemctl disable runner-room.service >/dev/null 2>&1 || true; fi
            if ((was_active)); then systemctl start runner-room.service || true; fi
        fi
        # work_dir is the exact private directory returned by mktemp above.
        [[ -z $work_dir ]] || rm -rf -- "$work_dir"
        flock -u 9 || true
        exit "$exit_code"
    }
    trap cleanup EXIT
    if [[ $version == latest ]]; then
        local redirected
        redirected=$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 3 --connect-timeout 15 \
            --output /dev/null --write-out '%{url_effective}' "https://github.com/$repo/releases/latest") || die 'No published release could be downloaded. The maintainer must publish a release first.'
        version=${redirected##*/}
        [[ $version =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][a-zA-Z0-9.-]+)?$ ]] || die 'Could not resolve the latest release tag.'
    fi
    asset="runner-room-$rid.tar.gz"
    base="https://github.com/$repo/releases/download/$version"
    printf 'Downloading Runner Room %s for %s…\n' "$version" "$rid"
    for file in "$asset" "$asset.sha256"; do
        curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --retry 3 --connect-timeout 15 \
            --output "$work_dir/$file" "$base/$file" || die "Could not download $file. Check that release $version has Linux assets."
    done
    expected=$(awk 'NR==1 {print $1}' "$work_dir/$asset.sha256")
    actual=$(sha256sum "$work_dir/$asset"); actual=${actual%% *}
    [[ $expected =~ ^[a-fA-F0-9]{64}$ && ${expected,,} == "$actual" ]] || die 'Download checksum does not match; installation was not changed.'
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
    chmod 755 "$release_dir/RunnerRoom"
    runuser -u "$service_user" -- "$release_dir/RunnerRoom" --check-runtime || die 'The bundled runtime cannot start. Use a supported glibc Linux distribution with its standard native libraries.'
    [[ ! -f $config ]] || cp -- "$config" "$work_dir/old.env"
    [[ ! -f $unit ]] || cp -- "$unit" "$work_dir/old.service"
    [[ ! -L $prefix/current ]] || previous=$(readlink -- "$prefix/current")
    systemctl is-active --quiet runner-room.service && was_active=1
    systemctl is-enabled --quiet runner-room.service && was_enabled=1
    changed=1
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
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=read-only

[Install]
WantedBy=multi-user.target
UNIT
    chmod 644 "$unit"
    ln -s -- "$release_dir" "$prefix/current.next"
    mv -Tf -- "$prefix/current.next" "$prefix/current"
    systemctl daemon-reload
    systemctl enable runner-room.service
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
    local address
    address=$(hostname -I 2>/dev/null || true); address=${address%% *}
    printf '\nRunner Room is running.\n  Dashboard: http://%s:%s\n  Runners:   %s\n  User:      %s\n  Settings:  %s\n' \
        "${address:-YOUR_SERVER_IP}" "$port" "$runners" "$service_user" "$config"
    printf '\nIf a firewall is enabled, allow this port from your LAN. The dashboard has no login.\n'
    cleanup
}

main "$@"
