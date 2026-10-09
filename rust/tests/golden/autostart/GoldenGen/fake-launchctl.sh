#!/bin/sh
# Поддельный launchctl для снятия эталонов: записывает аргументы и ничего не регистрирует.
printf '%s\n' "launchctl $*" >> "$FAKE_LAUNCHCTL_LOG"
exit 0
