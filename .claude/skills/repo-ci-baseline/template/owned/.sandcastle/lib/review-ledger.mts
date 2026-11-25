// The local review's ledger and its stamp: what `/pre-push-review` found on a
// branch's diff, what became of each finding, and the check
// `scripts/gate.sh` runs so nothing is pushed that the review didn't pass.
// Most of the threads GitHub's reviewers open on a PR are about behaviour a
// review before the push could have caught, so the gate asks for one.
//
// A ledger is keyed by the branch's own changes against its merge base with
// origin/main: their patch, with blob ids, line numbers and hunk headers left
// out (see diffKey). The key finds the ledger; the check then makes sure HEAD
// is the reviewed commit, or that commit with a clean merge of main and
// nothing else (see isReviewedCode). So a merge of main that leaves every hunk
// the branch changed as it was keeps the review, close to the rule PR
// Auto-Merge and Claude Review apply on GitHub, and so does a rebase onto main
// with no conflict when its tree is that merge's (one that isn't needs a new
// review), and a new commit, a conflict
// resolution or a main change inside one of the branch's hunks needs a new
// one.
//
// Ledgers live under the clone's .git (`review/<key>.json`, see ledgerDir), so
// every worktree of the clone sees them and nothing is committed. Sandcastle's
// sandboxes see an empty read-only folder there instead (protectedGitMounts in
// host-safety.mts): ledgers hold accepted security findings, which are never
// published. They are a process aid, not a security boundary against the
// person: anyone who can push can write one, as anyone can push with
// --no-verify.
//
// `record` also keeps a local record of security findings, `.security/` in the
// main checkout (see security-record.mts): one file per weakness, written for
// every security finding it records, so a later branch reads what an earlier
// one decided. It is git-ignored and never published, like the ledgers, and
// `security` lists it for the reviews, by id, status, lens and path only.
//
// Run as a script: `scripts/review.sh <command>` (see USAGE below). For a branch
// Sandcastle builds, the host runs the rounds itself (lib/host-review.mts) and
// calls prepareRound and recordReview in-process, in the main checkout, never
// the sandbox's scripts/review.sh; the module lives in .sandcastle/lib/ too.

import { createHash, randomBytes } from "node:crypto";
import { chmodSync, closeSync, constants, existsSync, fstatSync, linkSync, lstatSync, mkdirSync, openSync, readdirSync, readFileSync, realpathSync, renameSync, rmSync, statSync, writeFileSync, writeSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { plainText, RENDERED_LINK, stripControl, visibleLetters, withoutInvisible } from "./plain-text.mts";
import { NOT_OWNED_BASELINE_PATHS, REPO_SLUG } from "./repo-config.mts";
import { hostExecFileSync } from "./host-limit.mts";
import { ledgerGitEnv, markAccepted, pinLedgerGit, readEntries, recordSecurity, renderSecurityList, replaceFile, type SecurityInput, type SecurityOutcome } from "./security-record.mts";

// They live in security-record.mts, which runs git on the host the same way, and are re-exported for the callers of the ledger.
export { ledgerGitEnv, pinLedgerGit };

// The variable that skips the review check, for the gate runs that come
// before or during the review: the skill's own (each round's), and every gate
// in Sandcastle's sandbox until its reviewer runs the same review.
export const SKIP_REVIEW_ENV = "GATE_SKIP_REVIEW";

// The key of a branch with no changes against its base: nothing to review.
export const EMPTY_DIFF_KEY = "empty";

// The branch the Baseline's re-Apply pushes (repo-ci-baseline's reapply.sh).
export const REAPPLY_BRANCH = "chore/reapply-baseline";

// The Owned Baseline files, as the Template ships them (a test in dotfiles
// keeps this list and template/owned the same). A re-Apply overwrites only
// these (and writes any Seed file the repository lacks, which is not on this
// list and needs a review), and they're reviewed in the Template's own PRs; a
// repository must never change them, so a review there could only raise
// findings nobody there may fix. A re-Apply skips the local review for them,
// except for REVIEWED_ON_REAPPLY below. The skip is a decision per file, not a
// default: either the Template's own gate checks the file's content and the
// file neither runs nor feeds the review and the gate (the workflows, the
// other hooks, scripts and .sandcastle/lib/ modules, and their tests:
// actionlint, zizmor, shellcheck, pytest, tsc and the rest; the
// hardening-review skill's .py scripts are covered by pytest), or a person starts the reviewers
// that read it and reads their output (the hardening-review skill's text, brief.md included, so a wrong edit
// can only affect a review a person runs), or
// it steers only Copilot's review and commit messages and widens no tool and
// runs no code (the .github/instructions/ files, so a wrong instruction can only
// shape advice). A file no Template check can judge, or that a Sandcastle role
// reads as its prompt (the
// pre-push-review skill's SKILL.md, lenses.md and review-bar.md, which the host
// puts into its reviewers' prompts, and docs/PROCESS.md, which review-prompt.md
// tells a role to follow), or that is part of the chain that runs the review and
// the gate (scripts/gate.sh, scripts/review.sh, .github/hooks/pre-push, and the
// lib/ modules review-ledger, host-safety, sandbox-env, host-review,
// host-review-prompts and build, and the host's entrypoint and status command,
// main.mts and status.mts, which wire those modules together: the Template's
// checks run in the Template, so in a repo they judge whatever bytes the
// re-Apply wrote), goes in REVIEWED_ON_REAPPLY. In dotfiles,
// lib/owned-paths.test.mts enforces it: a single-file pattern must be
// classified there, and a file under a glob must be listed in that glob's
// entry, so a new Owned file is never exempt unseen.
export const OWNED_PATHS = [
  ".github/workflows/*.yml",
  ".github/hooks/**",
  ".github/scripts/**",
  ".github/instructions/*.md",
  ".github/pull_request_template.md",
  ".claude/agents/pre-push-reviewer.md",
  ".claude/skills/pre-push-review/SKILL.md",
  ".claude/skills/pre-push-review/lenses.md",
  ".claude/skills/pre-push-review/review-bar.md",
  ".claude/skills/hardening-review/**",
  ".sandcastle/main.mts",
  ".sandcastle/check.sh",
  ".sandcastle/status.mts",
  ".sandcastle/status.test.mts",
  ".sandcastle/lib/*.mts",
  ".sandcastle/critique-prompt.md",
  ".sandcastle/intake-prompt.md",
  ".sandcastle/plan-prompt.md",
  ".sandcastle/review-prompt.md",
  ".sandcastle/roles/architect.md",
  ".sandcastle/roles/backend.md",
  ".sandcastle/roles/tester.md",
  ".sandcastle/roles/follow-up.md",
  ".sandcastle/roles/gate-fixer.md",
  ".sandcastle/roles/scribe.md",
  ".sandcastle/roles/ui.md",
  ".sandcastle/tsconfig.json",
  ".sandcastle/.env.example",
  ".sandcastle/.gitignore",
  ".sandcastle/.markdownlint-cli2.jsonc",
  "scripts/gate.sh",
  "scripts/check-branch-name.sh",
  "scripts/check-git-hooks.sh",
  "scripts/lint-edited-file.sh",
  "scripts/check-pr-title.sh",
  "scripts/sync-copilot-review.sh",
  "scripts/review.sh",
  "scripts/watch-pr.sh",
  "scripts/cleanup.sh",
  "scripts/work-issue.sh",
  "scripts/tests/**",
  "docs/PROCESS.md",
  "docs/CODE_OF_CONDUCT.md",
  ".github/ISSUE_TEMPLATE/bug_report.md",
  ".github/ISSUE_TEMPLATE/chore_request.md",
  ".github/ISSUE_TEMPLATE/config.yml",
  ".github/ISSUE_TEMPLATE/feature_request.md",
  ".github/ci/coverage-threshold",
  ".claude/rules/sandcastle-hand-off.md",
  ".claude/rules/markdown-and-yaml-edits.md",
  ".claude/rules/script-edits.md",
  ".claude/skills/README.md",
  ".security/README.md",
  ".editorconfig",
  ".gitignore",
  ".markdownlint-cli2.jsonc",
  ".yamllint.yml",
  "codecov.yml",
  "GitVersion.yml",
  "global.json",
] as const;

// The Owned files whose content is behaviour, not text: the coverage gate
// ci.yml enforces (any whole percentage passes it, 0 included), the rules
// files that load into every Claude session in the repository, or into every one that works on a script (script-edits.md), and the issue
// templates, whose front matter sets the labels and assignee of every issue
// opened from them (a `Sandcastle` label would feed the unattended pipeline),
// the reviewer agent, whose `tools:` front matter is what keeps reviewers
// read-only, the pre-push-review skill's SKILL.md (its Severities), lenses.md and review-bar.md, which the host reads into the reviewers' prompts
// (lib/build.mts#readSkillFiles), the prompts that instruct the pipeline's roles, the
// local gate's .sandcastle/check.sh, whose watched paths and early exit decide
// whether the Sandcastle check runs at all, global.json, which sets the SDK and the msbuild-sdks a build runs,
// docs/PROCESS.md, which review-prompt.md tells a role to follow, scripts/cleanup.sh, which deletes branches, worktrees and origin
// branches, scripts/work-issue.sh, which makes a branch and worktree named from issue text, scripts/lint-edited-file.sh, which lints
// each Markdown, YAML, shell or Python file an agent writes, with no Bash prompt, and vets the repository's linter configs as it does, and the
// gate chain (see OWNED_PATHS above).
// The other files that moved from Seed to Owned (CODE_OF_CONDUCT.md,
// the READMEs under .security/ and .claude/skills/) are audited text that no
// role reads, and a wrong one only misleads a person, so they stay exempt.
// The exemption judges a path, never the bytes, since a repository has no copy
// of the Template to compare with.
export const REVIEWED_ON_REAPPLY: ReadonlySet<string> = new Set([
  "scripts/cleanup.sh",
  "scripts/work-issue.sh",
  "scripts/lint-edited-file.sh",
  ".github/ci/coverage-threshold",
  ".claude/rules/sandcastle-hand-off.md",
  ".claude/rules/markdown-and-yaml-edits.md",
  ".claude/rules/script-edits.md",
  ".github/ISSUE_TEMPLATE/bug_report.md",
  ".github/ISSUE_TEMPLATE/chore_request.md",
  ".github/ISSUE_TEMPLATE/config.yml",
  ".github/ISSUE_TEMPLATE/feature_request.md",
  ".claude/agents/pre-push-reviewer.md",
  ".claude/skills/pre-push-review/SKILL.md",
  ".claude/skills/pre-push-review/lenses.md",
  ".claude/skills/pre-push-review/review-bar.md",
  ".sandcastle/critique-prompt.md",
  ".sandcastle/intake-prompt.md",
  ".sandcastle/plan-prompt.md",
  ".sandcastle/review-prompt.md",
  ".sandcastle/roles/architect.md",
  ".sandcastle/roles/backend.md",
  ".sandcastle/roles/tester.md",
  ".sandcastle/roles/follow-up.md",
  ".sandcastle/roles/gate-fixer.md",
  ".sandcastle/roles/scribe.md",
  ".sandcastle/roles/ui.md",
  ".sandcastle/check.sh",
  "global.json",
  "docs/PROCESS.md",
  "scripts/gate.sh",
  "scripts/review.sh",
  ".github/hooks/pre-push",
  ".sandcastle/lib/review-ledger.mts",
  ".sandcastle/lib/host-safety.mts",
  ".sandcastle/lib/sandbox-env.mts",
  ".sandcastle/lib/host-review.mts",
  ".sandcastle/lib/host-review-prompts.mts",
  ".sandcastle/lib/build.mts",
  ".sandcastle/main.mts",
  ".sandcastle/status.mts",
]);

// The Owned files the Template retired, which Apply deletes when the repository's copy is identical to a version it
// shipped (the Factory's retired.txt, which test.sh holds this list to). A re-Apply that deletes one of them, a regular
// file the repository doesn't list as its own, passes without a review, as one that writes an Owned file does: the
// Template's own review covered the retirement.
export const RETIRED_PATHS: ReadonlySet<string> = new Set<string>([]);

// The files under an Owned pattern that the Template doesn't supply: its Seed
// repo-config.mts, and the files the repository lists in repo-config.mts's
// NOT_OWNED_BASELINE_PATHS (its own workflows, scripts and Sandcastle tests).
export const NOT_OWNED: ReadonlySet<string> = new Set([".sandcastle/lib/repo-config.mts", ...NOT_OWNED_BASELINE_PATHS]);

// A glob from OWNED_PATHS as a regular expression: `**` crosses folders, `*`
// doesn't.
const globPattern = (glob: string) =>
  new RegExp(`^${glob.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\u0000/g, ".*")}$`);
const ownedPatterns = OWNED_PATHS.map(globPattern);

// Whether `path` falls under one of the Owned patterns, Owned or not.
export const underOwnedPattern = (path: string) => ownedPatterns.some((pattern) => pattern.test(path));

// Whether `path` is an Owned Baseline file.
export const isOwnedPath = (path: string) => !NOT_OWNED.has(path) && underOwnedPattern(path);

// How much a finding matters, which decides what happens to it. `critical`:
// a bug in code that runs (src/, tests/, the repository's tooling, which
// decides what merges and releases, and build and dependency configuration),
// or new or changed behaviour with no test, fixed in the review behind a regression
// test, and blocking until it is fixed, or until the person accepts one left
// open for them. `warning`: a security weakness, whatever file it's in, which
// blocks until the person fixes or accepts it. `advisory`: everything else
// (docs, comments, prompts, conventions), fixed in the review too, which never
// blocks: one left for a person goes to the branch's follow-up issue.
export const SEVERITIES = ["critical", "warning", "advisory"] as const;

export type Severity = (typeof SEVERITIES)[number];

// What a finding's path may look like. The host's reviewer schema (lib/host-review.mts) uses the same pattern, so a path the ledger would
// refuse is asked for again with the reason, not found out when the review is recorded.
export const FINDING_PATH_SHAPE = /^[^\s<>`]+( [^\s<>`]+)*$/;

// A finding's words are a reviewer agent's, who read hostile diff text: control, format and invisible characters are removed
// (stripControl) before they are checked, so none reaches the gate's message, the report or a PR description, and a text that was
// only such characters is refused as empty. The host's reviewer schema (lib/host-review.mts) uses the same two, so a finding the
// ledger would refuse is asked for again, not found out when the review is recorded.
export const findingWords = () => z.string().transform(stripControl).pipe(z.string().trim().min(1));

// A repository path: no markup or backtick, and no whitespace but single spaces between its parts (a folder name may hold one), so it can't
// carry anything into the PR description that plainText would have to undo: renderReport and the notes run it through plainText, and a tab, newline or double space never gets that far. It keeps its
// whitespace through the strip, so a tab or a double space is still refused rather than made a single space.
export const findingPath = () =>
  z
    .string()
    .transform(withoutInvisible)
    .pipe(z.string().trim().min(1).regex(FINDING_PATH_SHAPE, "a path has no whitespace but single spaces between its parts, and no <, > or backtick"));

// One finding. `fixed` needs the regression test that pins it, or the reason
// it has none (a workflow or prose finding); `dismissed` needs the evidence
// that the reviewer was wrong, read from the code; `accepted` needs the reason
// a person accepted it; `open` waits for a person, or for the follow-up issue
// when it's Advisory.
const findingFields = z.object({
  id: z.string().regex(/^R[1-9]\d{0,5}$/, "ids are R1, R2, ... up to R999999, with no leading zero"),
  lens: findingWords(),
  path: findingPath(),
  line: z.number().int().nonnegative(),
  summary: findingWords(),
  status: z.enum(["fixed", "dismissed", "accepted", "open"]),
  test: findingWords().nullable(),
  reason: findingWords().nullable(),
  // The reviewer's suggested fix, kept for the follow-up issue's Advisory
  // findings (renderAdvisories); the agent gives the person deciding an open
  // Critical or Warning finding its fix in the skill's step 9.
  fix: findingWords().nullable().optional(),
});

