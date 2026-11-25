---
name: Chore
about: Ask for maintenance that changes no behaviour (a port, a refactor, a dependency or tooling change)
title: "[Chore] "
labels: chore
assignees: mpaulosky
---

# Chore

## What happens

What is there now, and why it needs the work.

## What we want

What is true once the work is done.

## Size

Keep the change small enough for one pull request that one review can read in full: 30 files and 3,000 changed lines at most, tests included (the line
the pre-push review warns at), and at most one open decision. Split a bigger change into smaller issues before work starts, ordered with GitHub's
"blocked by" links, so each one merges on its own.

Expected size: about N files and N changed lines, tests included.

## Split into

Only for a change past 30 files or 3,000 lines: the pull requests it becomes, in merge order, one issue each with its own acceptance criteria. Each stays under the
line and leaves the tree working (a shim the next one still needs stays until the last one removes it). Delete this section for a change under the line.

1. ...

## Out of scope

What this issue leaves for another.

## Notes

Anything else: the ADRs it touches, the files it changes, how it should roll out.
