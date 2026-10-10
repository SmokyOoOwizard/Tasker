#!/usr/bin/env python3
"""Общий кэш индекса файловой области у .NET- и Rust-консоли (TSK-161): один файл .tasker/.cache/index.db, одна схема.

  python3 -I scripts/test-shared-index.py <каталог .NET-сборки с tasker(.dll)> <каталог Rust-сборки с tasker> [процессов] [задач]

Проверяется:
  1. перекрёстное чтение: индекс, построенный одной сборкой на копии golden-корпуса, другая читает без пересборки — ни одна строка
     не переписана (дамп с rowid и сырыми mtime тот же: INSERT OR REPLACE дал бы новый rowid), схема и user_version те же; вывод
     списков (`project list`, `task list --all`, `series list`, `user list`, `board list`, `sync` — текст и --json) обеих сборок
     совпадает байт в байт; индексы, построенные .NET и Rust, совпадают строка в строку (без rowid);
  2. правка файла одной сборкой — другая перечитывает только его: после `task update` одной сборкой и чтения другой изменилась
     ровно одна строка files;
  3. совместная работа: N процессов каждой сборки одновременно создают задачи в одной серии и правят их в одной папке — ни
     потерь, ни дублей номеров (1..K без пропусков), отказы («database is locked» и прочие) считаются для каждой сборки; таблицы
     не пересоздавались (индекс-метка, добавленный в files до начала, на месте); старого index-rs.db нет; после — снова чтение обеими
     без пересборки и одинаковые списки.

Всё во временном каталоге: TASKER_HOME, TASKER_SERVICE_DIR, TASKER_SERVICE_LABEL, области. Код выхода 0 — всё в порядке.
"""
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
GOLDEN = os.path.join(REPO, "rust", "tests", "golden", "workspace", ".tasker")
TABLES = ("files", "task_series", "task_links", "task_fields", "task_field_values")


def command(bin_dir):
    exe = os.path.join(bin_dir, "tasker")
    if os.path.isfile(exe) and os.access(exe, os.X_OK):
        return [exe]
    return ["dotnet", os.path.join(bin_dir, "tasker.dll")]


class Cli:
    def __init__(self, name, bin_dir, home):
        self.name, self.cmd = name, command(bin_dir)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("TASKER_")}
        self.env.update(TASKER_HOME=home, TASKER_SERVICE_DIR=os.path.join(home, "service"),
                        TASKER_SERVICE_LABEL="com.tasker.shared-index-test", DOTNET_NOLOGO="1", TASKER_WIDTH="off")
        os.makedirs(os.path.join(home, "service"), exist_ok=True)

    def run(self, ws, *args, project=None, check=True):
        cmd = [*self.cmd, *args, "-w", ws] + (["-p", project] if project else [])
        p = subprocess.run(cmd, env=self.env, capture_output=True, text=True, encoding="utf-8")
        if check and p.returncode != 0:
            raise RuntimeError(f"{self.name}: tasker {' '.join(args)} -> {p.returncode}\n{p.stderr}{p.stdout}")
        return p

    def json(self, ws, *args, project=None):
        return json.loads(self.run(ws, *args, "--json", project=project).stdout)


failures = []


def expect(condition, message):
    if not condition:
        failures.append(message)
        print("FAIL:", message)


def db_of(ws):
    return os.path.join(ws, ".tasker", ".cache", "index.db")


def raw_dump(ws, with_rowid=True):
    """Все строки индекса (с rowid — чтобы увидеть перезапись), схема и user_version."""
    if not os.path.exists(db_of(ws)):
        return [("no index.db",)]
    conn = sqlite3.connect(db_of(ws))
    out = [("user_version", conn.execute("PRAGMA user_version").fetchone()[0])]
    out += [tuple(r) for r in conn.execute("SELECT type, name, sql FROM sqlite_master ORDER BY name")]
    for table in TABLES:
        rowid = "rowid, " if with_rowid else ""
        order = "path" if table == "files" else "path, rowid"
        out += [(table, *r) for r in conn.execute(f"SELECT {rowid}* FROM {table} ORDER BY {order}")]
    conn.close()
    return out


def changed_rows(before, after):
    return sorted(set(before) ^ set(after))


