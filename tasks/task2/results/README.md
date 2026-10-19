# Задание 2. Новые сервисы бронирования

Выносим бронирование из монолита по [ADR-001](../../task1/results/ADR-001-strangler-booking.md). Код монолита не меняется: создание и список бронирований он передаёт в booking-service через готовый gRPC-прокси, который включается переменной окружения.

| Компонент | Коротко |
| :- | :- |
| [booking-service](../booking-service) | C#, .NET 10. gRPC по [`booking.proto`](../booking.proto) на порту 9090, служебный HTTP на 8080: `/ping`, `/ready`, `/version`, `/diagnostics/outbox`. Своя PostgreSQL. Пользователя, отель, отзывы и промокод проверяет через REST монолита (Anti-Corruption Layer), правила цены перенесены без изменений. Бронирование и событие о нём пишутся одной транзакцией (transactional outbox) |
| [booking-history-service](../booking-history-service) | Читает топик `booking-events` своей группой, ведёт историю и статистику по пользователям, отелям и дням в своей PostgreSQL. Аналитик читает её ролью `analyst` только на чтение |
| [Контракт события](../contracts/booking-events.asyncapi.yaml) | `BookingCreated`, версия схемы 1, ключ сообщения — id бронирования |
| [Ops-джоба](../migration/migrate-bookings.sh) | Перенос бронирований при переключении и обратно при откате, сверка количества и контрольной суммы |
| [Стенд](../docker-compose.yml) | Монолит с прокси, три PostgreSQL, Kafka и ZooKeeper из задания, ops-джоба и регресс |
| [Регресс](regress.sh) | `test/regress.sh`: фикстуры переносятся той же ops-джобой, полный список проверяется в booking-service по gRPC, у отказов проверяется причина, новые бронирования проверяются в истории |

## Запуск

```bash
docker network create hotelio-net   # если сети ещё нет
cd tasks/task2
docker compose up -d --build
docker compose run --rm regress     # регресс курса против монолита с прокси
```

Схемы новых БД создаёт команда `migrate` из образов сервисов (`booking-schema`, `history-schema`) до их запуска. Пароли — демо-значения стенда; их можно переопределить переменными `LEGACY_DB_PASSWORD`, `BOOKING_DB_PASSWORD`, `HISTORY_DB_PASSWORD`, `ANALYST_PASSWORD` или файлом `.env`.

На чистом стенде переносить нечего, поэтому регресс сам загружает фикстуры в БД монолита и переносит их в booking-service ops-джобой с флагом `--reset-target` (он очищает БД сервиса и нужен только тестовому стенду). Без сервиса регресс падает на первой проверке бронирований:

```bash
docker compose stop booking-service; docker compose run --rm regress; docker compose start booking-service
```

## Стратегия миграции данных

Готовый прокси переносит в booking-service и создание, и список бронирований — сразу для всего трафика. Значит, после переключения пишет только сервис, а старые бронирования должны оказаться в его БД, иначе список через монолит их не покажет. Постоянной синхронизации нет: в каждый момент пишет одна сторона, откат — разовая операция.

1. **Окно переключения.** Останавливаем монолит и booking-service: в момент переноса бронирования никто не пишет. `MIGRATION_OFFLINE=yes` — подтверждение оператора, сам скрипт процессы не останавливает.
2. **Перенос** (`migrate-bookings.sh forward`). Таблица `booking` копируется с теми же id, счётчик id сервиса сдвигается за максимальный. Источник открывается только на чтение, приёмник пишется одной транзакцией, повторный запуск дублей не создаёт.
3. **Пустые даты.** Монолит не заполняет `created_at` у бронирований, созданных через API, а прокси не принимает пустую дату. Такие записи получают время переноса и флаг `created_at_estimated = true`.
4. **История.** Для перенесённых записей в outbox пишутся события `BookingCreated` с `source = migration`. eventId выводится из id бронирования, поэтому повторный перенос не создаёт новых событий.
5. **Сверка.** Количество и контрольная сумма неизменяемых полей совпадают в обеих БД, иначе джоба завершается с ошибкой и переключение не продолжается.
6. **Включение.** Запускаем booking-service, затем монолит с `BOOKING_SERVICE_EXTERNAL_HOST=booking-service`.

Переключение на существующих данных (монолит до этого работал с `BOOKING_SERVICE_EXTERNAL_HOST=false`):

```bash
docker compose stop monolith booking-service
docker compose build
docker compose run --rm -e MIGRATION_OFFLINE=yes booking-migration forward  # схема, перенос, сверка; дальше — только при коде 0
docker compose up -d booking-service booking-history-service
docker compose up -d monolith                                                # по умолчанию прокси включён
```

