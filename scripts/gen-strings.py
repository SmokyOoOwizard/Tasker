#!/usr/bin/env python3
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "design" / "strings.json"
MOCKUP = ROOT / "docs" / "mockups" / "web" / "main-design.html"
SWIFT_SOURCES = ROOT / "macos" / "Sources" / "Tasker"
CATALOG_OUT = SWIFT_SOURCES / "Generated" / "Localizable.xcstrings"
KEYS_OUT = SWIFT_SOURCES / "Generated" / "L10nKey.swift"
ERRORS_SOURCE = ROOT / "docs" / "api" / "error-codes.json"
ERRORS_OUT = SWIFT_SOURCES / "Generated" / "ErrorCatalog.swift"
WEB_SOURCES = ROOT / "frontend" / "src"
WEB_OUT = WEB_SOURCES / "generated" / "strings.ts"
WEB_PREFIXES = ("web.", "error.")

LANGS = ("ru", "en")
PLURAL_FORMS = {"ru": 3, "en": 2}
PLURAL_CATEGORIES = {"ru": ("one", "few", "other"), "en": ("one", "other")}
PLURAL_RE = re.compile(r"\{(\d+)#([^}]*)\}")
ARG_RE = re.compile(r"\{(\d+)\}")
UI_CALL_RE = re.compile(r"(?<![\w.])(Text|Button|Label|Toggle|Picker|Window|Section|Menu|LabeledContent|Link|TextField|SettingsLink)\(\s*\"|\.(help|navigationTitle|accessibilityLabel|accessibilityHint|badge)\(\s*\"")
CYRILLIC_RE = re.compile(r"[Ѐ-ӿ]")


def camel(key):
    parts = key.split(".")
    return parts[0] + "".join(p[:1].upper() + p[1:] for p in parts[1:])


def load_source():
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    return data["strings"]


def load_mockup_en():
    lines = MOCKUP.read_text(encoding="utf-8").splitlines()
    start = next(i for i, l in enumerate(lines) if l.startswith("const EN={"))
    result = {}
    for line in lines[start + 1:]:
        if line.startswith("};"):
            break
        text = line.rstrip()
        if text.endswith(","):
            text = text[:-1]
        key, value = split_pair(text)
        result[key] = value
    return result


def split_pair(text):
    decoder = json.JSONDecoder()
    key, end = decoder.raw_decode(text)
    assert text[end] == ":"
    value, _ = decoder.raw_decode(text[end + 1:])
    return key, value


def placeholders(text):
    return sorted(set(ARG_RE.findall(text)) | set(m.group(1) for m in PLURAL_RE.finditer(text)))


def plural_args(text):
    return sorted(set(m.group(1) for m in PLURAL_RE.finditer(text)))


def validate(strings, mockup_en):
    errors = []
    for key, entry in strings.items():
        if not re.fullmatch(r"[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)*", key):
            errors.append("%s: недопустимый ключ" % key)
        if set(entry) != set(LANGS):
            errors.append("%s: нужны ровно языки %s" % (key, ", ".join(LANGS)))
            continue
        for lang in LANGS:
            if not entry[lang].strip():
                errors.append("%s: пустой перевод %s" % (key, lang))
        if placeholders(entry["ru"]) != placeholders(entry["en"]):
            errors.append("%s: разные подстановки в ru и en" % key)
        if plural_args(entry["ru"]) != plural_args(entry["en"]):
            errors.append("%s: разные множественные формы в ru и en" % key)
        for lang in LANGS:
            for m in PLURAL_RE.finditer(entry[lang]):
                if len(m.group(2).split("|")) != PLURAL_FORMS[lang]:
                    errors.append("%s: в %s нужно %d формы(ы) после #" % (key, lang, PLURAL_FORMS[lang]))
            if len(plural_args(entry[lang])) > 1:
                errors.append("%s: поддерживается одна множественная подстановка" % key)
        if entry["ru"] in mockup_en and mockup_en[entry["ru"]] != entry["en"]:
            errors.append("%s: перевод расходится с макетом: «%s» против «%s»" % (key, entry["en"], mockup_en[entry["ru"]]))
    return errors


def load_errors():
    return json.loads(ERRORS_SOURCE.read_text(encoding="utf-8"))


def error_key(code):
    return "error." + code


def field_key(field):
    return "error.label." + field


def usage_key(kind):
    return "error.usage." + kind


def validate_errors(strings, catalog):
    errors = []
    expected = set()
    for code, entry in catalog["codes"].items():
        key = error_key(code)
        expected.add(key)
        if key not in strings:
            errors.append("%s: нет строки для кода %s" % (key, code))
            continue
        used = [int(x) for x in placeholders(strings[key]["ru"])]
        if sorted(used) != list(range(len(used))) or len(used) > len(entry["params"]):
            errors.append("%s: подстановки должны идти подряд с {0} и соответствовать параметрам %s" % (key, entry["params"]))
    for field in catalog["fields"]:
        expected.add(field_key(field))
        if field_key(field) not in strings:
            errors.append("%s: нет названия поля" % field_key(field))
    for kind in catalog["usageKinds"]:
        expected.add(usage_key(kind))
        if usage_key(kind) not in strings:
            errors.append("%s: нет подписи вида использования" % usage_key(kind))
    for key in strings:
        if key.startswith("error.") and key not in expected:
            errors.append("%s: ключа нет в docs/api/error-codes.json" % key)
    return errors


def check_hardcoded_web():
    errors = []
    for path in sorted(WEB_SOURCES.rglob("*.ts*")):
        if "generated" in path.parts:
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if CYRILLIC_RE.search(line):
                errors.append("%s:%d: строка интерфейса вписана в код: %s" % (path.relative_to(ROOT), number, line.strip()))
    return errors


