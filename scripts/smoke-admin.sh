#!/usr/bin/env bash
# Catalog, pricing, users, company, reports, credits and transfer-job checks against a running stack. Cleans up after itself.
# Usage: scripts/smoke-admin.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -f "$ROOT/.env" ]] && { set -a; source "$ROOT/.env"; set +a; }
URL="${API_URL:-http://localhost:8080}/api/v1"; U="${ADMIN_USER:-$BOOTSTRAP_ADMIN_USER}"; P="${ADMIN_PASSWORD:-$BOOTSTRAP_ADMIN_PASSWORD}"
pass=0; fail=0
same() { python3 -c "import sys
a,b=sys.argv[1:3]
try: sys.exit(0 if float(a)==float(b) else 1)
except ValueError: sys.exit(0 if a==b else 1)" "$1" "$2"; }
check() { if same "$2" "$3"; then echo "  ok   $1"; pass=$((pass+1)); else echo "  FAIL $1 (expected $2, got $3)"; fail=$((fail+1)); fi; }
J() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }
LB="$(printf '{"userCode":"%s","password":"%s","deviceName":"smoke-admin","platform":"desktop"}' "$U" "$P")"
TOK="$(curl -s -X POST "$URL/auth/login" -H 'content-type: application/json' -d "$LB" | J "d['accessToken']")"
H=(-H "Authorization: Bearer $TOK" -H 'content-type: application/json')
api() { local m="$1" p="$2"; shift 2; curl -s -X "$m" "${H[@]}" "$URL$p" "$@"; }
code() { local m="$1" p="$2"; shift 2; curl -s -o /dev/null -w '%{http_code}' -X "$m" "${H[@]}" "$URL$p" "$@"; }
sql() { docker compose --project-directory "$ROOT" exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db psql -X -q -tA -U "${POSTGRES_USER:-reflectopatronus_admin}" -d "${POSTGRES_DB:-reflectopatronus}" -c "$1"; }
CODE="ZZ$RANDOM"; UCODE="zzu$RANDOM"; CUST=""; LOSS_BACKUP="$(api GET /catalog/loss)"
cleanup() {
  [[ -n "$CUST" ]] && sql "DELETE FROM ar_entry WHERE customer_id=$CUST; DELETE FROM payment WHERE customer_id=$CUST; DELETE FROM document WHERE customer_id=$CUST; DELETE FROM customer WHERE id=$CUST;" >/dev/null
  sql "DELETE FROM part WHERE code LIKE 'ZZ%'; DELETE FROM device_session WHERE user_id IN (SELECT id FROM app_user WHERE code LIKE 'zzu%'); DELETE FROM app_user WHERE code LIKE 'zzu%';" >/dev/null
  api PUT /catalog/loss -d "$LOSS_BACKUP" >/dev/null
}
trap cleanup EXIT

echo "catalog"
B1="$(printf '{"code":"%s","source":"G","description":"Smoke glass","thicknesses":[{"num":1,"den":4,"prices":[{"category":null,"price1":10,"minPrice":5,"taxable":true},{"category":"1RTL","price1":12,"taxable":true}]},{"num":3,"den":8,"prices":[{"category":null,"price1":15,"taxable":true}]}]}' "$CODE")"
R="$(api POST /catalog/items -d "$B1")"; PID="$(echo "$R" | J "d['id']")"
check "create part" 1 "$([[ -n "$PID" ]] && echo 1 || echo 0)"
check "duplicate code is refused" 409 "$(code POST /catalog/items -d "$B1")"
D="$(api GET "/catalog/items/$PID")"
check "two thicknesses saved" 2 "$(echo "$D" | J "len(d['thicknesses'])")"
check "category price saved" 12 "$(echo "$D" | J "[p for t in d['thicknesses'] if t['num']==1 for p in t['prices'] if p['category']=='1RTL'][0]['price1']")"
check "search finds it" 1 "$(api GET "/catalog/items?q=$CODE" | J "len(d)")"
UB="$(echo "$D" | python3 -c "
import sys,json; d=json.load(sys.stdin)
d['description']='Edited'; d['thicknesses']=[t for t in d['thicknesses'] if t['num']==1]
d['thicknesses'][0]['prices']=[p for p in d['thicknesses'][0]['prices'] if p['category'] is None]; d['thicknesses'][0]['prices'][0]['price1']=11
print(json.dumps(d))")"
check "update part" 204 "$(code PUT "/catalog/items/$PID" -d "$UB")"
D2="$(api GET "/catalog/items/$PID")"
check "removed thickness is gone" 1 "$(echo "$D2" | J "len(d['thicknesses'])")"
check "removed price is gone" 1 "$(echo "$D2" | J "len(d['thicknesses'][0]['prices'])")"
check "price changed" 11 "$(echo "$D2" | J "d['thicknesses'][0]['prices'][0]['price1']")"
check "stale save is refused" 409 "$(code PUT "/catalog/items/$PID" -d "$UB")"
BAD_CAT="$(echo "$D2" | python3 -c "
import sys,json
d=json.load(sys.stdin); extra=dict(category='NOPE',price1=1,taxable=True); d['thicknesses'][0]['prices'].append(extra); print(json.dumps(d))")"
check "bad category is refused" 400 "$(code PUT "/catalog/items/$PID" -d "$BAD_CAT")"
BAD_PRICE="$(echo "$D2" | python3 -c "
import sys,json
d=json.load(sys.stdin); d['thicknesses'][0]['prices'][0]['price1']=-1; print(json.dumps(d))")"
check "negative price is refused" 400 "$(code PUT "/catalog/items/$PID" -d "$BAD_PRICE")"

