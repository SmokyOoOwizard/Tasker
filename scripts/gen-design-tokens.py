#!/usr/bin/env python3
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "design" / "tokens.json"
SWIFT_OUT = ROOT / "macos" / "Sources" / "Tasker" / "Generated" / "DesignTokens.swift"
CSS_OUT = ROOT / "design" / "generated" / "tokens.css"
ASSETS = ROOT / "macos" / "Sources" / "Tasker" / "Assets.xcassets" / "Tokens"

NS_WEIGHTS = [(100, -0.8), (200, -0.6), (300, -0.4), (400, 0.0), (500, 0.23), (600, 0.3), (700, 0.4), (800, 0.56), (900, 0.62)]


def kebab(name):
    return re.sub(r"(?<=[a-z0-9])([A-Z])", lambda m: "-" + m.group(1).lower(), name)


def num(v):
    s = repr(float(v))
    if s.endswith(".0"):
        s = s[:-2]
    return s


def swift_num(v):
    s = num(v)
    return s if ("." in s or "e" in s) else s + ".0"


def parse_color(text):
    text = text.strip()
    m = re.fullmatch(r"#([0-9a-fA-F]{6})", text)
    if m:
        h = m.group(1)
        return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), 1.0
    m = re.fullmatch(r"rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)", text)
    if m:
        return int(m.group(1)), int(m.group(2)), int(m.group(3)), float(m.group(4))
    raise SystemExit("неподдерживаемый цвет: " + text)


def css_color(text):
    r, g, b, a = parse_color(text)
    if a == 1.0:
        return "#%02x%02x%02x" % (r, g, b)
    return "rgba(%d,%d,%d,%s)" % (r, g, b, num(a))


def ns_weight(css):
    for (w1, v1), (w2, v2) in zip(NS_WEIGHTS, NS_WEIGHTS[1:]):
        if w1 <= css <= w2:
            return round(v1 + (v2 - v1) * (css - w1) / (w2 - w1), 4)
    raise SystemExit("вес вне диапазона: %s" % css)


def swift_weight_name(css):
    return {400: ".regular", 500: ".medium", 600: ".semibold", 700: ".bold"}[css]


def parse_path(d):
    tokens = re.findall(r"[MmLlHhVvZz]|-?(?:\d+\.?\d*|\.\d+)", d)
    out = []
    i = 0
    cx = cy = sx = sy = 0.0
    cmd = None

    def take():
        nonlocal i
        v = float(tokens[i])
        i += 1
        return v

    while i < len(tokens):
        if re.fullmatch(r"[A-Za-z]", tokens[i]):
            cmd = tokens[i]
            i += 1
            if cmd in "Zz":
                out.append(("close",))
                cx, cy = sx, sy
                continue
        if cmd in "Mm":
            x, y = take(), take()
            if cmd == "m":
                x, y = cx + x, cy + y
            out.append(("move", x, y))
            cx, cy, sx, sy = x, y, x, y
            cmd = "L" if cmd == "M" else "l"
        elif cmd in "Ll":
            x, y = take(), take()
            if cmd == "l":
                x, y = cx + x, cy + y
            out.append(("line", x, y))
            cx, cy = x, y
        elif cmd in "Hh":
            x = take()
            if cmd == "h":
                x = cx + x
            out.append(("line", x, cy))
            cx = x
        elif cmd in "Vv":
            y = take()
            if cmd == "v":
                y = cy + y
            out.append(("line", cx, y))
            cy = y
        else:
            raise SystemExit("неподдерживаемая команда пути: %s" % cmd)
    return out


def swift_path(d):
    parts = []
    for c in parse_path(d):
        if c[0] == "close":
            parts.append(".close")
        else:
            parts.append(".%s(%s, %s)" % (c[0], swift_num(round(c[1], 4)), swift_num(round(c[2], 4))))
    return "[" + ", ".join(parts) + "]"


