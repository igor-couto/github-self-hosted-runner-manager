#!/usr/bin/env bash
set -euo pipefail
# Exercise preflight through the public CLI, stopping at the download boundary.
mock_dir=$(mktemp -d)
trap 'rm -rf -- "$mock_dir"' EXIT
cat >"$mock_dir/uname" <<'SH'
#!/usr/bin/env bash
case "$1" in
    -s) echo Linux ;;
    -m) echo "$TEST_MACHINE" ;;
    *) exec /usr/bin/uname "$@" ;;
esac
SH
cat >"$mock_dir/getconf" <<'SH'
#!/usr/bin/env bash
echo "$TEST_BITS"
SH
cat >"$mock_dir/curl" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$@" >>"$TEST_DOWNLOAD_LOG"
exit 22
SH
chmod +x "$mock_dir/"*
export TEST_DOWNLOAD_LOG="$mock_dir/download.log"
for option in --runners --port --user --version; do
    for kind in missing empty next-option; do
        args=("$option")
        case "$kind" in empty) args+=('') ;; next-option) args+=(--help) ;; esac
        if bash /fixture/install.sh "${args[@]}" >"$mock_dir/output" 2>&1; then
            echo "Missing value was accepted: $option ($kind)"; exit 1
        fi
        grep -F "Missing value for $option." "$mock_dir/output" >/dev/null
        if grep -F 'unbound variable' "$mock_dir/output"; then exit 1; fi
    done
done
while read -r machine bits rid; do
    : >"$TEST_DOWNLOAD_LOG"
    if PATH="$mock_dir:$PATH" TEST_MACHINE="$machine" TEST_BITS="$bits" \
        bash /fixture/install.sh --runners /home/runner --user runner --version v0.1.0 >"$mock_dir/output" 2>&1; then
        echo 'The test download was supposed to fail.'; exit 1
    fi
    if [[ $rid == unsupported ]]; then
        test ! -s "$TEST_DOWNLOAD_LOG"
        grep -E 'not supported|64-bit Linux userspace' "$mock_dir/output" >/dev/null
    else
        grep -Fx "https://github.com/igor-couto/github-self-hosted-runner-manager/releases/download/v0.1.0/runner-room-$rid.tar.gz" "$TEST_DOWNLOAD_LOG" >/dev/null
    fi
done <<'CASES'
x86_64 64 linux-x64
armv7l 32 linux-arm
armv8l 32 linux-arm
aarch64 64 linux-arm64
aarch64 32 linux-arm
arm64 64 linux-arm64
armv6l 32 unsupported
x86_64 32 unsupported
CASES
echo 'PASS: missing arguments and x64/ARM32/ARM64 platform selection.'
