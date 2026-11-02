#!/bin/bash
set -euo pipefail

echo "🏁 Регрессионный тест до миграции Hotelio"

# Проверка соединения
echo "🧪 Проверка подключения к БД..."
timeout 2 bash -c "</dev/tcp/${DB_HOST}/${DB_PORT}" \
  || { echo "❌ Не удалось подключиться к ${DB_HOST}:${DB_PORT}"; exit 1; }

# Загрузка фикстур
echo "🧪 Загрузка фикстур..."
PGPASSWORD="${DB_PASSWORD}" psql -h "${DB_HOST}" -p "${DB_PORT}" -U "${DB_USER}" "${DB_NAME}" < init-fixtures.sql

# После выноса бронирований (задание 2) монолит читает их из booking-service через gRPC-прокси.
# Фикстуры бронирований переносим той же ops-джобой, что и боевые данные, — тест заодно проверяет миграцию.
# Включается, если задан BOOKING_DB_URL и смонтирован /migration (сервис regress в tasks/task2/docker-compose.yml).
if [[ -n "${BOOKING_DB_URL:-}" ]]; then
  echo "🧪 Перенос бронирований в booking-service (ops-джоба, режим тестового стенда)..."
  LEGACY_DB_URL="postgresql://${DB_USER}:${DB_PASSWORD}@${DB_HOST}:${DB_PORT}/${DB_NAME}" \
    MIGRATION_OFFLINE=yes /migration/migrate-bookings.sh forward --reset-target
fi

echo "🧪 Выполнение HTTP-тестов..."

pass() { echo "✅ $1"; }
fail() { echo "❌ $1"; exit 1; }

BASE="${API_URL:-http://localhost:8080}"

echo ""
echo "Тесты пользователей..."
# 1. Получение пользователя по ID
curl -sSf "${BASE}/api/users/test-user-1" | grep -q 'Alice' && pass "Получение test-user-1 по ID работает" || fail "Пользователь test-user-1 не найден"

# 2. Статус пользователя
curl -sSf "${BASE}/api/users/test-user-1/status" | grep -q 'ACTIVE' && pass "Статус test-user-1: ACTIVE" || fail "Неверный статус пользователя"

# 3. Блэклист
curl -sSf "${BASE}/api/users/test-user-1/blacklisted" | grep -q 'true' && pass "test-user-1 в блэклисте" || fail "Блэклист не работает"

# 4. Активность
curl -sSf "${BASE}/api/users/test-user-1/active" | grep -q 'true' && pass "test-user-1 активен" || fail "Активность не работает"

# 5. Авторизация
curl -sSf "${BASE}/api/users/test-user-1/authorized" | grep -q 'false' && pass "test-user-1 не авторизован (в блэклисте)" || fail "Авторизация работает неправильно"

# 6. VIP-статус
curl -sSf "${BASE}/api/users/test-user-3/vip" | grep -q 'true' && pass "test-user-3 — VIP-пользователь" || fail "VIP-статус не работает"

# 7. Авторизация: положительный кейс
curl -sSf "${BASE}/api/users/test-user-2/authorized" | grep -q 'true' && pass "test-user-2 авторизован" || fail "Авторизация (true) не работает"

echo ""
echo "Тесты отелей..."

# 1. Получение отеля по ID
curl -sSf "${BASE}/api/hotels/test-hotel-1" | grep -q 'Seoul' && pass "test-hotel-1 получен по ID" || fail "test-hotel-1 не найден"

# 2. Проверка operational
curl -sSf "${BASE}/api/hotels/test-hotel-1/operational" | grep -q 'true' && pass "test-hotel-1 работает" || fail "test-hotel-1 не работает"
curl -sSf "${BASE}/api/hotels/test-hotel-3/operational" | grep -q 'false' && pass "test-hotel-3 не работает" || fail "Статус работы test-hotel-3 некорректен"

