// ---------------------------------------------------------------------------
// The Factory's own values for the local review (ADR 0010). The Template's Seed
// file of this name configures all of Sandcastle; the Factory carries no Sandcastle,
// so this holds only the two names lib/review-ledger.mts imports. Start each declaration at the
// beginning of a line as `export const NAME`: scripts/review.sh looks for them that way.
// ---------------------------------------------------------------------------

export const REPO_SLUG = "mpaulosky/Application_Factory";

// Files under an Owned Baseline pattern (OWNED_PATHS in lib/review-ledger.mts) that are the
// Factory's own, not copies of the Template's. They are listed so that a change to them is
// reviewed: a branch that changes only Owned files passes the review check as a re-Apply
// would, and the Factory never re-Applies to itself. Each entry is a file, exactly as git names it.
export const NOT_OWNED_BASELINE_PATHS: readonly string[] = [
  ".github/workflows/template-tests.yml",
  ".gitignore",
  ".markdownlint-cli2.jsonc",
  ".yamllint.yml",
  "scripts/gate.sh",
  "scripts/tests/gate.test.sh",
];
