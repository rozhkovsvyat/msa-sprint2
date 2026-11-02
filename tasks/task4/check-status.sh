#!/bin/bash
# Статус развёртывания и вызов /ping через заранее запущенный port-forward:
#   kubectl -n <namespace> port-forward svc/booking-service 8080:80
set -euo pipefail
NAMESPACE="${NAMESPACE:-default}"
URL="${URL:-http://localhost:8080}"
k() { kubectl -n "$NAMESPACE" "$@"; }

echo "▶️ Checking booking-service deployment (namespace $NAMESPACE)..."
k rollout status deployment/booking-service --timeout=120s
k get pods -l app=booking-service

echo
echo "▶️ Checking service..."
k get svc booking-service

echo
echo "▶️ Helm release:"
helm -n "$NAMESPACE" list --filter '^booking-service$'

echo
echo "▶️ Quick curl $URL/ping"
body=$(curl --connect-timeout 2 --max-time 5 -fsS "$URL/ping")
echo "$body"
[ "$body" = pong ] && echo "✅ Reachable"
