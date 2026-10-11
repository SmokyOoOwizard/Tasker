#!/usr/bin/env python3
"""Совместная работа .NET- и Rust-консоли с одним файлом SQLite (TSK-159): та же БД, что открывают .NET-десктоп и сервер.

Второй участник — .NET-консоль: её в репозитории больше нет, нужна готовая .NET-сборка tasker прошлой версии (каталог
распакованного архива релиза 0.1.0). Хранилище в .NET-коде осталось у десктопа и сервера; совместную работу Rust-консоли с ним
в обычном прогоне проверяют .NET-тесты (tests/Tasker.Tests: Session рядом с процессами tasker).

  python3 -I scripts/test-sqlite-shared.py <каталог .NET-сборки с tasker(.dll)> <каталог Rust-сборки с tasker> [процессов] [задач]

Проверяется:
  1. по очереди: задачи с номером серии, связи и правки попеременно .NET и Rust — номера подряд, версии-счётчики, увиденные обеими
     сборками, совпадают (версия, выданная одной, принимается другой как --expected-version; устаревшая — [modified] у обеих);
  2. параллельно: N процессов каждой сборки одновременно создают задачи в одной серии и связывают их — ни потерь, ни дублей номеров
     (номера 1..K без пропусков), ошибки «database is locked» и прочие отказы считаются отдельно для каждой сборки;
  3. блокировки правки в таблице edit_locks: чужая блокировка (держатель десктопа, строка в БД) останавливает правку у обеих сборок
     тем же текстом; своя — видна обеим;
  4. итоговые списки (`task list --all --json`, `series list --json`, `link-type list --json`) обеих сборок совпадают байт в байт.

Всё во временном каталоге: TASKER_HOME, TASKER_SERVICE_DIR, TASKER_SERVICE_LABEL, файл БД. Код выхода 0 — всё в порядке.
"""
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import uuid
from datetime import datetime, timedelta, timezone


def command(bin_dir):
    exe = os.path.join(bin_dir, "tasker")
    if os.path.isfile(exe) and os.access(exe, os.X_OK):
        return [exe]
    return ["dotnet", os.path.join(bin_dir, "tasker.dll")]


class Cli:
    def __init__(self, name, bin_dir, home, db):
        self.name, self.cmd, self.db = name, command(bin_dir), db
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("TASKER_")}
        self.env.update(TASKER_HOME=home, TASKER_SERVICE_DIR=os.path.join(home, "service"),
                        TASKER_SERVICE_LABEL="com.tasker.shared-test", DOTNET_NOLOGO="1", TASKER_WIDTH="off")

    def run(self, *args, check=True):
        p = subprocess.run([*self.cmd, *args, "--sqlite", self.db, "-p", "Shared"], env=self.env, capture_output=True, text=True)
        if check and p.returncode != 0:
            raise RuntimeError(f"{self.name}: tasker {' '.join(args)} -> {p.returncode}\n{p.stderr}{p.stdout}")
        return p

    def json(self, *args):
        return json.loads(self.run(*args, "--json").stdout)


failures = []


def expect(condition, message):
    if not condition:
        failures.append(message)
        print("FAIL:", message)


