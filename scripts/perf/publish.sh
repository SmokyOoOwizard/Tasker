#!/usr/bin/env bash
# Собирает tasker (NativeAOT) и tasker-mcpd (один файл с рантаймом) так же, как scripts/release.sh, и кладёт обе программы в указанный
# каталог — но никуда не устанавливает. Нужен стенду scripts/perf/bench.py.
#   scripts/perf/publish.sh <каталог>
set -euo pipefail
OUT="${1:?usage: publish.sh <output dir>}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK is required: https://dot.net" >&2; exit 1; }
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;;
  Darwin-x86_64) RID=osx-x64 ;;
  Linux-aarch64) RID=linux-arm64 ;;
  *) RID=linux-x64 ;;
esac
[ "$(uname -s)" = Darwin ] && export PATH="/usr/bin:$PATH"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
dotnet publish "$REPO/dotnet/src/Tasker.Cli" -c Release -r "$RID" -p:PublishAot=true -o "$WORK/cli" --nologo -v quiet
dotnet publish "$REPO/dotnet/src/Tasker.Daemon.Host" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedFrontend=false -o "$WORK/daemon" --nologo -v quiet
mkdir -p "$OUT"
cp "$WORK/cli/tasker" "$WORK/daemon/tasker-mcpd" "$OUT/"
echo "published to $OUT"
