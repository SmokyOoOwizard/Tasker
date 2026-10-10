#!/usr/bin/env python3
"""Смешанные пары супервизор/рабочий процесс демона MCP (TSK-139): супервизор одной сборки (.NET или Rust) заменяет рабочий процесс
на другую сборку и обратно (`/daemon/upgrade`, как `tasker mcp upgrade --daemon`) под непрерывной нагрузкой новыми соединениями:
`/health`, `/mcp` list_projects и create_task. Порт не должен закрываться ни на миг: ни одного отказа соединения, ни одного не-200,
каждая подтверждённая запись есть в файлах ровно один раз, супервизор тот же.

  python3 -I scripts/test-mixed-upgrade.py <программа супервизора> <программа другого рабочего процесса> <область> [замен]

  # .NET-супервизор -> Rust-рабочий процесс -> .NET -> Rust
  python3 -I scripts/test-mixed-upgrade.py <bin .NET>/tasker-mcpd rust/target/release/tasker-mcpd rust/tests/golden/workspace 3
  # Rust-супервизор -> .NET-рабочий процесс -> Rust -> .NET
  python3 -I scripts/test-mixed-upgrade.py rust/target/release/tasker-mcpd <bin .NET>/tasker-mcpd rust/tests/golden/workspace 3

Область — копия корпуса `rust/tests/golden/workspace` (нужны проект Golden и тип Feature). MIXED_HANDOFF=message — передача сокета
сообщением (`--listen-handoff`, как на Windows). Всё изолировано: TASKER_HOME, TASKER_SERVICE_DIR, TASKER_SERVICE_LABEL во временной
папке, свободный порт; настоящий демон пользователя не затрагивается. Код выхода 0 — всё в порядке.
"""
import json, os, shutil, socket, subprocess, sys, tempfile, threading, time

PROJECT = "11111111-1111-4111-8111-111111111111"
FEATURE = "0000002b-0000-4000-8000-00000000002b"


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


def http(port, method, path, headers=None, body=b"", timeout=60):
    s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
    try:
        head = f"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nContent-Length: {len(body)}\r\n"
        for k, v in (headers or {}).items():
            head += f"{k}: {v}\r\n"
        if body:
            head += "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n"
        s.sendall(head.encode() + b"\r\n" + body)
        data = b""
        while True:
            chunk = s.recv(65536)
            if not chunk:
                break
            data += chunk
    finally:
        s.close()
    head, _, body = data.partition(b"\r\n\r\n")
    head = head.decode("latin-1")
    code = int(head.split(" ", 2)[1])
    if "transfer-encoding: chunked" in head.lower():
        decoded = b""
        while body:
            size_line, _, rest = body.partition(b"\r\n")
            size = int(size_line.split(b";")[0], 16)
            if size == 0:
                break
            decoded += rest[:size]
            body = rest[size + 2:]
        body = decoded
    return code, body.decode("utf-8", "replace")


def tool(name, args):
    return json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": args}}).encode()


def result_of(body):
    for line in body.splitlines():
        if line.startswith("data:"):
            r = json.loads(line[5:])["result"]
            return r.get("isError", False), r["content"][0]["text"]
    return None


