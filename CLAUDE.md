# CLAUDE.md

The Application Factory is the source of truth for every Application: the `repo-ci-baseline` skill with its Template (the CI, release and
git-hook Baseline every Application carries), the Fleet registry, and the scripts that Apply, Reapply, Roll out, check Status and Land
it. Read [`GLOSSARY.md`](GLOSSARY.md) for the terms (Factory, Application, Template, Baseline, Starter, Apply, Fleet, Drift) and use them as written.
Decisions are in [`docs/adr/`](docs/adr/); the Factory's ownership of the Template is ADR 0005.

## Layout

- `.claude/skills/repo-ci-baseline/`: the skill. `SKILL.md` and `CONTEXT.md` describe it; `template/owned/` (overwritten on every Apply) and
  `template/seed/` (written only if missing) hold the Template's files; `references/` and its own `docs/adr/` describe the parts;
  `github/repos.txt` is the Fleet registry; `apply.sh`, `reapply.sh`, `rollout.sh`, `status.sh`, `land.sh` and `github-settings.sh` are the scripts;
  `test.sh` and `tests/` test all of it.
- `.claude/skills/`: also the shared skills (`build-repair`, `dotnet-*`, `remove-squad`, `review-changes`), and `starting-points/`: the optional app-stack files
  (`.aspire/`, `.github/instructions/`, the `beast` agent, the `code-review` skill) that `docs/Instructions.md` Part C says to copy into an Application. ADR 0007.
- `.claude/rules/sandcastle.md`: the rules for writing the Template's Sandcastle code (loaded when you edit `template/**/.sandcastle/**`). The tests about the Template itself
  (`owned-paths`, `template`, `seed-config` and `template-prompts`) are in the skill's `tests/sandcastle/`; `tests/sandcastle-check.test.sh` copies them into a repo the Template was
  Applied to and runs them there. They ran in `dotfiles` before the Template moved.
- `docs/adr/`: the Factory's decisions. `docs/Instructions.md`: the steps for setting up a new Application or standardizing an existing one.
- `docs/research/`: dated research notes that the ADRs cite as evidence; they decide nothing, an ADR does.
- `docs/SECURITY.md`: how to report a vulnerability, and where an accepted security finding's reason goes (ADR 0009).
- `.github/workflows/template-tests.yml`: the Factory's own workflow (named `repo-ci-baseline Template`, the required job `Template tests`). It runs the gate one part per job in parallel
  (`GATE_PART`: `self`, `static`, `template`, `sandcastle`, ADR 0012), and
  `Template tests` passes only when every part did. `.github/actions/template-tools/` installs the tools each job needs.
- `.github/hooks/` (`pre-commit`, `pre-push`, `post-checkout`), `scripts/check-branch-name.sh`, `.github/workflows/{claude-review,pr-automerge,pr-review-submitted}.yml` and
  `.github/scripts/post_claude_review.py`, and the issue and PR process (ADR 0009): `.github/ISSUE_TEMPLATE/*`, `.github/pull_request_template.md`, `.github/workflows/pr-title.yml` and
  `scripts/check-pr-title.sh`, and `.github/workflows/lint-{markdown,yaml,actions}.yml`, and the local review (ADR 0010): `scripts/review.sh`, five `.sandcastle/lib/` modules with three tests,
  `.claude/skills/pre-push-review/`, `.claude/agents/pre-push-reviewer.md` and `.security/README.md`: copies of the Template's Owned files. `.github/dependabot.yml`, `.yamllint.yml` and
  `.markdownlint-cli2.jsonc` are the Factory's own (ADR 0009). Edit the Template's file and run `scripts/gate.sh --sync` to copy it over; `scripts/gate.sh` fails when a copy drifts.
  The review's own, non-copy files are `.sandcastle/lib/repo-config.mts` (`REPO_SLUG`, `NOT_OWNED_BASELINE_PATHS`), `package.json`, `pnpm-workspace.yaml`,
  `pnpm-lock.yaml` and the `.gitignore` lines for `node_modules/` and `.security/`.
