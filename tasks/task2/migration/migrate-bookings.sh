#!/usr/bin/env bash
# Ops-джоба переноса бронирований (ADR-001).
#   forward [--reset-target] — переключение: таблица booking монолита → БД booking-service;
#                              --reset-target сначала очищает приёмник (тестовый стенд, регресс).
#   reverse                  — откат: бронирования, созданные после переключения, → обратно в монолит.
# Запускать, когда монолит и booking-service остановлены; MIGRATION_OFFLINE=yes — подтверждение оператора.
# Подключения — libpq URI: LEGACY_DB_URL (монолит), BOOKING_DB_URL (booking-service).
set -euo pipefail

mode="${1:-}"
reset="${2:-}"
if [[ $# -gt 2 || ! "$mode" =~ ^(forward|reverse)$ || ( -n "$reset" && "$mode $reset" != "forward --reset-target" ) ]]; then
  echo "usage: $0 forward [--reset-target] | reverse" >&2; exit 2
fi
if [[ "${MIGRATION_OFFLINE:-no}" != yes ]]; then
  echo "Остановите monolith и booking-service и задайте MIGRATION_OFFLINE=yes" >&2; exit 2
fi
: "${LEGACY_DB_URL:?LEGACY_DB_URL is required}" "${BOOKING_DB_URL:?BOOKING_DB_URL is required}"

sql() { psql "$1" -X -q -A -t -v ON_ERROR_STOP=1 "${@:2}"; }
# Источник переноса открываем только на чтение.
read_sql() { PGOPTIONS="-c default_transaction_read_only=on" sql "$@"; }

# Количество и контрольная сумма переносимых полей. Даты не входят: пустые заменяются при переносе.
# Суммы приводятся к numeric(10,2): double precision монолита и numeric сервиса дают одинаковый текст.
checksum() { # $1 — БД, $2 — столбец скидки, $3 — условие
  read_sql "$1" -c "select count(*) || ' ' || coalesce(md5(string_agg(concat_ws('|', id, user_id, hotel_id,
    coalesce(promo_code, ''), coalesce($2, 0)::numeric(10, 2), price::numeric(10, 2)), ',' order by id)), '-')
    from booking $3"
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
started=$(date +%s)
run_id="migration-$(date -u +%Y%m%dT%H%M%SZ)"  # correlationId событий этого запуска

report() {
  echo "[migration] источник: $1"
  echo "[migration] приёмник: $2"
  echo "[migration] длительность: $(( $(date +%s) - started )) с"
  if [[ "$1" != "$2" ]]; then echo "[migration] ОШИБКА: расхождение после переноса" >&2; exit 1; fi
  echo "[migration] OK: количество и контрольная сумма совпадают"
}

forward() {
  read_sql "$LEGACY_DB_URL" -c "\copy (select id, user_id, hotel_id, promo_code, discount_percent, price, created_at
    from booking order by id) to '$work/booking.csv' csv"
  local max; max=$(read_sql "$LEGACY_DB_URL" -c "select coalesce(max(id), 0) from booking")
  echo "[migration] в монолите бронирований: $(wc -l < "$work/booking.csv" | tr -d ' '), максимальный id: $max"
  local truncate=""
  # Счётчик id не сбрасываем: id бронирований сервиса не повторяются и после очистки.
  [[ -z "$reset" ]] || truncate="truncate booking, outbox;"

  sql "$BOOKING_DB_URL" <<SQL
begin;
$truncate
create temp table staging (id bigint, user_id text, hotel_id text, promo_code text,
                           discount numeric, price numeric, created_at timestamptz);
\copy staging from '$work/booking.csv' csv
with inserted as (
  insert into booking (id, user_id, hotel_id, promo_code, discount_amount, price, created_at, created_at_estimated, source)
  select id, user_id, hotel_id, promo_code, coalesce(discount, 0), price,
         coalesce(created_at, now()), created_at is null, 'migration'  -- монолит не всегда заполнял дату
  from staging
  on conflict (id) do nothing  -- повторный запуск не создаёт дублей
  returning *
), queued as (
  -- История получает и перенесённые бронирования — через тот же outbox, eventId как в booking-service
  insert into outbox (event_id, aggregate_id, event_type, correlation_id, payload)
  select md5('hotelio:BookingCreated:v1:' || id)::uuid, id, 'BookingCreated', '$run_id', jsonb_build_object(
           'eventId', md5('hotelio:BookingCreated:v1:' || id)::uuid, 'eventType', 'BookingCreated', 'schemaVersion', 1,
           'occurredAt', now(), 'source', 'migration', 'correlationId', '$run_id',
           'booking', jsonb_build_object('id', id, 'userId', user_id, 'hotelId', hotel_id, 'promoCode', promo_code,
             'discountAmount', discount_amount, 'price', price,
             'createdAt', created_at, 'createdAtEstimated', created_at_estimated))
  from inserted
  on conflict (event_id) do nothing
)
select format('[migration] перенесено новых: %s, из них с подставленной датой: %s',
              count(*), count(*) filter (where created_at_estimated)) from inserted;
select format('[migration] счётчик id booking-service: %s', setval(pg_get_serial_sequence('booking', 'id'),
  greatest(max(id), pg_sequence_last_value(pg_get_serial_sequence('booking', 'id')::regclass))))
from booking having count(*) > 0;
commit;
SQL
  report "$(checksum "$LEGACY_DB_URL" discount_percent "")" "$(checksum "$BOOKING_DB_URL" discount_amount "where id <= $max")"
}

reverse() {
  local max; max=$(read_sql "$LEGACY_DB_URL" -c "select coalesce(max(id), 0) from booking")
  read_sql "$BOOKING_DB_URL" -c "\copy (select id, user_id, hotel_id, promo_code, discount_amount, price, created_at
    from booking where id > $max order by id) to '$work/back.csv' csv"
  echo "[migration] откат: бронирований после переключения (id > $max): $(wc -l < "$work/back.csv" | tr -d ' ')"

  sql "$LEGACY_DB_URL" <<SQL
begin;
create temp table staging (id bigint, user_id text, hotel_id text, promo_code text,
                           discount numeric, price numeric, created_at timestamptz);
\copy staging from '$work/back.csv' csv
insert into booking (id, user_id, hotel_id, promo_code, discount_percent, price, created_at)
select * from staging on conflict (id) do nothing;
select format('[migration] счётчик id монолита: %s', setval(pg_get_serial_sequence('booking', 'id'),
  greatest(max(id), pg_sequence_last_value(pg_get_serial_sequence('booking', 'id')::regclass))))
from booking having count(*) > 0;
commit;
SQL
  report "$(checksum "$BOOKING_DB_URL" discount_amount "")" "$(checksum "$LEGACY_DB_URL" discount_percent "")"
}

"$mode"
