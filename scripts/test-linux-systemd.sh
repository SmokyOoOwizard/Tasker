#!/usr/bin/env bash
# Проверка автозапуска демона через НАСТОЯЩИЙ systemd --user (TSK-111) в привилегированном контейнере Docker (с macOS и т. п.).
# Поднимает образ с systemd (PID 1), пользователя tester с включённым linger (чтобы был сеанс `systemctl --user`), ставит tasker и
# tasker-mcpd и проходит по чек-листу: включить/выключить автозапуск, статус, замена на лету под службой, перезапуск после убийства
# супервизора (Restart=always), остановка `systemctl stop` (SIGTERM всему cgroup), запуск и остановка командами tasker. Печатает
# «ok»/«FAIL» по каждому пункту; код выхода 1, если что-то не прошло. Настоящая служба хоста не задета.
#
#   scripts/test-linux-systemd.sh [--platform linux/amd64] [--keep]    # собрать tasker и tasker-mcpd из репозитория (cargo в контейнере);
#                                                                      # --keep: не удалять контейнер (имя tasker-systemd-check)
#   scripts/test-linux-systemd.sh --rust <каталог>   # готовые сборки tasker и tasker-mcpd под Linux
#   scripts/test-linux-systemd.sh --archive <tasker-X-linux-*.tar.gz>   # релизный архив (scripts/release.sh): install.sh из
#                                                    # распакованного архива в ~/.local, затем чек-лист с установленными программами
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${TASKER_SYSTEMD_IMAGE:-tasker-linux-systemd}"
name="tasker-systemd-check"
cargo_volume="${TASKER_LINUX_CARGO_VOLUME:-tasker-cargo-registry}"
keep=0
rust=""
archive=""
pf=()
while [ $# -gt 0 ]; do
  case "$1" in
    --platform) pf=(--platform "$2"); image="$image-${2//\//-}"; shift 2 ;;
    --keep) keep=1; shift ;;
    --rust) rust="$(cd "$2" && pwd)"; shift 2 ;;
    --archive) archive="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"; shift 2 ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
  esac
done
command -v docker >/dev/null || { echo "Нужен docker." >&2; exit 1; }
if [ -n "$rust" ] && { [ ! -x "$rust/tasker" ] || [ ! -x "$rust/tasker-mcpd" ]; }; then
  echo "В $rust нет исполняемых tasker и tasker-mcpd (сборка под Linux)." >&2; exit 2
fi
if [ -n "$archive" ] && [ ! -f "$archive" ]; then
  echo "Нет архива $archive." >&2; exit 2
fi
[ -n "$rust" ] && [ -n "$archive" ] && { echo "--rust и --archive — разные режимы, выберите один." >&2; exit 2; }
rust_mount=()
[ -n "$rust" ] && rust_mount=(-v "$rust":/rust:ro)
[ -n "$archive" ] && rust_mount=(-v "$archive":/release.tar.gz:ro)

if ! docker image inspect "$image" >/dev/null 2>&1; then
  echo ">> сборка образа $image"
  docker build "${pf[@]+"${pf[@]}"}" -t "$image" - <<'DOCKERFILE' || exit 1
FROM rust:1-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends systemd systemd-sysv dbus dbus-user-session libpam-systemd procps curl ca-certificates && rm -rf /var/lib/apt/lists/*
RUN useradd -m -u 1500 -s /bin/bash tester
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
DOCKERFILE
fi

cleanup() { [ "$keep" = 1 ] || docker rm -f "$name" >/dev/null 2>&1; }
trap cleanup EXIT
docker rm -f "$name" >/dev/null 2>&1
docker run -d --name "$name" "${pf[@]+"${pf[@]}"}" --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
  --tmpfs /run --tmpfs /run/lock --tmpfs /tmp -v "$repo":/src:ro -v "$cargo_volume":/usr/local/cargo/registry "${rust_mount[@]+"${rust_mount[@]}"}" "$image" >/dev/null || exit 1

for _ in $(seq 1 30); do
  state="$(docker exec "$name" systemctl is-system-running 2>/dev/null)"
  case "$state" in running|degraded) break ;; esac
  sleep 1
done
docker exec "$name" loginctl enable-linger tester
for _ in $(seq 1 30); do
  docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 "$name" systemctl --user is-system-running >/dev/null 2>&1 && break
  sleep 1
done

if [ -n "$rust" ]; then
  echo ">> готовые tasker и tasker-mcpd из $rust"
  docker exec "$name" bash -c 'mkdir -p /opt/tasker && cp /rust/tasker /rust/tasker-mcpd /opt/tasker/ && ln -sf /opt/tasker/tasker /usr/local/bin/tasker' \
    || { echo "FAIL: копирование сборок"; exit 1; }
elif [ -z "$archive" ]; then
  echo ">> сборка tasker и tasker-mcpd в контейнере (cargo build --release)"
  docker exec "$name" bash -c '
    mkdir -p /work && cd /src &&
    tar -c --exclude=./.git --exclude=target --exclude=bin --exclude=obj --exclude=node_modules --exclude=./.claude . | tar -x -C /work &&
    cd /work/rust && cargo build --release --locked --quiet --bin tasker --bin tasker-mcpd &&
    mkdir -p /opt/tasker && cp target/release/tasker target/release/tasker-mcpd /opt/tasker/ &&
    ln -sf /opt/tasker/tasker /usr/local/bin/tasker' || { echo "FAIL: сборка"; exit 1; }
fi

as_tester() {
  docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 -e HOME=/home/tester -e DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1500/bus "$name" "$@"
}
failed=0
check() { # check "описание" команда...
  local what="$1"; shift
  if "$@" >/dev/null 2>&1; then echo "ok    $what"; else echo "FAIL  $what"; failed=1; fi
}
active() { as_tester systemctl --user is-active --quiet tasker-mcp.service; }
inactive() { ! active; }
eventually() { for _ in $(seq 1 "${2:-20}"); do "$1" && return 0; sleep 1; done; return 1; }
has_worker() { as_tester tasker mcp status 2>&1 | grep -q 'worker pid'; }
worker_pid() { as_tester tasker mcp status 2>/dev/null | sed -n 's/^worker pid \([0-9]*\).*/\1/p' | head -n 1; }

