#!/usr/bin/env python3
"""
Стенд замера скорости консоли tasker (TSK-92). Ничего не трогает в настоящей установке: рабочая область, TASKER_HOME и демон
(на свободном порту) создаются во временном каталоге и удаляются в конце.

  scripts/perf/publish.sh <каталог>                 собрать tasker и tasker-mcpd так же, как установщик (R2R, self-contained)
  scripts/perf/bench.py calls   --bin <каталог>     медианы типичных вызовов и разбивка по фазам (TASKER_PROFILE=1)
  scripts/perf/bench.py degrade --bin <каталог>     время вызова от числа задач и от числа выполненных вызовов

Режимы: --mode daemon (демон запущен и обслуживает область: слежение за файлами, как у настоящего пользователя) и
--mode nodaemon (демона нет). Результаты печатаются таблицей; --json <файл> сохраняет сырые замеры.
Замеры на общей машине шумят: в начале и в конце печатается load average, ключевые измерения повторяются (--repeat),
берётся медиана; разброс показывают p10/p90.
"""
import argparse
import json
import os
import re
import shutil
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from concurrent.futures import ThreadPoolExecutor


# ----------------------------------------------------------------------------- окружение

class Env:
    """Временные TASKER_HOME и рабочая область (+ свой демон на свободном порту)."""

    def __init__(self, bin_dir, mode, root=None, keep=False):
        self.bin = os.path.abspath(bin_dir)
        self.tasker = os.path.join(self.bin, "tasker")
        self.mode = mode
        self.keep = keep
        self.root = tempfile.mkdtemp(prefix="tasker-perf-", dir=root)
        self.home = os.path.join(self.root, "home")
        self.ws = os.path.join(self.root, "ws")
        os.makedirs(self.home)
        os.makedirs(self.ws)
        self.env = dict(os.environ, TASKER_HOME=self.home, DOTNET_gcServer="0")
        for name in ("TASKER_PROJECT", "TASKER_PROFILE", "TASKER_SERVICE_LABEL"):
            self.env.pop(name, None)
        self.env["TASKER_SERVICE_LABEL"] = "com.tasker.perf-" + os.path.basename(self.root)[-8:]
        self.port = None

    def run(self, *args, profile=False, check=True, ws=True, cwd=None):
        env = dict(self.env)
        if profile:
            env["TASKER_PROFILE"] = "1"
        cmd = [self.tasker, *args]
        if ws:
            cmd += ["-w", self.ws]
        t0 = time.perf_counter()
        p = subprocess.run(cmd, env=env, capture_output=True, text=True, cwd=cwd)
        dt = (time.perf_counter() - t0) * 1000
        if check and p.returncode != 0:
            raise RuntimeError(f"tasker {' '.join(args)} failed ({p.returncode}): {p.stderr.strip()[:400]}")
        return dt, p

    def setup(self, project="Perf"):
        self.run("project", "create", project)
        self.run("status", "create", "Todo")
        self.run("status", "create", "Done")
        self.run("status-set", "create", "Main", "--status", "Todo", "--status", "Done")
        self.run("task-type", "create", "Bug", "--status-set", "Main")
        self.run("series", "create", "Tasks", "--prefix", "TSK")
        if self.mode == "daemon":
            self.start_daemon()

    def start_daemon(self):
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            self.port = s.getsockname()[1]
        self.run("mcp", "port", str(self.port), ws=False)
        self.run("mcp", "workspace", "add", self.ws, ws=False)
        self.run("mcp", "start", ws=False)

    def close(self):
        if self.mode == "daemon":
            self.run("mcp", "stop", ws=False, check=False)
        if not self.keep:
            shutil.rmtree(self.root, ignore_errors=True)


# ----------------------------------------------------------------------------- статистика / шум

