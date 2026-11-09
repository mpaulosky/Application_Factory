---
status: proposed
---
# The Factory reviews every branch before it is pushed

Every Application reviews its branch on the developer's machine before the push, and its gate refuses a push without a clean review. The Factory did not: ADR 0006 and ADR 0008 left the
local review
out ("not carried yet"), so a bad change reached GitHub first and Copilot or Claude Review found it afterwards. The Factory's `pre-push` hook is a copy of the Template's and still says "a
push never
skips the review", but the Factory's `scripts/gate.sh` has no review step to skip. The Factory now carries the review, and its gate refuses a push without one, exactly as an Application's does.
The Factory ships this code to every Application, so a mistake here costs more than anywhere else.

The Factory carries the review's own pieces, not the rest of Sandcastle:

| Carried | How |
| --- | --- |
| `scripts/review.sh` (the ledger's command) and the gate's "Local review" step in `scripts/gate.sh` | copy; the gate step as the Template's |
| `.sandcastle/lib/review-ledger.mts`, `security-record.mts`, `errors.mts`, `host-limit.mts`, `plain-text.mts` and their tests | copies |
| `.claude/skills/pre-push-review/` (`SKILL.md`, `lenses.md`, `review-bar.md`) and `.claude/agents/pre-push-reviewer.md` | copies |
| `.sandcastle/lib/repo-config.mts` (`REPO_SLUG`, `NOT_OWNED_BASELINE_PATHS`) | the Factory's own, as an Application's is |
| `package.json`, `pnpm-workspace.yaml` and the lockfile, for `tsx` and `zod` | the Factory's own, minimal |
| `.security/` in `.gitignore` | the Factory's own line |

Still not carried: the rest of Sandcastle (the host pipeline, roles, prompts), `hardening-review`, and `scripts/watch-pr.sh`.

## Considered Options

- **Carry the review's own pieces** (chosen): the mechanism the Fleet already trusts, byte for byte, with `scripts/gate.sh` catching drift. The ledger's import closure is five modules and
  four test files, and its only
  external packages are `tsx` and `zod`, so nothing else from Sandcastle is needed.
- **A wrapper over the Template's files in place**, with no copies: it needs its own tool install and path handling (the ledger expects repo-relative paths), and that custom code would have
  no test.
- **The skill only, run by hand** (`/review-changes`): nothing enforces it, so a hurried push skips it, which is the failure this ADR exists to stop.
- **Carry all of Sandcastle**: rejected by ADR 0008; the host pipeline would fail or do nothing in the Factory.

## Consequences

- Every push of a branch with a new hunk needs a review: the ledger is keyed by the branch's hunks against its merge base with `origin/main`, a clean merge of `main` keeps the review, and a
  new commit needs
  a new one. Its exemptions cover only a re-Apply branch (`chore/reapply-baseline`), which the Factory never makes, so a docs-only or ADR-only branch is reviewed like any other. The
reviewers run on Sonnet
  at high effort and use your Claude usage; that cost buys the guarantee, and an exemption list in a repo that has drifted before is what would rot.
- `GATE_SKIP_REVIEW=1` skips the check for gate runs started by hand while iterating, never for a push: the `pre-push` hook runs the gate without it. Copilot and Claude Review stay as a
  backstop on the PR.
- The Factory gets a `package.json`, reversing CLAUDE.md's "no `package.json`" for this one reason. Its `dependabot.yml` gains an `npm` block (grouped as the `github-actions` one is, minor
  and patch only), so the
  review's dependencies are watched; this amends ADR 0009's "`github-actions` only". The `Template tests` workflow already installs pnpm for the Sandcastle check.
- `scripts/gate.sh` copies the review's files as it copies the hooks, and `.sandcastle/lib/*.test.mts` run in CI. The implementing PRs say how: the Template's Owned originals are already
  tested in the
  Sandcastle check, so the copies in the Factory's root need their own run only to catch a copy that works differently from its original.
- A finding in a Template file goes to the Template's file, not to an edit of its copy, as for every other copy.
- Nothing is rolled out: no Owned Template file changes.