if [ -n "$archive" ]; then
  echo ">> установка релизного архива $(basename "$archive"): install.sh в ~/.local"
  as_tester bash -c 'rm -rf /home/tester/rel && mkdir /home/tester/rel && tar -xzf /release.tar.gz -C /home/tester/rel'
  check "install.sh из распакованного архива" as_tester bash -c '/home/tester/rel/*/install.sh --no-autostart --no-completion'
  docker exec "$name" ln -sf /home/tester/.local/bin/tasker /usr/local/bin/tasker
  check "в app/ только tasker и tasker-mcpd" as_tester bash -c "[ \"\$(ls -A /home/tester/.local/share/tasker/app | tr '\\n' ' ')\" = 'tasker tasker-mcpd ' ]"
  check "tasker --version — версия архива" bash -c "[ \"\$(docker exec $name tasker --version)\" = \"\$(docker exec $name sed -n 's/^version=//p' /home/tester/rel/$(tar -tzf "$archive" | head -n 1 | cut -d/ -f1)/release.txt)\" ]"
fi

echo ">> чек-лист systemd --user"
check "автозапуск выключен до включения" bash -c "[ \"\$(docker exec -u tester -e HOME=/home/tester $name tasker mcp autostart status)\" = off ]"
check "autostart enable создаёт и запускает службу" as_tester tasker mcp autostart enable
check "служба active" eventually active
check "unit-файл на месте" as_tester test -f /home/tester/.config/systemd/user/tasker-mcp.service
check "unit: Restart=always" as_tester grep -q '^Restart=always' /home/tester/.config/systemd/user/tasker-mcp.service
check "tasker mcp status видит демона и рабочий процесс" eventually has_worker
check "autostart status: enabled (systemd)" bash -c "docker exec -u tester -e HOME=/home/tester $name tasker mcp autostart status | grep -q 'enabled (systemd)'"
before="$(worker_pid)"
check "tasker mcp upgrade под службой заменяет рабочий процесс на лету" as_tester tasker mcp upgrade
after="$(worker_pid)"
check "pid рабочего процесса сменился ($before -> $after)" test -n "$before" -a -n "$after" -a "$before" != "$after"
check "служба active после замены" active
as_tester pkill -KILL -f 'tasker-mcpd --detached'
check "после убийства супервизора systemd поднимает службу заново (Restart=always, RestartSec=5)" eventually active 20
check "демон снова отвечает" eventually has_worker 20
check "systemctl stop завершает супервизор и рабочие процессы" bash -c "docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 $name systemctl --user stop tasker-mcp.service && ! docker exec $name pgrep -f tasker-mcpd"
check "журнал: остановка по SIGTERM штатная" as_tester bash -c 'grep -q "Stopping the MCP server: SIGTERM" /home/tester/.local/share/Tasker/logs/mcp-*.log'
check "tasker mcp start запускает службу" as_tester tasker mcp start
check "служба active после tasker mcp start" eventually active
check "tasker mcp stop останавливает" as_tester tasker mcp stop
check "служба неактивна после tasker mcp stop" eventually inactive
check "autostart disable убирает unit-файл" as_tester tasker mcp autostart disable
check "unit-файла нет" bash -c "! docker exec $name test -f /home/tester/.config/systemd/user/tasker-mcp.service"
as_tester tasker mcp stop >/dev/null 2>&1

if [ "$failed" = 0 ]; then echo "RESULT: all checks passed"; else echo "RESULT: FAILED"; fi
exit "$failed"
