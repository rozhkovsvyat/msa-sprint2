# Hotelio: переход от монолита к микросервисам — проектная работа спринта 2

Форк [Yandex-Practicum/msa-sprint2](https://github.com/Yandex-Practicum/msa-sprint2). Автор — Святослав Рожков.

| Задание | Результаты | Коротко |
| :- | :- | :- |
| 1. Миграция от монолита к микросервисам | [tasks/task1/results](tasks/task1/results) | [ADR-001](tasks/task1/results/ADR-001-strangler-booking.md): проблемы монолита, в том числе найденные в коде и на стенде, выбор первого модуля, план Strangler Fig, To-Be C1/C2, переключение и откат |
| 2. Новые сервисы | [tasks/task2/results](tasks/task2/results/README.md) | booking-service и booking-history-service на C#/.NET 10: gRPC, Anti-Corruption Layer к монолиту, outbox → Kafka, ops-джоба миграции данных, учения по переключению |
| 3. Личный кабинет | [tasks/task3/results](tasks/task3/results/report.md) | Apollo Federation 2: подграфы бронирований, отелей и промокодов, ACL, DataLoader, `@override` |
| 4. Развёртывание и тестирование | [tasks/task4/results](tasks/task4/results/report.md) | Docker, Helm-чарт со средами staging/prod, пайплайн build → test → deploy → tag (gitlab-ci-local), Minikube, DNS |
| 5. Управление трафиком с Istio | [tasks/task5/results](tasks/task5/results/report.md) | Канарейка 90/10, fallback v1 → v2, повторы, circuit breaking, фича-флаг через EnvoyFilter |

Изменения в общих файлах курса:
- `tasks/monolith/Dockerfile` и `test/Dockerfile` — актуальные базовые образы, исходные больше не собираются; в образ тестов добавлен `grpcurl`;
- `test/init-fixtures.sql` — два отеля для проверки причин отказа;
- `test/regress.sh` — регресс после выноса бронирований (задание 2).

Код монолита не менялся.

Вклад ИИ раскрыт в [AI-LOG.md](AI-LOG.md).
