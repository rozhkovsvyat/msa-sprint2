#!/bin/bash
# Circuit breaking (DestinationRule connectionPool): нагрузка из пода внутри mesh превышает лимиты,
# лишние запросы сразу получают 503 (флаг UO в sidecar), а не ждут в очереди.
set -e
source "$(dirname "$0")/lib.sh"
kubectl delete pod fortio --ignore-not-found >/dev/null
kubectl apply -f - >/dev/null <<'YAML'
apiVersion: v1
kind: Pod
metadata:
  name: fortio
  labels: { app: fortio }
spec:
  containers:
    - name: fortio
      image: fortio/fortio:1.69.5
      args: ["server"]
YAML
kubectl wait --for=condition=Ready pod/fortio --timeout=120s >/dev/null
codes() { grep -E "^Code [0-9]+" | sed -E 's/^Code ([0-9]+) : ([0-9]+).*/\1 \2/'; }
code_count() { echo "$1" | awk -v c="$2" '$1==c{n=$2} END{print n+0}'; }

echo "▶️ 1 соединение, 20 запросов — в пределах лимита"
out=$(kubectl exec fortio -c fortio -- fortio load -c 1 -qps 0 -n 20 -loglevel Warning http://booking-service/ping 2>&1 | codes)
echo "$out" | awk '{printf "  код %s — ответов: %s\n", $1, $2}'
expect "ответов 200" "$(code_count "$out" 200)" 20 20

echo "▶️ 40 параллельных соединений, 400 запросов — сверх maxConnections=10 и http1MaxPendingRequests=5"
out=$(kubectl exec fortio -c fortio -- fortio load -c 40 -qps 0 -n 400 -loglevel Warning http://booking-service/ping 2>&1 | codes)
echo "$out" | awk '{printf "  код %s — ответов: %s\n", $1, $2}'
expect "ответов 503 (отсечены)" "$(code_count "$out" 503)" 1 399
expect "ответов 200 (обслужены)" "$(code_count "$out" 200)" 1 399

echo "▶️ Метрика sidecar клиента istio_requests_total: код ответа и флаг (UO — отказ по переполнению, circuit breaker)"
stats=$(kubectl exec fortio -c istio-proxy -- pilot-agent request GET stats 2>/dev/null \
  | grep "istio_requests_total" | grep "destination_service.booking-service" \
  | sed -E 's/.*response_code\.([0-9]+)\..*response_flags\.([^.]+)\..*: ([0-9]+)$/\1 \2 \3/')
echo "$stats" | awk '{printf "  код %s, флаг %s — запросов: %s\n", $1, $2, $3}'
uo=$(echo "$stats" | awk '$2=="UO"{n+=$3} END{print n+0}')
expect "запросов с флагом UO" "$uo" 1 400
kubectl delete pod fortio --wait=false >/dev/null
finish
