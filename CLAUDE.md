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
- `.github/workflows/template-tests.yml`: the Factory's own workflow (named `repo-ci-baseline Template`, job `Template tests`). It runs `test.sh` and the Sandcastle check on every PR.
- `.github/hooks/` (`pre-commit`, `pre-push`, `post-checkout`), `scripts/check-branch-name.sh`, `.github/workflows/{claude-review,pr-automerge,pr-review-submitted}.yml` and
  `.github/scripts/post_claude_review.py`, and the issue and PR process (ADR 0009): `.github/ISSUE_TEMPLATE/*`, `.github/pull_request_template.md`, `.github/workflows/pr-title.yml` and
  `scripts/check-pr-title.sh`, and `.github/workflows/lint-{markdown,yaml,actions}.yml`, and the local review (ADR 0010): `scripts/review.sh`, five `.sandcastle/lib/` modules with three tests,
  `.claude/skills/pre-push-review/`, `.claude/agents/pre-push-reviewer.md` and `.security/README.md`: copies of the Template's Owned files. `.github/dependabot.yml`, `.yamllint.yml` and
  `.markdownlint-cli2.jsonc` are the Factory's own (ADR 0009). Edit the Template's file and copy it over; `scripts/gate.sh` fails when a copy drifts.
  The review's own, non-copy files are `.sandcastle/lib/repo-config.mts` (`REPO_SLUG`, `NOT_OWNED_BASELINE_PATHS`), `package.json`, `pnpm-workspace.yaml`,
  `pnpm-lock.yaml` and the `.gitignore` lines for `node_modules/` and `.security/`.
- `scripts/gate.sh`: the Factory's own pre-push gate, run by CI too. It runs its own test (`scripts/tests/gate.test.sh`) and the review's three tests, checks those copies,
  lints the whole repo's Markdown and YAML as the lint workflows do, requires a clean local review of the branch
  (`scripts/review.sh check`; CI and hand runs set `GATE_SKIP_REVIEW=1`), then runs `test.sh`. [ADR
  0006](docs/adr/0006-the-factory-carries-the-git-hooks-and-a-gate.md).
- `~/.claude/skills/repo-ci-baseline` is a symlink to the skill here, so editing it changes what `apply.sh` copies everywhere.

The Factory carries the git hooks and a gate of its own (ADR 0006), Claude Review with PR Auto-Merge (ADR 0008), the issue and PR process (ADR 0009) and the local review
(ADR 0010), not the rest of the Baseline: no other `.sandcastle/` code, .NET workflows or release workflows; its `package.json` holds only `tsx` and `zod`. Don't add more without an ADR.

## Commands

```bash
scripts/gate.sh                                               # the pre-push gate: its test, the review's tests, the copies' drift check, lint, the local review check, then test.sh
.claude/skills/repo-ci-baseline/test.sh                       # all the Template's tests; needs actionlint, zizmor, yamllint, shellcheck, markdownlint-cli2, node, git
.claude/skills/repo-ci-baseline/tests/sandcastle-check.test.sh # Sandcastle's type-check and tests on a throwaway Application; needs pnpm and the network
.claude/skills/repo-ci-baseline/apply.sh --dry-run <repo>      # what Apply would write into an Application; writes nothing
```

CI runs `scripts/gate.sh` and the Sandcastle check, so a push from a clone without hooks is gated too, except for the local review check:
CI has no review ledger, so it sets `GATE_SKIP_REVIEW=1` (ADR 0010). The pre-push hook runs `scripts/gate.sh` on every push; the second is slower, so run it when
you changed `template/**/.sandcastle/`
or the Seed `package.json`.

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
Start a branch with `git worktree add -b <branch> ../Application_Factory-worktrees/<folder> origin/main`, where `<folder>` is the branch with `/` as `-`.

Markdown is linted with `.markdownlint-cli2.jsonc` (lines up to 200 characters).
