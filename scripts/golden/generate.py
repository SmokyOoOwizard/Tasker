#!/usr/bin/env python3
"""
Golden-корпус для переноса консоли и демона на Rust (TSK-124, docs/rust-migration-plan.md, фаза 0).

Скрипт на текущей .NET-сборке создаёт рабочую область со всеми сущностями, всеми типами полей, «опасными» строками, файлами
всех formatVersion 0–9, legacy-именами <guid>.yaml, файлом с маркером конфликта и файлом «из будущего», приводит id и даты
к детерминированным значениям и снимает эталоны: версии файлов (хэши), результат `tasker migrate`, вывод ключевых команд
консоли, снапшоты MCP (tools/list, ответы инструментов, по ошибке каждого кода) и стилевые эталоны YamlDotNet.

  scripts/golden/generate.py [--bin <каталог с tasker и tasker-mcpd>] [--out rust/tests/golden] [--keep]
  scripts/golden/generate.py --verify [--bin <каталог>]      снять эталоны заново с указанного бинарника и сравнить с rust/tests/golden

Без --bin консоль и демон собираются из этого репозитория (dotnet build) во временный каталог. Ничего не трогает в настоящей
установке: TASKER_HOME, рабочая область и демон (на свободном порту) живут во временном каталоге и удаляются в конце
(кроме --keep).
"""
import argparse
import difflib
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_OUT = os.path.join(REPO, "rust", "tests", "golden")

GUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
TIMESTAMP = re.compile(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00")
BASE_TIME = datetime(2026, 1, 1, 0, 0, 0, tzinfo=timezone.utc)

# Канонические id проектов: по ним считаются id типов связей по умолчанию (MD5, см. DefaultLinkTypes.IdOf).
PROJECT_IDS = {"Golden": "11111111-1111-4111-8111-111111111111", "Legacy": "22222222-2222-4222-8222-222222222222"}
DEFAULT_LINK_TYPES = {"Blocks": "blocks", "Duplicate": "duplicate", "Cloners": "cloners", "Relates": "relates",
                      "Problem/Incident": "problem", "Parent/Child": "parent"}


def log(*parts):
    print("[golden]", *parts, file=sys.stderr, flush=True)


def default_link_type_id(project_id, key):
    """Guid(byte[]) в .NET — little-endian для первых трёх групп: uuid.UUID(bytes_le=...)."""
    digest = bytearray(hashlib.md5(f"tasker-link-type:{project_id}:{key}".encode()).digest())
    digest[6] = (digest[6] & 0x0F) | 0x30
    digest[8] = (digest[8] & 0x3F) | 0x80
    return str(uuid.UUID(bytes_le=bytes(digest)))


def version_of(data: bytes) -> str:
    """Версия сущности: первые 16 hex SHA-256 от байтов без BOM и с CRLF→LF (Tasker.Core.IO.LineEndings.ForHash)."""
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()[:16]


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def rmtree(path):
    shutil.rmtree(path, ignore_errors=True)


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mode = "wb" if isinstance(data, bytes) else "w"
    with open(path, mode, **({} if isinstance(data, bytes) else {"encoding": "utf-8", "newline": "\n"})) as f:
        f.write(data)


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def walk_yaml(root):
    """Все .yaml области (без .cache), относительные пути с '/', отсортированы."""
    result = []
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d != ".cache"]
        for name in files:
            if name.endswith(".yaml"):
                result.append(os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/"))
    return sorted(result)


# ----------------------------------------------------------------------------- запуск tasker

class Tasker:
    def __init__(self, bin_dir, home):
        self.bin = os.path.abspath(bin_dir)
        exe = os.path.join(self.bin, "tasker")
        dll = os.path.join(self.bin, "tasker.dll")
        if os.path.isfile(exe) and os.access(exe, os.X_OK):
            self.cmd = [exe]
        elif os.path.isfile(dll):
            self.cmd = ["dotnet", dll]
        else:
            raise SystemExit(f"no tasker in {self.bin}")
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("TASKER_")}
        self.env.update(TASKER_HOME=home, DOTNET_gcServer="0", DOTNET_NOLOGO="1",
                        TASKER_SERVICE_DIR=os.path.join(home, "service"),
                        TASKER_SERVICE_LABEL="com.tasker.golden-" + os.path.basename(home)[-8:])
        os.makedirs(self.env["TASKER_SERVICE_DIR"], exist_ok=True)

    def run(self, *args, ws=None, project=None, check=True, env=None, cwd=None, input=None):
        cmd = [*self.cmd, *args]
        if ws:
            cmd += ["-w", ws]
        if project:
            cmd += ["-p", project]
        e = dict(self.env)
        if env:
            e.update(env)
        p = subprocess.run(cmd, env=e, capture_output=True, cwd=cwd, input=input)
        p.out = p.stdout.decode("utf-8", "replace")
        p.err = p.stderr.decode("utf-8", "replace")
        if check and p.returncode != 0:
            raise RuntimeError(f"tasker {' '.join(args)} failed ({p.returncode}):\n{p.err.strip()[:2000]}\n{p.out.strip()[:500]}")
        return p

    def json(self, *args, **kw):
        return json.loads(self.run(*args, "--json", **kw).out)


def build(bin_dir):
    log("building tasker and tasker-mcpd from", REPO)
    for project, extra in (("src/Tasker.Cli", []), ("src/Tasker.Daemon.Host", ["-p:EmbedFrontend=false"])):
        subprocess.run(["dotnet", "build", os.path.join(REPO, project), "-c", "Release", "-o", bin_dir, "--nologo", "-v", "quiet", *extra],
                       check=True)


# ----------------------------------------------------------------------------- 1. рабочая область

# «Опасные» заголовки: кавычки YAML, спецсимволы, похожие на число/bool/null строки, юникод, длина (slug режется по 60 UTF-16-единиц).
TITLES = [
    "Plain task",
    "007",
    "true",
    "null",
    "~",
    "yes",
    "no",
    "off",
    "1.5",
    "1e3",
    "0x1F",
    "-",
    "---",
    "...",
    "Colon: in the middle",
    "Ends with colon:",
    "# starts with hash",
    "Hash # in the middle",
    "Quote 'single' and \"double\"",
    "'quoted'",
    "\"quoted\"",
    "[brackets] {braces} & ampersand * star | pipe > gt ! bang % percent @ at `backtick`",
    "Кириллица и эмодзи 🚀 ✨, ещё — тире и «ёлочки»",
    "Ümläute, İstanbul, ß, Ærø, naïve café",
    "日本語のタイトル и 中文",
    "Combining: e\u0301 (e + U+0301), precomposed: \u00e9",
    "Tab\tinside and  double  spaces",
    "A very long title that goes on and on past sixty characters so that the slug of the file name is cut while the title itself stays whole in the file",
    "Trailing spaces   ",
    "   Leading spaces",
    "Multi\nline title",
    "Backslash \\ and forward / slashes",
    "Percent %s and {0} braces and $var and ${env}",
]