function checkOutcome(finding: z.infer<typeof findingFields>, context: z.RefinementCtx): void {
  if (finding.status === "fixed" && finding.test === null && finding.reason === null) {
    context.addIssue({ code: "custom", message: `${finding.id} is fixed but names neither its test nor why it has none` });
  }
  if ((finding.status === "accepted" || finding.status === "dismissed") && finding.reason === null) {
    context.addIssue({ code: "custom", message: `${finding.id} is ${finding.status} without a reason` });
  }
}

// A finding a review records: triage must grade every one, and decide whether
// it's a security weakness. The grade alone doesn't decide what's published: a
// weakness misgraded Critical or Advisory under a lens that doesn't say
// security would reach the follow-up issue or the notes, so `security` is
// always given, and a security weakness is always a Warning. A finding whose
// lens names security is never Advisory: it would pass the check without a
// person deciding it, and go to the public follow-up issue.
// Its `fix` is always given, null when unused, and given for every open
// finding: the person deciding it, or the follow-up issue, needs it. A security
// weakness (isSecurity) is recorded fixed only with a test, which may name a
// gate check: a reason alone leaves it open.
const recordedFields = findingFields.extend({ severity: z.enum(SEVERITIES), security: z.boolean(), fix: findingWords().nullable() });

function checkRecorded(finding: z.infer<typeof recordedFields>, context: z.RefinementCtx): void {
  if (finding.status === "open" && finding.fix === null) {
    context.addIssue({ code: "custom", message: `${finding.id} is open without a suggested fix` });
  }
  // isSecurity is what the check and the notes use, so the agent-written `security` flag alone can't dodge this.
  if (finding.status === "fixed" && finding.test === null && isSecurity(finding)) {
    context.addIssue({ code: "custom", message: `${finding.id} is a security weakness recorded fixed without a test: name its test or gate check, or leave it open with its suggested fix` });
  }
  if (finding.security && finding.severity !== "warning") {
    context.addIssue({ code: "custom", message: `${finding.id} is a security weakness: grade it warning, not ${finding.severity}` });
  }
  if (finding.severity === "advisory" && /security/i.test(finding.lens)) {
    context.addIssue({ code: "custom", message: `${finding.id}'s lens names security, so it's a security weakness: grade it warning, not advisory` });
  }
}

export const findingSchema = recordedFields.strict().superRefine(checkOutcome).superRefine(checkRecorded);

// A finding as `record` is handed it: the above, and two fields only the security record uses and the ledger never
// stores. `policy` is the one-sentence rule that now holds for a security weakness; `repeats` is the number of the
// entry (`0003`) triage judged the finding repeats, for a repeat worded differently from the entry.
const inputFindingSchema = recordedFields
  .extend({ policy: z.string().trim().min(1).nullable().optional(), repeats: z.string().regex(/^\d{4,}$/, "repeats is an entry number such as 0003").optional() })
  .strict()
  .superRefine(checkOutcome)
  .superRefine(checkRecorded);

// A finding as stored. A ledger recorded before `severity` existed has none,
// nor a `fix`, and still parses, so check, accept, report and latest read it;
// one recorded before `security` existed, or by a version that graded
// findings without requiring the mark, has no `security` mark. A finding with no severity counts as a Warning (see
// severityOf), so it blocks until a person decides it, and one with no mark
// counts as a security finding (see isSecurity).
// `fixAsked` is the person's choice, through `scripts/review.sh accept-open --fix`, that an open finding be fixed rather than accepted: the
// next run's triage fixes it whatever its needsPerson said. Only that command sets it (record's input has no such field), only on an
// open finding, and accepting the finding drops it.
export const storedFindingSchema = findingFields
  .extend({ severity: z.enum(SEVERITIES).optional(), security: z.boolean().optional(), fixAsked: z.literal(true).optional() })
  .strict()
  .superRefine(checkOutcome)
  .superRefine((finding, context) => {
    if (finding.fixAsked === true && finding.status !== "open") {
      context.addIssue({ code: "custom", message: `${finding.id} is ${finding.status}, so it can't be asked to be fixed` });
    }
  });

export type Finding = z.infer<typeof storedFindingSchema>;

// A finding's severity, a Warning when it has none.
export const severityOf = (finding: Finding): Severity => finding.severity ?? "warning";

// Whether a finding is a security weakness: one graded Warning, one whose lens
// names security whatever it was graded, or one triage didn't mark as no
// security weakness, so a misgraded or unmarked one isn't published. The report's mark,
// the notes, the follow-up issue and the check all ask this, so they never
// disagree.
export const isSecurity = (finding: Finding) =>
  finding.severity === "warning" || finding.security !== false || /security/i.test(finding.lens);

// Whether a finding blocks the push: open, and not Advisory, or a security
// finding whatever its grade. The host-side review holds a PR as a draft while one is open, or when the review didn't finish (lib/build.mts), as the gate's check refuses the push.
export const isBlocking = (finding: Finding) => finding.status === "open" && (severityOf(finding) !== "advisory" || isSecurity(finding));

// An agent type name, with the optional plugin namespace (plugin:agent). No dot: www.example would autolink in the PR body.
const reviewerAgentSchema = z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}(?::[A-Za-z0-9][A-Za-z0-9_-]{0,63})?$/, "reviewerAgent is an agent type name");

// What the skill hands to `record`: the key and the HEAD `scripts/review.sh
// prepare` printed when the last round started, from the patch its reviewers
// read; how many rounds ran; and every finding this run raised, with its
// outcome. Never `accepted`: only a person running `scripts/review.sh accept` (or `accept-open`)
// accepts a finding, on the ledger it was raised in, so no acceptance carries
// from one ledger to another.
export const reviewInputSchema = z
  .object({
    reviewedKey: z.string().min(1),
    reviewedHead: z.string().regex(/^[0-9a-f]{40}(?:[0-9a-f]{24})?$/, "reviewedHead is the commit id prepare printed"),
    rounds: z.number().int().min(1),
    // The agent type that read the diff (pre-push-reviewer, or the one that
    // stood in for it), so the report and the notes show who reviewed.
    reviewerAgent: reviewerAgentSchema.optional(),
    // The number of the PR the branch has, when there is one, for the security record's entries. Not kept in the ledger.
    pr: z.number().int().positive().optional(),
    findings: z.array(inputFindingSchema),
  })
  .strict()
  .superRefine((input, context) => checkFindingList(input.findings, context));

function checkFindingList(findings: readonly { id: string; status: string }[], context: z.RefinementCtx): void {
  const ids = findings.map((finding) => finding.id);
  const repeated = ids.filter((id, index) => ids.indexOf(id) !== index);
  if (repeated.length > 0) {
    context.addIssue({ code: "custom", message: `finding ids repeat: ${[...new Set(repeated)].join(", ")}` });
  }
  const accepted = findings.filter((finding) => finding.status === "accepted").map((finding) => finding.id);
  if (accepted.length > 0) {
    context.addIssue({
      code: "custom",
      message: `${accepted.join(", ")} can't be recorded as accepted: record it open, and the person accepts it with scripts/review.sh accept or accept-open`,
    });
  }
}

export type ReviewInput = z.infer<typeof reviewInputSchema>;

// What a list of findings must satisfy as a whole: `record` applies it to its input, and `stage` to what it stages and to what is staged already,
// so a finding `record` would refuse is refused where it enters.
const findingListSchema = z.array(inputFindingSchema).superRefine((findings, context) => checkFindingList(findings, context));

export const ledgerSchema = z
  .object({
    version: z.literal(1),
    key: z.string().min(1),
    branch: z.string(),
    base: z.string().min(1),
    head: z.string().min(1),
    recordedAt: z.string().min(1),
    rounds: z.number().int().min(1),
    reviewerAgent: reviewerAgentSchema.optional(),
    findings: z.array(storedFindingSchema),
    // The highest id any review of the branch had used when this one was
    // recorded, so a review that found nothing still holds the ids taken.
    lastId: z.number().int().min(0).optional(),
  })
  .strict();

export type Ledger = z.infer<typeof ledgerSchema>;

// Run git in `cwd` and return its stdout as bytes, with the hook variables
// dropped. Replace refs are off, so the review reads and keys commits as they
// are, not as a replace ref shows them.
function gitBytes(cwd: string, args: readonly string[]): Buffer {
  return hostExecFileSync("git", args, { cwd, env: ledgerGitEnv(cwd), stdio: ["ignore", "pipe", "pipe"], maxBuffer: 512 * 1024 * 1024 });
}

// Whether a git call was ended by the host's time limit (lib/host-limit.mts kills it with SIGKILL and fails with ETIMEDOUT) and not by
// git's own answer. The callers below read a git failure as "no" (not an ancestor, no such main, not the reviewed code); a call that
// never answered says nothing, so it is rethrown instead.
// Any SIGKILL counts, not only the host's own: a git that was killed (even out of memory) gave no answer either.
function endedByLimit(error: unknown): boolean {
  if (typeof error !== "object" || error === null) return false;
  const { code, signal } = error as { code?: unknown; signal?: unknown };
  return code === "ETIMEDOUT" || signal === "SIGKILL";
}

const git = (cwd: string, ...args: string[]) => gitBytes(cwd, args).toString("utf8").trim();

// A full object id, SHA-1 or SHA-256, in lower case: what a ledger's `head` and a commit `prepare` takes as `<since>` must be.
const OBJECT_ID = /^([0-9a-f]{40}|[0-9a-f]{64})$/;

// Whether `id` is a full object id (OBJECT_ID) of a commit this clone has. Not a tag, a branch name or a short id, which can name
// another commit tomorrow. The pattern lets no option through: an id can't start with a dash.
function isCommitId(cwd: string, id: string): boolean {
  if (!OBJECT_ID.test(id)) return false;
  try {
    gitBytes(cwd, ["rev-parse", "--quiet", "--verify", `${id}^{commit}`]);
    return true;
  } catch (error) {
    if (endedByLimit(error)) throw error;
    return false;
  }
}

// Whether `ancestor` is an ancestor of (or is) `commit`. A commit git doesn't
// have counts as not, and so does a name that starts with a dash: a ledger's
// head is text anyone can write, and git would read it as an option. That is
// checked here rather than with --end-of-options, which not every git the
// Template runs on may take for merge-base.
export function isAncestor(cwd: string, ancestor: string, commit: string): boolean {
  if (ancestor.startsWith("-") || commit.startsWith("-")) return false;
  try {
    gitBytes(cwd, ["merge-base", "--is-ancestor", ancestor, commit]);
    return true;
  } catch (error) {
    if (endedByLimit(error)) throw error;
    return false;
  }
}

// Where `head` (HEAD by default) left `main` (origin/main by default). Throws
// when there's no such main: the review can't say what the branch changed, so
// the check fails closed.
export function mergeBase(cwd: string, main: string = "origin/main", head: string = "HEAD"): string {
  try {
    return git(cwd, "merge-base", main, head);
  } catch (error) {
    if (endedByLimit(error)) throw error;
    throw new Error(`can't find where this branch left ${main}: fetch it first (for origin/main, run \`git fetch origin main\`)`, { cause: error });
  }
}

// HEAD's commit id, resolved once so a key and the head a ledger records
// describe the same commit even if HEAD moves meanwhile.
export const headCommit = (cwd: string) => git(cwd, "rev-parse", "--verify", "HEAD^{commit}");

// The `git diff` settings the patch's text depends on, pinned, so a key
// depends on the two trees and not on whose config made it (the explicit
// prefixes also override diff.noprefix and diff.mnemonicPrefix). A binary file is
// keyed by its blob ids, not its compressed bytes, so zlib's settings don't
// matter either. core.bigFileThreshold is git's default, since a file above it
// is printed as binary and reviewPatch would refuse a text file the person's
// config made look like one. Submodules are never ignored: a pointer bump is
// a change to review. A different git version could still print a patch
// differently; that fails closed, with a new review.
const pinnedDiff = [
  "-c", "core.quotePath=true",
  "-c", "core.bigFileThreshold=512m",
  "-c", "core.attributesFile=/dev/null",
  "-c", "diff.relative=false",
  "-c", "diff.suppressBlankEmpty=false",
  "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--full-index",
  "--unified=3", "--inter-hunk-context=0", "--diff-algorithm=myers", "--no-indent-heuristic",
  "--submodule=short", "--ignore-submodules=none", "-O/dev/null", "--src-prefix=a/", "--dst-prefix=b/",
];

// The extensions of files that are binary by nature: only files named as one
// of these may have a NUL in their first 8000 bytes, and git then shows the
// reviewers only their names, so a source or text file with a stray NUL is
// refused. (A byte order mark, and a binary file turned into text, are refused
// whatever the name.) The list errs short: a binary file with another
// extension is refused, and the person adds it here.
export const BINARY_EXTENSIONS: ReadonlySet<string> = new Set([
  "png", "jpg", "jpeg", "gif", "ico", "webp", "avif", "bmp", "tif", "tiff", "woff", "woff2", "ttf", "otf", "eot",
  "pdf", "zip", "gz", "tgz", "bz2", "xz", "7z", "jar", "nupkg", "snupkg",
  "wasm", "dll", "exe", "pdb", "so", "dylib", "bin", "db", "sqlite", "mp3", "mp4", "webm", "ogg", "wav", "mov",
]);

// The pinned patch of `head` against `base`: what the reviewers read. Throws
// when it shows text only as "Binary files differ", since the reviewers
// couldn't read it: a text file a diff attribute (in .gitattributes or the
// clone's info/attributes) marks binary, a binary file the branch turns into
// text, which git prints whole as binary because one side is, or UTF-16 or
// UTF-32 text (known by its byte order mark), whose NULs make it look binary,
// or a file with a NUL that isn't a binary format by its extension. A real
// binary file has a NUL byte in its first 8000 bytes, as git's own test for
// one does: a NUL past them doesn't make git print a text file as binary.
export function reviewPatch(cwd: string, base: string, head: string = headCommit(cwd)): Buffer {
  const patch = gitBytes(cwd, [...pinnedDiff, base, head]);
  const text = patch.toString("latin1");
  const absent = (blob: string | undefined) => blob === undefined || /^0+$/.test(blob);
  const binary = (blob: string | undefined) => !absent(blob) && gitBytes(cwd, ["cat-file", "blob", blob as string]).subarray(0, 8000).includes(0);
  // Git ends its lines with \n alone, and `^` under the m flag also matches after a lone \r, which a changed line may hold, so these
  // anchors are \n and the ends of the section.
  for (const section of text.split(/(?<=\n)(?=diff --git )/)) {
    if (!/(?:^|\n)Binary files [^\n]* differ(?=\n|$)/.test(section)) continue;
    const [before, after] = /(?:^|\n)index ([0-9a-f]+)\.\.([0-9a-f]+)/.exec(section)?.slice(1) ?? [];
    const file = /(?:^|\n)diff --git ([^\n]*)(?=\n|$)/.exec(section)?.[1] ?? "a file";
    const start = absent(after) ? Buffer.alloc(0) : gitBytes(cwd, ["cat-file", "blob", after as string]).subarray(0, 4);
    if ([[0xff, 0xfe], [0xfe, 0xff], [0, 0, 0xfe, 0xff]].some((bom) => Buffer.from(bom).equals(start.subarray(0, bom.length)))) {
      // Rewriting a file main already holds this way would look like binary
      // turned into text (below), so that file needs a new path.
      const where = absent(before) ? "" : " at a new path, and delete this one";
      throw new Error(`${file}: this text is UTF-16 or UTF-32, which git shows only as "Binary files differ", so the reviewers couldn't read it. Re-encode it as UTF-8${where}.`);
    }
    // A file git prints as binary that isn't a binary format may be text
    // with a stray NUL (R42): the bytes can't tell them apart, since one
    // invalid UTF-8 byte makes text look like a real binary file, so the
    // extension does.
    const extension = /\.([a-z0-9]+)"?$/i.exec(file)?.[1]?.toLowerCase() ?? "";
    if (!absent(after) && binary(after) && !BINARY_EXTENSIONS.has(extension)) {
      throw new Error(`${file}: this file holds a NUL byte, so git shows it only as "Binary files differ" and the reviewers couldn't read it, and its extension isn't a binary format's. Remove the NUL; or, for a real binary file, give it its format's extension (see BINARY_EXTENSIONS in review-ledger.mts).`);
    }
    // An emptied file has no text to read, so there's nothing hidden to refuse.
    const emptied = !absent(after) && gitBytes(cwd, ["cat-file", "blob", after as string]).length === 0;
    if (binary(before) && !absent(after) && !binary(after) && !emptied) {
      throw new Error(`${file}: the branch turns a binary file (or UTF-16 or UTF-32 text) into text, which git shows only as "Binary files differ", so the reviewers couldn't read the text. Add the text at a new path instead.`);
    }
    // Not for a deleted or emptied file either: the old text is on main and the new side has none.
    if (!absent(after) && !emptied && !binary(before) && !binary(after)) {
      throw new Error(`${file}: a diff attribute marks this text file binary, so the reviewers couldn't read its change. Remove the attribute.`);
    }
  }
  return patch;
}

