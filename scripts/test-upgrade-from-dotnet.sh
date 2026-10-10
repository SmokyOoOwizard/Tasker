#!/usr/bin/env bash
# Сценарий обновления у пользователя (TSK-142): .NET-установка с демоном под автозапуском -> install.sh из Rust-архива поверх под
# нагрузкой -> 0 отказов -> `tasker mcp restart` -> супервизор тоже Rust. На этой машине (macOS: launchd; Linux: systemd --user),
# полностью изолированно: установка во временный --prefix, свои TASKER_HOME, TASKER_SERVICE_DIR, TASKER_SERVICE_LABEL и свободный
# порт — настоящая установка, демон и служба пользователя не затрагиваются. Linux под systemd в Docker — test-linux-systemd.sh --archive.
#
#   scripts/test-upgrade-from-dotnet.sh <.NET-архив tasker-X-RID.tar.gz> <Rust-архив tasker-Y-RID.tar.gz> [--keep]
#
# .NET-архив — релиз до перехода на Rust (scripts/release.sh из коммита до TSK-141, например f2a114d), ставится СВОИМ install.sh, как
# у пользователя. Rust-архив — scripts/release.sh. Печатает «ok»/«FAIL» по каждому пункту; код выхода 1, если что-то не прошло.
# --keep: не удалять временный каталог (служба всё равно выключается).
set -uo pipefail

[ $# -ge 2 ] || { sed -n '2,11p' "$0"; exit 2; }
dotnet_archive="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
rust_archive="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
keep=0; [ "${3:-}" = --keep ] && keep=1
[ -f "$dotnet_archive" ] && [ -f "$rust_archive" ] || { echo "Нет архива." >&2; exit 2; }
command -v curl >/dev/null && command -v python3 >/dev/null || { echo "Нужны curl и python3." >&2; exit 2; }

root="$(mktemp -d "${TMPDIR:-/tmp}/tasker-upgrade.XXXXXX")"
root="$(cd "$root" && pwd -P)"
port="$(python3 -I -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')"
prefix="$root/prefix"; ws="$root/ws"; app="$prefix/share/tasker/app"; tasker="$prefix/bin/tasker"
export TASKER_HOME="$root/home" TASKER_SERVICE_DIR="$root/service"
case "$(uname -s)" in
  Darwin) export TASKER_SERVICE_LABEL="com.tasker.upgrade-test-$port" ;;
  *) export TASKER_SERVICE_LABEL="tasker-upgrade-test-$port.service" ;;
esac
unset TASKER_BIN TASKER_MCPD_BIN TASKER_MCP_HANDOFF
mkdir -p "$TASKER_HOME" "$ws"
printf '{"mcp":{"port":%s}}\n' "$port" > "$TASKER_HOME/settings.json"

cleanup() {
  [ -x "$tasker" ] && { "$tasker" mcp autostart disable >/dev/null 2>&1; "$tasker" mcp stop >/dev/null 2>&1; }
  if [ "$(uname -s)" = Darwin ]; then
    launchctl bootout "gui/$(id -u)/$TASKER_SERVICE_LABEL" >/dev/null 2>&1
  else
    systemctl --user stop "$TASKER_SERVICE_LABEL" >/dev/null 2>&1
  fi
  pkill -f "$app/tasker-mcpd" 2>/dev/null
  touch "$root/load.stop" 2>/dev/null
  if [ "$keep" = 1 ]; then echo "Каталог оставлен: $root"; else rm -rf "$root"; fi
}
trap cleanup EXIT

