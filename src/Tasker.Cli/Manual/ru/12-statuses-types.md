# Статусы и типы задач с описанием

Объяснить людям и агентам, что значит статус и когда использовать тип задачи: у статуса и у типа есть необязательное описание.

## Что нужно

Проект (`tasker manual first-project`). Описание — свободный текст в любой кодировке Unicode, с переводами строк; длина не ограничена, как у описания задачи. Нет описания — пустая строка. Описание есть только у статусов и типов задач.

## Шаги

Описание задают при создании (`-d`) и меняют при правке; пустое `-d ""` очищает:

```bash
tasker status create "In progress" -d "Задачу взял исполнитель, код пишется. Закрывается в Done после слияния."
tasker status-set create Basic --status Todo "In progress" Done
tasker task-type create Bug --status-set Basic -d "Дефект в уже выпущенном поведении."
tasker task-type update Bug -d "Дефект в выпущенном поведении: воспроизведение обязательно."
tasker status update "In progress" -d ""
```

Прочитать описание:

```bash
tasker status get "In progress"
tasker task-type get Bug
tasker status list --json --description-length 100
```

## Пример

```text
$ tasker task-type get Bug
id:           4d3f5a1e-9b2c-4e7a-8c6d-1f0e2a3b4c5d
name:         Bug
status set:   Basic (7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d)
version:      c0ffee1234567890

Дефект в выпущенном поведении: воспроизведение обязательно.
```

## На что обратить внимание

- `status get` и `task-type get` показывают описание отдельным абзацем после полей. В текстовых списках (`status list`, `task-type list`) описаний нет: у типов они бывают длинными.
- В `--json` списков описание полное. `--description-length N` оставляет первые N символов (`0` — без описания, `-1` — полный текст, по умолчанию): в записи тогда `descriptionTruncated` и `descriptionLength` (полная длина). Так же работают `descriptionLength` у MCP `list_statuses`/`list_task_types` (по умолчанию 200) и `?descriptionLength=` у REST.
- Агенту описания доступны через MCP: `list_statuses` и `list_task_types` отдают их, `create_*` и `update_*` принимают `description`. Так правила проекта («когда Баг, а когда R&D») лежат рядом с данными, а не в README.
- В интерфейсе описание — поле в формах статуса и типа; оно же — подсказка при наведении на статус.
- Файлы хранилища с описанием записываются форматом 8: более старый Tasker такие файлы покажет как «более нового формата». Старые файлы без описания читаются как есть.
