#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root/macos"

command -v xcodegen >/dev/null || { echo "xcodegen не найден: brew install xcodegen" >&2; exit 1; }

export TASKER_VERSION="$(tr -d '[:space:]' < "$root/VERSION")"
export TASKER_BUILD="${TASKER_BUILD:-$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 1)}"

action="${1:-build}"
derived="$root/macos/build"

command -v dotnet >/dev/null || { echo "dotnet не найден: нужен .NET SDK, чтобы собрать демон tasker-mcpd" >&2; exit 1; }
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  *) rid=osx-x64 ;;
esac
PATH="/usr/bin:$PATH" dotnet publish "$root/dotnet/src/Tasker.Daemon.Host" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedFrontend=false \
  -p:TaskerVersion="$TASKER_VERSION" -o "$derived/daemon" --nologo -v quiet
chmod +x "$derived/daemon/tasker-mcpd"

xcodegen generate --quiet

case "$action" in
  generate) ;;
  build)
    xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Debug -destination 'platform=macOS' -derivedDataPath "$derived" build
    ;;
  release)
    xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Release -destination 'platform=macOS' -derivedDataPath "$derived" build
    ;;
  test)
    xcodebuild -project Tasker.xcodeproj -scheme Tasker -configuration Debug -destination 'platform=macOS' -derivedDataPath "$derived" test
    ;;
  run)
    "$0" build
    open "$derived/Build/Products/Debug/Tasker.app"
    ;;
  *)
    echo "использование: scripts/macos-build.sh [generate|build|release|test|run]" >&2
    exit 2
    ;;
esac
