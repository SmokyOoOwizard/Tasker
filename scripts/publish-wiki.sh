#!/bin/bash
# Публикует страницы вики из docs/wiki в вики репозитория на GitHub (отдельный git-репозиторий <репозиторий>.wiki.git).
#
#   scripts/publish-wiki.sh [--dry-run] [--message "Текст коммита"]
#
# Нужны права на запись в репозиторий (ваши логин и токен GitHub или ключ SSH, как для обычного `git push`).
# ОДИН РАЗ вручную: вики репозитория появляется только после первой страницы — откройте вкладку Wiki на GitHub,
# нажмите «Create the first page» и сохраните любую страницу (она будет заменена). Дальше скрипт сам клонирует вики,
# заменяет страницы содержимым docs/wiki и пушит. Страницы, которых нет в docs/wiki, из вики удаляются.
set -euo pipefail

dry=0
message="Update wiki from docs/wiki"
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) dry=1 ;;
    --message) message="${2:?--message needs a text}"; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/docs/wiki"
[ -d "$src" ] || { echo "No $src" >&2; exit 1; }

origin="$(git -C "$root" remote get-url origin)"
case "$origin" in
  *.git) wiki="${origin%.git}.wiki.git" ;;
  *) wiki="$origin.wiki.git" ;;
esac
echo "Wiki repository: $wiki"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
git clone --quiet "$wiki" "$work/wiki" || {
  echo "Could not clone the wiki. Create its first page on GitHub (Wiki tab) once, and check your access rights." >&2
  exit 1
}

# Заменяем содержимое целиком (кроме .git), чтобы удалённые страницы исчезли.
find "$work/wiki" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
cp "$src"/*.md "$work/wiki/"

cd "$work/wiki"
git add -A
if git diff --cached --quiet; then
  echo "Nothing to publish: the wiki is already up to date."
  exit 0
fi
git status --short
if [ "$dry" = 1 ]; then
  echo "Dry run: nothing was pushed."
  exit 0
fi
git commit --quiet -m "$message"
git push --quiet origin HEAD
echo "Published $(ls "$src"/*.md | wc -l | tr -d ' ') pages."
