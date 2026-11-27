#!/usr/bin/env bash
# Validate the repo-ci-baseline Template before it's Applied anywhere.
#
#   test.sh
#   TEST_SH_SHARD=hooks|scripts|checks test.sh
#
# Runs the Template's own tests (hook suites, auto-merge script tests,
# the Owned .github/scripts pytest with the gh check's own tests), the hardening-review scripts' tests, the renderer oracle, the parity suites
# (status, land, settings, check_gh_repo, the release scripts, draft_release and post_claude_review as .py and .cs, detect_changes, discover_tests, load_test_env, check_central_pins and rollout), the Claude Review benchmark's --rescore parity suite and
# the C# scripts' xUnit tests (tests/Scripts.Tests: the landing decision, status, land, settings, rollout and its AutoRollout libraries,
# what they share, the two checkers in tests/ and the benchmark), lints its workflows, YAML, shell
# and Markdown with the Template's configs, checks that
# Owned files carry no {{PLACEHOLDER}} and that the Owned scripts, and this repo's own
# scripts and skills, tell every gh command its repository, checks the Seed Sandcastle files, and smoke-tests apply.sh and
# reapply.sh against throwaway repos. The Application Factory's CI runs this on every change
# under the skill, and it's the check to run before committing a Template
# change. A repo's copies of Owned files are checked by status.sh and apply.sh --dry-run, not here.
#
# TEST_SH_SHARD runs one of its three shards, which is how the Factory's CI runs it in parallel jobs (ADR 0013): hooks is the hook and Owned script
# tests, scripts the auto-merge, Owned .github/scripts, hardening-review, renderer, parity, benchmark and C# script tests, checks everything from actionlint on. With
# no TEST_SH_SHARD every shard runs, in that order. Every step sits in exactly one shard; scripts/tests/gate.test.sh checks that.
set -euo pipefail
# The step headers carry SECONDS: count from here, whatever SECONDS a caller exported (mpaulosky/dotfiles#417).
SECONDS=0

# CDPATH is cleared for the cd: a caller's CDPATH that holds . would make it print the folder, and skill_dir would hold the path twice when test.sh is
# run by a relative path. A skill_dir that doesn't hold test.sh stops the run before anything uses it.
skill_dir="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ ! -f "$skill_dir/test.sh" ]]; then
  echo "test.sh: can't find its own folder (got '$skill_dir')." >&2
  exit 2
fi
owned="$skill_dir/template/owned"
seed="$skill_dir/template/seed"

