#!/usr/bin/env bash
# Прогон тестов Tasker на Linux в контейнере Docker (TSK-111) — например, с macOS. Репозиторий монтируется только для чтения,
# исходники копируются внутрь контейнера (без bin, obj, node_modules, каталога VCS), поэтому каталоги bin/obj хоста не затрагиваются.
# Кэш NuGet лежит в именованном томе (по умолчанию tasker-nuget), чтобы не качать пакеты каждый раз.
#
#   scripts/test-linux.sh                            # все тесты, нативная архитектура Docker
#   scripts/test-linux.sh --filter "FullyQualifiedName~DaemonUpgradeTests"
#   scripts/test-linux.sh --platform linux/amd64     # другая архитектура (под эмуляцией; может падать)
#   scripts/test-linux.sh --log out.log              # журнал прогона (по умолчанию test-linux.log в текущем каталоге)
#   scripts/test-linux.sh --shell                    # интерактивная оболочка в той же среде (исходники в /work)
#   scripts/test-linux.sh --stress "upgrade"         # вместо тестов — стенд tools/Tasker.Stress с этими аргументами
# Остальные аргументы после `--` передаются в `dotnet test`.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${TASKER_LINUX_IMAGE:-tasker-linux-test}"
base="${TASKER_LINUX_BASE:-mcr.microsoft.com/dotnet/sdk:10.0}"
nuget_volume="${TASKER_LINUX_NUGET_VOLUME:-tasker-nuget-user}"
platform=""
filter=""
log="test-linux.log"
mode="test"
stress_args=""
extra=()

while [ $# -gt 0 ]; do
  case "$1" in
    --filter) filter="$2"; shift 2 ;;
    --platform) platform="$2"; shift 2 ;;
    --log) log="$2"; shift 2 ;;
    --shell) mode="shell"; shift ;;
    --stress) mode="stress"; stress_args="$2"; shift 2 ;;
    --) shift; extra=("$@"); break ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
  esac
done

command -v docker >/dev/null || { echo "Нужен docker." >&2; exit 1; }
pf=()
tag="$image"
if [ -n "$platform" ]; then pf=(--platform "$platform"); tag="$image-${platform//\//-}"; fi

# Образ: sdk + то, чего в нём нет, а тестам нужно (VCS — для хуков, python3 — чужая блокировка, procps — ps).
# Тесты идут НЕ от root: root игнорирует права доступа к файлам (тест отката замены на лету снимает права с .tasker), а systemd --user
# и обычная установка тоже работают от пользователя. Контейнер запускается с --init: без init-процесса (PID 1) умершие дочерние
# процессы демона остаются зомби, и проверки «процесс завершился» не проходят.
if ! docker image inspect "$tag" >/dev/null 2>&1; then
  echo ">> сборка образа $tag"
  docker build "${pf[@]+"${pf[@]}"}" -t "$tag" - <<DOCKERFILE
FROM $base
RUN apt-get update && apt-get install -y --no-install-recommends git python3 procps ca-certificates && rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN useradd -m -u 1500 -s /bin/bash tester && mkdir -p /home/tester/.nuget /work && chown -R tester:tester /home/tester /work
USER tester
WORKDIR /work
# pwsh нужен тесту автодополнения PowerShell (WindowsConsoleTests): без него он молча ничего не проверяет.
RUN dotnet tool install --global PowerShell
ENV PATH="\$PATH:/home/tester/.dotnet/tools"
RUN git config --global user.email test@example.com && git config --global user.name test && git config --global init.defaultBranch main
DOCKERFILE
fi

name="tasker-linux-test-$$"
trap 'docker rm -f "$name" >/dev/null 2>&1 || true' EXIT

inner='set -e
mkdir -p /work
cd /src
tar -c --exclude=./.git --exclude=bin --exclude=obj --exclude=node_modules --exclude=./.claude . | tar -x -C /work
cd /work
'
case "$mode" in
  test)
    inner+="dotnet test tests/Tasker.Tests -p:EmbedFrontend=false --logger \"trx;LogFileName=/work/result.trx\" --logger \"console;verbosity=normal\""
    [ -n "$filter" ] && inner+=" --filter \"$filter\""
    for a in "${extra[@]+"${extra[@]}"}"; do inner+=" $(printf '%q' "$a")"; done
    ;;
  stress) inner+="dotnet run -c Release --project tools/Tasker.Stress -p:EmbedFrontend=false -- $stress_args" ;;
  shell) inner+="exec bash" ;;
esac

ti=()
[ "$mode" = shell ] && ti=(-it)
echo ">> $mode в контейнере $name (журнал: $log)"
set +e
docker run --init "${ti[@]+"${ti[@]}"}" "${pf[@]+"${pf[@]}"}" --name "$name" -v "$repo":/src:ro -v "$nuget_volume":/home/tester/.nuget \
  "$tag" bash -c "$inner" 2>&1 | tee "$log"
exit "${PIPESTATUS[0]}"
