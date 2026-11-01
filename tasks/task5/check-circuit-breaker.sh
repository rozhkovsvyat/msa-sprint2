#!/bin/bash
# Circuit breaking из DestinationRule: maxConnections 10, http1MaxPendingRequests 5.
# Нагрузку даёт fortio из пода внутри mesh, как в примере Circuit Breaking из документации Istio.

set -e

kubectl delete pod fortio --ignore-not-found >/dev/null
trap 'kubectl delete pod fortio --ignore-not-found --wait=false >/dev/null' EXIT
kubectl apply -f - >/dev/null <<'YAML'
apiVersion: v1
kind: Pod
metadata:
  name: fortio
  labels: { app: fortio }
  annotations:
    # Как в примере Istio: sidecar отдаёт статистику cluster.outbound, в ней счётчик переполнения
    proxy.istio.io/config: |
      proxyStatsMatcher:
        inclusionPrefixes: ["cluster.outbound"]
spec:
  containers:
    - name: fortio
      image: fortio/fortio:1.69.5
      args: ["server"]
YAML
kubectl wait --for=condition=Ready pod/fortio --timeout=120s >/dev/null

echo "▶️ 1 соединение, 20 запросов — в пределах лимита"
within=$(kubectl exec fortio -c fortio -- fortio load -c 1 -qps 0 -n 20 -loglevel Warning http://booking-service/ping 2>&1 | grep '^Code' || true)
echo "$within"

echo "▶️ 40 соединений, 400 запросов — сверх лимита, лишние запросы сразу получают 503"
out=$(kubectl exec fortio -c fortio -- fortio load -c 40 -qps 0 -n 400 -loglevel Warning http://booking-service/ping 2>&1 | grep '^Code' || true)
echo "$out"

echo "▶️ Счётчик переполнения в sidecar fortio (upstream_rq_pending_overflow)"
stats=$(kubectl exec fortio -c istio-proxy -- pilot-agent request GET stats 2>/dev/null | grep 'booking-service.default' | grep 'pending_overflow' || true)
echo "$stats"

rejected=$(echo "$out" | awk '$2 == 503 { print $4 }')
overflow=$(echo "$stats" | awk -F': ' '{ s += $2 } END { print s + 0 }')
[[ $within == "Code 200 : 20 "* ]] || { echo "❌ в пределах лимита есть ошибки"; exit 1; }
(( ${rejected:-0} > 0 )) || { echo "❌ ответов 503 нет — лимит не сработал"; exit 1; }
# Под fortio создаётся заново, поэтому счётчик относится только к этому прогону.
(( overflow >= rejected )) || { echo "❌ не все 503 — от лимита: переполнений $overflow, ответов 503 $rejected"; exit 1; }
echo "✅ OK: $rejected ответов 503, все — из-за переполнения очереди"
