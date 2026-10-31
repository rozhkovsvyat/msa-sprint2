#!/bin/bash

set -e
source "$(dirname "$0")/lib.sh"

echo "▶️ Checking canary release (90% v1, 10% v2)..."

# Посылаем 200 запросов и смотрим, какая версия ответила
stats=$(sample 200 "$GW/ping")
echo "$stats"

v1=$(count "$stats" "200 v1")
v2=$(count "$stats" "200 v2")
echo "v1: $v1, v2: $v2 из 200"

# Веса случайные: 10% от 200 — около 20 ответов v2, допускаем от 10 до 40
[ $((v1 + v2)) -eq 200 ] && [ "$v2" -ge 10 ] && [ "$v2" -le 40 ] || { echo "❌ распределение не похоже на 90/10"; exit 1; }
echo "✅ OK"
