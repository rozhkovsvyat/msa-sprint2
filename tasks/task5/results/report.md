# Задание 5. Управление трафиком booking-service с Istio

Istio 1.31.2 (профиль `demo`) в Minikube, sidecar внедряется в namespace `default`. booking-service — образ из задания 4 — развёрнут двумя Helm-релизами одного чарта:

| Версия | Values | Реплики | Отличие |
| :- | :- | :- | :- |
| v1 | [values-v1.yaml](values-v1.yaml) | 2 | Основная версия; создаёт общий Service `booking-service` |
| v2 | [values-v2.yaml](values-v2.yaml) | 1 | `ENABLE_FEATURE_X=true` — открыт `/feature`. Сюда идут 10% трафика и все запросы с флагом |

Чарт задания 4 дополнен метками версии: Service выбирает поды обеих версий по метке `app`, а Istio различает версии по метке `version` (subsets). Версию ответившего пода показывает заголовок `X-Booking-Service-Version`, который ставит сам сервис, поэтому проверочные скрипты считают распределение по факту.

## Развёртывание

```bash
istioctl install --set profile=demo -y
kubectl label namespace default istio-injection=enabled --overwrite

cd tasks/task5
TAG=...   # тег образа из пайплайна задания 4 (образ уже загружен в Minikube)
helm upgrade --install booking-service-v1 helm/booking-service -f helm/booking-service/values-v1.yaml --set image.tag=$TAG --wait
helm upgrade --install booking-service-v2 helm/booking-service -f helm/booking-service/values-v2.yaml --set image.tag=$TAG --wait
kubectl apply -f istio/gateway.yaml -f istio/destination-rule.yaml -f istio/virtual-service.yaml -f istio/envoy-filter.yaml
istioctl analyze

kubectl -n istio-system port-forward svc/istio-ingressgateway 19090:80   # вход для проверочных скриптов
```

Скрипты обращаются к `127.0.0.1:19090`: порт 9090 на машине занят gRPC booking-service из задания 2.

## Маршрутизация

Запросы приходят через `istio-ingressgateway` ([gateway.yaml](gateway.yaml)). В [virtual-service.yaml](virtual-service.yaml) четыре маршрута, Envoy выбирает первый подходящий:

| Маршрут | Какие запросы | Куда | Повторы |
| :- | :- | :- | :- |
| `feature-flag-v2-read` | GET/HEAD с внутренним заголовком `x-booking-route: v2` | subset v2 | до двух при 5xx и сбоях соединения |
| `feature-flag-v2` | остальные методы с тем же заголовком | subset v2 | только если соединение не установлено |
| `canary-with-fallback-read` | остальные GET/HEAD | 90% — пул «сначала v1, при отказе v2», 10% — subset v2 | до двух при 5xx и сбоях соединения |
| `canary-with-fallback` | остальные методы | так же 90/10 | только если соединение не установлено |

Чтение и запись разделены потому, что у создания бронирования пока нет ключа идемпотентности (ADR-001, Н7): повтор после ответа 5xx мог бы создать второе бронирование. Запись повторяется только при `connect-failure` и `refused-stream`, когда запрос точно не дошёл до сервиса. Время попытки — 4 с, больше бюджета сервиса на обращения к монолиту (3 с), общее время маршрута — 10 с.

[destination-rule.yaml](destination-rule.yaml):
- subsets v1 и v2;
- circuit breaking: не больше 10 соединений, очередь из 5 запросов, 20 запросов на соединение; сверх лимита Envoy сразу отвечает 503 с флагом `UO`;
- outlier detection: под, ответивший 5xx три раза подряд, исключается из балансировки на 15 с;
- `failoverPriority: ["version=v1"]`: в пуле без subset поды v1 получают приоритет, поды v2 — резерв.

[envoy-filter.yaml](envoy-filter.yaml) — Lua-фильтр на шлюзе. При `X-Feature-Enabled: true` он ставит `x-booking-route: v2` и заново выбирает маршрут; присланный клиентом `x-booking-route` удаляется, поэтому попасть на v2 в обход флага нельзя.

