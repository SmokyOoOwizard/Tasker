#!/bin/bash
# Собирает релиз Tasker: самодостаточные сборки (без установленного .NET) консольной утилиты tasker и демона MCP (tasker-mcpd)
# под выбранные платформы, упаковывает их в архивы и считает контрольные суммы SHA256. Ничего не устанавливает.
#
#   scripts/release.sh [--rid RID[,RID...]|all] [--version X.Y.Z] [--out DIR] [--no-r2r] [--no-archive] [--keep-work]
#
# Платформы (RID): win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64. По умолчанию — все; кросс-сборка идёт с любой из
# них (нужна сеть: .NET скачивает пакеты среды выполнения нужной платформы, затем они лежат в кэше NuGet).
#
# Версия — файл VERSION в корне репозитория (0.1.0, 0.2.0-rc.1): её показывает `tasker --version`. Если текущий коммит помечен тегом
# v*, тег должен совпасть с файлом (иначе ошибка). --version задаёт номер вручную (для пробной сборки).
#
# Результат (по умолчанию artifacts/release/<версия>/):
#   tasker-<версия>-<rid>.tar.gz | .zip (Windows)   архив: каталог tasker-<версия>-<rid>/ с app/ (tasker, tasker-mcpd, библиотеки),
#                                                   install.sh (install.ps1 для Windows) и release.txt (версия, платформа, коммит)
#   tasker-<версия>-<rid>.<расширение>.sha256       контрольная сумма архива
#   SHA256SUMS                                      все контрольные суммы одним файлом (проверка: shasum -a 256 -c SHA256SUMS)
#   install.sh, install.ps1                         скрипты установки отдельно (чтобы запускать их, не распаковывая архив)
#
# Размер и скорость: самодостаточная папка (одна копия среды выполнения на tasker и tasker-mcpd) с ReadyToRun; единый файл и
# обрезка (trimming) НЕ используются: Autofac, EF Core, System.CommandLine и ASP.NET Core опираются на рефлексию (проверено: с PublishTrimmed
# tasker падает на project create), а единый файл дублировал бы среду
# выполнения в каждом из двух исполняемых файлов. Десктоп (Tasker.Desktop) в релиз не входит.

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/release.sh [options]

Builds self-contained tasker + tasker-mcpd for the chosen platforms and packs them into archives with SHA256 checksums.

Options:
  --rid LIST     comma-separated RIDs or 'all' (default): win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64 osx-x64
  --version V    release version (default: the VERSION file; a git tag v* on HEAD must match it)
  --out DIR      output directory (default: artifacts/release/<version> in the repository)
  --no-r2r       publish without ReadyToRun (smaller, slower to start; no crossgen package needed)
  --no-archive   publish only (DIR/<rid>/tasker-<version>-<rid>/), no archives
  --keep-work    keep the intermediate publish folders next to the archives
  -h, --help     show this help
USAGE
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

ALL_RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-arm64 osx-x64)
RIDS=("${ALL_RIDS[@]}")
VERSION_OVERRIDE=""
OUT=""
R2R=true
ARCHIVE=true
KEEP_WORK=false

while [ $# -gt 0 ]; do
  case "$1" in
    --rid)
      [ $# -ge 2 ] || { echo "Error: --rid needs a list" >&2; usage >&2; exit 2; }
      if [ "$2" = "all" ]; then RIDS=("${ALL_RIDS[@]}"); else IFS=',' read -r -a RIDS <<< "$2"; fi
      shift 2
      ;;
    --version)
      [ $# -ge 2 ] || { echo "Error: --version needs a number" >&2; usage >&2; exit 2; }
      VERSION_OVERRIDE="$2"
      shift 2
      ;;
    --out)
      [ $# -ge 2 ] || { echo "Error: --out needs a directory" >&2; usage >&2; exit 2; }
      OUT="$2"
      shift 2
      ;;
    --no-r2r) R2R=false; shift ;;
    --no-archive) ARCHIVE=false; shift ;;
    --keep-work) KEEP_WORK=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

