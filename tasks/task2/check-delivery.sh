#!/usr/bin/env bash
# Проверка доставки событий на поднятом стенде задания 2 (после регресса, нужен образ hotelio-tester).
# 1. Kafka остановлена: бронирование создаётся, событие ждёт в outbox и переживает неудачные попытки.
# 2. Kafka запущена: событие доходит до истории один раз; повторная публикация историю не удваивает.
# 3. Сообщение неподдерживаемой версии уходит в DLQ-топик booking-events.dlq, чтение топика продолжается.
set -euo pipefail
booking_sql() { docker exec booking-db psql -U booking -d booking -XAtc "$1"; }
history_sql() { docker exec history-db psql -U history -d history -XAtc "$1"; }
show() { docker exec "$1-db" psql -U "$1" -d "$1" -Xc "$2"; }
dlq_end() { docker exec kafka kafka-run-class kafka.tools.GetOffsetShell --bootstrap-server kafka:9092 --topic booking-events.dlq 2>/dev/null \
  | awk -F: '{ s += $3 } END { print s + 0 }'; }
read_past() { # группа booking-history прочитала партицию $1 топика booking-events дальше смещения $2
  docker exec kafka kafka-consumer-groups --bootstrap-server kafka:9092 --group booking-history --describe 2>/dev/null \
    | awk -v p="$1" -v o="$2" '$2 == "booking-events" && $3 == p && $4 ~ /^[0-9]+$/ && $4 > o { ok = 1 } END { exit !ok }'; }
wait_until() { # $1 — что ждём, $2 — условие
  for _ in $(seq 1 120); do eval "$2" && return 0; sleep 1; done
  echo "[FAIL] не дождались: $1" >&2; exit 1
}
request="delivery-check-$(date +%s)"
trap 'docker start kafka >/dev/null' EXIT

echo "▶️ 1. Kafka остановлена, создаём бронирование (x-request-id: $request)"
docker stop kafka >/dev/null
id=$(docker run --rm --network hotelio-net --entrypoint grpcurl hotelio-tester -plaintext -H "x-request-id: $request" \
  -d '{"user_id":"test-user-2","hotel_id":"test-hotel-1"}' booking-service:9090 booking.BookingService/CreateBooking | jq -r .id)
wait_until "шесть неудачных попыток публикации" '(( $(booking_sql "select failure_count from outbox where aggregate_id = $id") >= 6 ))'
show booking "select event_id, failure_count, last_error, dead_lettered_at from outbox where aggregate_id = $id"
[[ $(history_sql "select count(*) from booking_history where booking_id = $id") == 0 ]]
echo "  [PASS] бронирование $id сохранено, событие ждёт в outbox, в карантин не попало"

echo "▶️ 2. Kafka запущена"
docker start kafka >/dev/null
wait_until "событие в истории" '[[ $(history_sql "select count(*) from booking_history where booking_id = $id") == 1 ]]'
show history "select event_id, booking_id, user_id, price, source, correlation_id from booking_history where booking_id = $id"
echo "  [PASS] событие опубликовано без ручного вмешательства, в истории одна запись"
booking_sql "update outbox set published_at = null, next_attempt_at = now() where aggregate_id = $id" >/dev/null
wait_until "повтор прочитан" 'docker logs --since 2m booking-history-service 2>&1 | grep -q "Duplicate skipped.*requestId=$request"'
docker logs --since 2m booking-history-service 2>&1 | grep "requestId=$request"
[[ $(history_sql "select count(*) from booking_history where booking_id = $id") == 1 ]]
echo "  [PASS] повторная публикация прочитана, история не удвоилась"

echo "▶️ 3. Сообщение с schemaVersion 2"
docker exec kafka kafka-topics --bootstrap-server kafka:9092 --describe --topic booking-events.dlq >/dev/null 2>&1 \
  || { echo "[FAIL] нет топика booking-events.dlq: его создаёт kafka-init, поднимите стенд заново" >&2; exit 1; }
dead=$(dlq_end)
payload=$(booking_sql "select jsonb_set(payload, '{schemaVersion}', '2')::text from outbox where aggregate_id = $id")
printf '%s|%s\n' "$id" "$payload" | docker exec -i kafka kafka-console-producer --bootstrap-server kafka:9092 \
  --topic booking-events --property parse.key=true --property key.separator='|'
wait_until "сообщение в DLQ" '(( $(dlq_end) > dead ))'
dlq=$(docker exec kafka kafka-console-consumer --bootstrap-server kafka:9092 --topic booking-events.dlq --partition 0 \
  --offset "$dead" --max-messages 1 --timeout-ms 30000 --property print.headers=true --property print.key=true 2>/dev/null)
echo "$dlq"
[[ $dlq == *"dlq-reason:unsupported schemaVersion 2"* && $dlq =~ dlq-partition:([0-9]+),dlq-offset:([0-9]+) ]]
partition=${BASH_REMATCH[1]} offset=${BASH_REMATCH[2]}
[[ $(history_sql "select count(*) from booking_history where booking_id = $id") == 1 ]]
wait_until "смещение группы дальше этого сообщения" 'read_past "$partition" "$offset"'
docker exec kafka kafka-consumer-groups --bootstrap-server kafka:9092 --group booking-history --describe 2>/dev/null
echo "  [PASS] сообщение в DLQ с причиной в заголовке, история не изменилась, смещение группы сдвинулось дальше"
