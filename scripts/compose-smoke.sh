#!/usr/bin/env bash
# End-to-end smoke test against the docker-compose stack: log in, hold a seat, pay, and check the email.
# Catches what unit and integration tests cannot: container networking, issuer configuration, wiring.
set -euo pipefail

GATEWAY=${GATEWAY:-http://localhost:5250}
KEYCLOAK=${KEYCLOAK:-http://localhost:5260}
MAILPIT=${MAILPIT:-http://localhost:5261}
EVENT=0197a000-0000-7000-8000-000000000001

json() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }

token=$(curl -sf "$KEYCLOAK/realms/tickethub/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=tickethub-cli -d username=alice -d password=password | json 'd["access_token"]')
echo "✓ logged in as alice"

seat=$(curl -sf "$GATEWAY/catalog/events/$EVENT/seat-map" \
  | json '[s["seatId"] for sec in d["sections"] for r in sec["rows"] for s in r["seats"] if s["status"]=="Available"][-1]')

reservation=$(curl -sf -X POST "$GATEWAY/booking/reservations" -H "Authorization: Bearer $token" \
  -H 'Content-Type: application/json' -d "{\"eventId\":\"$EVENT\",\"seatIds\":[\"$seat\"]}" | json 'd["id"]')
echo "✓ held seat $seat (reservation $reservation)"

curl -sf -X POST "$GATEWAY/booking/reservations/$reservation/checkout" -H "Authorization: Bearer $token" \
  -H 'Content-Type: application/json' -d '{"paymentToken":"test-card-approve"}' > /dev/null
echo "✓ checkout accepted"

for _ in $(seq 1 30); do
  status=$(curl -sf "$GATEWAY/booking/reservations/$reservation" -H "Authorization: Bearer $token" | json 'd["status"]')
  [ "$status" = "Confirmed" ] && break
  sleep 1
done
[ "$status" = "Confirmed" ] || { echo "✗ reservation is $status"; exit 1; }
echo "✓ reservation confirmed"

for _ in $(seq 1 30); do
  sold=$(curl -sf "$GATEWAY/catalog/events/$EVENT/seat-map" | json "[s['status'] for sec in d['sections'] for r in sec['rows'] for s in r['seats'] if s['seatId']=='$seat'][0]")
  [ "$sold" = "Sold" ] && break
  sleep 1
done
[ "$sold" = "Sold" ] || { echo "✗ seat map shows $sold"; exit 1; }
echo "✓ seat map shows the seat as sold"

for _ in $(seq 1 30); do
  emails=$(curl -sf "$MAILPIT/api/v1/search?query=subject:%22Your%20tickets%20are%20confirmed%22%20$reservation" | json 'd["messages_count"]')
  [ "$emails" -ge 1 ] && break
  sleep 1
done
[ "$emails" -ge 1 ] || { echo "✗ no confirmation email"; exit 1; }
echo "✓ confirmation email delivered"
