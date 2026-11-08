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

Строка запроса `--query` у `board tasks` работает как у `task list` (`tasker manual tasks`): складывается с условиями колонки и `--field` через И, а `| sort` в ней главнее `--sort`:

```bash
tasker board tasks Main Doing --query '.Field.Estimate > 3 | sort -.Updated'
```

Переместить задачу между колонками — значит поменять её статус на статус нужной колонки:

```bash
tasker task update TSK-1 --status Done
```

Колонка может хранить и **запрос** — задачи попадают в неё сами, по статусу и запросу вместе. Запрос пишут на языке строки запроса (как над списком задач в приложении), перед ним — название колонки и двоеточие:

```bash
tasker board create Team --status-set Basic --column "API=In progress" --column "Other=In progress" --column "Done=Done" --column-query 'API:.Field.Component == "API"' --column-query 'Other:.Field.Component != "API"'
tasker board update Team --column-query 'API:.Field.Component == "API" && .Field.Estimate >= 3 | sort -.Field.Estimate'
tasker board update Team --column-query "API:"
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
- Доску и колонку указывают по названию или id. `--json` у `board show` даёт `{board, columns:[{id, name, query, error, totalCount, tasks}]}`.
- `--description-length N` в `--json` усекает описания задач (`0` — без описания, `-1` — полные, по умолчанию), как у `task list`.
- Колонка = статусы **и** запрос: задача в колонке, если подходит статус и она подходит под запрос. У колонки один запрос, `--column-query` для одной колонки дважды — ошибка. Правка задачи переносит её в другую колонку без переноса.
- Сортировка из запроса (`| sort`) задаёт порядок задач колонки; `--sort` у `board show` и `board tasks` её заменяет. Без сортировки порядок прежний.
- Поля в запросе названы по имени: после удаления или переименования поля запрос колонки перестаёт разбираться, и колонка показывает ошибку (`board show` пишет `error: …` под колонкой), пока запрос не поправят.
- Один статус можно дать нескольким колонкам, только если их запросы не могут выполняться вместе (`.Field.Component == "API"` и `.Field.Component != "API"`, `.Field.Estimate >= 3` и `.Field.Estimate < 3`, `IsSet()` и `IsUnset()`); иначе задача попала бы в обе — доска не принимается.
- `board update` только с `--column-query` меняет запросы названных колонок и оставляет остальные; `--column-query "Колонка:"` убирает запрос. С новым `--column` колонка с прежним названием сохраняет свой запрос.
- `board show` пишет запрос в заголовке колонки (`API [.Field.Component == "API"] (2)`), `board get` — построчно. Перенос задачи (`move_task`) в колонку, под запрос которой она после смены статуса не подходит, отклоняется: сначала поправьте задачу.
- Прежние условия колонок по полям больше не применяются и в запросы не переводятся.
- Задачи, чей статус не входит ни в одну колонку, на доске не видны, зато видны в `tasker task list`.
