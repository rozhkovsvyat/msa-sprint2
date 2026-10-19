#!/bin/bash
# Канареечный выпуск: ~90% ответов от v1, ~10% от v2 (веса случайные), ошибок нет.
set -e
source "$(dirname "$0")/lib.sh"
N="${N:-400}"
echo "▶️ Checking canary release (90% v1, 10% v2) — ${N} запросов на ${GW}/ping"
sample "$N" "$GW/ping"
read -r lo hi <<<"$(binomial_bounds "$N" 0.1)"
expect "ответов v2" "$(count v2)" "$lo" "$hi"
expect "ответов v1" "$(count v1)" "$((N - hi))" "$((N - lo))"
expect "ошибок" "$(errors)" 0 0
finish
