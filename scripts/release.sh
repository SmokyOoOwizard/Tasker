#!/bin/bash
# Собирает релиз Tasker: консольную утилиту tasker и демон MCP (tasker-mcpd) — Rust-бинарники из rust/ (cargo, профиль release
# из rust/Cargo.toml) — под выбранные платформы, упаковывает их в архивы и считает контрольные суммы SHA256. Ничего не устанавливает.
#
#   scripts/release.sh [--rid RID[,RID...]|local|all] [--version X.Y.Z] [--out DIR] [--builder auto|cargo|zigbuild|docker]
#                      [--glibc X.Y] [--no-archive] [--keep-work]
#
# Платформы (RID, имена архивов те же, что у прежних .NET-релизов — на них опираются установщики) и цели Rust:
#   osx-arm64 aarch64-apple-darwin          osx-x64 x86_64-apple-darwin
#   linux-x64 x86_64-unknown-linux-gnu      linux-arm64 aarch64-unknown-linux-gnu        (glibc не новее --glibc, по умолчанию 2.28)
#   linux-musl-x64 x86_64-unknown-linux-musl  linux-musl-arm64 aarch64-unknown-linux-musl  (статические: Alpine и любой Linux)
#   win-x64 x86_64-pc-windows-msvc          win-arm64 aarch64-pc-windows-msvc
# По умолчанию (local) — все платформы, которые можно собрать на этой машине (остальные перечисляются с причиной); all — все
# восемь, и если какую-то здесь не собрать, скрипт останавливается до сборки.
#
# Чем собирается платформа (--builder auto, по умолчанию; можно задать и переменной TASKER_RELEASE_BUILDER):
#   cargo      — `cargo build --release --target T`: цель этой машины, а также другие цели той же ОС (macOS arm64 <-> x64, Windows
#                x64 <-> arm64), если для них установлена стандартная библиотека (`rustup target add T`);
#   zigbuild   — `cargo zigbuild` (нужны cargo-zigbuild, zig и `rustup target add T`): Linux с любой машины, glibc закрепляется --glibc;
#   docker     — Linux в контейнере (образ tasker-release-builder: rust:1-bookworm + цели Linux + zig + cargo-zigbuild, собирается
#                один раз; кэш cargo и target — в томах Docker). Нужен, когда на машине нет rustup (например, Rust из Homebrew).
#   macOS и Windows собираются только на своей ОС; все платформы сразу — workflow .github/workflows/release-build.yml.
#
# Версия — файл VERSION в корне репозитория (0.1.0, 0.2.0-rc.1): её показывает `tasker --version` (rust/crates/tasker-version,
# build.rs; скрипт передаёт её сборке через TASKER_VERSION). Если текущий коммит помечен тегом v*, тег должен совпасть с файлом
# (иначе ошибка). --version задаёт номер вручную (для пробной сборки).
#
# Результат (по умолчанию artifacts/release/<версия>/):
#   tasker-<версия>-<rid>.tar.gz | .zip (Windows)   архив: каталог tasker-<версия>-<rid>/ с app/ (tasker и tasker-mcpd, .exe на Windows),
#                                                   install.sh (install.ps1 для Windows) и release.txt (версия, платформа, коммит)
#   tasker-<версия>-<rid>.<расширение>.sha256       контрольная сумма архива
#   SHA256SUMS                                      все контрольные суммы одним файлом (проверка: shasum -a 256 -c SHA256SUMS)
#   install.sh, install.ps1                         скрипты установки отдельно (чтобы запускать их, не распаковывая архив)

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: scripts/release.sh [options]

Builds tasker + tasker-mcpd (Rust, cargo) for the chosen platforms and packs them into archives with SHA256 checksums.

Options:
  --rid LIST       comma-separated RIDs, 'local' (default: every platform this machine can build) or 'all' (all of them):
                   osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 win-x64 win-arm64
  --version V      release version (default: the VERSION file; a git tag v* on HEAD must match it)
  --out DIR        output directory (default: artifacts/release/<version> in the repository)
  --builder B      auto (default), cargo, zigbuild or docker: how the platforms are built (see the comment at the top)
  --glibc X.Y      oldest glibc the Linux builds run with (zigbuild and docker; default 2.28)
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
GLIBC=2.28
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
    --glibc)
      [ $# -ge 2 ] || { echo "Error: --glibc needs a version" >&2; usage >&2; exit 2; }
      GLIBC="$2"
      shift 2
      ;;
    --no-r2r) echo "Warning: --no-r2r is ignored: ReadyToRun was a .NET option, the release is built with cargo now" >&2; shift ;;
    --no-archive) ARCHIVE=false; shift ;;
    --keep-work) KEEP_WORK=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