// The ledger key of a patch: a SHA-256 of each file's part of it. A text
// file's `index` line (blob ids, which change when main changes another part
// of the same file) is left out and its hunk headers (line numbers, and the
// function names .gitattributes can change) are reduced to `@@`, leaving each
// hunk's context and changed lines: the branch's own change. A binary file
// has only its header, so its `index` line, with full blob ids, is kept.
export function patchKey(patch: Buffer): string {
  if (patch.length === 0) return EMPTY_DIFF_KEY;
  const hash = createHash("sha256");
  let section: Buffer[] = [];
  const flush = () => {
    const binary = section.some((line) => line.subarray(0, 13).toString("latin1") === "Binary files ");
    for (const line of section) {
      const text = line.subarray(0, 6).toString("latin1");
      if (!binary && text === "index ") continue;
      hash.update(!binary && text.startsWith("@@ ") ? Buffer.from("@@\n") : line);
    }
    section = [];
  };
  let start = 0;
  while (start < patch.length) {
    const newline = patch.indexOf(10, start);
    const end = newline === -1 ? patch.length : newline + 1;
    const line = patch.subarray(start, end);
    if (line.subarray(0, 11).toString("latin1") === "diff --git ") flush();
    section.push(line);
    start = end;
  }
  flush();
  return hash.digest("hex");
}

// How big a branch a review is asked to cover. Past either limit (strictly more, so exactly 30 files or 3,000 lines is fine) the review
// warns that the branch is better split: the rounds and the findings grow with the diff. The warning only advises.
export const REVIEW_SIZE_MAX_FILES = 30;
export const REVIEW_SIZE_MAX_LINES = 3000;

// The bytes of a path git quoted (C-style escapes, octal for non-ASCII), as text.
function unquote(quoted: string): string {
  const bytes: number[] = [];
  const escapes: Record<string, number> = { n: 10, t: 9, r: 13, a: 7, b: 8, f: 12, v: 11, '"': 34, "\\": 92 };
  for (let index = 0; index < quoted.length; index++) {
    const char = quoted[index]!;
    if (char !== "\\") {
      bytes.push(...Buffer.from(char));
      continue;
    }
    const octal = /^[0-3][0-7]{2}/.exec(quoted.slice(index + 1, index + 4))?.[0];
    if (octal !== undefined) {
      bytes.push(Number.parseInt(octal, 8));
      index += 3;
    } else {
      bytes.push(escapes[quoted[index + 1] ?? ""] ?? (quoted[index + 1] ?? "\\").charCodeAt(0));
      index += 1;
    }
  }
  return Buffer.from(bytes).toString("utf8");
}

// The path in a `diff --git` header's second half, and the rest after a quoted first half.
function quotedToken(text: string): [string, string] | undefined {
  const match = /^"((?:[^"\\]|\\.)*)"/.exec(text);
  return match === null ? undefined : [unquote(match[1]!), text.slice(match[0].length).trimStart()];
}

