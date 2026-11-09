---
status: accepted
---
# The Factory runs Claude Review and PR Auto-Merge

ADR 0006 gave the Factory the git hooks and a gate and left the review layer for a later decision. The Factory now carries `claude-review.yml`, `pr-automerge.yml`, `pr-review-submitted.yml` and
`.github/scripts/post_claude_review.py` as copies of the Template's Owned files, the way `dotfiles` carried them. A Factory PR gets a Claude review where Copilot can't review it, and merges
itself once its required check and its review threads are clean, instead of waiting for a hand merge. `scripts/gate.sh` fails when a copy differs from the Template's file, as it does for the hooks.

The Template's PR Auto-Merge starts on completion of the workflows it names, one of which is `repo-ci-baseline Template`. The Factory's test workflow, `template-tests.yml`, now has that name
(its job is still the "Template tests" check), so the copy stays byte for byte the Template's.

## Considered Options

- **Keep merging by hand**: nothing to maintain, but the Factory ships the review layer to every Application and doesn't use it, and a Factory PR waits on a person.
- **Copy the files** (chosen): the files `dotfiles` had, kept in step by the gate. Edit the Template's file and copy it over.
- **Copy Sandcastle too**: not now. `sandcastle.yml` needs `.sandcastle/`, a `package.json` and the `.github/ci/sandcastle-skills.txt` pin, which the Factory doesn't carry; the workflow alone would fail.
  It needs its own decision.

## Consequences

- The repo needs the `CLAUDE_CODE_OAUTH_TOKEN` secret for Claude Review to run; without it the review job skips, as in any Baseline repo.
- The branch ruleset on `main` should require the "Template tests" check, or PR Auto-Merge counts a PR with no required check as ready. Set it with `github-settings.sh` or by hand.
- A change to one of these files starts in `template/owned/` and is copied over; the gate says so when a copy has drifted.
- Not carried: Sandcastle, the local review (`/pre-push-review`, `scripts/review.sh`) and `scripts/watch-pr.sh`.
