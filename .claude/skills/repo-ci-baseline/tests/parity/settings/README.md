# The settings parity suite

`github/settings.py` through its command line: `tests/parity.test.sh settings [command...]`. The runner's header says what a case holds.
The cases were recorded from the Python script, which stays the reference while `rollout.py` still imports it (ADR 0017); `test.sh` runs them
against `github-settings.sh` too, which runs the C# port.

The suite carries its own `github/`: a small standard (`settings.json`, `main-rules.json`, `labels.json`, `legacy.json`), a `repos.txt` with each
scope, and two per-repo files (`repos/lib.json` adds a check and a label; `repos/loose.json` tries to loosen the standard). The runner lays it over a
copy of the skill's `github/` and runs the script from the copy, so the cases don't change when the Fleet's standard or registry does.

Every call is `gh api -X <method> <path> -H "Accept: application/vnd.github+json" [--input -]`, so `expected.calls` holds each call's whole argv,
and each write's body as canonical JSON (`tests/parity/fake-gh.sh`). A `--fix` case answers its second report from numbered fixtures
(`<key>.2.json`) where the fix changed the answer.

The cases cover a repo at the standard (exit 0); drift of every kind the script reports (exit 1): repo flags as Python's `repr` prints them, secret
scanning, Dependabot, CodeQL default setup beside a CodeQL workflow, the Actions token, unpinned actions, a ruleset whose conditions, bypass actors,
rules, merge methods, thread resolution and checks (one with no app) differ, a check kept outside the standard, legacy and other rulesets, classic
protection, the review cap, labels and retired labels, and a missing secret; the same as `--json`, byte for byte; `--fix` (it reports, applies and
reports again); `--fix --remove-legacy`; `--suspend` with and without a `main-rules` to disable; no repo and `--remove-legacy` without `--fix`
(exit 2); `--all` with every scope `repos.txt` gives, after the repos named; a per-repo file that adds and one that tries to loosen (exit 1); a
GitHub error past the 403 and 404 the script reads (exit 1); and what a repo doesn't offer (secret scanning, code scanning, rulesets) with no
workflows at all.

Not covered, on purpose:

- A repo argument that isn't `owner/repo`: the Python sent it to `gh` as it was, and the C# refuses it before any call (ADR 0017's Update for
  mpaulosky/Application_Factory#120), which `SettingsPortTests.cs` holds.
- More than 100 labels: the script reads the first page only, in both languages.
