#!/usr/bin/env bash
# Проверка автозапуска демона через НАСТОЯЩИЙ systemd --user (TSK-111) в привилегированном контейнере Docker (с macOS и т. п.).
# Поднимает образ с systemd (PID 1), пользователя tester с включённым linger (чтобы был сеанс `systemctl --user`), публикует tasker и
# tasker-mcpd из этого репозитория и проходит по чек-листу: включить/выключить автозапуск, статус, замена на лету под службой,
# перезапуск после убийства супервизора (Restart=always), остановка `systemctl stop` (SIGTERM всему cgroup), запуск и остановка
# командами tasker. Печатает «ok»/«FAIL» по каждому пункту; код выхода 1, если что-то не прошло. Настоящая служба хоста не задета.
#
#   scripts/test-linux-systemd.sh [--platform linux/amd64] [--keep]    # --keep: не удалять контейнер (имя tasker-systemd-check)
#   scripts/test-linux-systemd.sh --rust <каталог>   # Rust-сборки tasker и tasker-mcpd под Linux (TSK-139): сначала .NET-демон под
#                                                    # службой заменяется на лету Rust-рабочим процессом и обратно под непрерывной
#                                                    # нагрузкой (порт не закрывается ни на миг), затем чек-лист — с Rust-консолью и Rust-демоном
#   scripts/test-linux-systemd.sh --archive <tasker-X-linux-*.tar.gz>   # релизный архив (scripts/release.sh, TSK-141): .NET-сборка
#                                                    # ставится install.sh в ~/.local, демон под службой, затем install.sh из
#                                                    # распакованного архива поверх — под нагрузкой: .NET-супервизор переводится на
#                                                    # Rust-рабочий процесс без отказов, в app/ остаются только две программы; затем
#                                                    # чек-лист с установленными Rust-консолью и Rust-демоном
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${TASKER_SYSTEMD_IMAGE:-tasker-linux-systemd}"
name="tasker-systemd-check"
nuget_volume="${TASKER_LINUX_NUGET_VOLUME:-tasker-nuget-user}"
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
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends systemd systemd-sysv dbus dbus-user-session libpam-systemd procps ca-certificates && rm -rf /var/lib/apt/lists/*
RUN useradd -m -u 1500 -s /bin/bash tester
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
DOCKERFILE
fi

cleanup() { [ "$keep" = 1 ] || docker rm -f "$name" >/dev/null 2>&1; }
trap cleanup EXIT
docker rm -f "$name" >/dev/null 2>&1
docker run -d --name "$name" "${pf[@]+"${pf[@]}"}" --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
  --tmpfs /run --tmpfs /run/lock --tmpfs /tmp -v "$repo":/src:ro -v "$nuget_volume":/root/.nuget "${rust_mount[@]+"${rust_mount[@]}"}" "$image" >/dev/null || exit 1

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

echo ">> сборка tasker и tasker-mcpd в контейнере"
docker exec "$name" bash -c '
  mkdir -p /work && cd /src &&
  tar -c --exclude=./.git --exclude=bin --exclude=obj --exclude=node_modules --exclude=./.claude . | tar -x -C /work &&
  cd /work &&
  dotnet publish src/Tasker.Daemon.Host -c Release -p:EmbedFrontend=false -o /opt/tasker >/dev/null &&
  dotnet publish src/Tasker.Cli -c Release -p:EmbedFrontend=false -o /opt/tasker >/dev/null &&
  ln -sf /opt/tasker/tasker /usr/local/bin/tasker' || { echo "FAIL: сборка"; exit 1; }

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

if [ -n "$rust" ]; then
  echo ">> смешанная пара: .NET-демон под systemd --user -> Rust-рабочий процесс -> .NET, под нагрузкой"
  # Нагрузка изнутри контейнера: новые соединения к /health и /mcp (list_projects) подряд; каждая строка — код ответа (000 — отказ).
  load_start() {
    as_tester bash -c 'rm -f /tmp/load.codes /tmp/load.stop; cd /home/tester/ws && (while [ ! -e /tmp/load.stop ]; do
      curl -s -o /dev/null -w "%{http_code}\n" --max-time 30 http://127.0.0.1:5719/health
      curl -s -o /dev/null -w "%{http_code}\n" --max-time 30 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
        -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_projects\",\"arguments\":{}}}" http://127.0.0.1:5719/mcp
    done >> /tmp/load.codes) >/dev/null 2>&1 &'
  }
  load_stop() { as_tester touch /tmp/load.stop; sleep 2; }
  load_clean() { as_tester bash -c '[ "$(wc -l < /tmp/load.codes)" -gt 100 ] && ! grep -qv "^200$" /tmp/load.codes'; }
  daemon_pid() { as_tester tasker mcp status 2>/dev/null | sed -n 's/^pid \([0-9]*\),.*/\1/p' | head -n 1; }
  as_tester bash -c 'mkdir -p /home/tester/ws && cd /home/tester/ws && tasker project create Demo >/dev/null && tasker mcp workspace add . >/dev/null'
  check "[.NET] autostart enable" as_tester tasker mcp autostart enable
  check "[.NET] служба active, есть рабочий процесс" eventually has_worker
  supervisor="$(daemon_pid)"; before="$(worker_pid)"
  load_start; sleep 2
  check "[.NET -> Rust] tasker mcp upgrade --daemon /rust/tasker-mcpd" bash -c "docker exec -u tester -e HOME=/home/tester $name tasker mcp upgrade --daemon /rust/tasker-mcpd | grep -q 'replaced without downtime'"
  rust_worker="$(worker_pid)"
  check "рабочий процесс сменился ($before -> $rust_worker) и это Rust (/rust/tasker-mcpd)" bash -c "[ -n '$rust_worker' ] && [ '$before' != '$rust_worker' ] && docker exec $name cat /proc/$rust_worker/cmdline | tr '\\0' ' ' | grep -q '^/rust/tasker-mcpd --worker --listen-fd'"
  sleep 2
  check "[Rust -> .NET] tasker mcp upgrade обратно на .NET-сборку" bash -c "docker exec -u tester -e HOME=/home/tester $name tasker mcp upgrade | grep -q 'replaced without downtime'"
  sleep 2
  load_stop
  requests="$(as_tester bash -c 'wc -l < /tmp/load.codes' | tr -d ' ')"
  check "ни одного отказа соединения и не-200 за время замен ($requests запросов)" load_clean
  check "супервизор (.NET) и служба те же (pid $supervisor)" bash -c "[ '$(daemon_pid)' = '$supervisor' ] && docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 $name systemctl --user is-active --quiet tasker-mcp.service"
  check "[.NET] autostart disable" as_tester tasker mcp autostart disable
  as_tester tasker mcp stop >/dev/null 2>&1
  as_tester bash -c 'cd /home/tester/ws && tasker mcp workspace remove . >/dev/null'
  echo ">> Rust-сборки tasker и tasker-mcpd вместо .NET в /opt/tasker"
  docker exec "$name" bash -c 'cp /rust/tasker /rust/tasker-mcpd /opt/tasker/' || { echo "FAIL: копирование Rust-сборок"; exit 1; }