- `.claude/settings.json` and `scripts/lint-edited-file.sh` (a copy of the Template's Owned file): the edit-time lint (ADR 0011). The settings file holds one `PostToolUse` hook
  that lints each Markdown or YAML file Claude writes; the Template carries the script but not the settings file.
- `scripts/gate.sh`: the Factory's own pre-push gate, run by CI too; its header lists the steps (its test and the review's three tests, the copies' drift check, the bare-`#N` check,
  the Git-status check, lint, shellcheck of the changed shell files, the local review check, then `test.sh`, which it skips for a tree that already passed it,
  then the Sandcastle check when the branch changed a file it reads).
  `GATE_PART=<part>` runs one part (CI's jobs; the pre-push hook drops it). [ADR 0006](docs/adr/0006-the-factory-carries-the-git-hooks-and-a-gate.md), [ADR
  0012](docs/adr/0012-the-template-tests-workflow-runs-the-gate-in-parallel-parts.md).
- `~/.claude/skills/repo-ci-baseline` is a symlink to the skill here, so editing it changes what `apply.sh` copies everywhere.

The Factory carries the git hooks and a gate of its own (ADR 0006), Claude Review with PR Auto-Merge (ADR 0008), the issue and PR process (ADR 0009), the local review
(ADR 0010) and the edit-time lint (ADR 0011), not the rest of the Baseline: no other `.sandcastle/` code, .NET workflows or release workflows; its `package.json` holds only `tsx` and `zod`.
Don't add more without an ADR.

## Commands

```bash
scripts/gate.sh --sync                                        # copy the Template's files over the Factory's copies of them (after editing the Template)
scripts/gate.sh --quick                                       # while iterating: the gate up to and including shellcheck of the shell files the branch changed, without the local review check and test.sh (about a minute); never for a push
scripts/gate.sh                                               # the pre-push gate (needs pnpm on the first run); GATE_NO_CACHE=1 runs test.sh again on a tree that already passed it
.claude/skills/repo-ci-baseline/test.sh                       # all the Template's tests; needs actionlint, zizmor, yamllint, shellcheck, markdownlint-cli2, node, git, jq
.claude/skills/repo-ci-baseline/tests/sandcastle-check.test.sh # Sandcastle's type-check and tests on a throwaway Application; needs pnpm and the network
.claude/skills/repo-ci-baseline/template/owned/.claude/skills/hardening-review/scripts/mutate.py .claude/skills/repo-ci-baseline/tests/gh_repo/mutations.json  # ~1 min, from the repo root, needs uvx: edits check_gh_repo.py one mutation at a time and restores it; commit the scanner change first (a dirty target is refused)
.claude/skills/repo-ci-baseline/template/owned/.claude/skills/hardening-review/scripts/mutate.py .claude/skills/repo-ci-baseline/tests/mutations/cleanup-work-issue.json  # same, for cleanup.sh and work-issue.sh; ~30 min, run in the background after committing (test.sh checks that the spec still fits the code)
.claude/skills/repo-ci-baseline/apply.sh --dry-run <repo>      # what Apply would write into an Application; writes nothing
```

CI runs `scripts/gate.sh` in parts, so a push from a clone without hooks is gated too, except for the local review check:
CI has no review ledger, so it sets `GATE_SKIP_REVIEW=1` (ADR 0010). The pre-push hook runs `scripts/gate.sh` on every push, and the gate runs the Sandcastle check when you changed
`template/**/.sandcastle/` or the Seed `package.json` (the check needs the network and takes a few minutes; its pass is remembered for a clean tree). `GATE_PART=sandcastle scripts/gate.sh` runs it alone.

## Working on the Template

- Edit the Template's file, never an Application's copy of an Owned file; the next Apply overwrites it. Put a fix an Application needed into
  `template/owned/` first.
- A change to an Owned file reaches the Fleet by a Rollout (`rollout.sh`), not by itself. Say in the PR how it should roll out.
- Scaffolded code in an Application is written once (ADR 0002); the Factory doesn't keep it in sync.
- Update the matching file in `references/` when behaviour changes, and add a test to `test.sh` or `tests/`.

## Git workflow

Branches are `feature/{issue}-{slug}`, `fix/{issue}-{slug}`, `hotfix/{issue}-{slug}` or `chore/{slug}`. Commit subjects are
`<type>(<scope>): <Summary>`, with the scope `baseline` for the Template.

Commit in a linked worktree under `../Application_Factory-worktrees/`: `pre-commit` refuses a commit in the primary checkout, and `pre-push` refuses a push
to `main`, a bad branch name, a dirty tree or a branch behind `origin/main`. In each clone, turn the hooks on once with `git config core.hooksPath .github/hooks`.
Start a branch with `git worktree add --no-track -b <branch> ../Application_Factory-worktrees/<folder> origin/main`, where `<folder>` is the branch with `/` as `-`.

Markdown is linted with `.markdownlint-cli2.jsonc` (lines up to 200 characters).
