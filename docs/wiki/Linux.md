# Linux

Тесты и демон проверены на Linux (Ubuntu 24.04, arm64) в контейнере Docker с macOS, ничего не устанавливая на хост (TSK-111). Нужен только `docker`.

```bash
scripts/test-linux.sh                                                  # весь набор тестов (~5 минут), журнал — test-linux.log
scripts/test-linux.sh --filter "FullyQualifiedName~DaemonUpgradeTests"  # часть тестов (фильтр `dotnet test`)
scripts/test-linux.sh --stress "--scenarios upgrade"                    # стенд tools/Tasker.Stress вместо тестов (замена демона на лету, параллельная работа)
scripts/test-linux.sh --platform linux/amd64                            # другая архитектура (на Apple Silicon образ amd64 может не запуститься: exec format error)
scripts/test-linux.sh --shell                                           # оболочка в той же среде (исходники в /work)
scripts/test-linux-systemd.sh                                           # настоящий systemd --user: автозапуск, замена на лету, перезапуск, остановка
```

- **Среда.** Образ `tasker-linux-test` собирается один раз из `mcr.microsoft.com/dotnet/sdk:10.0` (+ `git`, `python3`, `procps`, `pwsh`), репозиторий монтируется только для чтения и копируется в контейнер без `bin`, `obj` и `node_modules`: каталоги хоста не загрязняются. Кэш NuGet — именованный том `tasker-nuget-user`. Прогон идёт **от обычного пользователя и с `--init`**: root игнорирует права доступа к файлам (тест отката замены на лету снимает права с `.tasker`), а без init-процесса умершие потомки демона остаются зомби и проверки «процесс завершился» не проходят. Контейнер после прогона удаляется; удалить том и образы: `docker volume rm tasker-nuget-user`, `docker rmi tasker-linux-test`.
- **Платформенные тесты.** Тесты, которым нужна своя система, помечены `[PlatformFact(TestPlatform.Windows|MacOS|Linux|Unix)]` (`PlatformFactAttribute`) и на других системах явно пропускаются (`Skipped` с причиной), а не молча проходят; на Linux пропускаются только тесты Windows. `launchctl` и `systemctl` в обычных тестах подменены (`IProcessRunner`), поэтому настоящую службу они не трогают.
- **Найденные и исправленные проблемы Linux.** (1) `dotnet tasker.dll` из каталога тестов или стенда падал с `FileNotFoundException: Microsoft.Extensions.Logging.Abstractions`: у тестов и стенда подключён общий каркас ASP.NET Core, и если его сборка не старее пакета, сборка пакета в выходной каталог не копируется, а консоль (только `Microsoft.NETCore.App`) без неё не работает; на macOS это скрывалось более старой средой. Теперь `tools/CopyConsoleDependencies.targets` докладывает недостающие сборки из выхода `Tasker.Cli`. (2) Свежая учётная запись без `~/.local/share` (и без `TASKER_HOME`): `GetFolderPath(LocalApplicationData)` возвращал пустую строку, данные Tasker искались в `/Tasker` (`Access to the path '/Tasker' is denied`) — теперь `SpecialFolderOption.DoNotVerify` (тест `LinuxHomeTests`).

### MCP-демон на Linux

Работает так же, как на macOS: супервизор держит `mcp/daemon.lock` и слушающий сокет (наследуется рабочими процессами через `fcntl`/Kestrel `ListenHandle`), `tasker mcp upgrade` заменяет рабочий процесс на лету, `SIGTERM` останавливает штатно (журнал `Stopping the MCP server: SIGTERM`), фоновый запуск — `setsid`. Служба — `~/.config/systemd/user/tasker-mcp.service` (`Restart=always`, `RestartSec=5`, `WantedBy=default.target`, `KillMode` по умолчанию — `control-group`): `systemctl stop` посылает `SIGTERM` и супервизору, и рабочим процессам, остановка занимает доли секунды; убитый супервизор поднимается заново через 5 секунд (его рабочие процессы уходят вместе с ним). Проверено под настоящим systemd (`scripts/test-linux-systemd.sh`: привилегированный контейнер с systemd в роли PID 1, `loginctl enable-linger`, `systemctl --user`).

### ПРОВЕРИТЬ НА LINUX С SYSTEMD

Скрипт проверяет всё, что можно в контейнере. Остаётся проверить на настоящей машине (или виртуальной машине) с systemd, где есть загрузка системы и вход пользователя:

1. Автозапуск после перезагрузки: `tasker mcp autostart enable`, перезагрузка **без** входа в систему при `loginctl enable-linger $USER` — `tasker mcp status` показывает демон; без linger — демон стартует при первом входе.
2. `tasker mcp autostart enable` по SSH или из `su -`/`sudo -u` (нет сеанса пользователя: `Failed to connect to bus` — команда печатает причину с подсказкой и не оставляет unit-файл; в контейнере это проверено, на настоящем `sudo -u` — нет).
3. Дистрибутивы без systemd (Alpine/OpenRC, WSL1): `tasker mcp autostart enable` сообщает, что автозапуск не поддержан; `tasker mcp start` работает.
4. Архитектура x86_64 (`linux-amd64`): `scripts/test-linux.sh --platform linux/amd64` на Apple Silicon не работает (Docker Desktop: `exec format error`, эмуляции нет) — нужен настоящий x86_64 (задача TSK-122).