DESCRIPTIONS = {
    "multiline": "First line.\n\nThird line after a blank one.\n  Indented line.\nLine with trailing spaces   \n# A hash line\nkey: value\n- a dash item\nLast line without newline",
    "trailing-newline": "Ends with a newline\n",
    "two-trailing-newlines": "Ends with two newlines\n\n",
    "spaces-only-line": "Above\n   \nBelow",
    "crlf": "Windows\r\nline\r\nendings",
    "numeric": "007",
    "bool": "true",
    "colon": "a: b",
    "hash": "#hashtag",
    "quotes": "'single' \"double\" `back`",
    "unicode": "Кириллица, 日本語, emoji 🎉, combining e\u0301, zero-width\u200bspace, nbsp\u00a0here",
    "tab": "col1\tcol2\n\tindented with a tab",
    "long-line": "x" * 300,
    "single-space": " ",
    "yaml-doc-markers": "---\nnot a document\n...",
    "leading-blank": "\nStarts with a blank line",
    "percent": "100% done, 50%",
}


def make_workspace(t: Tasker, ws):
    """Создаёт область «golden» со всеми сущностями. Возвращает заметки для README (что не удалось создать)."""
    notes = []
    os.makedirs(ws, exist_ok=True)
    t.run("whoami", "golden-user", ws=ws)
    t.run("project", "create", "Golden", ws=ws)
    P = dict(ws=ws, project="Golden")

    # Статусы: цвет, описание (многострочное), имена-ловушки.
    t.run("status", "create", "Backlog", **P)
    t.run("status", "create", "In Progress", "--color", "#1E90FF", "-d", "Work has started.\nSecond line: with a colon # and a hash.", **P)
    t.run("status", "create", "Done", "--color", "#2ECC71", "-d", "Finished", **P)
    t.run("status", "create", "Отложено", "--color", "#FFA500", "-d", "Статус «отложено»: вернёмся позже", **P)
    t.run("status", "create", "007", "--color", "#000000", **P)
    t.run("status-set", "create", "Main", "--status", "Backlog", "In Progress", "Done", **P)
    t.run("status-set", "create", "Extended", "--status", "Backlog", "In Progress", "Отложено", "Done", "007", **P)

    # Перечисления и поля всех типов, одиночные и множественные.
    t.run("enum", "create", "Priority", "--value", "Low", "Medium", "High", **P)
    t.run("enum", "create", "Платформа", "--value", "macOS", "Linux", "Windows", "Web: browser", "007", **P)
    t.run("field", "create", "Estimate", "--type", "int", **P)
    t.run("field", "create", "Score", "--type", "float", **P)
    t.run("field", "create", "Due", "--type", "date", **P)
    t.run("field", "create", "Urgent", "--type", "bool", **P)
    t.run("field", "create", "Priority", "--type", "enum", "--enum", "Priority", **P)
    t.run("field", "create", "Tags", "--type", "string", "--multiple", **P)
    t.run("field", "create", "Platforms", "--type", "enum", "--multiple", "--enum", "Платформа", **P)
    t.run("field", "create", "Notes", "--type", "string", **P)
    t.run("field", "create", "StoryPoints", "--type", "int", **P)
    t.run("field", "create", "Ожидание", "--type", "bool", **P)
    t.run("field", "create", "Numbers", "--type", "int", "--multiple", **P)
    t.run("field", "create", "Dates", "--type", "date", "--multiple", **P)

    # Типы задач: поля, обязательные поля, описание.
    t.run("task-type", "create", "Bug", "--status-set", "Main", "--field", "Priority:required", "--field", "Estimate", "--field", "Tags",
          "-d", "A defect.\n\nSteps: reproduce, fix, verify.", **P)
    t.run("task-type", "create", "Feature", "--status-set", "Extended", "--field", "Score", "--field", "Due", "--field", "Platforms",
          "--field", "Urgent", **P)
    t.run("task-type", "create", "Задача", "--status-set", "Main", "-d", "Тип без полей: # не комментарий, ключ: значение", **P)

    # Серии.
    t.run("series", "create", "Golden", "--prefix", "GLD", **P)
    t.run("series", "create", "Bugs", "--prefix", "BUG", **P)

    # Типы связей: свои (направленный без циклов, без направления, иерархический).
    t.run("link-type", "create", "Depends", "--outward", "depends on", "--inward", "is a dependency of", "--allow-cycles", "false", **P)
    t.run("link-type", "create", "Documents", "--outward", "documents", **P)
    t.run("link-type", "create", "Epic", "--outward", "contains", "--inward", "belongs to", "--hierarchical", "true", **P)

    # Задачи с «опасными» заголовками (тип без полей, серия GLD).
    created = {}
    for title in TITLES:
        r = t.run("task", "create", title, "--type", "Задача", "--series", "GLD", "--json", check=False, **P)
        if r.returncode != 0:
            notes.append(f"title {title!r} rejected: {r.err.strip()}")
            continue
        created[title] = json.loads(r.out)["id"]

    # Задачи с описаниями.
    for name, text in DESCRIPTIONS.items():
        r = t.run("task", "create", f"Description: {name}", "--type", "Задача", "--series", "GLD", "-d", text, "--json", check=False, **P)
        if r.returncode != 0:
            notes.append(f"description {name!r} rejected: {r.err.strip()}")
            continue
        created[f"Description: {name}"] = json.loads(r.out)["id"]

    # Задачи с полями: значения всех типов, несколько значений, собственные поля, поле без значения, канонизация (007 → 7, 1.50 → 1.5).
    bug1 = t.json("task", "create", "Bug with all fields", "--type", "Bug", "--series", "BUG", "--status", "In Progress",
                  "--field", "Priority=High", "--field", "Estimate=007", "--field", "Tags=alpha", "--field", "Tags=beta", "--field", "Tags=007",
                  "--field", "Notes=Additional catalog field", "--field", "StoryPoints=13",
                  "-d", "Bug with all fields.\nEstimate given as 007: stored canonically.", **P)
    bug2 = t.json("task", "create", "Bug with own fields", "--type", "Bug", "--series", "BUG", "--series", "GLD",
                  "--field", "Priority=Low",
                  "--custom-field", "Severity:enum:required:enum=Priority=Medium",
                  "--custom-field", "Reporters:string:multiple=alice", "--custom-field", "Reporters:string:multiple=Боб",
                  "--custom-field", "Reproducible:bool=true", "--custom-field", "Seen:date=2028-02-29",
                  "--custom-field", "Ratio:float=0.50", "--custom-field", "Attempts:int=-3",
                  "--add-field", "Ожидание", **P)
    feat1 = t.json("task", "create", "Feature with values", "--type", "Feature", "--series", "GLD", "--status", "Отложено",
                   "--field", "Score=1.50", "--field", "Due=2026-12-31", "--field", "Platforms=Linux", "--field", "Platforms=Web: browser",
                   "--field", "Urgent=false", "--field", "Numbers=1", "--field", "Numbers=-2", "--field", "Numbers=003",
                   "--field", "Dates=2026-01-01", "--field", "Dates=2025-12-31", **P)
    feat2 = t.json("task", "create", "Feature without values", "--type", "Feature", "--status", "007", **P)
    feat3 = t.json("task", "create", "Feature done", "--type", "Feature", "--series", "GLD", "--status", "Done",
                   "--field", "Score=-0.25", "--field", "Urgent=true", "--field", "Platforms=macOS", **P)
    bug3 = t.json("task", "create", "Bug done", "--type", "Bug", "--series", "BUG", "--status", "Done", "--field", "Priority=Medium",
                  "--field", "Estimate=0", **P)

    # Иерархия: эпик → задачи → подзадача; задача с двумя родителями; второй иерархический тип.
    epic = t.json("task", "create", "Epic: hierarchy", "--type", "Задача", "--series", "GLD", **P)
    child1 = t.json("task", "create", "Child one", "--type", "Задача", "--series", "GLD", "--parent", epic["id"], "--parent-type", "Parent/Child", **P)
    child2 = t.json("task", "create", "Child two", "--type", "Задача", "--series", "GLD", "--parent", epic["id"], "--parent-type", "Parent/Child", **P)
    grandchild = t.json("task", "create", "Grandchild", "--type", "Задача", "--series", "GLD", "--parent", child1["id"], "--parent-type", "Parent/Child", **P)
    epic2 = t.json("task", "create", "Epic two", "--type", "Задача", "--series", "GLD", **P)
    t.run("task", "update", child2["id"], "--add-parent", epic2["id"], "--parent-type", "Parent/Child", **P)
    t.run("task", "link", epic2["id"], "contains", grandchild["id"], **P)

    # Связи остальных типов: направленные, без направления, свои, дубликат связи при создании.
    t.run("task", "link", bug1["id"], "blocks", feat1["id"], **P)
    t.run("task", "link", bug2["id"], "is blocked by", bug1["id"], **P)
    t.run("task", "link", feat1["id"], "relates to", feat3["id"], **P)
    t.run("task", "link", bug3["id"], "duplicates", bug1["id"], **P)
    t.run("task", "link", feat2["id"], "clones", feat1["id"], **P)
    t.run("task", "link", bug1["id"], "causes", bug3["id"], **P)
    t.run("task", "link", feat3["id"], "depends on", feat2["id"], **P)
    t.run("task", "link", feat2["id"], "documents", epic["id"], **P)
    # Серии: задача в двух сериях уже есть (bug2); перенумерация и удаление номера.
    t.run("series", "renumber-task", "GLD", feat3["id"], "--number", "100", check=False, **P)
    t.run("series", "add-task", "BUG", feat2["id"], **P)
    # Правка задачи: updatedAt отличается от createdAt, смена статуса и описания.
    t.run("task", "update", created["Plain task"], "--status", "Done", "-d", "Updated description", **P)
    t.run("task", "update", bug1["id"], "--title", "Bug with all fields (renamed)", **P)

    # Доски: условия колонок по полям, несколько наборов статусов.
    t.run("board", "create", "Main board", "--status-set", "Main", "--column", "Todo=Backlog", "--column", "Doing=In Progress", "--column", "Done=Done",
          "--column-filter", "Doing:Estimate>=3", "--column-filter", "Todo:Priority=High", "--column-filter", "Todo:Tags:set", **P)
    t.run("board", "create", "All", "--status-set", "Main", "Extended", "--column", "Open=Backlog,In Progress,Отложено", "--column", "Closed=Done,007", **P)

    # Пользователи и агенты.
    t.run("user", "create", "alice", ws=ws)
    t.run("user", "create", "bob.the-2nd_user", ws=ws)
    t.run("agent", "create", "claude", ws=ws)
    t.run("agent", "create", "codex", ws=ws)

    # Второй проект — станет «старым»: файлы разных formatVersion и имена <guid>.yaml (см. make_legacy).
    t.run("project", "create", "Legacy", ws=ws)
    L = dict(ws=ws, project="Legacy")
    t.run("status", "create", "Open", **L)
    t.run("status", "create", "Closed", "-d", "Closed status", **L)
    t.run("status-set", "create", "Flow", "--status", "Open", "Closed", **L)
    t.run("task-type", "create", "Item", "--status-set", "Flow", "-d", "Legacy type", **L)
    t.run("series", "create", "Old", "--prefix", "OLD", **L)
    t.run("enum", "create", "Kind", "--value", "A", "B", **L)
    t.run("field", "create", "Kind", "--type", "enum", "--enum", "Kind", **L)
    t.run("board", "create", "Legacy board", "--status-set", "Flow", "--column", "Open=Open", "--column", "Closed=Closed", **L)
    l1 = t.json("task", "create", "Legacy one", "--type", "Item", "--series", "OLD", "-d", "Format version 0, legacy file name", **L)
    l2 = t.json("task", "create", "Legacy two", "--type", "Item", "--series", "OLD", **L)
    l3 = t.json("task", "create", "Legacy three", "--type", "Item", "--series", "OLD", "--status", "Closed", "--field", "Kind=B",
                "-d", "Line endings CRLF", **L)
    l4 = t.json("task", "create", "Legacy four", "--type", "Item", "-d", "With a BOM", **L)
    t.run("task", "link", l1["id"], "blocks", l2["id"], **L)
    t.run("task", "link", l3["id"], "includes", l4["id"], **L)
    return notes


