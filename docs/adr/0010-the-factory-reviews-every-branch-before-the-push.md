---
status: accepted
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
| `.sandcastle/lib/review-ledger.mts`, `security-record.mts`, `errors.mts`, `host-limit.mts`, `plain-text.mts` and the three tests that stand alone (`review-ledger`, `security-record`, `plain-text`) | copies |
| `.claude/skills/pre-push-review/` (`SKILL.md`, `lenses.md`, `review-bar.md`) and `.claude/agents/pre-push-reviewer.md` | copies |
| `.sandcastle/lib/repo-config.mts` (`REPO_SLUG`, `NOT_OWNED_BASELINE_PATHS`) | the Factory's own, as an Application's is |
| `package.json`, `pnpm-workspace.yaml` and the lockfile, for `tsx` and `zod` | the Factory's own, minimal |
| `.security/README.md` | copy |
| `.gitignore` lines `node_modules/`, `.security/*` and `!.security/README.md` | the Factory's own; `scripts/review.sh` installs `tsx` and `zod` into `node_modules/`, and an untracked one would make the worktree dirty, which `recordReview` and `pre-push` refuse; the ledger's test reads the checkout's `.gitignore` and requires the two `.security` lines |

`host-limit.test.mts` stays out: it spawns a child that imports `step-limit.mts`, which pulls in `status.mts` and more of the host pipeline, and the Template's Sandcastle check already runs
it there.
Still not carried: the rest of Sandcastle (the host pipeline, roles, prompts), `hardening-review`, and `scripts/watch-pr.sh`.

## Considered Options

- **Carry the review's own pieces** (chosen): the mechanism the Fleet already trusts, byte for byte, with `scripts/gate.sh` catching drift. The ledger's import closure is five modules, and
  its three stand-alone tests need nothing beyond them and two `repo-config.mts` exports; its only external packages are `tsx` and `zod`,
  so nothing else from Sandcastle is needed.
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
- `scripts/gate.sh` copies the review's files as it copies the hooks, and the three copied tests run in CI. The Template's Owned originals are already tested in the Sandcastle check, so the
  copies in
  the Factory's root need their own run only to catch a copy that works differently from its original.
- CI cannot prove a local review: `Template tests` runs `scripts/gate.sh` in a fresh checkout with no `.git/review/` ledger, so that workflow's gate step sets `GATE_SKIP_REVIEW=1` for its
  own invocation
  only. The `pre-push` hook still removes the variable, so a push never skips the review; CI keeps the gate's other checks, and the local review stays a local guarantee.
- A finding in a Template file goes to the Template's file, not to an edit of its copy, as for every other copy.
- Implementing this ADR changes a few Owned Template files (the `pre-push` hook and its test, which drop `GATE_SKIP_SELF_TEST` as they drop `GATE_SKIP_REVIEW`; the
  `pre-push-review` skill's wording; the `.sandcastle/lib` review modules), so they reach the Fleet by the next Rollout (`rollout.sh`).
  None is urgent: an Application's gate has no `GATE_SKIP_SELF_TEST`.
