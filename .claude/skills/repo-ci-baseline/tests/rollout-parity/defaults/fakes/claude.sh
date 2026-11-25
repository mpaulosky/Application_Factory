#!/usr/bin/env bash
# The default claude: its version, signed in, and a review session that stages no findings (an empty array) in the run
# folder its prompt names, which is beside the TMPDIR rollout.sh gives it. Anything else is a call nobody planned for.
if [[ $# -eq 1 && "$1" == --version ]]; then
  echo "2.1.326 (Claude Code)"
  exit 0
fi
if [[ $# -eq 5 && "$1" == --setting-sources && -z "$2" && "$3 $4 $5" == "auth status --json" ]]; then
  echo '{"loggedIn": true, "authMethod": "claude.ai"}'
  exit 0
fi
if [[ "${1:-}" == -p ]]; then
  printf '[]\n' > "${TMPDIR%/tmp}/run/findings.json"
  echo "Staged no findings."
  exit 0
fi
echo "UNMATCHED claude $* (no default answers it)" >> "$FAKE_GH_LOG"
exit 97