# ----------------------------------------------------------------------------- 2. канонизация id и дат

def entity_sort_key(rel, text):
    """Порядок обхода, не зависящий от случайных id: папка, slug имени файла, затем имя/заголовок и время создания из самого файла
    (у одинаковых slug — 'quoted' и "quoted", у файлов <guid>.yaml пользователей)."""
    folder, name = os.path.split(rel)
    stem = name[:-5]
    m = re.match(r"^(.+)-[0-9a-f]{8}$", stem)
    head = re.search(r"^(?:title|name|username): (.*)$", text, re.M)
    created = re.search(r"^createdAt: (.*)$", text, re.M)
    return (folder, m.group(1) if m else "", head.group(1) if head else "", created.group(1) if created else "", stem)


def canonicalize(tasker_root):
    """Переписывает все Guid и временные метки в детерминированные; переименовывает файлы и папки проектов."""
    texts = {rel: read_bytes(os.path.join(tasker_root, rel)).decode("utf-8") for rel in walk_yaml(tasker_root)}
    files = sorted(texts, key=lambda rel: entity_sort_key(rel, texts[rel]))

    mapping = {}
    # Проекты и их типы связей по умолчанию.
    for rel, text in texts.items():
        if rel.endswith("/project.yaml"):
            old = re.search(r"^id: (\S+)$", text, re.M).group(1)
            name = re.search(r"^name: (.*)$", text, re.M).group(1)
            mapping[old] = PROJECT_IDS[name]
    for rel, text in texts.items():
        if "/link-types/" in rel:
            project_old = rel.split("/")[1]
            old = re.search(r"^id: (\S+)$", text, re.M).group(1)
            name = re.search(r"^name: (.*)$", text, re.M).group(1)
            if name in DEFAULT_LINK_TYPES and old == default_link_type_id(project_old, DEFAULT_LINK_TYPES[name]):
                mapping[old] = default_link_type_id(mapping[project_old], DEFAULT_LINK_TYPES[name])
    # Остальные — по порядку первого появления.
    counter = 0
    for rel in files:
        for guid in [*GUID.findall(rel), *GUID.findall(texts[rel])]:
            if guid not in mapping:
                counter += 1
                mapping[guid] = f"{counter:08x}-0000-4000-8000-{counter:012x}"
    # Даты: порядок сохраняется, шаг — не круглый, чтобы минуты и секунды тоже менялись.
    stamps = sorted({s for text in texts.values() for s in TIMESTAMP.findall(text)})
    stamp_map = {}
    for i, stamp in enumerate(stamps):
        when = BASE_TIME + timedelta(seconds=61 * i, microseconds=123450 + i)
        stamp_map[stamp] = when.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"

    def fix(text):
        text = GUID.sub(lambda m: mapping[m.group(0)], text)
        return TIMESTAMP.sub(lambda m: stamp_map[m.group(0)], text)

    # Имя файла сущности кончается на первые 8 знаков id (EntityFileNames.IdPrefix) — их тоже надо заменить.
    id8 = {old[:8]: new[:8] for old, new in mapping.items()}

    def fix_name(rel):
        rel = fix(rel)
        return re.sub(r"-([0-9a-f]{8})\.yaml$", lambda m: f"-{id8.get(m.group(1), m.group(1))}.yaml", rel)

    for rel in files:
        os.remove(os.path.join(tasker_root, rel))
    for rel in files:
        new_rel = fix_name(rel)
        write(os.path.join(tasker_root, new_rel), fix(texts[rel]).encode("utf-8"))
    for base, dirs, _ in os.walk(tasker_root, topdown=False):
        for d in dirs:
            path = os.path.join(base, d)
            if not os.listdir(path):
                os.rmdir(path)
    rmtree(os.path.join(tasker_root, ".cache"))
    return mapping