# 3. Проверка fullyBooked
curl -sSf "${BASE}/api/hotels/test-hotel-2/fully-booked" | grep -q 'true' && pass "test-hotel-2 полностью забронирован" || fail "Статус fullyBooked test-hotel-2 неверен"

# 4. Поиск по городу
curl -sSf "${BASE}/api/hotels/by-city?city=Seoul" | grep -q 'Seoul' && pass "Поиск отелей в Сеуле работает" || fail "Поиск отелей в Сеуле не работает"

# 5. Топ-отели (по рейтингу, limit)
curl -sSf "${BASE}/api/hotels/top-rated?city=Seoul&limit=1" | grep -q 'Seoul' && pass "Топ-отели в Сеуле загружены" || fail "Топ-отели не найдены"

echo ""
echo "Тесты ревью..."

# 11. Отзывы по hotelId
curl -sSf "${BASE}/api/reviews/hotel/test-hotel-1" | grep -q 'Amazing experience' \
  && pass "Отзывы test-hotel-1 найдены" || fail "Отзывы test-hotel-1 не найдены"

# 12. Надёжный отель (>=10 отзывов и avgRating >= 4.0)
curl -sSf "${BASE}/api/reviews/hotel/test-hotel-1/trusted" | grep -q 'true' \
  && pass "test-hotel-1 признан надёжным" || fail "Надёжность test-hotel-1 не определена"

# 13. Сомнительный отель (мало отзывов/низкий рейтинг)
curl -sSf "${BASE}/api/reviews/hotel/test-hotel-3/trusted" | grep -q 'false' \
  && pass "test-hotel-3 НЕ признан надёжным (ожидаемо)" || fail "Надёжность test-hotel-3 некорректно определена"

echo ""
echo "Тесты промокодов..."

# 1. Получение промо по коду
curl -sSf "${BASE}/api/promos/TESTCODE1" | grep -q 'TESTCODE1' && pass "Промокод TESTCODE1 найден" || fail "Промокод TESTCODE1 не найден"

# 2. Проверка VIP промо — для VIP
curl -sSf "${BASE}/api/promos/TESTCODE-VIP/valid?isVipUser=true" | grep -q 'true' && pass "VIP-промо доступен VIP" || fail "VIP-промо НЕ доступен VIP"

# 3. Проверка VIP промо — для обычного
curl -sSf "${BASE}/api/promos/TESTCODE-VIP/valid?isVipUser=false" | grep -q 'false' && pass "VIP-промо недоступен обычному" || fail "VIP-промо доступен обычному"

# 4. Проверка обычного промо
curl -sSf "${BASE}/api/promos/TESTCODE1/valid" | grep -q 'true' && pass "Обычный промо доступен" || fail "Обычный промо недоступен"

# 5. Проверка истекшего промо
curl -sSf "${BASE}/api/promos/TESTCODE-OLD/valid" | grep -q 'false' && pass "Истекший промо недоступен" || fail "Истекший промо доступен"

# 6. Валидация промо для user-2 (обычного)
curl -sSf -X POST "${BASE}/api/promos/validate?code=TESTCODE1&userId=test-user-2" | grep -q 'TESTCODE1' && pass "POST /validate промо прошёл" || fail "POST /validate не прошёл"

echo ""
echo "Тесты бронирования..."

