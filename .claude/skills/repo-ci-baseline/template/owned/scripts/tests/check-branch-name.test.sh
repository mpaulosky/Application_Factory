#!/usr/bin/env bash
# Tests for scripts/check-branch-name.sh. pre-push.test.sh covers the standard
# itself through the hook; these cover the --pr form ci.yml's "Branch name"
# job uses, and the exit codes and message both callers rely on.
# Usage: scripts/tests/check-branch-name.test.sh
set -uo pipefail

SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/check-branch-name.sh"

PASSED=0
FAILED=0

# expect <allowed|refused> <args...>
expect() {
  local outcome="$1" output status
  shift
  output="$(bash "$SCRIPT" "$@" 2>&1)"
  status=$?
  local shown="${*: -1}"
  if [[ "$shown" == *[[:cntrl:]]* ]]; then shown="$(printf '%q' "$shown")"; fi
  if [[ "$outcome" == allowed && $status -eq 0 && -z "$output" ]] \
    || [[ "$outcome" == refused && $status -eq 1 && "$output" == *"'${shown}' does not match"* && "$output" != *$'\n'*$'\n'* ]]; then
    PASSED=$((PASSED + 1))
    echo "ok   - $* is $outcome"
  else
    FAILED=$((FAILED + 1))
    echo "FAIL - $* should be $outcome: exit $status"
    while IFS= read -r line; do echo "       | $line"; done <<< "$output"
  fi
}

expect allowed fix/42-null-children
expect allowed chore/tidy-docs
expect allowed --pr feature/7-oauth2-login
expect refused claude/project-thread-ot9sjt
expect refused --pr claude/project-thread-ot9sjt
expect refused --pr patch-1
expect refused ""

# Dependabot's branches are allowed to push to, so a person can fix its PR.
expect allowed dependabot/github_actions/main/minor-and-patch-actions-401d4b32a3
expect allowed --pr dependabot/nuget/all-nuget-1a2b3c
expect refused dependabot
# Scoped npm packages and build-metadata versions: '@' and '+' are shell-safe, so the hook takes them.
expect allowed dependabot/npm_and_yarn/@types/node-20.1.0
expect allowed dependabot/nuget/Foo.Bar_1.2.3+build.5
expect allowed dependabot/@scope/x
expect allowed dependabot/a+b
# The pattern is anchored at the start: dependabot/ inside another name is not a Dependabot branch.
expect refused claude/dependabot/x
expect refused xdependabot/a
# The --pr form takes any other dependabot/* name only from dependabot[bot]: its branch can't be renamed.
expect allowed --pr --author 'dependabot[bot]' dependabot/npm_and_yarn/types/node-20~1
expect allowed --pr --author 'dependabot[bot]' dependabot/
expect refused --pr --author someone dependabot/npm_and_yarn/types/node-20~1
expect refused --pr dependabot/npm_and_yarn/types/node-20~1
# shellcheck disable=SC2016 # the $( is the point: a literal name, not an expansion
expect refused --pr --author 'dependabot' 'dependabot/$(x)'
# shellcheck disable=SC2016 # the same literal name
expect refused --pr --author 'dependabot[bot] ' 'dependabot/$(x)'
expect allowed --pr --author someone dependabot/nuget/all-nuget-1a2b3c
# refused_as <shown name> <args...>: refused (exit 1), naming the branch it read; for calls whose last argument isn't the branch.
refused_as() {
  local shown="$1" output status
  shift
  output="$(bash "$SCRIPT" "$@" 2>&1)"
  status=$?
  if [[ $status -eq 1 && "$output" == "Branch name '${shown}' does not match"* ]]; then
    PASSED=$((PASSED + 1))
    echo "ok   - $* is refused, read as branch '$shown'"
  else
    FAILED=$((FAILED + 1))
    echo "FAIL - $* should be refused as branch '$shown': exit $status"
    while IFS= read -r line; do echo "       | $line"; done <<< "$output"
  fi
}
# A missing or empty --author, or a missing branch, is a refusal with the usual message, never a shift error or an author read as the branch.
refused_as "" --pr --author
refused_as "" --pr --author ''
refused_as "" --pr --author 'dependabot[bot]'
expect refused --pr --author '' 'dependabot/a|b'
# --author means something only after --pr: before it, it's the branch.
refused_as "--author" --author 'dependabot[bot]' 'dependabot/a|b'
# --author names only who opened the PR: it never lets another name through.
expect refused --pr --author 'dependabot[bot]' claude/x
expect refused --pr --author 'dependabot[bot]' patch-1
# Shell-unsafe or malformed names stay refused for a push.
expect refused dependabot/
expect refused dependabot/a/
expect refused dependabot//a
expect refused Dependabot/x
expect refused $'dependabot/a\nb'
expect refused "dependabot/x;rm"
# shellcheck disable=SC2016 # the \$( is the point: a literal name, not an expansion
expect refused 'dependabot/$(x)'
expect refused "dependabot/a b"
# The rest of the shell-unsafe characters, and a non-ASCII letter.
# shellcheck disable=SC2016 # the backticks are the point: a literal name, not a substitution
expect refused 'dependabot/a`x`'
expect refused "dependabot/a'b"
expect refused 'dependabot/a"b'
expect refused 'dependabot/a|b'
expect refused 'dependabot/a&b'
LC_ALL=C expect refused 'dependabot/aé'
# The script checks in ASCII whatever the caller's locale: in some UTF-8 locales (en_US.UTF-8 with glibc) bash's [a-z] and [A-Za-z] also match an
# accented letter, so a slug like fix/1-café would pass the hook there and be refused in CI. Run the non-ASCII names under every UTF-8 locale this
# machine has where a bare [a-z] does match 'é' (the ones that would let them through), and under C.UTF-8 and en_US.UTF-8 when present.
utf8_locales=()
widening=0
while IFS= read -r loc; do
  case "$loc" in *[Uu][Tt][Ff]8 | *[Uu][Tt][Ff]-8) ;; *) continue ;; esac
  if LC_ALL="$loc" bash -c '[[ é =~ ^[a-z]$ ]]' 2>/dev/null; then
    utf8_locales+=("$loc")
    widening=$((widening + 1))
  elif [[ "$loc" == C.* || "$loc" == en_US.* ]]; then
    utf8_locales+=("$loc")
  fi
