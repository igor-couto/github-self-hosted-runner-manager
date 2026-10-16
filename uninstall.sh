#!/usr/bin/env bash
# Offline removal of installer-owned application files. Keep execution last so
# an incomplete download cannot begin uninstalling.
set -Eeuo pipefail

main() {
    local prefix=/opt/runner-room unit=/etc/systemd/system/runner-room.service
    local updater=/var/lib/runner-room-updater updater_unit
    local yes=0 directory manifest entry relative target answer current='' preserved=0
    local -a files=() directories=() manifests=()
    local -a updater_units=() updater_files=()
    die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
    usage() {
        cat <<'HELP'
Runner Room uninstaller (Linux with systemd)

  sudo bash /opt/runner-room/current/uninstall.sh [--yes]

  --yes    Uninstall without the confirmation prompt
  --help   Show this help

Stops and removes only the Runner Room dashboard service and recorded release
files. Keeps /etc/runner-room, /var/lib/runner-room, runner installations and
their services. No runners are unregistered from GitHub. Managed process-mode
runners may stop when the dashboard stops; independent runner services remain.
Older release folders without an ownership manifest are preserved.
HELP
    }
    while (($#)); do
        case "$1" in
            --yes) yes=1 ;;
            --help|-h) usage; return ;;
            *) die "Unknown argument: $1. Use --help." ;;
        esac
        shift
    done
    [[ $(uname -s) == Linux ]] || die 'This uninstaller requires Linux.'
    [[ $EUID == 0 ]] || die 'Run the uninstaller with sudo.'
    for command in systemctl flock stat realpath find grep rm rmdir; do
        command -v "$command" >/dev/null || die "Required Linux utility is missing: $command."
    done
    [[ -d /run/systemd/system ]] || die 'A running systemd host is required.'
    exec 9>/run/lock/runner-room-install.lock
    flock -n 9 || die 'Another Runner Room install or uninstall is in progress.'

    # Every removed file is below a root-owned, non-writable real directory.
    # Reject symlink ancestors instead of allowing an apparent /opt path to
    # resolve into runner data elsewhere on the server.
    trusted() {
        local path=$1
        [[ ! -L $path ]] || die "Refusing symbolic link: $path"
        [[ $(stat -c %u -- "$path") == 0 ]] || die "Expected a root-owned path: $path"
        (( (8#$(stat -c %a -- "$path") & 8#022) == 0 )) || die "Path is writable by other users: $path"
    }
    for directory in /opt "$prefix" "$prefix/releases" /etc /etc/systemd /etc/systemd/system /var /var/lib "$updater"; do
        [[ ! -L $directory ]] || die "Refusing symbolic link: $directory"
        if [[ -e $directory ]]; then
            [[ -d $directory ]] || die "Expected a directory: $directory"
            trusted "$directory"
        fi
    done
    # Remove only the bridge installed with this dashboard; do not follow links
    # or adopt unrelated services/scripts, even when their names happen to match.
    for target in "$prefix/installer.sh" "$prefix/update-service.sh"; do
        if [[ -e $target || -L $target ]]; then
            [[ -f $target ]] || die 'An updater helper is not a regular file.'
            trusted "$target"
            grep -qx '# Managed by the Runner Room installer' "$target" || die 'An updater helper is not installer-managed.'
            updater_files+=("$target")
        fi
    done
    for updater_unit in runner-room-update.path runner-room-update.service; do
        target=/etc/systemd/system/$updater_unit
        if [[ -e $target || -L $target ]]; then
            [[ -f $target ]] || die 'An updater unit is not a regular file.'
            trusted "$target"
            grep -qx '# Managed by the Runner Room installer' "$target" || die 'An updater unit is not installer-managed.'
            [[ $(systemctl show "$updater_unit" --property=FragmentPath --value) == "$target" ]] || die 'A loaded updater unit does not match this installation.'
            updater_units+=("$updater_unit")
        fi
    done
    if [[ -e $updater/capability.json || -L $updater/capability.json ]]; then
        trusted "$updater/capability.json"
        grep -qx '{"protocol":1}' "$updater/capability.json" || die 'The updater capability is not recognized.'
        updater_files+=("$updater/capability.json")
        if [[ -e $updater/status.json || -L $updater/status.json ]]; then
            [[ -f $updater/status.json ]] || die 'The updater status is not a regular file.'
            trusted "$updater/status.json"
            updater_files+=("$updater/status.json")
        fi
        if [[ -e $updater/processing || -L $updater/processing ]]; then
            [[ -f $updater/processing ]] || die 'The updater processing marker is not a regular file.'
            trusted "$updater/processing"
            updater_files+=("$updater/processing")
        fi
        [[ ! -L $updater/inbox && ( ! -e $updater/inbox || -d $updater/inbox ) ]] || die 'The updater inbox is not a real directory.'
    fi
    if [[ -e $unit || -L $unit ]]; then
        [[ -f $unit ]] || die 'runner-room.service is not a regular file.'
        trusted "$unit"
        if ! grep -qx '# Managed by the Runner Room installer' "$unit" ||
            ! grep -qx 'ExecStart=/opt/runner-room/current/RunnerRoom' "$unit" ||
            ! grep -qx 'WorkingDirectory=/opt/runner-room/current' "$unit"; then
            die 'runner-room.service was not created by this installer; nothing was removed.'
        fi
        # A locally replaced service or systemd alias must not stop another app.
        local fragment
        fragment=$(systemctl show runner-room.service --property=FragmentPath --value) || die 'Could not inspect runner-room.service.'
        [[ $fragment == "$unit" ]] || die 'The loaded service does not match the installer-managed unit.'
    elif [[ -e $prefix/current || -L $prefix/current ]]; then
        die 'The managed service is missing; refusing to remove an unverified installation.'
    fi
    if [[ -e $prefix/current || -L $prefix/current ]]; then
        [[ -L $prefix/current ]] || die 'The current application must be an installer-managed symbolic link.'
        current=$(realpath -e -- "$prefix/current") || die 'The current release link is broken.'
        [[ $current == "$prefix/releases/"* && ${current#"$prefix/releases/"} != */* ]] || die 'The current release points outside the release directory.'
        [[ -d $current && ! -L $current ]] || die 'The current release is not a real directory.'
        trusted "$current"
    fi

    # Preflight every manifest before stopping services or removing anything.
    # NUL separators preserve spaces; reject traversal, link and type changes.
    shopt -s nullglob dotglob
    for directory in "$prefix/releases/"*; do
        if [[ ! -d $directory || -L $directory || ! -f $directory/.runner-room-manifest ]]; then
            preserved=$((preserved + 1)); continue
        fi
        trusted "$directory"
        manifest=$directory/.runner-room-manifest
        trusted "$manifest"
        exec 8<"$manifest"
        IFS= read -r -d '' entry <&8 || die 'The release manifest is incomplete.'
        [[ $entry == runner-room-release-v1 ]] || die 'The release manifest has an unknown format.'
        while IFS= read -r -d '' entry <&8; do
            relative=${entry:1}
            case "$relative" in ''|/*|..|../*|*/../*|*/..|.|./*|*/./*|*/.|.runner-room-manifest) die 'Unsafe path in release manifest.' ;; esac
            [[ $relative != *$'\n'* && $relative != *$'\r'* ]] || die 'Unsafe characters in release manifest.'
            target=$directory/$relative
            # Missing entries are harmless when a prior uninstall was interrupted.
            if [[ -e $target || -L $target ]]; then
                [[ $(realpath -e -- "$target") == "$target" ]] || die "Release path was replaced by a link: $target"
                trusted "$target"
                local parent=${target%/*}
                while [[ $parent != "$directory" ]]; do
                    trusted "$parent"
                    parent=${parent%/*}
                done
                case "${entry:0:1}" in
                    f) [[ -f $target ]] || die "Release file changed type: $target"; files+=("$target") ;;
                    d) [[ -d $target ]] || die "Release directory changed type: $target"; directories+=("$target") ;;
                    *) die 'Unknown entry type in release manifest.' ;;
                esac
            fi
        done
        [[ -z $entry ]] || die 'The release manifest is truncated.'
        exec 8<&-
        manifests+=("$manifest")
        directories+=("$directory")
    done
    if [[ ! -e $unit && ${#manifests[@]} == 0 && ${#updater_files[@]} == 0 && ${#updater_units[@]} == 0 ]]; then
        printf 'Runner Room is already uninstalled. Settings, history and untracked files have been preserved.\n'
        return
    fi
    printf 'This removes the Runner Room dashboard. Settings, history, runner folders and runner services will be kept.\n'
    printf 'Runners launched by the dashboard in process mode may stop. No runners will be unregistered.\n'
    if ((!yes)); then
        [[ -t 0 ]] || die 'Confirmation requires a terminal. Use --yes for unattended uninstall.'
        read -r -p 'Uninstall Runner Room? Type yes to continue: ' answer
        [[ $answer == yes ]] || { printf 'Uninstall cancelled.\n'; return; }
    fi
    for updater_unit in "${updater_units[@]}"; do
        systemctl stop "$updater_unit" || die 'Could not stop the update helper; application files were kept.'
        systemctl disable "$updater_unit" || die 'Could not disable the update helper; application files were kept.'
    done
    if [[ -f $unit ]]; then
        systemctl stop runner-room.service || die 'Could not stop Runner Room; application files were kept.'
        # A service that failed to stop must retain its files.
        local state
        state=$(systemctl show runner-room.service --property=ActiveState --value) || die 'Could not confirm the service stopped; application files were kept.'
        [[ $state == inactive || $state == failed ]] || die 'Runner Room is still active; application files were kept.'
        systemctl disable runner-room.service || die 'Could not disable Runner Room; application files were kept.'
        rm -- "$unit"
        systemctl daemon-reload
    fi
    for updater_unit in "${updater_units[@]}"; do rm -- "/etc/systemd/system/$updater_unit"; done
    for target in "${updater_files[@]}"; do rm -- "$target"; done
    if ((${#updater_units[@]})); then systemctl daemon-reload; fi
    # The inbox belongs to the app account. Remove only the known request file;
    # keep unknown user files/directories and never recurse through this path.
    if [[ -d $updater/inbox && ! -L $updater/inbox ]]; then
        for target in "$updater/inbox/request" "$updater/inbox/request.lock"; do
            if [[ -f $target || -L $target ]]; then rm -f -- "$target"; fi
        done
        rmdir -- "$updater/inbox" 2>/dev/null || true
    fi
    rmdir -- "$updater" 2>/dev/null || true
    [[ ! -L $prefix/current ]] || rm -- "$prefix/current"
    for target in "${files[@]}"; do rm -- "$target"; done
    for manifest in "${manifests[@]}"; do rm -- "$manifest"; done
    for directory in "${directories[@]}"; do rmdir -- "$directory" 2>/dev/null || true; done
    rmdir -- "$prefix/releases" "$prefix" 2>/dev/null || true
    printf '\nRunner Room uninstalled. Preserved settings: /etc/runner-room\nPreserved history and managed runner state: /var/lib/runner-room\n'
    if ((preserved)) || [[ -d $prefix ]]; then
        printf 'Legacy releases or untracked files remain in /opt/runner-room; review them manually if you want to reclaim that space.\n'
    fi
}

main "$@"
