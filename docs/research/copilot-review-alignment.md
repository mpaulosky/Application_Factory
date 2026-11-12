# Aligning the local pre-push review with GitHub Copilot code review

Research note, 2026-11-12. Nothing here is a decision; an ADR would be needed before any Factory-only file is added (CLAUDE.md). Facts marked **[V]** are verified, with a source or a command run.
Statements marked **[I]** are my inference. Anything I could not verify says so.

## Summary

- **[V]** Copilot left 61 top-level inline comments on 20 of the Factory's 41 PRs (#1 to #61, one still open). 5 PRs got a clean "Approval recommended" overview.
- **[V]** The local review has gated the Factory only since PR #43 (ADR 0010). Its PRs since then that carried a clean local review recorded in their body (#49, #59, #60, #61) still drew 19
  Copilot findings.
  #60's body reports 43 local findings fixed in 4 rounds, then Copilot raised 7 more; #61's reports 34 in 9 rounds, then 4 more.
- **[I]** What escapes now differs from what escaped before the review existed. Before #43, 39 findings: 23 were docs/ADR/skill text against repo facts, the rest cross-file contracts, small
  edge cases, races and tests.
After it, the 19 escapes are all code robustness: hand-written shell parsers (#49, #61), git-state and git-config variance and check-then-act races in shell (#60), a fail-open scan (#59), a
  masked test (#60).
- **[V]** The local reviewers are read-only (Read, Grep, Glob; `.claude/agents/pre-push-reviewer.md`), yet the Edge cases lens tells them to "run the input when the tools allow". They cannot.
  Later rounds review only the previous round's fixes (SKILL.md step 4). Both are structural reasons for the escapes. **[I]** I consider them the main cause.
- **[V]** Copilot reviews with an LLM that has repo-wide agentic context and is steered by `.github/copilot-instructions.md` and `.github/instructions/*.instructions.md`.
  GitHub's pages do not say it runs CodeQL or linters inside the review. Its findings here read as reasoning plus constructed counterexamples, not tool output **[I]**.
- Plan, in order: (1) a probe step so reviewers' counterexamples are actually run; (2) a differential-testing rule for hand-rolled parsers; (3) a shell/git environment-variance checklist and
  a TOCTOU rule;
  (4) a whole-function final round on code that churned; (5) a Copilot-escape corpus with a replay score; (6) a harvest script and ADR-style feedback loop; (7) a docs-claims check and lens;
  (8) an optional shared `copilot-instructions.md`.

## How GitHub and Copilot check

Pages were fetched with a summarising web tool, not read raw, so wording is the tool's paraphrase of the page unless quoted. I did not read the `github/docs` source repo.

### Copilot code review

Source: <https://docs.github.com/en/copilot/concepts/agents/code-review>

- **[V]** It reviews "the changes in a pull request" and also uses agentic capabilities to "analyze your entire repository" for context. These run on GitHub Actions runners.
  If Actions is unavailable the review still posts, without them.
- **[V]** It can call MCP tools; the GitHub and Playwright MCP servers are on by default and repository settings can turn MCP tools off for code review.
- **[V]** The page does not say the review runs CodeQL or linters. CodeQL appears only under the separate GitHub Code Quality feature, which "complements" it.
- **[V]** Files not reviewed: dependency management files (such as `package.json`, `Gemfile.lock`), log files, SVGs. So lockfile and `package.json` changes get no Copilot review, but the
  local rubric does grade `package.json` as code.
- **[V]** It reviews a PR once unless "review new pushes" is on. Cost rises with PR size and with custom instructions. "Model switching is not supported"; it uses "a carefully tuned mix of models".
- **[V]** Effort levels: Lite, Balanced (default; routes to a higher-reasoning model for complex logic and security-sensitive code), Max (public preview; several independent agents from
  different providers plus an aggregation agent).
  Sources: the same page, <https://github.blog/changelog/2026-10-02-copilot-code-review-api-support-and-new-default-effort-level> (Balanced default since 2026-09-28)
  and <https://github.blog/changelog/2026-10-28-copilot-code-review-merge-with-confidence-with-max-and-approvals>.
  **[V, observed]** Every Copilot overview on this repo's PRs ends with "Review effort: Balanced" (for example PR #49's reviews).
- **[V]** Limits it states: it is "not guaranteed to spot all problems", can be wrong, and should supplement human review
  (<https://docs.github.com/en/copilot/responsible-use/code-review>). That page describes the flow as code changes plus PR title and body plus custom instructions combined into a prompt for an LLM.
- **[V]** Each Copilot review has an approval assessment in the overview; approvals are off by default and, when on, count toward required approvals (Oct 28 changelog above).

### Custom instructions

Sources: <https://docs.github.com/en/copilot/how-tos/configure-custom-instructions/add-repository-instructions>,
<https://docs.github.com/en/copilot/concepts/prompting/response-customization>, <https://docs.github.com/en/copilot/tutorials/customize-code-review>

- **[V]** Files: `.github/copilot-instructions.md` (repo-wide); `.github/instructions/**/NAME.instructions.md` with `applyTo` glob front matter (comma-separated; `**/*.py` for any depth); `AGENTS.md`.
  Optional `excludeAgent: "code-review"` or `"cloud-agent"`. The response-customization page also lists `CLAUDE.md` and `GEMINI.md` as agent instructions "not supported by all Copilot features";
  **I could not verify that code review reads `CLAUDE.md`.**
- **[V]** For a PR, instructions, agent instructions and skills are read from the **head branch**, so an instructions change is tested in its own PR.
  A skill named `code-review` under `.github/skills/` is given as an example of a task-specific workflow.
- **[V]** Guidance: keep files short and specific; about 10 to 20 instructions to start; short imperative lines; incorrect/correct code examples; split into focused files; one file under
  about 1,000 lines.
- **[V]** Not supported: changing comment format or tone, changing the PR overview, changing core function (such as blocking merges), following external links (copy the content in), vague
  asks like "don't miss any issues".
- **Not verified:** a 4,000-character cap for code review instructions. None of the three pages I fetched mentions it, though I remember it from elsewhere. Treat any cap as unconfirmed and
  check the feature matrix
  (<https://docs.github.com/en/copilot/reference/copilot-feature-matrix>, not fetched) before relying on length.
- **[V]** Automatic review is configured by a ruleset rule "Automatically request Copilot code review", with options "review new pushes" and "review draft pull requests"
  (<https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/configure-automatic-review>).

### Other GitHub-side checks

- **[V]** Code scanning with CodeQL: default setup picks languages, query suite and triggers automatically; supported languages include GitHub Actions workflows, JavaScript/TypeScript,
  Python, Ruby, C# and others;
results show as code scanning alerts (<https://docs.github.com/en/code-security/code-scanning/introduction-to-code-scanning/about-code-scanning-with-codeql>). The page does not say which PR
  events trigger default setup.
- **[V]** Dependabot version updates open PRs to bump manifests and GitHub Actions; security updates are a separate feature without the default 3-day cooldown
  (<https://docs.github.com/en/code-security/dependabot/dependabot-version-updates/about-dependabot-version-updates>).
- **[V]** Secret scanning push protection blocks partner, generic and custom patterns on command-line pushes, UI commits and uploads; anyone with write access can bypass with a reason,
  logged in the audit log
  (<https://docs.github.com/en/code-security/secret-scanning/introduction/about-push-protection>).

### This repository's configuration (read-only `gh api`, 2026-11-12)

- **[V]** One active ruleset, `main-rules`: deletion and non-fast-forward blocked, linear history, squash-only merge, 0 required approvals, `required_review_thread_resolution: false`,
`dismiss_stale_reviews_on_push: true`, `copilot_code_review` with `review_on_push: true` and `review_draft_pull_requests: true`, and 7 required checks (PR title, actionlint, zizmor,
  shellcheck, markdownlint, yamllint, Template tests), strict.
- **[V]** CodeQL default setup: `state: not-configured` (the API lists detected languages: actions, javascript, javascript-typescript, python, ruby, typescript). Secret scanning and push
  protection: enabled. Dependabot security updates: enabled.
  No classic branch protection (404).
- **[V]** No `.github/copilot-instructions.md`, no root `.github/instructions/`, no `AGENTS.md`, no `.github/skills/` in the Factory. So Copilot reviews the Factory with no repo-specific
  rubric and falls back to CLAUDE.md only if it reads it (unverified).
- **[V]** The Template has Owned `.github/instructions/{blog,markdown}.instructions.md` and `git-commit-instructions.md`, and the generator `scripts/sync-copilot-review.sh` with
  `copilot-review-sync.yml` (reference: `references/copilot-review-sync.md`).
  It builds `copilot-instructions.md` from `.claude/skills/code-review/SKILL.md`. The Factory carries neither (ADR 0009 "not carried") and has no `code-review` skill of its own.
  **[V]** The local review's lenses come from `lenses.md` plus that same optional `code-review` skill, so the shared-rubric mechanism exists but the Factory is not opted in.
- **[V]** A recall benchmark already exists for Claude Review against Copilot findings: `.claude/skills/repo-ci-baseline/bench/` (`claude-review-bench.py`, `claude-review-cases.json` with
  Articles#298 and noise/large sets).
  It does not run the pre-push-review lenses and has no Factory cases.

## Escape inventory

Source: `gh api repos/mpaulosky/Application_Factory/pulls/N/comments` for every PR from `gh pr list --state all --limit 100`. Counts are top-level Copilot comments (replies excluded).
Categories and "why missed" are my judgment **[I]**. Links are the comment URLs on `https://github.com/mpaulosky/Application_Factory/pull/N`.
Rows marked "after review" are PRs whose body records a clean local review.

| PR (local review?) | Finding, abridged | Category | Why the local review missed it |
| --- | --- | --- | --- |
| #8 (none) 5 | Instructions Part A makes only a local repo, so the push of `main` hits the pre-push block; omits `CLAUDE_CODE_OAUTH_TOKEN`; shared `UserSecretsId` contradicts ADR 0004 (`pull/8#discussion_r4503318360`) | Docs vs repo reality | No review existed; a docs-claims lens would need to open ADR 0004, `settings.json`, the hook |
| #9 (none) 3 | New copy check in `scripts/gate.sh` untested; CI `test.sh` does not run the drift check; ADR says `post-checkout` warns but it cannot (`pull/9#discussion_r4503448411`) | Test gap, CI coverage, docs vs code | Same; the CI gap needs reading `template-tests.yml` beside `gate.sh` |
| #10 (none) 8 | dotnet skills: trimmed csproj drops `TargetFramework`/`OutputType`, VSTest `--filter` on MTP, obsolete bUnit `TestContext`, overrides in an Owned `.editorconfig`, `git ls-files --others --ignored` semantics (`pull/10#discussion_r4503661189`) | Skill text vs Baseline facts (7), shell semantics (1) | Needed `adapt.md` and `global.json` knowledge; claims about tool behaviour never run |
| #11 (none) 1 | `rules/sandcastle.md` says `test.sh` validates the Sandcastle contract; it does not | Docs vs code | Claim not checked against `test.sh` |
| #18 (none) 2 | Dotfiles-dependency guard is case-sensitive; skips whole file on decode error so a dependency passes (`pull/18#discussion_r4507952980`) | Edge case in guard code | No lens run; both are input-class cases the Edge cases lens lists |
| #20 (none) 2 | `rollout.sh --brings #80,#81` in docs: shell treats `#` as a comment; `status.py` text promises auto-merge the Factory lacks (`pull/20#discussion_r4508336571`) | Shell quoting in docs; docs vs code | No review; doc commands never run |
| #21 (none) 3 | `copilot-x` substring matches the Copilot bot; request failure before settle window triggers `review:claude`; ADR says `--fix` sets the ruleset but `repo-settings-only` returns early (`pull/21#discussion_r4508431070`) | Workflow logic, race/timing, docs vs code | No review |
| #25 (none) 2 | `askStartedAt` captured before `createComment`; a 300 ms cap in the test would pass even without the fix (`pull/25#discussion_r4515697640`) | Race/timing; masked test | No review |
| #29, #31 (none) 3 | ADR 0009 makes `.yamllint.yml` a gate copy yet prescribes local exclusions; Dependabot bump leaves the copy gate failing; auto-merge limit lets Dependabot PRs merge unreviewed (`pull/31#discussion_r4518353848`) | ADR internal consistency; inherited behaviour | No review |
| #33 (none) 4 | ADR 0010: single `.security/` ignore line fails the ledger test; `host-limit.test` imports `step-limit.mts`; CI has no ledger; `node_modules/` untracked makes the tree dirty (`pull/33#discussion_r4518429773`) | ADR vs code (design) | No review; needed running the proposed files together |
| #35 (none) 3 | Whole-repo markdown lint fails on old ADRs; `repos-scope.test.sh` resolves `SKILL_DIR` through the `~/.claude` symlink; test parses the registry differently from `settings.py` (`pull/35#discussion_r4519053874`) | Gate scope; portability; contract drift | No review |
| #36, #37 (none) 2 | Dependabot bump of a copy fails the copy gate; hook change breaks the `branches.test.mts` corpus (`pull/37#discussion_r4526816556`) | Cross-file contract / copy drift | No review; consumer found by grep, not diff |
| #42 (none) 1 | Test fixture has no `moved` commit, so the assertion checks nothing (`pull/42#discussion_r4530148386`) | Masked test | No review |
| #43 (being introduced) 3 | `plain-text.mts` normalisation can rebuild a secret after the check; BOM-to-space after both secret checks; stale `dotfiles` path prefix double-counts mirrors (`pull/43#discussion_r4532060338`) | Security text handling (2); stale path | Review introduced by this PR |
| #49 (after review, 4 rounds) 7 | `check_gh_repo.py` branch tracker: quoted/commented `fi`, source order, `[[ ... ]]` operands, escaped separator before `#`, `then` as an argument, multi-line `if`, continuation (`pull/49#discussion_r4542008862`) | Hand-rolled shell parser edge cases | Reviewers read-only so could not run counterexamples; fix rounds reviewed only prior fixes; each Copilot round found a new corner of the same function |
| #59 (after review, 3 rounds) 1 | `check-issue-refs.py`: `rglob()` silently skips unlistable dirs, so a "fail-closed" scan can exit 0 (`pull/59#discussion_r4544408240`) | Error handling, fail-open | Language-API behaviour (needs a probe); promised property not tested |
| #60 (after review, 4 rounds, 43 fixed) 7 | `cleanup.sh`: tip check then `git branch -D` race; fetch URL vs push URL repos; worktree removed before tip recheck; `work-issue.sh` assumes `autoSetupMerge`; `showUntrackedFiles=no` weakens guards; stale worktree registration; test calls `scrub` before it is defined (`pull/60#discussion_r4545290728`) | Races (2), git-config variance (3), stale state (1), test ordering (1) | Not diff-visible bugs: they appear only when git's config or timing differs; no probe step; lens text mentions GNU/BSD and `GIT_*` but not git config or check-then-act |
| #61 (after review, 9 rounds, open) 4 | `check-git-status.py` sees `git` inside comments/strings, whitespace splitting breaks quoting; spec checker accepts `[42]`; `--diff-filter` omits `T` (`pull/61#discussion_r4551623048`) | Parser (2), validation mismatch, git semantics | Same as #49; the last PR of 9 rounds still escaped |

### Category counts (all 61 findings) **[I]**

| Category | Total | PRs 8 to 42 (39, no local review) | #43 (3) | #49, #59, #60, #61 (19, after a recorded review) |
| --- | --- | --- | --- | --- |
| Docs, ADR or skill text vs repo reality | 23 | 23 | 0 | 0 |
| Parser and input-class edge cases in code | 15 | 4 | 0 | 11 (#49 seven, #59 one, #61 three) |
| Environment, git-config, quoting variance, stale state | 7 | 2 | 0 | 5 (#60 four, #61 one) |
| Cross-file contract, copy drift, CI coverage | 5 | 4 | 1 | 0 |
| Races and check-then-act | 4 | 2 | 0 | 2 (#60) |
| Test gaps and masked tests | 4 | 3 | 0 | 1 (#60) |
| Security text handling | 2 | 0 | 2 | 0 |
| Workflow logic (substring bot match) | 1 | 1 | 0 | 0 |

No Copilot comment on this repo concerned workflow permissions, secrets exposure in a workflow, or markdown style. The deterministic linters (actionlint, zizmor, shellcheck, markdownlint,
yamllint) already cover those; Copilot's value was reasoning about behaviour.

## Gap analysis

What covers each category today **[V]** from `lenses.md`, `review-bar.md`, `SKILL.md`, `scripts/gate.sh`:

| Category | Covered by | Gap |
| --- | --- | --- |
| Docs, ADR, skill text vs reality | Lens "Docs match the code" (asks if a doc still says what the code does); Repo conventions | Lens is generic; it does not tell the reviewer to open every file a claim names, nor to check a doc against its siblings (ADR table vs text). No deterministic check of paths, flags or counts in docs. In this window it produced 0 post-review escapes, so currently adequate **[I]** |
| Parser / input-class edge cases | "Edge cases" lens and checklist; step 6 n-1/n/n+1 tests and mutation checks; "replace the mechanism after two rounds" rule | Reviewers cannot run inputs (no shell). The fix author tests what they thought of; Copilot found the next corner each time. No differential test against the real tool (bash, git) |
| Environment variance (git config, symlink, quoting) | "Shell" lens: `set -euo pipefail`, empty var, spaces, GNU/BSD, missing tool, `GIT_*` | Not listed: `status.showUntrackedFiles`, `branch.autoSetupMerge`, `remote.*.pushurl`, `core.hooksPath`, symlinked invocation path, stale worktree registrations, diff-filter letters (`T`), `#` in documented commands |
| Races, check-then-act | "Races and caches" lens, scoped to "shared state, caches, async code, Sandcastle runs" | Its **Applies to** column does not match shell scripts that read a ref and later delete it, so #60's reviewers likely never got it **[I]** |
| Cross-file contract | "Repo conventions"; gate drift check for copies; `Template tests` | No lens asks for consumers of a changed constant/function/test corpus; relies on the reviewer grepping |
| Test gaps and masked tests | "Tests" lens; step 6 mutation checks (`hardening-review/scripts/mutate.py`) | Manual; "can each assertion actually fail" is asked but not enforced for new tests or fixtures; no check for a shell helper called before definition (shellcheck may flag it, not verified) |
| Security text handling | "Security" lens; `.security/` record | Adequate after #43 **[I]** |
| Whole-repo or branch churn | Round 1 reviews the whole branch | Rounds 2+ review only `fixes.patch`; the SKILL tells the reviewer to read the whole function and callers, but #49, #60 and #61 show fix-induced and neighbouring corners escaping anyway |

Structural causes **[I]**:

1. **No execution.** The reviewer is deliberately read-only (untrusted text), and the lens tells it to run inputs. Copilot's findings are concrete counterexamples (such as `if true then &&
   false; then`), the kind someone had to run or reason through in the shell grammar.
2. **Same author, same blind spot.** The fixer writes tests for the corners it thought of; the reviewers see only a diff plus lens text. Copilot reviews cold, and on `check_gh_repo.py` it
   opened a fresh corner on each of three pushes.
3. **Lens routing by file type.** A bash/git script reaches the Shell lens but not Races; a Python parser of shell reaches Edge cases but not Shell semantics.
4. **No feedback loop.** `lenses.md` says its lenses came from Copilot findings on Baseline repos, but there is no recorded mapping from a Copilot finding to a lens item or a test, and no
   recall measurement.

Constraints respected: changes below start in `template/owned/` (the Template, rolled out by `rollout.sh`) and are copied by `scripts/gate.sh --sync`; Factory-only files (new scripts or
tests in the Factory root) need an ADR or an amendment to ADR 0010.

## Prioritised plan

Catch-rate figures are my estimates **[I]** against the 19 post-review escapes, not measurements; step 5 is what would measure them.

### 1. Probe step: run the reviewers' counterexamples. Effort M

- Change: lets each reviewer return, with a finding, an optional `probe` (script text plus expected result) that the orchestrating agent, which has a shell, runs in a scratch directory.
The finding is kept only when the probe shows the failure (or is marked "unrun"). Reviewers stay read-only and hold no secrets; the probe runs on the orchestrator side in `<run>/probes/`.
  Add a sentence to the Edge cases and Shell lenses saying so.
- Files: Template `pre-push-review/SKILL.md` (steps 4 and 5), `lenses.md`, `review-bar.md`, possibly `review-ledger.mts` if `probe` becomes a recorded field (then its test).
- Expected impact: the largest single lever; plausibly 8 to 12 of the 19 (parser, git-config and race counterexamples are runnable). Also cuts false positives.
- Verify: replay (item 5) on #49 and #60 pre-fix heads; a skill test pinning the new answer format.

### 2. Differential testing for hand-rolled parsers. Effort M

- Change: a new lens "Parsers of other languages" (applies to code that tokenises shell, YAML, Markdown, git output): prefer a real parser (`bash -n`, `shlex`, `git` plumbing), otherwise
  require a differential test that feeds a generated corpus
(quotes, comments, `[[ ]]`, line continuations, `then`/`fi` as arguments) to both the checker and real bash/git and compares. Extend step 6's "replace the mechanism" rule: trigger on the
  first repeat finding in a parser, not the second round.
- Files: Template `lenses.md`, `SKILL.md` step 6; the corpus helper lives in `tests/` of the skill (not Factory root).
- Expected impact: 5 to 7 of 19 (#49, #61 parser items).
- Verify: apply it to `check_gh_repo.py` and `check-git-status.py` and see whether the Copilot cases from #49/#61 fall out as differential failures.

### 3. Environment-variance checklist and TOCTOU rule in the Shell lens; widen Races routing. Effort S

- Change: add the list from the gap table (git config keys that change command output, `--untracked-files=all`, `--track`, push vs fetch URL, `git worktree` stale entries, diff-filter
  letters, symlinked `$0`, `#` in documented commands);
add "any check followed later by an action on the same ref/file: use a compare-and-act primitive (`update-ref -d <ref> <oldvalue>`)". Make Races apply to `*.sh` and hooks that delete or move
  refs, files or worktrees.
- Files: Template `lenses.md`; update `pre-push-review-skill.test.mts` expectations if it pins lens text.
- Expected impact: 4 to 6 of 19 (#60 config variance and races). Cheapest.
- Verify: replay on #60's head; the lens test.

### 4. Churn-hot-spot final round. Effort S to M (extra tokens per push)

- Change: after the last fixing round, one round that gives the reviewers the whole current text of every function that appeared in two or more rounds' `fixes.patch` (not only the patch),
  under the lens that raised it, and asks for new corners.
  Today the "review-only" round exists (step 7) but is bounded to the fixes.
- Files: Template `SKILL.md` steps 4 and 7.
- Expected impact: unknown; targeted at #49 and #61 where one function churned. Cost scales with churn only.
- Verify: replay; compare tokens per branch.

### 5. Corpus-replay evaluation. Effort L (once)

- Change: extend `bench/` (Template) with Factory cases: for each Copilot finding, `{pr, base, head: commit Copilot reviewed (review.commit_id), expect: keywords, paths}` using the existing
  case format, plus a runner mode that executes the pre-push-review lenses instead of the claude-review prompt.
  Start with #49, #59, #60, #61, #43, #10 and #35. Score recall (found / expected) and noise on merged PRs with clean Copilot reviews (#44, #45, #46, #48, #58).
- Files: Template `bench/` (`claude-review-cases.json` or a sibling file, runner, tests); note the bench lives in the skill, not in Owned, so it needs no Rollout.
- Expected impact: no direct catch; makes every other item measurable (baseline recall now **unknown**).
- Verify: runner unit tests (the bench already has `tests/`); one run with current lenses is the baseline. Caveat **[I]**: pre-fix heads where Copilot commented exist in git history only if
  the branch commits survive squash; `review.commit_id` is in the API output I collected.

### 6. Feedback loop from Copilot findings to lens items (ADR-style). Effort M

- Change: `harvest` script (read-only `gh api`) listing Copilot threads on merged PRs not yet mapped; each gets a line in a table (`finding -> lens item or deterministic check -> corpus
  case`). A new finding with no mapping is a gate warning in the Factory's PR template checklist.
  Record the process in an ADR (amends 0010) since it adds a Factory-owned file.
- Files: Template skill `references/` (table), `bench/cases`, ADR in `docs/adr/`.
- Expected impact: prevents regressions of the class; recall trend visible.
- Verify: run `harvest` on the 61 findings, confirm each maps.

### 7. Docs-claims lens and deterministic reference check. Effort S (lens), M (check)

- Change: lens text: for each factual claim in a changed doc (path, flag, count, "X cannot Y", table row) open the named source and compare; compare an ADR's table with its own prose.
  Deterministic: a gate step that fails when a backticked repo path or `file:line` in changed Markdown does not exist.
- Files: Template `lenses.md`; check script under the skill's `tests/` wired into `test.sh` (not the Factory root).
- Expected impact: 0 of the 19 recent escapes, but it is the class that produced 23 of the 61 total; keeps it at zero when docs-heavy PRs return (#60 touched 9 docs and drew none).
- Verify: replay on #8, #10, #33.

### 8. Optional shared rubric: `.github/copilot-instructions.md`. Effort S to M

- Change: opt the Factory into the Template's existing generator by adding a `.claude/skills/code-review/SKILL.md` with a **Lenses** table (the same rubric the local review reads), generate
  `copilot-instructions.md`, and add short `*.instructions.md` files with `applyTo` for `**/*.sh` and `**/*.py`.
  This needs an ADR amending ADR 0009 ("not carried: `copilot-review-sync.yml`").
- Expected impact on local catch rate: none directly. It makes Copilot check the same lenses, so Copilot findings become signal on lens gaps, and it may cut Copilot's off-rubric comments
  (unverified). Copilot cost rises with instructions (docs), so keep it short.
- Constraints from GitHub's docs: no external links, no format changes, concrete examples, head-branch read.
- Verify: open a PR that changes only the instructions file and see which comments change.

Cost/benefit **[I]**: items 3, 7 (lens) and 4 are prompt text and cost little to build. Item 1 and item 4 cost tokens per push (the ADR 0010 cost line says reviewers already run at high
effort on Sonnet).
Item 2 is the only one that changes how code is tested rather than how it is read. Item 5 is the only one that can show whether 1 to 4 worked; do it before spending on 1 and 2.
A Copilot review is "free" per push in this repo's setup (ruleset-triggered), so the benefit of closing the gap is fewer fix-and-rereview round trips (PR #60 took three rounds of Copilot
comments after a 43-finding local review) rather than saved review cost.

## Open questions

- Is there a character or line cap on instructions for Copilot code review? Not found on the three pages fetched; check the feature matrix.
- Does Copilot code review read `CLAUDE.md` or only `AGENTS.md` and the `.github` files? The docs list `CLAUDE.md` as agent instructions "not supported by all features".
- Does Copilot's review run any static analysis? GitHub's pages are silent; only inference from its comments says no.
- Is the Copilot effort for this repo Balanced everywhere? Observed in overviews; the repository-level setting was not readable through the API I used.
- Can the probe in item 1 run safely? The reviewer's text may come from untrusted authors, so the probe must be authored by the reviewer from the diff but executed with no network and no
  secrets; whether that is acceptable is a security decision for the owner (it touches the SKILL's rule on shell access).
- Do the pre-fix commits of #49 and #60 survive in the clone for replay? Not checked.
- Whether Copilot's findings that the owner answered "Fixed in" plus a commit were all valid is not assessed; only some replies were read. False positives would lower the target.
- PR #61 is still open; its 4 findings may have been addressed since this collection.