done < <(locale -a 2>/dev/null)
# ${a[@]+"${a[@]}"}: an empty array is no unbound variable under set -u in bash before 4.4.
for loc in ${utf8_locales[@]+"${utf8_locales[@]}"}; do
  for name in fix/1-café feature/2-naïve chore/résumé 'dependabot/aé' 'dependabot/npm/é'; do
    LC_ALL="$loc" expect refused "$name"
  done
  LC_ALL="$loc" expect allowed fix/1-cafe
done
if ((widening == 0)); then
  echo "skip - no UTF-8 locale here where [a-z] matches an accented letter, so the names above prove nothing about the locale; the pin below still does"
fi
echo "     (non-ASCII names checked under ${#utf8_locales[@]} UTF-8 locale(s), $widening where [a-z] matches 'é': ${utf8_locales[*]:-none})"
# The pin, which holds on any machine: the script exports LC_ALL=C before its first regex match, so no match runs in the caller's locale.
first_match=$(grep -n '=~' "$SCRIPT" | head -n 1 | cut -d: -f1)
export_line=$(grep -nx 'export LC_ALL=C' "$SCRIPT" | head -n 1 | cut -d: -f1)
if [[ -n "$export_line" && -n "$first_match" ]] && ((export_line < first_match)); then
  PASSED=$((PASSED + 1))
  echo "ok   - the script exports LC_ALL=C (line $export_line) before its first regex match (line $first_match)"
else
  FAILED=$((FAILED + 1))
  echo "FAIL - the script must export LC_ALL=C before its first regex match: export at line ${export_line:-none}, first match at line ${first_match:-none}"
fi
# The --pr form takes them from dependabot[bot] alone.
expect allowed --pr --author 'dependabot[bot]' 'dependabot/a|b'
expect allowed --pr --author 'dependabot[bot]' 'dependabot/aé'
expect refused --pr 'dependabot/a|b'
expect refused --pr --author someone 'dependabot/aé'
# A refused name's control characters are printed escaped, on the one line.
expect refused --pr $'dependabot/a\nb'
expect refused $'claude/x\n::error::y'
expect refused $'chore/a\e[31mb'

# Branches Actions create are allowed only as a PR's head.
for branch in docs/release-notes docs/backfill-blog-posts; do
  expect allowed --pr "$branch"
  expect refused "$branch"
done
# Only those exact names: another docs/ branch is still refused.
expect refused --pr docs/release-notes-pr-12
expect refused --pr docs/fix-typo

echo
echo "$PASSED passed, $FAILED failed"
[[ $FAILED -eq 0 ]]
