#!/usr/bin/env bash
# The shfmt the check_gh_repo parity suite runs the checker with, first on PATH under the name shfmt (test.sh links it into a
# folder of its own). It holds the checker to how it runs shfmt, then hands the call to the real shfmt, the next one on PATH:
#
#   - from an empty folder (no dotfile either), with no environment but PATH (bash's own PWD, OLDPWD, SHLVL and _ apart), so
#     no file or variable of the repository it checks reaches shfmt. Otherwise, or when it can't list the folder or the
#     environment, it exits 97 with what it saw, which the checker reports as a failed shfmt;
#   - when the folder the checker runs from (read through /proc, so only on Linux) holds .shfmt-version, it answers --version
#     with that file's first line instead, so a case can stand for an older or a malformed shfmt release, and when it holds
#     .shfmt-version-exit, --version exits with that file's first line as its code;
#   - and when that folder holds .shfmt-parse, it answers every other call from it instead of parsing: its first line is the
#     exit code, and the rest goes to stdout when that is 0, to stderr otherwise, so a case can stand for a shfmt that fails
#     or prints no syntax tree.
#
# Exit 127 when there is no real shfmt on PATH after it.
set -uo pipefail

if ! listing="$(ls -A .)"; then
  echo "shfmt-shim: can't list the folder it runs from: $PWD" >&2
  exit 97
fi
if [[ -n "$listing" ]]; then
  echo "shfmt-shim: run from a folder that isn't empty: $PWD" >&2
  exit 97
fi
if ! environment="$(env)"; then
  echo "shfmt-shim: can't list its environment" >&2
  exit 97
fi
# A line of a value that holds a newline reads as one more name here, which only makes the check stricter.
extra=""
while IFS= read -r line; do
  case "${line%%=*}" in
    PATH | PWD | OLDPWD | SHLVL | _) ;;
    *) extra+="${line%%=*} " ;;
  esac
done <<< "$environment"
if [[ -n "$extra" ]]; then
  echo "shfmt-shim: run with more than PATH in its environment: $extra" >&2
  exit 97
fi

parent_cwd="$(readlink "/proc/$PPID/cwd" 2> /dev/null || true)"
if [[ -n "$parent_cwd" && "${1:-}" == --version && -f "$parent_cwd/.shfmt-version-exit" ]]; then
  echo "shfmt-shim: --version fails, as .shfmt-version-exit says" >&2
  exit "$(head -n 1 "$parent_cwd/.shfmt-version-exit")"
fi
if [[ -n "$parent_cwd" && "${1:-}" == --version && -f "$parent_cwd/.shfmt-version" ]]; then
  head -n 1 "$parent_cwd/.shfmt-version"
  exit 0
fi
if [[ -n "$parent_cwd" && "${1:-}" != --version && -f "$parent_cwd/.shfmt-parse" ]]; then
  code="$(head -n 1 "$parent_cwd/.shfmt-parse")"
  if [[ "$code" == 0 ]]; then
    tail -n +2 "$parent_cwd/.shfmt-parse"
  else
    tail -n +2 "$parent_cwd/.shfmt-parse" >&2
  fi
  exit "$code"
fi

# The real shfmt: the first executable shfmt on PATH that isn't this shim, by any link to it.
self="$(readlink -f "${BASH_SOURCE[0]}")"
rest=""
IFS=: read -ra entries <<< "$PATH"
for entry in "${entries[@]}"; do
  if [[ -x "$entry/shfmt" && "$(readlink -f "$entry/shfmt")" != "$self" ]]; then
    rest="$entry/shfmt"
    break
  fi
done
if [[ -z "$rest" ]]; then
  echo "shfmt-shim: no shfmt on PATH after the shim" >&2
  exit 127
fi
exec "$rest" "$@"