def copy_golden(dst):
    os.makedirs(dst)
    shutil.copytree(GOLDEN, os.path.join(dst, ".tasker"), ignore=shutil.ignore_patterns(".cache"))
    return dst


READS = [
    (("project", "list"), None), (("project", "list", "--json"), None),
    (("task", "list", "--all"), "Golden"), (("task", "list", "--all", "--json"), "Golden"),
    (("task", "list", "--all", "--json"), "Legacy"), (("task", "list", "--sort", "title", "--json"), "Golden"),
    (("series", "list", "--json"), "Golden"), (("user", "list", "--json"), None), (("board", "list", "--json"), "Golden"),
    (("status", "list", "--json"), "Golden"), (("field", "list", "--json"), "Golden"), (("sync",), None), (("sync", "--json"), None),
]


def cross_read(builder, reader, root):
    """Индекс строит builder, reader читает: без перезаписи строк, вывод тот же."""
    ws = copy_golden(os.path.join(root, f"cross-{builder.name}-{reader.name}".replace(".", "")))
    builder.run(ws, "sync", check=False)
    expect(not os.path.exists(os.path.join(ws, ".tasker", ".cache", "index-rs.db")), f"{builder.name} created index-rs.db")
    before = raw_dump(ws)
    outputs = {}
    for args, project in READS:
        outputs[(args, project)] = reader.run(ws, *args, project=project, check=False)
    after = raw_dump(ws)
    diff = changed_rows(before, after)
    expect(not diff, f"{reader.name} rewrote the index built by {builder.name}: {len(diff)} rows differ, e.g. {diff[:2]}")
    for (args, project), p in outputs.items():
        mine = builder.run(ws, *args, project=project, check=False)
        expect((mine.returncode, mine.stdout, mine.stderr) == (p.returncode, p.stdout, p.stderr),
               f"{' '.join(args)} -p {project}: {builder.name} and {reader.name} differ")
    expect(changed_rows(after, raw_dump(ws)) == [], f"{builder.name} rewrote rows after {reader.name} read them")

    # Правка одного файла сборкой reader: builder перечитывает только его.
    task = reader.json(ws, "task", "get", "GLD-1", project="Golden")
    reader.run(ws, "task", "update", "GLD-1", "--title", f"Edited by {reader.name}", project="Golden")
    edited = raw_dump(ws)
    builder.run(ws, "task", "list", "--all", project="Golden")
    files_changed = {r[2] for r in changed_rows(edited, raw_dump(ws)) if r[0] == "files"}
    expect(not files_changed, f"{builder.name} re-read files after {reader.name} edited one: {sorted(files_changed)[:3]}")
    touched = {r[2] for r in changed_rows(after, edited) if r[0] == "files"}
    # Новый заголовок — новое имя файла: прежний путь удалён, новый добавлен; других файлов правка не касается.
    expect(touched and all(task["id"][:8] in x for x in touched), f"{reader.name} edit touched {sorted(touched)}")
    expect(not os.path.exists(os.path.join(ws, ".tasker", ".cache", "index-rs.db")), f"{reader.name} created index-rs.db")

    # Списки reader берёт из этого кэша, а не из своего: строка, подменённая в index.db (файл не тронут), видна в его выводе.
    conn = sqlite3.connect(db_of(ws))
    conn.execute("UPDATE files SET data = replace(data, '\"Name\":\"Legacy\"', '\"Name\":\"Legacy from cache\"') WHERE kind = 'Project'")
    conn.commit()
    conn.close()
    names = [x["name"] for x in reader.json(ws, "project", "list")["data"]]
    expect("Legacy from cache" in names, f"{reader.name} does not read the shared index: projects {names}")
    return ws


