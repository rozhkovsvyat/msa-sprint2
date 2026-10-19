#!/usr/bin/env bash
# Запросы к личному кабинету: ./calls.sh success | deny | course | batch
# Каждый запрос получает свой x-request-id: по нему запрос находится в журналах шлюза и подграфов.
set -euo pipefail
GW="${GW:-http://localhost:4000/}"
USER_ID="${USER_ID:-test-user-2}"
OTHER_USER_ID="${OTHER_USER_ID:-test-user-3}"
response=''

call() { # $1 — userid (пусто — без заголовка), $2 — запрос, $3 — переменные
  local request_id="calls-$(date +%s)-$RANDOM" variables="${3:-}"
  local headers=(-H 'content-type: application/json' -H "x-request-id: $request_id")
  [[ -z "$1" ]] || headers+=(-H "userid: $1")
  [[ -n "$variables" ]] || variables='{}'
  printf '\nPOST %s  userid=%s  x-request-id=%s\n' "$GW" "${1:-<нет>}" "$request_id"
  response=$(curl -sS "$GW" "${headers[@]}" --data "$(jq -cn --arg q "$2" --argjson v "$variables" '{query: $q, variables: $v}')")
  jq . <<<"$response"
}
expect() { jq -e "$1" <<<"$response" >/dev/null || { echo "[FAIL] ожидалось: $1" >&2; exit 1; }; }

MY='query MyBookings($user: ID!) {
  bookingsByUser(userId: $user) {
    id promoCode discountPercent createdAt
    discountInfo { code isValid discountPercent vipOnly expired validUntil }
    hotel { id name city rating description }
  }
}'
vars=$(jq -cn --arg user "$USER_ID" '{user: $user}')

case "${1:-success}" in
  success) call "$USER_ID" "$MY" "$vars"; expect 'has("errors") | not' ;;
  deny)
    call "$OTHER_USER_ID" "$MY" "$vars"; expect '.errors[0].extensions.code == "FORBIDDEN"'
    call '' "$MY" "$vars"; expect '.errors[0].extensions.code == "UNAUTHENTICATED"'
    ;;
  course) call user1 '{ bookingsByUser(userId: "user1") { id hotel { name city } discountPercent } }'; expect 'has("errors") | not' ;;
  batch)
    call "$USER_ID" '{ hotelsByIds(ids: ["test-hotel-1", "test-hotel-2", "test-hotel-3", "test-hotel-1"]) { id city rating name } }'
    expect 'has("errors") | not'
    ;;
  *) echo "Неизвестная проверка: $1" >&2; exit 2 ;;
esac
