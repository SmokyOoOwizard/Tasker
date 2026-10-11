# Просмотр доски и колонок

Увидеть задачи по колонкам: всю доску сразу или одну колонку.

## Что нужно

Доска (`tasker manual first-project`) и задачи в статусах, которые входят в её колонки.

## Шаги

```bash
tasker board list
tasker board show Main
tasker board tasks Main Doing
```

Ограничить число задач и сузить показ по полю:

```bash
tasker board show Main --limit 10
tasker board show Main --all
tasker board show Main --field Priority=High
tasker board tasks Main Doing --field Priority=High --all
tasker board show Main --json --description-length 0
```

Порядок задач внутри колонок — `--sort` (ключи как у `task list`, см. `tasker manual tasks`):

```bash
tasker board show Main --sort -updated
tasker board tasks Main Doing --sort "Priority,title"
```

Переместить задачу между колонками — значит поменять её статус на статус нужной колонки:

```bash
tasker task update TSK-1 --status Done
```

Колонка может хранить и **условия по полям** — задачи попадают в неё сами, по статусу и полям вместе. Условие пишут как у `task list --field`, перед ним — название колонки и двоеточие:

```bash
tasker board create Team --status-set Basic --column "API=In progress" --column "Other=In progress" --column "Done=Done" --column-filter "API:Component=API" --column-filter "Other:Component!=API"
tasker board update Team --column-filter "API:Component=API" --column-filter "API:Estimate>=3"
tasker board update Team --column-filter "API:"
tasker board get Team
```

## Пример

```text
To do (1)
  TSK-2  Todo         Task  Второе

Doing (1)
  TSK-1  In progress  Task  Первая

Done (0)
  (no tasks)
```

## На что обратить внимание

- Название колонки в заголовке показано с общим числом задач; по умолчанию под колонкой до 50 задач, остаток — строка `... and N more`.
- `--field` с условием (`Имя=значение`, `"Имя>=3"`, `Имя:set` и другие, как у `task list`) — условие просмотра, колонка его не хранит; `tasker manual fields` рассказывает о полях.
- Доску и колонку указывают по названию или id. `--json` у `board show` даёт `{board, columns:[{id, name, totalCount, tasks}]}`.
- `--description-length N` в `--json` усекает описания задач (`0` — без описания, `-1` — полные, по умолчанию), как у `task list`.
- Колонка = статусы **и** условия по полям: задача в колонке, если подходит статус и выполнены все условия (повтор `--column-filter` — «и»). Правка поля задачи переносит её в другую колонку без переноса. Поле и значение enum хранятся по id, переименование колонку не ломает; поле, на которое есть условие, не удаляется и не меняет тип.
- Один статус можно дать нескольким колонкам, только если их условия не могут выполняться вместе (`Component=API` и `Component!=API`, `Estimate>=3` и `Estimate<3`, `:set` и `:unset`); иначе задача попала бы в обе — доска не принимается.
- `board update` только с `--column-filter` меняет условия названных колонок и оставляет остальные; `--column-filter "Колонка:"` убирает условия. С новым `--column` колонка с прежним названием сохраняет свои условия.
- `board show` пишет условия в заголовке колонки (`API [Component=API] (2)`), `board get` — построчно. Перенос задачи (`move_task`) в колонку, под условия которой она не подходит, отклоняется: сначала поправьте её поля.
- Задачи, чей статус не входит ни в одну колонку, на доске не видны, зато видны в `tasker task list`.