def swift_primitive(p):
    paint = []
    if p.get("fill"):
        fo = p.get("fillOpacity", 1)
        paint.append("fill: %s" % ("true" if fo == 1 else swift_num(fo)))
    if p.get("stroke"):
        paint.append("strokeWidth: %s" % swift_num(p["width"]))
    if p.get("dash"):
        paint.append("dash: [%s]" % ", ".join(swift_num(v) for v in p["dash"]))
    if p.get("cap"):
        paint.append("cap: .%s" % p["cap"])
    if p.get("join"):
        paint.append("join: .%s" % p["join"])
    if p.get("color"):
        paint.append("color: .%s" % p["color"])
    paint_s = ", ".join(paint)
    if p["type"] == "circle":
        head = "cx: %s, cy: %s, r: %s" % (swift_num(p["cx"]), swift_num(p["cy"]), swift_num(p["r"]))
        shape = ".circle(%s)" % head
    elif p["type"] == "pie":
        head = "cx: %s, cy: %s, r: %s, fraction: %s" % (swift_num(p["cx"]), swift_num(p["cy"]), swift_num(p["r"]), swift_num(p["fraction"]))
        return "StatusIconPrimitive(shape: .pie(%s), paint: StatusIconPaint(fill: true))" % head
    else:
        shape = ".path(%s)" % swift_path(p["d"])
    return "StatusIconPrimitive(shape: %s, paint: StatusIconPaint(%s))" % (shape, paint_s)


def gen_swift(t):
    colors = t["colors"]
    names = list(colors)
    L = []
    a = L.append
    a("import SwiftUI")
    a("")
    a("enum DesignColor: String, CaseIterable, Sendable {")
    for n in names:
        a("    case %s" % n)
    a("")
    a("    var assetName: String { \"token-\" + rawValue }")
    a("    var color: Color { Color(assetName) }")
    a("")
    a("    var lightHex: String {")
    a("        switch self {")
    for n in names:
        a("        case .%s: \"%s\"" % (n, css_color(colors[n]["light"])))
    a("        }")
    a("    }")
    a("")
    a("    var darkHex: String {")
    a("        switch self {")
    for n in names:
        a("        case .%s: \"%s\"" % (n, css_color(colors[n]["dark"])))
    a("        }")
    a("    }")
    a("}")
    a("")
    a("enum DesignShadow {")
    for n, s in t["shadows"].items():
        a("    static let %s = DesignShadowSpec(x: %s, y: %s, blur: %s, lightColor: \"%s\", darkColor: \"%s\")" % (
            n, swift_num(s["x"]), swift_num(s["y"]), swift_num(s["blur"]), css_color(s["color"]["light"]), css_color(s["color"]["dark"])))
    a("}")
    for title, key in (("DesignRadius", "radii"), ("DesignSpacing", "spacing"), ("DesignLayout", "layout")):
        a("")
        a("enum %s {" % title)
        for n, v in t[key].items():
            a("    static let %s: CGFloat = %s" % (n, swift_num(v)))
        a("}")
    ty = t["typography"]
    a("")
    a("enum DesignTypography {")
    a("    static let sansFamily = %s" % json.dumps(ty["families"]["sans"], ensure_ascii=False))
    a("    static let monoFamily = %s" % json.dumps(ty["families"]["mono"], ensure_ascii=False))
    for n, v in ty["sizes"].items():
        a("    static let %s: CGFloat = %s" % (n + "Size", swift_num(v)))
    for n, v in ty["weights"].items():
        a("    static let %sWeight: Font.Weight = %s" % (n, swift_weight_name(v)))
    a("    static let emphasisWeight: CGFloat = %s" % swift_num(ns_weight(ty["emphasis"])))
    a("    static let lineHeight: CGFloat = %s" % swift_num(ty["lineHeight"]))
    a("}")
    tg = t["tags"]
    a("")
    a("enum DesignTag {")
    a("    static let palette: [String] = [%s]" % ", ".join('"%s"' % css_color(c) for c in tg["palette"]))
    for part in ("background", "text"):
        for which in ("light", "dark"):
            spec = tg[part][which]
            a("    static let %s%sMix: CGFloat = %s" % (part, which.capitalize(), swift_num(spec["mix"])))
            a("    static let %s%sBase = \"%s\"" % (part, which.capitalize(), css_color(spec["base"])))
    a("}")
    si = t["statusIcons"]
    a("")
    a("enum StatusIconName: String, CaseIterable, Sendable {")
    for n in si["names"]:
        a("    case %s" % n)
    a("")
    a("    static let box: CGFloat = %s" % swift_num(si["box"]))
    a("")
    a("    var primitives: [StatusIconPrimitive] {")
    a("        switch self {")
    for n in si["names"]:
        a("        case .%s:" % n)
        a("            [")
        for p in si["icons"][n]:
            a("                %s," % swift_primitive(p))
        a("            ]")
    a("        }")
    a("    }")
    a("}")
    return "\n".join(L) + "\n"


