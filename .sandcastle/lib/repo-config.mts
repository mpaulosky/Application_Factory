// ---------------------------------------------------------------------------
// The Factory's own values for the local review (ADR 0010). The Template's Seed
// file of this name configures all of Sandcastle; the Factory carries no Sandcastle,
// so this holds only the two names lib/review-ledger.mts imports. Keep each as a
// one-line `export const NAME`: scripts/review.sh looks for them that way.
// ---------------------------------------------------------------------------

export const REPO_SLUG = "mpaulosky/Application_Factory";

// Files under an Owned Baseline pattern (OWNED_PATHS in lib/review-ledger.mts) that are the
// Factory's own. A change to only Owned files passes the review check as a re-Apply would; the
// Factory never re-Applies to itself, so it exempts nothing.
export const NOT_OWNED_BASELINE_PATHS: readonly string[] = [];