def check_hardcoded():
    errors = []
    for path in sorted(SWIFT_SOURCES.rglob("*.swift")):
        if "Generated" in path.parts:
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if UI_CALL_RE.search(line) or CYRILLIC_RE.search(line):
                errors.append("%s:%d: строка интерфейса вписана в код: %s" % (path.relative_to(ROOT), number, line.strip()))
    return errors


def plural_variants(text, lang):
    categories = PLURAL_CATEGORIES[lang]
    variants = {}
    for position, category in enumerate(categories):
        def pick(m):
            return m.group(2).split("|")[position]
        variants[category] = PLURAL_RE.sub(pick, text)
    return variants


def convert(text, plural_index, total_args):
    sole = total_args == 1

    def arg(m):
        index = int(m.group(1))
        if index == plural_index:
            return "%lld" if sole else "%%%d$lld" % (index + 1)
        return "%@" if sole else "%%%d$@" % (index + 1)
    return ARG_RE.sub(arg, text)


def gen_catalog(strings):
    out = {}
    for key, entry in strings.items():
        localizations = {}
        for lang in LANGS:
            text = entry[lang]
            pargs = plural_args(text)
            total = len(placeholders(text))
            if pargs:
                index = int(pargs[0])
                variations = {}
                for category, variant in plural_variants(text, lang).items():
                    variations[category] = {"stringUnit": {"state": "translated", "value": convert(variant, index, total)}}
                localizations[lang] = {"variations": {"plural": variations}}
            else:
                localizations[lang] = {"stringUnit": {"state": "translated", "value": convert(text, None, total)}}
        out[key] = {"extractionState": "manual", "localizations": localizations}
    catalog = {"sourceLanguage": "en", "strings": out, "version": "1.0"}
    return json.dumps(catalog, indent=2, ensure_ascii=False, sort_keys=True, separators=(",", " : ")) + "\n"


def gen_keys(strings):
    lines = ["import Foundation", "", "enum L10nKey: String, CaseIterable, Sendable {"]
    for key in strings:
        lines.append("    case %s = %s" % (camel(key), json.dumps(key)))
    lines.append("}")
    return "\n".join(lines) + "\n"


def swift_string(value):
    return json.dumps(value, ensure_ascii=False)


def gen_errors_swift(strings, catalog):
    lines = [
        "import Foundation",
        "",
        "struct ErrorCatalogEntry: Sendable {",
        "    let key: L10nKey",
        "    let params: [String]",
        "    let shown: Int",
        "    let plural: Int?",
        "}",
        "",
        "enum ErrorCatalog {",
        "    static let entries: [String: ErrorCatalogEntry] = [",
    ]
    for code, entry in sorted(catalog["codes"].items()):
        text = strings[error_key(code)]["ru"]
        plural = plural_args(text)
        lines.append("        %s: ErrorCatalogEntry(key: .%s, params: [%s], shown: %d, plural: %s)," % (
            swift_string(code),
            camel(error_key(code)),
            ", ".join(swift_string(x) for x in entry["params"]),
            len(placeholders(text)),
            plural[0] if plural else "nil",
        ))
    lines += ["    ]", "", "    static let fields: [String: L10nKey] = ["]
    for field in sorted(catalog["fields"]):
        lines.append("        %s: .%s," % (swift_string(field), camel(field_key(field))))
    lines += ["    ]", "", "    static let usages: [String: L10nKey] = ["]
    for kind in sorted(catalog["usageKinds"]):
        lines.append("        %s: .%s," % (swift_string(kind), camel(usage_key(kind))))
    lines += ["    ]", "}"]
    return "\n".join(lines) + "\n"


def gen_web(strings, catalog):
    keys = sorted(k for k in strings if k.startswith(WEB_PREFIXES))
    table = {lang: {k: strings[k][lang] for k in keys} for lang in LANGS}
    codes = {code: entry["params"] for code, entry in sorted(catalog["codes"].items())}
    return (
        "export const strings = " + json.dumps(table, indent=2, ensure_ascii=False, sort_keys=True) + " as const\n\n"
        "export type StringKey = keyof typeof strings.ru\n\n"
        "export const errorParams: Record<string, readonly string[]> = " + json.dumps(codes, indent=2, ensure_ascii=False) + "\n"
    )


def build(strings, catalog):
    return {
        CATALOG_OUT: gen_catalog(strings),
        KEYS_OUT: gen_keys(strings),
        ERRORS_OUT: gen_errors_swift(strings, catalog),
        WEB_OUT: gen_web(strings, catalog),
    }


def main():
    strings = load_source()
    catalog = load_errors()
    errors = validate(strings, load_mockup_en())
    errors += validate_errors(strings, catalog)
    errors += check_hardcoded()
    errors += check_hardcoded_web()
    if errors:
        for e in errors:
            print(e, file=sys.stderr)
        raise SystemExit(1)
    files = build(strings, catalog)
    if "--check" in sys.argv[1:]:
        bad = [p for p, t in files.items() if not p.exists() or p.read_text(encoding="utf-8") != t]
        if bad:
            for p in bad:
                print("рассинхрон с design/strings.json: " + str(p.relative_to(ROOT)), file=sys.stderr)
            print("запустите scripts/gen-strings.py", file=sys.stderr)
            raise SystemExit(1)
        print("строки в порядке: %d ключей" % len(strings))
        return
    for path, text in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    print("создано файлов: %d, ключей: %d" % (len(files), len(strings)))


if __name__ == "__main__":
    main()
