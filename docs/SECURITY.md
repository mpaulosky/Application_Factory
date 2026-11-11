# Security Policy

## Supported Versions

The Application Factory has no releases. `main` is the only supported version, and a fix lands there; an Application gets it by a Rollout (`rollout.sh`).

## Reporting a Vulnerability

**Please do not open a public issue for a security vulnerability.**

GitHub's private vulnerability reporting is off for this repository, so email <matthew.paulosky@outlook.com> with the subject `[SECURITY] Application_Factory`.

Please include:

1. A description of the vulnerability and its impact
2. Steps to reproduce it
3. The affected commit
4. A suggested fix, if you have one

Please keep the details private until a fix has landed on `main`.

## Accepted security findings from the local review

The pre-push review (ADR 0010, `.claude/skills/pre-push-review/`) never lists an accepted security finding in a public PR description: the notes only count them.
The person who accepted one keeps its reason in the review's ledger under `.git/review` on their own machine, which is not shared, so they email it as above,
naming the branch, before pushing.

## Contributors

- Never commit secrets, API keys or passwords; keep them in untracked local configuration and in repository secrets for CI.
- Keep dependencies current and act on Dependabot security alerts.
