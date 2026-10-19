#!/bin/bash

set -e

echo "▶️ Проверка установки Istio..."
istioctl version
kubectl get pods -n istio-system

echo "▶️ Поды booking-service: 2/2 = контейнер сервиса + sidecar Envoy"
kubectl get pods -l app=booking-service -L version
# Istio 1.31 внедряет istio-proxy нативным sidecar: initContainer с restartPolicy: Always.
kubectl get pods -l app=booking-service -o jsonpath='{range .items[*]}{.metadata.name}: {range .spec.containers[*]}{.name}{end} + {range .spec.initContainers[?(@.restartPolicy=="Always")]}{.name} (sidecar){end}{"\n"}{end}'

echo "▶️ Конфигурация маршрутизации"
kubectl get gateway,virtualservice,destinationrule -n default
kubectl get envoyfilter -n istio-system
istioctl analyze -n default

echo "▶️ Проверка Istio инъекции в default namespace..."
kubectl get namespace default -o json | jq '.metadata.labels."istio-injection"'