echo "pricing follows the catalog"
CUST="$(api POST /customers -d "{\"number\":\"$CODE\",\"type\":\"A\",\"lastName\":\"Admin Smoke\",\"taxExempt\":true,\"phones\":[]}" | J "d['id']")"
LB2="$(printf '{"customerId":%s,"lines":[{"source":"glass","qty":1,"partId":%s,"thickNum":1,"thickDen":4,"widthIn":12,"heightIn":12}]}' "$CUST" "$PID")"
check "glass line is priced from the new catalog row (12x12 + 2in waste at 11/sq ft)" 14.97 "$(api POST /documents/calculate -d "$LB2" | J "d['subtotal']")"
check "special price saved" 204 "$(code PUT "/customers/$CUST/prices" -d "$(printf '[{"partId":%s,"thickNum":1,"thickDen":4,"priceCategory":null,"price":40}]' "$PID")")"
check "special price listed" 40 "$(api GET "/customers/$CUST/prices" | J "d[0]['price']")"
check "special price now used (40/sq ft)" 54.44 "$(api POST /documents/calculate -d "$LB2" | J "d['subtotal']")"
check "part on a document cannot be deleted" 409 "$(code DELETE "/catalog/items/$(api GET '/catalog/items?q=LAM' | J "d[0]['id']")")"
check "delete the new part" 204 "$(code DELETE "/catalog/items/$PID")"

echo "loss table"
check "loss table readable" 200 "$(code GET /catalog/loss)"
check "bad loss row refused" 400 "$(code PUT /catalog/loss -d '[{"thickNum":1,"thickDen":0,"lossInt":0,"lossNum":0,"lossDen":1}]')"
check "duplicate loss rows refused" 400 "$(code PUT /catalog/loss -d '[{"thickNum":1,"thickDen":4,"lossInt":1,"lossNum":0,"lossDen":1},{"thickNum":1,"thickDen":4,"lossInt":2,"lossNum":0,"lossDen":1}]')"