def main():
    supervisor, worker, golden = sys.argv[1], sys.argv[2], sys.argv[3]
    rounds = int(sys.argv[4]) if len(sys.argv) > 4 else 2
    root = tempfile.mkdtemp(prefix="tasker-mixed-")
    home, ws = os.path.join(root, "home"), os.path.join(root, "ws")
    os.makedirs(home)
    shutil.copytree(golden, ws)
    port = free_port()
    with open(os.path.join(home, "settings.json"), "w") as f:
        json.dump({"mcp": {"port": port, "workspaces": [{"kind": "files", "path": ws}]}}, f)
    env = dict(os.environ, TASKER_HOME=home, TASKER_SERVICE_DIR=os.path.join(root, "service"),
               TASKER_SERVICE_LABEL=f"com.tasker.mixed-{port}", TASKER_MCP_DRAIN_QUIET_MS="300")
    for k in ("TASKER_MCP_HANDOFF", "TASKER_BIN", "TASKER_MCPD_BIN"):
        env.pop(k, None)
    if os.environ.get("MIXED_HANDOFF"):
        env["TASKER_MCP_HANDOFF"] = os.environ["MIXED_HANDOFF"]
    out = open(os.path.join(root, "supervisor.out"), "w")
    proc = subprocess.Popen([supervisor], env=env, stdout=out, stderr=subprocess.STDOUT)
    failures, acknowledged, served = [], [], {}
    stop = threading.Event()
    lock = threading.Lock()

    def status():
        token = json.load(open(os.path.join(home, "mcp", "daemon.json")))["token"]
        code, body = http(port, "GET", "/daemon/status", {"X-Tasker-Control": token})
        return json.loads(body) if code == 200 else None

    def wait(cond, limit=60):
        deadline = time.time() + limit
        last = None
        while time.time() < deadline:
            try:
                if cond():
                    return
            except Exception as e:
                last = repr(e)
            time.sleep(0.05)
        try:
            last = (last, status())
        except Exception as e:
            last = (last, repr(e))
        raise SystemExit(f"timeout; last: {last}")

    def settled(s):
        return s and len(s["workers"]) == 1 and s["workers"][0]["role"] == "active" and s["workers"][0]["build"] \
            and all(w["state"] == "open" for w in s["workspaces"])

    try:
        wait(lambda: settled(status()))
        first = status()["workers"][0]
        print(f"supervisor pid {proc.pid}, port {port}: worker {first['pid']} build {first['build']} ({supervisor})")

        def writer(client):
            n = 0
            while not stop.is_set():
                title = f"mixed {client}-{n}"; n += 1
                try:
                    code, body = http(port, "POST", "/mcp", body=tool("create_task", {"workspace": "ws", "projectId": PROJECT, "typeId": FEATURE, "title": title}))
                    r = result_of(body) if code == 200 else None
                    if r and not r[0]:
                        with lock: acknowledged.append(title)
                    else:
                        with lock: failures.append(f"{title}: HTTP {code} {body[:200]}")
                except Exception as e:
                    with lock: failures.append(f"{title}: {type(e).__name__} {e}")

        def prober():
            while not stop.is_set():
                try:
                    code, body = http(port, "GET", "/health")
                    if code != 200:
                        with lock: failures.append(f"health: HTTP {code}")
                    else:
                        pid = json.loads(body)["pid"]
                        with lock: served[pid] = served.get(pid, 0) + 1
                    code, body = http(port, "POST", "/mcp", body=tool("list_projects", {"workspace": "ws"}))
                    if code != 200 or "Golden" not in body:
                        with lock: failures.append(f"list_projects: HTTP {code} {body[:200]}")
                except Exception as e:
                    with lock: failures.append(f"probe: {type(e).__name__} {e}")
                time.sleep(0.005)

        threads = [threading.Thread(target=writer, args=(i,)) for i in range(4)] + [threading.Thread(target=prober) for _ in range(2)]
        for t in threads: t.start()
        time.sleep(1.5)
        programs = [worker, supervisor]
        for r in range(rounds):
            target = programs[r % 2]
            token = json.load(open(os.path.join(home, "mcp", "daemon.json")))["token"]
            started = time.time()
            code, body = http(port, "POST", "/daemon/upgrade", {"X-Tasker-Control": token},
                              json.dumps({"file": target, "arguments": [], "timeoutSeconds": 60}).encode(), timeout=120)
            res = json.loads(body)
            print(f"upgrade {r + 1} -> {target}: HTTP {code} in {time.time() - started:.1f} s: {res['message']}")
            if not res["ok"]:
                raise SystemExit("upgrade failed")
            wait(lambda: settled(status()))
            time.sleep(1.0)
        time.sleep(1.0)
        stop.set()
        for t in threads: t.join()
        final = status()
        tasks_dir = os.path.join(ws, ".tasker", "projects", PROJECT, "tasks")
        titles = []
        for name in os.listdir(tasks_dir):
            for line in open(os.path.join(tasks_dir, name), encoding="utf-8"):
                if line.startswith("title: "):
                    titles.append(line[7:].strip().strip("'\""))
        mixed = sorted(t for t in titles if t.startswith("mixed "))
        print(f"calls acknowledged: {len(acknowledged)}, failures: {len(failures)}, served by: {served}")
        print(f"final worker {final['workers'][0]['pid']} build {final['workers'][0]['build']} version {final['workers'][0]['version']}, supervisor pid {final['pid']} (same: {final['pid'] == proc.pid})")
        writes_ok = mixed == sorted(acknowledged)
        print(f"writes: {len(mixed)} in files, exactly the acknowledged ones: {writes_ok}")
        for f in failures[:10]:
            print("  FAIL", f)
        ok = not failures and writes_ok and len(served) >= rounds + 1 and final["pid"] == proc.pid
        print("RESULT", "OK" if ok else "FAILED")
        return 0 if ok else 1
    finally:
        stop.set()
        proc.terminate()
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()
        out.close()
        print("--- supervisor log tail ---")
        print("".join(open(os.path.join(root, "supervisor.out")).readlines()[-12:]))
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
