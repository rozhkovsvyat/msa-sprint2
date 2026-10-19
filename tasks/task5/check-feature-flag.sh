#!/bin/bash
# Фича-флаг: с X-Feature-Enabled: true все запросы идут на v2 (EnvoyFilter → x-booking-route → VirtualService).
# Без флага и с поддельным внутренним заголовком — обычная канарейка 90/10.
set -e
source "$(dirname "$0")/lib.sh"
N=30
read -r lo hi <<<"$(binomial_bounds "$N" 0.1)"

echo "▶️ Проверка Feature Flag (X-Feature-Enabled: true) — $N запросов"
sample "$N" -H "X-Feature-Enabled: true" "$GW/ping"
expect "ответов v2 с флагом" "$(count v2)" "$N" "$N"

echo "▶️ /feature с флагом (есть только в v2, ENABLE_FEATURE_X=true):"
feature=$(curl -s -H "X-Feature-Enabled: true" "$GW/feature"); echo "  $feature"
[ "$feature" = "Feature X is enabled!" ] && echo "  [PASS] /feature отвечает" || { echo "  [FAIL] /feature"; FAILED=1; }

echo "▶️ X-Feature-Enabled: false — обычная канарейка, $N запросов"
sample "$N" -H "X-Feature-Enabled: false" "$GW/ping"
expect "ответов v2 без флага" "$(count v2)" "$lo" "$hi"
expect "ошибок" "$(errors)" 0 0

echo "▶️ Подделка внутреннего заголовка x-booking-route: v2 без флага — EnvoyFilter его удаляет, $N запросов"
sample "$N" -H "x-booking-route: v2" "$GW/ping"
expect "ответов v2 при подделке" "$(count v2)" "$lo" "$hi"
expect "ошибок" "$(errors)" 0 0
finish