for rid in "${RIDS[@]}"; do
  known=false
  for candidate in "${ALL_RIDS[@]}"; do [ "$rid" = "$candidate" ] && known=true; done
  [ "$known" = true ] || { echo "Error: unknown platform '$rid' (known: ${ALL_RIDS[*]})" >&2; exit 2; }
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$REPO/src/Tasker.Cli/Tasker.Cli.csproj" ] || fail "cannot find src/Tasker.Cli in $REPO"
command -v dotnet >/dev/null 2>&1 || fail ".NET SDK 10 is required to build: https://dotnet.microsoft.com/download"

# ---- версия ----

# Номер релиза: x.y.z или x.y.z-метка (SemVer без build metadata).
valid_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]
}

FILE_VERSION="$(tr -d '[:space:]' < "$REPO/VERSION" 2>/dev/null || true)"
VERSION="${VERSION_OVERRIDE:-$FILE_VERSION}"
[ -n "$VERSION" ] || fail "no version: put it in the VERSION file or pass --version"
valid_version "$VERSION" || fail "bad version '$VERSION': expected x.y.z or x.y.z-label (for example 0.1.0 or 0.2.0-rc.1)"

COMMIT="$(git -C "$REPO" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
if [ "$COMMIT" != unknown ] && [ -n "$(git -C "$REPO" status --porcelain 2>/dev/null)" ]; then
  COMMIT="$COMMIT-dirty"
fi

# Тег на текущем коммите должен совпадать с номером: иначе выпустят архив «0.1.0» из коммита с тегом v0.2.0.
if TAGS="$(git -C "$REPO" tag --points-at HEAD 2>/dev/null | grep -E '^v[0-9]' || true)" && [ -n "$TAGS" ] && [ -z "$VERSION_OVERRIDE" ]; then
  echo "$TAGS" | grep -qxF "v$VERSION" || fail "the commit is tagged $(echo "$TAGS" | tr '\n' ' ')but the VERSION file says $VERSION: fix one of them"
fi

[ -n "$OUT" ] || OUT="$REPO/artifacts/release/$VERSION"
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac

# ---- вспомогательное ----

# Хэш SHA256 файла $1 (строчными буквами).
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

# Платформа, на которой запускается эта машина (чтобы проверить запуском собранное для неё).
host_rid() {
  local os arch
  case "$(uname -s)" in Darwin) os=osx ;; Linux) os=linux ;; *) return 0 ;; esac
  case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) return 0 ;; esac
  echo "$os-$arch"
}

# Архив $2 из каталога $1 (внутри него — одна папка bundle): tar.gz без атрибутов macOS и владельца сборочной машины, zip для Windows.
pack() {
  local parent=$1 name=$2 archive=$3
  case "$archive" in
    *.zip)
      if command -v zip >/dev/null 2>&1; then
        (cd "$parent" && zip -qr -X "$archive" "$name")
      else
        python3 - "$parent" "$name" "$archive" <<'PY'
import os, sys, zipfile
parent, name, archive = sys.argv[1:4]
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(os.path.join(parent, name)):
        for f in sorted(files):
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, parent))
PY
      fi
      ;;
    *)
      local flags=()
      if tar --version 2>/dev/null | grep -q bsdtar; then
        flags=(--no-xattrs --uid 0 --gid 0 --uname root --gname root)
      else
        flags=(--owner=0 --group=0 --numeric-owner)
      fi
      COPYFILE_DISABLE=1 tar "${flags[@]}" -czf "$archive" -C "$parent" "$name"
      ;;
  esac
}

# ---- сборка ----

R2R_ARGS=(-p:PublishReadyToRun=false)
[ "$R2R" = true ] && R2R_ARGS=(-p:PublishReadyToRun=true)
HOST="$(host_rid)"

mkdir -p "$OUT"
WORK="$OUT/work"
rm -rf "$WORK"
mkdir -p "$WORK"
SUMS="$OUT/SHA256SUMS"
: > "$SUMS"

echo "Tasker release $VERSION ($COMMIT), platforms: ${RIDS[*]}"
echo "Output: $OUT"

