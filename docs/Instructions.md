# Instructions For Setting Up or Standardizing a Repo

Every repo carries the same CI, release, hook and lint Baseline, copied from the `repo-ci-baseline` skill's Template by
`apply.sh`. A new repo gets it through the PR that follows its creation from the Starter (Part A); an existing repo gets it through one Standardize PR
(Part B). Parts C and D apply to either.

The skill lives at `.claude/skills/repo-ci-baseline/` in the Application Factory. Its `SKILL.md` is the source of truth;
these steps are the short path through it.

```bash
BASELINE="$HOME/github/Application_Factory/.claude/skills/repo-ci-baseline"
```

## Part A: Set Up a New Repo

A new Application is created from the `mpaulosky/Blazor-Server` Starter ([ADR 0004](adr/0004-applications-are-created-from-the-blazor-server-starter.md)),
so it starts with history and the Starter's stack. The Baseline then arrives through a branch and PR, like Part B, because the
installed `pre-push` hook refuses any push that updates `main`, the first included.

### A1. Create the Repository from the Starter

```bash
gh repo create mpaulosky/[ProjectName] --template mpaulosky/Blazor-Server --private --clone   # or --public
cd [ProjectName]
```

### A2. Rename

Run the rename pass on a branch in a worktree. ADR 0004 says it covers the solution name (`[ProjectName].slnx`, so `{{SOLUTION}}`
gets filled in), file headers, the Auth0 role-claim namespace, `UserSecretsId` (a new GUID, see Part D), and the README and `CLAUDE.md`
text that describes the repo as the Starter. It also covers the Starter's `.sandcastle/lib/repo-config.mts`: Apply skips an existing Seed
file, so set `REPO_SLUG` to `mpaulosky/[ProjectName]` and change the solution entry (`Blazor-Server.slnx`) to the new solution by hand,
or local review's `gh` commands target the Starter. It isn't scripted yet, so do it by hand, and follow Part B3 before pushing.

```bash
git worktree add -b chore/create-application ../[ProjectName]-worktrees/chore-create-application origin/main
cd ../[ProjectName]-worktrees/chore-create-application
```

### A3. Apply the Baseline

In the same worktree. The script refuses `main`, `preview`, `dev`, a detached `HEAD` and a dirty worktree, so commit the rename first:

```bash
git add -A
git commit -m "chore: rename the Starter to [ProjectName]"
"$BASELINE/apply.sh" .
git add -A
git commit -m "chore: apply repo-ci-baseline Template"
```

It also runs `git config core.hooksPath .github/hooks`. From here on, `pre-commit` refuses commits in the primary checkout: every
change, including filling the placeholders the report lists, is made in a worktree under `../[ProjectName]-worktrees/` (see the
skill's `references/git-hooks.md`).

`core.hooksPath` is what turns the hooks on, in each clone. Don't set a global `init.templateDir` to install them:
the hooks it copies into `.git/hooks/` are symlinks or copies that go stale when the Template moves (#12 left them
dangling), a Baseline repo's `core.hooksPath` overrides them anyway, and in any other repo the pre-push hook would run a
`scripts/gate.sh` that isn't there. To drop an old one: `git config --global --unset init.templateDir`.

### A4. Secrets, Settings and PR

Check the repo has its two required secrets (`gh secret list --repo mpaulosky/[ProjectName]`) and add any that is missing.
`github-settings.sh` only reports a missing one; it can't add it:

```bash
gh secret set RELEASE_PR_PAT --repo mpaulosky/[ProjectName]
claude setup-token   # prints the token; then:
gh secret set CLAUDE_CODE_OAUTH_TOKEN --repo mpaulosky/[ProjectName]
```

Without `CLAUDE_CODE_OAUTH_TOKEN`, Claude Review skips whenever Copilot can't review. Push the branch, open the PR as a draft, then apply the
GitHub settings (merge settings, ruleset, required checks, labels, Actions and security):

```bash
"$BASELINE/github-settings.sh" --fix mpaulosky/[ProjectName]
```

Land the PR as Part B4 describes, and run the settings script again once it merges: required checks are added only once `main` reports them.

### A5. Register in the Fleet

Neither Apply nor `github-settings.sh` adds the Application to the Fleet registry, which `status.sh`, `rollout.sh` and `github-settings.sh --all`
read. Open a Factory PR that adds `mpaulosky/[ProjectName]` as a line to `.claude/skills/repo-ci-baseline/github/repos.txt`, after the
repos already there (the list is in rollout order). Do it once the Application's first Baseline PR has merged, so a Rollout finds a checkout
of a repo that carries the Baseline.

## Part B: Standardize an Existing Repo

One repo, one PR. If the repo still has squad installed (`.squad/` or `squad-*` workflows), run the `remove-squad`
skill first and land that on its own.

### B1. Survey

From the repo's primary checkout, on any branch:

```bash
"$BASELINE/apply.sh" --dry-run .
```

The dry run writes nothing. Read the report with the skill's `references/apply.md`, and plan an action for every line:
drift in overwritten Owned files, skipped Seed files, unfilled placeholders, Leftovers and Conflicts.

### B2. Apply

Sync `main`, then Apply in a fresh worktree. The script refuses `main`, `preview`, `dev`, a detached `HEAD` and a dirty
worktree, so the Apply commit is exactly its output:

```bash
git fetch origin
git worktree add -b chore/standardize-baseline ../[Repo]-worktrees/chore-standardize-baseline origin/main
cd ../[Repo]-worktrees/chore-standardize-baseline
"$BASELINE/apply.sh" .
git add -A
git commit -m "chore: apply repo-ci-baseline Template"
```

### B3. Adapt

In further commits on the same branch, work through the skill's `references/adapt.md`: drop drift (or move a real local
fix into the Template first), merge skipped Seed files, fill placeholders, decide each Leftover, fix Conflicts, and move
repo-only CI setup into `.github/ci/prepare.sh`. Done when `scripts/gate.sh` passes locally.

