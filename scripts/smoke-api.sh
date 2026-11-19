#!/usr/bin/env bash
# End-to-end API smoke test against a running stack. Usage: API_URL=http://localhost:8080 ADMIN_USER=.. ADMIN_PASSWORD=.. scripts/smoke-api.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -f "$ROOT/.env" ]] && { set -a; source "$ROOT/.env"; set +a; }
URL="${API_URL:-http://localhost:8080}"; U="${ADMIN_USER:-$BOOTSTRAP_ADMIN_USER}"; P="${ADMIN_PASSWORD:-$BOOTSTRAP_ADMIN_PASSWORD}"
pass=0; fail=0
check() { # name expected actual
  if [[ "$2" == "$3" ]]; then echo "  ok   $1"; pass=$((pass+1)); else echo "  FAIL $1 (expected $2, got $3)"; fail=$((fail+1)); fi; }
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
J() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }
login() { local b; b="$(printf '{"userCode":"%s","password":"%s","deviceName":"%s","platform":"%s"}' "$U" "$P" "$1" "${2:-web}")"; curl -s -X POST "$URL/api/v1/auth/login" -H 'content-type: application/json' -d "$b"; }

# Bodies are built in variables first: bash 3.2 (macOS /bin/bash) brace-expands comma-separated JSON nested inside $( ).
BAD_PW="$(printf '{"userCode":"%s","password":"wrong","deviceName":"x","platform":"web"}' "$U")"
BAD_PLATFORM="$(printf '{"userCode":"%s","password":"%s","deviceName":"x","platform":"toaster"}' "$U" "$P")"
echo "auth"
check "unauthenticated /me is 401" 401 "$(code "$URL/api/v1/me")"
check "unauthenticated customers is 401" 401 "$(code "$URL/api/v1/customers")"
check "health stays public" 200 "$(code "$URL/health/live")"
check "bad password is 401" 401 "$(code -X POST "$URL/api/v1/auth/login" -H 'content-type: application/json' -d "$BAD_PW")"
check "bad platform is 400" 400 "$(code -X POST "$URL/api/v1/auth/login" -H 'content-type: application/json' -d "$BAD_PLATFORM")"
R1="$(login "smoke-laptop" desktop)"; A1="$(echo "$R1" | J "d['accessToken']")"; F1="$(echo "$R1" | J "d['refreshToken']")"
check "login returns tokens" 1 "$([[ -n "$A1" && -n "$F1" ]] && echo 1 || echo 0)"
H1=(-H "Authorization: Bearer $A1")
check "/me works with token" 200 "$(code "${H1[@]}" "$URL/api/v1/me")"

echo "time zone"
check "set valid zone" 200 "$(code -X PUT "${H1[@]}" -H 'content-type: application/json' -d '{"timeZone":"America/New_York"}' "$URL/api/v1/me/settings")"
check "zone persisted" America/New_York "$(curl -s "${H1[@]}" "$URL/api/v1/me" | J "d['timeZone']")"
check "reject invalid zone" 400 "$(code -X PUT "${H1[@]}" -H 'content-type: application/json' -d '{"timeZone":"Mars/Olympus"}' "$URL/api/v1/me/settings")"
check "clear falls back to company" "$(curl -s "${H1[@]}" "$URL/api/v1/me" | J "d['companyTimeZone']")" "$(curl -s -X PUT "${H1[@]}" -H 'content-type: application/json' -d '{"timeZone":null}' "$URL/api/v1/me/settings" | J "d['timeZone']")"
check "timezone list has Chicago" True "$(curl -s "${H1[@]}" "$URL/api/v1/timezones" | J "'America/Chicago' in d")"

echo "customers"
check "list works" 200 "$(code "${H1[@]}" "$URL/api/v1/customers?limit=5")"
check "limit respected" 5 "$(curl -s "${H1[@]}" "$URL/api/v1/customers?limit=5" | J "len(d['items'])")"
FIRST="$(curl -s "${H1[@]}" "$URL/api/v1/customers?limit=1")"; ID="$(echo "$FIRST" | J "d['items'][0]['id']")"; LN="$(echo "$FIRST" | J "d['items'][0]['lastName']")"
check "detail works" 200 "$(code "${H1[@]}" "$URL/api/v1/customers/$ID")"
check "missing customer is 404" 404 "$(code "${H1[@]}" "$URL/api/v1/customers/999999999")"
check "name search finds a known customer" True "$(curl -s "${H1[@]}" -G --data-urlencode "q=${LN:0:4}" "$URL/api/v1/customers" | J "any(i['id']==$ID for i in d['items'])")"
check "LIKE wildcard is literal" 0 "$(curl -s "${H1[@]}" -G --data-urlencode "q=%" "$URL/api/v1/customers" | J "len(d['items'])")"
check "SQL-ish input is harmless" 200 "$(code "${H1[@]}" -G --data-urlencode "q='; DROP TABLE customer;--" "$URL/api/v1/customers")"

