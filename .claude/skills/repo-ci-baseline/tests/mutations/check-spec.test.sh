#!/usr/bin/env bash
# Tests for check-spec.py, on a throwaway tree: each way a spec can stop fitting its code is found, and a good spec passes.
# Usage: bash tests/mutations/check-spec.test.sh
# shellcheck disable=SC2034 # $out and $st are read inside the checks' eval strings
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
checker="$here/check-spec.py"
work="$(mktemp -d)" || exit 1
[[ -n "$work" && -d "$work" ]] || exit 1
trap 'command rm -rf -- "$work"' EXIT
PASSED=0; FAILED=0
check() { if eval "$2"; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else FAILED=$((FAILED + 1)); echo "FAIL - $1"; fi; }

mkdir -p "$work/root/scripts" "$work/specs"
printf '%s\n' '#!/usr/bin/env bash' 'echo one' 'echo two' 'echo two again' > "$work/root/scripts/a.sh"
# spec <mutation JSON...>: the spec file specs/s.json with those mutations
spec() { local IFS=,; printf '{"test": ["true"], "timeout": 5, "mutations": [%s]}\n' "$*" > "$work/specs/s.json"; }
mut() { printf '{"path": "scripts/a.sh", "old": %s, "new": %s, "why": %s}' "$1" "$2" "${3:-\"a reason\"}"; }
run_checker() { out="$(python3 -I "$checker" "$work/root" "$work/specs" 2>&1)"; st=$?; }

spec "$(mut '"echo one"' '"echo 1"')"
run_checker
check "a spec that fits passes" "[[ $st == 0 && -z \"\$out\" ]]"
spec "$(mut '"echo nope"' '"x"')"
run_checker
check "an old text that is not in the file is found" "[[ $st == 1 ]] && grep -q 'occurs 0 times' <<<\"\$out\""
spec "$(mut '"echo two"' '"x"')"
run_checker
check "an old text that occurs twice is found" "[[ $st == 1 ]] && grep -q 'occurs 2 times' <<<\"\$out\""
spec "$(mut '"echo one"' '"echo one"')"
run_checker
check "a new text equal to the old is found" "[[ $st == 1 ]] && grep -q 'equals' <<<\"\$out\""
spec "$(mut '"echo one"' '"if then"')"
run_checker
check "a mutant that does not parse is found" "[[ $st == 1 ]] && grep -q 'does not parse' <<<\"\$out\""
spec "$(mut '"echo one"' '"echo 1"' '" "')"
run_checker
check "a mutation without a reason is found" "[[ $st == 1 ]] && grep -q 'no reason' <<<\"\$out\""
spec "$(mut '"echo one"' '"echo 1"')" "$(mut '"echo one"' '"echo 1"')"
run_checker
check "a repeated mutation is found" "[[ $st == 1 ]] && grep -q 'repeated' <<<\"\$out\""
spec '{"path": "scripts/missing.sh", "old": "a", "new": "b", "why": "w"}'
run_checker
check "a path that does not exist is found" "[[ $st == 1 ]] && grep -q 'does not exist' <<<\"\$out\""
spec '{"path": "scripts/a.sh", "old": "echo one", "why": "no new"}'
run_checker
check "a mutation missing a field is reported, not a traceback" "[[ $st == 1 ]] && grep -q 'needs new' <<<\"\$out\" && ! grep -q Traceback <<<\"\$out\""
printf '{"test": [], "timeout": 0, "mutations": []}\n' > "$work/specs/s.json"
run_checker
check "a spec without a test, a timeout or mutations is found" "[[ $st == 1 ]] && grep -q 'needs a .test. argv' <<<\"\$out\" && grep -q 'has no mutations' <<<\"\$out\""
printf '{not json\n' > "$work/specs/s.json"
run_checker
check "a spec that is not JSON is found" "[[ $st == 1 ]] && grep -q 'not valid JSON' <<<\"\$out\""
command rm -f "$work/specs/s.json"
run_checker
check "a folder with no spec is exit 2, not a pass" "[[ $st == 2 ]]"
python3 -I "$checker" >/dev/null 2>&1; st=$?
check "the real specs fit the real code" "[[ $st == 0 ]]"

echo "$PASSED passed, $FAILED failed"
[[ $FAILED == 0 ]]