### B4. Land

Push once and open the PR **as a draft**, because the repo's old auto-merge workflow would otherwise merge it as soon as
its checks pass. While it's a draft:

- run the docs-only probe from `references/docs-only-ci.md`,
- run `"$BASELINE/github-settings.sh" --fix mpaulosky/[Repo]` (it also checks the `RELEASE_PR_PAT` secret exists).

Then land it by hand as `references/automerge.md` describes, while nothing else is in flight: the merge is the new
`release.yml`'s first live run. Run `github-settings.sh --fix` again right after the merge, so the new required checks
are added. Done when the PR **and** its release-blog PR have merged, and `github-settings.sh` reports the repo clean.

### B5. Verify and Record

Run each part's live check from its reference; a part counts only once that passes on the repo. Put any lesson into the
matching reference, and any fix to an Owned file into the Template (a Factory PR with `test.sh` green), so the next
Standardize doesn't hit the same surprise.

Order across repos: `github/repos.txt`'s in the repo-ci-baseline skill, the one list of the repos, with TicketManager
first.

## Part C: Optional App-Stack Starting Points

These aren't part of the Baseline. Copy the ones that fit, and rewrite them for the project; they were written for
IssueTracker. They still live in `mpaulosky/dotfiles` under `Support For New Projects/` (they haven't moved to the Factory or
the Starter yet), so take them from there.

- [ ] `.aspire/` for an Aspire app
- [ ] `.github/instructions/blazor.instructions.md` for a Blazor UI
- [ ] `.github/instructions/dotnet-project.instructions.md`
- [ ] `.github/instructions/mongo-dba.instructions.md` for MongoDB
- [ ] `.github/skills/code-review/` (a repo skill; see `.claude/skills/README.md` in the repo)
- [ ] `.github/agents/beast.agent.md`

## Part D: Configure Auth0 for Web Projects

Give each Application its own `UserSecretsId`, so Applications don't share one local secret store. Set the same new GUID in both
`src/UI/UI.csproj` and `src/AppHost/AppHost.csproj`: the AppHost reads the Auth0 parameters from the UI's store and passes them to the UI as
environment variables, so changing only one leaves Aspire runs on the old shared store. The rename pass does this for a new Application.

```xml
<UserSecretsId>[new GUID, from `uuidgen`]</UserSecretsId>
```

The ID alone configures nothing. On each developer machine, set the Application's Auth0 values in its own store, copying them from an existing
Application (`dotnet user-secrets list --project src/UI`):

```bash
dotnet user-secrets set "Auth0:Domain" "[value]" --project src/UI
dotnet user-secrets set "Auth0:ClientId" "[value]" --project src/UI
dotnet user-secrets set "Auth0:ClientSecret" "[value]" --project src/UI
```

The AppHost also reads `Auth0:RoleClaimType` from configuration; set it the same way if the existing Application has it.
