#!/usr/bin/env bash
# Managed by the Runner Room installer
# Root-owned systemd helper. The dashboard can request only a fixed latest-release
# update; it never supplies executable code, a URL, a path or a release version.
set -Eeuo pipefail

main() {
    [[ $EUID == 0 ]] || { printf 'The update service must run as root.\n' >&2; exit 1; }
    local bridge=/var/lib/runner-room-updater path
    for path in /var /var/lib "$bridge" /opt /opt/runner-room /opt/runner-room/installer.sh; do
        [[ ! -L $path && $(stat -c %u -- "$path") == 0 ]] || exit 1
        (( (8#$(stat -c %a -- "$path") & 8#022) == 0 )) || exit 1
    done
    [[ -d $bridge/inbox && ! -L $bridge/inbox ]] || exit 1
    exec 7>/run/lock/runner-room-update.lock
    flock -n 7 || exit 0
    [[ -e $bridge/inbox/request || -L $bridge/inbox/request ]] || exit 0
    local marker
    marker=$(mktemp "$bridge/processing.XXXXXXXX")
    printf '1\n' >"$marker"
    mv -Tf -- "$marker" "$bridge/processing"
    # Claim into the root-only directory before inspecting any untrusted inode.
    # This prevents following a swapped symlink and bounds all reads to 34 bytes.
    local claim_directory claimed request_id='' temporary
    claim_directory=$(mktemp -d "$bridge/request.XXXXXXXX")
    claimed=$claim_directory/request
    mv -T -- "$bridge/inbox/request" "$claimed" || { rmdir -- "$claim_directory"; rm -f -- "$bridge/processing"; exit 1; }
    if [[ -f $claimed && ! -L $claimed && $(stat -c %s -- "$claimed") == 33 ]]; then
        if ! IFS= read -r request_id < <(head -c 34 -- "$claimed") || [[ ! $request_id =~ ^[a-f0-9]{32}$ ]]; then request_id=''; fi
    fi
    # Always consume invalid requests, including links, to avoid path-unit loops.
    if [[ -d $claimed && ! -L $claimed ]]; then rmdir -- "$claimed" 2>/dev/null || true;
    else rm -f -- "$claimed"; fi
    rmdir -- "$claim_directory" 2>/dev/null || true
    [[ -n $request_id ]] || { rm -f -- "$bridge/processing"; printf 'Invalid dashboard update request.\n' >&2; exit 1; }
    write_status() {
        temporary=$(mktemp "$bridge/status.XXXXXXXX")
        printf '{"requestId":"%s","state":"%s","message":"%s","updatedAt":"%s"}\n' \
            "$request_id" "$1" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$temporary"
        chmod 644 "$temporary"
        mv -Tf -- "$temporary" "$bridge/status.json"
    }
    write_status checking 'Checking the latest Runner Room release.'
    if ! /bin/bash /opt/runner-room/installer.sh --update --request-id "$request_id" 7>&-; then
        write_status failed 'The update failed. Check the updater service log; the dashboard may need attention.'
        rm -f -- "$bridge/processing"
        exit 1
    fi
    rm -f -- "$bridge/processing"
}

main "$@"
