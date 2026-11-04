#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package="$root/macos/TaskerAPI"
tool="$root/macos/Tools/OpenAPIGenerator"
out="$package/Sources/TaskerAPI/Generated/REST"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

python3 -I - "$root/docs/api/openapi.json" "$work/openapi.json" <<'PY'
import json
import sys

doc = json.load(open(sys.argv[1], encoding="utf-8"))
paths = {}
for path, item in doc["paths"].items():
    kept = {m: op for m, op in item.items() if not (isinstance(op, dict) and op.get("operationId") == "executeGraphQL")}
    if any(isinstance(op, dict) and "operationId" in op for op in kept.values()):
        paths[path] = kept
doc["paths"] = paths

schemas = doc["components"]["schemas"]
used = set()
pending = [json.dumps({"paths": paths, "security": doc["components"].get("securitySchemes")})]
while pending:
    text = pending.pop()
    for name in schemas:
        if name not in used and '"#/components/schemas/%s"' % name in text:
            used.add(name)
            pending.append(json.dumps(schemas[name]))
doc["components"]["schemas"] = {k: v for k, v in schemas.items() if k in used}
json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

swift build --package-path "$tool" -c release --product swift-openapi-generator >&2
"$(swift build --package-path "$tool" -c release --show-bin-path)/swift-openapi-generator" generate "$work/openapi.json" \
  --config "$package/openapi-generator-config.yaml" --output-directory "$work/out" >&2

if [ "${1:-}" = "--check" ]; then
  if ! diff -r "$work/out" "$out" >/dev/null; then
    echo "сгенерированный REST-клиент устарел: запустите scripts/gen-openapi-client.sh" >&2
    diff -r "$work/out" "$out" | head -40 >&2 || true
    exit 1
  fi
  echo "REST-клиент совпадает с docs/api/openapi.json"
else
  rm -rf "$out"
  mkdir -p "$out"
  cp "$work/out"/*.swift "$out/"
  echo "обновлено: ${out#$root/}"
fi
