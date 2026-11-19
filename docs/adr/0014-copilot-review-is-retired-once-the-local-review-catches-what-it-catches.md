---
status: proposed
---
# Copilot review is retired once the local review catches what it catches

Copilot's review of a pull request finds problems the local review (ADR 0010) misses, and each one costs a fix commit, a new local review and a new CI run. The research note
[`docs/research/copilot-review-alignment.md`](../research/copilot-review-alignment.md) inventories those escapes: 19 findings on four pull requests that had a clean local review. This ADR builds on
ADR 0008 (a Claude review where Copilot can't review a PR; `claude-review.yml`'s header gives the rule) and ADR 0010. It was first written as the second decision of ADR 0011
and split out so that ADR 0011 holds only what was built; it stays proposed until the replay below is run.

## Decision

The `main-rules` ruleset requests a Copilot review on every push to a pull request (`copilot_code_review`). It is removed, through `github-settings.sh` and the Fleet's `main-rules.json`, only after the
local review catches the kinds of finding Copilot raised on past pull requests, measured by replaying those pull requests' diffs through the review and comparing (the research note's plan, items 1 to
6). Until then Copilot's review stays on, and every finding it makes becomes a test case for the local review. The removal is its own pull request, with the replay's numbers in the description, and
that pull request marks this ADR accepted.

## Considered Options

- **Remove Copilot review when a replay shows the local review catches its findings** (chosen): the backstop goes only when the numbers say it is no longer needed.
- **Remove Copilot review now**: rejected; the research note found 19 findings on four pull requests that had a clean local review. Removing the backstop before the review improves loses those
  catches.
- **Keep Copilot review for good**: it costs review latency and Copilot usage on every push in the Fleet, and each finding it makes costs a fix cycle that a better local review would have saved.

## Consequences

- Nothing changes until the replay is run: `copilot_code_review` stays in `main-rules`, and PR Auto-Merge (ADR 0008) keeps waiting for Copilot's review or Claude Review as its backup.
- The removal is more than the ruleset entry. `claude-review.yml` skips a head while Copilot can review it, and PR Auto-Merge (`pr-automerge.yml`) asks Copilot itself, once per PR,
  and waits on the ruleset's per-push request only while one shows. Without the ruleset's request a PR still reaches a review, but Copilot is still asked once. The removal pull request
  settles whether that ask stays and what "Copilot can review" means once the ruleset sends no request. Those workflows are Owned Template files, so the change is made in
  `template/owned/` and reaches the Fleet by a Rollout (`rollout.sh`); the ruleset change goes through `main-rules.json` and `github-settings.sh --fix`.
- Removing `copilot_code_review` later reduces review latency and the Fleet's Copilot usage; it also removes a second pair of eyes. The replay's recall number is what decides, not this ADR.
