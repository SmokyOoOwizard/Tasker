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

## Иконка

Исходники иконок в `Design/icon/`: `icon-dark.svg` для тёмной темы и `icon-light.svg` для светлой. Растровые копии лежат в `Sources/Tasker/Assets.xcassets`: набор `AppIcon` (светлая, все размеры macOS) и наборы `IconDark`/`IconLight`, между которыми приложение переключает иконку в Dock вслед за темой системы.

## Окно

Размер и положение главного окна сохраняются между запусками (автосохранение рамки `TaskerMainWindow`). Второй экземпляр не запускается: повторный запуск выводит окно уже работающего.
