# Задание 1. Проработка миграции от монолита к микросервисам

- [ADR-001. Поэтапный переход Hotelio на микросервисы: первым выносим бронирование](ADR-001-strangler-booking.md)
- Диаграммы ([`diagrams/`](diagrams), исходники PlantUML рядом с изображениями):
  - [As-Is: компоненты монолита (C3)](diagrams/as-is-components.png)
  - [To-Be через два месяца: контекст (C1)](diagrams/to-be-context.png)
  - [Переходное состояние после переключения: контейнеры (C2)](diagrams/transition-containers.png)
  - [To-Be через два месяца: контейнеры (C2)](diagrams/to-be-containers.png)
  - [Переключение бронирований и откат](diagrams/cutover-sequence.png)
- [test-log.txt](test-log.txt) — запуск монолита, регрессионные тесты (29 проверок, все прошли), проверка API и кода

C4 подключается из стандартной библиотеки PlantUML (`!include <C4/...>`), картинки получаются командой `plantuml -tpng diagrams/*.puml` в этом каталоге.
