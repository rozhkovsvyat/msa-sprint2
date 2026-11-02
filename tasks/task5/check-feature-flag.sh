#!/bin/bash

set -e
source "$(dirname "$0")/lib.sh"

echo "▶️ Проверка Feature Flag (X-Feature-Enabled: true)..."

# С заголовком все запросы должны попасть на v2
stats=$(sample 20 -H "X-Feature-Enabled: true" "$GW/ping")
echo "$stats"
[ "$(count "$stats" "200 v2")" -eq 20 ] || { echo "❌ не все запросы с флагом попали на v2"; exit 1; }

# /feature открыт только в v2 (ENABLE_FEATURE_X=true)
feature=$(curl -s -H "X-Feature-Enabled: true" "$GW/feature")
echo "/feature: $feature"
[ "$feature" = "Feature X is enabled!" ] || { echo "❌ /feature не ответил"; exit 1; }

echo "▶️ Без заголовка — обычная канарейка"
stats=$(sample 20 "$GW/ping")
echo "$stats"
[ "$(count "$stats" "200 v2")" -lt 20 ] || { echo "❌ без флага все запросы ушли на v2"; exit 1; }

echo "▶️ Клиент сам прислал внутренний заголовок x-booking-route: v2 — EnvoyFilter его удаляет"
stats=$(sample 20 -H "x-booking-route: v2" "$GW/ping")
echo "$stats"
[ "$(count "$stats" "200 v2")" -lt 20 ] || { echo "❌ поддельный заголовок сработал"; exit 1; }
echo "✅ OK"