# ----------------------------------------------------------------------------- 3. «старые» файлы проекта Legacy

def set_version(text, version):
    """Как FormatVersions.SetVersion; version None — убрать строку (формат 0)."""
    without = re.sub(r"^formatVersion:[^\n]*\n", "", text, count=1, flags=re.M)
    return without if version is None else f"formatVersion: {version}\n{without}"


def make_legacy(tasker_root, mapping):
    """Проект Legacy: файлы разных formatVersion, имена <guid>.yaml, CRLF, BOM, конфликт слияния, файл новее текущего формата."""
    project = os.path.join(tasker_root, "projects", PROJECT_IDS["Legacy"])
    plan = {}  # относительный путь → (версия, legacy-имя?)

    def files_in(folder):
        return sorted(os.listdir(os.path.join(project, folder)))

    def rewrite(folder, name, version, legacy_name=False, transform=None):
        path = os.path.join(project, folder, name)
        text = read_bytes(path).decode("utf-8")
        text = set_version(text, version)
        if transform:
            text = transform(text)
        data = text.encode("utf-8")
        if legacy_name:
            guid = re.search(r"^id: (\S+)$", text, re.M).group(1)
            os.remove(path)
            path = os.path.join(project, folder, guid + ".yaml")
        write(path, data)
        return os.path.relpath(path, tasker_root).replace(os.sep, "/")

    text = read_bytes(os.path.join(project, "project.yaml")).decode("utf-8")
    write(os.path.join(project, "project.yaml"), set_version(text, 1).encode("utf-8"))
    statuses = files_in("statuses")
    rewrite("statuses", statuses[0], None, legacy_name=True)          # Closed: формат 0, <guid>.yaml
    rewrite("statuses", statuses[1], 3)                               # Open: формат 3
    rewrite("status-sets", files_in("status-sets")[0], 2, legacy_name=True)
    rewrite("task-types", files_in("task-types")[0], 5)
    rewrite("series", files_in("series")[0], 6)
    rewrite("enums", files_in("enums")[0], 4, legacy_name=True)
    rewrite("fields", files_in("fields")[0], 4)
    rewrite("boards", files_in("boards")[0], 5)
    for name in files_in("link-types"):
        if name.startswith("blocks-") or name.startswith("relates-"):
            # До формата 7 у типов связей не было allowCycles, до 9 — hierarchical.
            rewrite("link-types", name, 6, transform=lambda s: re.sub(r"^(allowCycles|hierarchical):.*\n", "", s, flags=re.M))
        elif name.startswith("parent-child-"):
            rewrite("link-types", name, 8, transform=lambda s: re.sub(r"^hierarchical:.*\n", "", s, flags=re.M))
    tasks = {name.split("-")[0] + "-" + name.split("-")[1]: name for name in files_in("tasks")}
    rewrite("tasks", tasks["legacy-one"], None, legacy_name=True)
    rewrite("tasks", tasks["legacy-two"], 1, legacy_name=True)
    rewrite("tasks", tasks["legacy-three"], 7, transform=lambda s: s.replace("\n", "\r\n"))
    rewrite("tasks", tasks["legacy-four"], 8, transform=lambda s: "\ufeff" + s)

    # Файл с неразрешённым конфликтом слияния (отдельная задача, не трогается migrate, индекс показывает проблему).
    base = read_bytes(os.path.join(project, "tasks", [n for n in files_in("tasks") if n.startswith("legacy-three")][0])).decode("utf-8").replace("\r\n", "\n")
    conflict_id = "cccccccc-0000-4000-8000-cccccccccccc"
    conflict = re.sub(r"^id: .*$", f"id: {conflict_id}", base, count=1, flags=re.M)
    conflict = re.sub(r"^title: .*$", "<<<<<<< HEAD\ntitle: Conflicted title (ours)\n=======\ntitle: Conflicted title (theirs)\n>>>>>>> feature", conflict, count=1, flags=re.M)
    conflict = re.sub(r"^description: .*$", "description: Unresolved git merge conflict", conflict, count=1, flags=re.M)
    conflict = re.sub(r"^series:\n(- .*\n|  .*\n)*", "", conflict, flags=re.M)
    conflict = re.sub(r"^links:\n(- .*\n|  .*\n)*", "", conflict, flags=re.M)
    write(os.path.join(project, "tasks", f"conflicted-title-ours-{conflict_id[:8]}.yaml"), conflict.encode("utf-8"))

    # Файл более нового формата: читать нельзя, в списках — проблема «обновите Tasker».
    future_id = "ffffffff-0000-4000-8000-ffffffffffff"
    future = re.sub(r"^id: .*$", f"id: {future_id}", base, count=1, flags=re.M)
    future = re.sub(r"^title: .*$", "title: From the future", future, count=1, flags=re.M)
    future = re.sub(r"^description: .*$", "description: formatVersion 99", future, count=1, flags=re.M)
    future = re.sub(r"^series:\n(- .*\n|  .*\n)*", "", future, flags=re.M)
    future = re.sub(r"^links:\n(- .*\n|  .*\n)*", "", future, flags=re.M)
    future = set_version(future, 99) + "futureKey: unknown to this Tasker\n"
    write(os.path.join(project, "tasks", f"from-the-future-{future_id[:8]}.yaml"), future.encode("utf-8"))
    return {"conflict": conflict_id, "future": future_id}