fi

if [ -n "$archive" ]; then
  echo ">> обновление .NET-установки релизным архивом $(basename "$archive"): install.sh, демон под systemd --user, нагрузка"
  app=/home/tester/.local/share/tasker/app
  load_start() {
    as_tester bash -c 'rm -f /tmp/load.codes /tmp/load.stop; cd /home/tester/ws && (while [ ! -e /tmp/load.stop ]; do
      curl -s -o /dev/null -w "%{http_code}\n" --max-time 30 http://127.0.0.1:5719/health
      curl -s -o /dev/null -w "%{http_code}\n" --max-time 30 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
        -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_projects\",\"arguments\":{}}}" http://127.0.0.1:5719/mcp
    done >> /tmp/load.codes) >/dev/null 2>&1 &'
  }
  load_stop() { as_tester touch /tmp/load.stop; sleep 2; }
  load_clean() { as_tester bash -c '[ "$(wc -l < /tmp/load.codes)" -gt 100 ] && ! grep -qv "^200$" /tmp/load.codes'; }
  daemon_pid() { as_tester tasker mcp status 2>/dev/null | sed -n 's/^pid \([0-9]*\),.*/\1/p' | head -n 1; }
  is_dotnet() { docker exec "$name" grep -q libcoreclr "/proc/$1/maps"; }
  # .NET-установка как у пользователя: install.sh в ~/.local, команда tasker — установленная.
  check "[.NET] install.sh --from <.NET-сборка> в ~/.local" as_tester bash /src/scripts/install.sh --from /opt/tasker --no-autostart --no-completion
  docker exec "$name" ln -sf /home/tester/.local/bin/tasker /usr/local/bin/tasker
  as_tester bash -c 'mkdir -p /home/tester/ws && cd /home/tester/ws && tasker project create Demo >/dev/null && tasker mcp workspace add . >/dev/null'
  check "[.NET] в app/ — .NET-сборка с библиотеками" as_tester test -f "$app/tasker.dll"
  check "[.NET] autostart enable" as_tester tasker mcp autostart enable
  check "[.NET] служба active, есть рабочий процесс" eventually has_worker
  supervisor="$(daemon_pid)"; before="$(worker_pid)"
  check "[.NET] супервизор ($supervisor) и рабочий процесс ($before) — .NET" bash -c "$(declare -f is_dotnet); name=$name; is_dotnet '$supervisor' && is_dotnet '$before'"
  load_start; sleep 2
  as_tester bash -c 'rm -rf /home/tester/rel && mkdir /home/tester/rel && tar -xzf /release.tar.gz -C /home/tester/rel'
  install_out="$(as_tester bash -c '/home/tester/rel/*/install.sh --no-completion' 2>&1)"
  printf '%s\n' "$install_out" | sed 's/^/      | /'
  check "install.sh из распакованного архива поверх (демон переводится через tasker mcp upgrade)" \
    grep -q 'switching it to the new one without downtime' <<< "$install_out"
  check "tasker mcp upgrade: замена без простоя" grep -q 'replaced without downtime' <<< "$install_out"
  sleep 3
  load_stop
  rust_worker="$(worker_pid)"
  requests="$(as_tester bash -c 'wc -l < /tmp/load.codes' | tr -d ' ')"
  check "ни одного отказа соединения и не-200 за время обновления ($requests запросов)" load_clean
  check "рабочий процесс сменился ($before -> $rust_worker) и он Rust" bash -c "$(declare -f is_dotnet); name=$name; [ -n '$rust_worker' ] && [ '$before' != '$rust_worker' ] && ! is_dotnet '$rust_worker'"
  check "супервизор тот же (.NET, pid $supervisor), служба active" bash -c "$(declare -f is_dotnet); name=$name; [ '$(daemon_pid)' = '$supervisor' ] && is_dotnet '$supervisor' && docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 $name systemctl --user is-active --quiet tasker-mcp.service"
  check "в app/ только tasker и tasker-mcpd (от .NET ничего не осталось)" as_tester bash -c "[ \"\$(ls -A $app | tr '\\n' ' ')\" = 'tasker tasker-mcpd ' ] && [ ! -e $app.old ]"
  check "tasker --version — версия архива" bash -c "[ \"\$(docker exec $name tasker --version)\" = \"\$(docker exec $name sed -n 's/^version=//p' /home/tester/rel/$(tar -tzf "$archive" | head -n 1 | cut -d/ -f1)/release.txt)\" ]"
  check "systemctl --user restart: супервизор тоже Rust" bash -c "docker exec -u tester -e XDG_RUNTIME_DIR=/run/user/1500 $name systemctl --user restart tasker-mcp.service"
  check "демон снова отвечает" eventually has_worker 20
  check "новый супервизор — Rust" bash -c "$(declare -f is_dotnet); name=$name; pid=\$(docker exec -u tester -e HOME=/home/tester $name tasker mcp status | sed -n 's/^pid \\([0-9]*\\),.*/\\1/p' | head -n 1); [ -n \"\$pid\" ] && ! is_dotnet \$pid"
  check "autostart disable" as_tester tasker mcp autostart disable
  as_tester tasker mcp stop >/dev/null 2>&1
  as_tester bash -c 'cd /home/tester/ws && tasker mcp workspace remove . >/dev/null'
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