failed=0
check() { # check "описание" команда...
  local what="$1"; shift
  if "$@" >/dev/null 2>&1; then echo "ok    $what"; else echo "FAIL  $what"; failed=1; fi
}
eventually() { for _ in $(seq 1 "${2:-30}"); do "$1" && return 0; sleep 1; done; return 1; }
status_text() { "$tasker" mcp status 2>/dev/null; }
supervisor_pid() { status_text | sed -n 's/^pid \([0-9]*\),.*/\1/p' | head -n 1; }
worker_pid() { status_text | sed -n 's/^worker pid \([0-9]*\).*/\1/p' | head -n 1; }
one_worker() { [ "$(status_text | grep -c '^worker pid')" = 1 ] && status_text | grep -q '  open '; }
# .NET-процесс — загружена среда выполнения CoreCLR.
is_dotnet() {
  if [ -r "/proc/$1/maps" ]; then grep -q libcoreclr "/proc/$1/maps"; else lsof -p "$1" 2>/dev/null | grep -q libcoreclr; fi
}
service_loaded() {
  if [ "$(uname -s)" = Darwin ]; then launchctl print "gui/$(id -u)/$TASKER_SERVICE_LABEL" >/dev/null 2>&1
  else systemctl --user is-active --quiet "$TASKER_SERVICE_LABEL"; fi
}

# Нагрузка: новые соединения подряд — /health и /mcp list_projects (два потока) и create_task (один поток); каждая строка — код
# ответа (000 — отказ соединения), подтверждённые записи — в writes.ok.
load_start() {
  rm -f "$root/load.codes" "$root/load.stop" "$root/writes.ok"
  local i
  for i in 1 2; do
    (while [ ! -e "$root/load.stop" ]; do
      curl -s -o /dev/null -w '%{http_code}\n' --max-time 30 "http://127.0.0.1:$port/health"
      curl -s -o /dev/null -w '%{http_code}\n' --max-time 30 -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_projects","arguments":{"workspace":"ws"}}}' \
        "http://127.0.0.1:$port/mcp"
    done >> "$root/load.codes") 2>/dev/null &
  done
  (n=0; while [ ! -e "$root/load.stop" ]; do
    n=$((n + 1)); title="load-$n"
    body="$(curl -s -w '\n%{http_code}' --max-time 30 -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
      -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"create_task\",\"arguments\":{\"workspace\":\"ws\",\"typeId\":\"$type_id\",\"title\":\"$title\"}}}" \
      "http://127.0.0.1:$port/mcp")"
    code="$(printf '%s' "$body" | tail -n 1)"
    echo "$code" >> "$root/load.codes"
    if [ "$code" = 200 ] && ! printf '%s' "$body" | grep -q '"isError": *true'; then echo "$title" >> "$root/writes.ok"; fi
  done) 2>/dev/null &
}
load_stop() { touch "$root/load.stop"; wait; }
load_clean() { [ "$(wc -l < "$root/load.codes")" -gt 100 ] && ! grep -qv '^200$' "$root/load.codes"; }
writes_exact() {
  local files acknowledged
  files="$(grep -rh '^title: load-' "$ws/.tasker" | sed 's/^title: //; s/["'"'"']//g' | sort)"
  acknowledged="$(sort "$root/writes.ok" 2>/dev/null)"
  [ -n "$acknowledged" ] && [ "$files" = "$acknowledged" ]
}

