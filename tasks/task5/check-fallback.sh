#!/bin/bash
# Fallback v1 → v2. 90% трафика идут в общий пул booking-service, где у подов v1 приоритет
# (failoverPriority в DestinationRule). Если подов v1 нет или outlier detection их исключил, пул обслуживает v2.

set -e
source "$(dirname "$0")/lib.sh"

# В конце возвращаем v1 как было: 2 реплики, без ошибок
restore() {
  kubectl set env deployment/booking-service-v1 FAULT_INJECTION_STATUS- >/dev/null
  kubectl scale deployment/booking-service-v1 --replicas=2 >/dev/null
  kubectl rollout status deployment/booking-service-v1 --timeout=120s >/dev/null
}
trap restore EXIT

echo "▶️ Testing fallback route..."

echo "▶️ 1. Гасим один под v1 и сразу шлём 50 запросов"
kubectl delete "$(kubectl get pods -l app=booking-service,version=v1 -o name | head -1)" --wait=false
stats=$(sample 50 "$GW/ping")
echo "$stats"
[ "$(count_ok "$stats")" -eq 50 ] || { echo "❌ есть ошибки"; exit 1; }

echo "▶️ 2. v1 работает, но /version отвечает 503 (FAULT_INJECTION_STATUS=503) — 100 запросов на /version"
kubectl set env deployment/booking-service-v1 FAULT_INJECTION_STATUS=503 >/dev/null
kubectl rollout status deployment/booking-service-v1 --timeout=120s >/dev/null
sleep 5   # шлюзу нужно несколько секунд, чтобы узнать адреса новых подов
kubectl get pods -l app=booking-service -L version
stats=$(sample 100 "$GW/version")
echo "$stats"
# Состояние подов глазами шлюза: v1 исключены outlier detection
istioctl proxy-config endpoint deploy/istio-ingressgateway -n istio-system | grep 'booking-service.default'
# Пока outlier detection не исключил поды v1, отдельные запросы могут закончиться 503 даже после повторов
[ "$(count "$stats" "200 v2")" -ge 95 ] || { echo "❌ v2 ответила меньше чем на 95 запросов"; exit 1; }

echo "▶️ 3. Гасим все поды v1 — 100 запросов"
kubectl scale deployment/booking-service-v1 --replicas=0 >/dev/null
kubectl wait --for=delete pod -l app=booking-service,version=v1 --timeout=60s >/dev/null 2>&1 || true
sleep 5
kubectl get pods -l app=booking-service -L version
stats=$(sample 100 "$GW/ping")
echo "$stats"
[ "$(count "$stats" "200 v2")" -eq 100 ] || { echo "❌ не все запросы обслужила v2"; exit 1; }

echo "▶️ 4. Возвращаем v1 — 100 запросов"
trap - EXIT
restore
sleep 5
stats=$(sample 100 "$GW/ping")
echo "$stats"
[ "$(count_ok "$stats")" -eq 100 ] || { echo "❌ есть ошибки"; exit 1; }
echo "✅ OK"
