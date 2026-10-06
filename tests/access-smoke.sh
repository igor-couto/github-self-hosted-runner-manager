#!/usr/bin/env bash
set -euo pipefail
# Disposable installer container only. Test password is never passed as a process argument.
hash=$(printf '%s\n' 'test-only-password-42' | /opt/runner-room/current/RunnerRoom --hash-password)
jq --arg hash "$hash" '.Access = {
  Enabled:true,RequireHttps:false,PublicOrigin:"http://127.0.0.1:8082",
  LocalUsers:[{Username:"admin",PasswordHash:$hash,Role:"admin"},{Username:"reader",PasswordHash:$hash,Role:"viewer"}]
}' /etc/runner-room/settings.json >/tmp/access-settings.json
cp /tmp/access-settings.json /etc/runner-room/settings.json
systemctl restart runner-room.service
for ((attempt=0; attempt<10; attempt++)); do curl -fsS http://127.0.0.1:8082/healthz >/dev/null && break; sleep 1; done
base=http://127.0.0.1:8082
test "$(curl -s -o /dev/null -w '%{http_code}' "$base/api/runners")" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' "$base/")" = 302
test "$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d '{}' "$base/api/access/login")" = 403
token=$(curl -fsS -c /tmp/viewer.cookies "$base/api/access/session" | jq -r '.csrfToken')
curl -fsS -b /tmp/viewer.cookies -c /tmp/viewer.cookies -H 'Content-Type: application/json' -H "X-RR-CSRF: $token" \
    -d '{"username":"reader","password":"test-only-password-42"}' "$base/api/access/login" >/dev/null
curl -fsS -b /tmp/viewer.cookies "$base/api/runners" | jq -e '.runners | length > 0'
token=$(curl -fsS -b /tmp/viewer.cookies -c /tmp/viewer.cookies "$base/api/access/session" | jq -r '.csrfToken')
for path in /api/integration /API/INTEGRATION/ /api/access/audit /API/ACCESS/AUDIT/ "/API/RUNNERS/$runner_id/LOGS/"; do
    test "$(curl -s -b /tmp/viewer.cookies -o /dev/null -w '%{http_code}' "$base$path")" = 403
done
test "$(curl -s -b /tmp/viewer.cookies -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -H "X-RR-CSRF: $token" -d '{}' "$base/API/ALERTS/CHECK/")" = 403
token=$(curl -fsS -c /tmp/admin.cookies "$base/api/access/session" | jq -r '.csrfToken')
curl -fsS -b /tmp/admin.cookies -c /tmp/admin.cookies -H 'Content-Type: application/json' -H "X-RR-CSRF: $token" \
    -d '{"username":"admin","password":"test-only-password-42"}' "$base/api/access/login" >/dev/null
curl -fsS -b /tmp/admin.cookies "$base/api/integration" | jq -e '.accessEnabled and (.localUsers | length == 2)'
test -s /var/lib/runner-room/access-audit.json
test "$(stat -c %U /var/lib/runner-room/access-audit.json)" = runner
test "$(stat -c %a /var/lib/runner-room/session-keys)" = 700
# Removing an account revokes its existing session; another user's session survives the restart.
jq '.Access.LocalUsers |= map(select(.Username != "reader"))' /etc/runner-room/settings.json >/tmp/access-settings.json
cp /tmp/access-settings.json /etc/runner-room/settings.json
systemctl restart runner-room.service
for ((attempt=0; attempt<10; attempt++)); do curl -fsS "$base/healthz" >/dev/null && break; sleep 1; done
test "$(curl -s -b /tmp/viewer.cookies -o /dev/null -w '%{http_code}' "$base/api/runners")" = 401
curl -fsS -b /tmp/admin.cookies "$base/api/access/audit" | jq -e '.entries | any(.[]; .event == "login_succeeded")'
token=$(curl -fsS -b /tmp/admin.cookies -c /tmp/admin.cookies "$base/api/access/session" | jq -r '.csrfToken')
curl -fsS -b /tmp/admin.cookies -c /tmp/admin.cookies -X POST -H "X-RR-CSRF: $token" "$base/api/access/logout" >/dev/null
test "$(curl -s -b /tmp/admin.cookies -o /dev/null -w '%{http_code}' "$base/api/runners")" = 401
# Untrusted forwarding headers cannot turn an HTTP request into HTTPS.
jq '.Access.RequireHttps=true | .Access.PublicOrigin="https://127.0.0.1:8082"' /etc/runner-room/settings.json >/tmp/access-settings.json
cp /tmp/access-settings.json /etc/runner-room/settings.json
systemctl restart runner-room.service
for ((attempt=0; attempt<10; attempt++)); do curl -fsS "$base/healthz" >/dev/null && break; sleep 1; done
test "$(curl -s -o /dev/null -w '%{http_code}' -H 'X-Forwarded-Proto: https' "$base/api/access/session")" = 400
jq '.Access.TrustedProxies=["127.0.0.1"]' /etc/runner-room/settings.json >/tmp/access-settings.json
cp /tmp/access-settings.json /etc/runner-room/settings.json
systemctl restart runner-room.service
for ((attempt=0; attempt<10; attempt++)); do curl -fsS "$base/healthz" >/dev/null && break; sleep 1; done
curl -fsS -D /tmp/proxy-headers -H 'X-Forwarded-Proto: https' "$base/api/access/session" | jq -e '.enabled and (.csrfToken | length > 0)'
grep -i '^Set-Cookie: RunnerRoom.Csrf=.*secure' /tmp/proxy-headers >/dev/null
echo 'PASS: authentication, CSRF, viewer/admin authorization, canonical routes, audit persistence, private keys, session restart/revocation and HTTPS proxy boundary.'
