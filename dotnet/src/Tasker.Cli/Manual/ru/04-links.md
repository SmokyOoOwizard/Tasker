# Связи между задачами

Одна задача блокирует, дублирует или клонирует другую, как связи в Jira.

## Что нужно

Две задачи одного проекта; удобнее всего, если у них есть ссылки `ПРЕФИКС-номер` (`tasker manual series`).

## Шаги

```bash
tasker link-type list
tasker task link TSK-1 blocks TSK-2
tasker task links TSK-1
tasker task unlink TSK-1 blocks TSK-2
```

Своя связь с двумя названиями сторон:

```bash
tasker link-type create "Depends on" --outward "depends on" --inward "is a dependency of"
tasker task link TSK-3 "depends on" TSK-1
```

Запретить или разрешить циклы у типа (A blocks B и B blocks A):

```bash
tasker link-type create Needs --outward needs --inward "is needed by" --allow-cycles false
tasker link-type update Blocks --allow-cycles true
```

### Эпик и подзадачи

Эпик — не особый тип задачи, а любая задача, у которой есть дочерние. Дочерние задачи — это связи **иерархического** типа; в проекте он есть сразу, это **Parent/Child** (`includes` / `is part of`: родитель *включает* потомка). Заведите эпик и подзадачи:

```bash
tasker task create "Новый вход" --type Task
tasker task create "Форма входа" --type Task --parent TSK-1
tasker task create "Сброс пароля" --type Task --parent TSK-1 --parent TSK-9
tasker task update TSK-5 --add-parent TSK-1 --remove-parent TSK-9
tasker task link TSK-1 includes TSK-6
tasker task list
tasker task list --flat
```

`--parent`, `--add-parent` и `--remove-parent` — короткая запись для `task link`/`task unlink` с иерархическим типом проекта (если таких типов несколько, укажите `--parent-type <имя>`). `task list` показывает дочерние задачи сразу под родителем с отступом, `--flat` возвращает плоский список. Свой иерархический тип (например, «входит в» / «включает» из другого трекера):

```bash
tasker link-type create "Epic link" --outward "has story" --inward "belongs to epic" --hierarchical true
tasker link-type update "Epic link" --hierarchical false
```

## Пример

```text
$ tasker task link TSK-1 blocks TSK-2
Linked: TSK-1 blocks TSK-2

$ tasker task links TSK-2
Found 2
is blocked by  TSK-1   In progress  Первая задача
is blocked by  TSK-10  Todo         Десятая задача
```

Дерево в `task list`: TSK-2 и TSK-4 входят в эпик TSK-1, у TSK-2 есть свой потомок TSK-3, а TSK-4 входит ещё и в эпик TSK-5 (повтор помечен `(+)`):

```text
$ tasker task list
Found 5
TSK-1          Todo  Task  Новый вход
    TSK-2      Todo  Task  Форма входа
        TSK-3  Todo  Task  Проверка пароля
    TSK-4      Todo  Task  Сброс пароля
TSK-5          Todo  Task  Другой эпик
    TSK-4 (+)  Todo  Task  Сброс пароля

$ tasker task list --limit 1
Found 5, shown top-level 1-1 of 2 (use --offset/--limit)
TSK-1          Todo  Task  Новый вход
    TSK-2      Todo  Task  Форма входа
        TSK-3  Todo  Task  Проверка пароля
    TSK-4      Todo  Task  Сброс пароля
```

Связь, замыкающая цикл, отклоняется с путём цикла:

```text
$ tasker task link TSK-2 blocks TSK-1
Error: Cycle: TSK-2 → TSK-1 → TSK-2 (link type 'Blocks' does not allow cycles; remove the opposite link or allow cycles for the type)
```

Колонки (сторона связи, ссылка, статус, заголовок) выровнены по самой широкой ячейке и разделены двумя пробелами; то же в блоке `links` у `task get`.

## На что обратить внимание

