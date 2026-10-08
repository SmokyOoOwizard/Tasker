#!/bin/bash
# Устанавливает консольную утилиту tasker и демон MCP (tasker-mcpd) в каталог пользователя на macOS и Linux, чтобы команда `tasker`
# была доступна из любого места. Права администратора не нужны, .NET ставить не нужно (сборка самодостаточная).
# (Windows: scripts/install.ps1.)
#
#   install.sh                                  из распакованного релиза (рядом со скриптом лежит каталог app/)
#   install.sh --from ФАЙЛ.tar.gz | КАТАЛОГ     из архива релиза или каталога с готовой сборкой
#   install.sh --url АДРЕС.tar.gz               скачать архив релиза (рядом должен лежать АДРЕС.tar.gz.sha256)
#   install.sh --from-source                    собрать из исходников репозитория (нужен .NET SDK 10), для разработчиков
#   install.sh --uninstall                      удалить
#   прочее: [--prefix DIR] [--no-completion] [--no-autostart] [--add-to-path] [--restart] [--no-verify]
#           [--framework-dependent] [--no-daemon]   (последние два — только с --from-source)
#
# Раскладка (по умолчанию --prefix ~/.local):
#   <prefix>/share/tasker/app/   tasker и tasker-mcpd (self-contained: .NET внутри, ничего ставить не нужно)
#   <prefix>/bin/tasker          ссылка на <prefix>/share/tasker/app/tasker (демон tasker запускает из своего каталога)
#   <prefix>/share/tasker/completions/   скрипты автодополнения по Tab (zsh: _tasker, bash: tasker.bash), см. ниже
# Автозапуск: `tasker mcp autostart enable` (launchd на macOS, служба systemd пользователя на Linux); --no-autostart его не включает.
# Работавший демон MCP переходит на новую сборку без простоя (`tasker mcp upgrade`: новый процесс поднимается рядом, старый заканчивает
# начатые вызовы); --restart вместо этого перезапускает его (вызовы в работе обрываются). Не получилось заменить — старый продолжает
# работать, перезапуск молча НЕ делается: скрипт печатает причину и подсказку `tasker mcp upgrade --restart`.
# Данные Tasker (настройки, журналы демона) лежат отдельно (macOS: ~/Library/Application Support/Tasker, Linux: ~/.local/share/Tasker)
# и установкой, обновлением и удалением не затрагиваются.
#
# Автодополнение по Tab: скрипты кладутся всегда, а в файл настроек оболочки (по $SHELL: zsh — ~/.zshrc, bash — на Linux ~/.bashrc,
# на macOS файл, который читает login-оболочка) дописывается один помеченный блок «# >>> tasker completion >>>» ...
# «# <<< tasker completion <<<». Повторная установка заменяет блок, а не дублирует; перед первой правкой делается копия
# <файл>.tasker-backup; --no-completion файл не трогает; --uninstall убирает блок и скрипты. Остальное содержимое файла не меняется.

set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: install.sh [options]

Installs the tasker command line tool and the MCP server (tasker-mcpd) for the current user (macOS and Linux).
Where the files come from (one of; default: the release this script came with, i.e. the app/ folder next to it):
  --from PATH             a release archive (.tar.gz) or a folder with the ready build (tasker, tasker-mcpd inside it or in its app/)
  --url URL               download a release archive; URL.sha256 next to it is checked
  --from-source           build from the repository sources (needs the .NET SDK 10): for developers

Options:
  --prefix DIR            install under DIR (default: ~/.local): DIR/share/tasker/app and DIR/bin/tasker
  --no-autostart          do not enable the autostart of the MCP server (launchd on macOS, systemd --user on Linux);
                          by default 'tasker mcp autostart enable' is run (an enabled autostart is left as it is)
  --no-completion         do not connect Tab completion to your shell (its scripts are still installed);
                          by default one marked block is added to ~/.zshrc (zsh) or the bash startup file (bash)
  --add-to-path           add DIR/bin to PATH in your shell profile (~/.zprofile for zsh, ~/.profile or the bash login file)
                          if it is not there yet
  --restart               restart a running MCP server after the installation (calls in progress are cut off);
                          by default it is switched to the new build without downtime ('tasker mcp upgrade')
  --no-verify             with --url: install even if the .sha256 file cannot be downloaded
  --framework-dependent   with --from-source: small build (~50 MB) that needs the .NET 10 runtime installed
  --no-daemon             with --from-source: only the command line tool (no MCP server: 'tasker mcp start' will not work)
  --uninstall             remove the tool (stops the MCP server and disables its autostart first)
                          and the Tab completion block it added to your shell startup file
  -h, --help              show this help

