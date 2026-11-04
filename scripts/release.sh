#!/bin/bash
# Собирает релиз Tasker: консольную утилиту tasker (.NET NativeAOT, dotnet/src/Tasker.Cli) и демон MCP tasker-mcpd (.NET, один файл
# с рантаймом, dotnet/src/Tasker.Daemon.Host) — под выбранные платформы, упаковывает их в архивы и считает контрольные суммы SHA256.
# Ничего не устанавливает.
#
#   scripts/release.sh [--rid RID[,RID...]|local|all] [--version X.Y.Z] [--out DIR] [--builder auto|dotnet|docker]
#                      [--no-archive] [--keep-work]
#
# Платформы (RID — имя платформы в имени архива, на него опираются установщики):
#   osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 win-x64 win-arm64
# По умолчанию (local) — все платформы, которые можно собрать на этой машине (остальные перечисляются с причиной); all — все
# восемь, и если какую-то здесь не собрать, скрипт останавливается до сборки.
#
# Чем собирается платформа (--builder auto, по умолчанию; можно задать и переменной TASKER_RELEASE_BUILDER):
#   dotnet — `dotnet publish` на этой машине: NativeAOT собирает только свою ОС; на macOS и Windows — обе архитектуры, на Linux —
#            архитектуру этой машины с её libc (glibc или musl);
#   docker — Linux musl в контейнере mcr.microsoft.com/dotnet/sdk:10.0-alpine той же архитектуры, что и машина.
#   Все платформы сразу — workflow .github/workflows/release-build.yml (каждая на машине своей ОС и архитектуры).
#
# Версия — файл VERSION в корне репозитория (0.1.0, 0.2.0-rc.1): её показывает `tasker --version`. Если текущий коммит помечен
# тегом v*, тег должен совпасть с файлом (иначе ошибка). --version задаёт номер вручную (для пробной сборки).
#
# Результат (по умолчанию artifacts/release/<версия>/):
#   tasker-<версия>-<rid>.tar.gz | .zip (Windows)   архив: каталог tasker-<версия>-<rid>/ с app/ (tasker и tasker-mcpd, .exe на Windows,
#                                                   и нативные библиотеки рядом, если они нужны), install.sh (install.ps1 для Windows)
#                                                   и release.txt (версия, платформа, коммит)
#   tasker-<версия>-<rid>.<расширение>.sha256       контрольная сумма архива
#   SHA256SUMS                                      все контрольные суммы одним файлом (проверка: shasum -a 256 -c SHA256SUMS)
#   install.sh, install.ps1                         скрипты установки отдельно (чтобы запускать их, не распаковывая архив)

set -euo pipefail
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

usage() {
  cat <<'USAGE'
Usage: scripts/release.sh [options]

Builds tasker (.NET NativeAOT) + tasker-mcpd (.NET single file) for the chosen platforms and packs them into archives with SHA256
checksums.

Options:
  --rid LIST       comma-separated RIDs, 'local' (default: every platform this machine can build) or 'all' (all of them):
                   osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 win-x64 win-arm64
  --version V      release version (default: the VERSION file; a git tag v* on HEAD must match it)
  --out DIR        output directory (default: artifacts/release/<version> in the repository)
  --builder B      auto (default), dotnet or docker: how the platforms are built (see the comment at the top)
  --no-archive     build only (DIR/work/<rid>/tasker-<version>-<rid>/), no archives
  --keep-work      keep the intermediate folders next to the archives
  -h, --help       show this help
USAGE
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

ALL_RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 win-x64 win-arm64)
RID_MODE=local
RIDS=()
VERSION_OVERRIDE=""
OUT=""
BUILDER="${TASKER_RELEASE_BUILDER:-auto}"
ARCHIVE=true
KEEP_WORK=false

while [ $# -gt 0 ]; do
  case "$1" in
    --rid)
      [ $# -ge 2 ] || { echo "Error: --rid needs a list" >&2; usage >&2; exit 2; }
      case "$2" in
        all|local) RID_MODE="$2"; RIDS=() ;;
        *) RID_MODE=list; IFS=',' read -r -a RIDS <<< "$2" ;;
      esac
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
    --builder)
      [ $# -ge 2 ] || { echo "Error: --builder needs a name" >&2; usage >&2; exit 2; }
      BUILDER="$2"
      shift 2
      ;;
    --no-archive) ARCHIVE=false; shift ;;
    --keep-work) KEEP_WORK=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