# A local pytest run must not leave caches in the Template for Apply to copy.
export PYTHONDONTWRITEBYTECODE=1
# The tests build throwaway repos. A global init.templateDir would install this
# machine's own hooks into each of them and block their commits.
# Both folders are made absolute, so a relative TMPDIR still names them from the folders the steps cd into. Each mktemp is its own assignment, so a failed
# one stops the run under set -e, and each is made absolute by prefixing $PWD rather than by cd, which a caller's CDPATH would make print the folder too.
# The trap comes first, over empty names, so a second mktemp that fails still removes the first folder.
GIT_TEMPLATE_DIR="" md_tmp=""
trap 'command rm -rf ${GIT_TEMPLATE_DIR:+"$GIT_TEMPLATE_DIR"} ${md_tmp:+"$md_tmp"}' EXIT
GIT_TEMPLATE_DIR="$(mktemp -d)"
[[ "$GIT_TEMPLATE_DIR" == /* ]] || GIT_TEMPLATE_DIR="$PWD/$GIT_TEMPLATE_DIR"
export GIT_TEMPLATE_DIR
md_tmp="$(mktemp -d)"
[[ "$md_tmp" == /* ]] || md_tmp="$PWD/$md_tmp"

failures=0
# Each header carries the seconds since test.sh started (bash's SECONDS, reset at the top), so a log shows which step ran long
# (a step's time is the next header's minus its own) even when the run is killed at a limit (mpaulosky/dotfiles#417).
step() {
  echo
  echo "── $1 [+${SECONDS}s]"
}
fail() {
  echo "FAIL: $1" >&2
  failures=$((failures + 1))
}
need() {
  command -v "$1" &> /dev/null || {
    echo "test.sh: $1 is required" >&2
    exit 2
  }
}
# The dotnet-install.sh channel of a global.json SDK version: its feature band (11.0.100 and 11.0.105 are 11.0.1xx), whose
# newest patch rollForward latestPatch accepts. Fails, printing nothing, for anything but a stable A.B.Cnn version
# (a prerelease, a four-digit patch, an empty value): global.json's allowPrerelease is false, and no band is A.B.0xx.
sdk_band() {
  [[ "$1" =~ ^([0-9]+)\.([0-9]+)\.([1-9])[0-9]{2}$ ]] || return 1
  printf '%s.%s.%sxx\n' "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}"
}
# Whether a dependabot.yml (argument 2) has an entry for the package-ecosystem argument 1 whose directory, or one of whose directories, is argument 3.
# Each entry under updates: (from one "  - " line to the next "  - " line or top-level key) is judged as a whole, whatever its keys'
# order: its keys at four spaces, a directories item at six. A path is compared as a string, written in double quotes with no comment
# after it, as the Factory's file writes it (trailing blanks and a CR dropped): a path's "." is only a dot, a comment line is no key or
# item, and another spelling of the path fails closed.
dependabot_entry_for() {
  awk -v ecosystem="$1" -v want="\"$3\"" '
    function end_entry() { if (eco == ecosystem && hit) found = 1; eco = ""; hit = 0; key = "" }
    { sub(/[ \t\r]+$/, "") }
    /^[a-z]/ { end_entry(); next }
    /^  - / { end_entry(); $0 = "    " substr($0, 5) }
    match($0, /^    [a-z-]+:/) {
      key = substr($0, 5, RLENGTH - 5); value = substr($0, RLENGTH + 1); sub(/^[ \t]+/, "", value)
      if (key == "package-ecosystem") { eco = value; gsub(/"/, "", eco) }
      if (key == "directory" && value == want) hit = 1
      next
    }
    /^      - / && key == "directories" && substr($0, 9) == want { hit = 1 }
    END { end_entry(); exit !found }' "$2"
}
# Whether a dependabot.yml (argument 1) has a dotnet-sdk entry for the directory argument 2.
dotnet_sdk_entry_for() { dependabot_entry_for dotnet-sdk "$1" "$2"; }
# Whether the setup-dotnet refs of a workflow (argument 2) are all the commit argument 1, a 40-hex pin: at least one, and no other ref of any kind (a tag,
# a branch, a short or longer hex). A commented-out line is no step. Prints the refs it found.
setup_dotnet_pins_are() {
  local pins
  pins="$({ grep -vE '^[[:space:]]*#' "$2" | grep -oE "actions/setup-dotnet@[^[:space:]\"']*" || true; } | sort -u)"
  printf '%s\n' "$pins"
  [[ "$1" =~ ^[0-9a-f]{40}$ && "$pins" == "actions/setup-dotnet@$1" ]]
}
# Whether a NuGet.config (argument 1) clears its auditSources and adds nuget.org alone, so a NuGet.config above it can't point the audit at a feed that
# reports no advisories. Prints the auditSources element it found, its lines joined. Line breaks and tabs become spaces, so attributes on lines of their
# own stay apart, and XML comments are dropped first, so a commented-out element counts for nothing.
audit_sources_nuget_only() {
  local audit
  audit="$(tr '\n\t\r' '   ' 2> /dev/null < "$1" | sed -E 's/<!--([^-]|-[^-])*-->//g' | grep -oE '<auditSources>.*</auditSources>' || true)"
  printf '%s\n' "$audit"
  [[ "$audit" =~ ^\<auditSources\>[[:space:]]*\<clear[[:space:]]*/\>[[:space:]]*\<add[[:space:]]+key=\"nuget\.org\"[[:space:]]+value=\"https://api\.nuget\.org/v3/index\.json\"[^/]*/\>[[:space:]]*\</auditSources\>$ ]]
}
# Whether a project file (argument 1) targets net<argument 2>.0 and nothing else: one TargetFramework or TargetFrameworks line, naming that one framework.
# Prints the frameworks it found, one line per element.
targets_net_major() {
  local tfms
  tfms="$(sed -n -E 's#^[[:space:]]*<TargetFrameworks?>([^<]*)</TargetFrameworks?>[[:space:]]*$#\1#p' "$1" 2> /dev/null || true)"
  printf '%s\n' "$tfms"
  [[ -n "$2" && "$tfms" == "net$2.0" ]]
}
# The major version of the SDK a global.json (argument 1) names, printed. Fails when the file isn't readable JSON or its sdk.version doesn't start with one.
sdk_major_of() {
  local version
  version="$(jq -r '.sdk.version // empty' "$1" 2> /dev/null)" || return 1
  [[ "$version" =~ ^([0-9]+)\. ]] || return 1
  printf '%s\n' "${BASH_REMATCH[1]}"
}
# Whether a file (argument 1) is the empty shield (ADR 0017), <Project />.
is_empty_shield() { [[ "$(cat "$1" 2> /dev/null)" == "<Project />" ]]; }
# Whether a dotnet test run of the C# scripts' test project (argument 2) passes, given its exit (argument 1): 0 passes; 8, "no test ran", passes only
# while the project holds no .cs source of its own, so a suite that loses its tests fails. What a build writes under the project (bin/, obj/, TestResults/:
# an IDE's build leaves generated .cs files in obj/) isn't a source. A find that fails passes nothing.
scripts_tests_pass() {
  local sources
  (($1 == 0)) && return 0
  (($1 == 8)) || return 1
  sources="$(find "$2" -mindepth 1 \( -name bin -o -name obj -o -name TestResults \) -prune -o -name '*.cs' -print)" || return 1
  [[ -z "$sources" ]]
}
# Runs the C# scripts' test project in folder argument 1, building into argument 2, as test.sh's scripts shard does: from the project's folder, as ADR 0017's
# wrappers run a script, so the SDK and the test runner come from the global.json found from there up; restoring in locked mode, so a package version
# packages.lock.json doesn't pin fails (NU1004); with no CLI usage data, no first-run setup (as in the Template's ci.yml) and no logo (DOTNET_NOLOGO,
# the Factory's own addition). Exits as dotnet test does.
# No build server is left running, as in the gate's build of the scripts: dotnet test passes --disable-build-servers on to the test app (which exits 5),
# so the MSBuild node, the MSBuild server and the compiler server are turned off one by one.
# The output folder and TMPDIR are made absolute first: from the project's folder a relative one would name a folder inside the tree.
scripts_tests_run() {
  local out="$2" tmp="${TMPDIR:-/tmp}"
  [[ "$out" == /* ]] || out="$PWD/$out"
  [[ "$tmp" == /* ]] || tmp="$PWD/$tmp"
  (cd "$1" && TMPDIR="$tmp" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    dotnet test --project Scripts.Tests.csproj -p:RestoreLockedMode=true -p:UseSharedCompilation=false --artifacts-path "$out" --results-directory "$out/results")
}
# Copies the skill into folder argument 1, as <argument 1>/.claude/skills/repo-ci-baseline with the Factory's CLAUDE.md at <argument 1>, for a check on a
# copy of the C# scripts' test project, and prints the copy's project folder: the project compiles the scripts in the skill's github/ and its tests read
# the skill's files, and the Factory's checkout three folders above the skill (AutoRollout.Factory: its CLAUDE.md, and ADR 0016 in its docs/adr/), so
# a copy builds and passes only in that layout.
# What a build wrote into the project's folder (bin/, obj/, TestResults/) is left out.
scripts_tests_copy() {
  local copy="$1/.claude/skills/repo-ci-baseline"
  mkdir -p "$copy" "$1/docs/adr" && cp -R "$skill_dir/." "$copy" && cp "$skill_dir/../../../CLAUDE.md" "$1/" \
    && cp "$skill_dir"/../../../docs/adr/0016-*.md "$1/docs/adr/" || return 1
  rm -rf "$copy/tests/Scripts.Tests/bin" "$copy/tests/Scripts.Tests/obj" "$copy/tests/Scripts.Tests/TestResults"
  printf '%s\n' "$copy/tests/Scripts.Tests"
}
# Whether an action or workflow (argument 1) has an actions/setup-dotnet step pinned to a 40-hex commit with global-json-file: global.json under it,
# before the next step; prints the commit. A commented-out line is no step.
setup_dotnet_step() {
  awk '
    { sub(/[ \t\r]+$/, "") }
    /^[ \t]*#/ { next }
    /^ *- / || /^ *uses:/ { if (sha != "" && json) { print sha; found = 1; exit } sha = ""; json = 0 }
    match($0, /uses: actions\/setup-dotnet@[0-9a-f]+( |$)/) { s = $0; sub(/.*@/, "", s); sub(/ .*/, "", s); if (length(s) == 40) sha = s }
    sha != "" && /^ +global-json-file: global\.json$/ { json = 1 }
    END { if (!found && sha != "" && json) { print sha; found = 1 } exit !found }' "$1"
}
# The shard is checked before the tools, after the temp folders and their trap above (the scripts shard's CDPATH check relies on that), and must be
# exactly one of the declared names: a value such as "hooks scripts" would match no block and run nothing.
shards=(hooks scripts checks)
shard="${TEST_SH_SHARD:-}"
known=0
for s in "${shards[@]}"; do [[ "$shard" != "$s" ]] || known=1; done
if [[ -n "$shard" ]] && ((known == 0)); then
  echo "test.sh: TEST_SH_SHARD must be one of ${shards[*]}, not '$shard'." >&2
  exit 2
fi
in_shard() { [[ -z "$shard" || "$shard" == "$1" ]]; }

for tool in actionlint zizmor yamllint shellcheck shfmt markdownlint-cli2 git; do need "$tool"; done
# check_gh_repo.py reads the syntax tree of shfmt v3.14 or a later v3 (ADR 0011): an older or newer one would fail its tests deep inside them.
[[ "$(shfmt --version)" =~ ^v3\.(1[4-9]|[2-9][0-9]|[1-9][0-9]{2,})\.[0-9]+$ ]] \
  || {
    echo "test.sh: shfmt v3.14 or a later v3 release is required, not '$(shfmt --version)'" >&2
    exit 2
  }

# pytest is used in the scripts and checks shards, so it's chosen before either runs, and only when one of them does.
if in_shard scripts || in_shard checks; then
  if python3 -c 'import pytest' &> /dev/null; then
    pytest_cmd=(python3 -m pytest)
  else
    need uvx
    pytest_cmd=(uvx pytest)
  fi
fi

if in_shard hooks; then
  step "Hook tests"
  bash "$owned/.github/hooks/tests/pre-push.test.sh" || fail "pre-push.test.sh"
  bash "$owned/.github/hooks/tests/pre-commit.test.sh" || fail "pre-commit.test.sh"
  bash "$owned/scripts/tests/check-branch-name.test.sh" || fail "check-branch-name.test.sh"
  bash "$owned/scripts/tests/check-pr-title.test.sh" || fail "check-pr-title.test.sh"
  bash "$owned/scripts/tests/sync-copilot-review.test.sh" || fail "sync-copilot-review.test.sh"
  bash "$owned/scripts/tests/watch-pr.test.sh" || fail "watch-pr.test.sh"
  bash "$owned/scripts/tests/cleanup.test.sh" || fail "cleanup.test.sh"
  bash "$owned/scripts/tests/work-issue.test.sh" || fail "work-issue.test.sh"
  # Both places that enforce the sync run the script's --check, which skips a repo without the code-review skill.
  # It runs through bash, so a lost mode bit fails loudly instead of skipping the check, and the anchors are the invocation lines (a step's label names the script too).
  grep -qxF '  bash scripts/sync-copilot-review.sh --check' "$owned/scripts/gate.sh" || fail "the Owned gate.sh must run: bash scripts/sync-copilot-review.sh --check"
  grep -qxF '        run: bash scripts/sync-copilot-review.sh --check' "$owned/.github/workflows/copilot-review-sync.yml" \
    || fail "the Owned copilot-review-sync.yml must run: bash scripts/sync-copilot-review.sh --check"
  bash "$owned/scripts/tests/check-git-hooks.test.sh" || fail "check-git-hooks.test.sh"
  bash "$owned/scripts/tests/lint-edited-file.test.sh" || fail "lint-edited-file.test.sh"
  # Every Owned script's and hook's test suite runs in the Owned ci.yml's hook-tests job, found by name, so a suite added later
  # can't be left out of it (check-git-hooks.test.sh once was) and every repo tests its copy.
  # Only the hook-tests job's block is read (from its key to the next job's), so a run line in another job doesn't count.
  hook_suites_run() { # hook_suites_run <ci.yml>: prints each Owned suite the hook-tests job doesn't run; exit 1 when one is missing or none is found
    local suite block n=0 st=0
    block="$(awk '/^  hook-tests:$/ { on = 1; print; next } on && /^  [A-Za-z0-9_-]+:$/ { exit } on' "$1")"
    for suite in "$owned"/.github/hooks/tests/*.test.sh "$owned"/scripts/tests/*.test.sh; do
      [[ -f "$suite" ]] || continue
      n=$((n + 1))
      grep -qxF "        run: bash ${suite#"$owned"/}" <<< "$block" || {
        echo "${suite#"$owned"/}"
        st=1
      }
    done
    ((n > 0)) || {
      echo "(no Owned test suite under .github/hooks/tests/ or scripts/tests/)"
      st=1
    }
    return "$st"
  }
  missing="$(hook_suites_run "$owned/.github/workflows/ci.yml")" \
    || fail "the Owned ci.yml's hook-tests job must run every Owned test suite, so every repo tests its copy; it doesn't run: $missing"
  # Each way a suite can drop out of the job is refused, on a copy of ci.yml: its run line removed, or moved to another job.
  hook_ci="$md_tmp/hook-tests-ci.yml"
  # shellcheck disable=SC2016 # sed scripts, not shell
  for drift in 'removed|/^        run: bash scripts\/tests\/cleanup\.test\.sh$/d' \
    'moved to another job|/^        run: bash scripts\/tests\/cleanup\.test\.sh$/d; /^  automerge-tests:$/a\        run: bash scripts/tests/cleanup.test.sh'; do
    cp "$owned/.github/workflows/ci.yml" "$hook_ci"
    sed -i -e "${drift#*|}" "$hook_ci"
    # The moved case must hold the run line in another job, or it would only repeat the removed case.
    [[ "${drift%%|*}" != moved* ]] || grep -qxF '        run: bash scripts/tests/cleanup.test.sh' "$hook_ci" \
      || fail "the drift copy for 'moved to another job' lost its moved run line: is automerge-tests still the job after hook-tests?"
    missing="$(hook_suites_run "$hook_ci")" && fail "the hook-tests check passes a ci.yml with cleanup.test.sh's run line ${drift%%|*}"
    [[ "$missing" == "scripts/tests/cleanup.test.sh" ]] || fail "the hook-tests check names '$missing', not scripts/tests/cleanup.test.sh, for its run line ${drift%%|*}"
  done
  rm -f "$hook_ci"
  # The edit-time lint sees only Write, Edit and MultiEdit, so an Owned rules file tells every session to edit Markdown and YAML with them.
  # It loads in every session (no front matter, so no paths:), and it is written into every repo, so it names nothing of the Factory's.
  edit_rule="$owned/.claude/rules/markdown-and-yaml-edits.md"
  if [[ ! -f "$edit_rule" ]]; then
    fail "the Template must carry .claude/rules/markdown-and-yaml-edits.md as an Owned file: the edit-time lint sees only Write, Edit and MultiEdit"
  else
    # A byte-order mark, a CR (a CRLF save) or trailing blanks after an opening `---` count as front matter too, so the check errs towards failing.
    edit_rule_first="$(head -n 1 "$edit_rule")"
    edit_rule_first="${edit_rule_first#$'\xEF\xBB\xBF'}"
    [[ ! "$edit_rule_first" =~ ^---[[:space:]]*$ ]] \
      || fail "the Owned .claude/rules/markdown-and-yaml-edits.md must load in every session: it takes no front matter"
    # 'Where .claude/settings.json has no such entry': what a session does in a repository that hasn't turned the hook on (most Applications).
    # shellcheck disable=SC2016 # the backticks are Markdown, matched as text
    for needle in 'with the Write and Edit tools, never with a shell command' 'scripts/lint-edited-file.sh' 'PostToolUse' '.claude/settings.json' \
      'Where `.claude/settings.json` has no such entry' 'markdownlint-cli2 <file>' 'yamllint -c .yamllint.yml <file>' \
      'actionlint <file>' 'zizmor --offline --min-severity medium <file>'; do
      grep -qF -- "$needle" "$edit_rule" || fail "the Owned .claude/rules/markdown-and-yaml-edits.md must say '$needle'"
    done
    if grep -niE 'dotfiles|repo-ci-baseline|factory|references/|docs/adr/' "$edit_rule"; then
      fail "the Owned .claude/rules/markdown-and-yaml-edits.md is written into every repo; it must not name the Factory, its skill or its docs"
    fi
    # The rule names the tools the hook runs after: each one in the matcher of the settings entry the script's header gives. A field that
    # isn't a plain tool name (empty, padded, a regex or a group) fails, since it would match any line or none, not name a tool.
    edit_rule_matcher="$(sed -nE 's/^#[[:space:]]*"matcher": "([^"]*)".*/\1/p' "$owned/scripts/lint-edited-file.sh" | head -n 1)"
    if [[ -z "$edit_rule_matcher" ]]; then
      fail "scripts/lint-edited-file.sh's header no longer gives the settings entry's \"matcher\", which .claude/rules/markdown-and-yaml-edits.md is checked against"
    else
      IFS='|' read -r -a edit_rule_tools <<< "$edit_rule_matcher"
      # read -a drops the empty field after a trailing `|`, so add it back: an empty last field still fails below.
      [[ "$edit_rule_matcher" != *'|' ]] || edit_rule_tools+=("")
      for tool in "${edit_rule_tools[@]}"; do
        if [[ ! "$tool" =~ ^[A-Za-z0-9_]+$ ]]; then
          fail "scripts/lint-edited-file.sh's matcher \"$edit_rule_matcher\" has the field '$tool', not a plain tool name, so .claude/rules/markdown-and-yaml-edits.md can't be checked against it"
          continue
        fi
        grep -qwF -- "$tool" "$edit_rule" \
          || fail "the Owned .claude/rules/markdown-and-yaml-edits.md must name $tool: scripts/lint-edited-file.sh runs after it (\"matcher\": \"$edit_rule_matcher\")"
      done
    fi
  fi
  # The edit-time lint lints and formats the shell files the gate does, with the shfmt releases it accepts: the hook's hook-name rule
  # is the Owned gate's (its SHELL_FILES and SHFMT_FILES lists, and the Lint Actions shellcheck job's), and its shfmt release rule is the
  # Owned gate's and this script's.
  python3 -I - "$owned" "$skill_dir/test.sh" << 'PY' || fail "scripts/lint-edited-file.sh must take the gate's shell files and shfmt releases (see above)"
import re, sys
owned, test_sh = sys.argv[1], sys.argv[2]
read = lambda path: open(path, encoding="utf-8").read()
hook, gate, actions = read(f"{owned}/scripts/lint-edited-file.sh"), read(f"{owned}/scripts/gate.sh"), read(f"{owned}/.github/workflows/lint-actions.yml")
problems = []
# The rule is the one in hook_name's own body: other functions match their argument with a line of the same shape.
names = re.findall(r'^hook_name\(\) \{\n(?:  [^\n]*\n)*?  \[\[ "\$1" =~ \^(.+)\$ \]\]\n\}$', hook, re.M)
if len(names) != 1:
    problems.append(f"lint-edited-file.sh's hook_name rule is not found once ({len(names)} found)")
else:
    hooks = "^\\.github/hooks/" + names[0] + "$"
    for where, text, pattern in (("gate.sh's SHELL_FILES", gate, r"^mapfile .* SHELL_FILES .*$"), ("gate.sh's SHFMT_FILES", gate, r"^mapfile .* SHFMT_FILES .*$"),
                                 ("lint-actions.yml's shellcheck job", actions, r"^ *mapfile -t files .*$")):
        lines = re.findall(pattern, text, re.M)
        if len(lines) != 1 or hooks not in lines[0]:
            problems.append(f"{where} doesn't take the hooks lint-edited-file.sh does ({hooks})")
release = lambda text: set(re.findall(r"=~ (\^v3\S+\$) \]\]", text))
rules = {"lint-edited-file.sh": release(hook), "gate.sh": release(gate), "test.sh": release(read(test_sh))}
if any(len(found) != 1 for found in rules.values()) or len(set.union(*rules.values())) != 1:
    problems.append(f"the shfmt release rules differ, or one is missing: {rules}")
for problem in problems:
    print(problem)
sys.exit(1 if problems else 0)
PY
  # The edit-time lint and the Owned gate check Python with the same ruff release rule and flags, and CI's python-tests job passes the
  # same flags. Their --target-version is the python-version CI runs. requirements-tests.txt pins the release the gate runs through uvx,
  # one the rule takes: a bump of the pin to another release line fails here, so the rule is moved with it on purpose (Dependabot leaves
  # ruff alone; see the dependabot.yml check). The Seed Sandcastle Dockerfile installs the same release, since the sandbox has no uv for the
  # gate to fall back on. ruff_agree <owned folder> <seed folder> checks the Template's files in those folders.
  ruff_agree() {
    python3 -I - "$1" "$2" << 'PY'
import re, shlex, sys
owned, seed = sys.argv[1], sys.argv[2]
read = lambda path: open(path, encoding="utf-8").read()
texts = {"lint-edited-file.sh": read(f"{owned}/scripts/lint-edited-file.sh"), "gate.sh": read(f"{owned}/scripts/gate.sh"),
         "ci.yml": read(f"{owned}/.github/workflows/ci.yml"), "requirements-tests.txt": read(f"{owned}/.github/scripts/requirements-tests.txt"),
         "Dockerfile": read(f"{seed}/.sandcastle/Dockerfile")}
problems = []
def one(where, pattern):
    found = re.findall(pattern, texts[where], re.M)
    if len(found) != 1:
        problems.append(f"{where}: {pattern} is not found once ({len(found)} found)")
        return None
    return found[0]
rules = {where: one(where, r"^RUFF_RELEASE='([^']+)'$") for where in ("lint-edited-file.sh", "gate.sh")}
flags = {where: one(where, r"^RUFF_ARGS=\((.+)\)$") for where in ("lint-edited-file.sh", "gate.sh")}
flags = {where: shlex.split(found) for where, found in flags.items() if found is not None}
ci = one("ci.yml", r"^ *run: python -m ruff (.+) -- \S.*$")
if ci is not None:
    flags["ci.yml"] = shlex.split(ci)
# CI's paths are folders of the Template, and between them they hold every Owned Python file, so a moved folder or a new one is found here.
ci_paths = one("ci.yml", r"^ *run: python -m ruff .+ -- (\S.*)$")
if ci_paths is not None:
    import os
    folders = shlex.split(ci_paths)
    for folder in folders:
        if not os.path.isdir(os.path.join(owned, folder)):
            problems.append(f"ci.yml's ruff step lints {folder}, which is not a folder of the Template")
    # Folders ruff skips when it walks a folder it is given (its default exclude, which --isolated keeps): a file under one is not linted.
    skipped = {".bzr", ".direnv", ".eggs", ".git", ".git-rewrite", ".hg", ".ipynb_checkpoints", ".mypy_cache", ".nox", ".pants.d", ".pyenv",
               ".pytest_cache", ".pytype", ".ruff_cache", ".svn", ".tox", ".venv", ".vscode", "__pypackages__", "_build", "buck-out", "build",
               "dist", "node_modules", "site-packages", "venv"}
    for top, dirs, files in os.walk(owned):
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        for name in files:
            if name.endswith(".py"):
                path = os.path.relpath(os.path.join(top, name), owned)
                given = next((folder.rstrip("/") for folder in folders if path.startswith(folder.rstrip("/") + "/")), None)
                # Only the folders below the one ci.yml names: ruff's default exclude doesn't drop a path it is given.
                parts = path[len(given) + 1 :].split(os.sep)[:-1] if given else []
                if given is None:
                    problems.append(f"ci.yml's ruff step doesn't lint the Owned {path}")
                elif skipped.intersection(parts):
                    problems.append(f"the Owned {path} is under a folder ruff skips by default ({sorted(skipped.intersection(parts))[0]}), so CI's ruff step doesn't lint it")
version = one("gate.sh", r'^RUFF_VERSION="([^"]+)"$')
pin = one("requirements-tests.txt", r"^ruff==(\S+) \\$")
if len(set(rules.values())) != 1:
    problems.append(f"the ruff release rules differ: {rules}")
if len({tuple(found) for found in flags.values()}) != 1:
    problems.append(f"the ruff flags differ: {flags}")
if "--isolated" not in flags.get("gate.sh", []):
    problems.append("gate.sh's RUFF_ARGS must hold --isolated, so no config an agent could write is read")
# ruff's target is the Python CI runs (ADR 0011, Update: Python): syntax newer than it is refused by design, and a move of CI's Python
# moves the target in the same change.
python = one("ci.yml", r'^ *python-version: "(3\.[0-9]+)"$')
gate_flags = flags.get("gate.sh", [])
target = gate_flags[gate_flags.index("--target-version") + 1] if "--target-version" in gate_flags[:-1] else None
if target is None:
    problems.append("gate.sh's RUFF_ARGS must give --target-version, the Python CI runs")
elif python is not None and target != "py" + python.replace(".", ""):
    problems.append(f"gate.sh's ruff --target-version {target} is not ci.yml's python-version {python}")
if version is not None and pin is not None and version != pin:
    problems.append(f"gate.sh's RUFF_VERSION {version} is not requirements-tests.txt's ruff=={pin}")
# The image's ARG names the release; that its RUN pip install line installs ruff==${RUFF_VERSION} is the Seed Sandcastle files step's check.
image = one("Dockerfile", r"^ARG RUFF_VERSION=(\S+)$")
if version is not None and image is not None and version != image:
    problems.append(f"the Seed .sandcastle/Dockerfile's ARG RUFF_VERSION {image} is not gate.sh's RUFF_VERSION {version}")
rule = rules.get("gate.sh")
if rule and version and not re.fullmatch(rule, f"ruff {version}"):
    problems.append(f"RUFF_VERSION {version} is not a release the rule {rule} takes")
# The messages name the release line, so they move with the rule too.
if version:
    line = ".".join(version.split(".")[:2])
    for where, words in (("lint-edited-file.sh", f"is not a {line} release"), ("gate.sh", f"needs ruff {line}")):
        if words not in texts[where]:
            problems.append(f"{where} doesn't say '{words}', the release line RUFF_VERSION {version} is in")
for problem in problems:
    print(problem)
sys.exit(1 if problems else 0)
PY
  }
  ruff_agree "$owned" "$seed" || fail "scripts/lint-edited-file.sh, scripts/gate.sh, ci.yml, requirements-tests.txt and the Seed .sandcastle/Dockerfile must agree on ruff (see above)"
  # Each way the target can drift from CI's Python, or the Seed Dockerfile from the gate's release, is refused, on a copy of the five files edited that way. The copy holds the folders
  # CI's ruff step lints too, so as it is it passes, and only the edit makes it fail. Under md_tmp, which the EXIT trap removes.
  ruff_copy="$md_tmp/ruff"
  ruff_drift() { # ruff_drift <what> [<expected problem> <file> <sed script>]: with no edit, the copy must pass
    local f found
    rm -rf "${ruff_copy:?}" && mkdir -p "$ruff_copy/scripts" "$ruff_copy/.github/workflows" "$ruff_copy/.github/scripts"
    for f in scripts/lint-edited-file.sh scripts/gate.sh .github/workflows/ci.yml .github/scripts/requirements-tests.txt; do cp "$owned/$f" "$ruff_copy/$f"; done
    mkdir -p "$ruff_copy/seed/.sandcastle" && cp "$seed/.sandcastle/Dockerfile" "$ruff_copy/seed/.sandcastle/Dockerfile"
    local -a linted
    read -ra linted <<< "$(sed -n 's/^ *run: python -m ruff .* -- //p' "$owned/.github/workflows/ci.yml")"
    for f in ${linted[@]+"${linted[@]}"}; do mkdir -p "$ruff_copy/$f"; done
    if (($# == 1)); then
      found="$(ruff_agree "$ruff_copy" "$ruff_copy/seed" 2>&1)" || fail "the ruff agreement check refuses $1: $found"
      return 0
    fi
    sed -i -e "$4" "$ruff_copy/$3"
    found="$(ruff_agree "$ruff_copy" "$ruff_copy/seed" 2>&1)" && fail "the ruff agreement check passes $1"
    grep -qF -- "$2" <<< "$found" || fail "the ruff agreement check doesn't say '$2' for $1: $found"
  }
  ruff_drift "an unedited copy of the Template's files"
  ruff_drift "a gate RUFF_ARGS with no --target-version" "gate.sh's RUFF_ARGS must give --target-version" scripts/gate.sh 's/ --target-version py312)$/)/'
  ruff_drift "a target that isn't CI's Python" "gate.sh's ruff --target-version py312 is not ci.yml's python-version 3.11" \
    .github/workflows/ci.yml 's/python-version: "3.12"/python-version: "3.11"/'
  ruff_drift "a ci.yml with two python-version lines" "is not found once (2 found)" .github/workflows/ci.yml 's/^\( *\)python-version: "3.12"$/&\n\1python-version: "3.12"/'
  # The other release is the gate's with its patch number moved on by one, so the case holds when RUFF_VERSION moves.
  gate_ruff="$(sed -n 's/^RUFF_VERSION="\([0-9]*\.[0-9]*\.[0-9]*\)"$/\1/p' "$owned/scripts/gate.sh")"
  [[ "$gate_ruff" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "found no RUFF_VERSION=\"<x.y.z>\" line in the Owned gate.sh for the Dockerfile drift case"
  other_ruff="${gate_ruff%.*}.$((${gate_ruff##*.} + 1))"
  ruff_drift "a Seed Dockerfile on another ruff" "the Seed .sandcastle/Dockerfile's ARG RUFF_VERSION $other_ruff is not gate.sh's RUFF_VERSION $gate_ruff" \
    seed/.sandcastle/Dockerfile "s/^ARG RUFF_VERSION=.*\$/ARG RUFF_VERSION=$other_ruff/"
  ruff_drift "a Seed Dockerfile with no ruff ARG" "Dockerfile: ^ARG RUFF_VERSION=" seed/.sandcastle/Dockerfile '/^ARG RUFF_VERSION=/d'
  rm -rf "$ruff_copy"
  # The docs that name ruff's target (ADR 0011's Update: Python, references/git-hooks.md) name the gate's, so a move of CI's Python
  # can't leave them stale (#91). Every py3NN in both is the target, each names one, and every 3.NN in the ADR's bullet on it is the
  # target's Python. (The role prompts give the gate's whole RUFF_ARGS, which tests/sandcastle/template-prompts.test.mts checks.)
  # The ADR is the Factory's, outside the skill: this is the one check that needs the Factory's checkout around the skill, so
  # test.sh run from a copy of the skill alone fails here, naming the ADR.
  # ruff_docs_agree <gate.sh> <ADR> <git-hooks.md>
  ruff_docs_agree() {
    python3 -I - "$@" << 'PY'
import re, shlex, sys
gate, adr, hooks = (open(path, encoding="utf-8").read() for path in sys.argv[1:4])
found = re.findall(r"^RUFF_ARGS=\((.+)\)$", gate, re.M)
flags = shlex.split(found[0]) if len(found) == 1 else []
if flags[:-1].count("--target-version") != 1:
    print("gate.sh's RUFF_ARGS must give --target-version once")
    sys.exit(1)
target = flags[flags.index("--target-version") + 1]
python = f"{target[2]}.{target[3:]}"
problems = []
for where, text in (("ADR 0011", adr), ("references/git-hooks.md", hooks)):
    named = re.findall(r"\bpy3[0-9]+\b", text)
    if not named:
        problems.append(f"{where} doesn't name the target {target}")
    problems += [f"{where} names {other}, but the gate's ruff target is {target}" for other in sorted(set(named) - {target})]
# The bullet ends at the next bullet, heading or blank line, so a paragraph after it is not read as part of it.
bullet = re.search(r"^- \*\*The target is the Python CI runs\.\*\*.*?(?=^- |^#|^[ \t]*$|\Z)", adr, re.M | re.S)
if bullet is None:
    problems.append("ADR 0011 has no bullet 'The target is the Python CI runs.'")
else:
    # A 3.NN that doesn't start inside a longer number ([0-9]+ takes every digit): one before a full stop (a sentence's end) or a
    # patch number counts as its 3.NN.
    versions = set(re.findall(r"(?<![0-9.])3\.[0-9]+", bullet.group(0)))
    problems += [f"ADR 0011's target bullet names Python {other}, but the gate's ruff target is {target} (Python {python})" for other in sorted(versions - {python})]
for problem in problems:
    print(problem)
sys.exit(1 if problems else 0)
PY
  }
  # Without the GIT_* variables a hook sets, which would point git at another repository.
  factory_root="$(env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX git -C "$skill_dir" rev-parse --show-toplevel 2>&1)" \
    || fail "test.sh can't find the Factory's checkout around $skill_dir: $factory_root"
  ruff_adr="$factory_root/docs/adr/0011-markdown-and-yaml-are-linted-as-they-are-written.md"
  if [[ -f "$ruff_adr" ]]; then
    found="$(ruff_docs_agree "$owned/scripts/gate.sh" "$ruff_adr" "$skill_dir/references/git-hooks.md" 2>&1)" \
      || fail "ADR 0011 and references/git-hooks.md must name the gate's ruff target: $found"
    # Each way the docs and the gate can drift apart is refused, on copies edited that way; with an empty expected problem the
    # edited copies must pass. The gate's copy moves too, so the docs are shown to be read against its target, not a fixed one.
    ruff_docs_drift() { # ruff_docs_drift <what> <expected problem, or ''> <adr|hooks|gate> <sed script>
      local docs="$md_tmp/ruff-docs" found
      rm -rf "${docs:?}" && mkdir -p "$docs"
      cp "$ruff_adr" "$docs/adr" && cp "$skill_dir/references/git-hooks.md" "$docs/hooks" && cp "$owned/scripts/gate.sh" "$docs/gate"
      cp "$docs/$3" "$docs/before"
      sed -i -e "$4" "$docs/$3"
      [[ "$1" == "an unedited copy" ]] || ! cmp -s "$docs/before" "$docs/$3" || fail "the ruff docs check's edit for $1 changed nothing: fix its sed script"
      if [[ -z "$2" ]]; then
        found="$(ruff_docs_agree "$docs/gate" "$docs/adr" "$docs/hooks" 2>&1)" || fail "the ruff docs check refuses $1: $found"
      else
        found="$(ruff_docs_agree "$docs/gate" "$docs/adr" "$docs/hooks" 2>&1)" && fail "the ruff docs check passes $1"
        grep -qF -- "$2" <<< "$found" || fail "the ruff docs check doesn't say '$2' for $1: $found"
      fi
      rm -rf "$docs"
    }
    ruff_docs_drift "an unedited copy" "" adr 's/^$//'
    ruff_docs_drift "an ADR naming py313" "ADR 0011 names py313" adr 's/py312/py313/'
    ruff_docs_drift "a git-hooks.md naming py311" "references/git-hooks.md names py311" hooks 's/py312/py311/'
    ruff_docs_drift "a git-hooks.md naming no target" "references/git-hooks.md doesn't name the target py312" hooks 's/py312/pyNNN/g'
    ruff_docs_drift "an ADR naming no target" "ADR 0011 doesn't name the target py312" adr 's/py312/pyNNN/g'
    ruff_docs_drift "an ADR bullet naming Python 3.13" "ADR 0011's target bullet names Python 3.13" adr 's/syntax newer than 3\.12/syntax newer than 3.13/'
    ruff_docs_drift "an ADR bullet naming Python 3.13 at a sentence's end" "ADR 0011's target bullet names Python 3.13" adr \
      's/is refused by the hook,/is refused, as in Python 3.13. The hook,/'
    ruff_docs_drift "an ADR bullet naming Python 3.13.1" "ADR 0011's target bullet names Python 3.13" adr 's/syntax newer than 3\.12/syntax newer than 3.13.1/'
    ruff_docs_drift "an ADR bullet naming the target's Python with a patch number" "" adr 's/syntax newer than 3\.12/syntax newer than 3.12.4/'
    ruff_docs_drift "an ADR without the target bullet" "ADR 0011 has no bullet" adr 's/The target is the Python CI runs\./The target./'
    ruff_docs_drift "a paragraph after the bullet naming another Python" "" adr \
      's/^- \*\*One release line\.\*\*/\nA later paragraph that names Python 3.9 and more.\n\n&/'
    ruff_docs_drift "a gate target moved with the docs left" "ADR 0011 names py312, but the gate's ruff target is py313" gate 's/--target-version py312/--target-version py313/'
    ruff_docs_drift "a gate target moved with git-hooks.md left" "references/git-hooks.md names py312, but the gate's ruff target is py313" gate \
      's/--target-version py312/--target-version py313/'
    ruff_docs_drift "a gate RUFF_ARGS with no --target-version" "gate.sh's RUFF_ARGS must give --target-version once" gate 's/ --target-version py312)$/)/'
    ruff_docs_drift "a gate RUFF_ARGS with --target-version twice" "gate.sh's RUFF_ARGS must give --target-version once" gate \
      's/ --target-version py312)$/ --target-version py312 --target-version py313)/'
  else
    fail "test.sh can't find ADR 0011 in the Factory's docs/adr/ (from $skill_dir), to check it names the gate's ruff target"
  fi
  # The script edit rule: the checks reviewers keep raising on script changes, loaded when a session works on a script (its paths: front matter
  # names the shell and Python files and the folders the hook lints), and written into every repo, so it names nothing of the Factory's.
  script_rule="$owned/.claude/rules/script-edits.md"
  if [[ ! -f "$script_rule" ]]; then
    fail "the Template must carry .claude/rules/script-edits.md as an Owned file: the checks reviewers keep raising on script changes"
  else
    # The front matter: a first line of exactly ---, then the paths: list up to the next ---.
    script_rule_paths="$(awk 'NR == 1 { if ($0 != "---") exit; next } $0 == "---" { exit } { print }' "$script_rule")"
    for glob in '**/*.sh' '**/*.py' 'scripts/**' '.github/hooks/**'; do
      grep -qxF -- "  - \"$glob\"" <<< "$script_rule_paths" \
        || fail "the Owned .claude/rules/script-edits.md's paths: front matter must name \"$glob\", so it loads where the hook lints"
    done
    grep -qx 'paths:' <<< "$script_rule_paths" || fail "the Owned .claude/rules/script-edits.md must open with paths: front matter"
    # shellcheck disable=SC2016 # the backticks are Markdown, matched as text
    for needle in 'with the Write and Edit tools' 'scripts/lint-edited-file.sh' 'PostToolUse' 'shfmt -w <file>' '.editorconfig' \
      'A test for every new branch' 'The header or docstring in the same edit' 'CRLF' 'byte-order mark' 'symlink' 'The error contract' \
      'Where `.claude/settings.json` has no such entry' 'shellcheck <file>' 'shfmt -d <file>' 'ruff check --isolated --no-cache --select F,B'; do
      grep -qF -- "$needle" "$script_rule" || fail "the Owned .claude/rules/script-edits.md must say '$needle'"
    done
    # It names the tools the hook runs after, as the Markdown and YAML rule does: the plain names of the matcher read above.
    for tool in ${edit_rule_tools[@]+"${edit_rule_tools[@]}"}; do
      [[ "$tool" =~ ^[A-Za-z0-9_]+$ ]] || continue
      grep -qwF -- "$tool" "$script_rule" \
        || fail "the Owned .claude/rules/script-edits.md must name $tool: scripts/lint-edited-file.sh runs after it (\"matcher\": \"$edit_rule_matcher\")"
    done
    if grep -niE 'dotfiles|repo-ci-baseline|factory|references/|docs/adr/' "$script_rule"; then
      fail "the Owned .claude/rules/script-edits.md is written into every repo; it must not name the Factory, its skill or its docs"
    fi
  fi
  bash "$skill_dir/tests/owned-gate-files.test.sh" || fail "owned-gate-files.test.sh"
  # CI's Branch name job passes the PR's author, from the event payload (which a pusher can't set), so only dependabot[bot]'s own PR
  # gets any dependabot/* name (R42 of TicketManager's re-Apply review).
  # shellcheck disable=SC2016 # the workflow's own text, which must not expand here
  grep -qxF '          PR_AUTHOR: ${{ github.event.pull_request.user.login }}' "$owned/.github/workflows/ci.yml" \
    || fail "the Owned ci.yml's Branch name job must set PR_AUTHOR from github.event.pull_request.user.login"
  # shellcheck disable=SC2016 # the workflow's own text
  grep -qxF '        run: bash scripts/check-branch-name.sh --pr --author "$PR_AUTHOR" "$HEAD_REF"' "$owned/.github/workflows/ci.yml" \
    || fail "the Owned ci.yml's Branch name job must run: bash scripts/check-branch-name.sh --pr --author \"\$PR_AUTHOR\" \"\$HEAD_REF\""
  # Every pip install in an Owned workflow takes only hashed pins (R43 of TicketManager's re-Apply review): the python-tests job installs
  # requirements-tests.txt with --require-hashes, and every pin there is exact and carries a hash. --only-binary=:all: makes pip refuse a source
  # archive, whose build would fetch its build backend unhashed.
  grep -qxF '          python -m pip install --require-hashes --no-deps --only-binary=:all: -r .github/scripts/requirements-tests.txt' "$owned/.github/workflows/ci.yml" \
    || fail "the Owned ci.yml's python-tests job must install .github/scripts/requirements-tests.txt with --require-hashes --no-deps --only-binary=:all:"
  if grep -nE 'pip install' "$owned"/.github/workflows/*.yml | grep -v -- '--require-hashes.*--only-binary=:all:'; then
    fail "every pip install in an Owned workflow must use --require-hashes and --only-binary=:all: (the lines above don't)"
  fi
  # The Factory's template-tools action installs the same hashed file, so a bump's PR runs the Template's Python tests on its pins
  # (R22), and no second pytest pin can drift from it.
  template_tools="$skill_dir/../../../.github/actions/template-tools/action.yml"
  hashed_install='        python -m pip install --require-hashes --no-deps --only-binary=:all: -r .claude/skills/repo-ci-baseline/template/owned/.github/scripts/requirements-tests.txt'
  grep -qxF -- "$hashed_install" "$template_tools" \
    || fail "the template-tools action must install the Template's requirements-tests.txt with --require-hashes --no-deps --only-binary=:all:"
  [[ -f "$owned/.github/scripts/requirements-tests.txt" ]] || fail "the Template's .github/scripts/requirements-tests.txt is missing"
  # Any other line that names pytest or PyYAML, in any case: only the exact hashed install line, comment lines and the top-level
  # description block may. A pin on the hashed line's own copy, or after a trailing comment, is on another line and is caught.
  second_pin() {
    awk '/^description:/ { desc = 1; next } /^[^ ]/ { desc = 0 } !desc { print NR ":" $0 }' "$1" \
      | grep -vxF -- "$(grep -nxF -- "$hashed_install" "$1" | head -n 1)" \
      | grep -vE '^[0-9]+:[[:space:]]*#' \
      | grep -iE '(^|[^a-z0-9_-])(pytest|pyyaml)([^a-z0-9_-]|$)|PYTEST_|PYYAML_'
  }
  if second_pin "$template_tools"; then
    fail "the template-tools action must take pytest and PyYAML from requirements-tests.txt only (the lines above name them again)"
  fi
  # The guard itself catches each spelling a re-pin is likely to take, and passes the description and the hashed line.
  pin_fixture() { printf 'description: >\n  pytest and PyYAML from requirements-tests.txt\nruns:\n  steps:\n      run: |\n%s\n%s\n' "$hashed_install" "$1" > "$md_tmp/action-pin.yml"; }
  for spelling in '        pip install pytest "yamllint==1"' '        "PyYAML==6.0.3"' '        pytest>=9' '        pyyaml~=6' '        PYTEST_VERSION: "9"' '        pip install Pytest' \
    '        python -m pip install pytest==9 -r x/requirements-tests.txt' '        pip install -r requirements-tests.txt pyyaml==5' '        pip install pytest==9  # requirements-tests.txt' \
    "${hashed_install} pytest==9" '  pytest: "9"' '    pytest==9'; do
    pin_fixture "$spelling"
    second_pin "$md_tmp/action-pin.yml" > /dev/null || fail "the template-tools pin guard misses: $spelling"
  done
  pin_fixture '        # pytest and PyYAML come from the line above'
  ! second_pin "$md_tmp/action-pin.yml" > /dev/null || fail "the template-tools pin guard flags the description, a comment or the hashed line"
  # Dependabot keeps the hashed pins current: the Factory's own entry bumps the Template's copy, the Seed's an Application's.
  for pair in "$skill_dir/../../../.github/dependabot.yml|/.claude/skills/repo-ci-baseline/template/owned/.github/scripts" \
    "$skill_dir/template/seed/.github/dependabot.yml|/.github/scripts"; do
    file="${pair%%|*}" dir="${pair#*|}"
    awk -v dir="$dir" '/package-ecosystem: "pip"/ { pip = 1; next } /package-ecosystem:/ { pip = 0 } pip && $0 ~ "directory: \"" dir "\"" { found = 1 } END { exit !found }' "$file" \
      || fail "$file must have a pip entry for $dir, so Dependabot bumps requirements-tests.txt"
    # But never ruff: its pin moves by hand with gate.sh's RUFF_VERSION and release rule (ADR 0011, Update: Python).
    awk '/package-ecosystem: "pip"/ { pip = 1; next } /package-ecosystem:/ { pip = 0 } pip && /^ *ignore:/ { ign = 1; next } pip && /^    [a-z]/ { ign = 0 }
      pip && ign && /^ *- dependency-name: "ruff" *$/ { found = 1 } END { exit !found }' "$file" \
      || fail "$file's pip entry must ignore ruff, whose pin moves only with gate.sh's RUFF_VERSION and release rule"
  done
  # The Factory moves the SDK: its own dotnet-sdk entry bumps the Template's Owned global.json (mpaulosky/Application_Factory#103) and the Factory's copy of it
  # at the root (mpaulosky/Application_Factory#117), so the copy never drifts from it.
  template_dir="/.claude/skills/repo-ci-baseline/template/owned"
  dotnet_sdk_entry_for "$skill_dir/../../../.github/dependabot.yml" "$template_dir" \
    || fail "the Factory's .github/dependabot.yml must have a dotnet-sdk entry for \"$template_dir\" (double-quoted), the Owned global.json"
  dependabot_entry_for nuget "$skill_dir/../../../.github/dependabot.yml" "/.claude/skills/repo-ci-baseline/tests/Scripts.Tests" \
    || fail "the Factory's .github/dependabot.yml must have a nuget entry for \"/.claude/skills/repo-ci-baseline/tests/Scripts.Tests\", so its packages and lock file are bumped"
  dotnet_sdk_entry_for "$skill_dir/../../../.github/dependabot.yml" "/" \
    || fail "the Factory's .github/dependabot.yml must have a dotnet-sdk entry for \"/\" (double-quoted), the Factory's copy of global.json"
  # The template-tools action installs the SDK the Factory's global.json names, with the Template ci.yml's setup-dotnet pin (ADR 0017).
  tools_sha="$(setup_dotnet_step "$template_tools")" \
    || fail "the template-tools action must have an actions/setup-dotnet step pinned to a commit, with global-json-file: global.json"
  ci_shas="$(setup_dotnet_pins_are "$tools_sha" "$owned/.github/workflows/ci.yml")" \
    || fail "the template-tools action's setup-dotnet pin ($tools_sha) must be the Template ci.yml's, its only one (found: ${ci_shas//$'\n'/ })"
  a40="0f4a354dd5547a28c70b7853dbd95d5b67886e73"
  b40="1111111111111111111111111111111111111111"
  pins_fixture() { printf 'jobs:\n  a:\n    steps:\n%s\n' "$1" > "$md_tmp/ci-pins.yml"; }
  pins_fixture "$(printf '      - uses: actions/setup-dotnet@%s # v6.1.0\n      - uses: actions/setup-dotnet@%s' "$a40" "$a40")"
  setup_dotnet_pins_are "$a40" "$md_tmp/ci-pins.yml" > /dev/null || fail "the setup-dotnet pin check refuses a ci.yml whose pins are all the action's"
  for wrong in "$(printf '      - uses: actions/setup-dotnet@%s\n      - uses: actions/setup-dotnet@%s' "$a40" "$b40")" \
    "$(printf '      - uses: actions/setup-dotnet@%s' "$b40")" "$(printf '      - uses: actions/setup-dotnet@v6')" "$(printf '      - run: "true"')" \
    "$(printf '      - uses: actions/setup-dotnet@%s\n      - uses: actions/setup-dotnet@v6' "$a40")" \
    "$(printf '      - uses: actions/setup-dotnet@%s\n      - uses: "actions/setup-dotnet@main"' "$a40")" \
    "$(printf '      - uses: actions/setup-dotnet@%s0' "$a40")"; do
    pins_fixture "$wrong"
    ! setup_dotnet_pins_are "$a40" "$md_tmp/ci-pins.yml" > /dev/null || fail "the setup-dotnet pin check passes: $wrong"
  done
  pins_fixture "$(printf '      - uses: actions/setup-dotnet@%s\n      # - uses: actions/setup-dotnet@v6' "$a40")"
  setup_dotnet_pins_are "$a40" "$md_tmp/ci-pins.yml" > /dev/null || fail "the setup-dotnet pin check counts a commented-out step"
  # The action's pin must be a commit itself: these workflows' refs equal it, so only that rule refuses them.
  pins_fixture '      - uses: actions/setup-dotnet@v6'
  ! setup_dotnet_pins_are "v6" "$md_tmp/ci-pins.yml" > /dev/null || fail "the setup-dotnet pin check passes an action pin that isn't a commit"
  pins_fixture '      - uses: actions/setup-dotnet@'
  ! setup_dotnet_pins_are "" "$md_tmp/ci-pins.yml" > /dev/null || fail "the setup-dotnet pin check passes an empty action pin"
  # Dependabot reads the template-tools action only where its folder is listed (mpaulosky/Application_Factory#145), so its pins move with ci.yml's.
  dependabot_entry_for github-actions "$skill_dir/../../../.github/dependabot.yml" "/.github/actions/template-tools" \
    || fail "the Factory's .github/dependabot.yml must list \"/.github/actions/template-tools\" (double-quoted) in its github-actions entry, so the action's pins are bumped"
  ga_fixture() { printf 'version: 2\nupdates:\n%s\n' "$1" > "$md_tmp/dependabot-ga.yml"; }
  ga_fixture "$(printf '  - package-ecosystem: "github-actions"\n    directories:\n      - "/"\n      - "/.github/actions/template-tools"')"
  dependabot_entry_for github-actions "$md_tmp/dependabot-ga.yml" "/.github/actions/template-tools" || fail "the github-actions entry check misses the template-tools folder"
  for wrong in \
    "$(printf '  - package-ecosystem: "github-actions"\n    directories:\n      - "/"\n      # - "/.github/actions/template-tools"')" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      - "/.github/actions/template-tools"\n  - package-ecosystem: "github-actions"\n    directory: "/"')" \
    "$(printf '  - package-ecosystem: "github-actions"\n    directories:\n      - "/.github/actions/template-tools/"')"; do
    ga_fixture "$wrong"
    ! dependabot_entry_for github-actions "$md_tmp/dependabot-ga.yml" "/.github/actions/template-tools" || fail "the github-actions entry check passes: $wrong"
  done
  dn_fixture() { printf 'runs:\n  using: composite\n  steps:\n%s\n    - name: Next\n      shell: bash\n      run: "true"\n' "$1" > "$md_tmp/action-dotnet.yml"; }
  for right in \
    "$(printf '    - name: Setup .NET\n      uses: actions/setup-dotnet@%s # v6.1.0\n      with:\n        global-json-file: global.json' "$a40")" \
    "$(printf '    - uses: actions/setup-dotnet@%s\n      with:\n        global-json-file: global.json  \r' "$a40")"; do
    dn_fixture "$right"
    [[ "$(setup_dotnet_step "$md_tmp/action-dotnet.yml")" == "$a40" ]] || fail "the setup-dotnet step check misses: $right"
  done
  for wrong in \
    "$(printf '    - name: Setup .NET\n      uses: actions/setup-dotnet@v6\n      with:\n        global-json-file: global.json')" \
    "$(printf '    - name: Setup .NET\n      uses: actions/setup-dotnet@%s\n      with:\n        dotnet-version: 11.0.x' "$a40")" \
    "$(printf '    - name: Setup .NET\n      uses: actions/setup-dotnet@%s\n      with:\n        global-json-file: other/global.json' "$a40")" \
    "$(printf '    # - uses: actions/setup-dotnet@%s\n    #   with:\n    #     global-json-file: global.json' "$a40")" \
    "$(printf '    - uses: actions/setup-dotnet@%s\n    - uses: actions/setup-python@%s\n      with:\n        global-json-file: global.json' "$a40" "$a40")" \
    "$(printf '    - uses: actions/setup-dotnet@%s0\n      with:\n        global-json-file: global.json' "$a40")" \
    "$(printf '    - name: Setup Python\n      uses: actions/setup-python@%s\n      with:\n        global-json-file: global.json' "$a40")"; do
    dn_fixture "$wrong"
    ! setup_dotnet_step "$md_tmp/action-dotnet.yml" > /dev/null || fail "the setup-dotnet step check passes: $wrong"
  done
  # The check itself, on fixtures: each "right" file has the entry, no "wrong" one does.
  sdk_entry() { printf 'version: 2\nupdates:\n%s\n' "$1" > "$md_tmp/dependabot-sdk.yml"; }
  t="$template_dir"
  for right in \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      - "/"\n      - "%s"\n    schedule:\n      interval: "weekly"' "$t")" \
    "$(printf '  - directory: "%s"\n    package-ecosystem: "dotnet-sdk"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"  \r\n    directory: "%s"\r' "$t")"; do
    sdk_entry "$right"
    dotnet_sdk_entry_for "$md_tmp/dependabot-sdk.yml" "$t" || fail "the dotnet-sdk entry check misses an entry naming the Template: $right"
  done
  for wrong in \
    "$(printf '  # - package-ecosystem: "dotnet-sdk"\n  #   directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "/"\n    # directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "pip"\n    directory: "%s"\n  - package-ecosystem: "dotnet-sdk"\n    directory: "/"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "/"\n  - package-ecosystem: "pip"\n    directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "github-actions"\n    directories:\n      - "/"\n      - "%s"\n  - package-ecosystem: "dotnet-sdk"\n    directory: "/"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      - "/"\n    ignore:\n      - "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "/"\n  - directory: "%s"\n    package-ecosystem: "pip"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "%s"' "${t/./X}")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "/"\nregistries:\n  r:\n    directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "%s/sub"' "$t")"; do
    sdk_entry "$wrong"
    ! dotnet_sdk_entry_for "$md_tmp/dependabot-sdk.yml" "$t" || fail "the dotnet-sdk entry check passes a file with no such entry: $wrong"
  done
  # And for the root, the Factory's copy: the Template's folder alone, or "/" under another ecosystem, is no entry for it.
  sdk_entry "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      - "/"\n      - "%s"' "$t")"
  dotnet_sdk_entry_for "$md_tmp/dependabot-sdk.yml" "/" || fail "the dotnet-sdk entry check misses \"/\" in a directories list"
  for wrong in \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      - "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "github-actions"\n    directories:\n      - "/"\n  - package-ecosystem: "dotnet-sdk"\n    directory: "%s"' "$t")" \
    "$(printf '  - package-ecosystem: "dotnet-sdk"\n    directories:\n      # - "/"\n      - "%s"' "$t")"; do
    sdk_entry "$wrong"
    ! dotnet_sdk_entry_for "$md_tmp/dependabot-sdk.yml" "/" || fail "the dotnet-sdk entry check passes a file with no entry for \"/\": $wrong"
  done
  python3 - "$owned/.github/scripts/requirements-tests.txt" << 'PY_REQ' || fail "requirements-tests.txt must pin each package exactly, with at least one sha256 hash, pytest and pyyaml among them"
import re, sys
text = open(sys.argv[1]).read()
entries = re.split(r"\n(?=[A-Za-z])", re.sub(r"^#.*\n|^\n", "", text, flags=re.M))
names = set()
for entry in filter(str.strip, entries):
    first = entry.split()[0]
    match = re.fullmatch(r"([a-z0-9][a-z0-9._-]*)==[0-9][^\s\\]*", first)
    assert match, f"not an exact pin: {first}"
    assert re.search(r"--hash=sha256:[0-9a-f]{64}", entry), f"no hash: {first}"
    names.add(match.group(1))
assert {"pytest", "pyyaml"} <= names, names
PY_REQ
fi # the hooks shard

if in_shard scripts; then
  step "Auto-merge script tests"
  need node
  node --test "$owned/.github/scripts/tests/pr-automerge.test.mjs" || fail "pr-automerge.test.mjs"

  step "Owned .github/scripts tests (release scripts, Claude Review, the gh check, the Owned files check and the central pins check)"
  # The tests an Application's python-tests job runs, from the Owned tree as from a repo root: check_gh_repo.py's own tests
  # among them, which need shfmt (checked above). Their Factory-only tests (the Template's folders, gotchas.md, the trigger test's guard) are tests/gh_repo/.
  (cd "$owned" && "${pytest_cmd[@]}" -q -p no:cacheprovider .github/scripts/tests) || fail "pytest"

  step "Hardening review script tests"
  # The mutation runner and the known-open list that .claude/skills/hardening-review/ ships; the tests stay here, out of the Template.
  (cd "$skill_dir/tests" && "${pytest_cmd[@]}" -q -p no:cacheprovider hardening) || fail "hardening pytest"

  step "Renderer oracle"
  # ReleasePost.cs's output, through tests/render_oracle/release_post.cs, rendered by cmark-gfm (GitHub) and kramdown (GitHub
  # Pages); kramdown runs from a local Ruby with its pinned gems, else Docker. The C# alone since the cut-over (ADR 0017's Updates
  # for mpaulosky/Application_Factory#164 and #128).
  need dotnet
  if python3 -c 'import cmarkgfm, pytest' &> /dev/null; then
    oracle_cmd=(python3 -m pytest)
  else
    need uvx
    oracle_cmd=(uvx --with cmarkgfm==2025.10.22 pytest)
  fi
  (cd "$skill_dir/tests/render_oracle" && "${oracle_cmd[@]}" -q -p no:cacheprovider .) || fail "renderer oracle"

  step "Parity suites (status.cs, land.cs, settings.cs, check_gh_repo.py, the release scripts, draft_release and post_claude_review as .py and .cs, detect_changes.cs, discover_tests.cs, load_test_env.cs, check_central_pins.cs, and rollout.sh through their command lines, against a fake gh) and their runners' tests"
  # The fake gh runs jq for a REST call's --jq, which the release scripts' suites make, and for a REST call's --input body, which the
  # settings suite's make. The status, land and settings suites' reference is the C# (github/status.cs, github/land.cs, github/settings.cs),
  # which the runner builds once before its cases (ADR 0017); the settings suite runs against github-settings.sh below too.
  need jq
  need dotnet
  # Every suite is run: each folder under tests/parity/ with a cases/ folder, so a suite added later can't be left out. Each needs its README.md
  # (what its cases cover and leave out), and there are at least the eight suites there are now, so a glob that matched nothing can't pass.
  parity_suites=()
  for suite_dir in "$skill_dir"/tests/parity/*/; do
    [[ -d "$suite_dir/cases" ]] || continue
    suite="$(basename -- "$suite_dir")"
    parity_suites+=("$suite")
    [[ -f "$suite_dir/README.md" ]] || fail "tests/parity/$suite has cases but no README.md saying what they cover and leave out"
  done
  ((${#parity_suites[@]} >= 8)) || fail "found ${#parity_suites[@]} parity suite(s) under tests/parity/, expected status, land, settings, check_gh_repo and the four release scripts' at least"
  # check_gh_repo's suite runs the checker with tests/parity/check_gh_repo/shfmt-shim.sh first on PATH as shfmt (its README says why); the
  # shim's own test runs first, so a broken shim fails as itself and not as a case.
  bash "$skill_dir/tests/shfmt-shim.test.sh" || fail "shfmt-shim.test.sh"
  shfmt_shim="$md_tmp/shfmt-shim"
  mkdir -p "$shfmt_shim" && ln -sf "$skill_dir/tests/parity/check_gh_repo/shfmt-shim.sh" "$shfmt_shim/shfmt"
  for suite in "${parity_suites[@]}"; do
    if [[ "$suite" == check_gh_repo ]]; then
      PATH="$shfmt_shim:$PATH" bash "$skill_dir/tests/parity.test.sh" "$suite" || fail "parity.test.sh $suite"
    else
      bash "$skill_dir/tests/parity.test.sh" "$suite" || fail "parity.test.sh $suite"
    fi
  done
  # An Owned script's C# ships beside its .py for one Rollout (ADR 0017), and the .py stays its suite's reference until then, so each Owned
  # entry file (a .cs in .github/scripts/ whose first line is a #: directive) is run against its suite too: built once from the Template's
  # folder, whose global.json picks the SDK, then run as ci.yml runs it, with dotnet run --no-build --file. An entry file with no suite fails.
  # Once its .py has retired the .cs is the suite's reference, which the loop above ran, so it isn't run again.
  # An entry file in library_first landed ahead of its cut-over (ADR 0017's library first): it only says the .py still runs, and exits 2,
  # so it isn't run against its suite; it must still do just that, and its .py must still be there. Its cut-over takes it off the list,
  # as mpaulosky/Application_Factory#128 took release_post off it.
  library_first=()
  owned_entries=0
  # A workflow builds an entry file in the Application's checkout and then reads git status (release.yml's docs job and
  # backfill-blog-posts.yml commit what it lists), so a build must write nothing under .github/: a file-based app's output is under
  # the temporary folder. Anything newer than this marker after the builds, a folder a build made a file in among them, fails.
  build_marker="$md_tmp/owned-cs-build.marker"
  touch "$build_marker"
  sleep 1
  for owned_cs in "$owned"/.github/scripts/*.cs; do
    [[ -f "$owned_cs" ]] && IFS= read -r first_line < "$owned_cs" && [[ "$first_line" == '#:'* ]] || continue
    owned_entries=$((owned_entries + 1))
    suite="$(basename -- "$owned_cs" .cs)"
    if [[ ! -d "$skill_dir/tests/parity/$suite/cases" ]]; then
      fail "template/owned/.github/scripts/$suite.cs is an Owned script with no parity suite (tests/parity/$suite/)"
      continue
    fi
    [[ -f "${owned_cs%.cs}.py" ]] || continue
    (cd "$owned" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
      UseSharedCompilation=false dotnet build "$owned_cs" < /dev/null > "$md_tmp/owned-cs-build.log" 2>&1) \
      || fail "dotnet build template/owned/.github/scripts/$suite.cs failed: $(tail -5 "$md_tmp/owned-cs-build.log")"
    if [[ " ${library_first[*]} " == *" $suite "* ]]; then
      library_rc=0
      library_err="$(cd "$owned" && dotnet run --no-build --file "$owned_cs" -- 2>&1 > /dev/null < /dev/null)" || library_rc=$?
      [[ $library_rc -eq 2 && "$library_err" == "$suite.cs: not cut over yet; "*"$suite.py"* ]] \
        || fail "$suite.cs is in library_first, so it must exit 2 saying $suite.py still runs; it exited $library_rc: $library_err"
      continue
    fi
    bash "$skill_dir/tests/parity.test.sh" "$suite" dotnet run --no-build --file "$owned_cs" -- || fail "parity.test.sh $suite against $suite.cs"
  done
  built_files="$(find "$owned/.github" -newer "$build_marker" -print)"
  [[ -z "$built_files" ]] || fail "building the Owned entry files wrote into the Template's .github/, which a workflow's git status would list: $built_files"
  # post_claude_review.cs is written for the checker layout the post job is to build it in (ADR 0017, mpaulosky/Application_Factory#129):
  # the file alone in an empty folder, built there and run as a dll, with no global.json or shield files near it. So it is built and run
  # that way too, against its suite; an #:include, #:package or #:project line, which the Template's folder could satisfy, fails first.
  checker_layout="$md_tmp/post-claude-review-checker"
  mkdir -p "$checker_layout" && cp -- "$owned/.github/scripts/post_claude_review.cs" "$checker_layout/"
  if grep -qE '^#:(include|package|project)' "$checker_layout/post_claude_review.cs"; then
    fail "post_claude_review.cs must include nothing and take no package or project (the checker layout): $(grep -E '^#:(include|package|project)' "$checker_layout/post_claude_review.cs")"
  elif (cd "$checker_layout" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 \
    DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false dotnet build post_claude_review.cs -o "$checker_layout/out" < /dev/null \
    > "$md_tmp/post-claude-review-checker.log" 2>&1); then
    bash "$skill_dir/tests/parity.test.sh" post_claude_review dotnet "$checker_layout/out/post_claude_review.dll" \
      || fail "parity.test.sh post_claude_review against post_claude_review.cs built alone in an empty folder"
  else
    fail "post_claude_review.cs doesn't build alone in an empty folder: $(tail -5 "$md_tmp/post-claude-review-checker.log")"
  fi
  for suite in "${library_first[@]}"; do
    [[ -f "$owned/.github/scripts/$suite.cs" && -f "$owned/.github/scripts/$suite.py" ]] \
      || fail "library_first names $suite, but the Template ships no $suite.cs beside a $suite.py: take it off the list at the cut-over"
  done
  ((owned_entries >= 9)) || fail "found $owned_entries Owned C# script(s) in template/owned/.github/scripts/, expected detect_changes.cs, discover_tests.cs, load_test_env.cs, check_central_pins.cs, draft_release.cs, release_post.cs, release_queue.cs, backfill_blog_posts.cs and post_claude_review.cs at least"
  # status.sh and land.sh run their C# from its own folder, so a global.json in the caller's folder that pins an SDK nobody has installed
  # changes nothing: the status suite passes against status.sh with one above every case's folder (the runner's TMPDIR), and land.sh
  # answers --help from such a folder. dotnet itself fails there, which shows the global.json is read.
  other_sdk="$md_tmp/other-sdk"
  mkdir -p "$other_sdk"
  printf '{"sdk": {"version": "8.0.1", "rollForward": "disable"}}\n' > "$other_sdk/global.json"
  ! (cd "$other_sdk" && dotnet --version) > /dev/null 2>&1 || fail "dotnet ran in a folder whose global.json pins SDK 8.0.1, so the wrappers' check below proves nothing"
  # The build output of a file-based app is under the temp folder, so with this TMPDIR status.sh builds status.cs again inside the
  # first case: it gets 300 seconds, not the runner's 60.
  TMPDIR="$other_sdk" PARITY_TIMEOUT=300 bash "$skill_dir/tests/parity.test.sh" status "$skill_dir/status.sh" > "$md_tmp/status-sh.log" 2>&1 \
    || fail "status.sh fails the status suite from under a global.json that pins another SDK: $(tail -5 "$md_tmp/status-sh.log")"
  land_help="$(cd "$other_sdk" && bash "$skill_dir/land.sh" --help 2>&1)" || fail "land.sh --help fails from a folder whose global.json pins another SDK: $land_help"
  [[ "$land_help" == "usage: land.cs "* ]] || fail "land.sh --help didn't print land's usage from a folder whose global.json pins another SDK: $land_help"
  # The settings suite against github-settings.sh, the C# port, run as status.sh is: from under that global.json, with a TMPDIR that makes
  # the runner's copy of github/ (the suite's github/ laid over it, so settings.cs is built afresh in the first case) a new path. The first
  # case passing shows the first build's CA2266 warning stays off stdout.
  TMPDIR="$other_sdk" PARITY_TIMEOUT=300 bash "$skill_dir/tests/parity.test.sh" settings "$skill_dir/github-settings.sh" > "$md_tmp/settings-sh.log" 2>&1 \
    || fail "github-settings.sh fails the settings suite from under a global.json that pins another SDK: $(tail -5 "$md_tmp/settings-sh.log")"
  settings_help="$(cd "$other_sdk" && bash "$skill_dir/github-settings.sh" --help 2>&1)" \
    || fail "github-settings.sh --help fails from a folder whose global.json pins another SDK: $settings_help"
  [[ "$settings_help" == "usage: settings.cs "* ]] || fail "github-settings.sh --help didn't print settings' usage: $settings_help"
  # rollout.sh builds github/rollout.cs from its own folder too, then runs the built dll from the caller's (ADR 0017).
  rollout_help="$(cd "$other_sdk" && bash "$skill_dir/rollout.sh" --help 2>&1)" || fail "rollout.sh --help fails from a folder whose global.json pins another SDK: $rollout_help"
  [[ "$rollout_help" == "usage: rollout.cs "* ]] || fail "rollout.sh --help didn't print rollout's usage from a folder whose global.json pins another SDK: $rollout_help"
  # rollout.sh runs the dll its build names in its own place, with its arguments, and passes on the signals its caller ignores (ROLLOUT_SIGIGN,
  # SigIgn of /proc/$$/status), since the runtime takes SIGTERM over before the round's first line: checked with a stand-in dotnet that
  # prints what it was asked to run and that variable. A build that fails shows its output on stderr and exits 1, and so does one that
  # names no dll; neither runs anything.
  if [[ -r /proc/self/status ]]; then
    fake_dotnet="$md_tmp/fake-dotnet"
    mkdir -p "$fake_dotnet"
    : > "$fake_dotnet/rollout.dll"
    # The stand-in's own $1, $* and variables, written for it to read.
    # shellcheck disable=SC2016
    printf '%s\n' '#!/usr/bin/env bash' 'if [[ "$1" == build ]]; then' \
      '  [[ -z "${FAKE_BUILD_FAILS:-}" ]] || { echo "error CS0103: broken" >&2; exit 1; }' \
      "  echo \"\${FAKE_DLL:-$fake_dotnet/rollout.dll}\"" '  exit 0' 'fi' 'printf "%s\n" "$*" "$ROLLOUT_SIGIGN"' > "$fake_dotnet/dotnet"
    chmod +x "$fake_dotnet/dotnet"
    said="$(PATH="$fake_dotnet:$PATH" bash -c 'trap "" TERM; exec bash "$1" --brings "#1" --root "a b"' _ "$skill_dir/rollout.sh" 2>&1)" \
      || fail "rollout.sh with a stand-in dotnet failed: $said"
    [[ "$(head -1 <<< "$said")" == "$fake_dotnet/rollout.dll --brings #1 --root a b" ]] || fail "rollout.sh didn't run the dll its build named, with its arguments: $said"
    mask="$(sed -n 2p <<< "$said")"
    if [[ ! "$mask" =~ ^[0-9a-f]{16}$ ]] || ((! (0x$mask & 0x4000))); then
      fail "rollout.sh didn't pass on its caller's ignored SIGTERM in ROLLOUT_SIGIGN: $said"
    fi
    code=0
    said="$(FAKE_BUILD_FAILS=1 PATH="$fake_dotnet:$PATH" bash "$skill_dir/rollout.sh" --brings '#1' 2>&1)" || code=$?
    [[ $code -eq 1 && "$said" == *"error CS0103: broken"* && "$said" == *"rollout.sh: dotnet build github/rollout.cs failed (above); nothing run"* ]] \
      || fail "rollout.sh with a build that fails exited $code, not 1 with the build's output: $said"
    code=0
    said="$(FAKE_DLL="$md_tmp/none.dll" PATH="$fake_dotnet:$PATH" bash "$skill_dir/rollout.sh" --brings '#1' 2>&1)" || code=$?
    [[ $code -eq 1 && "$said" == "rollout.sh: dotnet build github/rollout.cs named no dll it built"* ]] \
      || fail "rollout.sh with a build that named no dll exited $code, not 1 saying so: $said"
  fi
  # Runs started at once right after a change share the first build (mpaulosky/Application_Factory#178): each wrapper builds under a lock on
  # github/ first, so six runs of a fresh copy (a new path, so a new build) all pass, with only the usage on stdout. Without the lock about one
  # run in three of three failed, the file in use by another's build. The lock is flock(1); macOS has none, and there the wrappers build as before.
  if [[ "$(uname -s)" == Linux ]]; then
    need flock
    at_once="$md_tmp/at-once"
    mkdir -p "$at_once/skill"
    cp -R "$skill_dir/github" "$at_once/skill/github"
    cp "$skill_dir/status.sh" "$skill_dir/land.sh" "$skill_dir/github-settings.sh" "$skill_dir/rollout.sh" "$at_once/skill/"
    cp "$skill_dir/../../../global.json" "$at_once/global.json"
    for wrapper in status:status land:land github-settings:settings rollout:rollout; do
      script="${wrapper#*:}" wrapper="${wrapper%%:*}" pids=()
      for run in 1 2 3 4 5 6; do
        bash "$at_once/skill/$wrapper.sh" --help > "$at_once/$wrapper-$run.out" 2> "$at_once/$wrapper-$run.err" &
        pids+=($!)
      done
      for run in 1 2 3 4 5 6; do
        code=0
        wait "${pids[run - 1]}" || code=$?
        [[ $code -eq 0 && "$(head -1 "$at_once/$wrapper-$run.out")" == "usage: $script.cs "* ]] \
          || fail "$wrapper.sh run $run of six started at once on a fresh build exited $code: $(head -c 600 "$at_once/$wrapper-$run.out" "$at_once/$wrapper-$run.err")"
      done
    done
  fi
  # With no dotnet on PATH (only bash and dirname, which the wrappers need first), each wrapper exits 127 and says so.
  no_dotnet="$md_tmp/no-dotnet"
  mkdir -p "$no_dotnet"
  ln -sf "$(command -v bash)" "$no_dotnet/bash"
  ln -sf "$(command -v dirname)" "$no_dotnet/dirname"
  for wrapper in status:status land:land github-settings:settings rollout:rollout; do
    script="${wrapper#*:}" wrapper="${wrapper%%:*}" code=0
    said="$(PATH="$no_dotnet" "$no_dotnet/bash" "$skill_dir/$wrapper.sh" --help 2>&1)" || code=$?
    [[ $code -eq 127 && "$said" == "$wrapper.sh: dotnet isn't on PATH; github/$script.cs runs on the .NET SDK"* ]] \
      || fail "$wrapper.sh with no dotnet on PATH exited $code, not 127 with its message: $said"
  done
  bash "$skill_dir/tests/parity-runner.test.sh" || fail "parity-runner.test.sh"
  # Run again as a hook would, with GIT_DIR and the rest naming another repo: its throwaway repo must not touch that one.
  # The decoy sits under md_tmp, which the EXIT trap removes, and its own git calls run without test.sh's caller's GIT_*.
  decoy="$md_tmp/parity-decoy"
  decoy_git=(env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_PREFIX -u GIT_COMMON_DIR git)
  "${decoy_git[@]}" init -q "$decoy"
  [[ -d "$decoy/.git" ]] || fail "test.sh's decoy repo for the parity runner test wasn't made at $decoy (a caller's GIT_DIR reached git init?)"
  GIT_DIR="$decoy/.git" GIT_WORK_TREE="$decoy" GIT_INDEX_FILE="$decoy/.git/index" \
    bash "$skill_dir/tests/parity-runner.test.sh" > /dev/null || fail "parity-runner.test.sh with a hook's GIT_DIR set"
  [[ -z "$("${decoy_git[@]}" -C "$decoy" ls-files)" && ! -e "$decoy/cases" ]] || fail "parity-runner.test.sh staged files in the caller's repository"
  # The rollout suite has a runner of its own (a temporary copy of the skill folder with stub reapply.sh and land.sh, a stand-in Template,
  # a HOME per case and a PATH of fakes): its plain-mode and --auto cases against rollout.sh, then the runner's own tests.
  [[ -f "$skill_dir/tests/rollout-parity/README.md" ]] || fail "tests/rollout-parity has no README.md saying what its cases cover and leave out"
  bash "$skill_dir/tests/rollout-parity.test.sh" || fail "rollout-parity.test.sh"
  bash "$skill_dir/tests/rollout-parity-runner.test.sh" || fail "rollout-parity-runner.test.sh"

  step "Claude Review benchmark tests (its --rescore parity suite; its scoring, arguments, config swap and the workflow it reads are in tests/Scripts.Tests/Bench)"
  # The bench is a C# file-based app (ADR 0017, Update for mpaulosky/Application_Factory#161): the suite builds it once and holds what --rescore prints and
  # writes back to what the Python did. Its one package, YamlDotNet, restores from nuget.org alone (its own NuGet.config), against its lock file.
  need dotnet
  need git
  bash "$skill_dir/bench/tests/rescore.test.sh" || fail "bench/tests/rescore.test.sh"
  # The suite can fail: against an implementation that prints and writes nothing it must, and it never records from one under test.
  printf '#!/bin/sh\nexit 0\n' > "$md_tmp/rescore-silent"
  chmod +x "$md_tmp/rescore-silent"
  code=0
  bash "$skill_dir/bench/tests/rescore.test.sh" "$md_tmp/rescore-silent" > "$md_tmp/rescore-silent.out" 2>&1 || code=$?
  if [[ $code -ne 1 ]] || ! grep -q '^FAIL - it prints what the Python printed' "$md_tmp/rescore-silent.out"; then
    fail "rescore.test.sh against a bench that does nothing exited $code, not 1 with its stdout check failed: $(tail -3 "$md_tmp/rescore-silent.out")"
  fi
  code=0
  RESCORE_RECORD=1 bash "$skill_dir/bench/tests/rescore.test.sh" "$md_tmp/rescore-silent" > /dev/null 2>&1 || code=$?
  [[ $code -eq 2 ]] || fail "rescore.test.sh with RESCORE_RECORD and a command exited $code, not 2"
  # Again as a hook would run it, with GIT_DIR naming a decoy repo: the bench's git show must read the fixture clone, not the decoy.
  rescore_decoy="$md_tmp/rescore-decoy"
  "${decoy_git[@]}" init -q "$rescore_decoy"
  GIT_DIR="$rescore_decoy/.git" GIT_WORK_TREE="$rescore_decoy" GIT_INDEX_FILE="$rescore_decoy/.git/index" \
    bash "$skill_dir/bench/tests/rescore.test.sh" > /dev/null || fail "rescore.test.sh with a hook's GIT_DIR set"
  [[ -z "$("${decoy_git[@]}" -C "$rescore_decoy" ls-files)" ]] || fail "rescore.test.sh staged files in the caller's repository"
  # On a copy of bench/ under a folder whose NuGet.config adds another feed, which the bench's own must clear.
  mkdir -p "$md_tmp/bench-sources"
  printf '<configuration><packageSources><add key="other" value="https://other.invalid/v3/index.json" /></packageSources></configuration>\n' > "$md_tmp/bench-sources/NuGet.config"
  cp -R "$skill_dir/bench" "$md_tmp/bench-sources/bench" || fail "can't copy bench/ under $md_tmp/bench-sources"
  bench_sources="$(cd "$md_tmp/bench-sources/bench" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 dotnet nuget list source --format short)" \
    || fail "dotnet nuget list source failed in the copy of bench/"
  [[ "$bench_sources" == "E https://api.nuget.org/v3/index.json" ]] \
    || fail "the bench must restore from nuget.org alone, whatever a NuGet.config above says; it sees: ${bench_sources//$'\n'/ | }"
  audit="$(audit_sources_nuget_only "$skill_dir/bench/NuGet.config")" \
    || fail "bench/NuGet.config must clear its auditSources and add nuget.org alone (found: ${audit:-none})"
  # A feed that can't be reached (NU1900, and NU1905 since its audit source, nuget.org alone, gave no data) leaves the bench building as the gate builds it,
  # with scripts/gate.sh's properties, once the package is in the cache (the restore of tests/Scripts.Tests below and the gate's build put it there).
  bench_offline="$md_tmp/bench-offline"
  mkdir -p "$bench_offline"
  if ! cp "$skill_dir/../../../global.json" "$bench_offline/" || ! cp -R "$skill_dir/bench" "$skill_dir/github" "$bench_offline/"; then
    fail "can't copy bench/ and github/ under $bench_offline"
  fi
  sed -i.bak 's#https://api.nuget.org/v3/index.json#https://nuget.invalid/v3/index.json#g' "$bench_offline/bench/NuGet.config"
  ! cmp -s "$bench_offline/bench/NuGet.config" "$bench_offline/bench/NuGet.config.bak" \
    || fail "the bench's offline check couldn't point its copy at an unreachable feed"
  rm -f "$bench_offline/bench/NuGet.config.bak"
  bench_rc=0
  (cd "$bench_offline/bench" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 \
    dotnet build claude-review-bench.cs --no-incremental --disable-build-servers --artifacts-path "$bench_offline/out" \
    -p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=CA2266%3BNU1900%3BNU1905) > "$bench_offline/build.log" 2>&1 || bench_rc=$?
  if ((bench_rc != 0)) || ! grep -q 'warning NU1900' "$bench_offline/build.log" || ! grep -q 'warning NU1905' "$bench_offline/build.log"; then
    fail "the bench must build as the gate builds it, with NU1900 and NU1905 warnings, when its feed can't be reached (exit $bench_rc): $(grep -m 3 -E 'error|NU1' "$bench_offline/build.log" | tr '\n' ' ')"
  fi

  step "C# script tests (tests/Scripts.Tests, xUnit v3)"
  # The white-box tests of the C# scripts (ADR 0017), on Microsoft Testing Platform (the Factory's global.json). The build output and results go to md_tmp,
  # so the run leaves no file in the tree. Exit 8 is "no test ran", accepted only while the project holds no .cs file, so a suite that loses its tests fails.
  need dotnet
  need jq
  scripts_tests="$skill_dir/tests/Scripts.Tests"
  dotnet_rc=0
  scripts_tests_run "$scripts_tests" "$md_tmp/scripts-tests" || dotnet_rc=$?
  scripts_tests_pass "$dotnet_rc" "$scripts_tests" || fail "Scripts.Tests (dotnet test exit $dotnet_rc; 8 is accepted only while the project holds no .cs source)"
  # The rule itself, on fixtures: build output is no source, a source anywhere else is, and a missing folder passes nothing.
  st="$md_tmp/scripts-tests-rule"
  mkdir -p "$st/empty" "$st/built/obj/Debug" "$st/built/bin" "$st/built/TestResults" "$st/source/sub" "$st/root-source"
  : > "$st/built/obj/Debug/Scripts.Tests.AssemblyInfo.cs"
  : > "$st/built/bin/x.cs"
  : > "$st/built/TestResults/x.cs"
  : > "$st/source/sub/StatusTests.cs"
  : > "$st/root-source/Tests.cs"
  for ok in "0 $st/empty" "8 $st/empty" "8 $st/built" "0 $st/source"; do
    scripts_tests_pass "${ok%% *}" "${ok#* }" || fail "scripts_tests_pass refuses exit ${ok%% *} for ${ok#* }"
  done
  for bad in "8 $st/source" "8 $st/root-source" "1 $st/empty" "9 $st/empty" "2 $st/built" "8 $st/missing"; do
    ! scripts_tests_pass "${bad%% *}" "${bad#* }" || fail "scripts_tests_pass accepts exit ${bad%% *} for ${bad#* }"
  done
  # The run's paths and settings, with a stub dotnet: a relative output folder and a relative TMPDIR reach dotnet absolute, naming the folders they named
  # from where test.sh stood, not folders inside the project, and the run sets no build server, no node reuse and no first-run setup whatever the caller set.
  mkdir -p "$st/stub" "$st/proj" "$st/cwd/rel-tmp"
  # shellcheck disable=SC2016 # the stub's own $PWD, $TMPDIR and $@, expanded when it runs
  printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$PWD" "$TMPDIR" "$@" "MSBUILDDISABLENODEREUSE=$MSBUILDDISABLENODEREUSE" "DOTNET_CLI_USE_MSBUILD_SERVER=$DOTNET_CLI_USE_MSBUILD_SERVER" "DOTNET_CLI_TELEMETRY_OPTOUT=$DOTNET_CLI_TELEMETRY_OPTOUT" "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=$DOTNET_SKIP_FIRST_TIME_EXPERIENCE" "DOTNET_NOLOGO=$DOTNET_NOLOGO" > "%s"\n' "$st/stub.log" > "$st/stub/dotnet"
  chmod +x "$st/stub/dotnet"
  # The caller's values are the opposite ones, so the check shows the function sets its own: no build server or node reuse is left running.
  (cd "$st/cwd" && PATH="$st/stub:$PATH" TMPDIR=rel-tmp MSBUILDDISABLENODEREUSE=0 DOTNET_CLI_USE_MSBUILD_SERVER=1 DOTNET_CLI_TELEMETRY_OPTOUT=0 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=0 DOTNET_NOLOGO=0 scripts_tests_run "$st/proj" rel-out) || fail "scripts_tests_run fails with a stub dotnet"
  stub_log="$(cat "$st/stub.log" 2> /dev/null)"
  for want in "$st/proj" "$st/cwd/rel-tmp" "$st/cwd/rel-out" "$st/cwd/rel-out/results" "-p:RestoreLockedMode=true" "-p:UseSharedCompilation=false" \
    MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1; do
    grep -qxF -- "$want" <<< "$stub_log" || fail "scripts_tests_run doesn't pass dotnet $want (it got: ${stub_log//$'\n'/ })"
  done
  # test.sh's own temp folders, under a relative TMPDIR and a CDPATH that holds ., are removed by its EXIT trap: an unknown shard ends the run just
  # after they are made, so nothing may be left under that TMPDIR. The run must end at the shard check (exit 2 and its message), which sits below the
  # two mktemp lines and the trap, so an earlier failure that never made the folders can't pass this.
  cdp="$md_tmp/cdpath"
  mkdir -p "$cdp/rel"
  cdp_code=0
  (cd "$cdp" && TMPDIR=rel CDPATH=. TEST_SH_SHARD=no-such-shard bash "$skill_dir/test.sh" > "$cdp/out" 2>&1) || cdp_code=$?
  if [[ "$cdp_code" != 2 ]] || ! grep -qF "TEST_SH_SHARD must be one of" "$cdp/out"; then
    fail "test.sh under a relative TMPDIR with CDPATH=. didn't end at its shard check (exit $cdp_code): $(cat "$cdp/out")"
  fi
  [[ -z "$(ls -A "$cdp/rel")" ]] || fail "test.sh left its temp folders under a relative TMPDIR with CDPATH=.: $(ls -A "$cdp/rel")"
  # Run by a relative path under CDPATH=., as CLAUDE.md's commands run it from the repo root, it still finds its own folder and reaches the shard check.
  # Its TMPDIR is absolute, so a relative one this run was started with can't name a folder under the repo root instead.
  cdp_code=0
  (cd "$skill_dir/../../.." && TMPDIR="$md_tmp" CDPATH=. TEST_SH_SHARD=no-such-shard bash .claude/skills/repo-ci-baseline/test.sh > "$cdp/out-rel" 2>&1) || cdp_code=$?
  if [[ "$cdp_code" != 2 ]] || ! grep -qF "TEST_SH_SHARD must be one of" "$cdp/out-rel"; then
    fail "test.sh run by a relative path with CDPATH=. didn't reach its shard check (exit $cdp_code): $(cat "$cdp/out-rel")"
  fi
  # A skill_dir that holds no test.sh stops the run with exit 2 before the shard check: run from a process substitution, test.sh's BASH_SOURCE is
  # /dev/fd/N, whose folder holds no test.sh.
  guard_code=0
  (cd "$md_tmp" && TMPDIR="$md_tmp" TEST_SH_SHARD=no-such-shard bash <(cat "$skill_dir/test.sh") > "$cdp/out-guard" 2>&1) || guard_code=$?
  if [[ "$guard_code" != 2 ]] || ! grep -qF "can't find its own folder" "$cdp/out-guard" || grep -qF "TEST_SH_SHARD must be one of" "$cdp/out-guard"; then
    fail "test.sh whose own folder holds no test.sh didn't stop before its shard check (exit $guard_code): $(cat "$cdp/out-guard")"
  fi
  # A second mktemp that fails ends the run before the shard check, and the first folder is still removed: a stub mktemp makes the first folder (and
  # notes it) and fails the second time. The run must have made that folder and stopped before the shard check, so it can't pass by making none.
  half="$md_tmp/half-mktemp"
  mkdir -p "$half/bin" "$half/tmp"
  real_mktemp="$(command -v mktemp)"
  cat > "$half/bin/mktemp" << HALF
#!/usr/bin/env bash
if [[ -e "$half/made" ]]; then exit 1; fi
dir="\$("$real_mktemp" "\$@")" || exit 1
printf '%s\n' "\$dir" > "$half/made"
printf '%s\n' "\$dir"
HALF
  chmod +x "$half/bin/mktemp"
  half_code=0
  TMPDIR="$half/tmp" PATH="$half/bin:$PATH" TEST_SH_SHARD=no-such-shard bash "$skill_dir/test.sh" > "$half/out" 2>&1 || half_code=$?
  half_made="$(cat "$half/made" 2> /dev/null || true)"
  if [[ -z "$half_made" || "$half_code" == 0 || "$half_code" == 2 ]] || grep -qF "TEST_SH_SHARD must be one of" "$half/out"; then
    fail "test.sh with a second mktemp that fails didn't stop at it (exit $half_code, first folder: ${half_made:-none}): $(cat "$half/out")"
  fi
  [[ -z "$(ls -A "$half/tmp")" ]] || fail "test.sh left its first temp folder when the second mktemp failed: $(ls -A "$half/tmp")"
  # The audit reads advisories from nuget.org alone too: a NuGet.config above the folder can't point it at a feed that reports none.
  audit="$(audit_sources_nuget_only "$scripts_tests/NuGet.config")" \
    || fail "tests/Scripts.Tests/NuGet.config must clear its auditSources and add nuget.org alone (found: ${audit:-none})"
  # The check on configs it must pass (spread over lines, with an attribute after the URL) and ones it must refuse.
  nuget_org='<add key="nuget.org" value="https://api.nuget.org/v3/index.json"'
  printf '<configuration>\n  <auditSources>\n    <clear />\n    %s protocolVersion="3" />\n  </auditSources>\n</configuration>\n' "$nuget_org" > "$md_tmp/audit.config"
  audit_sources_nuget_only "$md_tmp/audit.config" > /dev/null || fail "the auditSources check refuses a config that clears them and adds nuget.org alone"
  printf '<configuration>\n\t<auditSources>\n\t\t<clear />\n\t\t<add key="nuget.org"\n\t\t\tvalue="https://api.nuget.org/v3/index.json" />\n\t</auditSources>\n</configuration>\n' \
    > "$md_tmp/audit.config"
  audit_sources_nuget_only "$md_tmp/audit.config" > /dev/null || fail "the auditSources check refuses a tab-indented config with the add's attributes on two lines"
  # Comments inside the element, one with hyphens in it and one hiding a second feed, are dropped, so the element still passes.
  printf '<configuration><auditSources><clear /><!-- nuget-only: audit -->%s /><!-- <add key="other" value="https://other.example/v3/index.json" /> --></auditSources></configuration>\n' \
    "$nuget_org" > "$md_tmp/audit.config"
  audit_sources_nuget_only "$md_tmp/audit.config" > /dev/null || fail "the auditSources check refuses a config with comments, one hyphenated, inside the element"
  for wrong in \
    "<!-- <auditSources><clear />$nuget_org /></auditSources> -->" \
    "<auditSources><clear />$nuget_org /><add key=\"other\" value=\"https://other.example/v3/index.json\" /></auditSources>" \
    "<auditSources>$nuget_org /></auditSources>" \
    '<auditSources><clear /><add key="nuget.org" value="https://other.example/v3/index.json" /></auditSources>' \
    '<auditSources><clear /></auditSources>' \
    '<packageSources><clear /></packageSources>'; do
    printf '<configuration>%s</configuration>\n' "$wrong" > "$md_tmp/audit.config"
    ! audit_sources_nuget_only "$md_tmp/audit.config" > /dev/null || fail "the auditSources check passes: $wrong"
  done
  ! audit_sources_nuget_only "$md_tmp/no-such.config" > /dev/null || fail "the auditSources check passes a NuGet.config that isn't there"
  # It targets the runtime of the SDK global.json names, so it runs where only that SDK is installed (a net10.0 build wouldn't start on runtime 11 alone).
  # A global.json that isn't readable JSON naming sdk.version fails here with a message and leaves the major empty, which the check below refuses.
  sdk_major="$(sdk_major_of "$skill_dir/../../../global.json")" || fail "global.json must be readable JSON whose sdk.version names a major version"
  printf '{"sdk": {"version": "11.0.100"}}\n' > "$md_tmp/global.json"
  [[ "$(sdk_major_of "$md_tmp/global.json")" == 11 ]] || fail "the SDK major of a global.json naming 11.0.100 isn't 11"
  for wrong in '{"sdk": {"version": "11.0.100"' '{"sdk": {}}' '{"sdk": {"version": "latest"}}'; do
    printf '%s\n' "$wrong" > "$md_tmp/global.json"
    ! sdk_major_of "$md_tmp/global.json" > /dev/null || fail "the SDK major check passes the global.json: $wrong"
  done
  ! sdk_major_of "$md_tmp/no-such-global.json" > /dev/null || fail "the SDK major check passes a global.json that isn't there"
  tfms="$(targets_net_major "$scripts_tests/Scripts.Tests.csproj" "$sdk_major")" \
    || fail "tests/Scripts.Tests must target net$sdk_major.0, the runtime of the SDK global.json names (it targets: ${tfms:-nothing})"
  printf '<Project>\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n' > "$md_tmp/tfm.csproj"
  targets_net_major "$md_tmp/tfm.csproj" 11 > /dev/null || fail "the target framework check refuses a project that targets net11.0 for SDK 11"
  ! targets_net_major "$md_tmp/tfm.csproj" "" > /dev/null || fail "the target framework check passes when global.json names no SDK"
  for wrong in '<TargetFramework>net10.0</TargetFramework>' '<TargetFrameworks>net11.0;net10.0</TargetFrameworks>' \
    $'<TargetFramework>net11.0</TargetFramework>\n<TargetFramework>net10.0</TargetFramework>' '<OutputType>Exe</OutputType>'; do
    printf '<Project>\n<PropertyGroup>\n%s\n</PropertyGroup>\n</Project>\n' "$wrong" > "$md_tmp/tfm.csproj"
    ! targets_net_major "$md_tmp/tfm.csproj" 11 > /dev/null || fail "the target framework check passes for SDK 11: $wrong"
  done
  # The shields (ADR 0017): an empty Directory.Build.props, Directory.Build.targets and Directory.Packages.props in github/, in tests/ (the two checkers), in
  # bench/, in the test project and beside the Template's C# scripts, so the files of those names above them never reach their builds.
  for shielded in github tests bench tests/Scripts.Tests template/owned/.github/scripts; do
    for shield in Directory.Build.props Directory.Build.targets Directory.Packages.props; do
      is_empty_shield "$skill_dir/$shielded/$shield" || fail "$shielded/$shield must be the empty shield, <Project />"
    done
  done
  printf '<Project />\n' > "$md_tmp/shield.props"
  is_empty_shield "$md_tmp/shield.props" || fail "the shield check refuses <Project />"
  for wrong in '<Project></Project>' '<Project><Import Project="../x.props" /></Project>' ''; do
    printf '%s\n' "$wrong" > "$md_tmp/shield.props"
    ! is_empty_shield "$md_tmp/shield.props" || fail "the shield check passes: ${wrong:-an empty file}"
  done
  ! is_empty_shield "$md_tmp/no-such.props" || fail "the shield check passes a shield that isn't there"
  # The restore is pinned, on a copy of the project under a folder whose NuGet.config adds another feed: the project's own NuGet.config leaves
  # nuget.org alone, and a version moved in the project file but not in packages.lock.json fails the run with NU1004.
  [[ -s "$scripts_tests/packages.lock.json" ]] || fail "tests/Scripts.Tests/packages.lock.json is missing: run dotnet restore in that folder and commit it"
  pinned="$md_tmp/scripts-tests-pinned"
  mkdir -p "$pinned"
  cp "$skill_dir/../../../global.json" "$pinned/"
  printf '<configuration><packageSources><add key="other" value="https://other.invalid/v3/index.json" /></packageSources></configuration>\n' > "$pinned/NuGet.config"
  pinned_project="$(scripts_tests_copy "$pinned")" || fail "can't copy the skill under $pinned for the locked-restore check"
  sources="$(cd "$pinned_project" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 dotnet nuget list source --format short)" \
    || fail "dotnet nuget list source failed in the copy of Scripts.Tests"
  [[ "$sources" == "E https://api.nuget.org/v3/index.json" ]] \
    || fail "Scripts.Tests must restore from nuget.org alone, whatever a NuGet.config above says; it sees: ${sources//$'\n'/ | }"
  sed -i.bak -E 's/(PackageReference Include="xunit\.v3\.mtp-v2" Version=")[^"]*"/\10.0.1"/' "$pinned_project/Scripts.Tests.csproj"
  ! cmp -s "$pinned_project/Scripts.Tests.csproj" "$pinned_project/Scripts.Tests.csproj.bak" \
    || fail "the locked-restore check couldn't move xunit.v3.mtp-v2's version in its copy of Scripts.Tests.csproj"
  rm -f "$pinned_project/Scripts.Tests.csproj.bak"
  locked_rc=0
  scripts_tests_run "$pinned_project" "$pinned/out" > "$pinned/run.log" 2>&1 || locked_rc=$?
  if ((locked_rc == 0 || locked_rc == 8)) || ! grep -q NU1004 "$pinned/run.log"; then
    fail "a Scripts.Tests whose package version packages.lock.json doesn't pin must fail its locked restore with NU1004 (exit $locked_rc)"
  fi
  # A vulnerability feed that can't be reached (NU1900, and NU1905 since the audit source, nuget.org alone, gave no data) is a warning, not an error: on a
  # copy whose nuget.org can't be reached, with the packages already in the cache from the run above, the run still passes and says why. NU1901 to NU1904, a vulnerable package, stay errors (ADR 0017). The copy sits under a
  # Directory.Build.props, Directory.Build.targets and Directory.Packages.props that would each fail its build, which the shields keep out.
  offline="$md_tmp/scripts-tests-offline"
  mkdir -p "$offline"
  cp "$skill_dir/../../../global.json" "$offline/"
  printf '<Project><Target Name="ParentProps" BeforeTargets="Build"><Error Text="a Directory.Build.props above Scripts.Tests reached its build" /></Target></Project>\n' \
    > "$offline/Directory.Build.props"
  printf '<Project><Target Name="ParentTargets" BeforeTargets="Build"><Error Text="a Directory.Build.targets above Scripts.Tests reached its build" /></Target></Project>\n' \
    > "$offline/Directory.Build.targets"
  printf '<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>\n' > "$offline/Directory.Packages.props"
  offline_project="$(scripts_tests_copy "$offline")" || fail "can't copy the skill under $offline for the NU1900 check"
  sed -i.bak 's#https://api.nuget.org/v3/index.json#https://nuget.invalid/v3/index.json#' "$offline_project/NuGet.config"
  ! cmp -s "$offline_project/NuGet.config" "$offline_project/NuGet.config.bak" \
    || fail "the NU1900 check couldn't point its copy of Scripts.Tests at an unreachable feed"
  rm -f "$offline_project/NuGet.config.bak"
  offline_rc=0
  scripts_tests_run "$offline_project" "$offline/out" > "$offline/run.log" 2>&1 || offline_rc=$?
  if ! scripts_tests_pass "$offline_rc" "$offline_project" || ! grep -q 'warning NU1900' "$offline/run.log" \
    || ! grep -q 'warning NU1905' "$offline/run.log"; then
    fail "Scripts.Tests must pass with NU1900 and NU1905 warnings when its feed can't be reached, its shields keeping out the files above it (exit $offline_rc): $(grep -m 3 -E 'error|NU1' "$offline/run.log" | tr '\n' ' ')"
  fi
fi # the scripts shard

if in_shard checks; then
  step "actionlint"
  mapfile -t workflows < <(find "$owned/.github/workflows" -name '*.yml' | sort)
  actionlint "${workflows[@]}" || fail "actionlint"

  step "zizmor"
  zizmor --offline --min-severity medium "$owned/.github" || fail "zizmor"

  step "yamllint"
  mapfile -t yaml_files < <(find "$owned" "$seed" -name '*.yml' -o -name '*.yaml' | sort)
  yamllint -c "$owned/.yamllint.yml" "${yaml_files[@]}" || fail "yamllint"

  step "shellcheck"
  shell_files=(
    "$skill_dir/apply.sh" "$skill_dir/reapply.sh" "$skill_dir/test.sh" "$skill_dir/github-settings.sh" "$skill_dir/status.sh"
    "$skill_dir/land.sh" "$skill_dir/rollout.sh"
    "$skill_dir/tests/apply.test.sh" "$skill_dir/tests/reapply.test.sh"
    "$skill_dir/tests/seed-check.test.sh" "$skill_dir/tests/seed-dependabot.test.sh"
    "$skill_dir/tests/seed-dockerfile.test.sh" "$skill_dir/tests/doc-single-source.test.sh" "$skill_dir/tests/no-dotfiles-dependency.test.sh"
    "$skill_dir/tests/owned-gate-files.test.sh"
    "$skill_dir/tests/sandcastle-check.test.sh" "$skill_dir/tests/repos-scope.test.sh"
    "$skill_dir/tests/parity.test.sh" "$skill_dir/tests/parity/fake-gh.sh" "$skill_dir/tests/parity/stderr.sh" "$skill_dir/tests/parity-runner.test.sh"
    "$skill_dir/tests/rollout-parity.test.sh" "$skill_dir/tests/rollout-parity/fake-tool.sh" "$skill_dir/tests/rollout-parity-runner.test.sh"
    "$skill_dir"/tests/rollout-parity/defaults/fakes/*.sh
    "$skill_dir/tests/parity/check_gh_repo/shfmt-shim.sh" "$skill_dir/tests/shfmt-shim.test.sh"
    "$skill_dir/tests/check-git-status.sh" "$skill_dir/tests/check-git-status.test.sh" "$skill_dir/tests/check-issue-refs.sh" "$skill_dir/tests/check-issue-refs.test.sh"
    "$skill_dir/tests/mutations/check-spec.test.sh"
    "$owned/scripts/gate.sh" "$owned/scripts/check-branch-name.sh" "$owned/scripts/tests/check-branch-name.test.sh"
    "$owned/scripts/check-pr-title.sh" "$owned/scripts/tests/check-pr-title.test.sh"
    "$owned/scripts/sync-copilot-review.sh" "$owned/scripts/tests/sync-copilot-review.test.sh"
    "$owned/scripts/watch-pr.sh" "$owned/scripts/tests/watch-pr.test.sh"
    "$owned/scripts/cleanup.sh" "$owned/scripts/tests/cleanup.test.sh" "$owned/scripts/work-issue.sh" "$owned/scripts/tests/work-issue.test.sh"
    "$owned/scripts/check-git-hooks.sh" "$owned/scripts/tests/check-git-hooks.test.sh" "$owned/scripts/review.sh"
    "$owned/scripts/lint-edited-file.sh" "$owned/scripts/tests/lint-edited-file.test.sh"
    "$seed/.github/ci/prepare.sh" "$seed/.github/ci/gate-checks.sh" "$owned/.sandcastle/check.sh"
    "$owned/.github/hooks/pre-commit" "$owned/.github/hooks/pre-push" "$owned/.github/hooks/post-checkout"
    "$owned/.github/hooks/tests/pre-commit.test.sh" "$owned/.github/hooks/tests/pre-push.test.sh"
  )
  shellcheck "${shell_files[@]}" || fail "shellcheck"

  step "markdownlint"
  # The skill's and the Template's Markdown, linted with the config the Template
  # Applies. --config needs a file name ending in .markdownlint-cli2.jsonc.
  command cp -f "$owned/.markdownlint-cli2.jsonc" "$md_tmp/template.markdownlint-cli2.jsonc"
  (cd "$skill_dir" && markdownlint-cli2 --config "$md_tmp/template.markdownlint-cli2.jsonc" \
    '*.md' 'references/*.md' 'docs/**/*.md' 'template/**/*.md' 'template/**/.*/**/*.md' '!template/seed/.sandcastle/**') || fail "markdownlint"
  # The Seed Sandcastle Markdown, as an Applied repo lints it: beside the Owned
  # .sandcastle/.markdownlint-cli2.jsonc, which markdownlint-cli2 merges with --config.
  mkdir -p "$md_tmp/seed-sandcastle/.sandcastle"
  (cd "$seed/.sandcastle" && find . -name '*.md' -exec cp --parents {} "$md_tmp/seed-sandcastle/.sandcastle/" \;)
  command cp -f "$owned/.sandcastle/.markdownlint-cli2.jsonc" "$md_tmp/seed-sandcastle/.sandcastle/"
  (cd "$md_tmp/seed-sandcastle" && markdownlint-cli2 --config "$md_tmp/template.markdownlint-cli2.jsonc" '.sandcastle/**/*.md') \
    || fail "markdownlint (Seed .sandcastle/)"

  step "No placeholders in Owned files"
  # Under .sandcastle/ only Apply's own three: Sandcastle fills its prompts'
  # {{NAME}}s ({{SHARED_RULES}}, {{ISSUES_JSON}} and the rest) when it runs them.
  if grep -rnE '\{\{[A-Z_]+\}\}' --exclude-dir=.sandcastle "$owned" \
    || grep -rnE '\{\{(OWNER|REPO|SOLUTION)\}\}' "$owned/.sandcastle"; then
    fail "Owned files must not carry {{PLACEHOLDER}}s; per-repo values belong in Seed files"
  fi

  step "A file is Owned or Seed, never both, and the Owned files that moved from the Seed hold what Apply stamps on every repo"
  # Apply writes the Owned copy and then reports the Seed one as skipped, so a file back in both folders would sit unnoticed.
  both="$(comm -12 <(cd "$owned" && find . -type f | sort) <(cd "$seed" && find . -type f | sort))"
  [[ -z "$both" ]] || fail "these files are in both template/owned and template/seed: $both"
  threshold="$(tr -d '[:space:]' < "$owned/.github/ci/coverage-threshold")"
  if [[ ! "$threshold" =~ ^[0-9]+$ ]] || ((10#$threshold > 100)); then
    fail "the Owned .github/ci/coverage-threshold must hold a whole percentage from 0 to 100 (ci.yml refuses anything else), not '$threshold'"
  fi
  [[ ! -e "$owned/LICENSE" ]] || fail "LICENSE must be a Seed file, not Owned: Apply would overwrite an existing project's copyright year"
  grep -qxF 'Copyright (c) {{YEAR}} mpaulosky' "$seed/LICENSE" || fail "the Seed LICENSE must read 'Copyright (c) {{YEAR}} mpaulosky': Apply fills in the year it writes the file, and never changes an existing one"
  for template in bug_report.md feature_request.md chore_request.md; do
    grep -qxF 'assignees: mpaulosky' "$owned/.github/ISSUE_TEMPLATE/$template" \
      || fail "the Owned $template must carry 'assignees: mpaulosky': an Owned file has no {{OWNER}} to fill"
  done

  step "The Owned files check covers what builds the C# scripts in .github/scripts/"
  # check_gh_repo.py's OWNED is the list owned-files.yml's check holds a PR to, and it names .github/scripts/ files one by one: each shield there
  # (a Directory.* file, which every build of a script reads) must be in it, so a PR can't make a script's build read the repo's own files
  # unseen, and each .github/scripts/ entry must be a file the Template ships (mpaulosky/Application_Factory#125).
  owned_scripts_list="$(python3 -I -c 'import ast, sys
tree = ast.parse(open(sys.argv[1], encoding="utf-8").read())
owned = next(ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign) and [getattr(t, "id", "") for t in node.targets] == ["OWNED"])
print("\n".join(entry for entry in owned if entry.startswith(".github/scripts/")))' "$owned/.github/scripts/check_gh_repo.py")" \
    || fail "can't read OWNED from template/owned/.github/scripts/check_gh_repo.py"
  owned_shields=0
  for shield in "$owned"/.github/scripts/Directory.*; do
    [[ -f "$shield" ]] || continue
    owned_shields=$((owned_shields + 1))
    grep -qxF ".github/scripts/${shield##*/}" <<< "$owned_scripts_list" \
      || fail "template/owned/.github/scripts/${shield##*/} is a shield of the C# scripts, so it must be in check_gh_repo.py's OWNED"
  done
  ((owned_shields == 3)) || fail "found $owned_shields Directory.* shield(s) in template/owned/.github/scripts/, expected Directory.Build.props, Directory.Build.targets and Directory.Packages.props"
  while IFS= read -r entry; do
    [[ -z "$entry" || -f "$owned/$entry" ]] || fail "check_gh_repo.py's OWNED names $entry, which the Template doesn't ship"
  done <<< "$owned_scripts_list"

  step "Issue references in Owned files name their repo"
  # Applied into a repo, a bare #N reads as that repo's own issue or PR
  # (mpaulosky/dotfiles#186): write mpaulosky/dotfiles#N, or <Repo>#N for
  # another repo's. A citation in parentheses is checked everywhere, tests
  # included; elsewhere every #N is, outside the tests' fixture data, except
  # HTML entities (&#9;) and the exact examples in EXAMPLES, so an example
  # hides nothing else on its line. A Sandcastle *.test.mts carries fixture
  # data in its strings, so only its // comment lines are checked for a bare
  # #N. C#, not grep -P, which BSD grep lacks.
  # The rule is tests/IssueRefs.cs (its Examples list is there), which tests/check-issue-refs.sh runs (ADR 0017): its parity suite is
  # tests/check-issue-refs.test.sh and its xUnit tests are in tests/Scripts.Tests/IssueRefs/. The Factory's scripts/gate.sh runs it too,
  # so --quick catches a bare #N before this step does.
  need dotnet
  bash "$skill_dir/tests/check-issue-refs.test.sh" || fail "check-issue-refs.test.sh"
  refs_rc=0
  bash "$skill_dir/tests/check-issue-refs.sh" "$owned" || refs_rc=$?
  if ((refs_rc == 1)); then
    fail "write issue references in Owned files as mpaulosky/dotfiles#N (or <Repo>#N), not a bare #N"
  elif ((refs_rc != 0)); then
    fail "check-issue-refs.sh failed (exit $refs_rc) without checking: see its output above"
  fi

  step "git status guards in Owned scripts name their untracked-files setting"
  # A guard that reads git status --porcelain without --untracked-files is weakened by a user's status.showUntrackedFiles=no. The rule is tests/GitStatusGuards.cs, which
  # tests/check-git-status.sh runs (ADR 0017; its parity suite is tests/check-git-status.test.sh); the Factory's scripts/gate.sh runs it too, so --quick catches it before this step does.
  bash "$skill_dir/tests/check-git-status.test.sh" || fail "check-git-status.test.sh"
  status_rc=0
  bash "$skill_dir/tests/check-git-status.sh" "$owned" || status_rc=$?
  if ((status_rc == 1)); then
    fail "give each git status --porcelain in an Owned script --untracked-files=all"
  elif ((status_rc != 0)); then
    fail "check-git-status.sh failed (exit $status_rc) without checking: see its output above"
  fi
  # The two wrappers run their C# from its own folder, so a global.json in the caller's folder that pins an SDK nobody has installed changes
  # nothing, and a relative folder is the caller's: each finds its finding in a relative folder from under such a global.json (dotnet itself
  # fails there, which shows the global.json is read). check-issue-refs prints paths below the folder, check-git-status the folder made absolute.
  wrappers="$md_tmp/checker-wrappers"
  mkdir -p "$wrappers/owned/scripts"
  printf '{"sdk": {"version": "8.0.1", "rollForward": "disable"}}\n' > "$wrappers/global.json"
  # shellcheck disable=SC2016 # the fixture's own text
  printf '%s\n' 'See #5 for why.' '[[ -z "$(git status --porcelain)" ]]' > "$wrappers/owned/scripts/a.sh"
  ! (cd "$wrappers" && dotnet --version) > /dev/null 2>&1 || fail "dotnet ran in a folder whose global.json pins SDK 8.0.1, so the wrappers' check below proves nothing"
  code=0
  said="$(cd "$wrappers" && bash "$skill_dir/tests/check-issue-refs.sh" owned 2>&1)" || code=$?
  [[ $code -eq 1 && "$said" == "scripts/a.sh:1: See #5 for why." ]] \
    || fail "check-issue-refs.sh on a relative folder under a global.json that pins another SDK exited $code, not 1 with its finding: $said"
  code=0
  said="$(cd "$wrappers" && bash "$skill_dir/tests/check-git-status.sh" owned 2> /dev/null)" || code=$?
  [[ $code -eq 1 && "$said" == "$wrappers/owned/scripts/a.sh:2:"* ]] \
    || fail "check-git-status.sh on a relative folder under a global.json that pins another SDK exited $code, not 1 with its finding: $said"
  # With no dotnet on PATH (only bash and dirname, which the wrappers need first), each wrapper exits 2, nothing checked, and says so.
  no_dotnet="$md_tmp/checker-no-dotnet"
  mkdir -p "$no_dotnet"
  ln -sf "$(command -v bash)" "$no_dotnet/bash"
  ln -sf "$(command -v dirname)" "$no_dotnet/dirname"
  for wrapper in check-issue-refs check-git-status; do
    code=0
    said="$(PATH="$no_dotnet" "$no_dotnet/bash" "$skill_dir/tests/$wrapper.sh" "$owned" 2>&1)" || code=$?
    [[ $code -eq 2 && "$said" == "$wrapper.sh: dotnet isn't on PATH; tests/$wrapper.cs runs on the .NET SDK"* ]] \
      || fail "$wrapper.sh with no dotnet on PATH exited $code, not 2 with its message: $said"
  done

  step "the mutation specs still fit the code"
  # tests/mutations/*.json are specs for the hardening-review skill's mutate.py (a run edits the tree one mutation at a time: see CLAUDE.md for each spec's time). This only checks that each
  # mutation's old text still occurs once, differs from the new, and leaves a shell file that parses, so a script edit that outdates a spec shows here and not in the next run.
  bash "$skill_dir/tests/mutations/check-spec.test.sh" || fail "check-spec.test.sh"
  python3 -I "$skill_dir/tests/mutations/check-spec.py" || fail "a mutation spec no longer fits the code: update tests/mutations/*.json"

  step "gh commands in Owned scripts are told their repository"
  # A Baseline repo's gate refuses a gh command under scripts/ that reaches gh's
  # default repository, a fork in a fork's clone (auth0-management-app's
  # .github/ci/gate-checks.sh), and the repo can't edit Owned files, so a
  # re-Apply that brings one would fail every gate there (mpaulosky/dotfiles#267).
  # check_gh_repo.py holds the rule. Its own tests ship with it and run in the scripts shard (the Owned .github/scripts
  # tests above), so a check that stopped refusing anything fails there; here run the tests that need the Factory (OWNED
  # against the Template's folders, gotchas.md, the shfmt installs and the mutation spec), and test_pr_automerge_triggers.py's
  # guard and workflow set, which its own pin of them skips outside the Template.
  (cd "$skill_dir/tests/gh_repo" && "${pytest_cmd[@]}" -q -p no:cacheprovider .) || fail "gh_repo pytest"
  python3 -I "$owned/.github/scripts/check_gh_repo.py" "$owned/scripts" \
    || fail "export GH_REPO before an Owned script's first gh command, and give gh repo's subcommands their repository"
  # Every repo's gate runs the same check on the repo's own scripts and skills (mpaulosky/dotfiles#321), so the
  # Application Factory's gate will cover its own once it has any; this skill's scripts and docs are not "its own" in that sense.
  # The Seed files land in every Applied repo's scripts, .github/ci and skills, where
  # a repo's gate checks them with no Owned file to edit.
  python3 -I -B "$owned/.github/scripts/check_gh_repo.py" --repo "$seed" \
    || fail "tell every gh command in a Seed script or skill its repository"
  # The check skips the Owned Baseline files by name (OWNED), so a new Owned
  # script or skill has to be listed, or it would be checked in every repo.
  python3 -I -B - "$owned" "$owned/.github/scripts" << 'PY' || fail "list new Owned scripts and skills in check_gh_repo.py's OWNED"
import pathlib, sys

sys.path.insert(0, sys.argv[2])
import check_gh_repo

owned = pathlib.Path(sys.argv[1])
problems = []
for folder in ("scripts", ".claude/skills"):
    for path in sorted(p for p in (owned / folder).rglob("*") if p.is_file()):
        name = path.relative_to(owned).as_posix()
        if not check_gh_repo.is_owned(name):
            problems.append(f"{name} is not in OWNED")
problems.extend(f"{entry} is in OWNED but not in the Template" for entry in check_gh_repo.OWNED
                if not (owned / entry).exists())
if problems:
    print("\n".join(problems))
sys.exit(1 if problems else 0)
PY

  step ".gitignore keeps secrets out"
  # Repos keep Auth0 and other secrets in .env, and the Template's .gitignore
  # replaces theirs on every Apply.
  ignore_repo="$md_tmp/ignore-repo"
  git init -q "$ignore_repo"
  command cp -f "$owned/.gitignore" "$ignore_repo/.gitignore"
  for path in .env .env.local .sandcastle/.env src/App/.env.local; do
    git -C "$ignore_repo" check-ignore -q "$path" || fail ".gitignore must ignore $path"
  done
  if git -C "$ignore_repo" check-ignore -q .env.example; then
    fail ".gitignore must not ignore .env.example"
  fi
  # Sandcastle's own .gitignore: its secrets, and the logs, worktrees and notes a run leaves.
  mkdir -p "$ignore_repo/.sandcastle"
  command cp -f "$owned/.sandcastle/.gitignore" "$ignore_repo/.sandcastle/.gitignore"
  for path in .env logs/x worktrees/x work/x follow-up.json; do
    git -C "$ignore_repo" check-ignore -q ".sandcastle/$path" || fail ".sandcastle/.gitignore must ignore $path"
  done
  if git -C "$ignore_repo" check-ignore -q .sandcastle/.env.example; then
    fail ".sandcastle/.gitignore must not ignore .env.example"
  fi

  step "The security record: its README is Owned, and the .gitignore keeps its entries out"
  # The review writes .security/NNNN-<slug>.md in the main checkout, one per security finding. The entries are local and never
  # published, so the Owned .gitignore ignores them (the README excepted, so it can be committed); the README is Owned, so Apply
  # keeps it current, and a re-Apply leaves the entries alone.
  [[ -f "$owned/.security/README.md" ]] || fail "the Template must carry .security/README.md as an Owned file"
  [[ ! -e "$seed/.security" ]] || fail "the Template's .security/README.md is Owned: the Seed must not carry a copy"
  ignore_files=("$owned/.gitignore")
  for ignore_file in "${ignore_files[@]}"; do
    grep -qxF '.security/*' "$ignore_file" || fail "$ignore_file must ignore .security/*"
    grep -qxF '!.security/README.md' "$ignore_file" || fail "$ignore_file must keep .security/README.md"
    security_ignore="$md_tmp/security-ignore"
    rm -rf "$security_ignore"
    git init -q "$security_ignore"
    command cp -f "$ignore_file" "$security_ignore/.gitignore"
    git -C "$security_ignore" check-ignore -q .security/0001-x.md || fail "$ignore_file must ignore a .security entry"
    if git -C "$security_ignore" check-ignore -q .security/README.md; then
      fail "$ignore_file must not ignore .security/README.md"
    fi
  done
  # A re-Apply of the real Template restores the README, leaves the entries alone, and --dry-run lists no entry as overwritten.
  security_repo="$md_tmp/security-repo"
  git init -q -b main "$security_repo"
  git -C "$security_repo" remote add origin "https://github.com/acme/Widget.git"
  "$skill_dir/apply.sh" "$security_repo" > /dev/null || fail "apply.sh failed on an empty repo"
  [[ -f "$security_repo/.security/README.md" ]] || fail "apply.sh must write .security/README.md from the Template"
  mkdir -p "$security_repo/.security"
  printf '\nOur own note on the record.\n' >> "$security_repo/.security/README.md"
  printf '# 0001: An entry\n\n- Status: fixed\n' > "$security_repo/.security/0001-an-entry.md"
  [[ -z "$(git -C "$security_repo" status --porcelain --untracked-files=all -- .security/0001-an-entry.md)" ]] \
    || fail "git status must show no .security entry after Apply"
  git -C "$security_repo" -c core.hooksPath=/dev/null add -A
  git -C "$security_repo" -c core.hooksPath=/dev/null -c user.name=t -c user.email=t@example.com commit -q -m "apply"
  git -C "$security_repo" switch -q -c chore/standardize-baseline
  security_dry="$("$skill_dir/apply.sh" --dry-run "$security_repo")"
  if awk '/^Owned files that would be (added|overwritten)/ {on = 1; next} /^$/ {on = 0} on' <<< "$security_dry" | grep -q '\.security/0001'; then
    fail "apply.sh --dry-run must not list a .security entry as added or overwritten"
  fi
  grep -A40 '^Owned files that would be overwritten' <<< "$security_dry" | grep -qF '.security/README.md' \
    || fail "apply.sh --dry-run must list an edited .security/README.md as an Owned file it would overwrite"
  "$skill_dir/apply.sh" "$security_repo" > /dev/null || fail "a re-Apply failed"
  cmp -s "$owned/.security/README.md" "$security_repo/.security/README.md" || fail "a re-Apply must restore .security/README.md"
  [[ "$(cat "$security_repo/.security/0001-an-entry.md")" == "# 0001: An entry

- Status: fixed" ]] || fail "a re-Apply must leave the .security entries alone"

  step "Sandcastle workflow"
  # A repo without the SANDCASTLE_GH_TOKEN secret doesn't run Sandcastle in Actions, and its cron would fail every two hours if the workflow
  # didn't skip: the first step reports whether the secret is set, and every later step waits for it. The skills step reads the repo's list as
  # data, before the PAT is wired into git, and runs none of the repo's code; the CI workflow names it follows are the real ones.
  if python3 -I -c 'import yaml' &> /dev/null; then
    yaml_py=(python3 -I)
  else
    need uvx
    yaml_py=(uvx --with pyyaml python -I)
  fi
  secrets_script="$md_tmp/sandcastle-secrets.sh"
  skills_script="$md_tmp/sandcastle-skills.sh"
  "${yaml_py[@]}" - "$owned/.github/workflows/sandcastle.yml" "$owned/.github/workflows/ci.yml" "$owned/.github/workflows/claude-review.yml" \
    "$secrets_script" "$skills_script" << 'PY' || fail "the Owned sandcastle.yml must skip every step but the secret check when SANDCASTLE_GH_TOKEN is unset"
import sys, yaml
workflow, ci, review, secrets_out, skills_out = sys.argv[1:6]
doc = yaml.safe_load(open(workflow))
steps = doc["jobs"]["sandcastle"]["steps"]
gate = "steps.secrets.outputs.enabled == 'true'"
names = [step["name"] for step in steps]
assert steps[0]["name"] == "Check the secrets" and steps[0]["id"] == "secrets", "the first step must be the secret check, with id secrets"
for step in steps[1:]:
    assert step.get("if") in (gate, "always() && " + gate), f"step '{step['name']}' must wait for the secret check, exactly: {step.get('if')!r}"
assert names.index("Install the skills") < names.index("Set up git for the PAT"), "the skills must be installed before git holds the PAT"
skills = steps[names.index("Install the skills")]
assert "secrets" not in str(skills.get("env", {})).lower() and "token" not in str(skills.get("env", {})).lower(), "the skills step must get no secret"
assert "sandcastle-prepare" not in open(workflow).read() and ".github/ci/sandcastle-skills.txt" in skills["run"], "the skills step reads the data file and runs no repo script"
workflows = doc["on"]["workflow_run"]["workflows"]
for name in (yaml.safe_load(open(ci))["name"], yaml.safe_load(open(review))["name"], "repo-ci-baseline Template"):
    assert name in workflows, f"workflow_run doesn't follow {name}"
open(secrets_out, "w").write(steps[0]["run"])
open(skills_out, "w").write(skills["run"])
PY
  # The secret check's own script, run like a run: step with no shell: set (bash -e), with a temp GITHUB_OUTPUT.
  run_secrets() {
    local out
    out="$(mktemp)"
    secrets_rc=0
    env -i PATH="$PATH" SANDCASTLE_GH_TOKEN="$1" ANTHROPIC_API_KEY="$2" CLAUDE_CODE_OAUTH_TOKEN="$3" GITHUB_OUTPUT="$out" \
      bash --noprofile --norc -e "$secrets_script" &> /dev/null || secrets_rc=$?
    secrets_out="$(< "$out")"
    rm -f "$out"
  }
  run_secrets "" "" ""
  [[ $secrets_rc -eq 0 && $secrets_out == "enabled=false" ]] || fail "sandcastle.yml: no token must end green with enabled=false (exit $secrets_rc, output '$secrets_out')"
  run_secrets token "" ""
  [[ $secrets_rc -ne 0 && $secrets_out != *enabled=true* ]] || fail "sandcastle.yml: a token with no Claude credential must fail (exit $secrets_rc, output '$secrets_out')"
  run_secrets token key ""
  [[ $secrets_rc -eq 0 && $secrets_out == "enabled=true" ]] || fail "sandcastle.yml: a token and ANTHROPIC_API_KEY must give enabled=true (exit $secrets_rc, output '$secrets_out')"
  run_secrets token "" oauth
  [[ $secrets_rc -eq 0 && $secrets_out == "enabled=true" ]] || fail "sandcastle.yml: a token and CLAUDE_CODE_OAUTH_TOKEN must give enabled=true (exit $secrets_rc, output '$secrets_out')"
  # The skills step, run for real against a local repository standing in for the Factory. The workspace holds the repo's list; HOME is empty.
  fixture="$md_tmp/skills-fixture"
  fixture_skills=".claude/skills"
  fixture_git() { git -C "$1" -c core.hooksPath=/dev/null -c user.name=t -c user.email=t@example.com "${@:2}"; }
  fixture_repo() {
    git init -q -b main "$1"
    git -C "$1" config uploadpack.allowFilter true
    git -C "$1" config uploadpack.allowAnySHA1InWant true
  }
  fixture_repo "$fixture"
  long_name="$(printf 'a%.0s' {1..64})"
  max_names=()
  for i in {1..32}; do max_names+=("s$i"); done
  for name in alpha beta linked "$long_name" "${max_names[@]}"; do
    mkdir -p "$fixture/$fixture_skills/$name"
    echo "$name" > "$fixture/$fixture_skills/$name/SKILL.md"
  done
  ln -s /etc "$fixture/$fixture_skills/linked/escape"
  ln -s alpha "$fixture/rootlink" # a symlink the skills don't touch, which must not matter
  fixture_git "$fixture" add -A
  fixture_git "$fixture" commit -q -m skills
  good_sha="$(git -C "$fixture" rev-parse HEAD)"
  # A commit that is not in main, as a fork's commit served by hash would be.
  fixture_git "$fixture" checkout -q -b fork
  echo forked > "$fixture/$fixture_skills/alpha/SKILL.md"
  fixture_git "$fixture" commit -q -am forked
  fork_sha="$(git -C "$fixture" rev-parse HEAD)"
  fixture_git "$fixture" checkout -q main
  # A skill folder that is itself a symlink, beside the others.
  ln -s alpha "$fixture/$fixture_skills/dirlink"
  fixture_git "$fixture" add -A
  fixture_git "$fixture" commit -q -m "a symlinked skill folder"
  dirlink_sha="$(git -C "$fixture" rev-parse HEAD)"
  # A folder above the skills that is a symlink: the skill's own folder and the files in it are real.
  fixture2="$md_tmp/skills-fixture-ancestor"
  fixture_repo "$fixture2"
  mkdir -p "$fixture2/real/skills/alpha"
  echo alpha > "$fixture2/real/skills/alpha/SKILL.md"
  mkdir -p "$fixture2/.claude"
  ln -s ../real/skills "$fixture2/.claude/skills"
  fixture_git "$fixture2" add -A
  fixture_git "$fixture2" commit -q -m ancestor
  ancestor_sha="$(git -C "$fixture2" rev-parse HEAD)"
  skills_url="file://$fixture"
  run_skills() { # the list's text; sets skills_rc, skills_log, skills_home
    local ws="$md_tmp/skills-ws" log
    rm -rf "$ws" "$md_tmp/skills-home" "$md_tmp/skills-runner"
    mkdir -p "$ws/.github/ci" "$md_tmp/skills-home" "$md_tmp/skills-runner"
    [[ $1 == "<none>" ]] || printf '%s' "$1" > "$ws/.github/ci/sandcastle-skills.txt"
    [[ -z ${skills_preexisting:-} ]] || mkdir -p "$md_tmp/skills-home/.claude/skills/$skills_preexisting"
    skills_home="$md_tmp/skills-home"
    skills_rc=0
    log="$md_tmp/skills.log"
    (cd "$ws" && env -i PATH="${skills_path_prefix:-}$PATH" HOME="$skills_home" RUNNER_TEMP="$md_tmp/skills-runner" FACTORY_URL="$skills_url" \
      GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 bash --noprofile --norc -e "$skills_script") &> "$log" || skills_rc=$?
    skills_log="$(< "$log")"
    # No line of the list is ever run as a command, whatever the outcome.
    [[ ! -e "$ws/pwned" ]] || fail "sandcastle.yml: the skills list was run as a command (list '$1')"
  }
  skills_installed() { [[ -e "$skills_home/.claude/skills/$1/SKILL.md" ]]; }
  for list in "<none>" "" $'# only a comment\n\n'; do
    run_skills "$list"
    [[ $skills_rc -eq 0 && ! -e "$skills_home/.claude" ]] || fail "sandcastle.yml: a skills list without entries must install nothing and pass (exit $skills_rc, list '$list')"
  done
  run_skills $'# pinned\ndotfiles-sha '"$good_sha"$'\nskill alpha\nskill beta\n'
  { [[ $skills_rc -eq 0 ]] && skills_installed alpha && skills_installed beta && [[ ! -e "$skills_home/.claude/skills/linked" ]]; } \
    || fail "sandcastle.yml: the listed skills, and only those, must be installed (exit $skills_rc): $skills_log"
  run_skills "dotfiles-sha $good_sha"$'\nskill alpha'
  { [[ $skills_rc -eq 0 ]] && skills_installed alpha; } || fail "sandcastle.yml: a list with no trailing newline must install its skills (exit $skills_rc)"
  run_skills "dotfiles-sha $dirlink_sha"$'\n \t \nskill alpha\n'
  { [[ $skills_rc -eq 0 ]] && skills_installed alpha && [[ ! -e "$skills_home/.claude/skills/dirlink" ]]; } \
    || fail "sandcastle.yml: a symlink beside the listed skills, and a whitespace-only line, must not matter (exit $skills_rc): $skills_log"
  # The caps' boundaries: 32 skills and a 64-character name pass.
  max_list="dotfiles-sha $good_sha"
  for name in "${max_names[@]}"; do max_list+=$'\nskill '"$name"; done
  run_skills "$max_list"
  { [[ $skills_rc -eq 0 ]] && skills_installed s1 && skills_installed s32; } || fail "sandcastle.yml: 32 skills must be installed (exit $skills_rc): $skills_log"
  run_skills "dotfiles-sha $good_sha"$'\nskill '"$long_name"
  { [[ $skills_rc -eq 0 ]] && skills_installed "$long_name"; } || fail "sandcastle.yml: a 64-character skill name must be installed (exit $skills_rc): $skills_log"
  over_list="dotfiles-sha $good_sha"
  for i in {1..33}; do over_list+=$'\nskill s'"$i"; done
  bad_lists=(
    $'skill alpha\n' "dotfiles-sha $good_sha"$'\n'
    "dotfiles-sha ${good_sha:0:39}"$'\nskill alpha\n' "dotfiles-sha ${good_sha^^}"$'\nskill alpha\n'
    "dotfiles-sha $good_sha"$'\ndotfiles-sha '"$good_sha"$'\nskill alpha\n'
    "dotfiles-sha $good_sha"$'\nskill Alpha\n' "dotfiles-sha $good_sha"$'\nskill ../alpha\n' "dotfiles-sha $good_sha"$'\nskill alpha beta\n'
    "dotfiles-sha $good_sha"$'\nskill alpha\nskill alpha\n' "dotfiles-sha $good_sha"$'\nskill\n' "dotfiles-sha $good_sha"$'\nskill -alpha\n'
    "dotfiles-sha $good_sha"$'\nplugin alpha\n' "dotfiles-sha $good_sha"$'\nskill alpha # trailing\n'
    "dotfiles-sha $good_sha"$'\nskill alpha\n$(touch pwned)\n' "dotfiles-sha $good_sha"$'\nskill alpha;touch pwned\n'
    "dotfiles-sha $good_sha"$'\nskill alpha\n`touch pwned`\n'
    "dotfiles-sha ${good_sha//[0-9a-f]/0}"$'\nskill alpha\n' "dotfiles-sha $good_sha"$'\nskill missing\n' "dotfiles-sha $good_sha"$'\nskill linked\n'
    "dotfiles-sha $good_sha"$'\r\nskill alpha\r\n' "dotfiles-sha $good_sha"$'\n  # indented\nskill alpha\n'
    "dotfiles-sha $fork_sha"$'\nskill alpha\n' "dotfiles-sha $dirlink_sha"$'\nskill dirlink\n' "$over_list"
    "dotfiles-sha $good_sha"$'\nskill '"${long_name}a"
  )
  for list in "${bad_lists[@]}"; do
    run_skills "$list"
    [[ $skills_rc -ne 0 && ! -e "$skills_home/.claude" ]] || fail "sandcastle.yml: a bad skills list must fail and install nothing (exit $skills_rc, list '$list')"
  done
  # The rules are the step's own, not git's or the file system's: each bad list is refused for its reason.
  refused_with() { # the list's text, the part of the error it must give, and optionally another wording of the same error
    run_skills "$1"
    [[ $skills_rc -ne 0 && ($skills_log == *"$2"* || (-n ${3:-} && $skills_log == *"$3"*)) ]]
  }
  check_refused() { # the same arguments
    refused_with "$@" || fail "sandcastle.yml: a skills list '$1' must be refused with '$2'${3:+ or $3} (exit $skills_rc): $skills_log"
  }
  check_refused "dotfiles-sha ${good_sha^^}"$'\nskill alpha' "40-digit"
  check_refused "dotfiles-sha ${good_sha:0:39}"$'\nskill alpha' "40-digit"
  check_refused "dotfiles-sha $good_sha"$'\r\nskill alpha\r\n' "40-digit"
  check_refused "dotfiles-sha $good_sha"$'\nskill Alpha' "lowercase name"
  check_refused "dotfiles-sha $good_sha"$'\nskill ../alpha' "lowercase name"
  check_refused "dotfiles-sha $good_sha"$'\nskill -alpha' "lowercase name"
  check_refused "dotfiles-sha $good_sha"$'\nskill '"${long_name}a" "up to 64"
  check_refused "$over_list" "at most 32"
  check_refused "dotfiles-sha $good_sha"$'\nskill alpha\nskill alpha' "listed once"
  check_refused "dotfiles-sha $good_sha"$'\nplugin alpha' "starts with"
  check_refused "dotfiles-sha $good_sha"$'\n  # indented\nskill alpha' "starts with"
  check_refused "dotfiles-sha $good_sha"$'\nskill alpha beta' "a key and one value"
  check_refused "dotfiles-sha $good_sha" "needs a dotfiles-sha line"
  check_refused "skill alpha" "needs a dotfiles-sha line"
  check_refused "dotfiles-sha $good_sha"$'\nskill missing' "has no missing skill"
  check_refused "dotfiles-sha $good_sha"$'\nskill linked' "holds a symlink"
  check_refused "dotfiles-sha $dirlink_sha"$'\nskill dirlink' "holds a symlink"
  check_refused "dotfiles-sha $fork_sha"$'\nskill alpha' "main branch"
  skills_url="file://$fixture2"
  check_refused "dotfiles-sha $ancestor_sha"$'\nskill alpha' "holds a symlink"
  skills_url="file://$fixture"
  skills_preexisting=alpha
  check_refused "dotfiles-sha $good_sha"$'\nskill alpha' "File exists" "Already exists"
  [[ ! -e "$skills_home/.claude/skills/alpha/alpha" ]] || fail "sandcastle.yml: a skill linked itself inside an existing folder"
  # ubuntu-latest became ubuntu-26.04 on 2026-11-03, whose ln is uutils': it says "Already exists" where GNU's says "File exists"
  # (mpaulosky/dotfiles#423). The step must still refuse, and the check must read either wording, on any runner: a stub ln gives
  # the uutils wording here, and a stub whose message has neither wording must not pass for it.
  uutils_bin="$md_tmp/uutils-bin"
  boom_bin="$md_tmp/boom-bin"
  mkdir -p "$uutils_bin" "$boom_bin"
  cat > "$uutils_bin/ln" << 'STUB'
#!/usr/bin/env bash
echo "ln: failed to create symbolic link '${@: -1}': Already exists (uutils stub)" >&2
exit 1
STUB
  cat > "$boom_bin/ln" << 'STUB'
#!/usr/bin/env bash
echo "ln: boom" >&2
exit 1
STUB
  chmod +x "$uutils_bin/ln" "$boom_bin/ln"
  skills_path_prefix="$uutils_bin:"
  refused_with "dotfiles-sha $good_sha"$'\nskill alpha' "File exists" "Already exists" \
    || fail "sandcastle.yml: a skill that exists already must be refused whichever way ln words it, GNU's 'File exists' or uutils' 'Already exists' (exit $skills_rc): $skills_log"
  # Only the stub ends its message with "(uutils stub)", whatever the runner's own ln is, so this shows the step ran it.
  [[ $skills_log == *"(uutils stub)"* ]] || fail "sandcastle.yml: the stub ln was not the one the skills step ran: $skills_log"
  skills_path_prefix="$boom_bin:"
  if refused_with "dotfiles-sha $good_sha"$'\nskill alpha' "File exists" "Already exists"; then
    fail "sandcastle.yml: an ln error with neither wording must not pass for 'File exists' or 'Already exists'"
  fi
  # The control only counts when the step reached the stub: any other early failure would also have no wording.
  [[ $skills_rc -ne 0 && $skills_log == *"ln: boom"* ]] || fail "sandcastle.yml: the stub ln that says neither wording was not run by the skills step (exit $skills_rc): $skills_log"
  skills_path_prefix=
  skills_preexisting=
  # A line is never printed, whichever rule rejects it: one starting with :: would otherwise run as a workflow command from the log.
  for list in "::add-mask::secretvalue" "::add-mask::secretvalue x" "skill ::add-mask::secretvalue" "dotfiles-sha ::add-mask::secretvalue" \
    "dotfiles-sha $good_sha"$'\nskill alpha ::add-mask::secretvalue' "dotfiles-sha $good_sha"$'\nbogus ::add-mask::secretvalue'; do
    run_skills "$list"
    [[ $skills_rc -ne 0 && $skills_log != *add-mask* && $skills_log != *secretvalue* ]] \
      || fail "sandcastle.yml: a rejected skills line must not be echoed (exit $skills_rc, list '$list'): $skills_log"
  done
  [[ ! -e "$seed/.github/ci/sandcastle-prepare.sh" ]] || fail "the Seed carries a repo script the Owned sandcastle.yml would run; the skills list replaced it (ADR 0009)"
  run_skills "$(< "$seed/.github/ci/sandcastle-skills.txt")"
  [[ $skills_rc -eq 0 && ! -e "$skills_home/.claude" ]] || fail "the Seed .github/ci/sandcastle-skills.txt must install nothing"
  if grep -vF 'mpaulosky/dotfiles.git' "$owned/.github/workflows/sandcastle.yml" | grep -qE 'sandcastle:dotfiles|Blazor-Server|TicketManager|IssueTracker|IssueManager|Articles|auth0'; then
    grep -nE 'sandcastle:dotfiles|Blazor-Server|TicketManager|IssueTracker|IssueManager|Articles|auth0' "$owned/.github/workflows/sandcastle.yml"
    fail "the Owned sandcastle.yml names a repo; it carries no per-repo values"
  fi

  # The skills step's claims stay limited to what it does, and its ancestor test names the branch, not a ref git could resolve to a tag.
  grep -qF 'refs/heads/main:refs/remotes/factory/main' "$owned/.github/workflows/sandcastle.yml" \
    || fail "the Owned sandcastle.yml must fetch the Factory's main as refs/heads/main, never a bare main"
  for file in "$owned/.github/workflows/sandcastle.yml" "$skill_dir/references/sandcastle.md"; do
    grep -qF "still run the repo's" "$file" \
      || fail "${file#"$skill_dir"/} must say the later steps still run the repo's own code from main, so the skills step's claim stays limited to that step"
  done

  step "Seed Sandcastle files"
  # Written from dotfiles' copies, so none of dotfiles' own text may stay behind.
  if grep -rnE 'dotfiles|repo-ci-baseline|Support For New Projects' "$seed/.sandcastle" "$seed/package.json"; then
    fail "the Seed Sandcastle files describe dotfiles; describe any Baseline repo instead"
  fi
  # The sandbox runs the gate, so its linters are the versions the Owned gate.sh pins.
  for name in ACTIONLINT_VERSION ZIZMOR_VERSION SHELLCHECK_VERSION MARKDOWNLINT_CLI2_VERSION RUFF_VERSION; do
    pinned="$(sed -nE "s/^$name=\"([^\"]+)\".*/\1/p" "$owned/scripts/gate.sh")"
    image="$(sed -nE "s/^ARG $name=([^ ]+).*/\1/p" "$seed/.sandcastle/Dockerfile")"
    [[ -n "$pinned" && "$image" == "$pinned" ]] \
      || fail "the Seed .sandcastle/Dockerfile's $name is '$image', gate.sh's '$pinned'; set it, and its SHA-256s, to gate.sh's"
  done
  # The ARG alone installs nothing: the image's pip install takes ruff at that release, or the sandbox's gate fails on a changed *.py.
  # shellcheck disable=SC2016 # the Dockerfile's own ${RUFF_VERSION}, matched as written
  # Comment lines dropped (Docker drops them inside a continued instruction too), then continued lines joined, CRLF or LF; RUN is any case,
  # and the spec is an argument of that pip install, not of a later command on the line.
  python3 -I -c 'import re, sys
text = re.sub(r"(?m)^[ \t]*#[^\n]*\n", "", open(sys.argv[1], encoding="utf-8").read().replace("\r\n", "\n"))
text = re.sub(r"\\[ \t]*\n", " ", text)
sys.exit(0 if re.search(r"^[ \t]*(?i:RUN)[ \t]+[^\n]*\bpip3?[ \t]+install\b[^\n&|;]*[ \t]\"ruff==\$\{RUFF_VERSION\}\"", text, re.M) else 1)' "$seed/.sandcastle/Dockerfile" \
    || fail "the Seed .sandcastle/Dockerfile must have \"ruff==\${RUFF_VERSION}\" on a RUN pip install line, the release the gate's Python lint step takes"
  # And the .NET SDK feature band global.json pins (11.0.100 is the band 11.0.1xx): the newest patch of that band is what
  # rollForward latestPatch accepts, so a Dependabot patch bump needs no image change (mpaulosky/Application_Factory#103).
  sdk="$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$owned/global.json")" \
    || fail "can't read the Template's global.json sdk.version"
  channel="$(sed -nE 's/^ARG DOTNET_CHANNEL=([^ ]+).*/\1/p' "$seed/.sandcastle/Dockerfile")"
  if band="$(sdk_band "$sdk")"; then
    [[ "$channel" == "$band" ]] || fail "the Seed .sandcastle/Dockerfile's DOTNET_CHANNEL is '$channel'; global.json's SDK $sdk is the band $band"
  else
    fail "global.json's sdk.version '$sdk' isn't a stable A.B.Cnn version, so the Seed Dockerfile's channel can't be its band"
  fi
  # The band itself, at each edge.
  for pair in 11.0.100=11.0.1xx 11.0.105=11.0.1xx 11.0.199=11.0.1xx 11.0.200=11.0.2xx 12.1.300=12.1.3xx 10.10.900=10.10.9xx; do
    [[ "$(sdk_band "${pair%%=*}")" == "${pair#*=}" ]] || fail "sdk_band ${pair%%=*} must be ${pair#*=}"
  done
  for not_stable in "" 11.0.100-rc.2 11.0.200-preview.1 11.0.1000 11.0.99 11.0.010 11.0 11.0.1xx " 11.0.100" "11.0.100 "; do
    ! sdk_band "$not_stable" > /dev/null || fail "sdk_band must refuse '$not_stable'"
  done

  step "Hand-off PRs (the sandcastle:follow-up label)"
  # The session guidance is an Owned rules file, so Apply keeps it current in a Sandcastle repo. It loads in every
  # session (no paths: frontmatter), says what the label does, who may add it, and the marker a session ends its replies with.
  hand_off="$owned/.claude/rules/sandcastle-hand-off.md"
  [[ -f "$hand_off" ]] || fail "the Template must carry .claude/rules/sandcastle-hand-off.md as an Owned file, the session guidance for handing a PR off with sandcastle:follow-up"
  if grep -q '^paths:' "$hand_off"; then
    fail "the Owned .claude/rules/sandcastle-hand-off.md must load in every session: it takes no paths: frontmatter"
  fi
  session_marker="$(sed -nE 's/^export const SESSION_REPLY_MARKER = "([^"]+)";.*/\1/p' "$owned/.sandcastle/lib/config.mts")"
  [[ -n "$session_marker" ]] || fail "lib/config.mts must export SESSION_REPLY_MARKER as a one-line string"
  for needle in 'sandcastle:follow-up' 'check-branch-name.sh' 'needs-human' 'opened by the repository' "$session_marker"; do
    if [[ -z "$needle" ]] || ! grep -qF -- "$needle" "$hand_off"; then
      fail "the Owned .claude/rules/sandcastle-hand-off.md must mention '$needle'"
    fi
  done
  grep -qF -- "$session_marker" "$owned/docs/PROCESS.md" \
    || fail "SESSION_REPLY_MARKER in lib/config.mts must be the marker docs/PROCESS.md tells a session to end its replies with"
  # A fixed Copilot or Claude thread's reply names the fixing commit (mpaulosky/dotfiles#328); PR Auto-Merge and status.sh flag a resolved bot thread with no reply.
  grep -qF -- 'short SHA' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must tell a session that the reply to a fixed review thread names the fixing commit by its short SHA"
  grep -qF -- 'resolved without a reply' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say a bot thread resolved without a reply is flagged ('resolved without a reply')"
  # Issue 54: the flow PROCESS.md documents for starting an issue, reviewing a fix after a recorded review, and pushing a rebased branch.
  grep -qF -- 'scripts/work-issue.sh <n>' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must tell a task for an issue to start with scripts/work-issue.sh <n>"
  grep -qF -- 'prepare <run> recorded' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say a fix after a recorded review is reviewed with scripts/review.sh prepare <run> recorded"
  grep -qF -- 'git push --force-with-lease' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say a pushed branch rebased onto main needs git push --force-with-lease"
  # shellcheck disable=SC2016 # the backticks are PROCESS.md's Markdown, matched as they are
  grep -qF -- 'review the fix before you merge `main`' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say to review a thread's fix before merging main into the branch"
  grep -qF -- 'a clean merge keeps the record' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say a clean merge of main keeps the recorded review"
  grep -qF -- 'the pre-commit hook refuses a commit in the primary checkout' "$owned/docs/PROCESS.md" \
    || fail "docs/PROCESS.md must say the pre-commit hook refuses a commit in the primary checkout"
  # PR Auto-Merge asks Copilot once per PR and falls back to Claude 30 seconds after a request that didn't register (mpaulosky/dotfiles#360),
  # which replaces #318's grace: the docs that describe the rule say so, and the two that describe today's behaviour name no grace.
  for ask_doc in "$skill_dir/references/automerge.md" "$skill_dir/docs/adr/0004-claude-review-as-copilots-backup.md" "$owned/docs/PROCESS.md"; do
    grep -qE '(^|[^0-9])30[ -]seconds?' "$ask_doc" \
      || fail "${ask_doc#"$skill_dir"/} must name the 30 seconds PR Auto-Merge waits after Copilot's one request before calling in Claude"
  done
  for current_doc in "$skill_dir/references/automerge.md" "$owned/docs/PROCESS.md"; do
    if grep -qE 'COPILOT_GRACE_MS|[0-9]+[ -]minute grace' "$current_doc"; then
      fail "${current_doc#"$skill_dir"/} still describes #318's grace (COPILOT_GRACE_MS, a N-minute grace): mpaulosky/dotfiles#360 replaced it with one Copilot request and a 30-second fallback"
    fi
  done
  grep -qiE 'once per PR|once for each PR|one request' "$skill_dir/references/automerge.md" \
    || fail "references/automerge.md must say Copilot is asked once per PR"
  grep -qF -- 'pr-automerge:copilot-asked' "$skill_dir/references/automerge.md" \
    || fail "references/automerge.md must name the comment that records the ask (the pr-automerge:copilot-asked marker)"
  grep -qE '^## .*360' "$skill_dir/docs/adr/0004-claude-review-as-copilots-backup.md" \
    || fail "ADR 0004 must have a section for mpaulosky/dotfiles#360 (ask once, read back, fall back in 30 seconds)"
  grep -qiE 'replaces' "$skill_dir/docs/adr/0004-claude-review-as-copilots-backup.md" \
    || fail "ADR 0004 must say the #360 rule replaces #318's grace"
  # No-CI head, Copilot-less repos and a bot-pushed clean merge (mpaulosky/dotfiles#331).
  automerge_doc="$skill_dir/references/automerge.md"
  adr_0004="$skill_dir/docs/adr/0004-claude-review-as-copilots-backup.md"
  adr_0005="$skill_dir/docs/adr/0005-pr-auto-merge-brings-ready-prs-up-to-date.md"
  # A no-CI head's notice says to review and merge by hand, label or not; it is no longer 'the owner's notices' or advice to add the label.
  for notice_doc in "$automerge_doc" "$adr_0004"; do
    if grep -qF -- "owner's notices" "$notice_doc"; then
      fail "${notice_doc#"$skill_dir"/} still says a no-CI head 'goes to the owner's notices': say the run logs a notice to review and merge it by hand"
    fi
    grep -qF -- 'review and merge' "$notice_doc" \
      || fail "${notice_doc#"$skill_dir"/} must say a head with no CI gets a notice to review and merge it by hand"
  done
  if grep -qF -- 'notice to add the label or merge by hand' "$automerge_doc"; then
    fail "references/automerge.md still tells the owner to add the label for a head with no CI: the notice says to review and merge by hand"
  fi
  # R38: ADR 0004 records that the dropped-request path applies to every same-repo PR, a ruleset check is rejected, and who decided.
  grep -qE 'every same-repo PR' "$adr_0004" \
    || fail "ADR 0004 must state the dropped-request path applies to every same-repo PR, including a repo whose ruleset never requests Copilot"
  grep -qF -- 'mpaulosky/dotfiles#331' "$adr_0004" \
    || fail "ADR 0004 must cite mpaulosky/dotfiles#331 for the decision to accept the path for every same-repo PR"
  adr_options="$(sed -n '/^## Considered Options/,/^## Consequences/p' "$adr_0004")"
  grep -qiE 'ruleset (requests|check)' <<< "$adr_options" \
    || fail "ADR 0004's Considered Options must list limiting the path to repos whose ruleset requests Copilot, as rejected"
  # A bot-pushed clean merge H2 is reviewed by Claude Review itself: the docs no longer say its run skips H2 with a notice.
  grep -qF -- 'reviews H2 itself' "$automerge_doc" \
    || fail "references/automerge.md must say Claude Review reviews a bot-pushed clean merge H2 itself ('reviews H2 itself')"
  if grep -qF -- 'skips it with a notice' "$automerge_doc" "$adr_0005"; then
    fail "references/automerge.md and ADR 0005 still say Claude Review skips a bot-pushed clean merge with a notice: it reviews H2 itself"
  fi
  # The review loop (mpaulosky/dotfiles#375): a round is a review of a non-merge commit, dirty when it raised a thread or an off-diff finding; the loop is
  # exhausted after MAX_CLAUDE_ROUNDS dirty rounds in a row. PR Auto-Merge then removes review:claude, documents the open threads in one comment and merges.
  adr_0002="$skill_dir/docs/adr/0002-copilot-review-cap-and-hand-back-hold.md"
  pr_process="$skill_dir/references/pr-process.md"
  for loop_doc in "$automerge_doc" "$pr_process" "$adr_0002" "$adr_0004" "$owned/docs/PROCESS.md"; do
    grep -qiE 'exhaust' "$loop_doc" \
      || fail "${loop_doc#"$skill_dir"/} must describe the exhausted review loop (MAX_CLAUDE_ROUNDS dirty rounds in a row), which mpaulosky/dotfiles#375 put in place of the review cap"
    grep -qiE 'dirty|raised (at least one )?thread|drew threads' "$loop_doc" \
      || fail "${loop_doc#"$skill_dir"/} must say what makes a round dirty (it raised a thread or an off-diff finding)"
  done
  grep -qF -- 'pr-automerge:review-loop-exhausted' "$automerge_doc" \
    || fail "references/automerge.md must name the marker the exhaustion comment ends with (pr-automerge:review-loop-exhausted)"
  grep -qiE 'exhaustion comment|review-loop-exhausted' "$adr_0004" \
    || fail "ADR 0004 must name the exhaustion comment PR Auto-Merge posts"
  grep -qiE 'supersede' "$adr_0002" \
    || fail "ADR 0002 must say mpaulosky/dotfiles#375 supersedes its rule that every unresolved thread holds the merge, cap or not (the #146 decision)"
  grep -qF -- '#146' "$adr_0002" \
    || fail "ADR 0002 must name the #146 decision it supersedes"
  for adr in "$adr_0002" "$adr_0004"; do
    grep -qF -- 'mpaulosky/dotfiles#375' "$adr" || fail "${adr#"$skill_dir"/} must cite mpaulosky/dotfiles#375"
  done
  # mpaulosky/dotfiles#413: the reviewers read the patch without verbatim Template mirrors, the key still covers the whole diff.
  for mirror_doc in "$owned/.claude/skills/pre-push-review/SKILL.md" "$skill_dir/references/pre-push-review.md"; do
    grep -qF -- 'verbatim' "$mirror_doc" \
      || fail "${mirror_doc#"$skill_dir"/} must say the patch leaves out Template mirrors that are verbatim copies of a root file (mpaulosky/dotfiles#413)"
    tr '\n' ' ' < "$mirror_doc" | tr -s ' ' | grep -qF -- 'a change in a mirror alone still needs a new review' \
      || fail "${mirror_doc#"$skill_dir"/} must say a change in a mirror alone still needs a new review (mpaulosky/dotfiles#413)"
  done
  grep -qF -- 'identical to the root copy' "$owned/.claude/skills/hardening-review/references/brief.md" \
    || fail "the hardening-review brief must tell its reviewers that identical Template mirrors are left out (mpaulosky/dotfiles#413)"

  # mpaulosky/dotfiles#380: Claude Review reads the Template's file, not an identical root copy; a drifting copy is still read.
  claude_review_wf="$owned/.github/workflows/claude-review.yml"
  for copy_doc in "$adr_0004" "$automerge_doc" "$claude_review_wf"; do
    grep -qiE 'identical cop' "$copy_doc" \
      || fail "${copy_doc#"$skill_dir"/} must say Claude Review leaves out a root copy that is identical to the Template's file (mpaulosky/dotfiles#380)"
    grep -qiE 'differs?|drift' "$copy_doc" \
      || fail "${copy_doc#"$skill_dir"/} must say a copy that differs from the Template's file is still read (mpaulosky/dotfiles#380)"
  done
  grep -qF -- 'mpaulosky/dotfiles#380' "$adr_0004" || fail "ADR 0004 must cite mpaulosky/dotfiles#380"
  # Docs of today's behaviour no longer say a PR waits for the owner at the cap, or that threads hold past it.
  for current_doc in "$automerge_doc" "$pr_process" "$skill_dir/SKILL.md" "$skill_dir/references/gotchas.md" "$owned/docs/PROCESS.md"; do
    if grep -qE 'the owner decides|cap or not|past the (review )?cap too|Adding the label again past the cap' "$current_doc"; then
      fail "${current_doc#"$skill_dir"/} still says a PR waits for the owner at the cap or that unresolved threads hold past it: an exhausted review loop is documented in one comment and merges (mpaulosky/dotfiles#375)"
    fi
  done
  if grep -qF -- 'hold the merge for at most three review rounds' "$skill_dir/references/github-settings.md"; then
    fail "references/github-settings.md still says review threads hold the merge for at most three review rounds: an exhausted loop merges with its threads open, documented in one comment (mpaulosky/dotfiles#375)"
  fi
  # The Owned workflow's header and constants say the same, and the settings reader still finds the cap's 3.
  pr_automerge="$owned/.github/workflows/pr-automerge.yml"
  header="$(sed -n '1,/^"on":/p' "$pr_automerge")"
  grep -qiE 'exhaust' <<< "$header" || fail "pr-automerge.yml's header must describe the exhausted review loop"
  if grep -qF -- 'Past the cap every unresolved thread still holds' <<< "$header"; then
    fail "pr-automerge.yml's header still says every unresolved thread holds the merge past the cap"
  fi
  grep -qE 'const COPILOT_REVIEW_CAP = 3;' "$pr_automerge" \
    || fail "pr-automerge.yml must keep 'const COPILOT_REVIEW_CAP = 3;': github-settings.sh and SettingsCore.cs read the cap by that line"
  grep -qE 'const MAX_CLAUDE_ROUNDS = 3;' "$pr_automerge" \
    || fail "pr-automerge.yml must keep 'const MAX_CLAUDE_ROUNDS = 3;'"
  grep -qF -- 'const EXHAUSTED_MARKER = "<!-- pr-automerge:review-loop-exhausted";' "$pr_automerge" \
    || fail "pr-automerge.yml must declare the exhaustion marker github/Landing.cs's ExhaustedMarker must equal"
  # Claude reviews only where Copilot can't, and a person's threads still hold at an exhausted loop (mpaulosky/dotfiles#378): the label is
  # ignored while Copilot can review, a person's open thread holds the merge, the exhaustion comment says when the head was not reviewed, and
  # only a login with write access restarts a loop.
  claude_review_yml="$owned/.github/workflows/claude-review.yml"
  claude_review_header="$(sed -n '1,/^"on":/p' "$claude_review_yml")"
  for rule_doc in "$automerge_doc" "$pr_process" "$adr_0002" "$adr_0004"; do
    grep -qF -- 'ignored while Copilot' "$rule_doc" || fail "${rule_doc#"$skill_dir"/} must say review:claude is ignored while Copilot can review (Claude reviews only where Copilot can't: mpaulosky/dotfiles#378)"
    grep -qF -- 'mpaulosky/dotfiles#378' "$rule_doc" || fail "${rule_doc#"$skill_dir"/} must cite mpaulosky/dotfiles#378"
  done
  for header_text in "$header" "$claude_review_header"; do
    grep -qF -- 'ignored while Copilot' <<< "$header_text" || fail "the headers of pr-automerge.yml and claude-review.yml must say review:claude is ignored while Copilot can review (mpaulosky/dotfiles#378)"
  done
  for person_doc in "$automerge_doc" "$pr_process" "$adr_0002"; do
    grep -qiE "opened by a person|a person's (open )?threads?" "$person_doc" || fail "${person_doc#"$skill_dir"/} must say a person's open thread still holds the merge at an exhausted review loop (mpaulosky/dotfiles#378)"
  done
  grep -qiE "opened by a person|a person's (open )?threads?" <<< "$header" || fail "pr-automerge.yml's header must say a person's open thread still holds the merge at an exhausted review loop (mpaulosky/dotfiles#378)"
  for restart_doc in "$automerge_doc" "$adr_0002"; do
    grep -qiE 'write access' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must say only a login with write access restarts a review loop (mpaulosky/dotfiles#378)"
    grep -qF -- 'was not reviewed' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must say the exhaustion comment says when the head 'was not reviewed' (mpaulosky/dotfiles#378)"
  done
  # A recognised restart stays recognised, and a 404 permission lookup is no write access (mpaulosky/dotfiles#381): the restart is decided
  # from the label events and the rounds (an add counts unless PR Auto-Merge removed it), never from whether Copilot can review now.
  for restart_doc in "$automerge_doc" "$adr_0002"; do
    grep -qF -- 'mpaulosky/dotfiles#381' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must cite mpaulosky/dotfiles#381"
    grep -qE '\b404\b' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must say a 404 from the permission lookup counts as no write access (mpaulosky/dotfiles#381)"
    grep -qiE 'no write access' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must say a login without write access never restarts a loop, a 404 included (mpaulosky/dotfiles#381)"
    grep -qiE 'recognised|removed (it|the label)|removal' "$restart_doc" || fail "${restart_doc#"$skill_dir"/} must say a recognised restart stays recognised: an add counts unless PR Auto-Merge removed it (mpaulosky/dotfiles#381)"
  done
  grep -qF -- 'mpaulosky/dotfiles#381' <<< "$header" || fail "pr-automerge.yml's header must cite mpaulosky/dotfiles#381 where it describes the restart and the 404 permission lookup"
  grep -qE '\b404\b' <<< "$header" || fail "pr-automerge.yml's header must say a 404 permission lookup counts as no write access (mpaulosky/dotfiles#381)"
  # The re-Apply PR's ask record, and Copilot as the exact bot (mpaulosky/dotfiles#384): a PR runs its base's workflows, so a repo's first
  # re-Apply PR has the previous PR Auto-Merge, which never posts the ask record; reapply.sh posts it when its Copilot request doesn't register.
  for record_doc in "$automerge_doc" "$pr_process" "$skill_dir/references/gotchas.md" "$adr_0004"; do
    grep -qF -- "\`reapply.sh\` posts the ask record" "$record_doc" || fail "${record_doc#"$skill_dir"/} must say \`reapply.sh\` posts the ask record on a repo's first re-Apply PR, when its Copilot request doesn't register (mpaulosky/dotfiles#384)"
  done
  grep -qF -- 'mpaulosky/dotfiles#384' "$adr_0004" || fail "ADR 0004 must cite mpaulosky/dotfiles#384"
  grep -qF -- 'copilot-pull-request-reviewer[bot]' "$claude_review_yml" || fail "claude-review.yml must name the exact bot, copilot-pull-request-reviewer[bot], that its Copilot step matches (mpaulosky/dotfiles#384)"
  grep -qF -- 'copilot-pull-request-reviewer[bot]' "$automerge_doc" || fail "references/automerge.md must say Claude Review matches Copilot by its exact login, copilot-pull-request-reviewer[bot] (mpaulosky/dotfiles#384)"
  grep -qiE "records Copilot's ask" "$skill_dir/SKILL.md" || fail "SKILL.md must say reapply.sh records Copilot's ask when the request doesn't register (mpaulosky/dotfiles#384)"
  # The docs of today's behaviour no longer say a Claude review runs beside Copilot's, or that no thread holds an exhausted loop's merge.
  for current_doc in "$automerge_doc" "$pr_process" "$adr_0002" "$owned/docs/PROCESS.md"; do
    if grep -qE "neither (does|do) the threads still (open|unresolved)|Exhaustion doesn't hold the merge, and neither" "$current_doc"; then
      fail "${current_doc#"$skill_dir"/} still says no thread holds an exhausted loop's merge: a person's open thread does (mpaulosky/dotfiles#378)"
    fi
  done
  if grep -qE "neither do the threads$|neither do the threads still open" <<< "$header"; then
    fail "pr-automerge.yml's header still says the threads still open at exhaustion don't hold the merge: a person's do (mpaulosky/dotfiles#378)"
  fi
  # The record-head job of the Owned sandcastle.yml posts the comment lib/follow-up.mts#approvedHeadOf reads, from the label event's head.
  sandcastle_yml="$owned/.github/workflows/sandcastle.yml"
  head_marker="$(sed -nE 's/^export const APPROVED_HEAD_MARKER = "([^"]+)";.*/\1/p' "$owned/.sandcastle/lib/config.mts")"
  [[ -n "$head_marker" ]] || fail "lib/config.mts must export APPROVED_HEAD_MARKER as a one-line string"
  # Only the record-head job's own text is searched, up to the next job (a line at two spaces' indent): the sandcastle job's if: has some of the same clauses.
  record_job="$(sed -n '/^  record-head:$/,$p' "$sandcastle_yml" | sed -n '1p;2,${/^  [A-Za-z_-]*:/q;p}')"
  [[ -n "$record_job" ]] || fail "sandcastle.yml must have a record-head job"
  for needle in "body=\"$head_marker \$HEAD_SHA -->" "github.event_name == 'pull_request_target'" 'github.run_attempt == 1' \
    "github.event.label.name == 'sandcastle:follow-up'" 'github.event.sender.login == github.repository_owner' \
    'github.event.pull_request.head.repo.full_name == github.repository' 'permissions: {}' '^[0-9a-f]{40}$' \
    "repos/\$REPO/pulls/\$PR_NUMBER\" --jq .head.sha" "if [ \"\$current\" != \"\$HEAD_SHA\" ]" 'for attempt in 1 2 3'; do
    grep -qF -- "$needle" <<< "$record_job" || fail "sandcastle.yml's record-head job must carry '$needle' (the comment's marker, the owner-only, same-repository and first-run guards, no token permissions, the SHA check, the current-head check, the retry)"
  done
  if grep -qE 'uses:|actions/checkout' <<< "$record_job"; then
    fail "sandcastle.yml's record-head job holds the host token on pull_request_target: it must use no action and check nothing out"
  fi
  if sed -n '/^        run: |$/,$p' <<< "$record_job" | grep -qF "\${{"; then
    fail "sandcastle.yml's record-head job must pass event data to its script through env, never inline in run:"
  fi
  # The job's script, run for real under env -i with stubs for gh and sleep on PATH: the greps above only see fragments, so an inverted
  # comparison, a dropped exit or a body that approvedHeadOf's regex no longer reads would pass them.
  record_script="$md_tmp/record-head.sh"
  "${yaml_py[@]}" - "$sandcastle_yml" "$record_script" << 'PY' || fail "sandcastle.yml's record-head job must have one step with a run: script"
import sys, yaml
steps = yaml.safe_load(open(sys.argv[1]))["jobs"]["record-head"]["steps"]
assert len(steps) == 1, "record-head must be a single step"
open(sys.argv[2], "w").write(steps[0]["run"])
PY
  record_bin="$md_tmp/record-bin"
  mkdir -p "$record_bin"
  cat > "$record_bin/sleep" << 'STUB'
#!/usr/bin/env bash
exit 0
STUB
  cat > "$record_bin/gh" << 'STUB'
#!/usr/bin/env bash
# Logs "<GET|POST> <endpoint> <GH_TOKEN>" to RECORD_CALLS. A GET prints RECORD_CURRENT, or fails when that is FAIL. A POST appends its body to
# RECORD_POSTS and fails while RECORD_FAILS has attempts left.
method=GET; endpoint=
for arg in "$@"; do [[ $arg == repos/* ]] && endpoint=$arg; done
[[ " $* " == *" --method POST "* ]] && method=POST
printf '%s %s %s\n' "$method" "$endpoint" "${GH_TOKEN-}" >> "$RECORD_CALLS"
if [[ $method == POST ]]; then
  for arg in "$@"; do [[ $arg == body=* ]] && printf '%s\n--\n' "${arg#body=}" >> "$RECORD_POSTS"; done
  left="$(<"$RECORD_FAILS")"
  if (( left > 0 )); then echo $((left - 1)) > "$RECORD_FAILS"; exit 1; fi
  exit 0
fi
[[ $RECORD_CURRENT == FAIL ]] && exit 1
echo "$RECORD_CURRENT"
STUB
  chmod +x "$record_bin/gh" "$record_bin/sleep"
  run_record() { # token head current post-failures
    record_posts="$md_tmp/record-posts"
    record_calls="$md_tmp/record-calls"
    record_out="$md_tmp/record-out"
    : > "$record_posts"
    : > "$record_calls"
    echo "$4" > "$md_tmp/record-fails"
    record_rc=0
    env -i PATH="$record_bin:$PATH" SANDCASTLE_GH_TOKEN="$1" HEAD_SHA="$2" RECORD_CURRENT="$3" PR_NUMBER=7 REPO=o/r \
      RECORD_POSTS="$record_posts" RECORD_CALLS="$record_calls" RECORD_FAILS="$md_tmp/record-fails" \
      bash --noprofile --norc -e "$record_script" &> "$record_out" || record_rc=$?
    record_count="$(grep -c '^--$' "$record_posts" || true)"
  }
  sha_a="$(printf 'a%.0s' {1..40})"
  sha_b="$(printf 'b%.0s' {1..40})"
  host_token="host-token-1"
  read_call="GET repos/o/r/pulls/7 $host_token"
  post_call="POST repos/o/r/issues/7/comments $host_token"
  run_record "" "$sha_a" "$sha_a" 0
  [[ $record_rc -eq 0 && $record_count -eq 0 && ! -s $record_calls ]] || fail "record-head: no token must warn, call gh not at all and exit 0 (exit $record_rc, posts $record_count)"
  # Each bad head is also the pull request's current head, so only the format check can stop it: it must fail before any call to gh.
  for bad_head in "not-a-sha" "${sha_a^^}" "${sha_a:1}" "${sha_a}a" ""; do
    run_record "$host_token" "$bad_head" "$bad_head" 0
    [[ $record_rc -ne 0 && $record_count -eq 0 && ! -s $record_calls ]] \
      || fail "record-head: a head that isn't 40 lower-case hex ('$bad_head') must fail before any call to gh and post nothing (exit $record_rc, posts $record_count)"
  done
  run_record "$host_token" "$sha_a" "$sha_b" 0
  [[ $record_rc -ne 0 && $record_count -eq 0 ]] || fail "record-head: a head that moved since the event must fail and post nothing (exit $record_rc, posts $record_count)"
  run_record "$host_token" "$sha_a" "" 0
  [[ $record_rc -ne 0 && $record_count -eq 0 ]] || fail "record-head: an empty read of the pull request's head must fail and post nothing (exit $record_rc, posts $record_count)"
  run_record "$host_token" "$sha_a" FAIL 0
  [[ $record_rc -ne 0 && $record_count -eq 0 ]] || fail "record-head: a failed read of the pull request's head must fail and post nothing (exit $record_rc, posts $record_count)"
  grep -qF "::error::Couldn't read the pull request's head" "$record_out" \
    || fail "record-head: a failed read of the pull request's head must say so in an ::error:: line, as the other failures do"
  run_record "$host_token" "$sha_a" "$sha_a" 0
  [[ $record_rc -eq 0 && $record_count -eq 1 ]] || fail "record-head: the event's head must be posted once (exit $record_rc, posts $record_count)"
  [[ "$(< "$record_calls")" == "$read_call"$'\n'"$post_call" ]] \
    || fail "record-head: it must read repos/o/r/pulls/7 and post to repos/o/r/issues/7/comments, both with SANDCASTLE_GH_TOKEN as GH_TOKEN (calls: $(tr '\n' '|' < "$record_calls"))"
  grep -qE "^$head_marker $sha_a -->\$" <(head -n1 "$record_posts") \
    || fail "record-head: the comment's first line must be '$head_marker <sha> -->', which lib/follow-up.mts#approvedHeadOf reads"
  run_record "$host_token" "$sha_a" "$sha_a" 2
  [[ $record_rc -eq 0 && $record_count -eq 3 ]] || fail "record-head: two failed posts then a good one must exit 0 after three tries (exit $record_rc, posts $record_count)"
  [[ "$(grep -c '^GET ' "$record_calls")" -eq 1 && "$(grep -c "^$post_call\$" "$record_calls")" -eq 3 ]] \
    || fail "record-head: it must read the pull request's head once and retry only the post, every try as the host (calls: $(tr '\n' '|' < "$record_calls"))"
  run_record "$host_token" "$sha_a" "$sha_a" 3
  [[ $record_rc -ne 0 && $record_count -eq 3 ]] || fail "record-head: three failed posts must exit non-zero after three tries (exit $record_rc, posts $record_count)"
  # mpaulosky/dotfiles#368: the owner's hand-back unlabel is a pull_request_target event too, so it re-queues a PR with merge conflicts (GitHub
  # starts no pull_request run on one) and runs main's copy of the workflow, never the PR branch's, with the secrets.
  grep -A1 '^  pull_request_target:$' "$sandcastle_yml" | grep -qF 'types: [labeled, unlabeled]' \
    || fail "sandcastle.yml's pull_request_target trigger must be types: [labeled, unlabeled]: labeled records the hand-off head, unlabeled re-queues a PR the owner hands back"
  if grep -qE '^  pull_request:' "$sandcastle_yml"; then
    fail "sandcastle.yml must have no pull_request: trigger: a pull_request run uses the PR branch's workflow with the secrets and doesn't start on a PR with merge conflicts"
  fi
  # The two jobs' if: expressions, evaluated for the events an unlabeled pull_request_target can carry.
  "${yaml_py[@]}" - "$sandcastle_yml" << 'PY' || fail "sandcastle.yml: an unlabeled pull_request_target event must start the sandcastle job only for the owner removing sandcastle:needs-info or sandcastle:needs-human on a PR into main, and never record-head"
import re, sys, yaml

jobs = yaml.safe_load(open(sys.argv[1]))["jobs"]
TOKEN = re.compile(r"\s*(?:('[^']*')|(&&)|(\|\|)|(==)|(!=)|(\()|(\))|(,)|(\d+)|([A-Za-z_][\w.]*))")

def evaluate(expr, ctx):
    """Evaluate the subset of GitHub's expression language the workflow's if: uses; a missing context value is ''."""
    py, pos = [], 0
    expr = expr.strip()
    while pos < len(expr):
        m = TOKEN.match(expr, pos)
        assert m, f"can't read the expression at: {expr[pos:pos + 30]!r}"
        pos = m.end()
        text, and_, or_, eq, ne, lp, rp, comma, num, name = m.groups()
        if text is not None: py.append(text)
        elif and_: py.append(" and ")
        elif or_: py.append(" or ")
        elif eq: py.append("==")
        elif ne: py.append("!=")
        elif lp: py.append("(")
        elif rp: py.append(")")
        elif comma: py.append(",")
        elif num: py.append(num)
        elif name in ("startsWith", "true", "false"):
            py.append({"startsWith": "startsWith", "true": "True", "false": "False"}[name])
        else: py.append(f"ctx.get({name!r}, '')")
    return bool(eval("".join(py), {"__builtins__": {}}, {"ctx": ctx, "startsWith": lambda a, b: str(a).startswith(str(b))}))

def starts(job, ctx):
    return evaluate(jobs[job]["if"], ctx)

def unlabeled(event="pull_request_target", sender="owner", label="sandcastle:needs-info", base="main", **extra):
    ctx = {
        "github.event_name": event, "github.event.action": "unlabeled", "github.event.sender.login": sender,
        "github.event.label.name": label, "github.repository_owner": "owner", "github.repository": "owner/repo",
        "github.event.pull_request.base.ref": base, "github.event.pull_request.head.repo.full_name": "owner/repo",
        "github.event.pull_request.head.ref": "fix/1-x", "github.ref": "refs/heads/main", "github.run_attempt": 1,
    }
    ctx.update(extra)
    return ctx

problems = []
def expect(what, got, want):
    if got != want:
        problems.append(f"{what}: sandcastle job {'starts' if got else 'does not start'}, want {'a start' if want else 'no start'}")

for label in ("sandcastle:needs-info", "sandcastle:needs-human"):
    expect(f"owner removes {label} from a PR into main", starts("sandcastle", unlabeled(label=label)), True)
    expect(f"owner removes {label} from a PR into a branch other than main", starts("sandcastle", unlabeled(label=label, base="release")), False)
    expect(f"owner removes {label}, base ref missing", starts("sandcastle", {k: v for k, v in unlabeled(label=label).items() if k != "github.event.pull_request.base.ref"}), False)
    expect(f"someone else removes {label} from a PR into main", starts("sandcastle", unlabeled(label=label, sender="stranger")), False)
    expect(f"the host's bot removes {label} from a PR into main", starts("sandcastle", unlabeled(label=label, sender="github-actions[bot]")), False)
    expect(f"a pull_request run (no longer a trigger) of owner removing {label} from a PR into main", starts("sandcastle", unlabeled(event="pull_request", label=label)), False)
for label in ("sandcastle:follow-up", "sandcastle:ready", "Sandcastle", "bug", "review:claude", "sandcastle:needs-info-x"):
    expect(f"owner removes {label} from a PR into main", starts("sandcastle", unlabeled(label=label)), False)
# A fork PR's pull_request_target run is main's workflow, so the base-ref check, not the head's repo, is what the job needs; the owner's label removal still counts.
expect("owner removes sandcastle:needs-info from a fork PR into main", starts("sandcastle", unlabeled(**{"github.event.pull_request.head.repo.full_name": "fork/repo"})), True)
# The same hand-back on an issue is unchanged: an issue event has no base ref.
issue = {k: v for k, v in unlabeled(event="issues").items() if not k.startswith("github.event.pull_request")}
expect("owner removes sandcastle:needs-info from an issue", starts("sandcastle", issue), True)
expect("someone else removes sandcastle:needs-info from an issue", starts("sandcastle", {**issue, "github.event.sender.login": "stranger"}), False)

for label in ("sandcastle:needs-info", "sandcastle:needs-human", "sandcastle:follow-up"):
    for base in ("main", "release"):
        for sender in ("owner", "stranger"):
            expect(f"record-head on unlabeled {label} by {sender}, base {base}", starts("record-head", unlabeled(label=label, sender=sender, base=base)), False)
# The labeled event still starts record-head, and not the sandcastle job, for the owner's hand-off label.
labeled = unlabeled(label="sandcastle:follow-up", **{"github.event.action": "labeled"})
expect("record-head on the owner's labeled sandcastle:follow-up", starts("record-head", labeled), True)
expect("the sandcastle job on the owner's labeled sandcastle:follow-up", starts("sandcastle", labeled), False)

if problems:
    print("\n".join(problems))
    sys.exit(1)
PY
  adr_0009_decisions="$(sed -n '/^## Decisions/,/^## Consequences/p' "$skill_dir/docs/adr/0009-sandcastle-workflow-is-owned.md")"
  for needle in 'unlabeled' 'conflict' 'fork' 'mpaulosky/dotfiles#368'; do
    grep -qiF -- "$needle" <<< "$adr_0009_decisions" \
      || fail "ADR 0009's Decisions must say why unlabeled runs on pull_request_target: it lacks '$needle' (conflicting PRs, main's copy of the workflow, fork PRs never had the secrets)"
  done
  if grep -nE 'dotfiles|repo-ci-baseline|Support For New Projects' "$hand_off"; then
    fail "the Owned .claude/rules/sandcastle-hand-off.md is written into other repos; describe any Baseline repo instead of dotfiles"
  fi
  for needle in 'sandcastle:follow-up' '.claude/rules/sandcastle-hand-off.md' 'SANDCASTLE_LABELS' 'claude-session'; do
    grep -qF -- "$needle" "$skill_dir/references/sandcastle.md" || fail "references/sandcastle.md must document the hand-off PRs: it doesn't mention '$needle'"
  done
  grep -qF -- '.claude/rules/sandcastle-hand-off.md' "$skill_dir/CONTEXT.md" \
    || fail "CONTEXT.md's Sandcastle section must list the Owned .claude/rules/sandcastle-hand-off.md"

  # retired.txt (the Owned files Apply deletes, mpaulosky/Application_Factory#124): each path is out of the Template, and the ledger's
  # RETIRED_PATHS, which lets a re-Apply's deletion through without a review, names exactly the same paths.
  step "retired Owned files"
  retired_listed=""
  if [[ -f "$skill_dir/retired.txt" ]]; then
    retired_listed="$(sed 's/\r$//' "$skill_dir/retired.txt" | awk '/^#/ || /^$/ { next } { print substr($0, 67) }' | LC_ALL=C sort -u)"
  fi
  while IFS= read -r rel; do
    [[ -n "$rel" ]] || continue
    if [[ -e "$owned/$rel" || -e "$seed/$rel" ]]; then fail "retired.txt names $rel, which is still in the Template"; fi
  done <<< "$retired_listed"
  retired_line="$(grep -E '^export const RETIRED_PATHS' "$owned/.sandcastle/lib/review-ledger.mts")" \
    || fail "review-ledger.mts must export RETIRED_PATHS on one line"
  retired_ledger="$({ grep -oE '"[^"]+"' <<< "${retired_line#*\(\[}" || true; } | tr -d '"' | LC_ALL=C sort -u)"
  [[ "$retired_ledger" == "$retired_listed" ]] \
    || fail "review-ledger.mts's RETIRED_PATHS must name exactly retired.txt's paths: it has '${retired_ledger//$'\n'/, }', retired.txt '${retired_listed//$'\n'/, }'"

  step "apply.sh smoke test"
  "$skill_dir/tests/apply.test.sh" || fail "apply.test.sh"
  "$skill_dir/tests/seed-check.test.sh" || fail "seed-check.test.sh"
  "$skill_dir/tests/seed-dependabot.test.sh" || fail "seed-dependabot.test.sh"
  "$skill_dir/tests/seed-dockerfile.test.sh" || fail "seed-dockerfile.test.sh"
  "$skill_dir/tests/doc-single-source.test.sh" || fail "doc-single-source.test.sh"
  "$skill_dir/tests/no-dotfiles-dependency.test.sh" || fail "no-dotfiles-dependency.test.sh"
  "$skill_dir/tests/repos-scope.test.sh" || fail "repos-scope.test.sh"

  step "reapply.sh smoke test"
  "$skill_dir/tests/reapply.test.sh" || fail "reapply.test.sh"
fi # the checks shard

echo
if [[ $failures -gt 0 ]]; then
  echo "test.sh${shard:+ ($shard shard)}: $failures check(s) failed [+${SECONDS}s]"
  exit 1
fi
echo "test.sh${shard:+ ($shard shard)}: all checks passed [+${SECONDS}s]"
