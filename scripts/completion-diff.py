#!/usr/bin/env python3
"""
Сверка автодополнения (директива [suggest]) .NET- и Rust-консоли байт в байт (TSK-136).

  scripts/completion-diff.py --net <tasker .NET> --rs <tasker Rust> [--mode seeded|two-projects|empty] [--jobs 6]

Во временном каталоге (свой TASKER_HOME) .NET-консолью засевается область, как в CompletionTests: проект «Мой проект», статусы,
тип, серия TSK и 11 задач, перечисление, поля, доска, пользователь и агент (two-projects — плюс второй проект; empty — области нет).
Корпус строк: строки из tests/Tasker.Tests/{CompletionTests,ManualCompletionTests}.cs и обход дерева по ответам .NET (команды
до третьего уровня, «-», «--», первая буква каждого варианта, каждый параметр без значения, со значением «x», с «x --» и с «В»);
каждая строка проверяется и с «--workspace "<область>"» впереди. Вывод, код и stderr обеих консолей сравниваются; расхождения —
в stdout и в mismatches.json. У .NET у источников значений срок 1,5 с: под нагрузкой ответ бывает пустым — повторите с меньшим --jobs.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SEED = [
    ['project', 'create', 'Мой проект'], ['status', 'create', 'В работе'], ['status', 'create', 'Готово'], ['status', 'create', 'Лишний'],
    ['status-set', 'create', 'Основной', '--status', 'В работе', 'Готово'], ['task-type', 'create', 'Фича', '--status-set', 'Основной'],
    ['series', 'create', 'Задачи', '--prefix', 'TSK'],
    *[['task', 'create', f'Задача {i}', '--type', 'Фича', '--series', 'TSK'] for i in range(1, 12)],
    ['enum', 'create', 'Приоритет', '--value', 'Высокий', 'Низкий', 'Очень срочно'], ['field', 'create', 'Приоритет', '--type', 'enum', '--enum', 'Приоритет'],
    ['field', 'create', 'Готово', '--type', 'bool'], ['field', 'create', 'StoryPoints', '--type', 'int'],
    ['board', 'create', 'Основная доска', '--status-set', 'Основной', '--column', 'Все=В работе', '--column', 'Сделано=Готово'],
    ['user', 'create', 'alice'], ['agent', 'create', 'bot'],
]


def test_lines():
    """Строки, которые набирают тесты .NET (аргументы Suggest/Values и InlineData)."""
    out = set()
    for name in ('CompletionTests.cs', 'ManualCompletionTests.cs'):
        text = open(os.path.join(REPO, 'tests', 'Tasker.Tests', name), encoding='utf-8').read()
        for m in re.finditer(r'(?:Values|Suggest)\((?:ws, )?"((?:[^"\\]|\\.)*)"|\[InlineData\("((?:[^"\\]|\\.)*)"', text):
            raw = m.group(1) if m.group(1) is not None else m.group(2)
            out.add(raw.encode().decode('unicode_escape').encode('latin1').decode('utf-8'))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--net', required=True, help='.NET-консоль (tasker из scripts/perf/publish.sh)')
    p.add_argument('--rs', required=True, help='Rust-консоль (rust/target/release/tasker)')
    p.add_argument('--mode', choices=['seeded', 'two-projects', 'empty'], default='seeded')
    p.add_argument('--jobs', type=int, default=6)
    p.add_argument('--json', default='mismatches.json')
    a = p.parse_args()
    net, rs = os.path.abspath(a.net), os.path.abspath(a.rs)

    root = tempfile.mkdtemp(prefix='tasker-completion-diff-')
    ws, home = os.path.join(root, 'ws'), os.path.join(root, 'home')
    os.makedirs(ws)
    os.makedirs(home)
    env = dict(os.environ, TASKER_HOME=home)
    for name in ('TASKER_PROJECT', 'TASKER_PROFILE', 'TASKER_LANG'):
        env.pop(name, None)

    def run(binary, *args):
        return subprocess.run([binary, *args], env=env, capture_output=True, cwd=ws)

    def suggest(binary, line):
        r = run(binary, f'[suggest:{len(line)}]', line)
        return r.returncode, r.stdout.decode(), r.stderr.decode()

    try:
        if a.mode != 'empty':
            for args in SEED + ([['project', 'create', 'Второй'], ['status', 'create', 'Свой статус', '-p', 'Второй']]
                                if a.mode == 'two-projects' else []):
                r = run(net, *args, '-w', ws)
                if r.returncode:
                    sys.exit(f'seed {args} failed: {r.stderr.decode()}')

        lines = test_lines()
        frontier, seen = [''], set()
        for depth in range(3):
            nxt = []
            with ThreadPoolExecutor(a.jobs) as ex:
                answers = list(ex.map(lambda l: (l, suggest(net, l)), frontier))
            for line, (_, out, _) in answers:
                lines.update((line, line + '--', line + '-'))
                for w in filter(None, out.split('\n')):
                    if w.startswith('-'):
                        continue
                    if depth < 2 and (line + w) not in seen and re.match(r'^[a-z][a-z-]*$', w):
                        seen.add(line + w)
                        nxt.append(line + w + ' ')
                    lines.add(line + w[:1])
            frontier = nxt
        paths = [l for l in lines if l.endswith(' ') and not l.strip().startswith('-')]
        with ThreadPoolExecutor(a.jobs) as ex:
            answers = list(ex.map(lambda l: (l, suggest(net, l + '--')), paths))
        for line, (_, out, _) in answers:
            for o in filter(None, out.split('\n')):
                lines.update((f'{line}{o} ', f'{line}{o} x ', f'{line}{o} x --', f'{line}{o} В'))

        variants = []
        for line in sorted(lines):
            variants += [line, f'--workspace "{ws}" {line}']
        with ThreadPoolExecutor(a.jobs) as ex:
            results = list(ex.map(lambda l: (l, suggest(net, l), suggest(rs, l)), variants))
        bad = [(l, n, r) for l, n, r in results if n != r]
        for line, n, r in bad[:100]:
            print('LINE', repr(line))
            print('  net', n)
            print('  rs ', r)
        json.dump([{'line': l, 'net': n, 'rs': r} for l, n, r in bad], open(a.json, 'w'), ensure_ascii=False, indent=1)
        print(f'{a.mode}: {len(bad)} mismatches of {len(results)} lines')
        sys.exit(1 if bad else 0)
    finally:
        shutil.rmtree(root, ignore_errors=True)


if __name__ == '__main__':
    main()