def pct(values, p):
    values = sorted(values)
    k = (len(values) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def summary(values):
    return {"n": len(values), "median": statistics.median(values), "p10": pct(values, 0.1), "p90": pct(values, 0.9),
            "min": min(values), "max": max(values)}


def load():
    return os.getloadavg()[0]


def cpu_busy():
    """Загрузка CPU всей машины в процентах за ~1 с (top)."""
    try:
        out = subprocess.run(["top", "-l", "2", "-n", "0", "-s", "1"], capture_output=True, text=True, timeout=20).stdout
        idle = re.findall(r"CPU usage:.*?([\d.]+)% idle", out)
        return 100 - float(idle[-1]) if idle else float("nan")
    except Exception:
        return float("nan")


def noise_line(tag):
    return f"[noise {tag}] load1={load():.2f} cpu_busy={cpu_busy():.0f}% cores={os.cpu_count()}"


# ----------------------------------------------------------------------------- calls

PROFILE = re.compile(r"^\[profile\] (\S+) ([\d.]+) (\d+)$", re.M)
COUNT = re.compile(r"^\[count\] (\S+) (\d+) ([\d.]+)$", re.M)


def phases(stderr):
    marks = [(m.group(1), float(m.group(2)), int(m.group(3))) for m in PROFILE.finditer(stderr)]
    result, prev, prev_jit = {}, 0.0, 0
    for name, t, jit in marks:
        result[name] = result.get(name, 0.0) + (t - prev)
        result[name + " (jit methods)"] = result.get(name + " (jit methods)", 0) + (jit - prev_jit)
        prev, prev_jit = t, jit
    for m in COUNT.finditer(stderr):
        result[f"#{m.group(1)} x{m.group(2)}"] = float(m.group(3))
    return result


def create_tasks(env, n, start=0, parallel=1):
    def one(i):
        env.run("task", "create", f"task {i}", "--type", "Bug", "--series", "TSK", "-d", "Task description for the benchmark " + "x" * 200)

    if parallel == 1:
        for i in range(start, start + n):
            one(i)
    else:
        with ThreadPoolExecutor(parallel) as ex:
            list(ex.map(one, range(start, start + n)))


def first_task_id(env):
    """Как зовут задачи агенты и скрипты: по ссылке серии (TSK-1)."""
    env.run("task", "get", "TSK-1")  # заодно проверка, что задача есть
    return "TSK-1"


COMMANDS = {
    "version": (lambda e, tid: e.run("--version", ws=False)),
    "status list": (lambda e, tid: e.run("status", "list")),
    "task list --limit 1": (lambda e, tid: e.run("task", "list", "--limit", "1")),
    "task get": (lambda e, tid: e.run("task", "get", tid)),
    "task create": (lambda e, tid: e.run("task", "create", "bench", "--type", "Bug", "--series", "TSK")),
}


def cmd_calls(a):
    env = Env(a.bin, a.mode, a.tmp)
    out = {}
    try:
        env.setup()
        create_tasks(env, a.tasks)
        tid = first_task_id(env)
        print(noise_line("start"))
        print(f"workspace: {a.tasks} tasks, mode={a.mode}, bin={env.bin}\n")
        for name, fn in COMMANDS.items():
            fn(env, tid)  # прогрев кэшей ОС
            times, ph = [], {}
            for _ in range(a.n):
                dt, p = fn(env, tid) if not a.phases else _profiled(env, name, tid)
                times.append(dt)
                if a.phases:
                    for k, v in phases(p.stderr).items():
                        ph.setdefault(k, []).append(v)
            out[name] = {"total": summary(times), "phases": {k: statistics.median(v) for k, v in ph.items()}}
            s = out[name]["total"]
            print(f"{name:22} median {s['median']:7.1f} ms   p10 {s['p10']:7.1f}  p90 {s['p90']:7.1f}  (n={s['n']})")
            for k, v in out[name]["phases"].items():
                print(f"    {k:22} {v:7.1f} ms")
        print(noise_line("end"))
    finally:
        env.close()
    if a.json:
        json.dump(out, open(a.json, "w"), indent=1)


def _profiled(env, name, tid):
    args = {
        "version": ["--version"], "status list": ["status", "list"], "task list --limit 1": ["task", "list", "--limit", "1"],
        "task get": ["task", "get", tid], "task create": ["task", "create", "bench", "--type", "Bug", "--series", "TSK"],
    }[name]
    return env.run(*args, profile=True, ws=(name != "version"))


# ----------------------------------------------------------------------------- degrade

def cmd_degrade(a):
    env = Env(a.bin, a.mode, a.tmp)
    rows = []
    try:
        env.setup()
        print(noise_line("start"))
        print(f"mode={a.mode} clients={a.clients} bin={env.bin}")
        created, calls = 0, 0
        tid = None
        header = f"{'tasks':>6} {'calls':>7} | {'get':>8} {'list1':>8} {'create':>8}   (median ms of {a.n}; p90 in brackets)"
        print(header)
        for target in a.steps:
            create_tasks(env, target - created, created, parallel=a.clients)
            created = target
            tid = tid or first_task_id(env)
            row = {"tasks": created, "calls": calls}
            for name, args in (("get", ["task", "get", tid]), ("list1", ["task", "list", "--limit", "1"]),
                               ("create", ["task", "create", "bench", "--type", "Bug", "--series", "TSK"])):
                times = measure(env, args, a.n, a.clients)
                calls += a.n
                created += a.n if name == "create" else 0
                row[name] = summary(times)
            row["calls"] = calls
            rows.append(row)
            print(f"{row['tasks']:>6} {calls:>7} | " + " ".join(
                f"{row[k]['median']:5.0f}({row[k]['p90']:3.0f})" for k in ("get", "list1", "create")))
            sys.stdout.flush()

        # Зависимость от числа ВЫПОЛНЕННЫХ вызовов при неизменном числе задач: --soak вызовов подряд, срезы по 50.
        if a.soak:
            print(f"\nsoak: {a.soak} x task get on {created} tasks, median per slice of {a.slice}")
            series = []
            for i in range(0, a.soak, a.slice):
                series.append(statistics.median(measure(env, ["task", "get", tid], a.slice, a.clients)))
                print(f"  calls {i + 1:>5}-{i + a.slice:<5} median {series[-1]:6.0f} ms")
            rows.append({"soak": series})
            print(f"  ratio last/first slice = {series[-1] / series[0]:.2f}")
        print(noise_line("end"))
        if env.mode == "daemon":
            print("daemon rss, MB:", rss_mb(env))
    finally:
        env.close()
    if a.json:
        json.dump(rows, open(a.json, "w"), indent=1)


def rss_mb(env):
    try:
        info = json.load(open(os.path.join(env.home, "mcp", "daemon.json")))
        out = subprocess.run(["ps", "-o", "rss=", "-p", str(info["Pid"] if "Pid" in info else info["pid"])], capture_output=True, text=True).stdout
        return int(out.strip()) // 1024
    except Exception as e:
        return f"? ({e})"


def measure(env, args, n, clients):
    """n вызовов; clients > 1 — столько процессов одновременно (время каждого вызова считается отдельно)."""
    times, lock = [], threading.Lock()

    def one(_):
        dt, _p = env.run(*args)
        with lock:
            times.append(dt)

    if clients == 1:
        for i in range(n):
            one(i)
    else:
        with ThreadPoolExecutor(clients) as ex:
            list(ex.map(one, range(n)))
    return times


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("calls", "degrade"):
        s = sub.add_parser(name)
        s.add_argument("--bin", required=True, help="каталог с tasker и tasker-mcpd (scripts/perf/publish.sh)")
        s.add_argument("--mode", choices=["daemon", "nodaemon"], default="daemon")
        s.add_argument("--tmp", help="где создать временную область (по умолчанию системный temp)")
        s.add_argument("--json", help="сохранить сырые замеры")
    c = sub.choices["calls"]
    c.add_argument("--n", type=int, default=25, help="вызовов на команду")
    c.add_argument("--tasks", type=int, default=50, help="задач в области")
    c.add_argument("--phases", action="store_true", help="разбивка по фазам (TASKER_PROFILE=1)")
    d = sub.choices["degrade"]
    d.add_argument("--steps", type=int, nargs="+", default=[100, 500, 1000, 2000], help="число задач на шагах")
    d.add_argument("--n", type=int, default=15, help="вызовов каждой команды на шаге")
    d.add_argument("--clients", type=int, default=1, help="параллельных клиентов")
    d.add_argument("--soak", type=int, default=0, help="подряд вызовов task get в конце (зависимость от числа вызовов)")
    d.add_argument("--slice", type=int, default=50)
    a = p.parse_args()
    {"calls": cmd_calls, "degrade": cmd_degrade}[a.cmd](a)


if __name__ == "__main__":
    main()
