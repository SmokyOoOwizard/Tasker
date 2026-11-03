# Windows: PowerShell, кодировка, кавычки, Tab

Как пользоваться `tasker` в Windows Terminal, PowerShell 7, Windows PowerShell 5.1 и cmd.exe: кириллица, кавычки в фильтрах, пути, `watch`, автодополнение.

## Что нужно

Windows 10 или 11, `tasker.exe` в PATH, Windows Terminal (лучше) или любая консоль: PowerShell 7, Windows PowerShell 5.1, cmd.exe. Права администратора не нужны.

## Шаги

### Кодировка

- При запуске в окне консоли `tasker` сам переключает кодовую страницу на UTF-8 (и возвращает прежнюю при выходе): кириллица в заголовках и таблицах читается и в cmd.exe, и в PowerShell. Нужен шрифт с кириллицей (Consolas, Cascadia Mono; растровый шрифт старой консоли не подходит).
- Если вывод перенаправлен (`> файл`, `| more`, `| clip`), `tasker` пишет UTF-8 **без BOM**, а ввод читает как UTF-8 (BOM допускается). Аргументы командной строки с кириллицей (`tasker task create "Починить вход"`) и переменные окружения передаются в Юникоде и работают как есть.
- Но **принимает и расшифровывает вывод сама оболочка**. PowerShell читает вывод программ в кодировке `[Console]::OutputEncoding`, а она по умолчанию равна кодовой странице окна (866, 1251): `tasker task list | Select-String Задача` и `$x = tasker task get TSK-1` дадут кракозябры. Поставьте в `$PROFILE` (подробнее ниже): `[Console]::OutputEncoding = [Text.UTF8Encoding]::new()`. Это нужно и для передачи текста в `tasker` по конвейеру (`"описание" | tasker task create …`): ещё и `$OutputEncoding = [Text.UTF8Encoding]::new()`.
- Оператор `>` в Windows PowerShell 5.1 сам пишет файл в UTF-16: `tasker task list > tasks.txt` получится в UTF-16 с BOM. Используйте PowerShell 7 (там `>` пишет UTF-8 без BOM), либо `tasker task list | Out-File -Encoding utf8 tasks.txt` (в 5.1 с BOM), либо `cmd /c "tasker task list > tasks.txt"` — оператор cmd.exe передаёт байты программы как есть (UTF-8 без BOM).

### Кавычки в фильтрах

Знаки `<`, `>`, `|`, `&`, `;`, `$`, обратный апостроф и пробелы оболочки понимают по-разному. Условие `--field` — одна строка, поэтому её берут в кавычки:

```text
# zsh, bash:  двойные кавычки; '!' лучше в одинарных (история команд)
tasker task list --field "Estimate>=3" --field 'Priority!=Low'

# PowerShell: одинарные кавычки везде (внутри них $ и обратный апостроф — обычные знаки)
tasker task list --field 'Estimate>=3' --field 'Priority!=Low' --field 'Cost<$100'

# cmd.exe: одинарных кавычек нет, только двойные; знаки &, |, <, > внутри них безопасны
tasker task list --field "Estimate>=3" --field "Priority!=Low"
```

- `Имя:set`, `Имя:unset`, `Имя:attached` кавычек не требуют ни в одной оболочке.
- PowerShell: без кавычек `>` — перенаправление в файл с именем `=3` (`tasker task list --field Estimate>=3` молча запишет вывод в файл `=3`), `;` — конец команды, `&` — зарезервирован, `$имя` — переменная. В двойных кавычках PowerShell ещё раскрывает `$` и понимает обратный апостроф: берите одинарные.
- Одинарная кавычка внутри значения в PowerShell удваивается: `--field 'Title=It''s'`. Пробелы в значениях безопасны в любых кавычках: `--field 'Component=Front end'`.
- Двойная кавычка внутри значения в Windows PowerShell 5.1 до программы доходит без экранирования; если она нужна, используйте PowerShell 7.3+.
- Режим `--%` (остановка разбора) не нужен: кавычек достаточно. Если используете, помните, что дальше `%ПЕРЕМЕННАЯ%` раскрывается как в cmd.exe.
- Названия проектов, статусов и досок с пробелами и кириллицей — в кавычках, как и в других оболочках: `tasker task list --status 'В работе'`.

