# Задание 5. Управление трафиком booking-service с Istio

Istio 1.31.2 (профиль `demo`) установлен в Minikube, sidecar внедряется в namespace `default`. booking-service — тот же образ, что в задании 4, — развёрнут двумя Helm-релизами одного чарта:

| Версия | Values | Реплики | Отличие |
| :- | :- | :- | :- |
| v1 | [values-v1.yaml](values-v1.yaml) | 2 | Основная версия; создаёт общий Service `booking-service` |
| v2 | [values-v2.yaml](values-v2.yaml) | 1 | `ENABLE_FEATURE_X=true` — открыт `/feature`. Сюда идут 10% трафика и все запросы с флагом |

Чарт задания 4 дополнен меткой версии и флагом `service.create`: общий Service создаёт релиз v1 и выбирает поды обеих версий по метке `app`, а Istio различает версии по метке `version` (subsets). Версию ответившего пода сервис возвращает в заголовке `X-Booking-Service-Version`, по нему проверочные скрипты считают распределение.

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

Скрипты обращаются к `127.0.0.1:19090`, потому что порт 9090 на машине занят gRPC booking-service из задания 2.

## Маршрутизация

Запросы приходят через `istio-ingressgateway` ([gateway.yaml](gateway.yaml)). В [virtual-service.yaml](virtual-service.yaml) два правила, Envoy берёт первое подходящее:

| Маршрут | Какие запросы | Куда |
| :- | :- | :- |
| `feature-flag-v2` | с флагом: EnvoyFilter поставил внутренний заголовок `x-booking-route: v2` | subset v2 |
| `canary-with-fallback` | все остальные | 90% — пул «сначала v1, при отказе v2», 10% — subset v2 |

Оба правила повторяют запрос до двух раз при 5xx и сбоях соединения; время попытки — 4 с, общее — 10 с. Через шлюз идут только служебные GET-запросы, их повторять безопасно. Создание бронирования идёт мимо шлюза, по gRPC от монолита: без ключа идемпотентности его нельзя было бы повторять после 5xx.

[destination-rule.yaml](destination-rule.yaml):
- subsets v1 и v2;
- circuit breaking: не больше 10 соединений, очередь из 5 запросов, 20 запросов на соединение; сверх лимита Envoy сразу отвечает 503 с флагом `UO`;
- outlier detection: под, ответивший 5xx три раза подряд, исключается из балансировки на 15 с;
- `failoverPriority: ["version=v1"]`: в пуле без subset поды v1 получают приоритет, поды v2 — резерв.

[envoy-filter.yaml](envoy-filter.yaml) — Lua-фильтр на шлюзе: при `X-Feature-Enabled: true` он ставит `x-booking-route: v2` и заново выбирает маршрут. Присланный клиентом `x-booking-route` фильтр удаляет, поэтому попасть на v2 в обход флага нельзя.

## Почему так

**Fallback v1 → v2.** Повтор в VirtualService идёт внутри того же subset и на v2 запрос не переведёт. Поэтому 90% трафика идут не в subset v1, а в общий пул `booking-service`, где у подов v1 приоритет ([`failoverPriority`](https://istio.io/latest/docs/reference/config/networking/destination-rule/)), и 10% — сразу в subset v2. Пока v1 здорова, соотношение остаётся 90/10. Если подов v1 нет или outlier detection их исключил, общий пул обслуживают поды v2. Чтобы проверить именно случай «v1 возвращает ошибку», у сервиса есть переменная `FAULT_INJECTION_STATUS`: под с ней отвечает на `/version` заданным кодом. Fault injection из Istio тут не подходит: ошибку вернёт сам шлюз, запрос до пода не дойдёт, и outlier detection её не увидит.

**Повторы.** Задание предлагает настроить повторы в DestinationRule, но в Istio число попыток, таймаут попытки и `retryOn` задаются только в VirtualService. В DestinationRule есть лишь общий лимит одновременных повторов `connectionPool.http.maxRetries`, он оставлен по умолчанию. Поэтому повторы описаны в VirtualService, а circuit breaking и outlier detection — в DestinationRule.

**Фича-флаг.** Как требует задание, флаг обрабатывает EnvoyFilter на входном шлюзе: Lua-фильтр превращает `X-Feature-Enabled: true` во внутренний заголовок маршрута и стирает такой же заголовок, если его прислал клиент. Фильтр завязан на внутреннее устройство Envoy, поэтому его надо перепроверять при каждом обновлении Istio; без фильтра то же самое сделал бы `match.headers` в VirtualService.

## Проверки

Скрипты из шаблона переписаны под вход через шлюз: `check-canary`, `check-feature-flag` и `check-fallback` считают ответы по версиям (заголовок `X-Booking-Service-Version`) и завершаются с ошибкой, если результат не совпал с ожидаемым; `check-istio` показывает состояние Istio и подов. Общие функции — в [lib.sh](../lib.sh). Логи сняты 31.10.2026 на том же образе, что в задании 4, — `booking-service:6201a7d2` (виден в [check-istio.log](check-istio.log)).

| Скрипт | Что проверяет | Результат | Лог |
| :- | :- | :- | :- |
| [check-istio.sh](../check-istio.sh) | Istio установлен, инъекция включена, поды v1 и v2 — `2/2` (сервис и sidecar), `istioctl analyze` без замечаний | Три пода `2/2`, замечаний нет | [check-istio.log](check-istio.log) |
| [check-canary.sh](../check-canary.sh) | 200 запросов, доля v2 около 10% | v1 — 183, v2 — 17 | [check-canary.log](check-canary.log) |
| [check-feature-flag.sh](../check-feature-flag.sh) | С `X-Feature-Enabled: true` все запросы идут на v2 и `/feature` отвечает; без флага и с поддельным `x-booking-route` — обычная канарейка | С флагом 20 из 20 на v2; без флага — 0 из 20, с подделкой — 1 из 20 | [check-feature-flag.log](check-feature-flag.log) |
| [check-fallback.sh](../check-fallback.sh) | Гасим один под v1, как в задании; затем v1 отвечает 503; затем все поды v1 погашены; в конце v1 возвращается | Удаление пода — без ошибок. При 503 у v1 на 99 запросов из 100 ответила v2; один запрос получил 503 — пока outlier detection не исключил поды v1, повторы тоже уходят на v1. Без v1 — 100 из 100 от v2 | [check-fallback.log](check-fallback.log) |
| [check-circuit-breaker.sh](../check-circuit-breaker.sh) | Нагрузка из пода fortio внутри mesh: 40 соединений при лимите 10 | Из 400 запросов 180 обслужены, 220 сразу получили 503; счётчик `upstream_rq_pending_overflow` — 220 | [check-circuit-breaker.log](check-circuit-breaker.log) |

## Ограничения

- Маршрутизация настроена на входном шлюзе. Для вызовов внутри mesh нужен такой же VirtualService с `gateways: [mesh]`.
- Маршруты, повторы и circuit breaking проверены на служебных `/ping`, `/version` и `/feature`: в кластере только booking-service, без БД и монолита (почему — в отчёте задания 4).
- Канарейка и фича-флаг независимы: 10% запросов без заголовка тоже попадают на v2 с включённой функцией. Чтобы открыть функцию только закрытой группе, флаг нужно проверять и в коде сервиса.