# ----------------------------------------------------------------------------- 4. эталоны консоли

def help_tree(t: Tasker):
    """Все команды и подкоманды по разделу Commands: справки."""
    result = []

    def visit(path):
        r = t.run(*path, "--help", check=False)
        result.append(path)
        section = r.out.split("\nCommands:\n", 1)
        if len(section) < 2:
            return
        for line in section[1].split("\n"):
            if not line.startswith("  ") or not line.strip():
                break
            head = re.split(r"\s{2,}", line.strip(), maxsplit=1)[0]
            names = [n.strip() for n in head.split(",")]
            name = names[-1].split(" ")[0]
            visit([*path, name])

    visit([])
    return result


WALL_CLOCK = re.compile(r"\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)? ?\+00:00")


def scrub(text):
    """Время по часам машины (срок блокировки правки) → <TIME>: оно не детерминировано."""
    return WALL_CLOCK.sub("<TIME>", text)


def root_replacements(root):
    """Пути временного каталога → <ROOT>; на macOS /var/... раскрывается в /private/var/..., поэтому оба варианта."""
    real = os.path.realpath(root)
    return [(real, "<ROOT>"), (root, "<ROOT>")] if real != root else [(root, "<ROOT>")]


class Snapshots:
    """Эталоны команд консоли: <имя>.args (по аргументу в строке), .out, .err, .code; пути области заменяются на <ROOT>."""

    def __init__(self, t: Tasker, out_dir, replacements):
        self.t = t
        self.dir = out_dir
        self.replacements = replacements
        self.index = 0

    def clean(self, text, scrub_times=False):
        for old, new in self.replacements:
            text = text.replace(old, new)
        return scrub(text) if scrub_times else text

    def take(self, name, *args, ws=None, project=None, env=None, cwd=None, scrub_times=False):
        self.index += 1
        r = self.t.run(*args, ws=ws, project=project, check=False, env=env, cwd=cwd)
        shown = [*args] + (["-w", "<ROOT>/golden"] if ws else []) + (["-p", project] if project else [])
        base = os.path.join(self.dir, f"{self.index:03d}-{name}")
        write(base + ".args", self.clean("\n".join(shown)) + "\n")
        write(base + ".out", self.clean(r.out, scrub_times))
        write(base + ".err", self.clean(r.err, scrub_times))
        write(base + ".code", f"{r.returncode}\n")
        return r


