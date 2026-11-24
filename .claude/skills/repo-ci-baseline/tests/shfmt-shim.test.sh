#!/usr/bin/env bash
# Tests for tests/parity/check_gh_repo/shfmt-shim.sh, the shfmt the check_gh_repo parity suite runs the checker with: it hands a call
# to the real shfmt only from an empty folder with nothing but PATH set, and answers --version from the checker's .shfmt-version.
# check() evals its condition string later, so the single quotes are deliberate and the variables it reads look unused to shellcheck.
# shellcheck disable=SC2016,SC2034
set -euo pipefail

here="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
shim_file="$here/parity/check_gh_repo/shfmt-shim.sh"
work="$(mktemp -d)"
trap 'command rm -rf "$work"' EXIT

pass=0
failed=0
check() {
  if eval "$2"; then pass=$((pass + 1)); else
    echo "FAIL: $1" >&2
    failed=$((failed + 1))
  fi
}

real="$(command -v shfmt)" || {
  echo "shfmt-shim.test.sh: needs the real shfmt on PATH" >&2
  exit 2
}
real_dir="$(dirname "$real")"
mkdir -p "$work/shim" "$work/empty" "$work/full" "$work/checker" "$work/alone"
ln -s "$shim_file" "$work/shim/shfmt"
touch "$work/full/file"
# The tools the shim itself runs, and no shfmt: for the case with no real shfmt after the shim.
for tool in bash env sed cut tr ls head readlink dirname; do ln -s "$(command -v "$tool")" "$work/alone/$tool"; done
printf 'v3.13.1\n' > "$work/checker/.shfmt-version"
path="$work/shim:$real_dir:/usr/bin:/bin"
shim() { (cd "$1" && shift && env -i PATH="$path" shfmt "$@"); }

status=0
out="$(shim "$work/empty" --version 2>&1)" || status=$?
check "an empty folder and PATH alone: the real shfmt answers" '[[ $status -eq 0 && "$out" == "$("$real" --version)" ]]'
out="$(printf 'if  true;then echo x;fi\n' | shim "$work/empty" -)"
check "its arguments and stdin reach the real shfmt" '[[ "$out" == "$(printf "if  true;then echo x;fi\n" | "$real" -)" ]]'

status=0
out="$(shim "$work/full" --version 2>&1)" || status=$?
check "a folder that isn't empty: exit 97, naming it" '[[ $status -eq 97 && "$out" == *"isn'"'"'t empty"* ]]'

status=0
out="$(cd "$work/empty" && env -i PATH="$path" GH_TOKEN=x shfmt --version 2>&1)" || status=$?
check "a variable beside PATH: exit 97, naming it" '[[ $status -eq 97 && "$out" == *"GH_TOKEN"* ]]'

if [[ -d /proc/self ]]; then
  out="$(cd "$work/checker" && python3 -c 'import subprocess, sys; sys.stdout.write(subprocess.run(["shfmt", "--version"], cwd=sys.argv[1], env={"PATH": sys.argv[2]}, capture_output=True, text=True).stdout)' "$work/empty" "$path")"
  check "the caller's .shfmt-version answers --version" '[[ "$out" == v3.13.1 ]]'
  out="$(cd "$work/checker" && python3 -c 'import subprocess, sys; sys.stdout.write(subprocess.run(["shfmt", "-"], input="a  b\n", cwd=sys.argv[1], env={"PATH": sys.argv[2]}, capture_output=True, text=True).stdout)' "$work/empty" "$path")"
  check "but not any other call" '[[ "$out" == "a b" ]]'
else
  echo "skip - .shfmt-version: no /proc here"
fi

status=0
out="$(cd "$work/empty" && env -i PATH="$work/shim:$work/alone" shfmt --version 2>&1)" || status=$?
check "no real shfmt after the shim: exit 127, saying so" '[[ $status -eq 127 && "$out" == *"no shfmt on PATH"* ]]'

echo "shfmt-shim.test.sh: $pass passed, $failed failed"
[[ $failed -eq 0 ]]
