#!/usr/bin/env python3
"""Post Claude Review's findings as one PR review on the head commit.

Called by .github/workflows/claude-review.yml's post job, once Claude has
reviewed the PR:

    FINDINGS='{"summary": ..., "findings": [...]}' \\
        python3 .github/scripts/post_claude_review.py --repo owner/name --pr 42 --head <sha>

FINDINGS is the review step's structured output: a summary, plus a path,
line and body for each finding. It may instead, or as well, describe the PR in
parts, each optional: an overview, changes (strings), files ({path,
description}), advisory (strings) and not_reviewed, and give each finding a
severity (high, medium or low). --effort, optional, is free text for the body's
footer.

The body reads like Copilot's review: a headline (Changes recommended or No
changes recommended), the title of the most severe finding, the open issues
(a severity badge and a link to each thread, High first), What changed in this
PR folded (the summary as it is, then the parts), the advisory and unreviewed
notes, the findings outside the diff, and a <sub> footer. The thread links
exist only once the review is posted, so the body is then edited to add them;
if reading the threads or the edit fails, the step warns and the first body,
with the titles unlinked, stays. Claude never holds a write token; this
script, in its own job, is what posts. It posts one COMMENT review pinned to
the head commit, each finding an inline comment on the new side of the
diff, so the PR records it the same way it records Copilot's.

GitHub rejects a whole review if one comment sits on a line outside the
diff, so a finding whose line isn't in the diff goes into the review body
instead. A finding on a changed file whose line isn't a line of that file
at all (past its end) is Claude citing the wrong number, not a finding
about unchanged code: it goes on the file's nearest line in the diff, as a
thread, with a note. A body finding opens no review thread to hold the merge, so the
body's second line is OFF_DIFF_MARKER, which PR Auto-Merge holds on while
that review is the latest of the head, past the review cap too. The step
passes with a warning: a failed check would leave the PR UNSTABLE, which
PR Auto-Merge never merges, and a later run with its jobs skipped would
replace it anyway. Only
malformed findings, a failed API call or empty findings fail the step: the
post job runs only once Claude has answered, so empty findings mean the
answer quoted a secret of the review job and was withheld.

The review job's check, inline in claude-review.yml, is what withholds it,
and it redacts anything shaped like a credential before the answer becomes
the FINDINGS the post job's log prints. This script redacts again with the
same CREDENTIAL, which must match the workflow's, and warns: those matches
are public text from the PR far more often than leaks. The body always
starts with MARKER: the review is posted as github-actions[bot], and the
marker is how PR Auto-Merge and the skill's landing decision tell it apart
from anything else posted under that login.

A comment may carry one GitHub suggestion, a fenced block the reader applies to the commented
line with one click, and only a single line (vet_suggestions): any other is turned into a
plain text fence. So is every suggestion in a comment moved to the nearest diff line, which
would replace the wrong line, and in a body finding, where none can be applied. A body
finding is indented under its bullet so its paragraphs stay with it.

Standard library only, like release_post.py; the gh CLI does the rest.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import unicodedata
import urllib.parse

# Accepted limit (ADR 0004 of the repo-ci-baseline skill, in
# mpaulosky/Application_Factory): the marker proves the login,
# not that Claude wrote the review; see claude-review.yml.
MARKER = "<!-- claude-review -->"
# The second line of a review with findings outside the diff.
OFF_DIFF_MARKER = "<!-- claude-review:off-diff -->"

# The new-side start of a hunk: "@@ -a,b +c,d @@" (",d" is optional).
HUNK = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")

# Text shaped like a credential, in the formats the tokens are issued in, so
# a long snake_case name such as ghs_installation_token_value isn't one:
# Anthropic keys and OAuth tokens, GitHub tokens (classic, fine-grained and
# today's ghs_<digits>_<JWT> installation tokens), and JWTs. No boundary on
# the left, so text run up against a token (token_ghp_..., x-eyJ...) can't
# hide it; a match only warns, so redacting a little too much costs little.
CREDENTIAL = re.compile(
    r"sk-ant-[A-Za-z0-9_-]{20,}"
    r"|gh[pousr]_[0-9]+_eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"
    r"|gh[pousr]_[A-Za-z0-9]{36,}"
    r"|github_pat_[A-Za-z0-9]{22}_[A-Za-z0-9]{59}"
    r"|eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"
)
REDACTED = "[redacted]"

# CREDENTIAL scans from every `eyJ`, and a long run of token characters (the first segment of a JWT
# holds the letters e, y and J) is rescanned from each one: quadratic on text a diff can steer.
# So a token, with the dots joining its segments, that holds a run longer than MAX_TOKEN_RUN and
# one of the strings every CREDENTIAL alternative starts with (CREDENTIAL_START) is replaced whole
# before CREDENTIAL looks at the text: it leaves nothing of a long JWT (its signature included)
# behind. A long token without one can't match CREDENTIAL, and legitimate review text has them (a
# generated test's file name, a long identifier, a digest), so it is left alone. What is left has
# no run holding a start longer than MAX_TOKEN_RUN characters, which CREDENTIAL scans in time
# linear in the text.
MAX_TOKEN_RUN = 128
TOKEN_CHAIN = re.compile(r"[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*")
CREDENTIAL_START = re.compile(r"eyJ|gh[pousr]_|sk-ant-|github_pat_")


def redact_credentials(text):
    """The text with credential-shaped text replaced, and how many were replaced."""
    count = 0

    def long_chain(match):
        nonlocal count
        chain = match.group()
        if any(len(run) > MAX_TOKEN_RUN for run in chain.split(".")) and CREDENTIAL_START.search(chain):
            count += 1
            return REDACTED
        return chain

    text = TOKEN_CHAIN.sub(long_chain, text)
    text, found = CREDENTIAL.subn(REDACTED, text)
    return text, count + found


MAX_PATH = 1024


def redact_path(path):
    """A finding's path with credential-shaped text replaced, and how many were replaced.

    Not the long-run rule: a long name that holds one of CREDENTIAL_START's strings without being
    a credential (a generated test's name with `eyJ` in it) would be replaced whole by it, and a
    path that no longer matches the diff leaves its finding outside it. A long name with none of
    them is left alone by redact_credentials already. A path past MAX_PATH is no real path and
    gets the full rule.
    """
    if len(path) > MAX_PATH:
        return redact_credentials(path)
    return CREDENTIAL.subn(REDACTED, path)

# A fence whose info string starts with `suggestion`, anywhere in a body (after a list or quote
# marker, or a lone CR, too): GitHub shows its content as a change the reader can apply with
# one click to the line the comment sits on, and the legacy `suggestion:-1+1` form changes more.
# Any whitespace but a line break may sit between the fence and the info string.
# A match starts only at the start of a run of fence characters, so a long run costs a linear scan.
SUGGESTION_FENCE = re.compile(r"(?i)((?<!`)`{3,}|(?<!~)~{3,})[^\S\r\n]*suggestion\b")
# The one form a comment may keep: a fence on its own lines whose info string is exactly
# `suggestion`, holding one non-empty line. The leading class and \S are disjoint, so
# matching is linear.
ONE_LINE_SUGGESTION = re.compile(r"(?m)^(`{3,}|~{3,})suggestion[ \t]*\n([^\S\n\r]*\S[^\n\r]*)\n\1[ \t]*$")


class MalformedFindings(ValueError):
    pass


class GitHub:
    """The two calls this script makes, through the gh CLI."""

    def __init__(self, repository):
        self.repository = repository

    def line_count(self, path, ref):
        """How many lines the file has at ref, or None when it can't be read."""
        try:
            data = subprocess.run(
                ["gh", "api", "-H", "Accept: application/vnd.github.raw",
                 f"repos/{self.repository}/contents/{urllib.parse.quote(path)}?ref={ref}"],
                check=True,
                capture_output=True,
            ).stdout
        except (subprocess.CalledProcessError, OSError):
            return None
        # Line numbers count newlines only, as in diff_lines(); a last line
        # without one still counts. Counted in bytes, so a file that isn't
        # UTF-8 can't fail the step, and a lone \r isn't read as a newline.
        return data.count(b"\n") + (0 if data.endswith(b"\n") or not data else 1)

    def pull_files(self, number):
        output = subprocess.run(
            ["gh", "api", "--paginate", "--slurp", f"repos/{self.repository}/pulls/{number}/files?per_page=100"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        return [file for page in json.loads(output) for file in page]

    def create_review(self, number, review):
        """Posts the review and returns it as GitHub answered (its id among it)."""
        output = subprocess.run(
            ["gh", "api", "--method", "POST", f"repos/{self.repository}/pulls/{number}/reviews", "--input", "-"],
            input=json.dumps(review),
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        return json.loads(output or "{}")

    def review_comments(self, number, review_id):
        output = subprocess.run(
            ["gh", "api", "--paginate", "--slurp",
             f"repos/{self.repository}/pulls/{number}/reviews/{review_id}/comments?per_page=100"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
        return [comment for page in json.loads(output) for comment in page]

    def update_review(self, number, review_id, body):
        subprocess.run(
            ["gh", "api", "--method", "PUT", f"repos/{self.repository}/pulls/{number}/reviews/{review_id}", "--input", "-"],
            input=json.dumps({"body": body}),
            check=True,
            capture_output=True,
            text=True,
        )


SEVERITIES = ("high", "medium", "low")


def is_strings(value):
    return isinstance(value, list) and all(isinstance(item, str) for item in value)


def parse_findings(text):
    """The report and findings from the review step's JSON, or MalformedFindings.

    The report is the fields that describe the PR, each optional: today's summary (Markdown, posted
    as it is), or the structured overview, changes, files, advisory and not_reviewed. An answer
    needs a summary or an overview. A finding may carry a severity.
    """
    try:
        data = json.loads(text)
    except json.JSONDecodeError as error:
        raise MalformedFindings(f"not JSON ({error})") from error
    if not isinstance(data, dict) or not (isinstance(data.get("summary"), str) or isinstance(data.get("overview"), str)):
        raise MalformedFindings("no summary string")
    for key in ("summary", "overview", "not_reviewed"):
        if key in data and not isinstance(data[key], str):
            raise MalformedFindings(f"{key} is not a string")
    for key in ("changes", "advisory"):
        if key in data and not is_strings(data[key]):
            raise MalformedFindings(f"{key} is not a list of strings")
    if "files" in data and not (
        isinstance(data["files"], list)
        and all(isinstance(file, dict) and isinstance(file.get("path"), str) and isinstance(file.get("description"), str)
                for file in data["files"])
    ):
        raise MalformedFindings("files is not a list of {path, description} strings")
    findings = data.get("findings")
    if not isinstance(findings, list):
        raise MalformedFindings("findings is not a list")
    for index, finding in enumerate(findings):
        if not (
            isinstance(finding, dict)
            and isinstance(finding.get("path"), str)
            and type(finding.get("line")) is int
            and isinstance(finding.get("body"), str)
        ):
            raise MalformedFindings(f"finding {index} needs a string path, an integer line and a string body")
        if "severity" in finding and finding["severity"] not in SEVERITIES:
            raise MalformedFindings(f"finding {index} has a severity other than high, medium or low")
    report = {key: data[key] for key in ("summary", "overview", "changes", "advisory", "not_reviewed") if key in data}
    if "files" in data:
        # Only the validated fields: an extra one of another type would break the redaction.
        report["files"] = [{"path": file["path"], "description": file["description"]} for file in data["files"]]
    return report, findings


def diff_lines(files):
    """Each file's new-side line numbers in the diff: the lines a review comment can sit on."""
    lines = {}
    for file in files:
        commentable = lines.setdefault(file["filename"], set())
        line = None
        # GitHub's patches are newline-delimited; splitlines() would also split
        # on form feeds and other line breaks inside a line's content.
        for row in (file.get("patch") or "").split("\n"):
            hunk = HUNK.match(row)
            if hunk:
                line = int(hunk.group(1))
            elif line is not None and not row.startswith("-") and not row.startswith("\\"):
                commentable.add(line)
                line += 1
    return lines


def redact(summary, findings):
    """The summary and findings with credential-shaped text replaced, and how many were replaced.

    The summary is a string or the report parse_findings returns, whose every string is cleaned
    (a file's path as a path).
    """
    count = 0
    def clean(text, scan=redact_credentials):
        nonlocal count
        if isinstance(text, list):
            return [clean(item, scan) for item in text]
        if isinstance(text, dict):
            return {key: clean(value, redact_path if key == "path" else redact_credentials) for key, value in text.items()}
        text, n = scan(text)
        count += n
        return text
    summary = clean(summary)
    findings = [{**f, "path": clean(f["path"], redact_path), "body": clean(f["body"])} for f in findings]
    return summary, findings, count


def defuse_suggestions(body):
    """The body with each suggestion fence turned into a plain text fence, so no one-click suggestion applies."""
    return SUGGESTION_FENCE.sub(r"\1text", body)


# Characters that render blank but are letters or symbols (Hangul fillers, the blank Braille cell,
# Khmer inherent vowels), which \s does not match.
BLANK_FILLERS = "\u115f\u1160\u17b4\u17b5\u2800\u3164\uffa0"


def suggestion_line_is_safe(fence, line):
    """Whether `line` can be the one line of a suggestion that `fence` opened.

    Not when it would close the block (a suggestion with no content deletes the line), nor when
    it holds a character the reader can't see in the diff: a control or format character
    (zero-width, bidirectional) or a Unicode line or paragraph separator, or when nothing in it is
    visible (spaces, combining marks and the blank fillers only).
    """
    closes = re.fullmatch(" {0,3}" + re.escape(fence[0]) + "{" + str(len(fence)) + ",}[ \t]*", line)
    hidden = any(unicodedata.category(c) in ("Cf", "Zl", "Zp") or (unicodedata.category(c) == "Cc" and c != "\t") for c in line)
    blank = all(c in BLANK_FILLERS or unicodedata.category(c) in ("Zs", "Cc", "Mn") for c in line)
    return not closes and not hidden and not blank


def vet_suggestions(body):
    """The body as it is when it holds no suggestion, or exactly one well-formed single-line suggestion.

    Any other suggestion (several, several lines, the multi-line form, an empty or hidden one, text
    redacted from a credential) is defused: the prompt asks for one line only, but a suggestion is
    applied with one click and what the model writes comes from a PR it read, so the script holds
    the rule.
    """
    fences = SUGGESTION_FENCE.findall(body)
    if not fences:
        return body
    block = ONE_LINE_SUGGESTION.search(body)
    if len(fences) == 1 and block and REDACTED not in block.group(2) and suggestion_line_is_safe(block.group(1), block.group(2)):
        return body
    return defuse_suggestions(body)


CHANGES_RECOMMENDED = "### 🟡 Changes recommended"
NO_CHANGES_RECOMMENDED = "### 🟢 No changes recommended"
OFF_DIFF_HEADER = ("Findings outside the diff (no thread to resolve, so they hold the merge until the next "
                   "push gets a fresh review):")
# A shields.io badge for each severity, in Copilot's pill colours (red, amber, blue). GitHub
# serves the image through its camo proxy, so a reader's browser never contacts shields.io, and
# the URL carries only the level; the alt text is the level, so the list reads without the image.
BADGE_COLOURS = {"high": "d1242f", "medium": "bf8700", "low": "0969da"}
MAX_TITLE = 200
# GitHub refuses a review body over 65,536 characters. The markers and the findings outside the
# diff are never cut: What changed goes first, then Advisory and Not reviewed, then the open
# issues past what fits. The effort in the footer is capped at MAX_EFFORT.
MAX_BODY = 65_536
MAX_EFFORT = 200
WHAT_CHANGED_CUT = "_What changed in this PR was left out to fit GitHub's limit on a review's length._"
EXTRAS_CUT = "_Advisory and Not reviewed were left out to fit GitHub's limit on a review's length._"


def badge(severity):
    """The severity's badge and a space, or nothing for a finding without one."""
    if severity not in BADGE_COLOURS:
        return ""
    level = severity.capitalize()
    return f"![{level}](https://img.shields.io/badge/{level}-{BADGE_COLOURS[severity]}?style=flat-square) "


def title_of(body):
    """A finding's title: the bold first line Claude starts it with, without the **, else its first line.

    On one line, and at most MAX_TITLE characters.
    """
    first = body.strip().split("\n", 1)[0]
    bold = re.match(r"\s*\*\*(.+?)\*\*", first)
    title = " ".join((bold.group(1) if bold else first).split())
    return title if len(title) <= MAX_TITLE else title[: MAX_TITLE - 1].rstrip() + "…"


def by_severity(items, severity=lambda item: item.get("severity")):
    """The items sorted High, Medium, Low, then unrated, keeping their order within each."""
    rank = {level: index for index, level in enumerate(SEVERITIES)}
    return sorted(items, key=lambda item: rank.get(severity(item), len(SEVERITIES)))


def escape_link_text(text):
    return re.sub(r"([\\\[\]])", r"\\\1", text)


def cell(text):
    """Text that stays in one cell of a Markdown table."""
    return " ".join(text.split()).replace("|", "\\|")


def bullets(items):
    """A Markdown list, each item's later lines indented under its bullet."""
    return "\n".join("- " + item.strip().replace("\n", "\n  ") for item in items)


def what_changed(report):
    """The What changed part: today's summary as it is, then the structured overview, changes and files."""
    parts = [report.get("summary", "").strip(), report.get("overview", "").strip()]
    if report.get("changes"):
        parts.append("**Changes:**\n\n" + bullets(report["changes"]))
    if report.get("files"):
        rows = [f"| `{cell(file['path'])}` | {cell(file['description'])} |" for file in report["files"]]
        parts.append("\n".join(["| File | Description |", "| --- | --- |", *rows]))
    return "\n\n".join(part for part in parts if part)


def place_findings(findings, commentable, line_count=lambda path: None):
    """The inline comments (with the finding each is for) and the off-diff bullets.

    line_count(path) is the file's length at the head, or None if unknown.
    """
    threads = []
    off_diff = []
    for finding in findings:
        path, line, body = finding["path"], finding["line"], finding["body"]
        lines = commentable.get(path, ())
        if line in lines:
            threads.append((finding, {"path": path, "line": line, "side": "RIGHT", "body": vet_suggestions(body)}))
        elif lines and (line < 1 or (length := line_count(path)) is not None and line > length):
            # Not a line of the file: Claude got the number wrong.
            nearest = min(lines, key=lambda n: (abs(n - line), n))
            note = f"_Claude cited line {line}, which isn't a line of this file, so this is on the nearest line in the diff._"
            # A suggestion would replace the nearest line, not the one Claude meant.
            threads.append((finding, {"path": path, "line": nearest, "side": "RIGHT",
                                      "body": f"{note}\n\n{defuse_suggestions(body)}"}))
        else:
            # In the review body a suggestion can't be applied, and a body of several
            # paragraphs stays under its bullet when indented.
            indented = defuse_suggestions(body).replace("\n", "\n  ")
            off_diff.append(f"- `{path}:{line}`: {indented}")
    return threads, off_diff


def render_body(report, findings, threads, off_diff, effort=None, notes=(), links=None):
    """The review's body in Copilot's layout, within MAX_BODY when the markers and off-diff findings allow.

    links[i] is the URL of threads[i]'s thread, or None; without links the titles aren't linked.
    """
    links = links or [None] * len(threads)
    issues = [f"{badge(finding.get('severity'))}" + (f"[{escape_link_text(title_of(finding['body']))}]({url})" if url
                                                     else title_of(finding["body"]))
              for (finding, _), url in by_severity(list(zip(threads, links, strict=True)),
                                                   lambda pair: pair[0][0].get("severity"))]
    changed = what_changed(report)
    effort = " ".join(effort.split()) if effort else ""
    if len(effort) > MAX_EFFORT:
        effort = effort[:MAX_EFFORT - 1] + "…"
    footer = f"Claude Review · Review effort: {effort}" if effort else "Claude Review"
    footer = footer.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    has_extras = bool(report.get("advisory") or report.get("not_reviewed", "").strip())

    def compose(changed, shown, extras=True):
        parts = [CHANGES_RECOMMENDED if findings else NO_CHANGES_RECOMMENDED]
        if findings:
            parts.append(title_of(by_severity(findings)[0]["body"]))
        if issues:
            count = f"{len(issues)} open issue" + ("" if len(issues) == 1 else "s")
            listed = "\n".join(f"- {issue}" for issue in issues[:shown])
            if shown < len(issues):
                listed += f"\n\n_{len(issues) - shown} more in the threads below, left out to fit GitHub's limit._"
            parts.append(f"<details open><summary><b>{count}</b></summary>\n\n{listed}\n\n</details>")
        if changed:
            parts.append(f"<details><summary><b>What changed in this PR</b></summary>\n\n{changed}\n\n</details>")
        if not extras:
            parts.append(EXTRAS_CUT)
        else:
            if report.get("advisory"):
                parts.append("**Advisory (not blocking)**\n\n" + bullets(report["advisory"]))
            if report.get("not_reviewed", "").strip():
                parts.append("**Not reviewed**\n\n" + report["not_reviewed"].strip())
        parts += notes
        if off_diff:
            # The last section with bullets: PR Auto-Merge reads every "- " line after its header as a finding.
            parts.append(OFF_DIFF_HEADER + "\n\n" + "\n".join(off_diff))
        parts.append(f"<sub>{footer}</sub>")
        markers = f"{MARKER}\n{OFF_DIFF_MARKER}" if off_diff else MARKER
        return f"{markers}\n" + "\n\n".join(parts)

    body = compose(changed, len(issues))
    if len(body) > MAX_BODY and changed:
        changed = WHAT_CHANGED_CUT
        body = compose(changed, len(issues))
    extras = True
    if len(body) > MAX_BODY and has_extras:
        extras = False
        body = compose(changed, len(issues), extras)
    shown = len(issues)
    while len(body) > MAX_BODY and shown:
        shown -= 1
        body = compose(changed, shown, extras)
    return body


def build_review(report, findings, commentable, head, line_count=lambda path: None, effort=None, notes=()):
    """The review to post, and its threads: inline comments for findings on the diff, the rest in the body.

    report is what parse_findings returns (a summary string alone is taken as {"summary": ...});
    line_count(path) is the file's length at the head, or None if unknown; notes are paragraphs
    for the body, before the findings outside the diff.
    """
    if isinstance(report, str):
        report = {"summary": report}
    threads, off_diff = place_findings(findings, commentable, line_count)
    body = render_body(report, findings, threads, off_diff, effort, notes)
    review = {"commit_id": head, "event": "COMMENT", "body": body, "comments": [comment for _, comment in threads]}
    return review, lambda links: render_body(report, findings, threads, off_diff, effort, notes, links)


def thread_links(comments, posted):
    """The URL of each comment's thread, from the review's comments as GitHub lists them, or None.

    A comment is matched by its path and body; one left unmatched takes the URL at its place when
    GitHub lists as many comments as were posted (it lists them in the order they were made).
    """
    urls = [c.get("html_url") if isinstance(c, dict) and str(c.get("html_url", "")).startswith("https://") else None
            for c in posted]
    by_key = {}
    for c, url in zip(posted, urls, strict=True):
        if url and isinstance(c, dict):
            by_key.setdefault((c.get("path"), c.get("body")), []).append(url)
    links = []
    for index, comment in enumerate(comments):
        matched = by_key.get((comment["path"], comment["body"]))
        if matched:
            links.append(matched.pop(0))
        elif len(posted) == len(comments):
            links.append(urls[index])
        else:
            links.append(None)
    return links


def main(argv=None, gh=None, findings=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", help="owner/name")
    parser.add_argument("--pr", type=int, help="the PR to review")
    parser.add_argument("--head", help="the head commit Claude reviewed")
    parser.add_argument("--effort", help="the review effort for the body's footer, such as 'high (claude-sonnet-5-5), 2 batches'")
    args = parser.parse_args(argv)
    if not (args.repo and args.pr and args.head):
        parser.error("--repo, --pr and --head are required to post")

    text = os.environ.get("FINDINGS", "") if findings is None else findings
    if not text.strip():
        print("post_claude_review.py: Claude Review's findings are empty, though Claude answered: the answer "
              "quoted a secret of the review job and was withheld.", file=sys.stderr)
        print("::error::Claude's review reached the post job empty: it quoted a secret of the review job, so the "
              "review job's check (or GitHub's masking) withheld it. A prompt-injected diff may have got Claude to "
              "quote CLAUDE_CODE_OAUTH_TOKEN, the only long-lived secret in that job (its GITHUB_TOKEN expires with "
              "the job): check the PR and the review job's log, and rotate CLAUDE_CODE_OAUTH_TOKEN.")
        sys.exit(1)
    try:
        report, parsed = parse_findings(text)
    except MalformedFindings as error:
        print(f"post_claude_review.py: Claude Review's findings are malformed: {error}", file=sys.stderr)
        sys.exit(1)

    report, parsed, redacted = redact(report, parsed)
    # The review job's check redacts first; this counts what it did too.
    redacted += int(os.environ.get("REDACTED") or 0)
    notes = [f"{redacted} credential-shaped string(s) were redacted from this review."] if redacted else []

    gh = gh or GitHub(args.repo)
    review, linked = build_review(report, parsed, diff_lines(gh.pull_files(args.pr)), args.head,
                                  lambda path: gh.line_count(path, args.head), args.effort, notes)
    posted = gh.create_review(args.pr, review)
    if review["comments"]:
        # The threads' URLs exist only once the review is posted, so the body is edited to link them.
        # The edit fires pull_request_review "edited", which PR Review Submitted ignores, and the
        # markers and off-diff findings are the same in both bodies.
        try:
            links = thread_links(review["comments"], gh.review_comments(args.pr, posted["id"]))
            body = linked(links)
            if any(links) and body != review["body"]:
                gh.update_review(args.pr, posted["id"], body)
                review["body"] = body
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError, TypeError) as error:
            print(f"::warning::Couldn't link the review's threads from its body ({type(error).__name__}), so it "
                  "lists their titles without links.")
    in_body = len(parsed) - len(review["comments"])
    print(f"Posted Claude's review of {args.head} on PR #{args.pr}: {len(review['comments'])} inline, {in_body} in the body.")
    if in_body:
        print(
            f"::warning::{in_body} finding(s) outside the diff are in the review body; "
            "its off-diff marker holds the merge until the next push gets a fresh review."
        )
    if redacted:
        print(f"::warning::Redacted {redacted} credential-shaped string(s) from Claude's review. They aren't this "
              "job's secrets (an answer quoting those is withheld), so they are most likely sample or placeholder "
              "tokens from the PR.")
    return review


if __name__ == "__main__":
    main()
