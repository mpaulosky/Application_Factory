#!/usr/bin/env bash
# Claude Code's PostToolUse hook for Edit, Write and MultiEdit (.claude/settings.json): lints the Markdown or YAML file
# the tool just wrote, so a violation is fixed in the same turn and not found by the commit hook, the gate, CI or a reviewer.
#
#   scripts/lint-edited-file.sh            (Claude Code passes the tool call as JSON on stdin)
#
# Markdown: markdownlint-cli2 with the repo's .markdownlint-cli2.jsonc, which is what the pre-commit hook, the gate and CI run.
# YAML: yamllint with the repo's .yamllint.yml. A workflow (.github/workflows/*.yml) also goes through actionlint and zizmor,
# and .github/dependabot.yml through zizmor, as the gate does.
#
# Exit 0: the file is clean, is not Markdown or YAML, is outside the repo, or no linter for it is installed (this hook only
# helps; the commit hook and the gate still check). Exit 2: a linter reported a violation; its output goes to stderr, which
# Claude Code feeds back to the agent. Exit 1: the hook could not read its input (python3 missing, bad JSON); Claude Code shows
# that to the person and carries on. A linter that is installed but crashes counts as a violation, not as clean.
set -uo pipefail

if ! command -v python3 > /dev/null 2>&1; then
  echo "lint-edited-file.sh: python3 not found, so the edited file was not linted." >&2
  exit 1
fi

# The file the tool wrote. Edit, Write and MultiEdit all name it file_path. A JSON error is an error, not "nothing to lint".
if ! file="$(python3 -I -c '
import json, sys
data = json.load(sys.stdin)
tool_input = data.get("tool_input") if isinstance(data, dict) else None
path = tool_input.get("file_path") if isinstance(tool_input, dict) else None
sys.stdout.write(path if isinstance(path, str) else "")
')"; then
  echo "lint-edited-file.sh: the hook input was not JSON, so the edited file was not linted." >&2
  exit 1
fi

[[ -n "$file" && -f "$file" ]] || exit 0

# Lower-case extension: a README.MD is Markdown too.
lower="$(printf '%s' "$file" | tr '[:upper:]' '[:lower:]')"
kind=
case "$lower" in
  *.md) kind=markdown ;;
  *.yml | *.yaml) kind=yaml ;;
esac
[[ -n "$kind" ]] || exit 0

command -v git > /dev/null 2>&1 || exit 0
dir="$(cd -P -- "$(dirname -- "$file")" 2> /dev/null && pwd -P)" || exit 0
# The repository the file is in, which in a linked worktree is that worktree: its configs are the ones that apply.
root="$(git -C "$dir" rev-parse --show-toplevel 2> /dev/null)" || exit 0
root="$(cd -P -- "$root" 2> /dev/null && pwd -P)" || exit 0
abs="$dir/$(basename -- "$file")"
case "$abs" in
  "$root"/*) rel="${abs#"$root"/}" ;;
  *) exit 0 ;;
esac
case "/$rel" in
  */node_modules/*) exit 0 ;;
esac
# "./" keeps a name that starts with "-" from being read as an option.
target="./$rel"

failures=()
skipped=()

# run_linter <name> <command...>: runs it from the repo root. Anything but exit 0 is a violation, a crash included.
run_linter() {
  local name="$1" output rc=0
  shift
  output="$(cd "$root" && "$@" 2>&1)" || rc=$?
  if ((rc != 0)); then
    failures+=("$name (exit $rc):"$'\n'"$output")
  fi
}

# local_bin <name>: the repo's own node_modules/.bin copy first, as the pre-commit hook probes it, then PATH.
local_bin() {
  if [[ -x "$root/node_modules/.bin/$1" ]]; then
    printf '%s' "$root/node_modules/.bin/$1"
  elif command -v "$1" > /dev/null 2>&1; then
    command -v "$1"
  fi
}

if [[ "$kind" == markdown ]]; then
  bin="$(local_bin markdownlint-cli2)"
  if [[ -z "$bin" ]]; then
    skipped+=("markdownlint-cli2")
  else
    # --no-globs: the path is a file name, even if it holds [ ] or *. The config's ignores still apply to it.
    run_linter markdownlint-cli2 "$bin" --no-globs "$target"
  fi
else
  if command -v yamllint > /dev/null 2>&1; then
    config=()
    [[ -f "$root/.yamllint.yml" ]] && config=(-c .yamllint.yml)
    run_linter yamllint yamllint ${config[@]+"${config[@]}"} "$target"
  else
    skipped+=("yamllint")
  fi

  case "$rel" in
    .github/workflows/*.yml | .github/workflows/*.yaml)
      if command -v actionlint > /dev/null 2>&1; then
        run_linter actionlint actionlint -- "$target"
      else
        skipped+=("actionlint")
      fi
      if command -v zizmor > /dev/null 2>&1; then
        # --offline: no GitHub token needed, as in the gate. CI also runs the online audits.
        run_linter zizmor zizmor --offline --min-severity medium -- "$target"
      else
        skipped+=("zizmor")
      fi
      ;;
    .github/dependabot.yml | .github/dependabot.yaml)
      if command -v zizmor > /dev/null 2>&1; then
        run_linter zizmor zizmor --offline --min-severity medium -- "$target"
      else
        skipped+=("zizmor")
      fi
      ;;
  esac
fi

if ((${#failures[@]} > 0)); then
  {
    echo "Lint found problems in $rel. Fix them now; the commit hook, the gate and CI run the same checks:"
    printf '%s\n' "${failures[@]}"
  } >&2
  exit 2
fi

if ((${#skipped[@]} > 0)); then
  echo "lint-edited-file.sh: $rel was not fully linted: ${skipped[*]} not installed (the gate and CI still check it)." >&2
fi
exit 0