## Решения, которые отличаются от формулировки задания

**Fallback v1 → v2.** Правила «если v1 вернула ошибку, иди в другой subset» в VirtualService нет: повтор идёт внутри того же subset, а outlier detection исключает поды только внутри своего пула. Поэтому 90% канарейки направлены на хост без subset с приоритетом подов `version=v1` ([`failoverPriority`](https://istio.io/latest/docs/reference/config/networking/destination-rule/)). Пока v1 здорова, эта доля целиком уходит в v1 и канарейка держит 90/10. Если подов v1 нет или outlier detection их исключил, доля переходит на v2. Без `failoverPriority` маршрут без subset делил бы трафик между всеми подами, и канарейка сломалась бы.

**Повторы — в VirtualService.** В задании повторы предлагается настроить в DestinationRule, но у DestinationRule таких параметров нет. Повторы заданы в VirtualService, circuit breaking и outlier detection — в DestinationRule.

**Фича-флаг через EnvoyFilter.** Обычный способ — `match.headers` в VirtualService; EnvoyFilter использован по заданию и делает одно: превращает внешний флаг во внутренний признак маршрута и защищает его от подделки. Istio сам предупреждает, что EnvoyFilter зависит от внутреннего устройства Envoy, поэтому фильтр перепроверяется при каждом обновлении Istio.

## Проверки

| Скрипт | Что проверяет | Лог |
| :- | :- | :- |
| [check-istio.sh](../check-istio.sh) | istiod и шлюзы работают, инъекция включена, поды v1 и v2 — `2/2` (сервис и sidecar), `istioctl analyze` без замечаний | [check-istio.log](check-istio.log) |
| [check-canary.sh](../check-canary.sh) | 400 запросов: доля v2 около 10% (в пределах трёх стандартных отклонений), ошибок нет | [check-canary.log](check-canary.log) |
| [check-feature-flag.sh](../check-feature-flag.sh) | С `X-Feature-Enabled: true` все запросы на v2 и `/feature` отвечает; без флага и с поддельным `x-booking-route` — обычная канарейка | [check-feature-flag.log](check-feature-flag.log) |
| [check-fallback.sh](../check-fallback.sh) | Удаление пода v1, одна реплика v1, живой под v1 с ответом 503 (`FAULT_INJECTION_STATUS=503`), все поды v1 погашены — запросы обслуживает v2; затем v1 возвращается | [check-fallback.log](check-fallback.log) |
| [check-negative-fallback.sh](../check-negative-fallback.sh) | Отрицательный контроль: при погашенной v1 обычная канарейка «90% → subset v1» отдаёт 503, рабочий маршрут — нет | [negative-control-classic-canary-no-fallback.log](negative-control-classic-canary-no-fallback.log) |
| [check-circuit-breaker.sh](../check-circuit-breaker.sh) | Нагрузка из пода внутри mesh сверх лимитов: часть запросов сразу получает 503 с флагом `UO` | [check-circuit-breaker.log](check-circuit-breaker.log) |

Общие функции — в [lib.sh](../lib.sh). Каждый скрипт завершается с ненулевым кодом, если ожидание не выполнено.

## Ограничения

- Маршрутизация настроена на входном шлюзе. Для вызовов внутри mesh нужен такой же VirtualService с `gateways: [mesh]` и хостом `booking-service`.
- Маршруты, повторы и circuit breaking проверяются на служебном HTTP (`/ping`, `/version`, `/feature`): в кластере нет БД, монолита и Kafka, бизнес-путь бронирования проверяется на стенде задания 2.
- Канарейка и фича-флаг независимы: 10% запросов без заголовка тоже попадают на v2 с включённой функцией. Чтобы открыть функцию только закрытой группе, флаг нужно проверять и в коде сервиса.
- Метрики Istio показывают версию по метке `app.kubernetes.io/version` (тег образа), а маршрутизация — по метке `version`; для дашбордов метки стоит выровнять.