echo "users and passwords"
NU="$(api POST /users -d "$(printf '{"code":"%s","name":"Smoke User","password":"correct horse battery","isAdmin":false,"active":true}' "$UCODE")")"; UID_="$(echo "$NU" | J "d['id']")"
check "short password refused" 400 "$(code POST /users -d '{"code":"zzushort","password":"short","isAdmin":false,"active":true}')"
check "duplicate user refused" 409 "$(code POST /users -d "$(printf '{"code":"%s","password":"correct horse battery","isAdmin":false,"active":true}' "$UCODE")")"
UT="$(curl -s -X POST "$URL/auth/login" -H 'content-type: application/json' -d "$(printf '{"userCode":"%s","password":"correct horse battery","deviceName":"u","platform":"desktop"}' "$UCODE")" | J "d['accessToken']")"
UH=(-H "Authorization: Bearer $UT" -H 'content-type: application/json')
check "new user signs in" 200 "$(curl -s -o /dev/null -w '%{http_code}' "${UH[@]}" "$URL/me")"
check "non-admin cannot list users" 403 "$(curl -s -o /dev/null -w '%{http_code}' "${UH[@]}" "$URL/users")"
check "non-admin cannot change the catalog" 403 "$(curl -s -o /dev/null -w '%{http_code}' -X PUT "${UH[@]}" "$URL/catalog/loss" -d '[]')"
check "wrong current password refused" 403 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "${UH[@]}" "$URL/me/password" -d '{"current":"nope nope nope nope","new":"another long password"}')"
check "change own password" 204 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "${UH[@]}" "$URL/me/password" -d '{"current":"correct horse battery","new":"another long password"}')"
check "old password no longer works" 401 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$URL/auth/login" -H 'content-type: application/json' -d "$(printf '{"userCode":"%s","password":"correct horse battery","deviceName":"u","platform":"desktop"}' "$UCODE")")"
check "admin resets the password" 204 "$(code POST "/users/$UID_/password" -d '{"password":"reset by the admin ok"}')"
check "token is revoked by the reset" 401 "$(curl -s -o /dev/null -w '%{http_code}' "${UH[@]}" "$URL/me")"
check "deactivate user" 204 "$(code PUT "/users/$UID_" -d '{"name":"Smoke User","active":false,"isAdmin":false}')"
check "inactive user cannot sign in" 401 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$URL/auth/login" -H 'content-type: application/json' -d "$(printf '{"userCode":"%s","password":"reset by the admin ok","deviceName":"u","platform":"desktop"}' "$UCODE")")"
ME="$(api GET /me | J "d['id']")"
check "cannot demote yourself" 409 "$(code PUT "/users/$ME" -d '{"name":"x","active":true,"isAdmin":false}')"

echo "company"
CO="$(api GET /company)"
check "company readable" 200 "$(code GET /company)"
check "company update" 204 "$(code PUT /company -d "$CO")"
check "company needs a name" 400 "$(code PUT /company -d "$(echo "$CO" | python3 -c "import sys,json; d=json.load(sys.stdin); d['name']=''; print(json.dumps(d))")")"

echo "credits"
PAY="$(api POST /payments -d "$(printf '{"customerId":%s,"amount":100}' "$CUST")")"
check "on-account payment" 100 "$(echo "$PAY" | J "d['onAccount']")"
QB="$(printf '{"customerId":%s,"lines":[{"source":"labor","qty":1,"description":"x","amount":60}]}' "$CUST")"
QID="$(api POST /documents -d "$QB" | J "d['id']")"; api POST "/documents/$QID/convert" -d '{"to":"I"}' >/dev/null
check "nothing to apply to a paid-up balance means credit remains" -40 "$(api GET "/customers/$CUST" | J "d['balance']")"
QID2="$(api POST /documents -d "$(printf '{"customerId":%s,"lines":[{"source":"labor","qty":1,"description":"y","amount":25}]}' "$CUST")" | J "d['id']")"; api POST "/documents/$QID2/convert" -d '{"to":"I"}' >/dev/null
check "balance after a second invoice" -15 "$(api GET "/customers/$CUST" | J "d['balance']")"
check "apply credits (the 85 owed is paid from the 100 held)" 85 "$(api POST "/customers/$CUST/apply-credits" | J "d['applied']")"
check "customer balance unchanged by applying" -15 "$(api GET "/customers/$CUST" | J "d['balance']")"
check "no document is left owing" 0 "$(api GET "/customers/$CUST/account" | J "len([x for x in d['documents'] if x['balance']>0])")"

echo "reports"
for r in $(api GET /reports | J "' '.join(x['id'] for x in d)"); do
  case "$r" in customer-history) q="?customerId=$CUST" ;; *) q="?from=2020-01-01&to=2030-12-31" ;; esac
  check "report $r runs" 200 "$(code GET "/reports/$r$q")"
done
check "customer history needs a customer" 400 "$(code GET /reports/customer-history)"
check "end before start is refused" 400 "$(code GET '/reports/sales-breakdown?from=2026-02-01&to=2026-01-01')"
check "unknown report is 404" 404 "$(code GET /reports/nope)"
check "history lists the 2 quotes and 2 invoices" 4 "$(api GET "/reports/customer-history?customerId=$CUST" | J "len(d['rows'])")"

echo "clean-up tools (counts only)"
check "count old quotes" 200 "$(code POST /utilities/delete-quotes -d '{"before":"1990-01-01","dryRun":true}')"
check "quote count needs a date" 400 "$(code POST /utilities/delete-quotes -d '{"dryRun":true}')"
check "count inactive customers" 200 "$(code POST /utilities/delete-inactive-customers -d '{"dryRun":true}')"

echo; echo "passed $pass, failed $fail"; [[ $fail -eq 0 ]]