BUILT=()
for rid in "${RIDS[@]}"; do
  case "$rid" in win-*) EXE=".exe"; EXT="zip"; INSTALLER="install.ps1" ;; *) EXE=""; EXT="tar.gz"; INSTALLER="install.sh" ;; esac
  NAME="tasker-$VERSION-$rid"
  BUNDLE="$WORK/$rid/$NAME"
  APP="$BUNDLE/app"
  mkdir -p "$APP"

  echo
  echo "== $rid: publishing tasker..."
  # Общие параметры: Release, самодостаточно, без отладочных символов и без языковых ресурсов сторонних библиотек.
  COMMON=(-c Release -r "$rid" --self-contained true "${R2R_ARGS[@]}" -p:DebugType=none -p:DebugSymbols=false
          -p:SatelliteResourceLanguages=en -p:TaskerVersion="$VERSION" -o "$APP" --nologo -v quiet --disable-build-servers)
  dotnet publish "$REPO/src/Tasker.Cli" "${COMMON[@]}" || fail "$rid: the tasker build failed (see the output above)"
  echo "== $rid: publishing tasker-mcpd..."
  # Демон — отдельная программа с ASP.NET Core; общие библиотеки в одном каталоге совпадают и не дублируются. Фронтенд ему не нужен.
  dotnet publish "$REPO/src/Tasker.Daemon.Host" "${COMMON[@]}" -p:EmbedFrontend=false \
    || fail "$rid: the tasker-mcpd build failed (see the output above)"

  [ -f "$APP/tasker$EXE" ] || fail "$rid: the build did not produce tasker$EXE"
  [ -f "$APP/tasker-mcpd$EXE" ] || fail "$rid: the build did not produce tasker-mcpd$EXE"
  chmod +x "$APP/tasker$EXE" "$APP/tasker-mcpd$EXE" 2>/dev/null || true

  # Платформа этой машины: собранное запускаем и проверяем номер версии (остальные платформы проверить запуском нечем).
  if [ "$rid" = "$HOST" ]; then
    SHOWN="$("$APP/tasker" --version 2>&1 || true)"
    [ "$SHOWN" = "$VERSION" ] || fail "$rid: 'tasker --version' prints '$SHOWN', expected '$VERSION'"
    echo "== $rid: smoke test passed (tasker --version = $SHOWN)"
  else
    echo "== $rid: built for another platform: not run here"
  fi

  cp "$REPO/scripts/$INSTALLER" "$BUNDLE/$INSTALLER"
  chmod +x "$BUNDLE/install.sh" 2>/dev/null || true
  {
    echo "version=$VERSION"
    echo "rid=$rid"
    echo "commit=$COMMIT"
    echo "built=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "readytorun=$R2R"
  } > "$BUNDLE/release.txt"

  SIZE="$(du -sh "$APP" | cut -f1)"
  if [ "$ARCHIVE" = true ]; then
    ARCHIVE_FILE="$OUT/$NAME.$EXT"
    rm -f "$ARCHIVE_FILE"
    pack "$WORK/$rid" "$NAME" "$ARCHIVE_FILE"
    HASH="$(sha256_of "$ARCHIVE_FILE")"
    echo "$HASH  $NAME.$EXT" > "$ARCHIVE_FILE.sha256"
    echo "$HASH  $NAME.$EXT" >> "$SUMS"
    echo "== $rid: $NAME.$EXT ($SIZE unpacked, $(du -h "$ARCHIVE_FILE" | cut -f1) packed)"
  else
    echo "== $rid: $BUNDLE ($SIZE)"
  fi
  BUILT+=("$rid")
done

if [ "$ARCHIVE" = true ]; then
  cp "$REPO/scripts/install.sh" "$REPO/scripts/install.ps1" "$OUT/"
  [ "$KEEP_WORK" = true ] || rm -rf "$WORK"
fi

echo
echo "Built: ${BUILT[*]}"
if [ "$ARCHIVE" = true ]; then
  echo "Checksums: $SUMS"
  cat "$SUMS"
fi