// The repository paths a patch changes, in patch order, once each, read from its `diff --git` headers. The review's patch
// has renames off, so both halves of a header name the same path; a deleted file keeps its old one.
export function patchPaths(patch: string): string[] {
  const paths = new Set<string>();
  for (const line of patch.split("\n")) {
    if (!line.startsWith("diff --git ")) continue;
    const rest = line.slice("diff --git ".length);
    let path: string | undefined;
    const first = quotedToken(rest);
    if (first !== undefined) {
      const second = quotedToken(first[1]);
      path = (second === undefined ? first[1] : second[0]).replace(/^b\//, "");
    } else {
      // Unquoted: "a/<p> b/<p>". With spaces in <p> only the midpoint splits it.
      const half = (rest.length - 5) / 2;
      path = Number.isInteger(half) && rest.slice(0, 2) === "a/" && rest.slice(2, 2 + half) === rest.slice(half + 5) ? rest.slice(2, 2 + half) : /^a\/.+ b\/(.+)$/.exec(rest)?.[1];
    }
    // A header it can't read still counts as a changed file, so no lens is skipped for it.
    paths.add(path ?? rest);
  }
  return [...paths];
}

// In the Application Factory, which owns the Template, an Owned Template file is held twice, at the repository root and under this path. In an
// Application nothing starts with it, so everything that reads it does nothing there.
export const TEMPLATE_OWNED_PREFIX = ".claude/skills/repo-ci-baseline/template/owned/";

// The patch the reviewers read: `patch` without the sections of verbatim Template mirrors. A section is a mirror when its path is
// TEMPLATE_OWNED_PREFIX + <P> and the same patch has a root section for <P> with the same final state (the same after-blob and the same
// final mode, or both deleted). A path can have two sections (git writes a deletion and an addition for a type change, such as a symlink that
// becomes a file): they are matched in order, the first of the mirror's with the first of the root's, so a type change on both sides leaves out
// both of the mirror's sections and never one. `test.sh` already fails when a copy drifts, so reading both only doubles the reading. Anything else
// stays: a mirror with no root section, a mirror that differs from its root copy (drift), a quoted or unreadable header, a section with no
// after-blob. The key is not computed from this: it stays that of the whole diff. The patch is read at LF only, byte for byte (latin1), so the kept
// sections are the input's own bytes; the same bytes are returned when nothing is left out.
export function readerPatch(patch: Buffer): Buffer {
  const text = patch.toString("latin1");
  const parts = text.split(/(?<=\n)(?=diff --git )/);
  // The lines before the first hunk are git's own header; a changed line can't start with `index `, and the lines are cut at LF only.
  const finalState = (part: string): string | undefined => {
    const header: string[] = [];
    for (const line of part.split("\n")) {
      if (line.startsWith("@@ ")) break;
      header.push(line);
    }
    if (header.some((line) => line.startsWith("deleted file mode "))) return "deleted";
    const blob = header.map((line) => /^index [0-9a-f]+\.\.([0-9a-f]+)(?: (\d+))?$/.exec(line)).find((found) => found !== null && found !== undefined);
    if (blob === undefined || blob === null) return undefined;
    const mode =
      header.map((line) => /^new mode (\d+)$/.exec(line)?.[1]).find((found) => found !== undefined) ??
      header.map((line) => /^new file mode (\d+)$/.exec(line)?.[1]).find((found) => found !== undefined) ??
      blob[2];
    return mode === undefined ? undefined : `${blob[1]}:${mode}`;
  };
  const pathOf = (part: string): string | undefined => {
    const header = part.slice(0, part.indexOf("\n") === -1 ? undefined : part.indexOf("\n"));
    if (!header.startsWith("diff --git a/")) return undefined;
    const path = patchPaths(header)[0];
    return path !== undefined && header === `diff --git a/${path} b/${path}` ? path : undefined;
  };
  // The final states of a path's sections, in order (git writes two for a type change). A mirror is left out only when its whole sequence equals
  // the root copy's, so a mirror that deletes while its root copy deletes and creates a symlink is kept.
  const sections = parts.map((part) => {
    const path = pathOf(part);
    return { part, path, state: path === undefined ? undefined : finalState(part) };
  });
  const sequences = new Map<string, (string | undefined)[]>();
  for (const { path, state } of sections) if (path !== undefined) sequences.set(path, [...(sequences.get(path) ?? []), state]);
  const sameSequence = (mirror: string): boolean => {
    const root = sequences.get(mirror.slice(TEMPLATE_OWNED_PREFIX.length));
    const own = sequences.get(mirror);
    return root !== undefined && own !== undefined && root.length === own.length && root.every((state, at) => state !== undefined && state === own[at]);
  };
  const kept = sections.filter(({ path }) => path === undefined || !path.startsWith(TEMPLATE_OWNED_PREFIX) || !sameSequence(path));
  return kept.length === parts.length ? patch : Buffer.from(kept.map(({ part }) => part).join(""), "latin1");
}

// The files and lines (added plus deleted) a patch changes, from the hunks. The patch is read line by line at `\n` only: a lone carriage return,
// or a Unicode line separator, inside a changed line is file content and starts nothing. Sections of the same path (git writes a deletion and an
// addition for a type change, such as a symlink that becomes a file) add up. A file and its Template mirror count once, and their lines once
// (the larger of the two, since `test.sh` makes them equal). A binary file, a mode change and an empty file count as a file with no lines.
export function branchSize(patch: Buffer | string): { files: number; lines: number } {
  const text = typeof patch === "string" ? patch : patch.toString("latin1");
  const byPath = new Map<string, number>();
  let current: string | undefined;
  let inHunk = false;
  for (const line of text.split("\n")) {
    if (line.startsWith("diff --git ")) {
      // A header patchPaths can't read still counts as a file (keyed by its text), as it does for the reviewers' lens choice: a size is never undercounted.
      current = patchPaths(line)[0];
      inHunk = false;
      if (current !== undefined && !byPath.has(current)) byPath.set(current, 0);
    } else if (current !== undefined) {
      // The file header's own `---` and `+++` lines come before the first hunk header; a body line starts with a space, `+`, `-` or `\`
      // (so an added line that reads `++ x` is still one added line, and "\ No newline" is none).
      if (!inHunk) inHunk = line.startsWith("@@ ");
      else if (line.startsWith("+") || line.startsWith("-")) byPath.set(current, (byPath.get(current) ?? 0) + 1);
    }
  }
  const lines = new Map<string, number>();
  for (const [path, changed] of byPath) {
    const key = path.startsWith(TEMPLATE_OWNED_PREFIX) ? path.slice(TEMPLATE_OWNED_PREFIX.length) : path;
    lines.set(key, Math.max(lines.get(key) ?? 0, changed));
  }
  let total = 0;
  for (const count of lines.values()) total += count;
  return { files: lines.size, lines: total };
}

// The warning for a branch past the limits, or undefined.
export function sizeWarning(patch: Buffer | string): string | undefined {
  const { files, lines } = branchSize(patch);
  if (files <= REVIEW_SIZE_MAX_FILES && lines <= REVIEW_SIZE_MAX_LINES) return undefined;
  const count = (n: number, noun: string) => `${n.toLocaleString("en-US")} ${noun}${n === 1 ? "" : "s"}`;
  return `this branch changes ${count(files, "file")} and ${count(lines, "line")}, past ${count(REVIEW_SIZE_MAX_FILES, "file")} or ${count(REVIEW_SIZE_MAX_LINES, "line")} (a file and its Template mirror count once): a review this size takes more rounds and keeps finding problems in its own fixes. Consider splitting the branch before pushing it.`;
}

// The ledger key for `head`'s diff against `base`.
export const diffKey = (cwd: string, base: string, head: string = headCommit(cwd)) => patchKey(reviewPatch(cwd, base, head));

// Whether `head` is the code reviewed at `reviewedHead`: that commit, or it
// with a clean merge of `base` and nothing else. A rebase onto `base` with no
// conflict passes too when its tree is that merge's, which is the tree this
// compares. The key alone can't tell: an
// edit moved to an identical block elsewhere in a file keeps it. Anything git
// can't answer (a commit it doesn't have, a conflict) counts as not.
export function isReviewedCode(cwd: string, reviewedHead: string, base: string, head: string): boolean {
  if (head === reviewedHead) return true;
  try {
    const merged = git(cwd, "merge-tree", "--write-tree", reviewedHead, base).split("\n")[0];
    return merged === git(cwd, "rev-parse", `${head}^{tree}`);
  } catch (error) {
    if (endedByLimit(error)) throw error;
    return false;
  }
}

// Whether this git has `merge-tree --write-tree` (2.38 and later), which
// isReviewedCode needs to see a merge of main, or a rebase onto it, as the
// reviewed code.
export function mergeTreeWorks(cwd: string): boolean {
  const head = headCommit(cwd);
  try {
    git(cwd, "merge-tree", "--write-tree", head, head);
    return true;
  } catch (error) {
    if (endedByLimit(error)) throw error;
    return false;
  }
}

// The folder ledgers live in, shared by every worktree of the clone: the
// review folder in the common directory git names.
export function ledgerDir(cwd: string): string {
  const common = git(cwd, "rev-parse", "--git-common-dir");
  return join(isAbsolute(common) ? common : join(cwd, common), "review");
}

const ledgerPath = (cwd: string, key: string) => join(ledgerDir(cwd), `${key}.json`);

// The ledger recorded for `key`, or undefined when there's none. Throws on a
// ledger that doesn't parse, naming it: a damaged ledger isn't a passed
// review.
export function readLedger(cwd: string, key: string): Ledger | undefined {
  const path = ledgerPath(cwd, key);
  if (!existsSync(path)) return undefined;
  try {
    return ledgerSchema.parse(JSON.parse(readFileSync(path, "utf8")));
  } catch (error) {
    throw new Error(`the review recorded at ${path} is damaged, so it doesn't count: run /pre-push-review again to record a new one`, { cause: error });
  }
}

// Write a ledger whole: to a file of its own, then renamed over the old one,
// so a reader never sees half a ledger.
function writeLedger(cwd: string, ledger: Ledger): void {
  // Owner-only (a ledger can hold accepted security findings), through a temp file of an unpredictable name that is made exclusively.
  replaceFile(ledgerPath(cwd, ledger.key), `${JSON.stringify(ledger, null, 2)}\n`);
}

// Which security entry each of a ledger's findings was recorded against, kept beside the ledger (`<key>.entries`, not
// `.json`, so no reader of ledgers sees it) and not in it, so the ledger is as before. `accept` reads it: a finding matched
// to an entry through `repeats` in other words, or to a fixed entry, can't be found again from its own text.
const entriesPath = (cwd: string, key: string) => join(ledgerDir(cwd), `${key}.entries`);

function writeEntryMap(cwd: string, key: string, entries: Record<string, string>): void {
  const path = entriesPath(cwd, key);
  // Nothing recorded against an entry: no file, and none left over from an earlier record of this diff.
  if (Object.keys(entries).length === 0) {
    rmSync(path, { force: true });
    return;
  }
  replaceFile(path, `${JSON.stringify(entries)}\n`);
}

// The entry ids the findings of the ledger at `key` were recorded against (finding id to entry id); empty when unknown (an
// older record, or none written: a missing file is said nothing of). A pair that isn't a finding id (`R<n>`) and an entry number
// (four or more digits) is left out. `warn` hears of a file that is there but doesn't read or parse as such a map, or holds a
// pair that isn't one: a re-raise is then matched by its text alone.
function readEntryMapAll(cwd: string, key: string, warn?: (message: string) => void): Record<string, string> {
  const path = entriesPath(cwd, key);
  let entries: unknown;
  try {
    entries = JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") warn?.(`couldn't read ${path}, which notes the entry each of a review's findings went to, so a finding of it raised again is matched by its text`);
    return {};
  }
  if (typeof entries !== "object" || entries === null || Array.isArray(entries)) {
    warn?.(`${path} isn't a map of finding ids to entries, so a finding of that review raised again is matched by its text`);
    return {};
  }
  const pairs = Object.entries(entries);
  const kept = pairs.filter((pair): pair is [string, string] => /^R[1-9]\d*$/.test(pair[0]) && typeof pair[1] === "string" && /^\d{4,}$/.test(pair[1]));
  if (kept.length < pairs.length) warn?.(`${path} holds a pair that isn't a finding id and an entry number, so those findings, raised again, are matched by their text`);
  return Object.fromEntries(kept);
}

// The entry id `findingId` was recorded against, or undefined when unknown (an older record, or none written).
function readEntryMap(cwd: string, key: string, findingId: string): string | undefined {
  return readEntryMapAll(cwd, key)[findingId];
}

// A finding the review raises again under a new id opens its summary with the old one, "R3 again", or, for a weakness only
// the security record holds, ".security/0012 again"; its words change from review to review, so its fingerprint does too.
// Case and spacing are set aside, as the fingerprint sets them aside, and so is markup around the id ("**R3** again",
// "(R3 again)", "`R3` again"); of a list ("R3, R5 again") the first id is the one read. An id is `R` and a number with no
// leading zero, or `.security/` and four or more digits.
const RE_RAISE_ID = String.raw`(?:r[1-9]\d*|\.security\/\d{4,})`;
const RE_RAISE = new RegExp(
  String.raw`^[\s*_\x60(\[]*(${RE_RAISE_ID})[*_\x60)\]]*(?:\s*(?:,|and|&)\s*[*_\x60(\[]*${RE_RAISE_ID}[*_\x60)\]]*)*\s+[*_\x60]*again\b`,
  "i",
);

// What a summary's opening says it raises again: `named`, the id it names, normalised ("R3", ".security/0012"), and `entry`, the
// entry that is: the one R<n> was recorded against (`earlier`, finding id to entry id, from the branch's own earlier reviews),
// or the number it names. `entry` is undefined for an R<n> no earlier review recorded against an entry. The whole result is
// undefined when the summary opens neither way: then it is matched by its text, as before. Only the opening counts, so a summary
// that mentions an entry or an id in passing ("Beyond .security/0017") is not a repeat of it.
export function reRaisedEntry(summary: string, earlier: Readonly<Record<string, string>>): { named: string; entry?: string } | undefined {
  const id = RE_RAISE.exec(summary)?.[1];
  if (id === undefined) return undefined;
  const number = /^\.security\/(\d+)$/i.exec(id)?.[1];
  if (number !== undefined) return { named: `.security/${number}`, entry: number };
  const named = id.toUpperCase();
  const entry = earlier[named];
  return entry === undefined ? { named } : { named, entry };
}

// A summary that opens with an id and says "again" near it, but not in a form reRaisedEntry reads ("R3: again", "R3 raised
// again", "[R3](link) again", "<b>R3</b> again", or "R3again" once invisible characters are dropped): the id it opens with, so
// the person is told it was matched by its text; undefined for any other summary, and for one reRaisedEntry reads. "again" counts
// as a word of its own (not "against" or "againstuff"; a digit before it, as in "R3again", is allowed) within NEAR_WINDOW
// characters after the id.
const NEAR_RE_RAISE = new RegExp(String.raw`^(?:[\s*_\x60(\[]|<[a-z]+>)*(${RE_RAISE_ID})(?![0-9])`, "i");
const NEAR_WINDOW = 40;
export function nearReRaise(summary: string): string | undefined {
  if (RE_RAISE.test(summary)) return undefined;
  const found = NEAR_RE_RAISE.exec(summary);
  if (found?.[1] === undefined) return undefined;
  // The word is matched on the uncut text, so a longer word reaching past the window's edge ("against") isn't cut down to "again".
  const word = /(?<![a-z])again(?![a-z])/i.exec(summary.slice(found[0].length));
  if (word === null || word.index + "again".length > NEAR_WINDOW) return undefined;
  const number = /^\.security\/(\d+)$/i.exec(found[1])?.[1];
  return number === undefined ? found[1].toUpperCase() : `.security/${number}`;
}

// How long a lock that names no process stands (empty, or not "pid token"): its holder may be about to write its id.
const STALE_LOCK_MS = 60_000;

// Whether a lock with this `content`, last written `ageMs` ago, was left behind. One that names a process is stale when that process
// is gone: only EPERM (a process we may not signal) means it is running, and an id no process can have (too large for the platform)
// is gone. One that names none is stale after STALE_LOCK_MS. A running process keeps its lock whatever its age: a pid proves nothing
// about the holder (it may have been reused), so such a lock fails closed and the error names the file to delete.
export function lockIsStale(content: string, ageMs: number): boolean {
  const pid = /^([1-9]\d*) /.exec(content)?.[1];
  if (pid === undefined) return ageMs > STALE_LOCK_MS;
  try {
    process.kill(Number(pid), 0);
    return false;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== "EPERM";
  }
}

// Test seams, called between the steps of taking over a stale lock, so a test can make another command act there.
export const lockTestHooks: { beforeRead?: () => void; afterRead?: () => void; afterRename?: () => void; beforeWrite?: () => void } = {};

// The text of the file at `path`, or undefined when there is none. Any other read error (a directory, no permission, no file
// handles left) throws: a lock that can't be read is not one to break.
function textOf(path: string): string | undefined {
  try {
    return readFileSync(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

// Run `change` holding the review folder's lock, so two writers (a `record`
// and an `accept`) can't interleave. One lock for every key, not one per key:
// a record reads every review of the branch for the ids taken (takenIds), so a
// record on another diff mustn't write between that read and its own write.
// The lock names its holder ("pid token"). A lock left by a process that was killed is taken over: renamed to a name of our own
// (one command wins the rename), judged again there by its own text and age, and removed only if that still says stale; if not (a live lock was made in
// between), it is put back and the command refuses. Nothing at the shared name is ever removed without being read first, so a live
// lock is not deleted from under its holder, though a third command taking the name in the instant it is put back could still
// share the lock: the takeover is best-effort, and a stale lock is rare. The holder removes the lock only while it still carries its
// token. Fails at once, naming the lock, when a running command holds it.
function withLock<T>(cwd: string, change: () => T): T {
  mkdirSync(ledgerDir(cwd), { recursive: true, mode: 0o700 });
  chmodSync(ledgerDir(cwd), 0o700);
  const lock = join(ledgerDir(cwd), ".lock");
  return holdLock(lock, (cause) => new Error(`another review command is writing a review; try again, or delete ${lock} if none is running`, { cause }), change);
}

// Run `change` holding the lock file `lock`, as withLock describes: made owner-only with "wx", named "pid token", taken over when its
// holder is gone (lockIsStale), and removed only while it still carries the token. `refuse` makes the error for a lock a running
// command holds. `close` is the descriptor close, a seam for a test that makes it fail: the lock is released all the same.
function holdLock<T>(lock: string, refuse: (cause?: unknown) => Error, change: () => T, close: (handle: number) => void = closeSync): T {
  const token = randomBytes(8).toString("hex");
  const mine = `${process.pid} ${token}\n`;
  let handle: number | undefined;
  for (let attempt = 0; handle === undefined; attempt += 1) {
    try {
      handle = openSync(lock, "wx", 0o600);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
      if (attempt > 0) throw refuse(error);
      let seen: string | undefined;
      let ageMs = 0;
      lockTestHooks.beforeRead?.();
      try {
        seen = textOf(lock);
        if (seen !== undefined) ageMs = Date.now() - statSync(lock).mtimeMs;
      } catch {
        throw refuse(error);
      }
      // Gone since the open failed: the next open decides, and nothing is removed.
      if (seen === undefined) continue;
      if (!lockIsStale(seen, ageMs)) throw refuse(error);
      lockTestHooks.afterRead?.();
      const moved = `${lock}.stale.${token}`;
      try {
        renameSync(lock, moved);
      } catch (renameError) {
        // Another command took it over first: the next open decides.
        if ((renameError as NodeJS.ErrnoException).code === "ENOENT") continue;
        throw refuse(renameError);
      }
      lockTestHooks.afterRename?.();
      // Judge the lock moved again, by its own text and age (a rename keeps the age): equal text proves nothing for an empty or
      // tokenless lock, and a fresh one made in the meantime is not stale.
      let movedStale = false;
      try {
        movedStale = lockIsStale(readFileSync(moved, "utf8"), Date.now() - statSync(moved).mtimeMs);
      } catch {
        movedStale = false;
      }
      if (movedStale) {
        rmSync(moved, { force: true });
        continue;
      }
      // Not a stale lock: a live one was made in the meantime. Put it back if its name is free, and refuse.
      try {
        linkSync(moved, lock);
      } catch {
        // The name was taken again; the lock moved is released early, which nothing here can undo.
      }
      rmSync(moved, { force: true });
      throw refuse();
    }
  }
  const held = handle;
  try {
    lockTestHooks.beforeWrite?.();
    writeSync(held, mine);
  } catch (error) {
    // The lock is ours (just made with "wx") and empty: leaving it would refuse every command for a minute. It goes even when
    // the close fails too, and the write's error is the one thrown.
    try {
      close(held);
    } catch {
      // The write's error says what went wrong.
    } finally {
      rmSync(lock, { force: true });
    }
    throw error;
  }
  try {
    return change();
  } finally {
    // The lock goes even when the close fails, and only while it is still ours.
    try {
      close(held);
    } finally {
      let current: string | undefined;
      try {
        current = textOf(lock);
      } catch {
        current = undefined;
      }
      if (current === mine) rmSync(lock, { force: true });
    }
  }
}

// The nearest earlier review of this branch's own work: the ledger recorded
// at the commit nearest HEAD among HEAD's ancestors that main doesn't have.
// Context for the next review: its reviewers don't raise again what was fixed
// or dismissed, and raise again what was accepted or left open, for the person
// to decide for the new code. Nothing is carried from it. A file that doesn't
// read or parse is skipped.
export function latestLedger(cwd: string, base: string, head: string = headCommit(cwd)): Ledger | undefined {
  let latest: { ledger: Ledger; distance: number } | undefined;
  for (const { ledger, distance } of branchLedgers(cwd, base, head)) {
    if (latest === undefined || distance < latest.distance || (distance === latest.distance && ledger.recordedAt > latest.ledger.recordedAt)) {
      latest = { ledger, distance };
    }
  }
  return latest?.ledger;
}

// Every review recorded at one of the branch's own commits (HEAD's ancestors
// that main doesn't have), with how many commits each is behind HEAD.
function branchLedgers(cwd: string, base: string, head: string): { ledger: Ledger; distance: number }[] {
  const found: { ledger: Ledger; distance: number }[] = [];
  for (const { ledger } of readLedgers(cwd)) {
    if (!isAncestor(cwd, ledger.head, head) || isAncestor(cwd, ledger.head, base)) continue;
    found.push({ ledger, distance: Number(git(cwd, "rev-list", "--count", `${ledger.head}..${head}`)) });
  }
  return found;
}

// Every ledger of the clone that reads and parses, with its file name; a file that doesn't is skipped.
function readLedgers(cwd: string): { ledger: Ledger; name: string }[] {
  const dir = ledgerDir(cwd);
  if (!existsSync(dir)) return [];
  const found: { ledger: Ledger; name: string }[] = [];
  for (const name of readdirSync(dir)) {
    if (!name.endsWith(".json")) continue;
    try {
      const parsed = ledgerSchema.safeParse(JSON.parse(readFileSync(join(dir, name), "utf8")));
      if (parsed.success) found.push({ ledger: parsed.data, name });
    } catch {
      continue;
    }
  }
  return found;
}

// The branch's own commits: HEAD and its ancestors that main (`base`) doesn't have, from one `git rev-list`, so a ledger's head is
// looked up rather than asked of git once per ledger. A commit this clone doesn't have, or text that isn't a full object id, is not
// among them, so it counts as not the branch's.
function branchCommits(cwd: string, base: string, head: string): Set<string> {
  return new Set(git(cwd, "rev-list", head, `^${base}`).split("\n").filter((line) => line !== ""));
}

// The first ledger file (`*.json` in the ledger folder, in name order) that can't be read, isn't JSON, or is JSON that fails the
// schema and may be one of the branch's reviews: recordedAnchor can't rule it out as the review it should anchor at, which may
// hold findings left open or accepted. A malformed ledger rules itself out only when it names another branch and a `head` that
// isn't one of the branch's own commits (branchCommits), as a review recordedAnchor would not take either.
function unreadableLedger(cwd: string, branch: string, own: Set<string>): string | undefined {
  const dir = ledgerDir(cwd);
  if (!existsSync(dir)) return undefined;
  for (const name of readdirSync(dir).filter((entry) => entry.endsWith(".json")).sort()) {
    const path = join(dir, name);
    let parsed: unknown;
    try {
      parsed = JSON.parse(readFileSync(path, "utf8"));
    } catch {
      return path;
    }
    if (ledgerSchema.safeParse(parsed).success) continue;
    const fields = typeof parsed === "object" && parsed !== null ? (parsed as { branch?: unknown; head?: unknown }) : {};
    const elsewhere = typeof fields.branch === "string" && fields.branch !== branch && typeof fields.head === "string" && !own.has(fields.head);
    if (!elsewhere) return path;
  }
  return undefined;
}

// What a refusal that starts the review over tells the person to run.
const RESTART = "start again at round 1 with prepare <dir>.";

// The anchor of a delta review: the commit the branch's last review vouched for (its ledger's `head`), so a fix made after the
// review was recorded (for a PR thread, say) is reviewed alone in a new run. The branch's reviews are taken as branchLedgers takes
// them, whatever name they were recorded under (a detached HEAD, a rename, a fast-forward from a sibling): every readable ledger
// whose head is one of the branch's own commits (branchCommits). The anchor is the last of them by ancestry, the head every other
// head is an ancestor of (`git merge-base --independent` gives it alone), never by recordedAt; heads that don't form one line
// refuse. Never a commit the caller names either, so the fixes patch holds every commit since the code a review vouched for.
//
// A finding left open or accepted by a review at an earlier head is superseded by a review of a descendant head recorded after it:
// that review read the earlier head's code too (round 1 reads the whole diff, and a recorded run anchors only at a review that left
// nothing open or accepted), so it raised the finding again or found it gone. That holds only in that order: a review at an earlier
// head recorded after the anchor's (out of order) was never read by it, so its open or accepted findings refuse. recordedAt decides
// only that order and whether a review off HEAD's history is later than the reviews on it, and it orders the messages.
//
// Refused, as a message, also:
// - on a detached HEAD, and when a ledger file may be one of these reviews but doesn't read or parse (unreadableLedger), or names
//   this branch or none with a head that isn't a full object id: it counts as no review, and round 1 and record leave it in place.
//   readLedgers still skips the first kind for branchLedgers, which latestLedger and takenIds use; the gate's check reads only the
//   diff's own ledger, with readLedger, and refuses a damaged one.
// - when a review recorded under this branch's name isn't one of HEAD's commits and was recorded after every review that is:
//   an amend or a rebase since, whose reviewed code is gone. An older review is not taken instead.
// - when no review of the branch's own commits is recorded, saying so when the last one of this name is at a commit main has.
// - when any review at the anchor's head holds a finding open or accepted (blocking or not, asked to be fixed or not, security
//   or not): the reviewers of a recorded run read only the fixes, so it would never be raised again for this code. Round 1
//   reads the whole diff and raises it again.
export function recordedAnchor(cwd: string, base: string, head: string, branch: string): { commit: string } | string {
  if (branch === "") return `HEAD isn't on a branch, so no review recorded for one can be its anchor: ${RESTART}`;
  const own = branchCommits(cwd, base, head);
  const noReview = (path: string, what: string) =>
    `the ledger ${path} ${what}, and may be the newest review of ${branch}, so a recorded run can't anchor while it is there. ` +
    `It counts as no review, and round 1 and record leave it in place: delete or move it to let recorded runs anchor again, or ${RESTART}`;
  const unreadable = unreadableLedger(cwd, branch, own);
  if (unreadable !== undefined) return noReview(unreadable, "doesn't read or parse as a review");
  const all = readLedgers(cwd);
  const badHead = all.find(({ ledger }) => !OBJECT_ID.test(ledger.head) && (ledger.branch === branch || ledger.branch === ""));
  if (badHead !== undefined) {
    return noReview(join(ledgerDir(cwd), badHead.name), `names its head ${JSON.stringify(printable(badHead.ledger.head))}, which isn't a full object id`);
  }
  const reviews = all.map(({ ledger }) => ledger).filter((ledger) => own.has(ledger.head));
  // The latest recorded; at one recordedAt, the greater head, so a message names the same commit whatever order the folder gives.
  const later = (a: Ledger, b: Ledger) => (a.recordedAt !== b.recordedAt ? a.recordedAt > b.recordedAt : a.head > b.head);
  const latestOf = (ledgers: Ledger[]) => ledgers.reduce<Ledger | undefined>((found, ledger) => (found === undefined || later(ledger, found) ? ledger : found), undefined);
  const named = all.map(({ ledger }) => ledger).filter((ledger) => ledger.branch === branch);
  const gone = latestOf(named.filter((ledger) => !isAncestor(cwd, ledger.head, head)));
  const newestReview = latestOf(reviews);
  if (gone !== undefined && (newestReview === undefined || gone.recordedAt > newestReview.recordedAt)) {
    return `the last review of ${branch} was recorded at ${gone.head}, which isn't one of HEAD's commits (an amend or a rebase since): ${RESTART}`;
  }
  if (newestReview === undefined) {
    const merged = latestOf(named);
    if (merged !== undefined) return `the last review of ${branch} was recorded at ${merged.head}, which main already has: ${RESTART}`;
    return `no review of ${branch}'s own commits is recorded in this clone: ${RESTART}`;
  }
  const heads = [...new Set(reviews.map((ledger) => ledger.head))].sort();
  const independent = heads.length === 1 ? heads : git(cwd, "merge-base", "--independent", ...heads).split("\n").filter((line) => line !== "");
  if (independent.length !== 1) {
    return `the reviews of ${branch}'s own commits were recorded at ${heads.join(", ")}, which don't form one line (none has every other as an ancestor), so none is the last: ${RESTART}`;
  }
  const last = independent[0] as string;
  const undecided = (ledger: Ledger) => ledger.findings.filter((finding) => finding.status === "open" || finding.status === "accepted").length;
  const atLast = reviews.filter((ledger) => ledger.head === last);
  const open = atLast.reduce((sum, ledger) => sum + undecided(ledger), 0);
  if (open > 0) {
    return `the last review of ${branch}, recorded at ${last}, has ${open} finding(s) open or accepted, which a review of the fixes alone would not raise again: ${RESTART}`;
  }
  // R35: a review at an earlier head recorded after the anchor's was never read by it.
  const anchorRecorded = (latestOf(atLast) as Ledger).recordedAt;
  const unread = latestOf(reviews.filter((ledger) => ledger.head !== last && undecided(ledger) > 0 && ledger.recordedAt > anchorRecorded));
  if (unread !== undefined) {
    // Every review at that head recorded after the anchor's counts, so the number doesn't depend on the folder's order.
    const count = reviews.filter((ledger) => ledger.head === unread.head && ledger.recordedAt > anchorRecorded).reduce((sum, ledger) => sum + undecided(ledger), 0);
    return (
      `a review of ${branch} at ${unread.head}, recorded at ${unread.recordedAt}, after the last review (at ${last}), has ${count} finding(s) open or accepted, ` +
      `which the last review never read and a review of the fixes alone would not raise again: ${RESTART}`
    );
  }
  return { commit: last };
}

// The highest id a review had taken, its own or one it held from earlier
// reviews; 0 for none.
const highestId = (ledger: Ledger | undefined): number =>
  Math.max(ledger?.lastId ?? 0, ...(ledger?.findings ?? []).map((finding) => Number(finding.id.slice(1))));

// The highest id the ledger at `key` or any review of the branch's own
// commits took; 0 for none.
export function takenIds(cwd: string, base: string, head: string = headCommit(cwd), key: string = diffKey(cwd, base, head)): number {
  let replaced: Ledger | undefined;
  try {
    replaced = readLedger(cwd, key);
  } catch {
    // A damaged ledger can't be accepted against, so its ids name nothing.
  }
  return Math.max(highestId(replaced), ...branchLedgers(cwd, base, head).map(({ ledger }) => highestId(ledger)));
}

// What one review round reads: the whole diff, the fixes since the round before (a later round only), the key the diff
// has and the commits it was made between. Both the CLI's `prepare` and the host's review (lib/host-review.mts) call
// prepareRound, so they cannot diverge. `patch` and `fixes` are whole: they feed the key and the size warning. `readPatch` and
// `readFixes` leave out the verbatim Template mirrors (readerPatch) and are what the reviewers read, what the size limit and the lens choice
// apply to, and what gets written to disk.
export type PreparedRound = { patch: Buffer; fixes: Buffer | undefined; readPatch: Buffer; readFixes: Buffer | undefined; key: string; head: string; base: string; since?: string };

// The fixes a later round reads: since the HEAD the round before in the same run printed (`commit` and `preparedHead`), or, with
// `recorded`, since the commit the branch's last recorded review vouched for (recordedAnchor), for a new run that reviews a fix
// made after the review was recorded.
export type FixesSince = { commit: string; preparedHead: string | undefined } | { recorded: true };

// The body of `prepare` without its file I/O: `head` (HEAD by default) against
// where it left `main` (origin/main by default), plus, for a later round, the
// fixes since `since.commit`. `since.preparedHead` is the HEAD the round before
// read, which `fixesPatch` checks `since.commit` against. With `since.recorded`
// the fixes are those since recordedAnchor, on the checkout's branch, so `head`
// must be the checkout's HEAD; `since` in the result names that commit. Throws an
// Error with `prepare`'s message when the fixes can't be told.
export function prepareRound(cwd: string, { main = "origin/main", head = headCommit(cwd), since }: { main?: string; head?: string; since?: FixesSince }): PreparedRound {
  const base = mergeBase(cwd, main, head);
  const patch = reviewPatch(cwd, base, head);
  let anchor: string | undefined;
  let fixes: Buffer | string | undefined;
  if (since === undefined) {
    fixes = undefined;
  } else if ("recorded" in since) {
    // The branch is the checkout's, so the head must be too: another commit's branch can't be told.
    if (head !== headCommit(cwd)) throw new Error(`a recorded run reads the checkout's branch, so it reviews the checkout's HEAD, not ${head}.`);
    const found = recordedAnchor(cwd, base, head, git(cwd, "branch", "--show-current"));
    if (typeof found === "string") throw new Error(found);
    anchor = found.commit;
    // Nothing to review: the recorded review already vouches for this code (no commits since, or commits that change nothing, such
    // as an empty commit or a change and its revert), so the gate passes without a new one.
    fixes = fixesSince(cwd, anchor, base, head, main, "the review was recorded", "no changes since the review was recorded: it already vouches for this code.");
  } else {
    fixes = fixesPatch(cwd, since.preparedHead, since.commit, base, head, main);
  }
  if (typeof fixes === "string") throw new Error(fixes);
  return {
    patch,
    fixes,
    readPatch: readerPatch(patch),
    readFixes: fixes === undefined ? undefined : readerPatch(fixes),
    key: patchKey(patch),
    head,
    base,
    ...(anchor === undefined ? {} : { since: anchor }),
  };
}

// Record a review of HEAD's diff, replacing any ledger at the same diff.
// Without `at`, refuses a dirty worktree; with `at` the caller names the commit and the branch and answers for the
// worktree it reviewed (the host's review of a sandbox's worktree: the gate ran clean on that commit). Refuses a HEAD that isn't the code the last round
// reviewed: the review would vouch for code it never read. Also refuses any id
// at or below the highest the ledger it replaces or any earlier review of the
// branch's own commits took, so an id names one finding along the branch: a
// person who read a report before a review was recorded again can't accept a
// different finding by its id. (A rebase or an amend leaves the branch's
// earlier commits behind, and their ids with them, unless the diff is the same.)
//
// Once the ledger is written, every security finding recorded fixed or open also
// goes to the security record (recordSecurity), under the same lock. That can't
// fail the record: the ledger is what the gate checks, so a problem writing
// entries comes back through `onSecurity` as warnings. `slug` is the repository
// the entries name a PR of.
export function recordReview(
  cwd: string,
  base: string,
  input: ReviewInput,
  now: Date = new Date(),
  taken: typeof takenIds = takenIds,
  {
    slug = REPO_SLUG,
    onSecurity,
    at,
    entriesOf = earlierEntries,
  }: { slug?: string; onSecurity?: (outcome: SecurityOutcome) => void; at?: { head: string; branch: string }; entriesOf?: typeof earlierEntries } = {},
): Ledger {
  const parsed = reviewInputSchema.parse(input);
  // The security record is local and not code (see security-record.mts): an untracked README, or a symlink put in its
  // place, isn't a change HEAD fails to vouch for. Tracked changes there still count.
  // With `at` the caller names the commit and branch (the host's review of a sandbox's worktree, whose gate ran clean on that
  // commit), so this checkout's own worktree and HEAD are not what is vouched for.
  if (at === undefined) {
    const dirty = git(cwd, "status", "--porcelain", "--untracked-files=all")
      .split("\n")
      .filter((line) => !/^\?\? \.security(\/|$)/.test(line))
      .join("\n");
    if (dirty !== "") {
      throw new Error(`the worktree has uncommitted changes, so the review can't vouch for HEAD:\n${dirty}`);
    }
  }
  const head = at === undefined ? headCommit(cwd) : git(cwd, "rev-parse", "--verify", `${at.head}^{commit}`);
  const key = diffKey(cwd, base, head);
  if (key !== parsed.reviewedKey || !isReviewedCode(cwd, parsed.reviewedHead, base, head)) {
    throw new Error("HEAD isn't the code the last review round read: commits landed after it started. Review again before recording.");
  }
  let ledger: Ledger = {
    version: 1,
    key,
    branch: at === undefined ? git(cwd, "branch", "--show-current") : at.branch,
    base,
    head,
    recordedAt: now.toISOString(),
    rounds: parsed.rounds,
    ...(parsed.reviewerAgent === undefined ? {} : { reviewerAgent: parsed.reviewerAgent }),
    // The security record's own fields stay out of the ledger, so every reader of it is as before.
    findings: parsed.findings.map(({ policy: _policy, repeats: _repeats, ...stored }) => stored),
  };
  withLock(cwd, () => {
    const floor = taken(cwd, base, head, key);
    const reused = parsed.findings.filter((finding) => Number(finding.id.slice(1)) <= floor).map((finding) => finding.id);
    if (reused.length > 0) {
      throw new Error(`ids up to R${floor} are taken by earlier reviews of this branch, so ${reused.join(", ")} can't be used: number this run's findings after R${floor}`);
    }
    ledger = { ...ledger, lastId: Math.max(floor, highestId(ledger)) };
    const security = parsed.findings.filter((finding): finding is typeof finding & { status: "fixed" | "open" } => isSecurity(finding) && (finding.status === "fixed" || finding.status === "open"));
    // Read before the ledger is written, so a failure here fails the record with nothing written, as a failed record must.
    const warnings: string[] = [];
    const earlier = security.length === 0 ? {} : entriesOf(cwd, base, head, (message) => warnings.push(message));
    const inputs: SecurityInput[] = security.map(({ id, lens, path, summary, status, test, reason, fix, policy, repeats }) => {
      const raised = reRaisedEntry(summary, earlier);
      const near = raised === undefined ? nearReRaise(summary) : undefined;
      if (near !== undefined) {
        warnings.push(`${id} opens with ${near} and says "again", but not as "${near} again: ...", so it is matched by its text`);
      }
      if (raised !== undefined && raised.entry === undefined) {
        warnings.push(`${id} says it raises ${raised.named} again, but no earlier review of this branch recorded ${raised.named} against an entry; matched by its text instead`);
      }
      const reRaised = raised?.entry;
      return { id, lens, path, summary, status, test, reason, fix, policy, ...(repeats === undefined ? {} : { repeats }), ...(reRaised === undefined ? {} : { reRaised }) };
    });
    writeLedger(cwd, ledger);
    let outcome: SecurityOutcome;
    try {
      outcome = recordSecurity(cwd, ledger, inputs, { ...(parsed.pr === undefined ? {} : { pr: parsed.pr }), slug });
    } catch (error) {
      outcome = { written: [], repeated: [], entries: {}, warnings: [`the security record couldn't be written: ${(error as Error).message ?? String(error)}`] };
    }
    outcome.warnings.unshift(...warnings);
    try {
      writeEntryMap(cwd, key, outcome.entries);
    } catch (error) {
      outcome.warnings.push(`couldn't note which entry each finding went to: ${(error as Error).message ?? String(error)}`);
    }
    onSecurity?.(outcome);
  });
  return ledger;
}

// Which entry each finding of the branch's earlier reviews went to (finding id to entry id), so one raised again ("R3 again")
// holds its entry rather than getting a second file. Only this branch's own reviews: an id names one finding along the branch
// alone. The review at the same key, which the record replaces, counts too: its ids stay taken (takenIds), so the new review's
// R<n> again names one of them. Nearer reviews are read last, so their pairs win (mergeEntryMaps).
export function earlierEntries(cwd: string, base: string, head: string, warn: (message: string) => void): Record<string, string> {
  return mergeEntryMaps(branchLedgers(cwd, base, head).map(({ ledger, distance }) => ({ distance, recordedAt: ledger.recordedAt, entries: readEntryMapAll(cwd, ledger.key, warn) })));
}

// Reviews' entry maps merged into one, whatever order they come in (readdir's): the nearest review to HEAD (fewest commits back),
// and of two as near the one recorded later, is merged last, so where two map one finding id to different entries its pair wins.
export function mergeEntryMaps(reviews: readonly { distance: number; recordedAt: string; entries: Readonly<Record<string, string>> }[]): Record<string, string> {
  const merged: Record<string, string> = {};
  const ordered = [...reviews].sort((a, b) => b.distance - a.distance || a.recordedAt.localeCompare(b.recordedAt));
  for (const { entries } of ordered) Object.assign(merged, entries);
  return merged;
}

// Accept an open finding on the ledger for HEAD's diff, with the person's
// reason. A finding that isn't open stays as it is. The security record's entry
// the finding was recorded against is accepted too if it is open (a fixed one stays, with a warning), and `onSecurity` hears of it (or of why it
// couldn't be): the acceptance itself stands either way.
export function acceptFinding(cwd: string, base: string, id: string, reason: string, onSecurity?: (message: string) => void): Ledger {
  return decideOpen(cwd, base, { accept: [{ id, reason }], fix: [] }, onSecurity);
}

// What the person decided about some of a review's open findings: each one accepted with its own reason, or asked to be fixed.
export type OpenDecision = { accept: readonly { id: string; reason: string }[]; fix: readonly string[] };

// Apply a person's decision to the open findings on the ledger for HEAD's diff, all or nothing: every id is checked (it names an
// open finding, it is decided once, an accepted one has a reason) before the ledger changes, so a refusal leaves it as it was.
// An accepted finding is accepted as acceptFinding says; one asked to be fixed stays open with `fixAsked`, and blocks the push
// until a review fixes it when it blocks at all (isBlocking: not a non-security Advisory one).
export function decideOpen(cwd: string, base: string, decision: OpenDecision, onSecurity?: (message: string) => void): Ledger {
  const accepting = decision.accept.map(({ id, reason }) => ({ id, why: stripControl(reason).trim() }));
  // The list's ready command holds "<reason>" for the person to replace: run as it is, it would record the placeholder as each reason.
  const unfilled = accepting.filter(({ why }) => /^<\s*reason\s*>$/i.test(why)).map(({ id }) => id);
  if (unfilled.length > 0) throw new Error(`write each reason in place of <reason>: ${unfilled.join(", ")}`);
  const unexplained = accepting.filter(({ why }) => why === "").map(({ id }) => id);
  if (unexplained.length > 0) throw new Error(`say why the finding is accepted: ${unexplained.join(", ")}`);
  const ids = [...accepting.map(({ id }) => id), ...decision.fix];
  if (ids.length === 0) throw new Error("name at least one finding to accept or fix");
  const twice = ids.filter((id, index) => ids.indexOf(id) !== index);
  if (twice.length > 0) throw new Error(`each finding is decided once: ${[...new Set(twice)].join(", ")} named more than once`);
  const key = diffKey(cwd, base);
  return withLock(cwd, () => {
    const ledger = readLedger(cwd, key);
    if (ledger === undefined) throw new Error("no review is recorded for this diff: run /pre-push-review first");
    const byId = new Map(ledger.findings.map((finding) => [finding.id, finding]));
    for (const id of ids) {
      const finding = byId.get(id);
      if (finding === undefined) throw new Error(`the review has no finding ${id}`);
      if (finding.status !== "open") throw new Error(`${id} is already ${finding.status}`);
    }
    const accepted = accepting.map(({ id, why }) => {
      const finding = byId.get(id) as Finding;
      finding.status = "accepted";
      finding.reason = why;
      delete finding.fixAsked;
      return { finding, why };
    });
    for (const id of decision.fix) (byId.get(id) as Finding).fixAsked = true;
    const written = ledgerSchema.parse(ledger);
    writeLedger(cwd, written);
    for (const { finding, why } of accepted) {
      if (!isSecurity(finding)) continue;
      try {
        const entry = markAccepted(cwd, finding, why, readEntryMap(cwd, key, finding.id), onSecurity);
        if (entry !== undefined) onSecurity?.(`marked ${entry} accepted`);
      } catch (error) {
        onSecurity?.(`couldn't mark the entry accepted: ${(error as Error).message ?? String(error)}`);
      }
    }
    return written;
  });
}

// The open findings of a review, for the person deciding them: each with its grade, where it is, what goes wrong, the suggested fix and
// whether it was asked to be fixed, then the commands that decide them. Local only (the person's own terminal), like the report.
export function renderOpen(ledger: Ledger): string {
  const open = ledger.findings.filter((finding) => finding.status === "open");
  if (open.length === 0) return "No open findings.\n";
  const blocks = open.map((finding) =>
    [
      `${finding.id}  ${severityOf(finding)}${isBlocking(finding) ? ", blocks the push" : ""}${finding.fixAsked === true ? ", asked to be fixed" : ""}  ${plainText(finding.lens)}${isSecurity(finding) ? " (security)" : ""}  ${plainText(`${finding.path}:${finding.line}`)}`,
      `    ${plainText(finding.summary)}`,
      ...(finding.fix === null || finding.fix === undefined ? [] : [`    Suggested fix: ${plainText(finding.fix)}`]),
    ].join("\n"),
  );
  // The ready command names the undecided findings that block, else (none of them blocks) every undecided one, and never one already
  // asked to be fixed; none when every open finding is asked. The examples use an undecided id when there is one, else an open one (an
  // asked finding is still open: accepting it drops the ask).
  const undecided = open.filter((finding) => finding.fixAsked !== true);
  const ready = (undecided.some(isBlocking) ? undecided.filter(isBlocking) : undecided).map((finding) => finding.id);
  const example = ready[0] ?? open[0]?.id ?? "R1";
  return [
    `${open.length} open finding(s) on the review of ${ledger.branch === "" ? "HEAD" : plainText(ledger.branch)} at ${ledger.head.slice(0, 12)}:`,
    "",
    ...blocks,
    "",
    "Decide them (yours to run, never an agent's on your behalf), naming each finding:",
    ...(ready.length === 0 ? [] : [`  scripts/review.sh accept-open ${ready.map((id) => `${id}="<reason>"`).join(" ")}`]),
    `  scripts/review.sh accept-open --reason "<reason>" ${example} ...     accept the ids named with one reason`,
    `  scripts/review.sh accept-open --fix ${example} ...                   ask for these to be fixed: the next /pre-push-review fixes them`,
    "",
  ].join("\n");
}

// The words after `accept-open`: ids to accept, each as `R3=<reason>` or bare with the --reason one, then after --fix the ids to fix.
// Every finding is named: there is no "all", so a decision never takes in a finding the person didn't name (a review recorded again
// since the list, or a fix asked meanwhile, changes the ids, and decideOpen refuses one that isn't open). A --reason no bare id uses,
// or one after --fix, is refused rather than dropped. Undefined with no words: list the open findings.
export function parseOpenDecision(words: readonly string[]): OpenDecision | undefined {
  if (words.length === 0) return undefined;
  let shared: string | undefined;
  let fixing = false;
  const named: { id: string; reason: string | undefined }[] = [];
  const fix: string[] = [];
  for (let index = 0; index < words.length; index += 1) {
    const word = words[index] as string;
    if (word === "--reason") {
      if (fixing) throw new Error("put --reason before --fix: every id after --fix is one to fix");
      const next = words[index + 1];
      if (next === undefined || /^--/.test(next)) throw new Error("--reason needs the reason after it");
      if (shared !== undefined) throw new Error("give --reason once");
      shared = next;
      index += 1;
    } else if (word === "--fix") {
      if (fixing) throw new Error("give --fix once, then the ids to fix");
      fixing = true;
    } else {
      const match = /^(R[1-9]\d{0,5})(?:=([\s\S]*))?$/.exec(word);
      if (match === null) throw new Error(`${word} isn't a finding id (R1, R2, ...) or one of --reason, --fix`);
      const [, id, reason] = match as unknown as [string, string, string | undefined];
      if (fixing) {
        if (reason !== undefined) throw new Error(`${id} is to be fixed, so it takes no reason`);
        fix.push(id);
      } else named.push({ id, reason });
    }
  }
  if (fixing && fix.length === 0) throw new Error("--fix needs the ids to fix after it");
  if (shared !== undefined && !named.some(({ reason }) => reason === undefined)) {
    throw new Error(`--reason is for bare ids to accept, and no id${fixing ? " before --fix" : ""} is bare (each has its own reason, or none is named): name a bare id such as R3 with it`);
  }
  const accept = named.map(({ id, reason }) => {
    const why = reason ?? shared;
    if (why === undefined) throw new Error(`say why ${id} is accepted: ${id}="<reason>", or --reason "<reason>"`);
    return { id, reason: why };
  });
  return { accept, fix };
}

export type ReviewCheck = { ok: boolean; message: string };

// The gate's review check: HEAD's diff has a recorded review with no open
// Critical or Warning finding. Three things pass without one: `GATE_SKIP_REVIEW=1` (that value
// only), a branch with no changes against the base, and a Baseline re-Apply
// on REAPPLY_BRANCH that changes only Owned files (or deletes a retired one), REVIEWED_ON_REAPPLY apart.
export function checkReview(cwd: string, base: string, env: NodeJS.ProcessEnv = process.env, retired: ReadonlySet<string> = RETIRED_PATHS): ReviewCheck {
  if (env[SKIP_REVIEW_ENV] === "1") {
    return { ok: true, message: `Review check skipped (${SKIP_REVIEW_ENV}=1).` };
  }
  const head = headCommit(cwd);
  const key = diffKey(cwd, base, head);
  if (key === EMPTY_DIFF_KEY) return { ok: true, message: "No changes against origin/main to review." };
  let withheld: string[] = [];
  if (git(cwd, "branch", "--show-current") === REAPPLY_BRANCH) {
    // The key's own pins: no renames (a file moved into an Owned folder is
    // still a change elsewhere), no ignored submodules, no relative paths, and
    // no trimming, so a name starting with a space keeps it. The raw form, not
    // just the names, because Apply only adds or overwrites regular files, and
    // deletes only a retired one: any other deleted Owned file, a symlink or a
    // submodule under an Owned path is not what a re-Apply writes, and is reviewed.
    const raw = gitBytes(cwd, ["-c", "diff.relative=false", "diff", "--raw", "--no-renames", "--ignore-submodules=none", "-z", base, head])
      .toString("utf8")
      .split("\0")
      .filter((entry) => entry !== "");
    const changes: { status: string; oldMode: string; mode: string; path: string }[] = [];
    for (let index = 0; index + 1 < raw.length; index += 2) {
      const [oldMode = "", mode = "", , , status = ""] = raw[index]!.split(" ");
      changes.push({ status, oldMode: oldMode.replace(/^:/, ""), mode, path: raw[index + 1]! });
    }
    const regular = (mode: string) => mode === "100644" || mode === "100755";
    const writtenByApply = (change: { status: string; oldMode: string; mode: string; path: string }) =>
      ((change.status === "A" || change.status === "M") && regular(change.mode) && isOwnedPath(change.path) && !REVIEWED_ON_REAPPLY.has(change.path)) ||
      (change.status === "D" && regular(change.oldMode) && retired.has(change.path) && !NOT_OWNED.has(change.path));
    withheld = changes.filter((change) => REVIEWED_ON_REAPPLY.has(change.path)).map((change) => change.path);
    if (changes.length > 0 && changes.every(writtenByApply)) {
      return { ok: true, message: "A Baseline re-Apply that changes only Owned files: the Template's own review covers them." };
    }
  }
  const ledger = readLedger(cwd, key);
  if (ledger !== undefined && ledger.head !== head && !mergeTreeWorks(cwd)) {
    return {
      ok: false,
      message: `${git(cwd, "version")} can't check the review across a merge of main or a rebase onto it: that needs git 2.38 or later (merge-tree --write-tree). Upgrade git.`,
    };
  }
  if (ledger === undefined || !isReviewedCode(cwd, ledger.head, base, head)) {
    const why = withheld.length > 0 ? ` A re-Apply skips the review only for Owned files that carry no behaviour; it is withheld for: ${withheld.join(", ")}.` : "";
    return { ok: false, message: `No review is recorded for this diff. Run /pre-push-review, which reviews, fixes and records it.${why}` };
  }
  const blocking = ledger.findings.filter(isBlocking);
  if (blocking.length > 0) {
    const lines = blocking.map((finding) => `  ${finding.id} ${severityOf(finding)} ${finding.path}:${finding.line} (${finding.lens}): ${finding.summary}`);
    return {
      ok: false,
      message: [
        `${blocking.length} review finding(s) wait for a person. Fix them and run /pre-push-review again, or the person you work for accepts each one (never an agent on their behalf):`,
        ...lines,
        '  scripts/review.sh accept <id> "<reason>"',
        "  scripts/review.sh accept-open   (lists each with its suggested fix; decides several at once, accepting or asking for a fix)",
      ].join("\n"),
    };
  }
  const advisory = ledger.findings.filter((finding) => finding.status === "open").length;
  const followUp = advisory === 0 ? "" : ` ${advisory} Advisory finding(s) open for the follow-up issue.`;
  return { ok: true, message: `Reviewed clean: ${ledger.findings.length} finding(s), none blocking.${followUp}` };
}

// Reviewer and person text as one line of plain Markdown (plainText, in
// lib/plain-text.mts: no mention, reference, closing keyword, tag or code span gets
// through), with pipes escaped for a table cell.
const cell = (text: string) => plainText(text).replace(/\|/g, "\\|");

const where = (finding: Finding) => cell(`${finding.path}:${finding.line}`);

const severityHeadings: Record<Severity, string> = { critical: "Critical", warning: "Warning", advisory: "Advisory" };

// The review report: every finding, grouped by severity, where it is, what
// happened to it, and the test or reason behind that. A severity no finding
// has gets no section.
export function renderReport(ledger: Ledger): string {
  const header = `Review of ${ledger.branch === "" ? "HEAD" : cell(ledger.branch)} at ${ledger.head.slice(0, 12)}: ${ledger.rounds} round(s), ${ledger.findings.length} finding(s), reviewed by ${plainText(ledger.reviewerAgent ?? "an unrecorded agent")}.`;
  if (ledger.findings.length === 0) return `${header}\n\nNo findings.\n`;
  const sections = SEVERITIES.flatMap((severity) => {
    const rows = ledger.findings
      .filter((finding) => severityOf(finding) === severity)
      .map(
        (finding) =>
          `| ${finding.id} | ${cell(finding.lens)}${isSecurity(finding) ? " (security)" : ""} | ${where(finding)} | ${cell(finding.summary)} | ${finding.status}${finding.fixAsked === true ? ", asked to be fixed" : ""} | ${cell(finding.test ?? finding.reason ?? "")} |`,
      );
    if (rows.length === 0) return [];
    return ["", `### ${severityHeadings[severity]}`, "", "| Id | Lens | Where | Finding | Status | Test or reason |", "| --- | --- | --- | --- | --- | --- |", ...rows];
  });
  return [header, ...sections, ""].join("\n");
}

// The markers around the PR description's `## Review notes`, so the section
// can be replaced without touching what follows it (`Fixes #`, the footer).
// The release blog drops HTML comments, so the markers don't reach the published
// notes.
export const REVIEW_NOTES_START = "<!-- review-notes -->";
export const REVIEW_NOTES_END = "<!-- /review-notes -->";

// The PR description's `## Review notes`, between its markers: the Critical and Advisory findings a
// person accepted, for people reading the PR. Security findings (see
// isSecurity: graded Warning, a lens naming security, or unmarked) are only
// counted, so the maintainer knows to wait for the
// private report, and so is an accepted finding whose path, summary or reason holds a link or an image
// GitHub would render (RENDERED_LINK): its words stay in the ledger and the report. Claude Review reads the notes' accepted non-security findings on an owner's same-repo PR, but
// raises a security finding again (the notes only count it), and Copilot isn't known to read them, so
// a thread one opens on an accepted finding gets the same "Left for the
// owner." reply as every thread left for the owner (docs/PROCESS.md), which doesn't
// say which thread a security finding is on; the reason stays in the ledger
// and, for a finding that isn't a security one and holds no link, in these notes. Empty when
// none was accepted and the reviewer was the pre-push-reviewer (or unrecorded).
export function renderReviewNotes(ledger: Ledger): string {
  const accepted = ledger.findings.filter((finding) => finding.status === "accepted");
  // An accepted security finding is a known weakness left as it is, and the
  // description becomes public release notes: it's tracked privately instead
  // (docs/SECURITY.md), and only counted here.
  const security = accepted.filter(isSecurity);
  // The description is public and a reviewer agent that read hostile text wrote these words, so an accepted finding whose path, summary or
  // reason holds a link or an image GitHub would render (RENDERED_LINK) is only counted too; its words stay in the ledger and the report.
  const linked = accepted.filter((finding) => !security.includes(finding) && RENDERED_LINK.test(`${finding.path} ${finding.summary} ${finding.reason ?? ""}`));
  const listed = accepted.filter((finding) => !security.includes(finding) && !linked.includes(finding));
  const standIn = ledger.reviewerAgent !== undefined && ledger.reviewerAgent !== "pre-push-reviewer";
  if (accepted.length === 0 && !standIn) return "";
  const privately =
    security.length === 0
      ? []
      : ["", `${security.length} accepted security finding(s) are left out here and reported privately, as docs/SECURITY.md describes.`];
  const withheld =
    linked.length === 0 ? [] : ["", `${linked.length} accepted finding(s) holding a link or an image are left out here; they are in the local review record.`];
  const section = (...body: string[]) => [REVIEW_NOTES_START, "## Review notes", ...body, REVIEW_NOTES_END, ""].join("\n");
  const reviewer = standIn ? ["", `Reviewed by ${plainText(ledger.reviewerAgent ?? "")}, not the read-only pre-push-reviewer.`] : [];
  if (listed.length === 0) return section(...privately, ...withheld, ...reviewer);
  const lines = listed.map(
    (finding) => `- ${plainText(`${finding.path}:${finding.line}`)}: ${plainText(finding.summary)} Accepted: ${plainText(finding.reason ?? "")}`,
  );
  return section("", "The local review raised these and they were accepted, not changed:", "", ...lines, ...privately, ...withheld, ...reviewer);
}

// Which lines of a Markdown text are inside a fenced code block (the fence
// lines included), and whether one is still open at the end. A fence opens
// with ``` or ~~~, up to 3 spaces in or after a list item's marker, but not a
// ``` whose line has another backtick (that's a code span); it closes with a
// line of only the same character, at least as many.
function codeBlocks(lines: readonly string[]): { fenced: boolean[]; open: boolean } {
  let fence: string | undefined;
  const fenced = lines.map((line) => {
    if (fence === undefined) {
      const [, mark, rest] = /^ {0,3}(?:(?:[-+*]|\d{1,9}[.)])[ \t]+)*(`{3,}|~{3,})(.*)$/.exec(line) ?? [];
      if (mark === undefined || (mark[0] === "`" && (rest ?? "").includes("`"))) return false;
      fence = mark;
      return true;
    }
    const close = /^[ \t]*(`{3,}|~{3,})[ \t]*$/.exec(line)?.[1];
    if (close !== undefined && close[0] === fence[0] && close.length >= fence.length) fence = undefined;
    return true;
  });
  return { fenced, open: fence !== undefined };
}

// A line that reads "Review notes" first, in any form a reader sees those
// words in: a heading of any kind, in a quote or a list, with emphasis, tags,
// character references or invisible characters (see visibleLetters).
const readsReviewNotes = (line: string) => visibleLetters(line).split(" ").some((reading) => reading.startsWith("reviewnotes"));

// A closing line (`Fixes #n`, `Closes #n` or `Resolves #n`), or the footer.
const isClosingLine = (line: string) => /^(?:fixes|closes|resolves) #\d+/i.test(line);
const isFooter = (line: string) => line.startsWith("🤖 Generated with");

// `body` with its Review notes section replaced by `notes` (renderReviewNotes'
// output; empty removes the section). A marker counts only as a line of its
// own, unindented and outside a code block, so one quoted in the text or shown
// in an example isn't taken for it. A body with no section gets one above its
// closing line (the last `Fixes`, `Closes` or `Resolves #n` that starts a
// paragraph), else above the footer, else at the end. Throws, rather than
// guess, on an empty body (a `gh pr view` that failed, which would publish
// the notes alone) or one that removing the section leaves empty, on a code
// block that never closes, on markers that don't make one pair, on a closing
// line or the footer inside the section (replacing it would delete them), and
// on any other line outside it that reads "Review notes", such as an unmarked
// section, which would otherwise stay beside the new one. CRLF, as GitHub may
// store a description, comes back as LF, and the result ends in one newline.
export function spliceReviewNotes(body: string, notes: string): string {
  const ask = (problem: string) =>
    new Error(`the PR description ${problem}, so its Review notes can't be replaced safely: show it to the person and ask the person what to keep`);
  if (body.trim() === "") throw ask("is empty");
  const lines = body.replace(/\r\n?/g, "\n").replace(/\n+$/, "").split("\n");
  const { fenced, open } = codeBlocks(lines);
  if (open) throw ask("has a code block that never closes");
  const outside = (test: (line: string, index: number) => boolean) =>
    lines.flatMap((line, index) => (!fenced[index] && test(line, index) ? [index] : []));
  const starts = outside((line) => line.trimEnd() === REVIEW_NOTES_START);
  const ends = outside((line) => line.trimEnd() === REVIEW_NOTES_END);
  if (starts.length > 1 || ends.length > 1) throw ask("has more than one Review notes section");
  if (starts.length !== ends.length || (starts[0] ?? 0) > (ends[0] ?? 0)) throw ask("has Review notes markers that don't make one pair");
  const [first, last] = [starts[0] ?? lines.length, ends[0] ?? -1];
  const unmarked = (index: number) => !fenced[index] && (index < first || index > last);
  if (outside((line, index) => unmarked(index) && readsReviewNotes(line)).length > 0) throw ask("has a Review notes section without markers");
  // A heading can span lines (setext text, an <h2> broken across two), so each
  // paragraph is read as one text as well, its line endings kept (a tag or a
  // blockquote's marker depends on them).
  let paragraph: string[] = [];
  for (const [index, line] of [...lines, ""].entries()) {
    if (index < lines.length && line.trim() !== "" && unmarked(index)) {
      paragraph.push(line);
      continue;
    }
    if (paragraph.length > 1 && readsReviewNotes(paragraph.join("\n"))) throw ask("has a Review notes section without markers");
    paragraph = [];
  }
  if (lines.slice(first, last + 1).some((line) => isClosingLine(line) || isFooter(line))) throw ask("has its closing line or footer inside the Review notes markers");
  const added = notes === "" ? [] : notes.replace(/\n+$/, "").split("\n");
  let start: number;
  let removed: number;
  if (starts.length === 1) {
    start = starts[0] as number;
    removed = (ends[0] as number) - start + 1;
    if (added.length === 0) {
      // Take one blank line with it, so no gap is left where it was.
      if (lines[start + removed] === "") {
        removed += 1;
      } else if (start > 0 && lines[start - 1] === "") {
        start -= 1;
        removed += 1;
      }
    }
  } else {
    if (added.length === 0) return `${lines.join("\n")}\n`;
    const closing = outside((line, index) => isClosingLine(line) && (index === 0 || lines[index - 1] === "")).at(-1);
    const footer = outside(isFooter).at(-1);
    start = closing ?? footer ?? lines.length;
    removed = 0;
    // A blank line between it and what it's added above, or after the text
    // it's added below.
    if (start === lines.length) {
      added.unshift("");
    } else {
      added.push("");
    }
  }
  lines.splice(start, removed, ...added);
  // The section was all the body held and nothing is accepted any more.
  if (lines.every((line) => line.trim() === "")) throw ask("would be empty without its Review notes");
  return `${lines.join("\n")}\n`;
}

// The patch of the fixes a later round reviews: `since`, the HEAD the round
// before read, to `head`. The reasons it can't be, as a message: a commit that
// isn't one of HEAD's, no round prepared in `dir`, a commit later than the
// HEAD the last prepare in `dir` printed (it would leave fixes unread; an
// earlier one only rereads more, as when a round is prepared twice), a merge
// of main since (the patch would hold main's changes), or no fixes at all.
// `preparedHead` is the HEAD that prepare printed (`dir`'s `head` file for the
// CLI), and `main` what the branch is compared with.
function fixesPatch(cwd: string, preparedHead: string | undefined, since: string, base: string, head: string, main: string): Buffer | string {
  if (!isAncestor(cwd, since, head)) {
    return `${since} isn't one of HEAD's commits: give the HEAD prepare printed in the round before. After an amend or a rebase there's none: ${RESTART}`;
  }
  const before = preparedHead;
  if (before === undefined || !isAncestor(cwd, since, before)) {
    return `${since} is later than the HEAD prepare printed in the round before (${before ?? "none in this folder"}): give that one, so no fix goes unread.`;
  }
  return fixesSince(cwd, since, base, head, main, "the round before", `no fixes since ${since}: a later round follows a round that fixed something, after its fix commits.`);
}

// The patch from `since`, one of HEAD's commits, to `head`, for both kinds of later round; `after` says what `since` is, for the
// messages. Refused, as a message, after a merge of main since (the patch would hold main's changes) or, with `noFixes`, when
// there are no fixes at all.
function fixesSince(cwd: string, since: string, base: string, head: string, main: string, after: string, noFixes: string): Buffer | string {
  if (git(cwd, "merge-base", main, since) !== base) {
    return `main was merged in since ${after}, so the fixes can't be told from main's changes: ${RESTART}`;
  }
  const fixes = reviewPatch(cwd, since, head);
  if (fixes.length === 0) return noFixes;
  return fixes;
}

// The follow-up issue's list of the open Advisory findings, each with where it
// is, what goes wrong and the suggested fix, as plain Markdown (plainText: no
// mention, reference, closing keyword, tag or code span gets through), ending
// with the session marker. A finding isSecurity marks is left out, whatever
// its grade: the issue is public. Empty when there's none.
export function renderAdvisories(ledger: Ledger): string {
  const open = ledger.findings.filter((finding) => finding.status === "open" && severityOf(finding) === "advisory" && !isSecurity(finding));
  if (open.length === 0) return "";
  const lines = open.map((finding) => {
    const fix = finding.fix == null ? "" : ` Suggested fix: ${plainText(finding.fix)}`;
    return `- ${finding.id} ${plainText(`${finding.path}:${finding.line}`)}: ${plainText(finding.summary)}${fix}`;
  });
  const branch = ledger.branch === "" ? "HEAD" : plainText(ledger.branch);
  return [`Advisory findings from the local review of ${branch}, left for a follow-up:`, "", ...lines, "", "<!-- claude-session -->", ""].join("\n");
}

const lstatOrUndefined = (path: string) => {
  try {
    return lstatSync(path);
  } catch {
    return undefined;
  }
};

// Whether `a` and `b` are one file, by path, symlink or hard link: `body`
// removes its output first, which would delete an input it names.
function sameFile(cwd: string, a: string, b: string): boolean {
  if (resolve(cwd, a) === resolve(cwd, b)) return true;
  try {
    const [first, second] = [statSync(resolve(cwd, a)), statSync(resolve(cwd, b))];
    return first.dev === second.dev && first.ino === second.ino;
  } catch {
    return false;
  }
}

const USAGE = `Usage: scripts/review.sh <command>
  prepare <absolute dir> [<since>|last|recorded]
                         write HEAD's diff to <dir>/diff.patch for the reviewers, and with <since> (the full id of the HEAD
                         the round before printed, which "last" reads from <dir>/head) the fixes since to <dir>/fixes.patch, both without the
                         Template mirrors that are verbatim copies of a root file in the same patch; with "recorded", the fixes since the
                         commit the branch's last recorded review vouched for, for a new run that reviews only a fix made after it; print
                         the key of the whole diff, then HEAD
  key                    print the ledger key of HEAD's diff against origin/main
  check [<base>]         the gate's check: exit 1 unless HEAD's diff was reviewed clean
  size [<base>]          print the warning prepare gives for a branch past 30 files or 3,000 lines (a file and its Template mirror count once), for
                         HEAD's diff against <base> (its merge base with origin/main by default), or nothing; exits 0 either way
  stage <absolute dir> [<file>|-]
                         add the findings in the JSON array in <file> (stdin by default, or -) to <dir>/findings.json, a finding with an id already there replacing
                         it, so a round's triage survives a long session; prints how many are staged
  record <file> [--staged <absolute dir>]
                         record a review from JSON ({ reviewedKey, reviewedHead, rounds, reviewerAgent, pr, findings }); - reads stdin;
                         with --staged the findings in <dir>/findings.json come before the file's own
  accept <id> <reason>   accept an open finding on HEAD's review
  accept-open [--reason <reason>] [<id>[=<reason>]...] [--fix <id>...]
                         with nothing after it, list HEAD's review's open findings and how to decide them; otherwise accept the ids
                         named (each with its own =<reason>, or the --reason one) and ask for the ids after --fix to be fixed by the
                         next review, all at once or none
  report                 print HEAD's review report
  notes                  print the "## Review notes" section for the PR description
  advisory               print the follow-up issue's list of open Advisory findings
  body <in> <out>        write to <out> the PR description in <in> (- reads stdin) with its Review notes replaced;
                         <out> is removed first and written only on success
  latest                 print the nearest earlier review of this branch's work as JSON ({} when none)
  security               list the local security record (.security/ in the main checkout): id, status, lens, path, file
  repo                   print this repository as OWNER/REPO, for GH_REPO (lib/repo-config.mts's REPO_SLUG)
  next                   print the first finding id this run may use`;

// Why `slug` isn't a repository gh can be told (GH_REPO), or undefined when it is: exactly OWNER/REPO, each
// part a GitHub name, with nothing around it. The Seed's unfilled owner-and-repo placeholder fails.
export function repoSlugProblem(slug: string): string | undefined {
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(slug) ? undefined : `REPO_SLUG in lib/repo-config.mts is ${JSON.stringify(slug)}, not OWNER/REPO.`;
}

// `text` without the control, format and invisible characters a terminal acts on, except newlines, tabs and the zero-width space
// breakReferences adds (after an @ before an ASCII letter or digit, after a # or GH- before a digit, and in github.com): a published `advisory`
// or `notes` keeps those, and any other zero-width space, such as one in an echoed parse error, is removed.
function printable(text: string): string {
  // Only the characters next to a zero-width space are read, so the cost of one text is linear in its length.
  const wordBefore = (word: string, offset: number, whole: string) =>
    offset >= word.length && whole.slice(offset - word.length, offset).toLowerCase() === word && !/\w/.test(whole.charAt(offset - word.length - 1));
  return text.replace(/[\p{C}\p{Default_Ignorable_Code_Point}\u2800\u2028\u2029]/gu, (character, offset: number, whole: string) => {
    if (character === "\n" || character === "\t") return character;
    if (character !== "\u200b") return "";
    const previous = whole.charAt(offset - 1);
    const next = whole.charAt(offset + 1);
    const breaksReference =
      (previous === "@" && /[A-Za-z0-9]/.test(next)) ||
      ((previous === "#" || wordBefore("gh-", offset, whole)) && /\d/.test(next)) ||
      (wordBefore("github", offset, whole) && /^\.com\b/i.test(whole.slice(offset + 1, offset + 6)));
    return breaksReference ? character : "";
  });
}

// The findings a run keeps between its rounds, in the run folder `prepare` writes to: each round's triage is `stage`d as it ends, and
// `record --staged` reads them, so the record doesn't depend on what the session still remembers of the earlier rounds.
const STAGED_FILE = "findings.json";

// Why a staging file or folder isn't the run's own, or undefined: it belongs to another user, or the group or others can write to it. `uid` is the
// current user's (undefined where the platform has none, which skips the check). The findings in it are unpublished, and the file is read back
// into the record, so a file another user could have written is not trusted.
export function stagedOwnerProblem(info: { uid: number; mode: number }, uid: number | undefined, what: string): string | undefined {
  if (uid === undefined) return undefined;
  if (info.uid !== uid) return `${what} isn't owned by you.`;
  if ((info.mode & 0o022) !== 0) return `${what} can be written by the group or others: make it owner-only (chmod 700 for a folder, 600 for a file).`;
  return undefined;
}

// The findings staged in `directory`, each checked as `record` checks its input. A file that is missing, a symlink or not a regular file,
// not the current user's or writable by the group or others, unopenable, not JSON, not an array, or holding a finding `record` would refuse
// stops the command and names the file, so a damaged file fails where it is met. The file is opened once, without following a link and without
// blocking (a FIFO opens at once and is refused below), and judged by that descriptor, so it can't be swapped between the check and the read.
function readStaged(directory: string): z.infer<typeof findingListSchema> {
  const file = join(directory, STAGED_FILE);
  return parseStaged(file, stagedText(file));
}

// The text of the staging file `file`, opened without following a link or blocking (a FIFO), and read only when it is a
// regular file the current user owns that the group and others can't write.
function stagedText(file: string): string {
  let handle: number;
  try {
    handle = openSync(file, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT") throw new Error(`${file} doesn't exist: stage the findings of a round first (scripts/review.sh stage).`, { cause: error });
    if (code === "ELOOP") throw new Error(`${file} isn't a regular file (it is a symlink).`, { cause: error });
    throw new Error(`${file} can't be opened: ${(error as Error).message}`, { cause: error });
  }
  let text: string;
  try {
    const info = fstatSync(handle);
    if (!info.isFile()) throw new Error(`${file} isn't a regular file.`);
    const problem = stagedOwnerProblem(info, process.getuid?.(), file);
    if (problem !== undefined) throw new Error(problem);
    text = readFileSync(handle, "utf8");
  } finally {
    closeSync(handle);
  }
  return text;
}

// The findings the staging file `file` holds, from its `text`, each checked as `record` will check it.
function parseStaged(file: string, text: string): z.infer<typeof findingListSchema> {
  let staged: unknown;
  try {
    staged = JSON.parse(text);
  } catch {
    throw new Error(`${file} isn't JSON.`);
  }
  if (!Array.isArray(staged)) throw new Error(`${file} isn't a JSON array.`);
  const checked = findingListSchema.safeParse(staged);
  if (!checked.success) throw new Error(`${file} holds findings the review refuses:\n${z.prettifyError(checked.error)}`);
  return checked.data;
}

// A test seam: called after stage has merged its findings and before it checks the file and writes it.
export const stageTestHooks: { beforeReplace?: () => void } = {};

// Add `findings` (a JSON array, each checked as `record` will check it) to the folder's file; a finding whose id is staged already
// replaces that one, since a later round may change what became of it. The file holds the run's unpublished findings: it is made
// owner-only and whole (a temp file renamed over it, never written through a link), in a folder that is the current user's, not a symlink
// (a trailing slash doesn't hide one: the path is resolved first) and not writable by the group or others, and a second `stage` that runs at the same
// time is refused by a lock file, since two would each merge into the same old file and one would lose the other's findings. The lock
// is the ledger's own kind (holdLock), so one a killed stage left is taken over rather than refusing every later stage in the folder.
// `close` is the descriptor close, a seam for the test that makes it fail.
export function stageFindings(given: string, findings: unknown, close: (handle: number) => void = closeSync): { added: number; total: number } {
  const parsed = findingListSchema.parse(findings);
  const directory = resolve(given);
  mkdirSync(directory, { recursive: true, mode: 0o700 });
  const folder = lstatSync(directory);
  if (folder.isSymbolicLink()) throw new Error(`${directory} is a symlink: give the run's own folder.`);
  const problem = stagedOwnerProblem(folder, process.getuid?.(), directory);
  if (problem !== undefined) throw new Error(problem);
  const file = join(directory, STAGED_FILE);
  const lock = `${file}.lock`;
  const refuse = (cause?: unknown) => new Error(`another stage is writing ${file}; try again, or delete ${lock} if none is running.`, { cause });
  return holdLock(
    lock,
    refuse,
    () => {
      const current = () => (lstatOrUndefined(file) === undefined ? undefined : stagedText(file));
      const read = current();
      const kept = read === undefined ? [] : parseStaged(file, read);
      const ids = new Set(parsed.map((finding) => finding.id));
      const merged = [...kept.filter((finding) => !ids.has(finding.id)), ...parsed];
      stageTestHooks.beforeReplace?.();
      // The lock's takeover is best-effort (withLock), so a second stage may share it in a rare race. A file that changed since
      // this one read it is refused, and running it again merges both; that narrows a lost round to a stage that writes in the
      // instant between this check and the rename, which nothing here can close.
      if (current() !== read) throw new Error(`another stage changed ${file} while this one was merging into it; run stage again.`);
      replaceFile(file, `${JSON.stringify(merged, null, 2)}\n`);
      return { added: parsed.length, total: merged.length };
    },
    close,
  );
}

// The command line. Returns the exit code: 0 only for a command that did
// what it was asked, so the gate can't pass on a failure.
export function main(args: readonly string[], cwd: string = process.cwd(), env: NodeJS.ProcessEnv = process.env, slug: string = REPO_SLUG): number {
  const [command, ...rest] = args;
  // Text that may quote the agent's input (a parse error echoing a key, a finding) is printed without control characters, keeping the
  // newlines and tabs the output is laid out with.
  const out = (text: string) => process.stdout.write(printable(text.endsWith("\n") ? text : `${text}\n`));
  const fail = (text: string) => {
    process.stderr.write(`${printable(text)}\n`);
    return 1;
  };
  try {
    switch (command) {
      case "key":
        out(diffKey(cwd, mergeBase(cwd)));
        return 0;
      case "prepare": {
        // An absolute folder only: an empty shell variable would otherwise
        // write the diff somewhere no reviewer is told to look.
        if (rest[0] === undefined || !isAbsolute(rest[0])) return fail(USAGE);
        // Refused before anything in the folder is removed or written: a third argument, or a second that is none of `last`,
        // `recorded` and a commit.
        if (rest.length > 2) return fail(USAGE);
        if (rest[1] !== undefined && rest[1] !== "last" && rest[1] !== "recorded" && !isCommitId(cwd, rest[1])) {
          return fail(`prepare: ${JSON.stringify(printable(rest[1]))} is none of last, recorded and the full id of a commit this clone has.\n${USAGE}`);
        }
        // One prepareRound gives both: the key is that of the whole diff, and the files written below hold what the reviewers read (readPatch
        // and readFixes), which leave out the verbatim Template mirrors.
        // A fixes patch is only ever this prepare's: an earlier round's would
        // have the reviewers read old fixes.
        rmSync(join(rest[0], "fixes.patch"), { force: true });
        const headFile = join(rest[0], "head");
        const preparedHead = existsSync(headFile) ? readFileSync(headFile, "utf8").trim() : undefined;
        // `last` is the HEAD the round before printed, which this folder's `head` file holds: no HEAD to copy from the last round's output.
        if (rest[1] === "last" && preparedHead === undefined) return fail("prepare <dir> last: no round was prepared in this folder yet, so there is no HEAD to read the fixes since. Run prepare <dir> first.");
        // `recorded`: the fixes since the commit the branch's last recorded review vouched for (recordedAnchor), in a new run.
        const sinceCommit = rest[1] === "last" ? preparedHead : rest[1];
        const since: FixesSince | undefined = rest[1] === "recorded" ? { recorded: true } : sinceCommit === undefined ? undefined : { commit: sinceCommit, preparedHead };
        const round = prepareRound(cwd, since === undefined ? {} : { since });
        if (round.since !== undefined) process.stderr.write(`Fixes since ${round.since}, the commit the last recorded review of this branch vouched for.\n`);
        mkdirSync(rest[0], { recursive: true });
        // What the reviewers read, without the verbatim Template mirrors; the key printed below is that of the whole diff.
        writeFileSync(join(rest[0], "diff.patch"), round.readPatch);
        if (round.readFixes !== undefined) writeFileSync(join(rest[0], "fixes.patch"), round.readFixes);
        // The HEAD this round reads, for the next round's fixesPatch.
        writeFileSync(join(rest[0], "head"), `${round.head}\n`);
        // On stderr, since callers read the two lines on stdout. Round 1 only: a later round is the same branch.
        const size = since === undefined ? sizeWarning(round.patch) : undefined;
        if (size !== undefined) process.stderr.write(`⚠ ${size}\n`);
        out(`${round.key}\n${round.head}`);
        return 0;
      }
      case "size": {
        // The gate's warning: the same one prepare gives in round 1, for HEAD's diff against <base> (its merge base with origin/main by default). It only advises, so a
        // branch past the line still exits 0; nothing is printed for one under it.
        if (rest.length > 1) return fail(USAGE);
        const size = sizeWarning(reviewPatch(cwd, rest[0] || mergeBase(cwd)));
        if (size !== undefined) out(`⚠ ${size}`);
        return 0;
      }
      case "check": {
        const result = checkReview(cwd, rest[0] || mergeBase(cwd), env);
        if (!result.ok) return fail(result.message);
        out(result.message);
        return 0;
      }
      case "stage": {
        const [directory, source] = rest;
        if ((rest.length !== 1 && rest.length !== 2) || directory === undefined || !isAbsolute(directory)) return fail(USAGE);
        const staged = stageFindings(directory, JSON.parse(readFileSync(source === undefined || source === "-" ? 0 : source, "utf8")));
        out(`Staged ${staged.added} finding(s); ${staged.total} in ${join(directory, STAGED_FILE)}.`);
        return 0;
      }
      case "record": {
        const flag = rest.indexOf("--staged");
        const stagedDirectory = flag < 0 ? undefined : rest[flag + 1];
        if (flag >= 0 && (stagedDirectory === undefined || !isAbsolute(stagedDirectory) || rest.length !== 3 || flag !== 1)) return fail(USAGE);
        if (rest[0] === undefined || (flag < 0 && rest.length !== 1)) return fail(USAGE);
        let text = readFileSync(rest[0] === "-" ? 0 : rest[0], "utf8");
        if (stagedDirectory !== undefined) {
          // The file's own findings are kept whole (a value that isn't an array is the schema's to refuse, as without --staged).
          const parsed: unknown = JSON.parse(text);
          if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return fail("The review input must be a JSON object.");
          const own = (parsed as { findings?: unknown }).findings;
          if (own !== undefined && !Array.isArray(own)) return fail("findings in the review input must be an array.");
          text = JSON.stringify({ ...parsed, findings: [...readStaged(stagedDirectory), ...(own ?? [])] });
        }
        const note = (message: string) => process.stderr.write(`.security: ${printable(message)}\n`);
        const report = renderReport(
          recordReview(cwd, mergeBase(cwd), JSON.parse(text) as ReviewInput, undefined, undefined, {
            slug,
            onSecurity: (outcome) => {
              const status = new Map(readEntries(cwd).map((entry) => [entry.file, entry.status]));
              for (const file of outcome.written) note(`wrote ${file} (${status.get(file) ?? "unknown"})`);
              for (const { finding, entry } of outcome.repeated) note(`${finding} repeats ${entry}, no new entry`);
              for (const warning of outcome.warnings) note(plainText(warning));
            },
          }),
        );
        out(report);
        return 0;
      }
      case "accept": {
        const [id, ...reason] = rest;
        if (id === undefined || reason.length === 0) return fail(USAGE);
        out(renderReport(acceptFinding(cwd, mergeBase(cwd), id, reason.join(" "), (message) => process.stderr.write(`.security: ${printable(message)}\n`))));
        return 0;
      }
      case "accept-open": {
        const ledger = readLedger(cwd, diffKey(cwd, mergeBase(cwd)));
        if (ledger === undefined) return fail("No review is recorded for this diff.");
        const decision = parseOpenDecision(rest);
        if (decision === undefined) {
          out(renderOpen(ledger));
          return 0;
        }
        const decided = decideOpen(cwd, mergeBase(cwd), decision, (message) => process.stderr.write(`.security: ${printable(message)}\n`));
        out(renderReport(decided));
        if (decision.fix.length > 0) {
          const asked = decided.findings.filter((finding) => decision.fix.includes(finding.id));
          const blocking = asked.filter(isBlocking).map((finding) => finding.id);
          const waiting = asked.filter((finding) => !isBlocking(finding)).map((finding) => finding.id);
          out(
            [
              `Asked to be fixed: ${decision.fix.join(", ")}. Run /pre-push-review: it fixes them.`,
              ...(blocking.length === 0 ? [] : [`${blocking.join(", ")} block the push until then.`]),
              ...(waiting.length === 0 ? [] : [`${waiting.join(", ")} (Advisory) don't block the push.`]),
            ].join(" "),
          );
        }
        return 0;
      }
      case "report":
      case "notes":
      case "advisory": {
        const ledger = readLedger(cwd, diffKey(cwd, mergeBase(cwd)));
        if (ledger === undefined) return fail("No review is recorded for this diff.");
        if (command === "report") out(renderReport(ledger));
        else if (command === "notes") out(renderReviewNotes(ledger) || "No accepted findings.");
        else out(renderAdvisories(ledger) || "No open Advisory findings.");
        return 0;
      }
      case "body": {
        const [input, output] = rest;
        if (input === undefined || output === undefined || output === "-") return fail(USAGE);
        if (input !== "-" && sameFile(cwd, input, output)) return fail(`${output} is the input file: give the new description its own file.`);
        const existing = lstatOrUndefined(resolve(cwd, output));
        if (existing !== undefined && !existing.isFile() && !existing.isSymbolicLink()) return fail(`${output} exists and isn't a regular file.`);
        // Removed first, so a refusal leaves no file for `gh pr edit
        // --body-file` to publish, neither empty nor an earlier run's.
        rmSync(output, { force: true });
        const ledger = readLedger(cwd, diffKey(cwd, mergeBase(cwd)));
        if (ledger === undefined) return fail("No review is recorded for this diff.");
        const body = spliceReviewNotes(readFileSync(input === "-" ? 0 : input, "utf8"), renderReviewNotes(ledger));
        writeFileSync(output, body);
        out(`Wrote the PR description with its Review notes to ${output}.`);
        return 0;
      }
      case "latest": {
        const ledger = latestLedger(cwd, mergeBase(cwd));
        out(ledger === undefined ? "{}" : JSON.stringify(ledger, null, 2));
        return 0;
      }
      case "security":
        out(renderSecurityList(readEntries(cwd)));
        return 0;
      case "next":
        out(`R${takenIds(cwd, mergeBase(cwd)) + 1}`);
        return 0;
      case "repo": {
        // gh ignores an empty GH_REPO and falls back to its default repository, so a slug that isn't
        // OWNER/REPO fails here with a non-zero exit, and the skill stops its command chain on it.
        const problem = repoSlugProblem(slug);
        if (problem !== undefined) return fail(problem);
        out(slug);
        return 0;
      }
      default:
        return fail(USAGE);
    }
  } catch (error) {
    return fail(error instanceof z.ZodError ? `The review doesn't parse:\n${z.prettifyError(error)}` : String((error as Error).message ?? error));
  }
}

// Whether this module is the script being run. Real paths are compared, so a
// symlinked checkout still runs the command: a guard that silently didn't
// match would exit 0 and let the gate pass.
function isEntryPoint(): boolean {
  const script = process.argv[1];
  if (script === undefined) return false;
  try {
    return realpathSync(script) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
}

if (isEntryPoint()) {
  process.exitCode = main(process.argv.slice(2));
}
