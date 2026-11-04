# Tasker для macOS

Нативное приложение на SwiftUI + AppKit. Минимальная версия macOS — 14 (Sonoma), Swift 6 со строгой проверкой конкурентности.

## Как устроен проект

Файл `Tasker.xcodeproj` в git не хранится: он создаётся из описания `project.yml` инструментом [XcodeGen](https://github.com/yonaskolb/XcodeGen). Так параллельные ветки не конфликтуют в `project.pbxproj`, а новые файлы из `Sources/` и `Tests/` подхватываются сами.

- `Sources/Tasker` — приложение (bundle id `com.technotronika.tasker`);
- `Tests/TaskerTests` — тесты на Swift Testing;
- версия берётся из файла `VERSION` в корне репозитория, номер сборки — число коммитов.

## Требования

- Xcode 16 или новее;
- XcodeGen: `brew install xcodegen`.

## Команды

Из корня репозитория:

```bash
scripts/macos-build.sh generate
```

Создаёт `macos/Tasker.xcodeproj`, после чего его можно открыть в Xcode.

```bash
scripts/macos-build.sh build
```

Собирает отладочную версию в `macos/build/Build/Products/Debug/Tasker.app`.

```bash
scripts/macos-build.sh test
```

Запускает тесты.

```bash
scripts/macos-build.sh run
```

Собирает и запускает приложение.

```bash
scripts/macos-build.sh release
```

Собирает релизную версию.

## Токены дизайна

Цвета двух тем, радиусы, отступы, размеры шрифтов, тени и контуры значков статусов лежат в `design/tokens.json` (значения взяты из `docs/mockups/web/main-design.html`). Скрипт `scripts/gen-design-tokens.py` создаёт из него:

- `macos/Sources/Tasker/Generated/DesignTokens.swift` — константы и описание значков статусов;
- `macos/Sources/Tasker/Assets.xcassets/Tokens/` — цвета для светлой и тёмной темы;
- `design/generated/tokens.css` — CSS-переменные (во фронтенд пока не подключены).

После правки `tokens.json` запустите `python3 scripts/gen-design-tokens.py`. Команда `python3 scripts/gen-design-tokens.py --check` падает, если сгенерированные файлы разошлись с источником; её выполняет CI (`.github/workflows/design-tokens.yml`).

В коде используются `DesignColor.<имя>.color`, `DesignRadius`, `DesignSpacing`, `DesignLayout`, `DesignTypography` и `StatusIconView(name:color:)`. Системные цвета и числа, вписанные вручную, в видах не применяются.

## Строки и языки

Интерфейс на русском и английском. Все строки лежат в `design/strings.json`: ключ вида `section.tasks`, у каждого два перевода (`ru`, `en`). Подстановки пишутся как `{0}`, множественные формы как `{0#задача|задачи|задач}` (у русского три формы, у английского две) — так же, как в словаре макета `docs/mockups/web/main-design.html`. Строки, которые есть и в макете, обязаны переводиться одинаково.

Скрипт `scripts/gen-strings.py` создаёт из файла:

- `macos/Sources/Tasker/Generated/Localizable.xcstrings` — String Catalog;
- `macos/Sources/Tasker/Generated/L10nKey.swift` — перечисление ключей.

После правки `strings.json` запустите `python3 scripts/gen-strings.py`. Команда `python3 scripts/gen-strings.py --check` падает, если нет перевода, не совпадают подстановки или формы, перевод разошёлся с макетом, сгенерированные файлы устарели или в коде Swift вписана строка интерфейса (`Text("…")`, кириллица и т. п.); её выполняет CI (`.github/workflows/design-strings.yml`).

В коде строки берутся через `@Environment(\.localizer)`: `l.string(.sectionTasks)`, `l.taskCount(5)`, `l.date(...)`, `l.number(...)`. Язык по умолчанию — системный (русский, если в системе русский, иначе английский). Внизу боковой панели кнопка с текущим языком (RU или EN) открывает список: «Как в системе», «Русский», «English». Выбор применяется сразу, а системные пункты меню macOS обновятся после перезапуска.

## Иконка

Исходники иконок в `Design/icon/`: `icon-dark.svg` для тёмной темы и `icon-light.svg` для светлой. Растровые копии лежат в `Sources/Tasker/Assets.xcassets`: набор `AppIcon` (светлая, все размеры macOS) и наборы `IconDark`/`IconLight`, между которыми приложение переключает иконку в Dock вслед за темой системы.

## Окно

Размер и положение главного окна сохраняются между запусками (автосохранение рамки `TaskerMainWindow`). Второй экземпляр не запускается: повторный запуск выводит окно уже работающего.
