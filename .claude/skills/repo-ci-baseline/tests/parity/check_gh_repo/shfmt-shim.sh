#!/usr/bin/env bash
# The shfmt the check_gh_repo parity suite runs the checker with, first on PATH under the name shfmt (test.sh links it into a
# folder of its own). It holds the checker to how it runs shfmt, then hands the call to the real shfmt, the next one on PATH:
#
#   - from an empty folder, with no environment but PATH (bash's own PWD, SHLVL and _ apart), so no file or variable of the
#     repository it checks reaches shfmt. Otherwise it exits 97 with what it saw, which the checker reports as a failed shfmt;
#   - and, when the folder the checker runs from holds .shfmt-version (read through /proc, so only on Linux), it answers
#     --version with that file's first line instead, so a case can stand for an older or a malformed shfmt release.
set -uo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

if [[ -n "$(ls -A .)" ]]; then
  echo "shfmt-shim: run from a folder that isn't empty: $PWD" >&2
  exit 97
fi
extra="$(env | sed -e '/^PATH=/d' -e '/^PWD=/d' -e '/^SHLVL=/d' -e '/^_=/d' -e '/^OLDPWD=/d' | cut -d= -f1 | tr '\n' ' ')"
if [[ -n "$extra" ]]; then
  echo "shfmt-shim: run with more than PATH in its environment: $extra" >&2
  exit 97
fi

if [[ "${1:-}" == --version ]]; then
  parent_cwd="$(readlink "/proc/$PPID/cwd" 2> /dev/null || true)"
  if [[ -n "$parent_cwd" && -f "$parent_cwd/.shfmt-version" ]]; then
    head -n 1 "$parent_cwd/.shfmt-version"
    exit 0
  fi
fi

# The real shfmt: the first on PATH that isn't this shim's own folder.
rest=""
IFS=: read -ra entries <<< "$PATH"
for entry in "${entries[@]}"; do
  [[ -n "$entry" && "$(CDPATH='' cd -- "$entry" 2> /dev/null && pwd -P)" != "$here" ]] || continue
  if [[ -x "$entry/shfmt" && "$(readlink -f "$entry/shfmt")" != "$(readlink -f "${BASH_SOURCE[0]}")" ]]; then
    rest="$entry/shfmt"
    break
  fi
done
if [[ -z "$rest" ]]; then
  echo "shfmt-shim: no shfmt on PATH after the shim" >&2
  exit 127
fi
exec "$rest" "$@"