def snapshot_cli(t: Tasker, run_ws, out_dir, legacy_ids):
    root = os.path.dirname(run_ws)
    s = Snapshots(t, os.path.join(out_dir, "cli"), root_replacements(root))
    G = dict(ws=run_ws, project="Golden")
    L = dict(ws=run_ws, project="Legacy")
    env_plain = {"TASKER_WIDTH": "off"}

    s.take("sync", "sync", ws=run_ws)
    s.take("sync-json", "sync", "--json", ws=run_ws)
    s.take("sync-quiet", "sync", "-q", ws=run_ws)
    s.take("cleanup-check", "cleanup", "--check", ws=run_ws)
    s.take("cleanup-check-json", "cleanup", "--check", "--json", ws=run_ws)
    s.take("cleanup-dry-run", "cleanup", "--dry-run", ws=run_ws)
    s.take("migrate-check", "migrate", "--check", ws=run_ws)
    s.take("migrate-check-json", "migrate", "--check", "--json", ws=run_ws)
    s.take("migrate-dry-run", "migrate", "--dry-run", ws=run_ws)
    s.take("project-list", "project", "list", ws=run_ws)
    s.take("project-list-json", "project", "list", "--json", ws=run_ws)
    s.take("project-get", "project", "get", "Golden", ws=run_ws)
    s.take("project-get-json", "project", "get", "Golden", "--json", ws=run_ws)
    for entity in ("status", "status-set", "task-type", "series", "field", "enum", "board", "link-type"):
        s.take(f"{entity}-list", entity, "list", **G)
        s.take(f"{entity}-list-json", entity, "list", "--json", **G)
    s.take("status-get", "status", "get", "In Progress", **G)
    s.take("status-get-json", "status", "get", "In Progress", "--json", **G)
    s.take("status-set-get", "status-set", "get", "Extended", **G)
    s.take("task-type-get", "task-type", "get", "Bug", **G)
    s.take("task-type-get-json", "task-type", "get", "Bug", "--json", **G)
    s.take("series-get", "series", "get", "GLD", **G)
    s.take("field-get", "field", "get", "Platforms", **G)
    s.take("field-get-json", "field", "get", "Platforms", "--json", **G)
    s.take("enum-get", "enum", "get", "Платформа", **G)
    s.take("enum-get-json", "enum", "get", "Платформа", "--json", **G)
    s.take("board-get", "board", "get", "Main board", **G)
    s.take("board-get-json", "board", "get", "Main board", "--json", **G)
    s.take("board-show", "board", "show", "Main board", **G)
    s.take("board-show-json", "board", "show", "Main board", "--json", **G)
    s.take("board-tasks", "board", "tasks", "All", "Open", **G)
    s.take("link-type-get", "link-type", "get", "Depends", **G)
    s.take("link-type-get-json", "link-type", "get", "Depends", "--json", **G)
    s.take("user-list", "user", "list", ws=run_ws)
    s.take("user-list-json", "user", "list", "--json", ws=run_ws)
    s.take("agent-list", "agent", "list", ws=run_ws)
    s.take("agent-list-json", "agent", "list", "--json", ws=run_ws)
    s.take("whoami", "whoami", ws=run_ws)

    s.take("task-list", "task", "list", "--all", **G)
    s.take("task-list-json", "task", "list", "--all", "--json", **G)
    s.take("task-list-flat", "task", "list", "--all", "--flat", **G)
    s.take("task-list-page", "task", "list", "--offset", "5", "--limit", "7", **G)
    s.take("task-list-quiet", "task", "list", "--all", "-q", **G)
    s.take("task-list-truncate-60", "task", "list", "--all", "--truncate", "--width", "60", **G)
    s.take("task-list-description-length", "task", "list", "--all", "--json", "--description-length", "12", **G)
    s.take("task-list-sort-status-desc-updated", "task", "list", "--all", "--sort", "status,-updated", **G)
    s.take("task-list-sort-title", "task", "list", "--all", "--sort", "title", "--flat", **G)
    s.take("task-list-sort-series", "task", "list", "--all", "--sort", "-series", "--flat", **G)
    s.take("task-list-sort-field", "task", "list", "--all", "--sort", "Estimate,-Score", "--flat", **G)
    s.take("task-list-filter-type", "task", "list", "--all", "--type", "Bug", "Feature", **G)
    s.take("task-list-filter-status", "task", "list", "--all", "--status", "Done", "--status", "007", **G)
    s.take("task-list-filter-series", "task", "list", "--all", "--series", "BUG", **G)
    s.take("task-list-filter-field-eq", "task", "list", "--all", "--field", "Priority=High", **G)
    s.take("task-list-filter-field-ne", "task", "list", "--all", "--field", "Priority!=High", "--json", **G)
    s.take("task-list-filter-field-ge", "task", "list", "--all", "--field", "Estimate>=3", **G)
    s.take("task-list-filter-field-set", "task", "list", "--all", "--field", "Tags:set", **G)
    s.take("task-list-filter-field-attached", "task", "list", "--all", "--field", "Ожидание:attached", **G)
    s.take("task-list-filter-multi", "task", "list", "--all", "--field", "Platforms=Linux", "Urgent=false", **G)
    s.take("task-list-legacy", "task", "list", "--all", **L)
    s.take("task-list-legacy-json", "task", "list", "--all", "--json", **L)

    tasks = json.loads(s.t.run("task", "list", "--all", "--json", **G).out)["data"]
    by_title = {x["title"]: x for x in tasks}
    for title, name in (("Bug with all fields (renamed)", "bug-all-fields"), ("Bug with own fields", "bug-own-fields"),
                        ("Feature with values", "feature-values"), ("Epic: hierarchy", "epic"), ("Child two", "child-two"),
                        ("007", "title-007"), ("Description: multiline", "description-multiline"), ("Description: crlf", "description-crlf"),
                        ("Кириллица и эмодзи 🚀 ✨, ещё — тире и «ёлочки»", "title-unicode")):
        task = by_title[title]
        ref = next((f"{n['prefix']}-{n['number']}" for n in task.get("seriesNumbers", []) if n.get("prefix")), None)
        s.take(f"task-get-{name}", "task", "get", task["id"], **G)
        s.take(f"task-get-{name}-json", "task", "get", task["id"], "--json", **G)
        s.take(f"task-links-{name}", "task", "links", task["id"], **G)
        if ref:
            s.take(f"task-get-{name}-by-ref", "task", "get", ref, **G)
    s.take("task-get-short-id", "task", "get", by_title["Plain task"]["id"][:8], **G)
    s.take("task-links-json", "task", "links", by_title["Bug with all fields (renamed)"]["id"], "--json", **G)
    s.take("task-get-legacy-crlf", "task", "get", "OLD-3", **L)
    s.take("task-get-legacy-crlf-json", "task", "get", "OLD-3", "--json", **L)
    s.take("task-get-legacy-one-json", "task", "get", "OLD-1", "--json", **L)
    s.take("task-get-legacy-bom-json", "task", "get", "Legacy four", "--json", **L)
    s.take("task-get-future", "task", "get", legacy_ids["future"], **L)
    # task get по id файла с конфликтом слияния падает с необработанным исключением YamlDotNet (баг, заведён задачей) — не снимаем.
    s.take("lock-show", "lock", "show", "task", "GLD-1", **G)
    s.take("lock-acquire", "lock", "acquire", "task", "GLD-1", scrub_times=True, **G)
    s.take("lock-show-held", "lock", "show", "task", "GLD-1", scrub_times=True, **G)
    s.take("lock-show-held-json", "lock", "show", "task", "GLD-1", "--json", scrub_times=True, **G)
    # Консоль и десктоп на одной машине — один держатель («local»), поэтому «Locked:» в консоли не воспроизвести; ошибка [locked] снимается через MCP.
    s.take("lock-release", "lock", "release", "task", "GLD-1", **G)

    # Ошибки и коды выхода.
    s.take("error-not-found", "task", "get", "GLD-999", **G)
    s.take("error-not-found-json", "task", "get", "GLD-999", "--json", **G)
    s.take("error-in-use", "status", "delete", "Done", **G)
    s.take("error-modified", "task", "update", "GLD-1", "--title", "x", "--expected-version", "0000000000000000", **G)
    s.take("error-invalid-field", "task", "create", "Bad", "--type", "Bug", "--field", "Estimate=abc", **G)
    s.take("error-required-field", "task", "create", "Bad", "--type", "Bug", **G)
    s.take("error-cycle", "task", "link", "GLD-1", "blocks", "GLD-1", **G)
    s.take("error-no-project", "task", "list", ws=run_ws)
    s.take("error-unknown-project", "task", "list", ws=run_ws, project="Nope")
    s.take("error-no-workspace", "task", "list", "-w", os.path.join(root, "nowhere"))
    s.take("error-unknown-command", "nope")
    s.take("error-env-typo", "task", "list", "--all", ws=run_ws, project="Golden", env={"TASKER_PROJCET": "Golden"})

    # Справка всех команд, автодополнение, manual.
    for path in help_tree(t):
        s.take("help-" + ("-".join(path) or "root"), *path, "--help")
    for shell in ("bash", "zsh", "powershell"):
        s.take(f"completion-{shell}", "completion", shell)
    # Директива автодополнения: «[suggest:ПОЗИЦИЯ]» и набранная строка одним аргументом; область — текущая папка.
    for name, line in (("commands", ""), ("task-subcommands", "task "), ("options", "task list --"), ("status-values", "task list --status "),
                       ("task-refs", "task get GLD-1"), ("field-values", "task list --field Pri"), ("type-values", "task-type get "),
                       ("project-values", "-p "), ("manual-topics", "manual ")):
        s.take(f"suggest-{name}", f"[suggest:{len(line)}]", line, cwd=run_ws)
    s.take("manual", "manual")
    s.take("manual-topic-tasks", "manual", "tasks")
    return by_title


# ----------------------------------------------------------------------------- 5. эталоны MCP

