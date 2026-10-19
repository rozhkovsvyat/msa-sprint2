#!/bin/bash
# Fallback v1 → v2 (DestinationRule: failoverPriority version=v1 + outlier detection).
# 0. Исходное состояние: 2 пода v1 — канарейка 90/10.
# 1. Сбой одного пода: сначала в момент удаления, затем после стабилизации на одной реплике v1.
# 2. Под v1 жив и проходит пробы, но отвечает 503: outlier detection выбрасывает его, трафик уходит на v2.
# 3. v1 погашена полностью: весь трафик на v2, ошибок нет.
# В конце v1 возвращается в исходное состояние.
set -e
source "$(dirname "$0")/lib.sh"
N=100
read -r lo hi <<<"$(binomial_bounds "$N" 0.1)"
v1_ready() { kubectl rollout status deployment/booking-service-v1 --timeout=120s >/dev/null; }

restore() {
  kubectl delete pod fault-probe --ignore-not-found --wait=false >/dev/null
  kubectl set env deployment/booking-service-v1 FAULT_INJECTION_STATUS- >/dev/null
  kubectl scale deployment booking-service-v1 --replicas=2 >/dev/null
  v1_ready
  wait_gateway_endpoints 3
}
trap restore EXIT

v1_ready
wait_gateway_endpoints 3

echo "▶️ Шаг 0. Исходное состояние: v1 — 2 пода, $N запросов"
kubectl get pods -l app=booking-service -L version --no-headers
sample "$N" "$GW/ping"
expect "ответов v2" "$(count v2)" "$lo" "$hi"
expect "ошибок" "$(errors)" 0 0

echo "▶️ Шаг 1а. Удаляем один под v1 и сразу шлём 50 запросов"
pod=$(kubectl get pods -l app=booking-service,version=v1 -o jsonpath='{.items[0].metadata.name}')
kubectl delete pod "$pod" --wait=false
gateway_endpoints
sample 50 "$GW/ping"
expect "ошибок" "$(errors)" 0 0

echo "▶️ Шаг 1б. Оставляем одну реплику v1, ждём готовности и шлём $N запросов"
kubectl scale deployment booking-service-v1 --replicas=1 >/dev/null
v1_ready
wait_gateway_endpoints 2
gateway_endpoints
sample "$N" "$GW/ping"
expect "ответов v2 (одна здоровая реплика v1 держит свою долю)" "$(count v2)" "$lo" "$hi"
expect "ошибок" "$(errors)" 0 0

echo "▶️ Шаг 2. Под v1 жив и проходит пробы, но /version отвечает 503 (FAULT_INJECTION_STATUS=503), $N запросов на /version"
kubectl set env deployment/booking-service-v1 FAULT_INJECTION_STATUS=503 >/dev/null
v1_ready
wait_gateway_endpoints 2
# Флаг outlier detection до и после запросов показывает, что под исключён именно из-за 5xx.
ejections() { istioctl proxy-config endpoint -n istio-system deploy/istio-ingressgateway \
  --cluster "outbound|80||booking-service.default.svc.cluster.local" -o json \
  | jq '[.[0].hostStatuses[] | select(.healthStatus.failedOutlierCheck == true)] | length'; }
before=$(ejections)
# Запрос из пода без sidecar напрямую к поду v1 показывает, что он жив и отвечает 503.
podip=$(kubectl get pods -l app=booking-service,version=v1 -o json | jq -r '[.items[] | select(.metadata.deletionTimestamp == null) | select(any(.status.conditions[]?; .type == "Ready" and .status == "True"))][0].status.podIP')
[[ -n "$podip" && "$podip" != null ]]
kubectl delete pod fault-probe --ignore-not-found --wait=true --timeout=60s >/dev/null
kubectl run fault-probe --image=curlimages/curl:8.12.1 --restart=Never --labels='sidecar.istio.io/inject=false' \
  --command -- sh -c "code=\$(curl --connect-timeout 2 --max-time 5 -s -o /dev/null -w '%{http_code}' http://${podip}:8080/version); echo v1_status=\$code; test \"\$code\" = 503" >/dev/null
kubectl wait --for=jsonpath='{.status.phase}'=Succeeded pod/fault-probe --timeout=60s >/dev/null
kubectl logs fault-probe
kubectl delete pod fault-probe --wait=false >/dev/null
sample "$N" "$GW/version"
after=$(ejections)
expect "подов v1, исключённых outlier detection" "$((after - before))" 1 100
gateway_endpoints
expect "успешных ответов (повторы и выброс пода скрывают сбой)" "$(count v2)" "$((N - 5))" "$N"
expect "ответов v1" "$(count v1)" 0 0

echo "▶️ Шаг 3. Гасим все поды v1 (scale 0), $N запросов"
kubectl scale deployment booking-service-v1 --replicas=0 >/dev/null
kubectl wait --for=delete pod -l app=booking-service,version=v1 --timeout=60s >/dev/null 2>&1 || true
wait_gateway_endpoints 1
kubectl get pods -l app=booking-service -L version --no-headers
sample "$N" "$GW/ping"
expect "ответов v2" "$(count v2)" "$N" "$N"

echo "▶️ Возвращаем v1 (2 реплики, без инъекции ошибок), $N запросов"
trap - EXIT
restore
sample "$N" "$GW/ping"
expect "ответов v2" "$(count v2)" "$lo" "$hi"
expect "ошибок" "$(errors)" 0 0
finish
