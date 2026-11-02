# Общие функции проверочных скриптов. Запросы идут через istio-ingressgateway:
#   kubectl -n istio-system port-forward svc/istio-ingressgateway 19090:80
GW="${GW:-http://127.0.0.1:19090}"

# Шлёт N запросов и считает ответы по строкам «код версия».
# Версию ответившего пода сервис пишет в заголовок X-Booking-Service-Version.
sample() {
  local n=$1; shift
  for _ in $(seq 1 "$n"); do
    curl -s -o /dev/null --max-time 12 -w '%{http_code} %header{x-booking-service-version}\n' "$@"
  done | sort | uniq -c
}

# Сколько в выводе sample ответов вида «200 v2»
count() { echo "$1" | awk -v k="$2" '{ n = $1; $1 = ""; if (substr($0, 2) == k) c = n } END { print c + 0 }'; }

# Сколько всего успешных ответов (200)
count_ok() { echo "$1" | awk '$2 == "200" { c += $1 } END { print c + 0 }'; }