Your data (settings, logs: ~/Library/Application Support/Tasker on macOS, ~/.local/share/Tasker on Linux) is never touched.
USAGE
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

# Неверные параметры: сообщение, справка, код 2.
bad_usage() {
  echo "Error: $*" >&2
  usage >&2
  exit 2
}

PREFIX="$HOME/.local"
FROM=""
URL=""
FROM_SOURCE=false
SELF_CONTAINED=true
ADD_TO_PATH=false
WITH_COMPLETION=true
WITH_AUTOSTART=true
UNINSTALL=false
WITH_DAEMON=true
DAEMON_FLAG_GIVEN=false
FRAMEWORK_FLAG_GIVEN=false
RESTART_DAEMON=false
VERIFY=true

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix)
      [ $# -ge 2 ] || bad_usage "--prefix needs a directory"
      PREFIX="$2"
      shift 2
      ;;
    --from)
      [ $# -ge 2 ] || bad_usage "--from needs a file or a directory"
      FROM="$2"
      shift 2
      ;;
    --url)
      [ $# -ge 2 ] || bad_usage "--url needs an address"
      URL="$2"
      shift 2
      ;;
    --from-source) FROM_SOURCE=true; shift ;;
    --framework-dependent) SELF_CONTAINED=false; FRAMEWORK_FLAG_GIVEN=true; shift ;;
    --no-daemon) WITH_DAEMON=false; DAEMON_FLAG_GIVEN=true; shift ;;
    --restart) RESTART_DAEMON=true; shift ;;
    --add-to-path) ADD_TO_PATH=true; shift ;;
    --no-completion) WITH_COMPLETION=false; shift ;;
    --no-autostart) WITH_AUTOSTART=false; shift ;;
    --no-verify) VERIFY=false; shift ;;
    --uninstall) UNINSTALL=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) bad_usage "unknown option '$1'" ;;
  esac
done

SOURCES=0
[ -n "$FROM" ] && SOURCES=$((SOURCES + 1))
[ -n "$URL" ] && SOURCES=$((SOURCES + 1))
[ "$FROM_SOURCE" = true ] && SOURCES=$((SOURCES + 1))
[ "$SOURCES" -le 1 ] || bad_usage "choose one of --from, --url, --from-source"
if [ "$FROM_SOURCE" = false ]; then
  [ "$FRAMEWORK_FLAG_GIVEN" = false ] || bad_usage "--framework-dependent works with --from-source only (release builds are self-contained)"
  [ "$DAEMON_FLAG_GIVEN" = false ] || bad_usage "--no-daemon works with --from-source only (a release always includes the MCP server)"
fi

# ---- система и архитектура ----

case "$(uname -s)" in
  Darwin) OS=osx ;;
  Linux) OS=linux ;;
  MINGW*|MSYS*|CYGWIN*) fail "this script is for macOS and Linux: on Windows use scripts/install.ps1 in PowerShell" ;;
  *) fail "unsupported system: $(uname -s) (macOS and Linux are supported; Windows: scripts/install.ps1)" ;;
esac

if [ "$OS" = osx ]; then
  DATA_TEXT="~/Library/Application Support/Tasker"
else
  DATA_TEXT="~/.local/share/Tasker"
fi

# Платформа сборки (RID .NET): osx-arm64, osx-x64, linux-arm64, linux-x64.
detect_rid() {
  local arch
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64) arch=x64 ;;
    *) fail "unsupported CPU: $(uname -m) (arm64 and x64 are supported)" ;;
  esac
  # Оболочка под Rosetta на Mac с Apple Silicon видит x86_64, а ставить надо родную сборку.
  if [ "$OS" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then
    arch=arm64
  fi
  if [ "$OS" = linux ] && command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
    fail "this Linux uses musl (Alpine): the release builds need glibc"
  fi
  echo "$OS-$arch"
}

