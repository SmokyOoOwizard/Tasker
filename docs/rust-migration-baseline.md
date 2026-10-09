# Базовая линия .NET-сборки: скорость вызовов и размер (TSK-126)

Числа «до» для плана `docs/rust-migration-plan.md` (фаза 0, п. 4). После переноса консоли и демона на Rust (фаза 3, TSK-136)
те же команды прогоняются тем же стендом и сравниваются с этим документом. Сырые замеры (JSON стенда) лежат рядом,
в `docs/perf-baseline/`.

## Условия

| | |
|---|---|
| Дата | 2026-10-09 |
| Сборка | `tasker --version` → `0.1.0`, коммит `d030bb9` (main после TSK-125) |
| Публикация | `scripts/perf/publish.sh` — Release, `osx-arm64`, self-contained, ReadyToRun (так же, как установщик) |
| Машина | Apple M2 Max, 12 ядер, 64 ГБ, macOS 26.5.2 (25F84), .NET SDK 10.0.201 |
| Стенд | `scripts/perf/bench.py` (TSK-92): временные `TASKER_HOME` и область, свой демон на свободном порту; `DOTNET_gcServer=0` |

## Размер релизной сборки

Собрано `scripts/release.sh --rid osx-arm64 --keep-work` (релиз только для текущей платформы; `--rid` без `all`, иначе
скрипт собирает все шесть платформ). Размеры — `du -sh`, числа файлов — `find -type f | wc -l`.

| Что | Размер |
|---|---:|
| Папка `app/` (tasker + tasker-mcpd + общая среда выполнения) | **152 МБ** (155 964 КБ), 377 файлов |
| Архив `tasker-0.1.0-osx-arm64.tar.gz` | **56 МБ** (58 633 393 байт) |
| `tasker` (apphost-загрузчик) | 122 КБ |
| `tasker-mcpd` (apphost-загрузчик) | 122 КБ |
| Собственный код (`Tasker.*.dll`) | 5,1 МБ |
| Управляемые сборки, всего | 356 `.dll`, 133 МБ |
| Нативные библиотеки среды выполнения | 14 `.dylib`, 18,6 МБ |

Крупнейшие файлы: `System.Private.CoreLib.dll` 16,3 МБ, `System.Private.Xml.dll` 8,6 МБ, `Microsoft.EntityFrameworkCore.dll` 7,6 МБ,
`Microsoft.EntityFrameworkCore.Relational.dll` 6,0 МБ, `libcoreclr.dylib` 5,9 МБ, `System.Linq.Expressions.dll` 4,4 МБ,
`Npgsql.dll` 4,1 МБ, `ModelContextProtocol.Core.dll` 3,2 МБ, `System.Data.Common.dll` 3,1 МБ, `libclrjit.dylib` 3,0 МБ.

Сборка `publish.sh` (без `DebugType=none` и `SatelliteResourceLanguages=en`) — 153 МБ, 400 файлов; разница с релизом —
pdb (640 КБ) и языковые ресурсы. Ориентир плана для Rust: два статически слинкованных бинарника по 3–8 МБ.

## Время вызова консоли (50 задач в области)

`bench.py calls --n 30`: каждая команда вызывается 30 раз подряд после одного прогревочного вызова; время — от запуска процесса
до его завершения, в миллисекундах. Замеры сделаны в тихое окно (`cpu_busy` 14–20 %, см. «Шум»).

### `--mode daemon` (демон запущен и следит за областью)

| Вызов | медиана, мс | p10 | p90 | n |
|---|---:|---:|---:|---:|
| `version` | 60 | 58 | 61 | 30 |
| `status list` | 152 | 148 | 155 | 30 |
| `task list --limit 1` | 178 | 175 | 181 | 30 |
| `task get` | 202 | 199 | 205 | 30 |
| `task create` | 241 | 238 | 243 | 30 |

### `--mode nodaemon` (демона нет)

| Вызов | медиана, мс | p10 | p90 | n |
|---|---:|---:|---:|---:|
| `version` | 59 | 58 | 61 | 30 |
| `status list` | 152 | 147 | 154 | 30 |
| `task list --limit 1` | 178 | 175 | 181 | 30 |
| `task get` | 202 | 199 | 204 | 30 |
| `task create` | 241 | 238 | 245 | 30 |