# 1. Получение всех бронирований
if [[ -n "${BOOKING_GRPC:-}" ]]; then
  [[ -n "${BOOKING_ADMIN_TOKEN:-}" ]] || fail "Не задан BOOKING_ADMIN_TOKEN: служебный список не проверить"
  # После включения прокси список без userId через монолит недоступен: готовый p-o-y-1.0.0.jar
  # вызывает BookingListRequest.setUserId(null) и падает с NullPointerException (HTTP 500).
  # Полный список отдаёт служебный метод booking-service — с токеном и постранично.
  admin_list() {
    grpcurl -plaintext -H "authorization: Bearer ${BOOKING_ADMIN_TOKEN}" \
      -d "$1" "${BOOKING_GRPC}" booking.admin.BookingAdminService/ListAllBookings
  }
  admin_list '{"page_size":500}' | grep -q 'test-user-2' \
    && pass "Все бронирования получены (служебный gRPC-метод ListAllBookings)" || fail "Бронирования не получены"

  # Обход по одной строке за токеном страницы должен дать тот же набор id, что и одна большая страница.
  # Ответ grpcurl — JSON по полю в строке с именами из proto, разбираем sed: jq в образе тестировщика нет.
  ids_of() { sed -n 's/.*"id":[[:space:]]*"\([0-9]*\)".*/\1/p' <<<"$1"; }
  token_of() { sed -nE 's/.*"(next_page_token|nextPageToken)":[[:space:]]*"([^"]*)".*/\2/p' <<<"$1"; }
  paged_ids="" token=""
  for _ in $(seq 1 100); do
    if [[ -n "$token" ]]; then request="{\"page_size\":1,\"page_token\":\"$token\"}"; else request='{"page_size":1}'; fi
    page=$(admin_list "$request") || fail "Служебный список не отдал страницу"
    paged_ids+=" $(ids_of "$page" | tr '\n' ' ')"
    token=$(token_of "$page")
    [[ -z "$token" ]] && break
  done
  [[ -n "$token" ]] && fail "Служебный список не закончился за 100 страниц"
  all_ids=$(admin_list '{"page_size":500}' | sed -n 's/.*"id":[[:space:]]*"\([0-9]*\)".*/\1/p' | sort -n | tr '\n' ' ')
  walked=$(echo "$paged_ids" | tr ' ' '\n' | grep . | sort -n | tr '\n' ' ')
  [[ "$(echo "$walked" | wc -w | tr -d ' ')" -gt 1 && "$walked" == "$all_ids" ]] \
    && pass "Пагинация служебного списка: страницы по одной строке дают тот же набор id" \
    || fail "Пагинация служебного списка вернула не то: [$walked] против [$all_ids]"

  # Публичный метод не подменяется служебным: без токена список закрыт, а пустой user_id не отдаёт чужое.
  denied=$(grpcurl -plaintext -d '{"page_size":10}' "${BOOKING_GRPC}" \
    booking.admin.BookingAdminService/ListAllBookings 2>&1 || true)
  [[ "$denied" == *Unauthenticated* ]] \
    && pass "Служебный список без токена закрыт" || fail "Служебный список доступен без токена: $denied"
  empty=$(grpcurl -plaintext -d '{}' "${BOOKING_GRPC}" booking.BookingService/ListBookings 2>&1 || true)
  [[ "$empty" != *test-user-2* ]] \
    && pass "Пустой user_id не отдаёт чужие бронирования" || fail "Пустой user_id отдал все бронирования"
else
  curl -sSf "${BASE}/api/bookings" | grep -q 'test-user-2' && pass "Все бронирования получены" || fail "Бронирования не получены"
fi

# 2. Получение бронирований пользователя
curl -sSf "${BASE}/api/bookings?userId=test-user-2" | grep -q 'test-user-2' && pass "Бронирования test-user-2 найдены" || fail "Нет бронирований test-user-2"

# 3. Успешное бронирование отеля без промо
resp3=$(curl -sSf -X POST "${BASE}/api/bookings?userId=test-user-3&hotelId=test-hotel-1") || fail "Бронирование (без промо) не прошло"
echo "$resp3" | grep -q 'test-hotel-1' && pass "Бронирование прошло (без промо)" || fail "Бронирование (без промо) не прошло"
created_ids=$(echo "$resp3" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')

# 4. Успешное бронирование с промо
resp4=$(curl -sSf -X POST "${BASE}/api/bookings?userId=test-user-2&hotelId=test-hotel-1&promoCode=TESTCODE1") || fail "Бронирование с промо не прошло"
echo "$resp4" | grep -q 'TESTCODE1' && pass "Бронирование с промо прошло" || fail "Бронирование с промо не прошло"
created_ids="$created_ids $(echo "$resp4" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')"

# 5. Ошибка — неактивный пользователь
code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE}/api/bookings?userId=test-user-0&hotelId=test-hotel-1")
if [[ "$code" == "500" ]]; then
  pass "Отклонено: неактивный пользователь"