Откат: `reverse` дописывает в монолит бронирования, созданные после переключения, с теми же id и сдвигает его счётчик. Монолит с `BOOKING_SERVICE_EXTERNAL_HOST=false` снова работает сам: прокси подключается через `@ConditionalOnProperty`.

```bash
docker compose stop monolith booking-service
docker compose run --rm -e MIGRATION_OFFLINE=yes booking-migration reverse
BOOKING_SERVICE_EXTERNAL_HOST=false docker compose up -d monolith
```

Весь цикл «перенос → новое бронирование → откат → повторный перенос» на чистом стенде выполняет [`migration/cutover-drill.sh`](../migration/cutover-drill.sh), результат — [cutover-drill-log.txt](cutover-drill-log.txt). Время переноса растёт с размером таблицы; для боевого объёма его замеряют на копии, и если окно недопустимо — переходят на CDC (альтернативы в ADR-001).

На первых учениях нашлась проблема монолита: в `application.yml` стоит `ddl-auto: create`, и перезапуск при переключении стирал пользователей, отели и бронирования. В compose настройка переопределена окружением `SPRING_JPA_HIBERNATE_DDL_AUTO=update`, код монолита не менялся.

## Стратегия To-Be

Целевое состояние через два месяца и через год описано в ADR-001. Следующие шаги для бронирования:

- личный кабинет читает бронирования из booking-service через GraphQL (задание 3), затем и остальные клиенты обращаются к сервису через шлюз, а не через монолит;
- сервис выпускается Helm-чартом из CI (задание 4), новые версии — канарейкой в Istio (задание 5);
- после стабилизации — ключ идемпотентности создания и исправление дефектов, перенесённых ради паритета (скидка как сумма, ответ 500 на бизнес-отказ);
- прокси монолита заменяется своим кодом, `promo_code` в контракте становится `optional`, архив `booking` в монолите удаляется.

## Ограничения готового прокси

- `GET /api/bookings` без `userId` через монолит падает с 500: прокси вызывает `BookingListRequest.setUserId(null)` ещё до обращения к сервису (конец [listing-rest-vs-grpc.txt](listing-rest-vs-grpc.txt)). Поэтому регресс проверяет полный список в booking-service по gRPC — задание разрешает менять тесты бронирований.
- `promo_code` в `booking.proto` — обычная строка proto3, поэтому монолит отдаёт `"promoCode": ""` вместо `null`.
- Бизнес-отказ сервис возвращает как gRPC `FAILED_PRECONDITION` с причиной, монолит по-прежнему отвечает клиенту 500 без текста.
- Прокси не передаёт сервису `x-request-id` исходного HTTP-запроса, поэтому на этой границе создаётся новый идентификатор.

## Эксплуатация

**Конфигурация.** Адреса и пароли приходят только из окружения, без значений по умолчанию в коде.

| Переменная | Сервис | Назначение |
| :- | :- | :- |
| `BOOKING_DB`, `HISTORY_DB` | оба | Строка подключения Npgsql к своей БД |
| `MONOLITH_URL` | booking-service | Адрес REST монолита |
| `KAFKA_BOOTSTRAP` | оба | Адрес Kafka; booking-service не требует его при `OUTBOX_PUBLISHER=disabled` |
| `OUTBOX_PUBLISHER` | booking-service | `enabled` (по умолчанию) или `disabled`: события копятся в outbox, публикации нет (кластер заданий 4–5) |
| `ANALYST_PASSWORD` | history, команда `migrate` | Пароль роли `analyst` |
| `ENABLE_FEATURE_X`, `SERVICE_VERSION` | booking-service | Фича-флаг (`/feature`) и версия в `/version` и заголовке ответа |
| `FAULT_INJECTION_STATUS` | booking-service | Код ответа `/version` для проверки fallback в Istio (задание 5), по умолчанию 0 — выключено |

**Схема БД.** Миграции — версионные SQL-скрипты DbUp в образе сервиса, журнал — таблица `schemaversions`. Применяет их команда `dotnet BookingService.dll migrate` (или `BookingHistoryService.dll migrate`) того же образа до запуска новой версии: в compose — сервисы `booking-schema` и `history-schema`, в Helm-чарте задания 4 — Job перед обновлением. `/ready` отвечает 503, пока БД недоступна или схема отстаёт от образа; history в этом случае вообще не начинает читать Kafka.

