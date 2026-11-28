# CLAUDE.md

The Application Factory is the source of truth for every Application: the `repo-ci-baseline` skill with its Template (the CI, release and
git-hook Baseline every Application carries), the Fleet registry, and the scripts that Apply, Reapply, Roll out, check Status and Land
it. Read [`GLOSSARY.md`](GLOSSARY.md) for the terms (Factory, Application, Template, Baseline, Starter, Apply, Fleet, Drift) and use them as written.
Decisions are in [`docs/adr/`](docs/adr/); the Factory's ownership of the Template is ADR 0005.

## Layout

- `.claude/skills/repo-ci-baseline/`: the skill. `SKILL.md` and `CONTEXT.md` describe it; `template/owned/` (overwritten on every Apply) and
  `template/seed/` (written only if missing) hold the Template's files; `references/` and its own `docs/adr/` describe the parts;
  `github/repos.txt` is the Fleet registry; `apply.sh`, `reapply.sh`, `rollout.sh`, `status.sh`, `land.sh` and `github-settings.sh` are the scripts;
  `status.sh`, `land.sh` and `github-settings.sh` run the C# file-based apps `github/status.cs`, `github/land.cs` and `github/settings.cs`, which include
  `Landing.cs`, `StatusCore.cs`, `LandCore.cs`, `SettingsCore.cs`, `Py.cs`, `Repos.cs` and `Gh.cs` (ADR 0017); `rollout.sh` builds `github/rollout.cs` and runs the
  built dll, which includes `Gh.cs`, `Repos.cs`, `Py.cs`, `RolloutCore.cs` (the plain mode) and the `AutoRollout*.cs` libraries (`--auto`, ADR 0016), so the
  skill's `github/` holds no Python.
  `bench/` holds the Claude Review benchmark, the C# file-based app
  `claude-review-bench.cs` (with `ReviewBench.cs`, `Yaml.cs`, and `Gh.cs` and `Py.cs` from `github/`), which reads `claude-review.yml` with YamlDotNet, its one package,
  pinned in its own `packages.lock.json` and restored from nuget.org alone by its own `NuGet.config` (ADR 0017's Update for mpaulosky/Application_Factory#161);
  `bench/tests/rescore.test.sh` is its `--rescore` parity suite.
  `test.sh` and `tests/` test all of it. The gate and `test.sh` run two checkers in `tests/`: `check-issue-refs.sh` and `check-git-status.sh` run the C# file-based apps
  `check-issue-refs.cs` and `check-git-status.cs`, which include `IssueRefs.cs`, `GitStatusGuards.cs` and `Checker.cs`, each judged by its parity suite (`<checker>.test.sh`).
  `tests/Scripts.Tests/` is the xUnit v3 project for the C# scripts (ADR 0017), the Template's own in `.github/scripts/` among them (in `Template/`, namespace `Baseline`,
  but `Baseline.PostReview` for `post_claude_review.cs`, which carries its own copies of `Gh.cs`'s classes);
  it, `tests/`, `github/`, `bench/` and the Factory's own `scripts/` each hold three empty shield files (`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`), which are not
  Owned; the Template's `.github/scripts/` holds three more, which are. The test project also holds its own `NuGet.config`
  (nuget.org alone) and `packages.lock.json`, which `test.sh` restores against in locked mode (ADR 0017's Update on hardening the C# step).
- `.claude/skills/`: also the shared skills (`build-repair`, `dotnet-*`, `remove-squad`, `review-changes`), the Factory's own `gate-timings` (below), and `starting-points/`: the optional app-stack files
  (`.aspire/`, `.github/instructions/`, the `beast` agent, the `code-review` skill) that `docs/Instructions.md` Part C says to copy into an Application. ADR 0007.
- `.claude/rules/sandcastle.md`: the rules for writing the Template's Sandcastle code (loaded when you edit `template/**/.sandcastle/**`). The tests about the Template itself
  (`owned-paths`, `template`, `seed-config` and `template-prompts`) are in the skill's `tests/sandcastle/`; `tests/sandcastle-check.test.sh` copies them into a repo the Template was
  Applied to and runs them there. They ran in `dotfiles` before the Template moved.
- `docs/adr/`: the Factory's decisions. `docs/Instructions.md`: the steps for setting up a new Application or standardizing an existing one.
- `docs/research/`: dated research notes that the ADRs cite as evidence; they decide nothing, an ADR does.
- `docs/agent-teams.md`: the reference for building Claude Code agent teams (when to use one, setup, spawn prompts, hooks, limits) and what this file's
  rules mean for a team. It is compiled from Claude Code's docs on the date it gives, and agent teams are experimental: check those docs again before relying on a detail.
- `docs/SECURITY.md`: how to report a vulnerability, and where an accepted security finding's reason goes (ADR 0009).
- `.github/workflows/template-tests.yml`: the Factory's own workflow (named `repo-ci-baseline Template`, the required job `Template tests`). It runs the gate one part per job in parallel
  (`GATE_PART`: `self`, `review`, `static`, `template`, `sandcastle`, ADR 0012), with `test.sh` in three shards (`TEST_SH_SHARD`, ADR 0013), and
  `Template tests` passes only when every part did. `.github/actions/template-tools/` installs the tools each job needs.
- `.github/workflows/claude-issues.yml`: the Factory's own workflow (ADR 0018). Claude works the open issues labelled `claude`, up to three at once and skipping blocked ones, and opens a
  draft pull request for each, with a comment naming the other open pull requests from this repository's branches that change the same files and the order to merge them (written
  when its run ends, so an older pull request's comment doesn't learn of a newer one); its header lists the labels it uses.
- `.github/hooks/` (`pre-commit`, `pre-push`, `post-checkout`), `scripts/check-branch-name.sh`, `.github/workflows/{claude-review,pr-automerge,pr-review-submitted}.yml` and
  `.github/scripts/post_claude_review.{cs,py}`, and the issue and PR process (ADR 0009): `.github/ISSUE_TEMPLATE/*`, `.github/pull_request_template.md`, `.github/workflows/pr-title.yml` and
  `scripts/check-pr-title.sh`, and `.github/workflows/lint-{markdown,yaml,actions}.yml`, and the local review (ADR 0010): `scripts/review.sh`, five `.sandcastle/lib/` modules with three tests,
  `.claude/skills/pre-push-review/`, `.claude/agents/pre-push-reviewer.md` and `.security/README.md`: copies of the Template's Owned files. `.github/dependabot.yml`, `.yamllint.yml` and
  `.markdownlint-cli2.jsonc` are the Factory's own (ADR 0009). `.editorconfig`, which holds the shell style shfmt reads (ADR 0015), is a copy too, and so is
  `.claude/rules/script-edits.md`, the checks to make when editing a shell or Python script (ADR 0011), and `global.json`, the .NET SDK (ADR 0017).
  Edit the Template's file and run `scripts/gate.sh --sync` to copy it over; `scripts/gate.sh` fails when a copy drifts.
  The review's own, non-copy files are `.sandcastle/lib/repo-config.mts` (`REPO_SLUG`, `NOT_OWNED_BASELINE_PATHS`), `package.json`, `pnpm-workspace.yaml`,
  `pnpm-lock.yaml` and the `.gitignore` lines for `node_modules/` and `.security/`.
- `.claude/settings.json` and `scripts/lint-edited-file.sh` (a copy of the Template's Owned file): the edit-time lint (ADR 0011). The settings file holds one `PostToolUse` hook
  that lints each Markdown, YAML, shell or Python file Claude writes; the Template carries the script but not the settings file.
- `scripts/gate.sh`: the Factory's own pre-push gate, run by CI too; its header lists the steps (its test, the step timings' test and the review's three tests,
  a warning for a branch past the review's size line, the copies' drift check, the bare-`#N` check, the Git-status check, lint, shellcheck of the changed shell files,
  shfmt of every shell file, ruff of every Python file, `dotnet format whitespace` of every C# file and a build
  of every C# script with warnings as errors but CA2266 and NU1900 (ADR 0017), the local review check, then `test.sh`, which it skips for a tree and .NET SDK that already passed it, then
  the Sandcastle check when the branch changed a file it reads).
  `GATE_PART=<part>` runs one part (CI's jobs; the pre-push hook drops it), and `TEST_SH_SHARD` one shard of `test.sh` in the `template` part only.
  [ADR 0006](docs/adr/0006-the-factory-carries-the-git-hooks-and-a-gate.md), [ADR
  0012](docs/adr/0012-the-template-tests-workflow-runs-the-gate-in-parallel-parts.md), [ADR 0013](docs/adr/0013-ci-runs-test-sh-in-shards-and-the-review-tests-as-their-own-part.md).
- Step timings (mpaulosky/Application_Factory#220): `scripts/gate.sh` and `test.sh` source `scripts/lib/timings.sh` and log each step's wall time and status to
  `docs/timings/gate-timings.tsv` in the primary checkout (every worktree's runs in one log; `.gitignore` leaves `docs/timings/` out; `GATE_TIMINGS=off` logs
  nothing, `GATE_TIMINGS_LOG` names another file), and the gate ends a run with its five slowest steps. `scripts/gate-timings.sh` summarizes the log by
  running the C# file-based app `scripts/gate-timings.cs`, which includes `scripts/GateTimings.cs` (ADR 0017's Update for mpaulosky/Application_Factory#220; `scripts/` holds the three empty
  shield files too), and the `gate-timings` skill (`.claude/skills/gate-timings/`) writes a report from it to `docs/timings/report-YYYY-MM-DD.md`.
  `scripts/tests/timings.test.sh` tests the library and the wrapper, in the gate's `self` part, and `tests/Scripts.Tests/GateTimings/` the summary's rules.
- `~/.claude/skills/repo-ci-baseline` is a symlink to the skill here, so editing it changes what `apply.sh` copies everywhere.

The Factory carries the git hooks and a gate of its own (ADR 0006), Claude Review with PR Auto-Merge (ADR 0008), the issue and PR process (ADR 0009), the local review
(ADR 0010), the edit-time lint (ADR 0011), the shell style (ADR 0015), the issue worker (ADR 0018) and the .NET SDK with the C# its ported scripts need (ADR 0017), not the
rest of the Baseline: no other `.sandcastle/` code, .NET workflows or release workflows; its `package.json` holds only `tsx` and `zod`.
Don't add more without an ADR.

## Commands

```bash
scripts/gate.sh --sync                                        # copy the Template's files over the Factory's copies of them (after editing the Template)
scripts/gate.sh --quick                                       # while iterating: the gate up to and including shellcheck of the shell files the branch changed, shfmt of every shell file, ruff of every Python file and the C# check, without the local review check, test.sh and the Sandcastle check (about a minute, a few seconds more on its first run in a worktree, which builds the two C# checkers); never for a push
scripts/gate.sh                                               # the pre-push gate (needs pnpm on the first run, test.sh's tools, shfmt and the .NET SDK among them, and ruff 0.17: uv tool install ruff@0.17.6); GATE_NO_CACHE=1 runs test.sh and the Sandcastle check again on a tree that already passed them
GATE_PART=sandcastle scripts/gate.sh                          # one part alone (self, review, static, template, sandcastle), as a CI job runs it; the Sandcastle check needs pnpm and the network
scripts/gate-timings.sh [--runs N]                            # the step timings log as a table, slowest median first, over the last N runs (20); /gate-timings writes a report from it
.claude/skills/repo-ci-baseline/test.sh                       # all the Template's tests (TEST_SH_SHARD=hooks|scripts|checks runs one shard); needs actionlint, zizmor, yamllint, shellcheck, shfmt (v3.14 or a later v3), markdownlint-cli2, node, git, jq, and the .NET SDK global.json names for the scripts shard
.claude/skills/repo-ci-baseline/tests/sandcastle-check.test.sh # Sandcastle's type-check and tests on a throwaway Application; needs pnpm and the network
.claude/skills/repo-ci-baseline/template/owned/.claude/skills/hardening-review/scripts/mutate.py .claude/skills/repo-ci-baseline/tests/gh_repo/mutations.json  # ~5 min, from the repo root, needs uvx: edits check_gh_repo.py one mutation at a time and restores it; commit the checker change first (a dirty target is refused)
.claude/skills/repo-ci-baseline/template/owned/.claude/skills/hardening-review/scripts/mutate.py .claude/skills/repo-ci-baseline/tests/mutations/cleanup-work-issue.json  # same, for cleanup.sh and work-issue.sh; ~30 min, run in the background after committing (test.sh checks that the spec still fits the code)
.claude/skills/repo-ci-baseline/apply.sh --dry-run <repo>      # what Apply would write into an Application; writes nothing
```

CI runs `scripts/gate.sh` in parts, so a push from a clone without hooks is gated too, except for the local review check:
CI has no review ledger, so it sets `GATE_SKIP_REVIEW=1` (ADR 0010). The pre-push hook runs `scripts/gate.sh` on every push, and the gate runs the Sandcastle check when
you changed anything it applies or tests (`template/`, `apply.sh`, `SKILL.md`, `CONTEXT.md`, `references/`, `docs/adr/`, `tests/sandcastle*`,
`.claude/rules/sandcastle.md`; ADR 0012). The check needs the network and takes a few minutes; its pass is remembered for a clean tree. `GATE_PART=sandcastle scripts/gate.sh` runs it alone.

## Working on the Template

- Edit the Template's file, never an Application's copy of an Owned file; the next Apply overwrites it. Put a fix an Application needed into
  `template/owned/` first.
- A change to an Owned file reaches the Fleet by a Rollout (`rollout.sh`), not by itself. Say in the PR how it should roll out.
- Scaffolded code in an Application is written once (ADR 0002); the Factory doesn't keep it in sync.
- Update the matching file in `references/` when behaviour changes, and add a test to `test.sh` or `tests/`.

## Git workflow

Branches are `feature/{issue}-{slug}`, `fix/{issue}-{slug}`, `hotfix/{issue}-{slug}` or `chore/{slug}`. Commit subjects are
`<type>(<scope>): <Summary>`, with the scope `baseline` for the Template.

Size each issue so its pull request stays small: 30 files and 3,000 changed lines at most, tests included (the line the pre-push review and the gate warn at), and at
most one open decision, as the feature request and chore templates say, with the expected size and, past the line, the pull requests it splits into.
Claude Review reads a big diff in up to four batches of 8 pieces (about 300 KB each), a review each, so a bigger pull request costs a review per batch
and each review round over it takes longer; only pieces past the fourth batch go unread, and they hold the merge. Split a bigger issue into smaller ones before work starts, ordered with "blocked by"
links, rather than opening one large pull request.

Commit in a linked worktree under `../Application_Factory-worktrees/`: `pre-commit` refuses a commit in the primary checkout, and `pre-push` refuses a push
to `main`, a bad branch name, a dirty tree or a branch behind `origin/main`. In each clone, turn the hooks on once with `git config core.hooksPath .github/hooks`.
Start a branch with `git worktree add --no-track -b <branch> ../Application_Factory-worktrees/<folder> origin/main`, where `<folder>` is the branch with `/` as `-`.

Markdown is linted with `.markdownlint-cli2.jsonc` (lines up to 200 characters). Shell files are formatted by shfmt in the style `.editorconfig` gives (ADR 0015): run `shfmt -w <file>`
after editing one, with no style flags, so it reads that file. The edit-time lint runs shellcheck and `shfmt -d` on a shell file Write or Edit writes.
Python files pass ruff's bug-finding rules (F and B) with no config read (ADR 0011, Update: Python), at edit time and in the gate; mark a false alarm with
`# noqa: <code>` and the reason on its line.

Write and edit Markdown and YAML files with the Write and Edit tools, never with a shell command (`cat <<EOF >`, `sed -i`, `tee`, `echo >>`, a `python`
or `node` one-liner, a script). The edit-time lint (ADR 0011) is a `PostToolUse` hook on Write, Edit and MultiEdit: it lints the file the moment the tool
writes it, so a violation is fixed in the same turn. A Bash command doesn't name the files it wrote, so the hook never sees them, and the violation turns up
later: at the commit for Markdown (the `pre-commit` hook lints no YAML), otherwise at the gate, in CI or in a review. A script or test that writes Markdown
as its own output is fine; the rule is about the files you are editing.