def colorset(c):
    def comp(text):
        r, g, b, al = parse_color(text)
        return {"color-space": "srgb", "components": {"alpha": "%.3f" % al, "blue": "0x%02X" % b, "green": "0x%02X" % g, "red": "0x%02X" % r}}

    return json.dumps({
        "colors": [
            {"color": comp(c["light"]), "idiom": "universal"},
            {"appearances": [{"appearance": "luminosity", "value": "dark"}], "color": comp(c["dark"]), "idiom": "universal"},
        ],
        "info": {"author": "xcode", "version": 1},
    }, indent=2, ensure_ascii=False) + "\n"


def gen_css(t):
    ty = t["typography"]

    def theme_block(selector, which):
        lines = [selector + " {"]
        for n, c in t["colors"].items():
            lines.append("  --%s: %s;" % (kebab(n), css_color(c[which])))
        for n, s in t["shadows"].items():
            lines.append("  --shadow-%s: %spx %spx %spx %s;" % (kebab(n), num(s["x"]), num(s["y"]), num(s["blur"]), css_color(s["color"][which])))
        tg = t["tags"]
        lines.append("  --tbp: %s%%;" % num(tg["background"][which]["mix"] * 100))
        lines.append("  --tbb: %s;" % css_color(tg["background"][which]["base"]))
        lines.append("  --tfp: %s%%;" % num(tg["text"][which]["mix"] * 100))
        lines.append("  --tfb: %s;" % css_color(tg["text"][which]["base"]))
        lines.append("}")
        return lines

    L = ["/* Файл создан scripts/gen-design-tokens.py из design/tokens.json. Не редактировать вручную. */"]
    L += theme_block(":root", "dark")
    L += theme_block("[data-theme=light]", "light")
    L += [":root {"]
    for n, v in t["radii"].items():
        L.append("  --radius-%s: %spx;" % (kebab(n), num(v)))
    for n, v in t["spacing"].items():
        L.append("  --space-%s: %spx;" % (kebab(n), num(v)))
    for n, v in t["layout"].items():
        L.append("  --layout-%s: %s;" % (kebab(n), num(v) + "px"))
    L.append("  --font-sans: %s;" % ty["families"]["sans"])
    L.append("  --font-mono: %s;" % ty["families"]["mono"])
    for n, v in ty["sizes"].items():
        L.append("  --font-size-%s: %spx;" % (kebab(n), num(v)))
    for n, v in ty["weights"].items():
        L.append("  --font-weight-%s: %s;" % (kebab(n), v))
    L.append("  --font-weight-emphasis: %s;" % ty["emphasis"])
    L.append("  --line-height: %s;" % num(ty["lineHeight"]))
    for i, c in enumerate(t["tags"]["palette"]):
        L.append("  --tag-%d: %s;" % (i, css_color(c)))
    L.append("}")
    return "\n".join(L) + "\n"


def build():
    t = json.loads(SOURCE.read_text(encoding="utf-8"))
    files = {SWIFT_OUT: gen_swift(t), CSS_OUT: gen_css(t)}
    files[ASSETS / "Contents.json"] = json.dumps({"info": {"author": "xcode", "version": 1}}, indent=2) + "\n"
    for n, c in t["colors"].items():
        files[ASSETS / ("token-%s.colorset" % n) / "Contents.json"] = colorset(c)
    return files


def existing_generated():
    found = set()
    if ASSETS.exists():
        found.update(p for p in ASSETS.rglob("*") if p.is_file())
    return found


def main():
    files = build()
    if "--check" in sys.argv[1:]:
        bad = []
        for path, text in files.items():
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                bad.append(path)
        for path in existing_generated() - set(files):
            bad.append(path)
        if bad:
            for p in sorted(bad):
                print("рассинхрон с design/tokens.json: " + str(p.relative_to(ROOT)), file=sys.stderr)
            print("запустите scripts/gen-design-tokens.py", file=sys.stderr)
            sys.exit(1)
        print("токены дизайна синхронны")
        return
    for path in existing_generated() - set(files):
        path.unlink()
    for path, text in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    print("сгенерировано файлов: %d" % len(files))


if __name__ == "__main__":
    main()
