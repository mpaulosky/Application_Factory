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
- `docs/adr/`: the Factory's decisions. `docs/Instructions.md`: the steps for setting up a new Application or standardizing an existing one.
- `.github/workflows/template-tests.yml`: the Factory's only workflow. It runs `test.sh` and the Sandcastle check on every PR.
- `~/.claude/skills/repo-ci-baseline` is a symlink to the skill here, so editing it changes what `apply.sh` copies everywhere.

The Factory doesn't carry the Baseline itself: no `.sandcastle/`, hooks, `scripts/gate.sh` or `package.json`. Don't add them without an ADR.

## Commands

```bash
.claude/skills/repo-ci-baseline/test.sh                       # all the Template's tests; needs actionlint, zizmor, yamllint, shellcheck, markdownlint-cli2, node, git
.claude/skills/repo-ci-baseline/tests/sandcastle-check.test.sh # Sandcastle's type-check and tests on a throwaway Application; needs pnpm and the network
.claude/skills/repo-ci-baseline/apply.sh --dry-run <repo>      # what Apply would write into an Application; writes nothing
```

CI runs both test scripts. Run `test.sh` before every push; the second is slower, so run it when you changed `template/**/.sandcastle/`
or the Seed `package.json`.

## Working on the Template

- Edit the Template's file, never an Application's copy of an Owned file; the next Apply overwrites it. Put a fix an Application needed into
  `template/owned/` first.
- A change to an Owned file reaches the Fleet by a Rollout (`rollout.sh`), not by itself. Say in the PR how it should roll out.
- Scaffolded code in an Application is written once (ADR 0002); the Factory doesn't keep it in sync.
- Update the matching file in `references/` when behaviour changes, and add a test to `test.sh` or `tests/`.

## Git workflow

Branches are `feature/{issue}-{slug}`, `fix/{issue}-{slug}`, `hotfix/{issue}-{slug}` or `chore/{slug}`. Commit subjects are
`<type>(<scope>): <Summary>`, with the scope `baseline` for the Template. Work in a worktree, not on `main`.

Markdown is linted with `.markdownlint-cli2.jsonc` (lines up to 200 characters).