case "$BUILDER" in auto|cargo|zigbuild|docker) ;; *) echo "Error: unknown builder '$BUILDER' (auto, cargo, zigbuild, docker)" >&2; exit 2 ;; esac
[[ "$GLIBC" =~ ^2\.[0-9]+$ ]] || { echo "Error: bad glibc version '$GLIBC' (for example 2.28)" >&2; exit 2; }

for rid in "${RIDS[@]+"${RIDS[@]}"}"; do
  known=false
  for candidate in "${ALL_RIDS[@]}"; do [ "$rid" = "$candidate" ] && known=true; done
  [ "$known" = true ] || { echo "Error: unknown platform '$rid' (known: ${ALL_RIDS[*]})" >&2; exit 2; }
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORKSPACE="$REPO/rust"
[ -f "$WORKSPACE/Cargo.toml" ] || fail "cannot find the Rust workspace rust/Cargo.toml in $REPO"

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

target_of() {
  case "$1" in
    osx-arm64) echo aarch64-apple-darwin ;;
    osx-x64) echo x86_64-apple-darwin ;;
    linux-x64) echo x86_64-unknown-linux-gnu ;;
    linux-arm64) echo aarch64-unknown-linux-gnu ;;
    linux-musl-x64) echo x86_64-unknown-linux-musl ;;
    linux-musl-arm64) echo aarch64-unknown-linux-musl ;;
    win-x64) echo x86_64-pc-windows-msvc ;;
    win-arm64) echo aarch64-pc-windows-msvc ;;
  esac
}

HAVE_CARGO=false
HOST_TARGET=""
if command -v cargo >/dev/null 2>&1 && command -v rustc >/dev/null 2>&1; then
  HAVE_CARGO=true
  HOST_TARGET="$(rustc -vV | sed -n 's/^host: //p')"
fi

case "$(uname -s)" in
  Darwin) HOST_OS=macos ;;
  Linux) HOST_OS=linux ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT) HOST_OS=windows ;;
  *) HOST_OS=other ;;
esac

os_of() {
  case "$1" in osx-*) echo macos ;; linux-*) echo linux ;; win-*) echo windows ;; esac
}

# Стандартная библиотека Rust для цели $1 установлена (rustup target add или цель этой машины).
has_std() {
  [ "$HAVE_CARGO" = true ] || return 1
  [ "$1" = "$HOST_TARGET" ] && return 0
  local dir
  dir="$(rustc --print target-libdir --target "$1" 2>/dev/null)" || return 1
  ls "$dir"/libstd-*.rlib >/dev/null 2>&1
}

have_zigbuild() {
  [ "$HAVE_CARGO" = true ] && cargo zigbuild --help >/dev/null 2>&1 && { command -v zig >/dev/null 2>&1 || python3 -m ziglang version >/dev/null 2>&1; }
}

have_docker() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

# Чем собрать платформу $1: печатает cargo, zigbuild или docker; не выйдет — печатает причину, код 1.
builder_for() {
  local rid=$1 target os
  target="$(target_of "$rid")"
  os="$(os_of "$rid")"
  case "$BUILDER" in
    cargo)
      if [ "$HAVE_CARGO" != true ]; then echo "cargo is not installed"; return 1; fi
      if [ "$os" != "$HOST_OS" ]; then echo "cargo builds $os targets on $os only (try --builder zigbuild or docker for Linux)"; return 1; fi
      if ! has_std "$target"; then echo "no Rust standard library for $target: rustup target add $target"; return 1; fi
      echo cargo; return 0 ;;
    zigbuild)
      if [ "$os" != linux ]; then echo "zigbuild is used for Linux targets only"; return 1; fi
      if ! have_zigbuild; then echo "cargo-zigbuild or zig is not installed (cargo install cargo-zigbuild; zig: https://ziglang.org)"; return 1; fi
      if ! has_std "$target"; then echo "no Rust standard library for $target: rustup target add $target"; return 1; fi
      echo zigbuild; return 0 ;;
    docker)
      if [ "$os" != linux ]; then echo "docker builds Linux targets only"; return 1; fi
      if ! have_docker; then echo "docker is not available"; return 1; fi
      echo docker; return 0 ;;
  esac
  # auto
  if [ "$os" = "$HOST_OS" ] && [ "$os" != linux ] && has_std "$target"; then
    echo cargo; return 0
  fi
  if [ "$os" = linux ]; then
    # Linux: zigbuild закрепляет glibc (--glibc); без него — своя цель этой машины обычным cargo (glibc этой машины) или Docker.
    if have_zigbuild && has_std "$target"; then echo zigbuild; return 0; fi
    if [ "$HOST_OS" = linux ] && [ "$target" = "$HOST_TARGET" ]; then echo cargo; return 0; fi
    if have_docker; then echo docker; return 0; fi
    echo "needs cargo-zigbuild with zig and 'rustup target add $target', or docker"
    return 1
  fi
  case "$os" in
    macos) echo "macOS builds are made on macOS$([ "$HOST_OS" = macos ] && echo " (rustup target add $target)") or in CI (.github/workflows/release-build.yml)" ;;
    windows) echo "Windows builds are made on Windows with the MSVC toolchain$([ "$HOST_OS" = windows ] && echo " (rustup target add $target)") or in CI (.github/workflows/release-build.yml)" ;;
  esac
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
[ "${#PLAN_RIDS[@]}" -gt 0 ] || fail "none of the platforms can be built on this machine (install Rust: https://rustup.rs)"

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
  case "$HOST_OS" in macos) os=osx ;; linux) os=linux ;; windows) os=win ;; *) return 0 ;; esac
  case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) return 0 ;; esac
  echo "$os-$arch"
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