**Доставка событий.** Outbox обрабатывает каждую строку в своей транзакции. Временная ошибка Kafka откладывает строку с растущей паузой (1, 2, 4 … 30 с) до восстановления брокера; ошибку, которую повтором не исправить (сообщение слишком велико, нет доступа к топику), строка получает в карантин (`dead_lettered_at`). Доставка «хотя бы один раз»: сбой после подтверждения Kafka приводит к повторной публикации, и history отсеивает её по `event_id`.

History фиксирует смещение только после записи в историю. Неразбираемое сообщение, другая версия схемы или пустые обязательные поля уходят в очередь ошибочных сообщений (таблица `dead_letter`) с причиной и позицией в Kafka, поток не останавливается. Временная ошибка БД повторяется до пяти раз, затем сообщение читается снова без фиксации смещения.

`GET /diagnostics/outbox` показывает `pending`, `retrying`, `deadLetters` и `oldestAgeSeconds`. Цель — событие доходит до истории за 1 минуту; тревога — `oldestAgeSeconds` больше 60, рост `deadLetters` или отставание группы `booking-history`. После устранения причины строку из карантина публикуют повторно с тем же `event_id`:

```sql
update outbox set dead_lettered_at = null, failure_count = 0, next_attempt_at = now()
where event_id = :'event_id' and published_at is null;
```

Топик `booking-events`: 3 партиции, ключ — id бронирования, хранение 7 суток. На стенде один брокер без репликации, как в задании.

**Обращения к монолиту.** Anti-Corruption Layer — модуль `Legacy` внутри booking-service: он знает форматы монолита и переводит их в модель бронирования.

| Ответ монолита | Модель сервиса |
| :- | :- |
| `GET /api/users/{id}`: `status`, `active`, `blacklisted`; 404 | `UserFacts`; VIP без учёта регистра — база 80, иначе 100; 404 — неизвестный пользователь, отказ «User is inactive» |
| `GET /api/hotels/{id}`: `operational`, `fullyBooked`; `GET /api/reviews/hotel/{id}/trusted` | `HotelFacts`; 404 — неизвестный отель, отказ «Hotel is not operational» |
| `POST /api/promos/validate`: поле `discount`; 400 | Сумма скидки; 400 — промокод не подходит, скидки нет |
| Пустой или неполный ответ, неверный тип, другой код | Монолит недоступен: gRPC `UNAVAILABLE` |

Все обращения одного создания укладываются в бюджет 3 с: попытка — до 1 с, GET повторяется один раз, POST не повторяется. Circuit breaker размыкается на 5 с, если за 10 с ошибкой закончились 10 % вызовов (при восьми вызовах и больше). Одновременно с пода к монолиту уходит до 100 вызовов, ещё 200 ждут в очереди в пределах бюджета — это защищает монолит от лавины запросов. Исчерпан бюджет — `DEADLINE_EXCEEDED`, отказ монолита — `UNAVAILABLE`.

**Идентификатор запроса.** HTTP и gRPC принимают `x-request-id` (1–128 символов: латинские буквы, цифры и `-_.:`), иначе создают новый UUID и возвращают его в ответе. Сервис передаёт его монолиту, в событие (`correlationId` и заголовок `correlation-id`) и в историю (`correlation_id`); в журналах сервисов это поле `requestId`. События ops-джобы получают идентификатор её запуска.

## Проверки

```bash
cd tasks/task2/booking-service
dotnet test --project tests/BookingService.Tests.csproj   # правила, ACL, контракт события, классификация ошибок доставки
cd .. && ./check-delivery.sh                              # на поднятом стенде: остановка Kafka, повтор, dead letter
./migration/cutover-drill.sh                              # учения по переключению; удаляет тома стенда
```

## Результаты

| Требование задания | Файл |
| :- | :- |
| Лог `docker ps` | [docker-ps.txt](docker-ps.txt) |
| Доработанный `regress.sh` | [regress.sh](regress.sh) |
| `test-log.txt` с тестовыми запросами | [test-log.txt](test-log.txt): регресс без booking-service падает, с ним проходят 29 проверок курса, две точные причины отказа и две проверки истории |
| `select * from bookings` из новой и старой системы | [bookings-old-vs-new.txt](bookings-old-vs-new.txt) |
| README со стратегией миграции данных и To-Be | этот файл |
| Листинг бронирований через REST монолита и gRPC сервиса | [listing-rest-vs-grpc.txt](listing-rest-vs-grpc.txt) |
| `select *` из таблицы истории | [history.txt](history.txt): история, статистика, права аналитика, отставание группы |
| Дополнительно | [cutover-drill-log.txt](cutover-drill-log.txt) — учения по переключению и откату, [delivery-check.log](delivery-check.log) — доставка при остановке Kafka и dead letter, [unit-tests.txt](unit-tests.txt) |
