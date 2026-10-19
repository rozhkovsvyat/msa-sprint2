#!/bin/bash
# Проверка DNS изнутри кластера: под с curl обращается к http://booking-service/ping по имени сервиса.
set -euo pipefail
k() { if [[ -n "${NAMESPACE:-}" ]]; then kubectl -n "$NAMESPACE" "$@"; else kubectl "$@"; fi; }
probe="dns-test-$RANDOM"
trap 'k delete pod "$probe" --ignore-not-found --wait=false >/dev/null 2>&1' EXIT

echo "▶️ Running in-cluster DNS test${NAMESPACE:+ (namespace $NAMESPACE)}..."
# Под без sidecar Istio; GET с таймаутами и двумя повторами на время обновления эндпоинтов.
k run "$probe" --restart=Never --image=curlimages/curl:8.12.1 --labels='sidecar.istio.io/inject=false' \
  --command -- curl -sf --connect-timeout 2 --max-time 5 --retry 2 --retry-all-errors http://booking-service/ping >/dev/null
for _ in $(seq 1 60); do
  phase=$(k get pod "$probe" -o jsonpath='{.status.phase}')
  [[ "$phase" == Succeeded || "$phase" == Failed ]] && break
  sleep 1
done
response=$(k logs "$probe" 2>/dev/null || true)
echo "$response"
if [[ "$phase" == Succeeded && "$response" == pong ]]; then echo "✅ Success"; else echo "❌ Failed (pod phase: $phase)"; exit 1; fi
