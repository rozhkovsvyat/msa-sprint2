#!/usr/bin/env bash
# Учения по переключению бронирований и откату (ADR-001) на чистом стенде задания 2.
# Запуск из tasks/task2: ./migration/cutover-drill.sh. Нужна сеть hotelio-net.
# Удаляет тома стенда (docker compose down -v): только для тестового стенда.
set -euo pipefail
cd "$(dirname "$0")/.."
export MIGRATION_OFFLINE=yes
API=http://localhost:8084

log()  { printf '\n[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }
psql_mono()    { docker exec hotelio-db psql -U hotelio -d hotelio -X "$@"; }
psql_booking() { docker exec booking-db psql -U booking -d booking -X "$@"; }
monolith() { # $1 — значение BOOKING_SERVICE_EXTERNAL_HOST: booking-service включает прокси, false выключает
  BOOKING_SERVICE_EXTERNAL_HOST=$1 docker compose up -d --force-recreate --wait monolith >/dev/null 2>&1
  docker logs hotelio-monolith 2>&1 | grep -A2 "BookingService beans"
}

log "0. Чистый стенд"
docker compose down -v --remove-orphans >/dev/null 2>&1 || true
docker compose build -q
docker compose up -d booking-db history-db zookeeper kafka kafka-init >/dev/null 2>&1

log "1. Монолит без прокси: фикстуры курса и два бронирования через API (регресс курса без переноса)"
monolith false
docker compose run --rm --no-deps -e BOOKING_DB_URL= -e HISTORY_DB_URL= -e BOOKING_GRPC= regress | tail -1
psql_mono -c "select id, user_id, hotel_id, promo_code, discount_percent, price, created_at from booking order by id"

log "2. Окно переключения: остановка записи → перенос → включение прокси"
window_start=$(date +%s)
docker compose stop monolith booking-service >/dev/null 2>&1
docker compose run --rm booking-migration forward
docker compose up -d booking-service booking-history-service >/dev/null 2>&1
monolith booking-service
log "Окно переключения: $(( $(date +%s) - window_start )) с, включая перезапуск монолита"

log "3. После переключения список через монолит читает booking-service; перенесённые даты помечены"
curl -s "$API/api/bookings?userId=test-user-3"; echo
psql_booking -c "select id, user_id, hotel_id, promo_code, discount_amount, price, created_at, created_at_estimated, source from booking order by id"

log "4. Новое бронирование после переключения продолжает счётчик id"
curl -s -X POST "$API/api/bookings?userId=test-user-2&hotelId=test-hotel-1&promoCode=TESTCODE1"; echo
sleep 3
docker exec history-db psql -U history -d history -X -c "select booking_id, user_id, price, booked_at_estimated, source from booking_history order by booking_id"

log "5. Откат: остановка → обратный перенос → монолит без прокси"
rollback_start=$(date +%s)
docker compose stop monolith booking-service >/dev/null 2>&1
docker compose run --rm booking-migration reverse
monolith false
log "Окно отката: $(( $(date +%s) - rollback_start )) с. Бронирование, созданное после переключения, вернулось в монолит:"
curl -s "$API/api/bookings?userId=test-user-2"; echo
psql_mono -c "select id, user_id, hotel_id, promo_code, discount_percent, price, created_at from booking order by id"

log "6. Повторное переключение: перенос идемпотентен"
docker compose stop monolith booking-service >/dev/null 2>&1
docker compose run --rm booking-migration forward
docker compose up -d booking-service >/dev/null 2>&1
monolith booking-service
curl -s "$API/api/bookings?userId=test-user-2"; echo
log "Учения завершены"