Режимы совпадают в пределах погрешности: консоль не ходит в демон, а работающий демон не мешает ей (область маленькая,
слежение за файлами не создаёт нагрузки).

## Разбивка по фазам (`TASKER_PROFILE=1`)

`bench.py calls --phases --n 30`, режим `daemon` (в `nodaemon` цифры те же ±0,5 мс, см. `calls-nodaemon-phases.json`). Медианы
длительности каждой фазы, мс. Фазы — метки `PerfTrace.Mark` в коде: `main` — от старта процесса до входа в `Main` (загрузка
среды выполнения), `env-check` — проверка окружения, `build-root` — построение дерева команд System.CommandLine, `parse`,
`invoke`, `container-register`/`container-build` — Autofac, `index-lock`/`index-schema` — открытие SQLite-индекса области,
`sync-*` — сканирование файлов и досинхронизация индекса, `lifecycle-start`, `action` — сама команда (для `task get` разбита на
`get.project`/`get.find`/`get.describe`), `end` — завершение.

| Фаза | `version` | `status list` | `task list --limit 1` | `task get` | `task create` |
|---|---:|---:|---:|---:|---:|
| main | 29.0 | 29.4 | 29.4 | 29.4 | 29.6 |
| env-check | 3.1 | 3.2 | 3.1 | 3.2 | 3.2 |
| build-root | 13.7 | 4.9 | 4.6 | 4.6 | 4.8 |
| parse | 0.7 | 1.1 | 1.0 | 0.9 | 1.0 |
| invoke | — | 0.8 | 0.8 | 0.8 | 0.8 |
| container-register | — | 21.7 | 21.7 | 21.6 | 21.8 |
| container-build | — | 21.5 | 22.1 | 21.7 | 22.5 |
| index-lock | — | 18.6 | 18.2 | 18.2 | 18.6 |
| index-schema | — | 2.2 | 2.2 | 2.2 | 2.2 |
| sync-indexed | — | 3.5 | 3.7 | 3.6 | 89.7 |
| sync-scan | — | 2.6 | 2.6 | 2.7 | 3.5 |
| sync-commit | — | 0.9 | 1.0 | 0.9 | 20.4 |
| lifecycle-start | — | 1.0 | 0.9 | 1.0 | 1.0 |
| session-open | — | 0.1 | 0.0 | 0.0 | 0.1 |
| action | — | 22.6 | 47.6 | 0.1 | 3.9 |
| get.project | — | — | — | 21.0 | — |
| get.find | — | — | — | 14.2 | — |
| get.describe | — | — | — | 33.7 | — |
| end | 0.6 | 1.2 | 1.2 | 1.2 | 1.2 |
| **итого, медиана с профилированием** | **79** | **169** | **194** | **215** | **258** |

Профилирование само стоит ~15–20 мс на вызов (сравни с таблицами выше). Разница между итогом и суммой фаз (~10–15 мс) —
запуск и завершение процесса вне меток (`fork`/`exec`, загрузка `libcoreclr`, выгрузка).

JIT: число методов, скомпилированных за вызов (счётчик `#jit.methods`), и суммарное время JIT по счётчику среды выполнения:

| Вызов | методов JIT | время JIT, мс |
|---|---:|---:|
| `version` | 80 | 9 |
| `status list` | 510 | 33 |
| `task list --limit 1` | 778 | 46 |
| `task get` | 855 | 52 |
| `task create` | 1224 | 83 |

Несмотря на ReadyToRun, у `task get` ~850 методов всё равно компилируются на лету (generic-инстанцирования, лямбды,
EF Core/LINQ-выражения: `get.project`/`get.find`/`get.describe` дают 230/203/152 методов). У `task create` фаза `sync-indexed`
занимает 90 мс: это индексирование файла задачи, созданного предыдущим вызовом (в стенде создания идут подряд), плюс
`sync-commit` 20 мс — запись в SQLite.