# Абсолютный путь без завершающего «/»: по нему строятся ссылки.
absolute() {
  local path=$1
  case "$path" in
    /*) ;;
    "~"|"~/"*) path="$HOME${path#\~}" ;;
    *) path="$PWD/$path" ;;
  esac
  echo "${path%/}"
}

PREFIX="$(absolute "$PREFIX")"

BIN_DIR="$PREFIX/bin"
SHARE_DIR="$PREFIX/share/tasker"
APP_DIR="$SHARE_DIR/app"
LINK="$BIN_DIR/tasker"
COMPLETIONS_DIR="$SHARE_DIR/completions"

# Как вызвать этот скрипт повторно (в подсказках); из конвейера (curl | bash) файла скрипта нет.
SCRIPT_FILE="${BASH_SOURCE[0]:-}"
if [ -n "$SCRIPT_FILE" ] && [ -f "$SCRIPT_FILE" ]; then
  SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_FILE")" && pwd)"
  SELF="$SCRIPT_DIR/$(basename "$SCRIPT_FILE")"
else
  SCRIPT_DIR=""
  SELF="install.sh"
fi

# ---- автодополнение: оболочки и помеченный блок в их файлах настроек ----

COMPLETION_BEGIN="# >>> tasker completion >>>"
COMPLETION_END="# <<< tasker completion <<<"
COMPLETION_BACKUP_SUFFIX=".tasker-backup"

# Оболочки с автодополнением. Новая оболочка (fish) — это скрипт в src/Tasker.Cli/Completion/Shells,
# строка в Shells.cs и по ветке в completion_file, completion_rc и completion_block ниже.
COMPLETION_SHELLS=(zsh bash)

# Имя файла скрипта оболочки $1 в $COMPLETIONS_DIR.
completion_file() {
  case "$1" in
    zsh) echo "_tasker" ;;
    bash) echo "tasker.bash" ;;
  esac
}

# Файл настроек, который читает оболочка $1. Linux: bash читает ~/.bashrc (интерактивная оболочка). macOS: bash в Terminal —
# login-оболочка: она читает первый существующий из ~/.bash_profile, ~/.bash_login, ~/.profile (а не ~/.bashrc); нет ни одного —
# создаётся ~/.bash_profile.
completion_rc() {
  case "$1" in
    zsh) echo "$HOME/.zshrc" ;;
    bash)
      if [ "$OS" = linux ]; then
        echo "$HOME/.bashrc"
        return
      fi
      local name
      for name in .bash_profile .bash_login .profile; do
        if [ -e "$HOME/$name" ]; then
          echo "$HOME/$name"
          return
        fi
      done
      echo "$HOME/.bash_profile"
      ;;
  esac
}

# Файл профиля входа, куда --add-to-path дописывает PATH: zsh — ~/.zprofile; bash — файл входа macOS или ~/.profile (Linux);
# прочие оболочки — ~/.zprofile на macOS и ~/.profile на Linux.
path_profile() {
  case "$(basename "${SHELL:-}")" in
    zsh) echo "$HOME/.zprofile" ;;
    bash)
      if [ "$OS" = linux ]; then echo "$HOME/.profile"; else completion_rc bash; fi
      ;;
    *)
      if [ "$OS" = linux ]; then echo "$HOME/.profile"; else echo "$HOME/.zprofile"; fi
      ;;
  esac
}

# Где ещё мог остаться наш блок (после смены оболочки или файла): их просматривает удаление.
COMPLETION_RC_CANDIDATES=("$HOME/.zshrc" "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile" "$HOME/.bashrc")

# Блок для оболочки $1 вместе с маркерами. Условие «файл есть»: удалили скрипт руками — оболочка не ругается при запуске.
completion_block() {
  local file="$COMPLETIONS_DIR/$(completion_file "$1")"
  echo "$COMPLETION_BEGIN"
  echo "# Tab completion for tasker, added by install.sh. To undo: delete this block or run install.sh --uninstall."
  case "$1" in
    zsh)
      echo "if [ -f \"$file\" ]; then"
      echo "  fpath=(\"$COMPLETIONS_DIR\" \$fpath)"
      echo "  autoload -Uz compinit _tasker"
      echo "  (( \$+functions[compdef] )) || compinit"
      echo "  compdef _tasker tasker"
      echo "fi"
      ;;
    bash)
      echo "if [ -f \"$file\" ]; then . \"$file\"; fi"
      ;;
  esac
  echo "$COMPLETION_END"
}

# В файле $1 есть и начало, и конец нашего блока.
has_block() {
  [ -f "$1" ] && grep -qxF -- "$COMPLETION_BEGIN" "$1" && grep -qxF -- "$COMPLETION_END" "$1"
}

# Один из маркеров есть, а второго нет: блок повреждён, файл не трогаем.
has_half_block() {
  [ -f "$1" ] || return 1
  local begin=0 end=0
  grep -qxF -- "$COMPLETION_BEGIN" "$1" && begin=1
  grep -qxF -- "$COMPLETION_END" "$1" && end=1
  [ "$begin" != "$end" ]
}

# Печатает файл $1 с нашим блоком: show — только блок; remove — без блока (и без пустой строки перед ним, которую добавляли мы);
# replace — с блоком из файла $3 вместо старого. Остальные строки не меняются.
rewrite_block() {
  local file=$1 mode=$2 block=${3:-/dev/null}
  awk -v begin="$COMPLETION_BEGIN" -v end="$COMPLETION_END" -v mode="$mode" -v blockfile="$block" '
    { line[NR] = $0 }
    END {
      b = 0; e = 0
      for (i = 1; i <= NR; i++) {
        if (!b && line[i] == begin) b = i
        else if (b && !e && line[i] == end) e = i
      }
      if (mode == "show") {
        for (i = b; i <= e; i++) print line[i]
        exit
      }
      for (i = 1; i < b; i++) {
        if (mode == "remove" && i == b - 1 && line[i] == "") continue
        print line[i]
      }
      if (mode == "replace") {
        while ((getline text < blockfile) > 0) print text
      }
      for (i = e + 1; i <= NR; i++) print line[i]
    }' "$file"
}

# Подменяет содержимое файла $1 текстом из $2, не заменяя сам файл: права, владелец и ссылка (dotfiles) остаются.
overwrite() {
  cat "$2" > "$1"
}

# Копия файла настроек перед первой нашей правкой (повторные правки её не затирают).
backup_once() {
  if [ -e "$1" ] && [ ! -e "$1$COMPLETION_BACKUP_SUFFIX" ]; then
    cp -p "$1" "$1$COMPLETION_BACKUP_SUFFIX"
    echo "  backup:    $1$COMPLETION_BACKUP_SUFFIX (the file as it was before)"
  fi
}

# Подключает автодополнение к оболочке $1 и печатает, что сделано. Блок один: есть и актуален — файл не трогаем,
# есть старый — заменяем на месте, нет — дописываем в конец.
wire_completion() {
  local shell=$1 rc block work
  rc="$(completion_rc "$shell")"
  block="$(mktemp)"
  completion_block "$shell" > "$block"

  if has_half_block "$rc"; then
    echo "  skipped:   $rc has only one of the '$COMPLETION_BEGIN' / '$COMPLETION_END' lines: fix or delete it, then run this script again"
  elif has_block "$rc"; then
    if [ "$(rewrite_block "$rc" show)" = "$(cat "$block")" ]; then
      echo "  unchanged: $rc already has the tasker completion block"
    else
      backup_once "$rc"
      work="$(mktemp)"
      rewrite_block "$rc" replace "$block" > "$work"
      overwrite "$rc" "$work"
      rm -f "$work"
      echo "  changed:   $rc (the tasker completion block is updated)"
    fi
  else
    backup_once "$rc"
    # Блок отделяется от прежнего содержимого пустой строкой (её же убирает удаление).
    if [ -s "$rc" ]; then
      [ -z "$(tail -c1 "$rc")" ] || echo >> "$rc"
      echo >> "$rc"
    fi
    cat "$block" >> "$rc"
    echo "  changed:   $rc (the tasker completion block is added at the end)"
  fi
  rm -f "$block"
}

# Скрипты автодополнения всех оболочек — в $COMPLETIONS_DIR; их печатает установленный tasker. Не получилось — не беда для установки.
install_completion_scripts() {
  local shell file
  mkdir -p "$COMPLETIONS_DIR"
  for shell in "${COMPLETION_SHELLS[@]}"; do
    file="$COMPLETIONS_DIR/$(completion_file "$shell")"
    if "$APP_DIR/tasker" completion "$shell" > "$file.new" && [ -s "$file.new" ]; then
      mv "$file.new" "$file"
    else
      rm -f "$file.new"
      return 1
    fi
  done
}

# ---- удаление ----

if [ "$UNINSTALL" = true ]; then
  if [ ! -e "$APP_DIR" ] && [ ! -L "$LINK" ] && [ ! -e "$COMPLETIONS_DIR" ]; then
    echo "Nothing to uninstall: tasker is not installed under $PREFIX"
    exit 0
  fi

  if [ -x "$APP_DIR/tasker" ]; then
    # Служба автозапуска (launchd, systemd) и git-хуки хранят путь к утилите: службу выключаем, пока утилита ещё есть.
    "$APP_DIR/tasker" mcp autostart disable >/dev/null 2>&1 || true
    "$APP_DIR/tasker" mcp stop >/dev/null 2>&1 || true
  fi

  # Ссылку удаляем, только если она ведёт в нашу установку.
  if [ -L "$LINK" ] && [ "$(readlink "$LINK")" = "$APP_DIR/tasker" ]; then
    rm -f "$LINK"
  fi
  rm -rf "$APP_DIR"

  # Блок автодополнения убираем из файлов оболочек, только если он ведёт в эту установку (у другой --prefix свой).
  for RC in "${COMPLETION_RC_CANDIDATES[@]}"; do
    if has_block "$RC" && rewrite_block "$RC" show | grep -qF -- "$COMPLETIONS_DIR"; then
      WORK="$(mktemp)"
      rewrite_block "$RC" remove > "$WORK"
      overwrite "$RC" "$WORK"
      rm -f "$WORK"
      echo "Removed the tasker completion block from $RC"
      if [ -e "$RC$COMPLETION_BACKUP_SUFFIX" ]; then
        echo "  the copy made before the first change is kept: $RC$COMPLETION_BACKUP_SUFFIX (delete it if you do not need it)"
      fi
    fi
  done
  rm -rf "$COMPLETIONS_DIR"
  rmdir "$SHARE_DIR" 2>/dev/null || true

  echo "tasker is uninstalled from $PREFIX"
  echo "Your data in $DATA_TEXT is kept."
  echo "Git hooks installed with 'tasker hooks install' stay in place and do nothing without tasker:"
  echo "remove them with 'tasker hooks uninstall' before uninstalling if you do not need them."
  exit 0
fi

# ---- откуда берём сборку ----

RID="$(detect_rid)"
mkdir -p "$SHARE_DIR" "$BIN_DIR"
STAGING="$(mktemp -d "$SHARE_DIR/.staging.XXXXXX")"
TEMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "$STAGING" "$TEMP_DIR"; }
trap cleanup EXIT

# Хэш SHA256 файла $1.
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  else
    fail "cannot check the download: neither sha256sum nor shasum is available"
  fi
}

# Каталог со сборкой внутри $1: сам каталог (там tasker), его app/ (так устроен релиз) или единственный вложенный каталог релиза.
find_app() {
  local root=$1 candidate
  for candidate in "$root" "$root/app"; do
    if [ -f "$candidate/tasker" ]; then
      echo "$candidate"
      return 0
    fi
  done
  for candidate in "$root"/*/app "$root"/*; do
    if [ -f "$candidate/tasker" ]; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

# Распаковывает архив $1 в $2.
extract() {
  case "$1" in
    *.tar.gz|*.tgz) tar -xzf "$1" -C "$2" ;;
    *.zip) fail "$1 is a Windows archive: on macOS and Linux use the .tar.gz one" ;;
    *) fail "unknown archive type: $1 (a .tar.gz release archive or a folder is expected)" ;;
  esac
}

# Платформа из release.txt рядом со сборкой (в корне релиза) должна совпасть с этой машиной.
check_release_platform() {
  local app=$1 info
  for info in "$(dirname "$app")/release.txt" "$app/release.txt"; do
    if [ -f "$info" ]; then
      local built
      built="$(grep '^rid=' "$info" | head -1 | cut -d= -f2)"
      if [ -n "$built" ] && [ "$built" != "$RID" ]; then
        fail "this build is for $built, but this machine is $RID: download the $RID archive"
      fi
      return 0
    fi
  done
}

BUILD_SOURCE=""   # каталог готовой сборки; пуст — собираем из исходников

if [ "$FROM_SOURCE" = true ]; then
  REPO=""
  if [ -n "$SCRIPT_DIR" ]; then REPO="$(cd "$SCRIPT_DIR/.." && pwd)"; fi
  [ -n "$REPO" ] && [ -f "$REPO/src/Tasker.Cli/Tasker.Cli.csproj" ] || fail "--from-source needs this script inside the repository (cannot find src/Tasker.Cli)"
elif [ -n "$URL" ]; then
  command -v curl >/dev/null 2>&1 || fail "curl is required for --url"
  ARCHIVE="$TEMP_DIR/$(basename "${URL%%\?*}")"
  echo "Downloading $URL ..."
  curl -fsSL "$URL" -o "$ARCHIVE" || fail "cannot download $URL"
  if curl -fsSL "$URL.sha256" -o "$ARCHIVE.sha256" 2>/dev/null; then
    EXPECTED="$(awk '{print tolower($1)}' "$ARCHIVE.sha256" | head -1)"
    ACTUAL="$(sha256_of "$ARCHIVE")"
    [ "$EXPECTED" = "$ACTUAL" ] || fail "the checksum of the download does not match $URL.sha256 (expected $EXPECTED, got $ACTUAL): nothing was installed"
    echo "SHA256 checked."
  elif [ "$VERIFY" = true ]; then
    fail "cannot download $URL.sha256 to check the archive: nothing was installed (--no-verify installs without the check)"
  else
    echo "Warning: $URL.sha256 is not available: installing without the checksum check (--no-verify)" >&2
  fi
  mkdir -p "$TEMP_DIR/unpacked"
  extract "$ARCHIVE" "$TEMP_DIR/unpacked"
  BUILD_SOURCE="$(find_app "$TEMP_DIR/unpacked")" || fail "the archive does not contain tasker"
elif [ -n "$FROM" ]; then
  FROM="$(absolute "$FROM")"
  if [ -d "$FROM" ]; then
    BUILD_SOURCE="$(find_app "$FROM")" || fail "no tasker program in $FROM (nor in $FROM/app)"
  elif [ -f "$FROM" ]; then
    mkdir -p "$TEMP_DIR/unpacked"
    extract "$FROM" "$TEMP_DIR/unpacked"
    BUILD_SOURCE="$(find_app "$TEMP_DIR/unpacked")" || fail "the archive does not contain tasker"
  else
    fail "$FROM does not exist"
  fi
else
  # Без параметров: релиз, с которым пришёл скрипт (в каталоге релиза лежат install.sh и app/).
  if [ -n "$SCRIPT_DIR" ] && [ -f "$SCRIPT_DIR/app/tasker" ]; then
    BUILD_SOURCE="$SCRIPT_DIR/app"
  elif [ -n "$SCRIPT_DIR" ] && [ -f "$SCRIPT_DIR/../src/Tasker.Cli/Tasker.Cli.csproj" ]; then
    fail "this is a repository, not a release: use --from-source to build and install from it (or --from / --url for a release)"
  else
    fail "nothing to install: run this script from an unpacked release, or pass --from FILE|DIR, --url ADDRESS (or --from-source in the repository)"
  fi
fi

if [ -n "$BUILD_SOURCE" ]; then
  check_release_platform "$BUILD_SOURCE"
  [ -f "$BUILD_SOURCE/tasker-mcpd" ] || fail "$BUILD_SOURCE has no tasker-mcpd: the MCP server is a part of every release"
  echo "Installing tasker from ${URL:-${FROM:-$BUILD_SOURCE}} ($RID)..."
  cp -pR "$BUILD_SOURCE/." "$STAGING/"
  chmod +x "$STAGING/tasker" "$STAGING/tasker-mcpd" 2>/dev/null || true
  # Архив, скачанный браузером, помечен «карантином» macOS: Gatekeeper не даст запустить программу без подписи разработчика.
  if [ "$OS" = osx ]; then
    xattr -dr com.apple.quarantine "$STAGING" 2>/dev/null || true
  fi
else
  command -v dotnet >/dev/null 2>&1 || fail ".NET SDK 10 is required to build: https://dotnet.microsoft.com/download"
  DOTNET_MAJOR="$(dotnet --version | cut -d. -f1)"
  [ "$DOTNET_MAJOR" -ge 10 ] 2>/dev/null || fail ".NET SDK 10 or newer is required (found $(dotnet --version))"

  # К номеру из файла VERSION добавляется коммит (build metadata): сборка из исходников отличима от релиза.
  BASE_VERSION="$(tr -d '[:space:]' < "$REPO/VERSION" 2>/dev/null || true)"
  COMMIT="$(git -C "$REPO" describe --always --dirty --abbrev=7 2>/dev/null || true)"
  VERSION="${BASE_VERSION:-0.0.0}${COMMIT:++$COMMIT}"

  if [ "$SELF_CONTAINED" = true ]; then
    MODE_ARGS=(--self-contained true -p:PublishReadyToRun=true)
    MODE_TEXT="self-contained"
  else
    MODE_ARGS=(--self-contained false)
    MODE_TEXT="framework-dependent"
  fi

  echo "Building tasker $VERSION for $RID ($MODE_TEXT, Release)..."
  # Собираем во временную папку — рабочая установка не пострадает, если сборка упадёт.
  # --disable-build-servers: после установки не остаются фоновые серверы MSBuild и компилятора.
  dotnet publish "$REPO/src/Tasker.Cli" -c Release -r "$RID" "${MODE_ARGS[@]}" \
    -p:InformationalVersion="$VERSION" \
    -o "$STAGING" --nologo -v quiet --disable-build-servers \
    || fail "the build failed (see the output above)"

  [ -x "$STAGING/tasker" ] || fail "the build did not produce the tasker executable"

  if [ "$WITH_DAEMON" = true ]; then
    echo "Building the MCP server (tasker-mcpd)..."
    # Демон — отдельная программа с ASP.NET Core; общие библиотеки в одном каталоге совпадают и не дублируются.
    # EmbedFrontend=false: ему фронтенд не нужен.
    dotnet publish "$REPO/src/Tasker.Daemon.Host" -c Release -r "$RID" "${MODE_ARGS[@]}" \
      -p:EmbedFrontend=false -p:InformationalVersion="$VERSION" \
      -o "$STAGING" --nologo -v quiet --disable-build-servers \
      || fail "the MCP server build failed (see the output above)"

    [ -x "$STAGING/tasker-mcpd" ] || fail "the build did not produce the tasker-mcpd executable"
  fi
fi

# Нужные Linux библиотеки: .NET без ICU не запускается (Windows и macOS это не касается).
icu_missing() {
  [ "$OS" = linux ] || return 1
  case "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" in 1|true|TRUE|True) return 1 ;; esac
  if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -q 'libicuuc'; then
    return 1
  fi
  local file
  for file in /usr/lib/libicuuc.so* /usr/lib64/libicuuc.so* /usr/lib/*/libicuuc.so* /lib/*/libicuuc.so* /usr/local/lib/libicuuc.so*; do
    [ -e "$file" ] && return 1
  done
  return 0
}

ICU_HINT="Linux needs the ICU library for .NET: install libicu (Debian/Ubuntu: apt install libicu-dev; Fedora: dnf install libicu; Arch: pacman -S icu), or set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"

if icu_missing; then
  echo "Warning: the ICU library (libicu) was not found. $ICU_HINT" >&2
fi

# Проверка до подмены: рабочая установка не заменяется сломанной. Свой каталог данных и папка — ничего не оставляем.
SMOKE="$(mktemp -d)"
trap 'rm -rf "$STAGING" "$TEMP_DIR" "$SMOKE"' EXIT
mkdir "$SMOKE/workspace"
if ! SMOKE_OUT="$(TASKER_HOME="$SMOKE/home" "$STAGING/tasker" project create Smoke --workspace "$SMOKE/workspace" 2>&1)"; then
  echo "$SMOKE_OUT" >&2
  if icu_missing || echo "$SMOKE_OUT" | grep -qi 'icu'; then
    echo "$ICU_HINT" >&2
  fi
  fail "the built tasker does not work: the smoke test failed (nothing was installed)"
fi
if [ -f "$STAGING/tasker-mcpd" ]; then
  "$STAGING/tasker-mcpd" --help >/dev/null 2>&1 \
    || fail "the built tasker-mcpd does not work: the smoke test failed (nothing was installed)"
fi

# ---- установка: подмена целиком, старая версия остаётся до успеха ----

if [ -e "$APP_DIR" ]; then
  rm -rf "$APP_DIR.old"
  mv "$APP_DIR" "$APP_DIR.old"
fi
mv "$STAGING" "$APP_DIR"
rm -rf "$APP_DIR.old"
ln -sfn "$APP_DIR/tasker" "$LINK"

INSTALLED_VERSION="$("$LINK" --version 2>/dev/null || echo unknown)"
echo "Installed tasker $INSTALLED_VERSION"
echo "  program: $APP_DIR ($(du -sh "$APP_DIR" | cut -f1))$([ -f "$APP_DIR/tasker-mcpd" ] && echo ', with the MCP server' || echo ', command line only')"
echo "  command: $LINK"

# ---- PATH ----

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *)
    LINE="export PATH=\"$BIN_DIR:\$PATH\""
    PROFILE="$(path_profile)"
    TILDE='~'
    PROFILE_TEXT="${PROFILE/#$HOME/$TILDE}"
    if [ "$ADD_TO_PATH" = true ]; then
      if grep -qF "# tasker" "$PROFILE" 2>/dev/null; then
        echo "PATH: $PROFILE_TEXT already has the tasker line"
      else
        printf '\n# tasker\n%s\n' "$LINE" >> "$PROFILE"
        echo "PATH: added to $PROFILE_TEXT — open a new terminal window (or run: source $PROFILE_TEXT)"
      fi
    else
      echo
      echo "$BIN_DIR is not in your PATH. Add it (once) with:"
      echo "  echo '$LINE' >> $PROFILE_TEXT"
      echo "and open a new terminal window — or run this script again with --add-to-path."
    fi
    ;;
esac

# ---- автодополнение по Tab ----

echo
if install_completion_scripts; then
  echo "Tab completion: scripts installed in $COMPLETIONS_DIR"
  SHELL_NAME="$(basename "${SHELL:-}")"
  SUPPORTED=false
  for SUPPORTED_SHELL in "${COMPLETION_SHELLS[@]}"; do
    [ "$SHELL_NAME" = "$SUPPORTED_SHELL" ] && SUPPORTED=true
  done

  if [ "$WITH_COMPLETION" = false ]; then
    echo "  not connected to your shell (--no-completion): your startup files were not touched."
    echo "  To connect it later, run this script again without that option."
  elif [ "$SUPPORTED" = false ]; then
    echo "  your shell (${SHELL_NAME:-unknown}) is not supported yet (${COMPLETION_SHELLS[*]} are): nothing was connected"
  else
    wire_completion "$SHELL_NAME"
    COMPLETION_RC="$(completion_rc "$SHELL_NAME")"
    echo "  to undo: delete the block between '$COMPLETION_BEGIN' and '$COMPLETION_END' in $COMPLETION_RC,"
    echo "           or run: $SELF --uninstall$([ "$PREFIX" = "$HOME/.local" ] || echo " --prefix $PREFIX")"
    echo "  open a new terminal window (or run: source $COMPLETION_RC) to use it: type 'tasker pro' and press Tab"
  fi
else
  echo "Warning: could not generate the Tab completion scripts (tasker completion failed): tasker itself is installed" >&2
fi

# ---- демон MCP: автозапуск и переход на новую сборку ----

# Работавший демон MCP: переводим на новую сборку на лету (новый процесс рядом, старый доделывает начатые вызовы), без простоя.
# Демон, запущенный по-старому (одним процессом), `tasker mcp upgrade` сам перезапускает. Не вышло — старый остаётся работать:
# перезапуск здесь не делаем (новая сборка могла не подняться, а перезапуск остановил бы и рабочий сервер).
upgrade_daemon() {
  echo
  if [ "$RESTART_DAEMON" = true ]; then
    echo "The MCP server is running with the previous build: restarting it (--restart)..."
    "$LINK" mcp upgrade --restart || echo "Warning: the restart failed: see 'tasker mcp status'" >&2
  else
    echo "The MCP server is running with the previous build: switching it to the new one without downtime..."
    if ! "$LINK" mcp upgrade; then
      echo "Warning: the MCP server was not switched: it keeps running with the previous build." >&2
      echo "Fix the reason above and run 'tasker mcp upgrade', or 'tasker mcp upgrade --restart' to restart it (calls in progress are cut off)." >&2
    fi
  fi
}

DAEMON_RUNNING=false
if [ -f "$APP_DIR/tasker-mcpd" ] && "$LINK" mcp status >/dev/null 2>&1; then
  DAEMON_RUNNING=true
fi

if [ -f "$APP_DIR/tasker-mcpd" ] && [ "$WITH_AUTOSTART" = true ]; then
  AUTOSTART_ENABLED=false
  if "$LINK" mcp autostart status --json 2>/dev/null | grep -Eq '"enabled": *true'; then
    AUTOSTART_ENABLED=true
  fi

  if [ "$AUTOSTART_ENABLED" = true ]; then
    # Служба запускает tasker-mcpd по прежнему пути — он тот же: достаточно перевести работающий демон на новую сборку.
    echo
    echo "MCP server autostart: already enabled, left as it is"
    [ "$DAEMON_RUNNING" = false ] || upgrade_daemon
  else
    echo
    echo "MCP server autostart: enabling (it starts the MCP server now and at every login; turn it off with 'tasker mcp autostart disable')..."
    if ! "$LINK" mcp autostart enable; then
      echo "Warning: autostart was not enabled; tasker itself is installed. See the reason above;" >&2
      echo "you can retry with 'tasker mcp autostart enable' or start the server by hand: 'tasker mcp start'." >&2
      [ "$DAEMON_RUNNING" = false ] || upgrade_daemon
    fi
  fi
elif [ "$DAEMON_RUNNING" = true ]; then
  upgrade_daemon
fi
