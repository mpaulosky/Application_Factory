#!/usr/bin/env bash
# Tests for tests/parity/check_gh_repo/shfmt-shim.sh, the shfmt the check_gh_repo parity suite runs the checker with: it hands a call
# to the real shfmt only from an empty folder with nothing but PATH set, answers --version from the checker's .shfmt-version (or
# fails it from .shfmt-version-exit) and any other call from its .shfmt-parse, and finds the real shfmt past any link to itself.
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

# The real shfmt: the first on PATH that isn't a link to the shim, so this test also runs with the shim first on PATH.
real=""
IFS=: read -ra entries <<< "$PATH"
for entry in "${entries[@]}"; do
  if [[ -n "$entry" && -x "$entry/shfmt" && "$(readlink -f "$entry/shfmt")" != "$(readlink -f "$shim_file")" ]]; then
    real="$entry/shfmt"
    break
  fi
done
[[ -n "$real" ]] || {
  echo "shfmt-shim.test.sh: needs the real shfmt on PATH" >&2
  exit 2
}
real_dir="$(dirname "$real")"
mkdir -p "$work/shim" "$work/shim2" "$work/empty" "$work/full" "$work/dotonly" "$work/checker" "$work/twolines" "$work/parse" "$work/parse-fails" "$work/version-fails"
ln -s "$shim_file" "$work/shim/shfmt"
ln -s "$shim_file" "$work/shim2/shfmt"
touch "$work/full/file" "$work/dotonly/.hidden"
# The tools the shim itself runs, and no shfmt: for the case with no real shfmt after the shim. Each of the next two leaves one out,
# and is the whole PATH after the shim, so the tool left out is found nowhere else, wherever the real shfmt is installed.
tools=(bash env ls readlink head tail)
for leave_out in none ls env; do
  mkdir -p "$work/tools-$leave_out"
  for tool in "${tools[@]}"; do
    [[ "$tool" == "$leave_out" ]] || ln -s "$(command -v "$tool")" "$work/tools-$leave_out/$tool"
  done