Как складываются ~200 мс `task get`: ~45 мс запуск процесса и среды выполнения (`main` + вне меток), ~8 мс команды и окружение,
~43 мс Autofac, ~21 мс открытие индекса, ~7 мс синхронизация, ~69 мс сама команда (из них ~50 мс JIT), остальное — завершение.

## Зависимость от числа задач

`bench.py degrade --steps 100 500 1000 2000 --n 15`: на каждом шаге область доращивается до нужного числа задач, затем по 15
вызовов каждой команды (медиана, в скобках p90). Столбец «вызовов до» — сколько измеряемых вызовов уже сделано к этому шагу.

### `--mode daemon` (`cpu_busy` 25–32 %)

| Задач | Вызовов до | get, мс (p90) | list --limit 1, мс (p90) | create, мс (p90) |
|---:|---:|---:|---:|---:|
| 100 | 45 | 203 (246) | 180 (190) | 242 (251) |
| 500 | 90 | 230 (359) | 191 (420) | 252 (259) |
| 1000 | 135 | 212 (214) | 184 (188) | 265 (267) |
| 2000 | 180 | 241 (257) | 200 (203) | 282 (289) |

RSS демона после 2000 задач и ~2200 изменений файлов: 36 МБ.

### `--mode nodaemon`

Повтор в тихом окне (`cpu_busy` 16 %):

| Задач | Вызовов до | get, мс (p90) | list --limit 1, мс (p90) | create, мс (p90) |
|---:|---:|---:|---:|---:|
| 100 | 45 | 201 (203) | 177 (181) | 242 (244) |
| 500 | 90 | 207 (210) | 183 (185) | 250 (253) |
| 1000 | 135 | 212 (215) | 188 (192) | 263 (267) |
| 2000 | 180 | 225 (233) | 203 (205) | 283 (289) |

От 100 до 2000 задач вызовы дорожают на 12–17 % (`get` 201→225, `list` 177→203, `create` 242→283 мс в тихом повторе без демона; с демоном 203→241, 180→200, 242→282): индекс в
SQLite работает, время определяется в основном запуском процесса, а не размером области. Шаг 500 с демоном попал под всплеск
нагрузки (p90 359/420 мс при медианах ~230/190) — см. «Шум».

## Шум

Машина в день замера была занята чужими сборками (`dotnet`, виртуальная машина): load average 1-min от 3 до 76. Стенд печатает
строки `[noise]` (load1, занятость CPU по `top` за секунду, число ядер) в начале и в конце каждого прогона:

| Прогон | `[noise start]` | `[noise end]` | использован |
|---|---|---|---|
| calls daemon (1-й) | `load1=8.17 cpu_busy=28%` | `load1=6.70 cpu_busy=18%` | нет (`noisy-calls-daemon-1.json`; медианы на 2–5 % выше повтора) |
| calls daemon --phases (1-й) | `load1=71.77 cpu_busy=93%` | `load1=50.86 cpu_busy=85%` | нет (итоги завышены на 20–30 %) |
| calls nodaemon (1-й) | `load1=17.22 cpu_busy=94%` | `load1=18.61 cpu_busy=71%` | нет (`noisy-calls-nodaemon-1.json`; p90 до +60 %) |
| calls nodaemon --phases (1-й) | `load1=20.55 cpu_busy=31%` | `load1=14.83 cpu_busy=27%` | нет |
| degrade daemon | `load1=3.75 cpu_busy=32%` | `load1=21.10 cpu_busy=25%` | да |
| degrade nodaemon (1-й) | `load1=14.82 cpu_busy=78%` | `load1=3.62 cpu_busy=15%` | нет (`noisy-degrade-nodaemon-1.json`; шаги 100–500 завышены) |
| **calls daemon (повтор)** | `load1=3.92 cpu_busy=20%` | `load1=3.77 cpu_busy=16%` | да |
| **calls nodaemon (повтор)** | `load1=3.83 cpu_busy=15%` | `load1=3.55 cpu_busy=14%` | да |
| **calls daemon --phases (повтор)** | `load1=3.51 cpu_busy=14%` | `load1=3.25 cpu_busy=16%` | да |
| **calls nodaemon --phases (повтор)** | `load1=3.32 cpu_busy=15%` | `load1=3.45 cpu_busy=14%` | да |
| **degrade nodaemon (повтор)** | `load1=3.49 cpu_busy=16%` | `load1=4.54 cpu_busy=16%` | да |

