#!/usr/bin/env bash
set -euo pipefail
OUT="${1:?usage: publish-dotnet.sh <output dir> r2r|aot [extra msbuild args...]}"
KIND="${2:?usage: publish-dotnet.sh <output dir> r2r|aot [extra msbuild args...]}"
shift 2
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
case "$(uname -s)-$(uname -m)" in Darwin-arm64) RID=osx-arm64 ;; Darwin-x86_64) RID=osx-x64 ;; *) RID=linux-x64 ;; esac
export PATH="/usr/bin:$PATH"
case "$KIND" in
  r2r) MODE=(--self-contained true -p:PublishReadyToRun=true) ;;
  aot) MODE=(-p:PublishAot=true) ;;
  *) echo "unknown kind: $KIND" >&2; exit 1 ;;
esac
mkdir -p "$OUT"
for P in Tasker.Cli Tasker.Daemon.Host; do
  rm -rf "$REPO/dotnet/src/$P/bin/Release/net10.0/$RID" "$REPO/dotnet/src/$P/obj/Release/net10.0/$RID"
  dotnet publish "$REPO/dotnet/src/$P" -c Release -r "$RID" "${MODE[@]}" -p:EmbedFrontend=false "$@" \
    -o "$OUT" --nologo -v quiet --disable-build-servers
done
rm -rf "$OUT"/*.dSYM "$OUT"/*.pdb
echo "published to $OUT"
