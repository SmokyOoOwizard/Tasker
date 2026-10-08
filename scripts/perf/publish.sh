#!/usr/bin/env bash
# Собирает tasker и tasker-mcpd в указанный каталог так же, как scripts/release.sh (Release, self-contained, ReadyToRun) —
# но никуда не устанавливает. Нужен стенду scripts/perf/bench.py.
#   scripts/perf/publish.sh <каталог>
set -euo pipefail
OUT="${1:?usage: publish.sh <output dir>}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) RID=linux-x64 ;; esac
mkdir -p "$OUT"
dotnet publish "$REPO/src/Tasker.Cli" -c Release -r "$RID" --self-contained true -p:PublishReadyToRun=true \
  -o "$OUT" --nologo -v quiet --disable-build-servers
dotnet publish "$REPO/src/Tasker.Daemon.Host" -c Release -r "$RID" --self-contained true -p:PublishReadyToRun=true \
  -p:EmbedFrontend=false -o "$OUT" --nologo -v quiet --disable-build-servers
echo "published to $OUT"
