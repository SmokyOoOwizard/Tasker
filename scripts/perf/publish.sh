#!/usr/bin/env bash
# Собирает tasker и tasker-mcpd (Rust, `cargo build --release` — профиль release из rust/Cargo.toml, как у scripts/release.sh) и
# кладёт обе программы в указанный каталог — но никуда не устанавливает. Нужен стенду scripts/perf/bench.py.
#   scripts/perf/publish.sh <каталог>
set -euo pipefail
OUT="${1:?usage: publish.sh <output dir>}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
command -v cargo >/dev/null 2>&1 || { echo "Rust (cargo) is required: https://rustup.rs" >&2; exit 1; }
(cd "$REPO/rust" && cargo build --release --locked -p tasker-cli -p tasker-mcpd)
TARGET="${CARGO_TARGET_DIR:-target}"
case "$TARGET" in /*) ;; *) TARGET="$REPO/rust/$TARGET" ;; esac
mkdir -p "$OUT"
cp "$TARGET/release/tasker" "$TARGET/release/tasker-mcpd" "$OUT/"
echo "published to $OUT"
