#!/bin/bash

set -e

echo "▶️ Проверка установки Istio..."
istioctl version
kubectl get pods -n istio-system

echo "▶️ Поды booking-service: 2/2 = контейнер сервиса + sidecar Envoy"
kubectl get pods -l app=booking-service -L version
# Istio 1.31 внедряет istio-proxy нативным sidecar: initContainer с restartPolicy: Always.
kubectl get pods -l app=booking-service -o jsonpath='{range .items[*]}{.metadata.name}: {range .spec.containers[*]}{.name}{end} + {range .spec.initContainers[?(@.restartPolicy=="Always")]}{.name} (sidecar){end}{"\n"}{end}'

echo "▶️ Образы v1 и v2"
kubectl get deployments -l app=booking-service -o 'custom-columns=NAME:.metadata.name,IMAGE:.spec.template.spec.containers[0].image'

echo "▶️ Конфигурация маршрутизации"
kubectl get gateway,virtualservice,destinationrule -n default
kubectl get envoyfilter -n istio-system
istioctl analyze -n default

echo "▶️ Проверка Istio инъекции в default namespace..."
kubectl get namespace default -o json | jq '.metadata.labels."istio-injection"'

[ "$(kubectl get namespace default -o jsonpath='{.metadata.labels.istio-injection}')" = enabled ] \
  || { echo "❌ инъекция sidecar в namespace default выключена"; exit 1; }
# Без sidecar маршруты, повторы и лимиты Istio на под не действуют.
without=$(kubectl get pods -l app=booking-service -o jsonpath='{range .items[*]}{.metadata.name} {.spec.initContainers[*].name} {.spec.containers[*].name}{"\n"}{end}' | grep -v istio-proxy || true)
[ -z "$without" ] || { echo "❌ поды без sidecar: $without"; exit 1; }
for v in v1 v2; do
  [ -n "$(kubectl get pods -l app=booking-service,version=$v -o name)" ] || { echo "❌ нет подов $v"; exit 1; }
done
kubectl wait --for=condition=Ready pod -l app=booking-service --timeout=60s >/dev/null
echo "✅ OK: инъекция включена, у подов v1 и v2 есть sidecar, все готовы"