def numbers(cli, prefix):
    series = next(x["id"] for x in cli.json("series", "list")["data"] if x["prefix"] == prefix)
    tasks = cli.json("task", "list", "--all", "--flat")["data"]
    found = [n["number"] for t in tasks for n in t.get("seriesNumbers", []) if n["seriesId"] == series]
    return sorted(found), tasks


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    net_bin, rust_bin = sys.argv[1], sys.argv[2]
    processes = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    per_process = int(sys.argv[4]) if len(sys.argv) > 4 else 5
    root = tempfile.mkdtemp(prefix="tasker-sqlite-shared-")
    try:
        homes = {n: os.path.join(root, f"home-{n}") for n in ("net", "rust")}
        for h in homes.values():
            os.makedirs(os.path.join(h, "service"))
        db = os.path.join(root, "shared.db")
        net, rust = Cli(".NET", net_bin, homes["net"], db), Cli("Rust", rust_bin, homes["rust"], db)
        both = [net, rust]

        # Схему создаёт .NET; Rust дописывает проект и справочники в ту же БД.
        subprocess.run([*net.cmd, "project", "create", "Shared", "--sqlite", db], env=net.env, check=True, capture_output=True)
        rust.run("status", "create", "Todo")
        net.run("status", "create", "Done")
        rust.run("status-set", "create", "Flow", "--status", "Todo", "Done")
        net.run("task-type", "create", "Task", "--status-set", "Flow")
        rust.run("series", "create", "Shared", "--prefix", "SH")

        # 1. По очереди.
        for i in range(10):
            cli = both[i % 2]
            cli.run("task", "create", f"Seq {i} by {cli.name}", "--type", "Task", "--series", "SH")
        got, _ = numbers(net, "SH")
        expect(got == list(range(1, 11)), f"sequential numbers: {got}")
        for i in range(1, 10):
            both[i % 2].run("task", "link", f"SH-{i}", "blocks", f"SH-{i + 1}")
        for a, b in ((net, rust), (rust, net)):
            task = a.json("task", "get", "SH-1")
            seen = b.json("task", "get", "SH-1")
            expect(task["version"] == seen["version"], f"versions differ: {a.name} {task['version']}, {b.name} {seen['version']}")
            b.run("task", "update", "SH-1", "--title", f"Renamed by {b.name}", "--expected-version", task["version"])
            after = a.json("task", "get", "SH-1")
            expect(after["version"] == str(int(task["version"]) + 1), f"{b.name} update: version {task['version']} -> {after['version']}")
            stale = a.run("task", "update", "SH-1", "--title", "stale", "--expected-version", task["version"], check=False)
            expect(stale.returncode != 0 and "changed by someone else" in stale.stderr,
                   f"{a.name} stale update: {stale.returncode} {stale.stderr.strip()}")
        stale_texts = {c.name: c.run("task", "update", "SH-2", "--title", "x", "--expected-version", "1", check=False) for c in both}
        expect(stale_texts["Rust"].stderr == stale_texts[".NET"].stderr and stale_texts["Rust"].returncode == stale_texts[".NET"].returncode,
               f"stale-version error differs: {stale_texts['.NET'].stderr!r} vs {stale_texts['Rust'].stderr!r}")

        # 2. Параллельно.
        errors = {".NET": [], "Rust": []}
        lock = threading.Lock()

        def worker(cli, w):
            for j in range(per_process):
                p = cli.run("task", "create", f"Par {cli.name} {w}-{j}", "--type", "Task", "--series", "SH", check=False)
                if p.returncode != 0:
                    with lock:
                        errors[cli.name].append(p.stderr.strip())

        threads = [threading.Thread(target=worker, args=(cli, w)) for w in range(processes) for cli in both]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        created = 2 * processes * per_process - sum(len(e) for e in errors.values())
        got, tasks = numbers(rust, "SH")
        expect(len(tasks) == 10 + created, f"tasks: {len(tasks)}, expected {10 + created}")
        expect(got == list(range(1, 11 + created)), f"parallel numbers are not 1..{10 + created}: duplicates or gaps {got}")
        for name, errs in errors.items():
            locked = sum("locked" in e for e in errs)
            print(f"{name}: {len(errs)} failed creates ({locked} 'database is locked')", *(f"\n  {e}" for e in errs[:3]))
        expect(not any(errors.values()), f"failed creates: {errors}")

        # Связи параллельно: каждая сборка связывает свою половину пар.
        link_errors = []

        def linker(cli, pairs):
            for a, b in pairs:
                p = cli.run("task", "link", f"SH-{a}", "relates", f"SH-{b}", check=False)
                if p.returncode != 0:
                    link_errors.append((cli.name, p.stderr.strip()))

        pairs = [(i, i + 1) for i in range(11, 10 + created)]
        threads = [threading.Thread(target=linker, args=(both[k % 2], pairs[k::4])) for k in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        expect(not link_errors, f"link errors: {link_errors[:3]}")
        conn = sqlite3.connect(db)
        relates = conn.execute(
            "SELECT COUNT(*) FROM task_links l JOIN link_types t ON t.Id = l.TypeId WHERE t.Name = 'Relates'").fetchone()[0]
        expect(relates == len(pairs), f"relates links: {relates}, expected {len(pairs)}")

        # 3. Блокировки правки: чужая (как у десктопа) — строка в edit_locks.
        task = net.json("task", "get", "SH-3")
        now = datetime.now(timezone.utc)
        fmt = lambda t: t.strftime("%Y-%m-%d %H:%M:%S.%f").rstrip("0").rstrip(".")
        conn.execute("INSERT INTO edit_locks (Entity, Id, HolderKey, HolderName, ProjectId, AcquiredAt, ExpiresAt) VALUES (?,?,?,?,?,?,?)",
                     ("Task", task["id"].upper(), "user:desktop", "Desktop user", task["projectId"].upper(), fmt(now),
                      fmt(now + timedelta(minutes=5))))
        conn.commit()
        refused = {c.name: c.run("task", "update", "SH-3", "--title", "x", check=False) for c in both}
        for name, p in refused.items():
            expect(p.returncode != 0 and "Desktop user" in p.stderr, f"{name} ignored a foreign edit lock: {p.returncode} {p.stderr!r}")
        expect(refused[".NET"].stderr == refused["Rust"].stderr, f"locked error differs: {refused['.NET'].stderr!r} vs {refused['Rust'].stderr!r}")
        conn.execute("DELETE FROM edit_locks")
        conn.commit()
        rust.run("lock", "acquire", "task", "SH-4")
        shown = net.json("lock", "show", "task", "SH-4")["lock"]
        expect(shown is not None and shown["mine"], f".NET does not see the lock taken by Rust: {shown}")
        net.run("lock", "release", "task", "SH-4")
        expect(rust.json("lock", "show", "task", "SH-4")["lock"] is None, "Rust still sees the lock released by .NET")
        conn.close()

        # 4. Итоговые списки одинаковы.
        for args in (("task", "list", "--all", "--json"), ("series", "list", "--json"), ("link-type", "list", "--json"),
                     ("task", "get", "SH-1", "--json"), ("status-set", "list", "--json")):
            a, b = net.run(*args).stdout, rust.run(*args).stdout
            expect(a == b, f"output of {' '.join(args)} differs")
        print(f"tasks: {len(tasks)} ({created} created in parallel by {2 * processes} processes), relates links: {relates}")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("OK" if not failures else f"{len(failures)} failures")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