else
  fail "Ошибка: сервер принял бронирование от неактивного пользователя (код $code)"
fi

# 6. Ошибка — test-hotel-3 отключён (к тому же не доверенный); каждую причину отдельно проверяют 8–9
curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE}/api/bookings?userId=test-user-2&hotelId=test-hotel-3" | grep -q '500' \
  && pass "Отклонено: отключённый отель из исходной фикстуры" \
  || fail "Ошибка: сервер принял бронирование в отключённый отель"

# 7. Ошибка — у test-hotel-2 мало отзывов (к тому же мест нет); каждую причину отдельно проверяют 8–9
curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE}/api/bookings?userId=test-user-2&hotelId=test-hotel-2" | grep -q '500' \
  && pass "Отклонено: отель с недостатком отзывов и без мест" \
  || fail "Ошибка: сервер принял бронирование в исходный проблемный отель"

# 8–9. Каждая причина отказа отдельно: у отелей test-hotel-4 и test-hotel-5 нарушено ровно одно правило.
# Монолит отдаёт любой отказ как HTTP 500 без текста, поэтому причину читаем из ответа booking-service по gRPC.
rejected() { # $1 — отель, $2 — ожидаемая причина, $3 — описание
  local code reason=""
  code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${BASE}/api/bookings?userId=test-user-2&hotelId=$1")
  [[ "$code" == "500" ]] || fail "Ошибка: сервер принял бронирование ($3, код $code)"
  if [[ -n "${BOOKING_GRPC:-}" ]]; then
    reason=$(grpcurl -plaintext -d "{\"user_id\":\"test-user-2\",\"hotel_id\":\"$1\"}" "${BOOKING_GRPC}" \
      booking.BookingService/CreateBooking 2>&1 | sed -n 's/^ *Message: //p' || true)
    [[ "$reason" == "$2" ]] || fail "Отказ по другой причине ($3): «${reason}», ожидалось «$2»"
    list=$(grpcurl -plaintext -d '{"user_id":"test-user-2"}' "${BOOKING_GRPC}" booking.BookingService/ListBookings)
    [[ "$list" != *"\"$1\""* ]] || fail "После отказа появилась запись бронирования в $1"
  fi
  pass "Отклонено: $3${reason:+ — ${reason}, записи нет}"
}
rejected test-hotel-4 "Hotel is not trusted based on reviews" "недоверенный отель (работает, места есть)"
rejected test-hotel-5 "Hotel is fully booked" "полностью занятый отель (работает, доверенный)"

# История бронирований (задание 2): события BookingCreated из booking-service доходят
# до booking-history-service через Kafka асинхронно — ждём до 30 секунд.
if [[ -n "${HISTORY_DB_URL:-}" ]]; then
  echo ""
  echo "Тесты истории бронирований..."
  [[ $(wc -w <<< "$created_ids") -eq 2 ]] || fail "Не удалось прочитать id созданных бронирований: '$created_ids'"
  for id in $created_ids; do
    found=0
    for _ in $(seq 1 30); do
      n=$(psql "${HISTORY_DB_URL}" -At -c "select count(*) from booking_history where booking_id = ${id} and source = 'booking-service'")
      [[ "$n" == "1" ]] && { found=1; break; }
      sleep 1
    done
    [[ "$found" == "1" ]] && pass "Бронирование ${id} попало в историю ровно один раз" || fail "Бронирование ${id} не появилось в истории"
  done
fi

echo "✅ Все HTTP-тесты пройдены!"
