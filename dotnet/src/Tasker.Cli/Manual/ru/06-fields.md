# Поля задач и перечисления

Свои поля у задач: строки, числа, даты, флаги и списки значений (перечисления); значения в задаче и фильтр по ним.

## Что нужно

Проект с типом задачи. Поле сначала заводят в каталоге проекта, а потом подключают к типу задач или добавляют отдельной задаче.

## Шаги

Каталог: перечисление, поля, подключение к типу задач.

```bash
tasker enum create Priority --value Low High
tasker field create Priority --type enum --enum Priority
tasker field create Estimate --type int
tasker field create Labels --type string --multiple
tasker task-type update Task --add-field Priority:required --add-field Estimate
```

Значения у задач и фильтр:

```bash
tasker task create "Починить вход" --type Task --field Priority=High --field Estimate=3
tasker task update TSK-1 --field Priority=Low --field Labels=ui --field Labels=crash
tasker task list --field Priority=High
tasker task get TSK-1
```

Сравнения, «не равно» и проверка наличия (условия со знаками `<` и `>` пишут в кавычках):

```bash
tasker task list --field "Estimate>=3" --field "Estimate<=8"
tasker task list --field 'Priority!=Low'
tasker task list --field Labels:unset
tasker task list --field Estimate:attached --field Estimate:unset
```

Поле, которое нужно только одной задаче (собственное):

```bash
tasker task update TSK-1 --custom-field Risk:int=5
```

Править перечисления и поля:

```bash
tasker enum update Priority --add-value Urgent
tasker enum update Priority --remove-value Low --replace-with High
tasker field update Estimate --type float
```

## Пример

```text
$ tasker task get TSK-1
...
fields:
  Priority (enum, required): High
  Estimate (int): 3
  Risk (int, own): 5
```

## На что обратить внимание

- Типы значений: `string`, `int`, `float` (с точкой), `bool` (`true`/`false`), `date` (`yyyy-MM-dd`), `enum` (название или id значения).
- Обязательное поле (`:required`) проверяется при создании и правке содержимого задачи, но не при смене статуса и переносе по доске.
- `--field Имя=` без значения убирает значения; у поля с несколькими значениями параметр повторяют, новые значения заменяют прежние.
- Имя поля (и собственного поля задачи) состоит только из букв и цифр (кириллица годится; без пробелов, `_`, `-` и знаков `= ! < > :`).
- Фильтр `--field`: `=`, `!=` (любой тип) и `>`, `>=`, `<`, `<=` (только int, float, date; диапазон — два условия, `">=3"` и `"<=8"`). Несколько условий — «и». У поля с несколькими значениями `=` и сравнения — «хотя бы одно значение подходит», `!=` — «ни одно не равно»; `!=` берёт и задачи без значения (вместе `=` и `!=` дают все задачи).
- `Имя:set` и `Имя:unset` — есть значение или нет; `Имя:attached` и `Имя:detached` — поле подключено к задаче (в её типе или добавлено ей, даже без значения) или нет. «Подключено, но не заполнено» — `Имя:attached` вместе с `Имя:unset`.
- Фильтр ищет и по собственным полям задач (определены в самой задаче). Если поле с таким именем есть в каталоге — значение читается по его типу, а собственные поля с тем же именем и **тем же типом** тоже подходят (с другим типом — нет). Если в каталоге поля нет, а у задач есть собственные поля с этим именем, значение читается по их типу; когда типов несколько (например, int и string) — ошибка со списком типов. `Имя:attached` для собственного поля — у задачи есть такое собственное поле. Тот же фильтр есть у `board show` и `board tasks` — там это условие просмотра, колонка его не хранит; условие, хранящееся в самой колонке доски, задают `--column-filter` у `board create`/`board update` (`tasker manual boards`).
- Поле, на которое есть условие колонки доски, не удаляется и не меняет тип, перечисление и множественность (`In use: …`); значение перечисления из условия колонки удаляется только с выбором: `--drop` снимает условие с колонки, `--replace-with` подставляет другое значение.
- Убираемое значение перечисления, выбранное у задач, и убираемое из типа поле со значениями удаляются только с явным выбором (`--replace-with`/`--drop`, `--drop-values`/`--keep-values`); без него ошибка `In use: …` и ничего не меняется.
- Форма собственного поля: `Имя:тип[:required][:multiple][:enum=Перечисление][=значение]`.
