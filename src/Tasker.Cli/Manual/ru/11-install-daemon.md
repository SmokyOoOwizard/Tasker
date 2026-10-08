# Установка и MCP-демон

Поставить `tasker` на машину и запустить фоновый демон MCP, чтобы агенты работали с папками без десктопа.

## Что нужно

Готовый релиз Tasker под вашу систему: macOS (Apple Silicon, Intel), Linux (x64, arm64) или Windows 10 1809+ (x64, arm64) — архив `tasker-<версия>-<платформа>.tar.gz` (Windows — `.zip`). .NET ставить не нужно (внутри самодостаточная сборка), права администратора не нужны.

## Шаги

Установка из распакованного релиза (в `~/.local`, на Windows — в `%LOCALAPPDATA%\Programs\Tasker`): ставит `tasker` и демон `tasker-mcpd`, подключает автодополнение по Tab и включает автозапуск демона (launchd, systemd `--user` или Планировщик заданий).

```bash
tar -xzf tasker-0.1.0-linux-x64.tar.gz
tasker-0.1.0-linux-x64/install.sh --add-to-path
```

```powershell
Expand-Archive tasker-0.1.0-win-x64.zip .
.\tasker-0.1.0-win-x64\install.ps1
```

Другие варианты: `install.sh --from ФАЙЛ.tar.gz|КАТАЛОГ`, `--url АДРЕС` (проверяется `.sha256` рядом), `--prefix КАТАЛОГ`, `--no-autostart`, `--no-completion`, `--restart`, `--from-source` (сборка из исходников для разработчиков, нужен .NET SDK 10). У `install.ps1`: `-From`, `-Url`, `-Prefix`, `-NoPath`, `-NoCompletion`, `-NoAutostart`, `-DryRun` (показать, что будет сделано). Обновление — тот же скрипт нового релиза ещё раз; удаление — `install.sh --uninstall` / `install.ps1 -Uninstall` (данные Tasker остаются).

Демон: разрешить рабочие области, запустить, проверить.

```bash
tasker mcp workspace add ~/work/project-a
tasker mcp workspace add ~/work/project-b
tasker mcp start
tasker mcp status
```

Запускать при входе в систему и поднимать при падении:

```bash
tasker mcp autostart enable
tasker mcp autostart status
```

Остановить, перезапустить (жёстко), заменить на новую сборку без простоя, сменить порт:

```bash
tasker mcp stop
tasker mcp restart
tasker mcp upgrade
tasker mcp port 5719
tasker mcp config
```

## Пример

```text
$ tasker mcp status
```

Показывает, работает ли демон, порт, области и автозапуск; код выхода 3, если демон не работает. Подключение агента — `tasker manual agent`.

## На что обратить внимание

- Демон — отдельная программа `tasker-mcpd` рядом с `tasker`; релиз всегда содержит обе (только `install.sh --from-source --no-daemon` ставит одну консоль, и тогда `tasker mcp start` не заработает).
- После обновления `tasker` установщик (macOS, Linux) сам переводит работавший демон на новую сборку без простоя (`tasker mcp upgrade`): новый процесс поднимается рядом, принимает вызовы, когда открыл области, а старый заканчивает начатое; порт не закрывается, вызовы не отказывают. Не получилось — старый продолжает работать, перезапуска молча нет: установщик печатает причину и подсказку `tasker mcp upgrade --restart`. На Windows установщик останавливает демон перед заменой файлов и запускает снова. Руками: `tasker mcp upgrade` (`--restart` — жёсткий перезапуск, вызовы в работе обрываются; демон, запущенный по-старому, upgrade сам перезапускает). `tasker mcp status` на время замены показывает оба процесса.
- Области из `mcp workspace add` открываются и закрываются на лету, перезапуск не нужен; порт применяется после `restart`.
- Порт занят (десктопом или другой программой) — смените его командой `tasker mcp port <номер>` и перезапустите демон.
- `tasker mcp run` запускает демон в этом терминале до Ctrl+C: удобно для отладки.
- Автозапуск на macOS — агент launchd, на Linux — служба systemd пользователя, на Windows — задача Планировщика заданий (ниже).

## Windows

Автозапуск — задача Планировщика заданий «Tasker MCP» для текущего пользователя (права администратора не нужны). Она запускает `tasker-mcpd` при входе без окна консоли, перезапускает его при падении (раз в минуту) и держит одну копию.

```powershell
tasker mcp autostart enable
tasker mcp autostart status
Get-ScheduledTask -TaskName "Tasker MCP"
tasker mcp autostart disable
```

`tasker mcp start`, `stop` и `restart` при включённом автозапуске идут через Планировщик (`schtasks /run`, `/end`). Описание задачи лежит в `%LOCALAPPDATA%\Tasker\service\Tasker MCP.xml`. Нужна Windows 10 1809 или новее.