Load average на этой машине и в тихое время держится ~3–4 (WindowServer, Claude, Safari), поэтому показателен `cpu_busy`:
при 15–30 % разброс p10–p90 укладывается в ±2–5 % медианы, при 70–95 % медианы растут на 20–30 %, а p90 — до +60 %.
Все таблицы выше, кроме `degrade --mode daemon` (снят при `cpu_busy` 25–32 %, кроме всплеска на шаге 500), взяты из повторов в тихом окне; шумные первые прогоны сохранены для сравнения.

## Что должно измениться у Rust и что нет

Должно улучшиться (это и есть цель переноса):

- **Запуск процесса**: `main` 29 мс + ~15 мс вне меток + `env-check`/`build-root` ~8–17 мс → у нативного бинарника единицы мс.
  `tasker --version` 60 мс — нижняя граница любого вызова .NET-консоли.
- **JIT**: 50–80 мс на `task get`/`task create` (850–1200 методов) исчезают полностью.
- **DI-контейнер**: Autofac `container-register` + `container-build` ≈ 43 мс на каждый вызов → статическая сборка графа, ~0.
- **Размер**: 152 МБ / 377 файлов → ориентир 3–8 МБ на бинарник, без среды выполнения.

Не улучшится само по себе (упирается в ввод-вывод и SQLite, не в среду выполнения):

- `index-lock` 18 мс и `index-schema` 2 мс — открытие SQLite и блокировка; `sync-commit` ~20 мс на запись после создания задачи.
  У Rust с `rusqlite` будет примерно столько же; выигрыш возможен только за счёт схемы работы (WAL, меньше транзакций).
- `sync-scan`/`sync-indexed` — чтение каталога и YAML-файлов (90 мс на индексирование одной новой задачи сейчас — это в основном
  JIT и EF Core; чистое чтение файла и запись строк в SQLite должны стать существенно дешевле, но не нулевыми).
- Рост времени с числом задач (+10–20 % от 100 до 2000) определяется SQLite-запросами и сканированием каталога — он останется,
  хотя в абсолютных числах будет меньше заметен.

Реалистичный ориентир после фазы 3: `task get` на 100–2000 задачах — 10–40 мс (из них 15–25 мс SQLite и файлы), `version` — <5 мс.

## Как повторить

```bash
# сборка как у установщика (Release, R2R, self-contained) в отдельный каталог
scripts/perf/publish.sh /tmp/tasker-bin

# размер релиза для текущей платформы
scripts/release.sh --rid osx-arm64 --out /tmp/tasker-release --keep-work
du -sh /tmp/tasker-release/work/osx-arm64/tasker-*/app

# вызовы: медианы и фазы, оба режима (перед запуском: uptime; мерить при низкой занятости CPU, bench печатает [noise])
scripts/perf/bench.py calls --bin /tmp/tasker-bin --mode daemon   --n 30 --json calls-daemon.json
scripts/perf/bench.py calls --bin /tmp/tasker-bin --mode nodaemon --n 30 --json calls-nodaemon.json
scripts/perf/bench.py calls --bin /tmp/tasker-bin --mode daemon   --n 30 --phases --json calls-daemon-phases.json
scripts/perf/bench.py calls --bin /tmp/tasker-bin --mode nodaemon --n 30 --phases --json calls-nodaemon-phases.json

# зависимость от числа задач
scripts/perf/bench.py degrade --bin /tmp/tasker-bin --mode daemon   --steps 100 500 1000 2000 --n 15 --json degrade-daemon.json
scripts/perf/bench.py degrade --bin /tmp/tasker-bin --mode nodaemon --steps 100 500 1000 2000 --n 15 --json degrade-nodaemon.json
```

Для Rust-сборки `--bin` указывает на каталог с `tasker` и `tasker-mcpd`; фазы (`TASKER_PROFILE`) появятся, когда в Rust
будет свой `PerfTrace` с теми же метками (TSK-136).
