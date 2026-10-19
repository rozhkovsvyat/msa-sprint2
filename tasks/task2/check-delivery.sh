#!/usr/bin/env bash
# Проверка доставки событий на поднятом стенде задания 2 (после регресса, нужен образ hotelio-tester).
# 1. Kafka остановлена: бронирование создаётся, событие ждёт в outbox и переживает неудачные попытки.
# 2. Kafka запущена: событие доходит до истории один раз; повторная публикация историю не удваивает.
# 3. Сообщение неподдерживаемой версии уходит в dead letter, чтение топика продолжается.
set -euo pipefail
booking_sql() { docker exec booking-db psql -U booking -d booking -XAtc "$1"; }
history_sql() { docker exec history-db psql -U history -d history -XAtc "$1"; }
show() { docker exec "$1-db" psql -U "$1" -d "$1" -Xc "$2"; }
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
dead=$(history_sql "select count(*) from dead_letter")
payload=$(booking_sql "select jsonb_set(payload, '{schemaVersion}', '2')::text from outbox where aggregate_id = $id")
printf '%s|%s\n' "$id" "$payload" | docker exec -i kafka kafka-console-producer --bootstrap-server kafka:9092 \
  --topic booking-events --property parse.key=true --property key.separator='|'
wait_until "запись в dead letter" '(( $(history_sql "select count(*) from dead_letter") > dead ))'
show history "select topic, kafka_partition, kafka_offset, error from dead_letter order by id desc limit 1"
[[ $(history_sql "select count(*) from booking_history where booking_id = $id") == 1 ]]
sleep 3
docker exec kafka kafka-consumer-groups --bootstrap-server kafka:9092 --group booking-history --describe 2>/dev/null
echo "  [PASS] сообщение в dead letter, история не изменилась, смещение группы сдвинулось дальше"