case "$BUILDER" in auto|dotnet|docker) ;; *) echo "Error: unknown builder '$BUILDER' (auto, dotnet, docker)" >&2; exit 2 ;; esac

for rid in "${RIDS[@]+"${RIDS[@]}"}"; do
  known=false
  for candidate in "${ALL_RIDS[@]}"; do [ "$rid" = "$candidate" ] && known=true; done
  [ "$known" = true ] || { echo "Error: unknown platform '$rid' (known: ${ALL_RIDS[*]})" >&2; exit 2; }
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$REPO/dotnet/src/Tasker.Cli/Tasker.Cli.csproj" ] || fail "cannot find dotnet/src/Tasker.Cli in $REPO"

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
case "$OUT" in /*|[A-Za-z]:[\\/]*) ;; *) OUT="$PWD/$OUT" ;; esac

# ---- платформы и чем их собирать ----

HAVE_DOTNET=false
command -v dotnet >/dev/null 2>&1 && HAVE_DOTNET=true

case "$(uname -s)" in
  Darwin) HOST_OS=macos ;;
  Linux) HOST_OS=linux ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT) HOST_OS=windows ;;
  *) HOST_OS=other ;;
esac

case "$(uname -m)" in
  arm64|aarch64) HOST_ARCH=arm64 ;;
  x86_64|amd64) HOST_ARCH=x64 ;;
  *) HOST_ARCH=other ;;
esac

HOST_MUSL=false
if [ "$HOST_OS" = linux ]; then
  case "$(ldd --version 2>&1 || true)" in *musl*|*Musl*|*MUSL*) HOST_MUSL=true ;; esac
  if compgen -G '/lib/ld-musl-*' >/dev/null; then HOST_MUSL=true; fi
fi

os_of() {
  case "$1" in osx-*) echo macos ;; linux-*) echo linux ;; win-*) echo windows ;; esac
}

arch_of() {
  case "$1" in *-arm64) echo arm64 ;; *) echo x64 ;; esac
}

is_musl() {
  case "$1" in linux-musl-*) return 0 ;; *) return 1 ;; esac
}

have_docker() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

dotnet_can() {
  local rid=$1 os arch
  os="$(os_of "$rid")"
  arch="$(arch_of "$rid")"
  if [ "$HAVE_DOTNET" != true ]; then echo "the .NET SDK is not installed (https://dot.net)"; return 1; fi
  if [ "$os" != "$HOST_OS" ]; then echo "NativeAOT builds $os on $os only (or in CI: .github/workflows/release-build.yml)"; return 1; fi
  if [ "$os" = linux ]; then
    if [ "$arch" != "$HOST_ARCH" ]; then echo "Linux $arch is built on a Linux $arch machine (or in CI)"; return 1; fi
    if is_musl "$rid" && [ "$HOST_MUSL" != true ]; then echo "musl builds need a musl machine or --builder docker"; return 1; fi
    if ! is_musl "$rid" && [ "$HOST_MUSL" = true ]; then echo "glibc builds need a glibc machine"; return 1; fi
  fi
  return 0
}

# Чем собрать платформу $1: печатает dotnet или docker; не выйдет — печатает причину, код 1.
builder_for() {
  local rid=$1 why
  case "$BUILDER" in
    dotnet)
      why="$(dotnet_can "$rid")" || { echo "$why"; return 1; }
      echo dotnet; return 0 ;;
    docker)
      if ! is_musl "$rid"; then echo "docker builds Linux musl only"; return 1; fi
      if [ "$(arch_of "$rid")" != "$HOST_ARCH" ]; then echo "docker builds musl of the machine's architecture ($HOST_ARCH) only"; return 1; fi
      if ! have_docker; then echo "docker is not available"; return 1; fi
      echo docker; return 0 ;;
  esac
  if why="$(dotnet_can "$rid")"; then echo dotnet; return 0; fi
  if is_musl "$rid" && [ "$(arch_of "$rid")" = "$HOST_ARCH" ] && have_docker; then echo docker; return 0; fi
  echo "$why"
  return 1
}

PLAN_RIDS=()
PLAN_BUILDERS=()
SKIPPED=()
CANDIDATES=("${ALL_RIDS[@]}")
[ "$RID_MODE" = list ] && CANDIDATES=("${RIDS[@]}")
for rid in "${CANDIDATES[@]}"; do
  if how="$(builder_for "$rid")"; then
    PLAN_RIDS+=("$rid")
    PLAN_BUILDERS+=("$how")
  elif [ "$RID_MODE" = local ]; then
    SKIPPED+=("$rid: $how")
  else
    fail "$rid cannot be built on this machine: $how"
  fi
done
[ "${#PLAN_RIDS[@]}" -gt 0 ] || fail "none of the platforms can be built on this machine (install the .NET SDK: https://dot.net)"

# ---- вспомогательное ----

# Хэш SHA256 файла $1 (строчными буквами).
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

# Платформа этой машины (чтобы проверить запуском собранное для неё).
host_rid() {
  local os
  case "$HOST_OS" in macos) os=osx ;; linux) os=linux ;; windows) os=win ;; *) return 0 ;; esac
  [ "$HOST_ARCH" != other ] || return 0
  if [ "$HOST_MUSL" = true ]; then echo "linux-musl-$HOST_ARCH"; else echo "$os-$HOST_ARCH"; fi
}

PYTHON=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import zipfile' >/dev/null 2>&1; then PYTHON="$candidate"; break; fi
done

# Архив $3 из каталога $1/$2: tar.gz без атрибутов macOS и владельца сборочной машины, zip для Windows.
pack() {
  local parent=$1 name=$2 archive=$3
  case "$archive" in
    *.zip)
      if command -v zip >/dev/null 2>&1; then
        (cd "$parent" && zip -qr -X "$archive" "$name")
      elif [ -n "$PYTHON" ]; then
        "$PYTHON" - "$parent" "$name" "$archive" <<'PY'
import os, sys, zipfile
parent, name, archive = sys.argv[1:4]
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(os.path.join(parent, name)):
        dirs.sort()
        for f in sorted(files):
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, parent).replace(os.sep, "/"))
PY
      else
        fail "cannot make $archive: neither zip nor python is available"
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

publish() {
  local rid=$1 dest=$2 work=$3
  local cli="$REPO/dotnet/src/Tasker.Cli" daemon="$REPO/dotnet/src/Tasker.Daemon.Host"
  [ "$HOST_OS" = macos ] && export PATH="/usr/bin:$PATH"
  dotnet publish "$cli" -c Release -r "$rid" -p:PublishAot=true -p:TaskerVersion="$VERSION" \
    -o "$work/cli" --nologo -v quiet || fail "$rid: the build of tasker failed (see the output above)"
  dotnet publish "$daemon" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EmbedFrontend=false -p:TaskerVersion="$VERSION" -o "$work/daemon" --nologo -v quiet \
    || fail "$rid: the build of tasker-mcpd failed (see the output above)"
  copy_programs "$work/cli" "$dest"
  copy_programs "$work/daemon" "$dest"
}

copy_programs() {
  local from=$1 dest=$2 file
  for file in "$from"/*; do
    case "$file" in *.pdb|*.dbg|*.dSYM) continue ;; esac
    [ -f "$file" ] && cp -p "$file" "$dest/"
  done
}

publish_docker() {
  local rid=$1 dest=$2
  docker run --rm -v "$REPO":/src:ro -v "$dest":/out -v tasker-release-nuget:/root/.nuget/packages \
    -e TASKER_VERSION="$VERSION" -e RID="$rid" mcr.microsoft.com/dotnet/sdk:10.0-alpine sh -c '
      set -e
      apk add --no-cache clang build-base zlib-dev sqlite-dev sqlite-static >/dev/null
      cp -r /src /work
      dotnet publish /work/dotnet/src/Tasker.Cli -c Release -r "$RID" -p:PublishAot=true -p:TaskerVersion="$TASKER_VERSION" -o /tmp/cli --nologo -v quiet
      dotnet publish /work/dotnet/src/Tasker.Daemon.Host -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedFrontend=false -p:TaskerVersion="$TASKER_VERSION" -o /tmp/daemon --nologo -v quiet
      for f in /tmp/cli/* /tmp/daemon/*; do case "$f" in *.pdb|*.dbg) ;; *) [ -f "$f" ] && cp "$f" /out/ ;; esac; done
      chown -R '"$(id -u):$(id -g)"' /out' \
    || fail "$rid: the build in Docker failed (see the output above)"
}

# ---- сборка ----

HOST="$(host_rid)"

mkdir -p "$OUT"
WORK="$OUT/work"
rm -rf "$WORK"
mkdir -p "$WORK"
SUMS="$OUT/SHA256SUMS"
[ "$ARCHIVE" = true ] && : > "$SUMS"

echo "Tasker release $VERSION ($COMMIT), platforms: ${PLAN_RIDS[*]}"
for line in "${SKIPPED[@]+"${SKIPPED[@]}"}"; do echo "  skipped $line"; done
echo "Output: $OUT"

BUILT=()
SIZES=()
for i in "${!PLAN_RIDS[@]}"; do
  rid="${PLAN_RIDS[$i]}"
  how="${PLAN_BUILDERS[$i]}"
  case "$rid" in win-*) EXE=".exe"; EXT="zip"; INSTALLER="install.ps1" ;; *) EXE=""; EXT="tar.gz"; INSTALLER="install.sh" ;; esac
  NAME="tasker-$VERSION-$rid"
  BUNDLE="$WORK/$rid/$NAME"
  APP="$BUNDLE/app"
  mkdir -p "$APP"

  echo
  echo "== $rid: building ($how)..."
  if [ "$how" = docker ]; then
    publish_docker "$rid" "$APP"
  else
    publish "$rid" "$APP" "$WORK/$rid/publish"
    rm -rf "$WORK/$rid/publish"
  fi

  [ -f "$APP/tasker$EXE" ] || fail "$rid: the build did not produce tasker$EXE"
  [ -f "$APP/tasker-mcpd$EXE" ] || fail "$rid: the build did not produce tasker-mcpd$EXE"
  chmod +x "$APP/tasker$EXE" "$APP/tasker-mcpd$EXE" 2>/dev/null || true

  # Платформа этой машины: собранное запускаем и проверяем номер версии (остальные платформы проверить запуском нечем).
  if [ "$rid" = "$HOST" ]; then
    SHOWN="$("$APP/tasker$EXE" --version 2>&1 | tr -d '\r' || true)"
    [ "$SHOWN" = "$VERSION" ] || fail "$rid: 'tasker --version' prints '$SHOWN', expected '$VERSION'"
    "$APP/tasker-mcpd$EXE" --help >/dev/null 2>&1 || fail "$rid: 'tasker-mcpd --help' fails"
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
  } > "$BUNDLE/release.txt"

  SIZE="$(du -sh "$APP" | cut -f1)"
  if [ "$ARCHIVE" = true ]; then
    ARCHIVE_FILE="$OUT/$NAME.$EXT"
    rm -f "$ARCHIVE_FILE"
    pack "$WORK/$rid" "$NAME" "$ARCHIVE_FILE"
    HASH="$(sha256_of "$ARCHIVE_FILE")"
    echo "$HASH  $NAME.$EXT" > "$ARCHIVE_FILE.sha256"
    echo "$HASH  $NAME.$EXT" >> "$SUMS"
    PACKED="$(du -h "$ARCHIVE_FILE" | cut -f1)"
    echo "== $rid: $NAME.$EXT ($SIZE unpacked, $PACKED packed)"
    SIZES+=("$rid: $SIZE unpacked, $PACKED packed")
  else
    echo "== $rid: $BUNDLE ($SIZE)"
    SIZES+=("$rid: $SIZE")
  fi
  BUILT+=("$rid")
done

if [ "$ARCHIVE" = true ]; then
  cp "$REPO/scripts/install.sh" "$REPO/scripts/install.ps1" "$OUT/"
  [ "$KEEP_WORK" = true ] || rm -rf "$WORK"
fi

echo
echo "Built: ${BUILT[*]}"
for line in "${SIZES[@]}"; do echo "  $line"; done
for line in "${SKIPPED[@]+"${SKIPPED[@]}"}"; do echo "Not built here: $line"; done
if [ "$ARCHIVE" = true ]; then
  echo "Checksums: $SUMS"
  cat "$SUMS"
fi
