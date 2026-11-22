#!/usr/bin/env bash
# Document workflow + ledger test: quote -> work order -> invoice -> payment -> credit memo -> voids, against a running stack.
# Creates a throw-away customer and removes everything it made (needs the Docker db service for the cleanup).
# Usage: scripts/smoke-workflow.sh
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
LB="$(printf '{"userCode":"%s","password":"%s","deviceName":"smoke-workflow","platform":"desktop"}' "$U" "$P")"
TOK="$(curl -s -X POST "$URL/auth/login" -H 'content-type: application/json' -d "$LB" | J "d['accessToken']")"
H=(-H "Authorization: Bearer $TOK" -H 'content-type: application/json')
api() { local m="$1" p="$2"; shift 2; curl -s -X "$m" "${H[@]}" "$URL$p" "$@"; }
code() { local m="$1" p="$2"; shift 2; curl -s -o /dev/null -w '%{http_code}' -X "$m" "${H[@]}" "$URL$p" "$@"; }
sql() { docker compose --project-directory "$ROOT" exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db psql -X -q -tA -U "${POSTGRES_USER:-reflectopatronus_admin}" -d "${POSTGRES_DB:-reflectopatronus}" -c "$1"; }

CODE="ZZWF$RANDOM"
CUST="$(api POST /customers -d "{\"number\":\"$CODE\",\"type\":\"A\",\"lastName\":\"Workflow Test\",\"taxExempt\":true,\"phones\":[]}" | J "d['id']")"
cleanup() {
  sql "DELETE FROM ar_entry WHERE customer_id = $CUST; DELETE FROM payment WHERE customer_id = $CUST; DELETE FROM document WHERE customer_id = $CUST; DELETE FROM customer WHERE id = $CUST;" >/dev/null
}
trap cleanup EXIT

echo "quote -> work order -> invoice"
QB="{\"customerId\":$CUST,\"taxGroup\":null,\"lines\":[{\"source\":\"labor\",\"qty\":1,\"description\":\"Test labor\",\"amount\":100.00},{\"source\":\"custom\",\"qty\":2,\"description\":\"Test item\",\"amount\":50.00}]}"
Q="$(api POST /documents -d "$QB")"; QID="$(echo "$Q" | J "d['id']")"
check "quote total" 150.0 "$(echo "$Q" | J "d['total']")"
W="$(api POST "/documents/$QID/convert" -d '{"to":"W"}')"; WID="$(echo "$W" | J "d['id']")"
check "work order created" W "$(echo "$W" | J "d['type']")"
check "work order keeps the total" 150.0 "$(echo "$W" | J "d['total']")"
check "work order links back to the quote" "$(api GET "/documents/$QID" | J "d['number']")" "$(api GET "/documents/$WID" | J "d['sourceDocumentNumber']")"
check "second conversion of the same quote is refused" 409 "$(code POST "/documents/$QID/convert" -d '{"to":"W"}')"
check "work order is editable" True "$(api GET "/documents/$WID" | J "d['editable']")"
check "no ledger entry for a work order" 0 "$(sql "SELECT count(*) FROM ar_entry WHERE document_id = $WID")"
check "work order cannot become a credit" 409 "$(code POST "/documents/$WID/convert" -d '{"to":"C"}')"
INV="$(api POST "/documents/$WID/convert" -d '{"to":"I"}')"
check "invoice reuses the work order row" "$WID" "$(echo "$INV" | J "d['id']")"
check "invoice keeps the work order number" "$(echo "$W" | J "d['number']")" "$(echo "$INV" | J "d['number']")"
check "invoice posts a debit" 150.0 "$(api GET "/documents/$WID" | J "d['balance']")"
check "customer balance" 150.0 "$(api GET "/customers/$CUST" | J "d['balance']")"

