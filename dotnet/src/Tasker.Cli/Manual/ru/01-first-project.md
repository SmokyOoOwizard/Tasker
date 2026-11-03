# Проект и первая доска

С нуля до работающей доски: проект, статусы, набор статусов, тип задачи и доска с колонками.

## Что нужно

Папка для данных (Tasker хранит их в `<папка>/.tasker`) или файл SQLite. Больше ничего: приложение запускать не нужно. Команды работают с текущей папкой; другую выбирают `-w <папка>` (или `--sqlite <файл>`).

## Шаги

В новом проекте нет ни статусов, ни типов задач, ни досок: задачу нельзя создать без типа, а тип нужен набор статусов.

```bash
tasker project create Demo
tasker status create Todo
tasker status create "In progress"
tasker status create Done --color "#2E9E5B"
tasker status-set create Basic --status Todo "In progress" Done
tasker task-type create Task --status-set Basic
tasker board create Main --status-set Basic --column "To do=Todo" --column "Doing=In progress" --column "Done=Done"
```

Проверить, что получилось:

```bash
tasker status list
tasker board list
tasker board show Main
```

## Пример

```text
Created project 'Demo' f78469c8-5645-443f-9d95-d7728199ab25
Created status 'Todo' e31ae094-03ef-49bb-916b-b146c87cdcef
...
Created board 'Main' d92008eb-ff8e-4352-b91d-26a99ed1148e
```

## На что обратить внимание

- Если в папке один проект, параметр `-p/--project` не нужен. Проектов несколько — указывайте `-p <имя или id>` или задайте переменную `TASKER_PROJECT`.
- Всё, что есть в проекте (статус, набор, тип, доску), указывают по имени или id.
- Колонка доски — это один или несколько статусов: `"Имя=статус,статус"`. Задача, перенесённая в колонку, получает первый её статус, который есть в наборе её типа.
- Дальше: `tasker manual tasks` — как создавать задачи и менять их статусы.