# Сборка в контейнере: образ с целями Linux, zig и cargo-zigbuild (один раз), тома с кэшем cargo и каталогом target.
DOCKER_IMAGE="${TASKER_RELEASE_IMAGE:-tasker-release-builder}"
docker_image() {
  docker image inspect "$DOCKER_IMAGE" >/dev/null 2>&1 && return 0
  echo "== building the Docker image $DOCKER_IMAGE (once)..."
  docker build -t "$DOCKER_IMAGE" - <<'DOCKERFILE'
FROM rust:1-bookworm
RUN rustup target add x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu x86_64-unknown-linux-musl aarch64-unknown-linux-musl \
 && apt-get update && apt-get install -y --no-install-recommends python3-pip && rm -rf /var/lib/apt/lists/* \
 && pip3 install --break-system-packages --no-cache-dir ziglang==0.13.0.post1 \
 && cargo install --locked cargo-zigbuild@0.20.1 && rm -rf /usr/local/cargo/registry
DOCKERFILE
}

# Собирает цель $1 сборщиком $2 и копирует tasker и tasker-mcpd (с расширением $3) в каталог $4.
build_target() {
  local target=$1 how=$2 exe=$3 dest=$4 zig_target=$1
  case "$target" in *-linux-gnu) zig_target="$target.$GLIBC" ;; esac
  case "$how" in
    cargo)
      (cd "$WORKSPACE" && TASKER_VERSION="$VERSION" cargo build --release --locked --target "$target" -p tasker-cli -p tasker-mcpd) \
        || fail "$target: the build failed (see the output above)"
      cp "$WORKSPACE/target/$target/release/tasker$exe" "$WORKSPACE/target/$target/release/tasker-mcpd$exe" "$dest/"
      ;;
    zigbuild)
      (cd "$WORKSPACE" && TASKER_VERSION="$VERSION" cargo zigbuild --release --locked --target "$zig_target" -p tasker-cli -p tasker-mcpd) \
        || fail "$target: the build failed (see the output above)"
      cp "$WORKSPACE/target/$target/release/tasker$exe" "$WORKSPACE/target/$target/release/tasker-mcpd$exe" "$dest/"
      ;;
    docker)
      docker_image || fail "cannot build the Docker image $DOCKER_IMAGE"
      docker run --rm -v "$REPO":/src:ro -v "$dest":/out -v tasker-release-cargo:/usr/local/cargo/registry -v tasker-release-target:/target \
        -e CARGO_TARGET_DIR=/target -e TASKER_VERSION="$VERSION" -w /src/rust "$DOCKER_IMAGE" bash -c "
          cargo zigbuild --release --locked --target '$zig_target' -p tasker-cli -p tasker-mcpd &&
          cp /target/$target/release/tasker /target/$target/release/tasker-mcpd /out/ &&
          chown $(id -u):$(id -g) /out/tasker /out/tasker-mcpd" \
        || fail "$target: the build in Docker failed (see the output above)"
      ;;
  esac
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
  target="$(target_of "$rid")"
  case "$rid" in win-*) EXE=".exe"; EXT="zip"; INSTALLER="install.ps1" ;; *) EXE=""; EXT="tar.gz"; INSTALLER="install.sh" ;; esac
  NAME="tasker-$VERSION-$rid"
  BUNDLE="$WORK/$rid/$NAME"
  APP="$BUNDLE/app"
  mkdir -p "$APP"

  echo
  echo "== $rid: building $target ($how)..."
  build_target "$target" "$how" "$EXE" "$APP"

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
    echo "target=$target"
    echo "commit=$COMMIT"
    echo "built=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    [ "$how" = cargo ] || [ "${target%-gnu}" = "$target" ] || echo "glibc=$GLIBC"
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
