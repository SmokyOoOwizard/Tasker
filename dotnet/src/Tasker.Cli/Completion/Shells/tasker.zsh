#compdef tasker
# Tab completion for tasker (zsh): commands, options and values (projects, statuses, tasks...) of the current workspace.
# Printed by 'tasker completion zsh'; scripts/install.sh puts it on fpath. All the knowledge about commands lives in tasker:
# this script only asks it with the [suggest] directive and hands the answer to zsh, which does the quoting.

_tasker() {
  local line out candidate
  local -a candidates plain joined

  # The command line after the program name, up to the end of the word under the cursor, as typed (quotes and backslashes kept).
  line="${(j: :)words[2,CURRENT]}"

  # Tasker prints one candidate per line; nothing (no workspace, no match, an error) is an empty answer, never an error message.
  out="$(command tasker "[suggest:${#line}]" "$line" 2>/dev/null)" || out=""
  candidates=( ${(f)out} )

  if (( ${#candidates} == 0 )); then
    # Nothing to suggest: the argument is a path (-w, --sqlite, mcp workspace add) or free text.
    _files
    return
  fi

  # 'Name=' and 'TSK-' continue in the same word: no space after them.
  for candidate in $candidates; do
    case $candidate in
      *[=:-]) joined+=( "$candidate" ) ;;
      *) plain+=( "$candidate" ) ;;
    esac
  done
  (( ${#plain} )) && compadd -- "${plain[@]}"
  (( ${#joined} )) && compadd -S '' -- "${joined[@]}"
  return 0
}

if [[ ${funcstack[1]} == _tasker ]]; then
  # Autoloaded from fpath as the completion function: complete now.
  _tasker "$@"
else
  # Sourced (for example 'source <(tasker completion zsh)'): register it.
  compdef _tasker tasker
fi
