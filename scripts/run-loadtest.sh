#!/usr/bin/env bash
# Runs the k6 same-seat race against the docker-compose stack, then audits Booking's database.
#   docker compose -f deploy/docker-compose.yml up -d --build
#   scripts/run-loadtest.sh
set -euo pipefail
cd "$(dirname "$0")/.."

docker run --rm --network host --user "$(id -u):$(id -g)" \
  -v "$PWD/loadtests/k6:/scripts:ro" -v "$PWD/loadtests/results:/results" \
  -e CUSTOMERS="${CUSTOMERS:-500}" \
  grafana/k6:1.3.0 run --quiet /scripts/same-seat-race.js

echo "--- Database audit: seats held or sold by more than one active reservation (expect none) ---"
docker exec tickethub-postgres-1 psql -U postgres -d bookingdb -At -c "
  SELECT seat->>'SeatId' AS seat, count(*) AS reservations
  FROM \"Reservations\" r, jsonb_array_elements(r.\"Seats\") seat
  WHERE r.\"Status\" IN ('Held', 'AwaitingPayment', 'Confirmed')
  GROUP BY 1 HAVING count(*) > 1;" | tee loadtests/results/duplicate-seats.txt
[ -s loadtests/results/duplicate-seats.txt ] && { echo "OVERBOOKING DETECTED"; exit 1; }
echo "none: zero overbooking"
