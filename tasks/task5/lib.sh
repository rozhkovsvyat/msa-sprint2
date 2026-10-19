# Общие функции проверочных скриптов. Вход — istio-ingressgateway:
#   kubectl -n istio-system port-forward svc/istio-ingressgateway 19090:80
# Каждый скрипт проверяет ожидания и завершается с кодом 1, если хоть одно не выполнено.
GW="${GW:-http://127.0.0.1:19090}"
FAILED=0

# Версия, ответившая на запрос (заголовок X-Booking-Service-Version ставит сам сервис), или код ошибки
version_of() {
  local out code ver
  out=$(curl --connect-timeout 2 --max-time 12 -s -o /dev/null -D - -w 'HTTP_CODE:%{http_code}' "$@")
  code=$(echo "$out" | sed -n 's/.*HTTP_CODE:\([0-9]*\).*/\1/p')
  ver=$(echo "$out" | tr -d '\r' | awk -F': ' 'tolower($1)=="x-booking-service-version"{print $2}')
  if [ "$code" = "200" ] && [ -n "$ver" ]; then echo "$ver"; else echo "HTTP_$code"; fi
}

# Распределение ответов: $1 — число запросов, далее аргументы curl. Печатает и сохраняет в DIST.
sample() {
  local n=$1; shift
  DIST=$(for _ in $(seq 1 "$n"); do version_of "$@"; done | sort | uniq -c)
  echo "$DIST"
}
count() { echo "$DIST" | awk -v k="$1" '$2==k{n=$1} END{print n+0}'; }
errors() { echo "$DIST" | awk '$2 ~ /^HTTP_/{n+=$1} END{print n+0}'; }

# $1 — что проверяем, $2 — факт, $3 и $4 — допустимые границы включительно
expect() {
  if [ "$2" -ge "$3" ] && [ "$2" -le "$4" ]; then
    echo "  [PASS] $1: $2 (ожидается $3–$4)"
  else
    echo "  [FAIL] $1: $2 (ожидается $3–$4)"; FAILED=1
  fi
}

# Границы для доли p из n запросов: среднее ± 3 стандартных отклонения (ошибочный FAIL — около 0,3% прогонов)
binomial_bounds() { awk -v n="$1" -v p="$2" 'BEGIN{m=n*p; s=sqrt(n*p*(1-p)); lo=int(m-3*s); if(lo<0)lo=0; hi=int(m+3*s+0.999); if(hi>n)hi=n; print lo, hi}'; }

# Поды booking-service глазами ingress-шлюза: статус и результат outlier detection по каждому
gateway_endpoints() {
  istioctl proxy-config endpoint -n istio-system deploy/istio-ingressgateway \
    --cluster "outbound|80||booking-service.default.svc.cluster.local"
}

finish() {
  if [ "$FAILED" = 0 ]; then echo "✅ Все проверки пройдены"; else echo "❌ Есть непройденные проверки"; exit 1; fi
}

# Ждём, пока список подов обновится в самом Envoy ingress-шлюза, а не только в Kubernetes.
wait_gateway_endpoints() {
  local expected=$1 actual actual_ips expected_ips
  for _ in $(seq 1 30); do
    actual=$(istioctl proxy-config endpoint -n istio-system deploy/istio-ingressgateway \
      --cluster "outbound|80||booking-service.default.svc.cluster.local" -o json \
      | jq '.[0].hostStatuses | length')
    actual_ips=$(istioctl proxy-config endpoint -n istio-system deploy/istio-ingressgateway \
      --cluster "outbound|80||booking-service.default.svc.cluster.local" -o json \
      | jq -r '.[0].hostStatuses[].address.socketAddress.address' | sort)
    expected_ips=$(kubectl get endpointslices -l kubernetes.io/service-name=booking-service -o json \
      | jq -r '.items[].endpoints[] | select(.conditions.ready==true) | .addresses[]' | sort)
    if [ "$actual" = "$expected" ] && [ "$actual_ips" = "$expected_ips" ]; then
      echo "  [PASS] подов в балансировке шлюза: $actual"
      return 0
    fi
    sleep 1
  done
  echo "  [FAIL] подов в балансировке шлюза: $actual, ожидалось $expected"
  return 1
}