### Пути

- `-w`, `--sqlite`, `mcp workspace add|remove` принимают `C:\work\project`, `D:/work/project`, пути с пробелами и кириллицей (в кавычках) и `~`: PowerShell и cmd.exe её сами не раскрывают, `tasker` раскрывает сам (`~`, `~\work`, `~/work` — в папке профиля пользователя).
- Каталог данных Tasker (настройки, состояние демона, журналы) — `%LOCALAPPDATA%\Tasker`; `TASKER_HOME` переопределяет его: `$env:TASKER_HOME = 'D:\tasker-data'` (cmd.exe: `set TASKER_HOME=D:\tasker-data`).
- Имя в блокировках (`tasker whoami`) — имя пользователя Windows без домена: `CORP\ivan` и `ivan@corp.local` превращаются в `ivan`. Сменить: `tasker whoami "Иван Петров"`.

### Ширина окна и `watch`

- Строки списков обрезаются по ширине окна (Windows Terminal и conhost); в классической консоли последний столбец остаётся свободным, чтобы строка на всю ширину не давала пустую строку после себя. Широкие знаки (эмодзи, китайские иероглифы) выравниваются по двум ячейкам; в старой консоли (conhost) шрифт может рисовать их иначе, и колонки поедут — Windows Terminal это делает верно.
- Программы `watch` в Windows нет. Аналог в PowerShell (в терминале ширина определяется сама, `--truncate` не нужен; остановка — Ctrl+C):

```text
while ($true) { Clear-Host; tasker task list --status 'В работе'; Start-Sleep 10 }
```

В cmd.exe: `for /l %i in (0,0,1) do @(cls & tasker task list & timeout /t 10 /nobreak >nul)`. Если вывод идёт не в терминал, ширину задаёт `--width N` или `$env:TASKER_WIDTH = 100`.

### Автодополнение по Tab (PowerShell)

`tasker completion pwsh` печатает скрипт (`Register-ArgumentCompleter -Native`): он работает в PowerShell 7 и Windows PowerShell 5.1, подсказывает команды, параметры и значения (проекты, статусы, задачи…) рабочей области текущей папки, названия с пробелами и кириллицей берёт в кавычки. **cmd.exe автодополнение не поддерживает** (в нём нельзя программировать Tab); пользуйтесь PowerShell.

```text
tasker completion pwsh --install      # скрипт и помеченный блок в профиль (PowerShell 7 и 5.1), резервная копия
tasker completion pwsh --uninstall    # убрать блок и скрипт
tasker completion pwsh | Out-String | Invoke-Expression   # только в этом окне, без установки
```

`--install` сохраняет скрипт в `%LOCALAPPDATA%\Tasker\completions\tasker.ps1` (`--script ПУТЬ` — в другое место) и добавляет в профили один блок `# >>> tasker completion >>>` … `# <<< tasker completion <<<`, который подключает этот файл. Повторная установка заменяет блок, а не дублирует; перед первой правкой профиль копируется в `<профиль>.tasker-backup`; профиль не в UTF-8 не трогается. `--profile ФАЙЛ` — свой профиль вместо стандартных. Если запуск скриптов запрещён (`Get-ExecutionPolicy`), разрешите их для пользователя: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

## Пример

Фильтр в PowerShell (одинарные кавычки), подключение Tab, имя в блокировках:

```bash
tasker task list --field 'Estimate>=3' --field 'Priority!=Low'
tasker completion pwsh --install
tasker whoami
```

Убрать автодополнение: `tasker completion pwsh --uninstall`.

## На что обратить внимание

- Демон MCP и автозапуск в Windows настраиваются отдельно (`tasker manual install-daemon`).
- Git Bash и WSL — обычные bash: действуют правила zsh/bash, автодополнение — `tasker completion bash`.