done
printf 'v3.13.1\n' > "$work/checker/.shfmt-version"
printf 'v3.13.1\nv3.14.0\n' > "$work/twolines/.shfmt-version"
printf '0\nnot a tree\nsecond line\n' > "$work/parse/.shfmt-parse"
printf '3\nboom\n' > "$work/parse-fails/.shfmt-parse"
printf '5\n' > "$work/version-fails/.shfmt-version-exit"
path="$work/shim:$real_dir:/usr/bin:/bin"
shim() { (cd "$1" && shift && env -i PATH="$path" shfmt "$@"); }
# from_checker <folder> <stdin> <arg>...: the shim run from an empty folder by a process whose own folder is <folder>, as the checker runs it;
# prints its stdout, then its stderr and exit code on a line of their own.
from_checker() {
  (cd "$1" && python3 -I -c 'import subprocess, sys
r = subprocess.run(["shfmt", *sys.argv[4:]], input=sys.argv[3], cwd=sys.argv[1], env={"PATH": sys.argv[2]}, capture_output=True, text=True)
sys.stdout.write(r.stdout + "|" + r.stderr.strip() + "|" + str(r.returncode))' "$work/empty" "$path" "$2" "${@:3}")
}

status=0
out="$(shim "$work/empty" --version 2>&1)" || status=$?
check "an empty folder and PATH alone: the real shfmt answers" '[[ $status -eq 0 && "$out" == "$("$real" --version)" ]]'
out="$(printf 'if  true;then echo x;fi\n' | shim "$work/empty" -)"
check "its arguments and stdin reach the real shfmt" '[[ "$out" == "$(printf "if  true;then echo x;fi\n" | "$real" -)" ]]'
status=0
out="$(cd "$work/empty" && env -i PATH=":$work/shim2:$work/shim:$real_dir:/usr/bin:/bin" shfmt --version 2>&1)" || status=$?
check "an empty PATH entry and another link to the shim ahead of the real shfmt are passed over" '[[ $status -eq 0 && "$out" == "$("$real" --version)" ]]'

status=0
out="$(shim "$work/full" --version 2>&1)" || status=$?
check "a folder that isn't empty: exit 97, naming it" '[[ $status -eq 97 && "$out" == *"isn'"'"'t empty"* ]]'
status=0
out="$(shim "$work/dotonly" --version 2>&1)" || status=$?
check "a folder that holds only a dotfile isn't empty either" '[[ $status -eq 97 && "$out" == *"isn'"'"'t empty"* ]]'
status=0
out="$(cd "$work/empty" && env -i PATH="$work/shim:$work/tools-ls" shfmt --version 2>&1)" || status=$?
check "no ls to list the folder with: exit 97, saying so" '[[ $status -eq 97 && "$out" == *"can'"'"'t list the folder"* ]]'

status=0
out="$(cd "$work/empty" && env -i PATH="$path" GH_TOKEN=x shfmt --version 2>&1)" || status=$?
check "a variable beside PATH: exit 97, naming it" '[[ $status -eq 97 && "$out" == *"GH_TOKEN"* ]]'
status=0
out="$(cd "$work/empty" && env -i PATH="$path" OLDPWD=/ shfmt --version 2>&1)" || status=$?
check "OLDPWD, which bash may export itself, isn't counted" '[[ $status -eq 0 && "$out" == "$("$real" --version)" ]]'
status=0
out="$(cd "$work/empty" && env -i PATH="$work/shim:$work/tools-env" shfmt --version 2>&1)" || status=$?
check "no env to list the environment with: exit 97, saying so" '[[ $status -eq 97 && "$out" == *"can'"'"'t list its environment"* ]]'

if [[ -d /proc/self ]]; then
  out="$(from_checker "$work/checker" "" --version)"
  check "the caller's .shfmt-version answers --version" '[[ "$out" == "v3.13.1"$'"'"'\n'"'"'"||0" ]]'
  out="$(from_checker "$work/twolines" "" --version)"
  check "with its first line only" '[[ "$out" == "v3.13.1"$'"'"'\n'"'"'"||0" ]]'
  out="$(from_checker "$work/checker" $'a  b\n' -)"
  check "but not any other call" '[[ "$out" == "a b"$'"'"'\n'"'"'"||0" ]]'
  out="$(from_checker "$work/parse" $'a  b\n' -ln bash --to-json)"
  check "the caller's .shfmt-parse answers any other call: exit 0, the rest on stdout" '[[ "$out" == "not a tree"$'"'"'\n'"'"'"second line"$'"'"'\n'"'"'"||0" ]]'
  out="$(from_checker "$work/parse-fails" $'a  b\n' -ln bash --to-json)"
  check "and another exit code with the rest on stderr" '[[ "$out" == "|boom|3" ]]'
  out="$(from_checker "$work/version-fails" "" --version)"
  check "the caller's .shfmt-version-exit makes --version fail with its code" '[[ "$out" == "|shfmt-shim: --version fails, as .shfmt-version-exit says|5" ]]'
  out="$(from_checker "$work/version-fails" $'a  b\n' -)"
  check "but not any other call: .shfmt-version-exit leaves a parse alone" '[[ "$out" == "a b"$'"'"'\n'"'"'"||0" ]]'
  out="$(from_checker "$work/parse" "" --version)"
  check "but not --version" '[[ "$out" == "$("$real" --version)"$'"'"'\n'"'"'"||0" ]]'
else
  echo "skip - .shfmt-version, .shfmt-version-exit and .shfmt-parse: no /proc here"
fi

status=0
out="$(cd "$work/empty" && env -i PATH="$work/shim:$work/tools-none" shfmt --version 2>&1)" || status=$?
check "no real shfmt after the shim: exit 127, saying so" '[[ $status -eq 127 && "$out" == *"no shfmt on PATH"* ]]'

echo "shfmt-shim.test.sh: $pass passed, $failed failed"
[[ $failed -eq 0 ]]