echo "documents"
check "unauthenticated documents is 401" 401 "$(code "$URL/api/v1/documents")"
check "list works" 200 "$(code "${H1[@]}" "$URL/api/v1/documents?limit=5")"
check "bad type is 400" 400 "$(code "${H1[@]}" "$URL/api/v1/documents?type=Z")"
check "type filter (invoices only)" True "$(curl -s "${H1[@]}" "$URL/api/v1/documents?type=I&limit=20" | J "all(i['type']=='I' for i in d['items']) and len(d['items'])==20")"
check "newest first" True "$(curl -s "${H1[@]}" "$URL/api/v1/documents?limit=50" | J "[i['date'] for i in d['items']]==sorted([i['date'] for i in d['items']],reverse=True)")"
DOC="$(curl -s "${H1[@]}" "$URL/api/v1/documents?type=I&limit=1")"; DID="$(echo "$DOC" | J "d['items'][0]['id']")"; DNUM="$(echo "$DOC" | J "d['items'][0]['number']")"; DTOT="$(echo "$DOC" | J "d['items'][0]['total']")"
DET="$(curl -s "${H1[@]}" "$URL/api/v1/documents/$DID")"
check "detail total equals list total" "$DTOT" "$(echo "$DET" | J "d['totals']['total']")"
check "detail total = lines + tax" True "$(echo "$DET" | J "round(sum(l['amount'] for l in d['lines'])+sum(t['amount'] for t in d['taxes']),2)==d['totals']['total']")"
check "document number search finds it" True "$(curl -s "${H1[@]}" -G --data-urlencode "q=$DNUM" "$URL/api/v1/documents" | J "any(i['id']==$DID for i in d['items'])")"
check "missing document is 404" 404 "$(code "${H1[@]}" "$URL/api/v1/documents/999999999")"
check "date range filter" True "$(curl -s "${H1[@]}" "$URL/api/v1/documents?from=2020-01-01&to=2020-12-31&limit=50" | J "all('2020-01-01'<=i['date']<='2020-12-31' for i in d['items'])")"
# Some real customer names contain a literal '%', so assert every hit really contains one (an unescaped wildcard would match all).
check "LIKE wildcard is literal" True "$(curl -s "${H1[@]}" -G --data-urlencode "q=%" --data-urlencode "limit=100" "$URL/api/v1/documents" | J "all('%' in ((i['customerName'] or '')+(i['poNumber'] or '')+i['number']) for i in d['items'])")"
check "SQL-ish input is harmless" 200 "$(code "${H1[@]}" -G --data-urlencode "q='; DROP TABLE document;--" "$URL/api/v1/documents")"
check "bad date is 400" 400 "$(code "${H1[@]}" "$URL/api/v1/documents?from=notadate")"

echo "sessions"
R2="$(login "smoke-ipad" ipados)"; A2="$(echo "$R2" | J "d['accessToken']")"; F2="$(echo "$R2" | J "d['refreshToken']")"
SID2="$(curl -s -H "Authorization: Bearer $A2" "$URL/api/v1/me/sessions" | J "[s['id'] for s in d if s['current']][0]")"
check "second device sees its own session as current" 1 "$([[ -n "$SID2" ]] && echo 1 || echo 0)"
check "device 1 revokes device 2" 204 "$(code -X DELETE "${H1[@]}" "$URL/api/v1/me/sessions/$SID2")"
check "revoked device is locked out" 401 "$(code -H "Authorization: Bearer $A2" "$URL/api/v1/me")"
check "revoked refresh token is dead" 401 "$(code -X POST "$URL/api/v1/auth/refresh" -H 'content-type: application/json' -d "{\"refreshToken\":\"$F2\"}")"
R3="$(curl -s -X POST "$URL/api/v1/auth/refresh" -H 'content-type: application/json' -d "{\"refreshToken\":\"$F1\"}")"; A3="$(echo "$R3" | J "d['accessToken']")"; F3="$(echo "$R3" | J "d['refreshToken']")"
check "refresh rotates tokens" 1 "$([[ -n "$A3" && "$A3" != "$A1" && "$F3" != "$F1" ]] && echo 1 || echo 0)"
check "old access token dies after rotation" 401 "$(code "${H1[@]}" "$URL/api/v1/me")"
check "old refresh token can't be reused" 401 "$(code -X POST "$URL/api/v1/auth/refresh" -H 'content-type: application/json' -d "{\"refreshToken\":\"$F1\"}")"
check "logout" 204 "$(code -X POST -H "Authorization: Bearer $A3" "$URL/api/v1/auth/logout")"
check "token dead after logout" 401 "$(code -H "Authorization: Bearer $A3" "$URL/api/v1/me")"

echo; echo "passed $pass, failed $fail"; exit $((fail > 0))