def concurrent(net, rust, root, processes, per_process):
    ws = os.path.join(root, "shared")
    os.makedirs(ws)
    both = [net, rust]
    net.run(ws, "project", "create", "Shared")
    rust.run(ws, "status", "create", "Todo", project="Shared")
    net.run(ws, "status", "create", "Done", project="Shared")
    rust.run(ws, "status-set", "create", "Flow", "--status", "Todo", "Done", project="Shared")
    net.run(ws, "task-type", "create", "Task", "--status-set", "Flow", project="Shared")
    rust.run(ws, "series", "create", "Shared", "--prefix", "SH", project="Shared")
    # Метка: индекс на files. Пересоздание таблиц (чужая схема, повреждение) снесло бы его вместе с таблицей.
    conn = sqlite3.connect(db_of(ws))
    conn.execute("CREATE INDEX probe_not_recreated ON files (size)")
    conn.commit()
    conn.close()

    errors = {".NET": [], "Rust": []}
    lock = threading.Lock()

    def worker(cli, w):
        for j in range(per_process):
            p = cli.run(ws, "task", "create", f"Par {cli.name} {w}-{j}", "--type", "Task", "--series", "SH", "--json",
                        project="Shared", check=False)
            if p.returncode != 0:
                with lock:
                    errors[cli.name].append(p.stderr.strip())
                continue
            ref = f"SH-{json.loads(p.stdout)['seriesNumbers'][0]['number']}"
            u = cli.run(ws, "task", "update", ref, "--title", f"Par {cli.name} {w}-{j} edited", project="Shared", check=False)
            if u.returncode != 0:
                with lock:
                    errors[cli.name].append(u.stderr.strip())

    threads = [threading.Thread(target=worker, args=(cli, w)) for w in range(processes) for cli in both]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    for name, errs in errors.items():
        locked = sum("locked" in e for e in errs)
        print(f"{name}: {len(errs)} failed commands ({locked} 'database is locked')", *(f"\n  {e}" for e in errs[:3]))
    expect(not any(errors.values()), "failed commands during the parallel run")
    created = 2 * processes * per_process - sum(len(e) for e in errors.values())
    conn = sqlite3.connect(db_of(ws))
    probe = conn.execute("SELECT count(*) FROM sqlite_master WHERE name = 'probe_not_recreated'").fetchone()[0]
    version = conn.execute("PRAGMA user_version").fetchone()[0]
    conn.close()
    expect(probe == 1, "index tables were recreated during the parallel run")
    expect(version == 10, f"user_version {version}")
    expect(not os.path.exists(os.path.join(ws, ".tasker", ".cache", "index-rs.db")), "index-rs.db exists")

    for cli in both:
        tasks = cli.json(ws, "task", "list", "--all", "--flat", project="Shared")["data"]
        got = sorted(n["number"] for t in tasks for n in t.get("seriesNumbers", []))
        expect(got == list(range(1, created + 1)), f"{cli.name}: numbers are not 1..{created}: {got}")
        expect(all(t["title"].endswith("edited") for t in tasks), f"{cli.name}: not every task shows its edit")
    before = raw_dump(ws)
    for args in (("task", "list", "--all", "--json"), ("series", "list", "--json"), ("sync", "--json")):
        a, b = net.run(ws, *args, project="Shared"), rust.run(ws, *args, project="Shared")
        expect(a.stdout == b.stdout, f"output of {' '.join(args)} differs after the parallel run")
    expect(changed_rows(before, raw_dump(ws)) == [], "reads after the parallel run rewrote the index")
    print(f"parallel: {created} tasks created and edited by {2 * processes} processes")


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    net_bin, rust_bin = sys.argv[1], sys.argv[2]
    processes = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    per_process = int(sys.argv[4]) if len(sys.argv) > 4 else 5
    root = tempfile.mkdtemp(prefix="tasker-shared-index-")
    try:
        net = Cli(".NET", net_bin, os.path.join(root, "home-net"))
        rust = Cli("Rust", rust_bin, os.path.join(root, "home-rust"))
        built_by_net = cross_read(net, rust, root)
        built_by_rust = cross_read(rust, net, root)
        # Индексы, построенные каждой сборкой на одинаковой области после одинаковой правки — те же строки (без rowid и mtime).
        strip = lambda rows: [r for r in rows if r[0] != "files"] + [
            (r[0], *r[1:3], *r[4:]) for r in rows if r[0] == "files"]
        edited = lambda r: "Edited by" in str(r) or "edited-by" in str(r)
        a = [r for r in strip(raw_dump(built_by_net, with_rowid=False)) if not edited(r)]
        b = [r for r in strip(raw_dump(built_by_rust, with_rowid=False)) if not edited(r)]
        expect(a == b, "indexes built by .NET and Rust differ (beyond the edited task)")
        print("cross-read: OK" if not failures else "cross-read: failures above")
        concurrent(net, rust, root, processes, per_process)
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("OK" if not failures else f"{len(failures)} failures")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
