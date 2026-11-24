# Reading the Apply report

`apply.sh [--dry-run] <repo>` prints one report, the same shape for a dry run and a real run.
Each section maps to an action in Adapt ([adapt.md](adapt.md)).

| Section | Meaning | Action |
| --- | --- | --- |
| Placeholders | The `OWNER`, `REPO` and `SOLUTION` values, from `origin` and the single root `*.slnx`; `?` when not found | A `?` leaves those placeholders unfilled |
| Owned files added | The repo lacked them | None; review them in the Apply commit |
| Owned files overwritten | **Drift**: the repo's copy differed in content or execute bit | Each one is drift to drop, or a local fix to move into the Template first |
| Owned files already identical | Count only | None |
| Retired Owned files removed | A [retired](#retired-owned-files) file the repo's copy of was identical to a version the Template shipped; a dry run says "would be removed" | None; the deletion is in the Apply commit |
| Retired Owned files kept | A retired file whose copy differs from every shipped version, or is a symlink, a folder or reached through a symlinked folder ("not a regular file in the repo") | Delete it, or keep it as the repo's own |
| Sandcastle files left out | Count of the [Sandcastle section](sandcastle.md)'s files skipped because the repo has no `.sandcastle/` folder | None; to add them, create `.sandcastle/` and Apply again |
| Seed files written | New, placeholders filled (`{{YEAR}}` is always this year) | Fill any left in "unfilled placeholders", which counts only `{{OWNER}}`, `{{REPO}}` and `{{SOLUTION}}` |
| Seed files skipped | The repo has its own copy, kept as is | Compare with the Template's and merge what the repo lacks |
| Leftovers | Files under `.github/workflows`, `.github/hooks` or `.github/scripts` the Template doesn't carry | Delete, keep as repo-specific, or move into the Template |
| Conflicts | Known clashes: a `.markdownlint.*` file that overrides the Template's rules, squad still installed, a `.sandcastle/check.sh` the repo uses as its sandbox gate (Apply keeps it; rename it and Apply again, or, if nothing runs it as a gate, put the text that names check.sh on a comment line of its own that names neither `GATE_COMMAND` nor `GATE_SCRIPT`, or more than ten lines after them; a gate named through another constant isn't followed, so rename it before Applying) | Fix before landing |

## Guards

A real run refuses a dirty worktree (untracked files count), a detached `HEAD`, and `main`, `preview` or `dev`, so the Apply commit is exactly the script's output on a work branch.
A repo with no commits yet is allowed on any branch.
A dry run has no guards and writes nothing, not even `core.hooksPath`.

The Sandcastle section (`.sandcastle/`, `package.json`, `pnpm-workspace.yaml`, and the local review's skills (`pre-push-review` and `hardening-review`), agent
and `scripts/review.sh`) is written only into a repo with a `.sandcastle/` folder or no commits yet
([sandcastle.md](sandcastle.md#when-apply-adds-it)).

## Retired Owned files

`retired.txt`, beside `apply.sh`, lists the Owned files the Template no longer ships: one `<sha256>  <path>` line, as `sha256sum` prints it, for every version of the file the
Template shipped (its header gives the command that lists them). Apply deletes the repo's copy only when it is a regular file, in a folder that resolves inside the repo, whose
SHA-256 is one of those; a copy already gone is not mentioned. A Rollout deletes a retired file in every Application the same way, and `reapply.sh` commits the deletion with the
rest of Apply's output.

- A retired path still under `template/owned/` or `template/seed/`, or one that is absolute or has an empty, `.`, `..` or `.git` part, stops Apply before it writes anything;
  `test.sh` fails on the first too.
- The ledger's `RETIRED_PATHS` (`.sandcastle/lib/review-ledger.mts`) names the same paths, which `test.sh` checks: a re-Apply that deletes one of them, where `main` had a regular
  file, passes the gate's review check as one that writes Owned files does. The Template's own review of the retirement covers it.
- `rollout.sh --auto` lists a deleted retired file in the review's prompt with the other Owned files the branch changes.
- Retiring a file the Factory copies (`scripts/gate.sh`'s copies list) also takes it out of that list and deletes the Factory's copy in the same PR, or the gate's drift check fails
  on the missing source.

## What it never does

Delete anything but a retired Owned file identical to a version the Template shipped.
Leftovers and conflicts are reported for a person to decide, because a workflow the Template doesn't know may be a real feature (TicketManager's `generate-blog-posts-json.yml`).