echo "editing an invoice adjusts the ledger"
D="$(api GET "/documents/$WID")"
EB="$(echo "$D" | python3 -c "
import sys,json; d=json.load(sys.stdin)
lines=[{'source':l['source'],'qty':l['qty'],'description':l['description'],'amount':l['amount']} for l in d['lines']]
lines.append({'source':'labor','qty':1,'description':'Extra','amount':25.0})
print(json.dumps({'customerId':d['customerId'],'taxGroup':d['taxGroup'],'priceCategory':d['priceCategory'],'discountPercent':None,'lines':lines,'updatedAt':d['updatedAt']}))")"
check "edit saved" 175.0 "$(api PUT "/documents/$WID" -d "$EB" | J "d['total']")"
check "balance follows the edit" 175.0 "$(api GET "/documents/$WID" | J "d['balance']")"

echo "payments"
B_OVER="$(printf '{"customerId":%s,"amount":500,"applications":[{"documentId":%s,"amount":500}]}' "$CUST" "$WID")"
check "overpaying a document is refused" 409 "$(code POST /payments -d "$B_OVER")"
B_MORE="$(printf '{"customerId":%s,"amount":10,"applications":[{"documentId":%s,"amount":20}]}' "$CUST" "$WID")"
check "applying more than received is refused" 400 "$(code POST /payments -d "$B_MORE")"
B_PAY="$(printf '{"customerId":%s,"amount":75,"type":"P","category":"B","description":"check 1001","applications":[{"documentId":%s,"amount":75}]}' "$CUST" "$WID")"
PAY="$(api POST /payments -d "$B_PAY")"; PID="$(echo "$PAY" | J "d['id']")"
check "payment applied" 75 "$(echo "$PAY" | J "d['applied']")"
check "invoice balance after payment" 100.0 "$(api GET "/documents/$WID" | J "d['balance']")"
check "payment shows as paid" 75.0 "$(api GET "/documents/$WID" | J "d['paid']")"
check "paid invoice cannot be voided" 409 "$(code POST "/documents/$WID/void")"
B_AUTO="$(printf '{"customerId":%s,"amount":120,"autoApply":true}' "$CUST")"
PAY2="$(api POST /payments -d "$B_AUTO")"
check "auto-apply pays the open balance" 100 "$(echo "$PAY2" | J "d['applied']")"
check "the rest is held on account" 20 "$(echo "$PAY2" | J "d['onAccount']")"
ACC="$(api GET "/customers/$CUST/account")"
check "balance is negative (on account)" -20 "$(echo "$ACC" | J "d['balance']")"
check "on account shown" 20 "$(echo "$ACC" | J "d['onAccount']")"
check "voiding a payment needs admin (admin here)" 204 "$(code POST "/payments/$(echo "$PAY2" | J "d['id']")/void")"
check "voided payment restores the balance" 100.0 "$(api GET "/customers/$CUST" | J "d['balance']")"
check "voiding twice is refused" 409 "$(code POST "/payments/$(echo "$PAY2" | J "d['id']")/void")"

echo "credit memo"
CM="$(api POST "/documents/$WID/convert" -d '{"to":"C","lineSeqs":[1]}')"; CID="$(echo "$CM" | J "d['id']")"
check "credit memo total is negative" -100.0 "$(echo "$CM" | J "d['total']")"
check "credit memo nets to zero on its own" 0.0 "$(api GET "/documents/$CID" | J "d['balance']")"
check "invoice reduced by the credit" 0.0 "$(api GET "/documents/$WID" | J "d['balance']")"
check "customer balance after credit" 0.0 "$(api GET "/customers/$CUST" | J "d['balance']")"
check "credit memo is not editable" False "$(api GET "/documents/$CID" | J "d['editable']")"
check "voiding the credit restores the invoice" 204 "$(code POST "/documents/$CID/void")"
check "invoice owes again" 100.0 "$(api GET "/documents/$WID" | J "d['balance']")"

echo "voids"
check "void the payment" 204 "$(code POST "/payments/$PID/void")"
check "now the invoice can be voided" 204 "$(code POST "/documents/$WID/void")"
check "balance cleared by void" 0.0 "$(api GET "/customers/$CUST" | J "d['balance']")"
check "voided invoice is not editable" False "$(api GET "/documents/$WID" | J "d['editable']")"
check "quote can still be voided" 204 "$(code POST "/documents/$QID/void")"
check "receivables list works" 200 "$(code GET "/receivables")"

echo; echo "passed $pass, failed $fail"; [[ $fail -eq 0 ]]
