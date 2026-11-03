# Tab completion for tasker (bash): commands, options and values (projects, statuses, tasks...) of the current workspace.
# Printed by 'tasker completion bash'; scripts/install.sh sources it from your bash startup file.
# All the knowledge about commands lives in tasker: this script only asks it with the [suggest] directive and quotes the
# answer for bash. Works with bash 3.2 (the one macOS ships) and does not need the bash-completion package.

# Quotes one candidate for the word being completed: $1 - the candidate, $2 - the quote the word was opened with (or empty).
_tasker_quote() {
  local text=$1 quote=$2 result= char i single=\'
  if [[ $quote == "'" ]]; then
    printf '%s' "${text//$single/$single\\$single$single}"
    return
  fi
  for ((i = 0; i < ${#text}; i++)); do
    char=${text:i:1}
    if [[ $quote == '"' ]]; then
      case $char in
        '"' | '$' | '`' | \\) result+="\\$char" ;;
        *) result+=$char ;;
      esac
    else
      case $char in
        ' ' | $'\t' | '"' | "'" | '`' | '$' | '&' | ';' | '(' | ')' | '<' | '>' | '|' | '*' | '?' | '[' | ']' | '{' | '}' | '!' | '#' | \\)
          result+="\\$char" ;;
        '~') if ((i == 0)); then result+='\~'; else result+=$char; fi ;;
        *) result+=$char ;;
      esac
    fi
  done
  printf '%s' "$result"
}

_tasker() {
  COMPREPLY=()

  # The line after the program name, up to the cursor, as typed.
  local line=${COMP_LINE:0:COMP_POINT}
  line=${line#"${line%%[![:space:]]*}"}
  line=${line#"${COMP_WORDS[0]}"}
  line=${line#"${line%%[![:space:]]*}"}

  # The word under the cursor as typed (it starts after the last space that is not quoted or escaped), without quotes and
  # backslashes. readline replaces only what follows the last word break (= and : by default, unless quoted or escaped):
  # 'head' is the part of the word before it.
  local typed= quote= escaped= breakAt=0 quoteAt=0 char i
  for ((i = 0; i < ${#line}; i++)); do
    char=${line:i:1}
    if [[ -n $escaped ]]; then
      typed+=$char
      escaped=
    elif [[ $quote == "'" ]]; then
      if [[ $char == "'" ]]; then quote=; else typed+=$char; fi
    elif [[ $quote == '"' ]]; then
      if [[ $char == '"' ]]; then quote=; elif [[ $char == \\ ]]; then escaped=1; else typed+=$char; fi
    elif [[ $char == \\ ]]; then
      escaped=1
    elif [[ $char == "'" || $char == '"' ]]; then
      quote=$char
      quoteAt=${#typed}
    elif [[ $char == [[:space:]] ]]; then
      typed=
      breakAt=0
    else
      typed+=$char
      [[ $COMP_WORDBREAKS == *"$char"* ]] && breakAt=${#typed}
    fi
  done
  local head=${typed:0:breakAt}
  [[ -n $quote ]] && head=${typed:0:quoteAt}

  # Tasker prints one candidate per line; any problem is an empty answer, never an error message.
  local output candidate
  output=$(command tasker "[suggest:${#line}]" "$line" 2>/dev/null) || output=
  while IFS= read -r candidate; do
    [[ -n $candidate && $candidate == "$typed"* ]] || continue
    COMPREPLY+=("$(_tasker_quote "${candidate:${#head}}" "$quote")")
  done <<< "$output"

  # 'Name=' and 'TSK-' continue in the same word: no space after them (compopt needs bash 4; older bash adds the space).
  if ((${#COMPREPLY[@]} == 1)) && [[ ${COMPREPLY[0]} == *[=:-] ]]; then
    compopt -o nospace 2>/dev/null
  fi
  return 0
}

# Nothing to suggest (a path after -w, --sqlite, free text): file names, as usual.
complete -o default -F _tasker tasker
