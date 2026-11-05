#!/usr/bin/env python3
import html
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHORTCUTS = ROOT / "design" / "shortcuts.json"
STRINGS = ROOT / "design" / "strings.json"
SWIFT_OUT = ROOT / "macos" / "Sources" / "Tasker" / "Generated" / "ShortcutTable.swift"
HELP_ROOT = ROOT / "macos" / "Resources" / "Tasker.help"
LANGS = ("ru", "en")
HELP_NAME = "com.technotronika.tasker.help"


def camel(key):
    parts = key.split(".")
    return parts[0] + "".join(p[:1].upper() + p[1:] for p in parts[1:])


def swift_string(text):
    return json.dumps(text, ensure_ascii=False)


def build_swift(groups):
    lines = [
        "enum ShortcutTable {",
        "    struct Row: Sendable {",
        "        let title: L10nKey",
        "        let keys: [String]",
        "        let command: String?",
        "    }",
        "",
        "    struct Group: Sendable {",
        "        let title: L10nKey",
        "        let rows: [Row]",
        "    }",
        "",
        "    static let groups: [Group] = [",
    ]
    for group in groups:
        lines.append("        Group(title: ." + camel(group["title"]) + ", rows: [")
        for row in group["rows"]:
            keys = ", ".join(swift_string(k) for k in row["keys"].split())
            command = swift_string(row["command"]) if row.get("command") else "nil"
            lines.append(f"            Row(title: .{camel(row['title'])}, keys: [{keys}], command: {command}),")
        lines.append("        ]),")
    lines += ["    ]", "}", ""]
    return "\n".join(lines)


def build_page(lang, groups, strings):
    t = lambda key: html.escape(strings[key][lang])
    body = [f"<h1>{t('help.title')}</h1>", f"<p>{t('help.intro')}</p>"]
    for group in groups:
        body.append(f"<h2>{t(group['title'])}</h2>")
        body.append("<table>")
        for row in group["rows"]:
            keys = " ".join(f"<kbd>{html.escape(k)}</kbd>" for k in row["keys"].split())
            body.append(f"<tr><td>{t(row['title'])}</td><td class=\"keys\">{keys}</td></tr>")
        body.append("</table>")
    body.append(f"<p class=\"note\">{t('help.note')}</p>")
    body.append(f"<p class=\"note\">{t('help.settings')}</p>")
    title = t("help.title")
    return (
        "<!DOCTYPE html>\n"
        f"<html lang=\"{lang}\">\n<head>\n<meta charset=\"utf-8\">\n"
        f"<title>{title}</title>\n"
        f"<meta name=\"AppleTitle\" content=\"{title}\">\n"
        "<meta name=\"robots\" content=\"anchors\">\n"
        "<link rel=\"stylesheet\" href=\"../shrd/style.css\">\n</head>\n<body>\n"
        + "\n".join(body)
        + "\n</body>\n</html>\n"
    )


STYLE = """body{font:13px -apple-system,Helvetica,sans-serif;margin:24px;max-width:620px;color:#222}
h1{font-size:22px}
h2{font-size:15px;margin-top:22px}
table{border-collapse:collapse;width:100%}
td{padding:5px 0;border-bottom:1px solid #ddd}
td.keys{text-align:right;white-space:nowrap}
kbd{font:12px ui-monospace,Menlo,monospace;border:1px solid #bbb;border-bottom-width:2px;border-radius:5px;padding:0 5px;background:#f6f6f6}
.note{color:#666}
@media (prefers-color-scheme:dark){body{background:#1e1e1e;color:#ddd}td{border-color:#444}kbd{background:#2c2c2c;border-color:#555}.note{color:#999}}
"""

INFO = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>ru</string>
	<key>CFBundleIdentifier</key>
	<string>{name}</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>CFBundleName</key>
	<string>Tasker</string>
	<key>CFBundlePackageType</key>
	<string>BNDL</string>
	<key>CFBundleSignature</key>
	<string>hbwr</string>
	<key>CFBundleShortVersionString</key>
	<string>1</string>
	<key>CFBundleVersion</key>
	<string>1</string>
	<key>HPDBookAccessPath</key>
	<string>index.html</string>
	<key>HPDBookTitle</key>
	<string>Tasker</string>
	<key>HPDBookType</key>
	<string>3</string>
</dict>
</plist>
"""


def main():
    groups = json.loads(SHORTCUTS.read_text(encoding="utf-8"))["groups"]
    strings = json.loads(STRINGS.read_text(encoding="utf-8"))["strings"]
    needed = {"help.title", "help.intro", "help.note", "help.settings"}
    for group in groups:
        needed.add(group["title"])
        needed.update(row["title"] for row in group["rows"])
    missing = sorted(k for k in needed if k not in strings)
    if missing:
        print("нет строк в design/strings.json: " + ", ".join(missing), file=sys.stderr)
        return 1
    outputs = {SWIFT_OUT: build_swift(groups), HELP_ROOT / "Contents" / "Info.plist": INFO.replace("{name}", HELP_NAME),
               HELP_ROOT / "Contents" / "Resources" / "shrd" / "style.css": STYLE}
    for lang in LANGS:
        outputs[HELP_ROOT / "Contents" / "Resources" / f"{lang}.lproj" / "index.html"] = build_page(lang, groups, strings)
    for path, text in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    print(f"создано файлов: {len(outputs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