echo ">> временный каталог $root, порт $port, служба $TASKER_SERVICE_LABEL"
echo ">> .NET-установка: install.sh из $(basename "$dotnet_archive")"
mkdir "$root/dotnet" "$root/rust"
tar -xzf "$dotnet_archive" -C "$root/dotnet" && tar -xzf "$rust_archive" -C "$root/rust" || exit 1
check "[.NET] install.sh --prefix (свой скрипт .NET-релиза)" bash "$root"/dotnet/*/install.sh --prefix "$prefix" --no-completion --no-autostart
check "[.NET] в app/ — .NET-сборка с библиотеками" test -f "$app/tasker.dll"
(cd "$ws" && "$tasker" project create Demo >/dev/null && "$tasker" status create Todo >/dev/null &&
  "$tasker" status-set create Flow --status Todo >/dev/null && "$tasker" task-type create Bug --status-set Flow >/dev/null) || exit 1
"$tasker" mcp workspace add "$ws" >/dev/null
type_id="$(cd "$ws" && "$tasker" task-type list --json | python3 -I -c 'import json,sys; d=json.load(sys.stdin); d=d.get("data",d); print(d[0]["id"])')"
check "[.NET] autostart enable (служба с изолированным именем)" "$tasker" mcp autostart enable
check "[.NET] служба загружена, демон отвечает, один рабочий процесс" eventually one_worker
supervisor="$(supervisor_pid)"; before="$(worker_pid)"
check "[.NET] супервизор ($supervisor) и рабочий процесс ($before) — .NET" bash -c "$(declare -f is_dotnet); is_dotnet '$supervisor' && is_dotnet '$before'"

echo ">> обновление: install.sh из $(basename "$rust_archive") поверх, под нагрузкой"
load_start; sleep 2
install_out="$(bash "$root"/rust/*/install.sh --prefix "$prefix" --no-completion 2>&1)"
printf '%s\n' "$install_out" | sed 's/^/      | /'
check "install.sh переводит работающий демон через tasker mcp upgrade" grep -q 'switching it to the new one without downtime' <<< "$install_out"
check "tasker mcp upgrade: замена без простоя" grep -q 'replaced without downtime' <<< "$install_out"
check "install.sh объясняет, что супервизор пока .NET и это штатно" grep -q 'is still the previous .NET build' <<< "$install_out"
sleep 3
load_stop
requests="$(wc -l < "$root/load.codes" | tr -d ' ')"
refused="$(grep -cv '^200$' "$root/load.codes")"
check "ни одного отказа соединения и не-200 за время обновления ($requests запросов, отказов: $refused)" load_clean
check "каждая подтверждённая запись в файлах ровно один раз ($(cat "$root/writes.ok" 2>/dev/null | wc -l | tr -d ' ') записей)" writes_exact
after="$(worker_pid)"
check "рабочий процесс сменился ($before -> $after) и он Rust" bash -c "$(declare -f is_dotnet); [ -n '$after' ] && [ '$before' != '$after' ] && ! is_dotnet '$after'"
check "супервизор тот же (pid $supervisor) и он .NET, служба загружена" bash -c "$(declare -f is_dotnet service_loaded); [ \"\$('$tasker' mcp status | sed -n 's/^pid \\([0-9]*\\),.*/\\1/p')\" = '$supervisor' ] && is_dotnet '$supervisor' && service_loaded"
check "в app/ только tasker и tasker-mcpd" bash -c "[ \"\$(ls -A '$app' | tr '\\n' ' ')\" = 'tasker tasker-mcpd ' ] && [ ! -e '$app.old' ]"
check "tasker --version — версия Rust-архива" bash -c "[ \"\$('$tasker' --version)\" = \"\$(sed -n 's/^version=//p' '$root'/rust/*/release.txt)\" ]"

echo ">> tasker mcp restart: супервизор тоже Rust"
check "tasker mcp restart" "$tasker" mcp restart
check "демон снова отвечает, один рабочий процесс" eventually one_worker
new_supervisor="$(supervisor_pid)"
check "новый супервизор ($new_supervisor) — Rust, служба загружена" bash -c "$(declare -f is_dotnet service_loaded); [ -n '$new_supervisor' ] && [ '$new_supervisor' != '$supervisor' ] && ! is_dotnet '$new_supervisor' && service_loaded"
check "рабочий процесс — Rust" bash -c "$(declare -f is_dotnet); w=\$('$tasker' mcp status | sed -n 's/^worker pid \\([0-9]*\\).*/\\1/p' | head -n 1); [ -n \"\$w\" ] && ! is_dotnet \$w"
check "MCP отвечает (list_projects)" bash -c "curl -s -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_projects\",\"arguments\":{\"workspace\":\"ws\"}}}' http://127.0.0.1:$port/mcp | grep -q Demo"
check "autostart disable" "$tasker" mcp autostart disable

if [ "$failed" = 0 ]; then echo "RESULT: all checks passed"; else echo "RESULT: FAILED"; fi
exit "$failed"
