# Tasker

Трекер задач, который живёт рядом с кодом. Проекты, задачи, статусы, доски, серии и связи хранятся обычными файлами в папке `.tasker` вашего репозитория (их версионирует git) или в базе данных. Работать можно из консоли, из десктопного и веб-интерфейса, а ИИ-агенты подключаются через MCP.

## Что умеет

- **Задачи и доски:** проекты, статусы и их наборы, типы задач, доски с колонками, серии со сквозными номерами (`TSK-12`).
- **Связи и иерархия:** блокировки, дубликаты, «входит в»; эпик показывается вместе с вложенными задачами.
- **Свои поля:** строки, числа, даты, флаги, перечисления, обязательные поля, несколько значений.
- **Поиск:** фильтры по статусам, типам, сериям и полям, сортировка, постраничный вывод.
- **Агенты:** MCP-сервер и фоновый демон с автозапуском, который обновляется без простоя.
- **Совместная работа:** блокировка на время правки, проверка версии, разбор конфликтов слияния git.
- **Удобная консоль:** автодополнение по Tab (zsh, bash, PowerShell), встроенный справочник `tasker manual`.

## Платформы

macOS и Linux проверены. Windows поддерживается, но на самой Windows пока не проверялся: см. [страницу Windows](https://github.com/SmokyOoOwizard/Tasker/wiki/Платформы).

## Установка

Готовых релизов на GitHub пока нет: релиз собирается скриптом (для сборки нужен .NET SDK 10). Сама установка ставит консоль и демон одним скриптом, права администратора и установленный .NET не нужны (сборка самодостаточная).

```bash
scripts/release.sh --rid osx-arm64        # релиз для своей платформы (rid: osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64)
artifacts/release/<версия>/install.sh --from artifacts/release/<версия>/tasker-<версия>-osx-arm64.tar.gz
```

Из исходников для разработки (нужен .NET SDK 10): `scripts/install.sh --from-source`. На Windows: `scripts/install.ps1`. Подробности и удаление: [Установка](https://github.com/SmokyOoOwizard/Tasker/wiki/Установка).

## Быстрый старт

```bash
tasker project create Demo
tasker status create "Бэклог" && tasker status create "В работе" && tasker status create "Готово"
tasker status-set create Поток --status "Бэклог" "В работе" "Готово"
tasker task-type create Задача --status-set Поток
tasker series create Задачи --prefix TSK
tasker task create "Первая задача" --type Задача --series TSK
tasker task list
```

Данные появятся в `./.tasker`. Дальше: доски, эпики, поля, фильтры — в [быстром старте](https://github.com/SmokyOoOwizard/Tasker/wiki/Быстрый-старт) и в `tasker manual`.

## Подключение агента

```bash
tasker mcp autostart enable
claude mcp add --transport http tasker http://127.0.0.1:5719/mcp
```

## Документация

Полное описание — в [вики проекта](https://github.com/SmokyOoOwizard/Tasker/wiki): команды консоли, формат файлов, MCP и демон, поля и связи, REST API, настройки, устройство и сборка.

Страницы вики лежат также в `docs/wiki` и публикуются скриптом `scripts/publish-wiki.sh`.