class Mcp:
    def __init__(self, port, replacements):
        self.url = f"http://127.0.0.1:{port}/mcp"
        self.replacements = replacements

    def rpc(self, method, params, headers=None):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
        req = urllib.request.Request(self.url, data=body, method="POST",
                                     headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream", **(headers or {})})
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                text = resp.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            return {"httpStatus": e.code, "body": e.read().decode("utf-8", "replace")}
        data = next(line for line in text.split("\n") if line.startswith("data:"))[len("data:"):].strip()
        return json.loads(data)

    def call(self, tool, arguments, headers=None):
        return self.rpc("tools/call", {"name": tool, "arguments": arguments}, headers)

    def clean(self, obj):
        text = json.dumps(obj, ensure_ascii=False, indent=2)
        for old, new in self.replacements:
            text = text.replace(json.dumps(old, ensure_ascii=False)[1:-1], new)
        return text + "\n"


def workspace_id(path):
    """Id области в демоне: 12 hex SHA-256 от «Files:<канонический путь>»."""
    return hashlib.sha256(f"Files:{os.path.realpath(path)}".encode()).hexdigest()[:12]


def snapshot_mcp(t: Tasker, run_ws, out_dir, legacy_ids, by_title):
    root = os.path.dirname(run_ws)
    broken = os.path.join(root, "broken")
    os.makedirs(broken)
    t.run("project", "create", "Broken", ws=broken)
    port = free_port()
    t.run("mcp", "port", str(port))
    t.run("mcp", "workspace", "add", run_ws)
    t.run("mcp", "workspace", "add", broken)
    rmtree(broken)  # папка исчезла до старта демона: область не откроется → [failed]
    log("starting the daemon on port", port)
    t.run("mcp", "start")
    for _ in range(100):
        r = t.run("mcp", "status", "--json", check=False)
        if r.returncode == 0 and all(w.get("state") != "opening" for w in json.loads(r.out).get("daemon", {}).get("workspaces", [])):
            break
        time.sleep(0.2)
    try:
        m = Mcp(port, [*root_replacements(root), (workspace_id(run_ws), "<WSID:golden>"), (workspace_id(broken), "<WSID:broken>")])
        mcp_dir = os.path.join(out_dir, "mcp")
        G = {"workspace": "golden", "projectId": PROJECT_IDS["Golden"]}
        L = {"workspace": "golden", "projectId": PROJECT_IDS["Legacy"]}

        def save(name, result, scrub_times=False):
            text = m.clean(result)
            write(os.path.join(mcp_dir, name + ".json"), scrub(text) if scrub_times else text)
            return result

        me = json.loads(m.call("whoami", {"workspace": "golden"})["result"]["content"][0]["text"])
        m.replacements += [(me["id"], "<LOCAL-AGENT-ID>"), (me["createdAt"], "<LOCAL-AGENT-CREATED>"), (me["version"], "<LOCAL-AGENT-VERSION>")]

        save("initialize", m.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "golden", "version": "0"}}))
        tools = save("tools-list", m.rpc("tools/list", {}))
        names = sorted(x["name"] for x in tools.get("result", {}).get("tools", []))
        write(os.path.join(mcp_dir, "tool-names.txt"), "\n".join(names) + "\n")
        save("list_workspaces", m.call("list_workspaces", {}))
        save("whoami", m.call("whoami", {"workspace": "golden"}))
        save("list_projects", m.call("list_projects", {"workspace": "golden"}))
        save("list_tasks", m.call("list_tasks", {**G}))
        save("list_tasks-brief", m.call("list_tasks", {**G, "descriptionLength": 0, "limit": 5}))
        bug = by_title["Bug with all fields (renamed)"]
        save("get_task", m.call("get_task", {**G, "taskId": bug["id"]}))
        save("get_task-by-reference", m.call("get_task", {**G, "taskId": "GLD-1"}))
        save("get_task_links", m.call("get_task_links", {**G, "taskId": bug["id"]}))
        save("find_tasks_by_reference", m.call("find_tasks_by_reference", {**G, "reference": "BUG-1"}))
        save("list_fields", m.call("list_fields", {**G}))
        save("list_enums", m.call("list_enums", {**G}))
        save("list_boards", m.call("list_boards", {**G}))
        save("get_board", m.call("get_board", {**G, "boardId": next(b["id"] for b in t.json("board", "list", ws=run_ws, project="Golden")["data"] if b["name"] == "Main board")}))
        save("list_link_types", m.call("list_link_types", {**G}))
        save("list_series", m.call("list_series", {**G}))
        save("series_health", m.call("series_health", {**G}))
        save("list_statuses", m.call("list_statuses", {**G}))
        save("list_status_sets", m.call("list_status_sets", {**G}))
        save("list_task_types", m.call("list_task_types", {**G}))
        save("list_users", m.call("list_users", {"workspace": "golden"}))
        save("list_project_members", m.call("list_project_members", {**G}))
        save("list_workspace_problems", m.call("list_workspace_problems", {"workspace": "golden"}))
        save("get_lock", m.call("get_lock", {**G, "entity": "task", "entityId": bug["id"]}))

        # По ошибке каждого кода.
        errors = os.path.join("errors")
        save(f"{errors}/invalid-no-project", m.call("get_task", {"workspace": "golden", "taskId": "GLD-1"}))
        save(f"{errors}/invalid-bad-id", m.call("get_task", {**G, "taskId": "not-a-task"}))
        save(f"{errors}/invalid-missing-argument", m.call("get_task", {**G}))
        save(f"{errors}/invalid-no-workspace", m.call("list_projects", {}))
        save(f"{errors}/not_found-task", m.call("get_task", {**G, "taskId": "GLD-999"}))
        save(f"{errors}/not_found-workspace", m.call("list_projects", {"workspace": "nowhere"}))
        save(f"{errors}/modified", m.call("update_task", {**G, "taskId": bug["id"], "title": "x", "version": "0000000000000000"}))
        done = next(x for x in t.json("status", "list", ws=run_ws, project="Golden")["data"] if x["name"] == "Done")
        save(f"{errors}/in_use", m.call("delete_status", {**G, "statusId": done["id"], "version": done["version"]}))
        save(f"{errors}/forbidden", m.call("list_projects", {"workspace": "golden"}, headers={"X-Tasker-Agent": "99999999-9999-4999-8999-999999999999"}))
        save(f"{errors}/unsupported_format", m.call("get_task", {**L, "taskId": legacy_ids["future"]}))
        save(f"{errors}/failed", m.call("list_projects", {"workspace": "broken"}))
        t.run("lock", "acquire", "task", bug["id"], ws=run_ws, project="Golden")
        save(f"{errors}/locked", m.call("update_task", {**G, "taskId": bug["id"], "title": "x", "version": bug["version"]}), scrub_times=True)
        t.run("lock", "release", "task", bug["id"], ws=run_ws, project="Golden")
        save(f"{errors}/unknown-tool", m.call("no_such_tool", {}))
    finally:
        t.run("mcp", "stop", check=False)


# ----------------------------------------------------------------------------- 6. стилевые эталоны YamlDotNet

def yaml_style(ws_tasker, out_dir, by_title):
    """Для каждой задачи-пробы: значение (из JSON консоли) и то, как YamlDotNet записал его в файл."""
    project = os.path.join(ws_tasker, "projects", PROJECT_IDS["Golden"], "tasks")
    files = {}
    for name in os.listdir(project):
        text = read_bytes(os.path.join(project, name)).decode("utf-8")
        files[re.search(r"^id: (\S+)$", text, re.M).group(1)] = (name, text)

    def block(text, key):
        m = re.search(rf"^{key}:(.*\n(?:  .*\n|\n)*)", text, re.M)
        return m.group(0).rstrip("\n") if m else None

    scalars, blocks = [], []
    for title, task in sorted(by_title.items()):
        if task["id"] not in files:
            continue
        name, text = files[task["id"]]
        entry = {"value": title, "yaml": block(text, "title"), "file": name}
        scalars.append(entry)
        if task.get("description"):
            blocks.append({"value": task["description"], "yaml": block(text, "description"), "file": name})
    write(os.path.join(out_dir, "yaml-style", "titles.json"), json.dumps(scalars, ensure_ascii=False, indent=2) + "\n")
    write(os.path.join(out_dir, "yaml-style", "descriptions.json"), json.dumps(blocks, ensure_ascii=False, indent=2) + "\n")


# ----------------------------------------------------------------------------- 7. versions, migrate

def versions(tasker_root):
    return {rel: version_of(read_bytes(os.path.join(tasker_root, rel))) for rel in walk_yaml(tasker_root)}


def copy_workspace(src_tasker, dst_ws):
    os.makedirs(dst_ws, exist_ok=True)
    shutil.copytree(src_tasker, os.path.join(dst_ws, ".tasker"), ignore=shutil.ignore_patterns(".cache"))