- Связь «A blocks B» и «B is blocked by A» — одна и та же: можно указать любую сторону.
- В проекте по умолчанию шесть типов: Blocks, Duplicate, Cloners, Relates, Problem/Incident и иерархический Parent/Child. Их можно переименовать и удалить, можно добавить свои. Parent/Child появляется и в проектах, созданных раньше, без миграции данных: пока в проекте нет иерархического типа и типа с таким названием, он виден и записывается при первой записи типов или связей. Чтобы его не было совсем, заведите свой иерархический тип и удалите Parent/Child.
- Тип связи можно удалить, только если по нему нет связей; удаляя задачу, Tasker убирает связи с ней.
- `task get` показывает связи с обеих сторон (блок `links:`), а `task get --json` — в полях `linkViews` и `linkCount`; поле `links` в JSON — только сырые исходящие связи (id типа и цели).
- `task unlink` для несуществующей связи не ошибка.
- Связи на удалённые в другой ветке задачи убирает `tasker cleanup`.
- Циклы: у типа есть признак «допускает циклы» (`--allow-cycles true|false`). У **Blocks** он выключен, у остальных типов по умолчанию включён, у своего типа вы выбираете сами (по умолчанию включён). Если признак выключен, отклоняется любая связь, замыкающая цикл любой длины (A blocks B, B blocks C, C blocks A). Связи «relates to» (без направления) циклов не образуют; связь задачи с самой собой не принимается никогда. Признак не трогает связи, которые уже есть.
- Если две ветки git добавили встречные связи, после слияния цикл всё же появится: `tasker sync`, `tasker cleanup --check` и демон сообщат `Link cycle: TSK-1 → TSK-2 → TSK-1`. `tasker cleanup` такие связи **не удаляет**: снимите одну командой `tasker task unlink TSK-2 blocks TSK-1`.
- Иерархия. Признак «иерархический» (`--hierarchical true`) у типа связи: источник связи — родитель, цель — потомок. У иерархического типа циклы запрещены всегда (`--allow-cycles true` вместе с ним — ошибка), цикл не может пройти и по связям разных иерархических типов, названия сторон должны различаться. У задачи **может быть несколько родителей** (одна задача входит в несколько эпиков), поэтому иерархия — граф без циклов, а не дерево. Эпик = задача, у которой есть исходящие иерархические связи.
- `task list` по умолчанию всегда показывает иерархию: под задачей идут её дочерние (отступ в первой колонке — четыре пробела на уровень, колонки остаются выровнены, отступ входит в ширину при обрезке). Фильтры (`--status`, `--type`, `--series`, `--field`) действуют на каждую задачу отдельно: эпик, не прошедший фильтр, не показывается, а подошедшая дочерняя идёт на **верхнем уровне** без отступа; при нескольких родителях — под каждым подошедшим, на верхнем уровне — только если не подошёл ни один. Порядок `--sort` применяется и к верхнему уровню, и к дочерним внутри каждого родителя.
- Задача с несколькими родителями показывается под каждым вместе со своими потомками; повторные строки помечены `(+)` после ссылки. Глубина показа — 10 уровней, а на тяжёлых графах (общие поддеревья под многими эпиками) после 5000 строк на странице повторы остаются одной строкой без потомков. Строка `Found N` считает **уникальные задачи** (не показанные строки); `--offset`/`--limit` считают задачи верхнего уровня (без родителя в результате) и показывают их поддеревья целиком: `Found 106, shown top-level 1-20 of 57 (use --offset/--limit)`. Если вложенности в результате нет, строка прежняя (`Found 106, shown 1-20 (use --offset/--limit)`).
- `--flat`, `--json` и `board show`/`board tasks` дерево не строят. В `--json` (и в REST, и в MCP `list_tasks`/`find_tasks_by_reference`) список остаётся плоским, в записи добавлены `parentIds` (id родителей) и `childCount` (число дочерних; больше 0 — эпик). REST: `GET /tasks?flat=false` отдаёт строки деревом (у записей `depth` и `repeated`, `topLevelCount`); без параметра список плоский.
- `task get` показывает родителей и потомков блоком `links:` (`is part of` у потомка, `includes` у родителя) — связи видны с обеих сторон.
