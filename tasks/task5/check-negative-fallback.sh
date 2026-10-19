#!/usr/bin/env bash
# Отрицательный контроль: v1 погашена; меняем только направление 90% трафика с общего пула на subset v1.
# Обычная канарейка без fallback отдаёт 503 вместо перехода на v2.
set -euo pipefail
cd "$(dirname "$0")"
source ./lib.sh
tmp=$(mktemp)
restore() {
  kubectl apply -f istio/virtual-service.yaml >/dev/null
  kubectl scale deployment booking-service-v1 --replicas=2 >/dev/null
  kubectl rollout status deployment/booking-service-v1 --timeout=120s >/dev/null
  wait_gateway_endpoints 3
  rm -f "$tmp"
}
trap restore EXIT
kubectl scale deployment booking-service-v1 --replicas=0 >/dev/null
kubectl wait --for=delete pod -l app=booking-service,version=v1 --timeout=60s >/dev/null
wait_gateway_endpoints 1
echo '▶️ Рабочий маршрут: общий пул с приоритетом v1 → v2'
sample 100 "$GW/ping"
expect 'ответов v2' "$(count v2)" 100 100
[ "$FAILED" = 0 ] || exit 1
# Оба 90%-маршрута (чтение и запись) направляем в subset v1.
awk '{ if (prev ~ /^ +host: booking-service$/ && $0 ~ /^ +weight: 90$/) print "            subset: v1"; print; prev = $0 }' \
  istio/virtual-service.yaml > "$tmp"
[[ $(grep -c 'subset: v1' "$tmp") == 2 ]]
kubectl apply -f "$tmp" >/dev/null
# Ждём, пока новый маршрут дойдёт до Envoy шлюза: kubectl apply сам этого не гарантирует.
for _ in $(seq 1 30); do
  if istioctl proxy-config routes -n istio-system deploy/istio-ingressgateway -o json \
      | jq -e '.. | objects | select(.name? == "canary-with-fallback-read") |
        .route.weightedClusters.clusters[0].name | contains("|v1|")' >/dev/null; then break; fi
  sleep 1
done
echo '▶️ Контроль: те же запросы и поды, 90% идут в пустой subset v1'
sample 100 "$GW/ping"
read -r lo hi <<<"$(binomial_bounds 100 0.9)"
expect '503 при отсутствии fallback' "$(count HTTP_503)" "$lo" "$hi"
expect 'оставшиеся ответы v2' "$(count v2)" "$((100-hi))" "$((100-lo))"
finish
