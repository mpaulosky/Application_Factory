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
  if [[ "$outcome" == allowed && $status -eq 0 && -z "$output" ]] \
    || [[ "$outcome" == refused && $status -eq 1 && "$output" == *"'${*: -1}' does not match"* ]]; then
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
# The --pr form takes any dependabot/* name, as before the hook did: CI quotes it, and a PR must not fail on a character.
expect allowed --pr dependabot/npm_and_yarn/types/node-20~1
expect allowed --pr dependabot/
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
# LC_ALL=C: in a UTF-8 locale bash's [A-Za-z] can match an accented letter, and the hook would take it (it is shell-safe anyway).
LC_ALL=C expect refused 'dependabot/aé'
# The --pr form still takes them: CI quotes the name.
expect allowed --pr 'dependabot/a|b'
expect allowed --pr 'dependabot/aé'
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
