#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root/macos"

command -v xcodegen >/dev/null || { echo "xcodegen не найден: brew install xcodegen" >&2; exit 1; }

export TASKER_VERSION="${TASKER_VERSION:-$(tr -d '[:space:]' < "$root/VERSION")}"
export TASKER_BUILD="${TASKER_BUILD:-$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 1)}"

action="${1:-build}"
derived="$root/macos/build"

command -v dotnet >/dev/null || { echo "dotnet не найден: нужен .NET SDK, чтобы собрать консоль tasker и демон tasker-mcpd" >&2; exit 1; }
rid="${TASKER_RID:-}"
if [ -z "$rid" ]; then
  case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    *) rid=osx-x64 ;;
  esac
fi
case "$rid" in
  osx-arm64) arch=arm64 ;;
  osx-x64) arch=x86_64 ;;
  *) echo "неизвестная платформа: $rid (osx-arm64, osx-x64)" >&2; exit 2 ;;
esac
PATH="/usr/bin:$PATH" dotnet publish "$root/dotnet/src/Tasker.Cli" -c Release -r "$rid" -p:PublishAot=true \
  -p:TaskerVersion="$TASKER_VERSION" -o "$derived/cli" --nologo -v quiet
PATH="/usr/bin:$PATH" dotnet publish "$root/dotnet/src/Tasker.Daemon.Host" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedFrontend=false \
  -p:TaskerVersion="$TASKER_VERSION" -o "$derived/daemon" --nologo -v quiet
chmod +x "$derived/cli/tasker" "$derived/daemon/tasker-mcpd"
/usr/bin/codesign --force --sign - "$derived/cli/tasker" "$derived/daemon/tasker-mcpd"

xcodegen generate --quiet

release_build() {
  xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Release -destination 'generic/platform=macOS' \
    -derivedDataPath "$derived" ARCHS="$arch" ONLY_ACTIVE_ARCH=NO build
}

check_parts() {
  local app="$derived/Build/Products/Release/Tasker.app" shown
  [ -x "$app/Contents/Helpers/tasker" ] && [ -x "$app/Contents/Helpers/tasker-mcpd" ] \
    || { echo "в пакете нет консоли или демона" >&2; exit 1; }
  if [ "$rid" = osx-x64 ] && [ "$(uname -m)" = arm64 ] && ! arch -x86_64 /usr/bin/true 2>/dev/null; then
    return 0
  fi
  shown="$("$app/Contents/Helpers/tasker" --version)"
  [ "$shown" = "$TASKER_VERSION" ] || { echo "версия консоли $shown, версия приложения $TASKER_VERSION" >&2; exit 1; }
  "$app/Contents/Helpers/tasker-mcpd" --help >/dev/null || { echo "демон из пакета не запускается" >&2; exit 1; }
}

case "$action" in
  generate) ;;
  build)
    xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Debug -destination 'platform=macOS' -derivedDataPath "$derived" build
    ;;
  release)
    release_build
    check_parts
    ;;
  package)
    release_build
    check_parts
    out="${TASKER_PACKAGE_DIR:-$root/artifacts/app/$TASKER_VERSION}"
    mkdir -p "$out"
    name="Tasker-$TASKER_VERSION-$rid.zip"
    rm -f "$out/$name" "$out/$name.sha256"
    /usr/bin/ditto -c -k --keepParent "$derived/Build/Products/Release/Tasker.app" "$out/$name"
    (cd "$out" && shasum -a 256 "$name" > "$name.sha256")
    echo "$out/$name"
    ;;
  test)
    xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Debug -destination 'platform=macOS' -derivedDataPath "$derived" test
    ;;
  run)
    "$0" build
    open "$derived/Build/Products/Debug/Tasker.app"
    ;;
  *)
    echo "использование: scripts/macos-build.sh [generate|build|release|package|test|run]" >&2
    exit 2
    ;;
esac