def snapshot_migrate(t: Tasker, canonical_tasker, root, out_dir):
    ws = os.path.join(root, "migrate", "golden")
    copy_workspace(canonical_tasker, ws)
    s = Snapshots(t, os.path.join(out_dir, "migrate"), root_replacements(os.path.dirname(ws)))
    s.take("migrate", "migrate", ws=ws)
    s.take("migrate-check-after", "migrate", "--check", ws=ws)
    s.take("migrate-again", "migrate", ws=ws)
    s.take("task-list-legacy-after-json", "task", "list", "--all", "--json", ws=ws, project="Legacy")
    tasker_root = os.path.join(ws, ".tasker")
    rmtree(os.path.join(tasker_root, ".cache"))
    dst = os.path.join(out_dir, "migrate", "tasker")
    shutil.copytree(tasker_root, dst)
    write(os.path.join(out_dir, "migrate", "versions.json"), json.dumps(versions(tasker_root), indent=2, ensure_ascii=False) + "\n")


# ----------------------------------------------------------------------------- main

def snapshot_all(t: Tasker, canonical_tasker, root, out_dir, legacy_ids):
    """Все эталоны с канонической области: консоль, MCP, migrate, versions, стиль YAML."""
    run_ws = os.path.join(root, "run", "golden")
    copy_workspace(canonical_tasker, run_ws)
    t.run("whoami", "golden-user")  # имя держателя блокировок в снапшотах не должно зависеть от пользователя ОС
    write(os.path.join(out_dir, "versions.json"), json.dumps(versions(canonical_tasker), indent=2, ensure_ascii=False) + "\n")
    log("console snapshots")
    by_title = snapshot_cli(t, run_ws, out_dir, legacy_ids)
    # Версии из консоли должны совпасть с посчитанными по байтам.
    computed = versions(canonical_tasker)
    for title, task in by_title.items():
        matches = [v for rel, v in computed.items() if rel.endswith(f"-{task['id'][:8]}.yaml") and PROJECT_IDS["Golden"] in rel]
        if task["version"] not in matches:
            raise RuntimeError(f"version mismatch for {title!r}: console {task['version']}, files {matches}")
    log("migrate snapshots")
    snapshot_migrate(t, canonical_tasker, root, out_dir)
    log("MCP snapshots")
    snapshot_mcp(t, run_ws, out_dir, legacy_ids, by_title)
    yaml_style(canonical_tasker, out_dir, by_title)


def generate(args, bin_dir, root):
    home = os.path.join(root, "home")
    os.makedirs(home)
    t = Tasker(bin_dir, home)
    ws = os.path.join(root, "gen", "golden")
    log("creating the workspace")
    notes = make_workspace(t, ws)
    for note in notes:
        log("note:", note)
    t.run("sync", ws=ws)
    tasker_root = os.path.join(ws, ".tasker")
    log("canonicalizing ids and timestamps")
    mapping = canonicalize(tasker_root)
    legacy_ids = make_legacy(tasker_root, mapping)

    out = os.path.abspath(args.out)
    for name in ("workspace", "expected", "yaml-style", "versions.json", "notes.json"):
        rmtree(os.path.join(out, name)) if os.path.isdir(os.path.join(out, name)) else (os.remove(os.path.join(out, name)) if os.path.exists(os.path.join(out, name)) else None)
    shutil.copytree(tasker_root, os.path.join(out, "workspace", ".tasker"))
    expected = os.path.join(out, "expected")
    snapshot_all(t, tasker_root, root, expected, legacy_ids)
    shutil.move(os.path.join(expected, "yaml-style"), os.path.join(out, "yaml-style"))
    shutil.move(os.path.join(expected, "versions.json"), os.path.join(out, "versions.json"))
    write(os.path.join(out, "notes.json"), json.dumps({
        "generatedWith": t.run("--version").out.strip(),
        "rejected": notes,
        "legacy": legacy_ids,
        "projects": PROJECT_IDS,
    }, ensure_ascii=False, indent=2) + "\n")
    log("done:", out)


def verify(args, bin_dir, root):
    out = os.path.abspath(args.out)
    home = os.path.join(root, "home")
    os.makedirs(home)
    t = Tasker(bin_dir, home)
    notes = json.load(open(os.path.join(out, "notes.json"), encoding="utf-8"))
    canonical = os.path.join(out, "workspace", ".tasker")
    fresh = os.path.join(root, "expected")
    snapshot_all(t, canonical, root, fresh, notes["legacy"])
    shutil.move(os.path.join(fresh, "yaml-style"), os.path.join(root, "yaml-style"))
    shutil.move(os.path.join(fresh, "versions.json"), os.path.join(root, "versions.json"))
    failures = 0
    for sub in ("expected", "yaml-style", "versions.json"):
        a, b = os.path.join(out, sub), os.path.join(root, sub)
        files_a = {os.path.relpath(os.path.join(d, f), a) for d, _, fs in os.walk(a) for f in fs} if os.path.isdir(a) else {""}
        files_b = {os.path.relpath(os.path.join(d, f), b) for d, _, fs in os.walk(b) for f in fs} if os.path.isdir(b) else {""}
        for rel in sorted(files_a | files_b):
            pa, pb = (os.path.join(a, rel), os.path.join(b, rel)) if rel else (a, b)
            if not os.path.exists(pa) or not os.path.exists(pb):
                failures += 1
                print(f"only in {'expected' if os.path.exists(pa) else 'actual'}: {sub}/{rel}")
                continue
            da, db = read_bytes(pa), read_bytes(pb)
            if da != db:
                failures += 1
                print(f"differs: {sub}/{rel}")
                if args.diff:
                    for line in difflib.unified_diff(da.decode("utf-8", "replace").splitlines(), db.decode("utf-8", "replace").splitlines(),
                                                     "expected", "actual", lineterm="", n=2):
                        print("   ", line)
    print("verify:", "OK" if failures == 0 else f"{failures} differences")
    return 0 if failures == 0 else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bin", help="каталог с tasker(.dll) и tasker-mcpd(.dll); без него — сборка из репозитория")
    parser.add_argument("--out", default=DEFAULT_OUT, help="куда писать корпус (по умолчанию rust/tests/golden)")
    parser.add_argument("--verify", action="store_true", help="снять эталоны заново и сравнить с --out, ничего не перезаписывая")
    parser.add_argument("--diff", action="store_true", help="с --verify: печатать построчные различия")
    parser.add_argument("--keep", action="store_true", help="не удалять временный каталог")
    args = parser.parse_args()
    root = tempfile.mkdtemp(prefix="tasker-golden-")
    try:
        bin_dir = os.path.abspath(args.bin) if args.bin else os.path.join(root, "bin")
        if not args.bin:
            build(bin_dir)
        code = verify(args, bin_dir, root) if args.verify else (generate(args, bin_dir, root) or 0)
    finally:
        if args.keep:
            log("kept", root)
        else:
            rmtree(root)
    sys.exit(code)


if __name__ == "__main__":
    main()
