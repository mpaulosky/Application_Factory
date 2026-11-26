import ast
import json
import os
import random
import re
import string
import subprocess
import sys
import textwrap
import time
import unicodedata
from pathlib import Path

import pytest

import post_claude_review as pcr

HEAD = "abc123"

PATCH = "\n".join(
    [
        "@@ -1,3 +1,4 @@",
        " unchanged",
        "-removed",
        "+added one",
        "+added two",
        " context",
        "@@ -20,2 +21,2 @@",
        " later context",
        "+later added",
    ]
)


def findings_json(*findings, summary="Looked at the diff."):
    return json.dumps(
        {"summary": summary, "findings": [{"path": p, "line": n, "body": b} for p, n, b in findings]}
    )


class FakeGitHub:
    """GitHub for main(): the review posted, and its comments read back with a URL each, unless fail names a call."""

    def __init__(self, files=None, lengths=None, fail=None):
        self.files = files if files is not None else [{"filename": "src/app.py", "patch": PATCH}]
        # src/app.py runs to line 30: lines 5-20 and 23-30 are real but outside the diff.
        self.lengths = lengths if lengths is not None else {"src/app.py": 30}
        self.fail = fail
        self.posted = []
        self.updates = []

    def line_count(self, path, ref):
        assert ref == HEAD
        return self.lengths.get(path)

    def pull_files(self, number):
        return self.files

    def create_review(self, number, review):
        # A copy: posted holds the first body, as GitHub got it, even after main edits the review.
        self.posted.append((number, {**review}))
        return {"id": 99, "body": review["body"]}

    def review_comments(self, number, review_id):
        assert (number, review_id) == (7, 99)
        if self.fail == "read":
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        _, review = self.posted[-1]
        return [{**comment, "html_url": f"https://github.com/octo/demo/pull/7#discussion_r{index}"}
                for index, comment in enumerate(review["comments"], 1)]

    def update_review(self, number, review_id, body):
        assert (number, review_id) == (7, 99)
        if self.fail == "update":
            raise subprocess.CalledProcessError(1, ["gh", "api"])
        self.updates.append(body)


def run(findings, gh=None):
    gh = gh or FakeGitHub()
    pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD], gh=gh, findings=findings)
    return gh


# diff_lines()


def test_diff_lines_are_the_new_side_lines_a_comment_can_sit_on():
    lines = pcr.diff_lines([{"filename": "src/app.py", "patch": PATCH}])

    assert lines == {"src/app.py": {1, 2, 3, 4, 21, 22}}


def test_line_breaks_other_than_newline_inside_a_line_dont_shift_the_count():
    patch = "\n".join(["@@ -1,2 +1,3 @@", " page one\x0cpage two", "+added\x85text\u2028more", " context"])

    assert pcr.diff_lines([{"filename": "doc.txt", "patch": patch}]) == {"doc.txt": {1, 2, 3}}


def test_a_file_without_a_patch_has_no_commentable_lines():
    assert pcr.diff_lines([{"filename": "logo.png"}]) == {"logo.png": set()}


# main()


def test_findings_become_one_review_on_the_head_commit():
    gh = run(findings_json(("src/app.py", 2, "Off by one."), ("src/app.py", 22, "Unused.")))

    assert len(gh.posted) == 1
    number, review = gh.posted[0]
    assert number == 7
    assert review["commit_id"] == HEAD
    assert review["event"] == "COMMENT"
    assert review["comments"] == [
        {"path": "src/app.py", "line": 2, "side": "RIGHT", "body": "Off by one."},
        {"path": "src/app.py", "line": 22, "side": "RIGHT", "body": "Unused."},
    ]
    assert "Looked at the diff." in review["body"]


def test_the_marker_is_always_first():
    for findings in (findings_json(), findings_json(("src/app.py", 2, "x")), findings_json(("other.py", 1, "y"))):
        _, review = run(findings).posted[0]
        assert review["body"].startswith(pcr.MARKER + "\n")


def test_a_finding_off_the_diff_moves_to_the_body():
    findings = findings_json(("src/app.py", 2, "On the diff."), ("src/app.py", 10, "Off the diff."),
                             ("README.md", 1, "Not changed."))
    gh = run(findings)

    _, review = gh.posted[0]
    assert [c["line"] for c in review["comments"]] == [2]
    assert "`src/app.py:10`" in review["body"] and "Off the diff." in review["body"]
    assert "`README.md:1`" in review["body"] and "Not changed." in review["body"]


def test_a_finding_off_the_diff_passes_the_step_with_a_warning(capsys):
    # The marker, not the check, holds the merge: a failed check would leave
    # the PR UNSTABLE, which PR Auto-Merge never merges.
    gh = run(findings_json(("src/app.py", 2, "On."), ("README.md", 1, "Off.")))

    assert len(gh.posted) == 1
    assert "::warning::1 finding(s) outside the diff" in capsys.readouterr().out
    assert "hold the merge" in gh.posted[0][1]["body"]
    # PR Auto-Merge reads this: it's the only thing holding the merge.
    assert gh.posted[0][1]["body"].startswith(pcr.MARKER + "\n" + pcr.OFF_DIFF_MARKER + "\n")


def test_a_line_past_the_end_of_a_changed_file_goes_on_its_nearest_diff_line(capsys):
    # A wrong line number on changed code, not a finding about unchanged code:
    # a thread to resolve, not a hold on the merge.
    gh = run(findings_json(("src/app.py", 598, "Wrong number."), ("src/app.py", 0, "Zero.")))

    _, review = gh.posted[0]
    assert [(c["line"], c["side"]) for c in review["comments"]] == [(22, "RIGHT"), (1, "RIGHT")]
    assert "cited line 598, which isn't a line of this file" in review["comments"][0]["body"]
    assert review["comments"][0]["body"].endswith("Wrong number.")
    assert pcr.OFF_DIFF_MARKER not in review["body"]
    assert "::warning::" not in capsys.readouterr().out


SUGGESTED = "**Wrong default**\n\nIt is 0.\n\n```suggestion\nvalue = 1\n```"


def test_a_suggestion_on_the_cited_line_is_kept():
    gh = run(findings_json(("src/app.py", 22, SUGGESTED)))

    _, review = gh.posted[0]
    assert review["comments"][0]["body"] == SUGGESTED


def test_a_suggestion_on_the_nearest_line_is_turned_into_plain_text():
    # The suggestion would replace the nearest diff line, not the line Claude meant.
    gh = run(findings_json(("src/app.py", 598, SUGGESTED)))

    _, review = gh.posted[0]
    body = review["comments"][0]["body"]
    assert "```suggestion" not in body
    assert "```text\nvalue = 1\n```" in body


@pytest.mark.parametrize(
    ("body", "expected"),
    [
        ("```suggestion\nx\n```", "```text\nx\n```"),
        ("````suggestion\nx\n````", "````text\nx\n````"),
        ("~~~ suggestion\nx\n~~~", "~~~text\nx\n~~~"),
        ("```SUGGESTION\nx\n```", "```text\nx\n```"),
        ("   ```suggestion\nx\n```", "   ```text\nx\n```"),
        ("a `suggestion` word and ```suggestions``` inline", "a `suggestion` word and ```suggestions``` inline"),
        ("```python\nsuggestion = 1\n```", "```python\nsuggestion = 1\n```"),
    ],
)
def test_defuse_suggestions_changes_only_suggestion_fences(body, expected):
    assert pcr.defuse_suggestions(body) == expected


@pytest.mark.parametrize(
    "body",
    [
        "> ```suggestion\nx\n> ```",
        "- ```suggestion\n  x\n  ```",
        "a\r```suggestion\nx\n```",
        "    ```suggestion\nx\n```",
        "```suggestion foo\nx\n```",
        "```suggestion:-1+1\nx\ny\n```",
        "````\n```suggestion\nx\n```\n````",
        "```suggestion\r\nx\r\n```",
        "```\x0bsuggestion\nx\n```",
        "```\x0csuggestion\nx\n```",
    ],
)
def test_defuse_suggestions_catches_every_form_a_fence_can_take(body):
    assert not pcr.SUGGESTION_FENCE.search(pcr.defuse_suggestions(body))


def test_a_suggestion_fence_after_a_quote_marker_is_defused_to_text():
    assert pcr.defuse_suggestions("> ```suggestion\nx") == "> ```text\nx"


GOOD = "**T**\n\nWhy.\n\n```suggestion\n    value = 1\n```"


@pytest.mark.parametrize(
    "body",
    [
        "No suggestion here.",
        GOOD,
        GOOD.replace("```", "~~~"),
        "```python\nsuggestion = 1\n```",
        "````suggestion\n```bash\n````",  # content that can't close a longer fence
        "```suggestion\n\tvalue = 1  \n```",  # a tab and trailing spaces are the line's own
        "```suggestion\n    ```\n```",  # four spaces make it indented text, not a closing fence
    ],
)
def test_vet_suggestions_keeps_no_suggestion_or_one_good_line(body):
    assert pcr.vet_suggestions(body) == body


@pytest.mark.parametrize(
    "body",
    [
        "```suggestion\na\nb\n```",  # two lines
        "```suggestion\n\n```",  # nothing
        "```suggestion\n   \n```",  # blank
        "```suggestion:-1+1\nx\n```",  # the multi-line form
        "```suggestion\nx\n```\n\n```suggestion\ny\n```",  # two blocks
        "```suggestion\ntoken = [redacted]\n```",  # a click would write the redaction
        "``` suggestion\nx\n```",  # not exactly the info string
        "- ```suggestion\n  x\n  ```",  # not on its own lines
        "```suggestion\nx\r\n```",  # a lone CR in the block
        "```suggestion\n```\n```",  # the content closes the block: a click would delete the line
        "~~~suggestion\n~~~\n~~~",
        "```suggestion\n  ```\n```",
        "````suggestion\n`````\n````",
        "```suggestion\nvalue = 1\n",  # never closed
        "```suggestion\nvalue = 1\n~~~",  # closed by the other marker
        "```suggestion\n\u200b\n```",  # a zero-width space is not a visible line
        "```suggestion\nvalue\u202e = 1\n```",  # a bidirectional override
        "```suggestion\nvalue\x00 = 1\n```",
        "```suggestion\nvalue\u2028= 1\n```",
        "```suggestion\n\u3164\n```",  # a Hangul filler renders blank
        "```suggestion\n \u2800 \n```",  # a blank Braille cell
        "```suggestion\n\u0301\n```",  # a lone combining mark
        "```suggestion\nx\n````",  # a closing fence longer than the opener
        "```suggestion\nx\n  ```",  # a closing fence indented
        "```\x0bsuggestion\nx\ny\n```",
    ],
)
def test_vet_suggestions_defuses_anything_but_one_good_line(body):
    vetted = pcr.vet_suggestions(body)

    assert vetted != body
    assert not pcr.SUGGESTION_FENCE.search(vetted)


def test_an_on_diff_comment_goes_through_vet_suggestions():
    body = "**T**\n\n```suggestion\na\nb\n```"
    _, review = run(findings_json(("src/app.py", 22, body))).posted[0]

    assert "```suggestion" not in review["comments"][0]["body"]
    assert "```text\na\nb\n```" in review["comments"][0]["body"]


def test_a_finding_off_the_diff_keeps_a_table_and_a_list_under_its_bullet():
    body = "**T**\n\n| a | b |\n| - | - |\n| 1 | 2 |\n\n- one\n- two"
    _, review = run(findings_json(("src/app.py", 10, body))).posted[0]

    for row in ("  | a | b |", "  | - | - |", "  | 1 | 2 |", "  - one", "  - two"):
        assert row in review["body"].split("\n")


def test_a_finding_off_the_diff_keeps_its_paragraphs_under_its_bullet_and_loses_its_suggestion():
    body = "**Title**\n\nWhat goes wrong.\n\n```suggestion\nx = 1\n```"
    _, review = run(findings_json(("src/app.py", 10, body))).posted[0]

    assert "- `src/app.py:10`: **Title**\n  \n  What goes wrong." in review["body"]
    assert "```suggestion" not in review["body"]
    assert "  ```text\n  x = 1\n  ```" in review["body"]


def test_a_real_line_outside_the_hunks_still_holds_the_merge():
    _, review = run(findings_json(("src/app.py", 10, "Unchanged code."))).posted[0]

    assert review["comments"] == []
    assert review["body"].startswith(pcr.MARKER + "\n" + pcr.OFF_DIFF_MARKER + "\n")


def test_an_unreadable_file_length_keeps_the_hold():
    # Unknown length: it may be a real line, so it's treated as one.
    gh = run(findings_json(("src/app.py", 598, "Maybe real.")), FakeGitHub(lengths={}))

    assert gh.posted[0][1]["comments"] == []
    assert pcr.OFF_DIFF_MARKER in gh.posted[0][1]["body"]


def test_a_file_the_diff_doesnt_touch_still_holds_the_merge():
    _, review = run(findings_json(("README.md", 999, "Not changed."))).posted[0]

    assert review["comments"] == []
    assert "`README.md:999`" in review["body"]


@pytest.mark.parametrize(
    "text, lines",
    [(b"", 0), (b"one\n", 1), (b"one\ntwo", 2), ("one\x0cstill one\x85and\u2028one\n".encode(), 1), (b"a\nb\n", 2),
     (b"caf\xe9\nna\xefve\n", 2), (b"one\rstill one\n", 1)],
)
def test_a_files_length_counts_newlines_only(text, lines, monkeypatch):
    # Bytes, as gh returns them: a file that isn't UTF-8 (the Latin-1 case)
    # still counts, and a lone \r isn't a newline.
    def fake_run(argv, **kwargs):
        assert argv[-1] == "repos/octo/demo/contents/docs/a%20b.md?ref=abc123"
        assert "text" not in kwargs
        return subprocess.CompletedProcess(argv, 0, stdout=text)
    monkeypatch.setattr(pcr.subprocess, "run", fake_run)

    assert pcr.GitHub("octo/demo").line_count("docs/a b.md", HEAD) == lines


@pytest.mark.parametrize("error", [subprocess.CalledProcessError(1, ["gh"]), FileNotFoundError("gh")])
def test_a_files_length_is_unknown_when_gh_fails(error, monkeypatch):
    # A 404 for a symlink or submodule, a rate limit, or no gh at all: the
    # finding stays in the body instead of failing the step.
    def fake_run(argv, **kwargs):
        raise error
    monkeypatch.setattr(pcr.subprocess, "run", fake_run)

    assert pcr.GitHub("octo/demo").line_count("docs/a.md", HEAD) is None


# Built at runtime, so this file holds nothing credential-shaped for a review
# of it to quote.
JWT = "eyJ" + "hbGciOiJIUzI1NiJ9" + "." + "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0" + "." + "abcdefghijklmnop"
TOKENS = [
    "sk-ant-" + "oat01-" + "a" * 40,
    "ghp_" + "b" * 36,
    "github_pat_" + "c" * 22 + "_" + "d" * 59,
    JWT,
    # Today's installation token: digits, then a JWT after an underscore.
    "ghs_" + "12345_" + JWT,
]


# Text run up against a token doesn't hide it.
PREFIXED = ["x-access-token:" + TOKENS[-1] + "@github.com", "token_" + TOKENS[1], "token_" + TOKENS[2],
            "x" + JWT, "token-" + JWT, "docs/session-" + JWT, "\\n" + TOKENS[1], "risk-" + TOKENS[0]]


@pytest.mark.parametrize("token", TOKENS + PREFIXED)
def test_a_credential_is_redacted_everywhere_and_warns(token, capsys):
    gh = FakeGitHub()
    findings = findings_json(("src/app.py", 2, f"The token is {token}."), (f"docs/{token}.md", 1, "Path."),
                             summary=f"Found {token} in the environment.")

    pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD], gh=gh, findings=findings)

    _, review = gh.posted[0]
    posted = json.dumps(review)
    assert token not in posted and not any(t in posted for t in TOKENS)
    assert pcr.REDACTED in review["comments"][0]["body"]
    assert "3 credential-shaped string(s) were redacted" in review["body"]
    assert "::warning::Redacted 3" in capsys.readouterr().out


def test_a_credential_in_the_structured_fields_is_redacted_too(capsys):
    token = TOKENS[1]
    findings = json.dumps({"overview": token, "changes": [token], "files": [{"path": f"docs/{token}", "description": token}],
                           "advisory": [token], "not_reviewed": token, "findings": []})

    _, review = run(findings).posted[0]

    assert token not in review["body"] and review["body"].count(pcr.REDACTED) == 6
    assert "6 credential-shaped string(s) were redacted" in review["body"]


def test_the_review_jobs_redactions_are_counted(monkeypatch, capsys):
    monkeypatch.setenv("REDACTED", "2")
    _, review = run(findings_json(("src/app.py", 2, "Was [redacted]."))).posted[0]

    assert "2 credential-shaped string(s) were redacted" in review["body"]
    assert "::warning::Redacted 2" in capsys.readouterr().out


@pytest.mark.parametrize("text", [
    "ghs_installation_token_value_for_the_runner_job",
    "github_pat_example_placeholder_value_goes_here_xx",
    "ghp_short",
])
def test_names_shaped_loosely_like_tokens_arent_redacted(text):
    _, review = run(findings_json(("src/app.py", 2, text))).posted[0]

    assert review["comments"][0]["body"] == text


def test_ordinary_text_isnt_redacted():
    text = "The ghp_ prefix, sk-ant- keys and a JWT header like eyJ are named here, not quoted."
    _, review = run(findings_json(("src/app.py", 2, text))).posted[0]

    assert review["comments"][0]["body"] == text


def test_a_failed_post_fails_the_step():
    class FailingGitHub(FakeGitHub):
        def create_review(self, number, review):
            raise subprocess.CalledProcessError(1, ["gh", "api"])

    with pytest.raises(subprocess.CalledProcessError):
        run(findings_json(("README.md", 1, "Off.")), FailingGitHub())


def test_findings_on_the_diff_only_pass():
    gh = run(findings_json(("src/app.py", 2, "On.")))

    assert "hold the merge" not in gh.posted[0][1]["body"]
    assert pcr.OFF_DIFF_MARKER not in gh.posted[0][1]["body"]


def test_no_findings_says_so():
    _, review = run(findings_json()).posted[0]

    assert review["comments"] == []
    assert review["body"].split("\n")[1] == pcr.NO_CHANGES_RECOMMENDED == "### 🟢 No changes recommended"
    assert "open issue" not in review["body"]


# The body's layout


def structured(*findings, **report):
    """FINDINGS in the structured shape: findings are (path, line, body, severity or None)."""
    report.setdefault("overview", "An overview.")
    return json.dumps({**report, "findings": [{"path": p, "line": n, "body": b, **({"severity": s} if s else {})}
                                              for p, n, b, s in findings]})


def sections(body):
    """The body's parts, as the blank lines between them split it (a <details> block is one part)."""
    parts, current, depth = [], [], 0
    for line in body.split("\n"):
        depth += line.count("<details") - line.count("</details>")
        if line == "" and depth == 0 and current:
            parts.append("\n".join(current))
            current = []
        elif line != "" or depth:
            current.append(line)
    return parts + (["\n".join(current)] if current else [])


def test_a_string_summary_renders_the_new_layout_with_the_summary_folded_as_it_is():
    summary = "**Overview**\n\nAdds a cache.\n\n**Advisory (not blocking)**\n\n- A doc nit."
    gh = run(findings_json(("src/app.py", 2, "**Off by one**\n\nThe loop stops early."), summary=summary))

    _, review = gh.posted[0]
    lines = review["body"].split("\n")
    assert lines[0] == pcr.MARKER and lines[1] == "### 🟡 Changes recommended"
    assert lines[3] == "Off by one"
    assert "<details open><summary><b>1 open issue</b></summary>\n\n- Off by one\n\n</details>" in review["body"]
    assert (f"<details><summary><b>What changed in this PR</b></summary>\n\n{summary}\n\n</details>"
            in review["body"])
    assert lines[-1] == "<sub>Claude Review</sub>"
    assert "**Claude Review**" not in review["body"]


def test_the_structured_input_renders_each_section():
    findings = structured(
        ("src/app.py", 2, "**Cache init can delete another run's cache**\n\nDetail.", "high"),
        ("src/app.py", 22, "**Child outlives the command**\n\nDetail.", "medium"),
        overview="Adds a shared cache.",
        changes=["Adds `cache.sh`.", "Wires it into CI."],
        files=[{"path": "src/app.py", "description": "The cache | its setup"}],
        advisory=["A comment is stale."],
        not_reviewed="The vendored code.",
    )

    gh = FakeGitHub()
    pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD, "--effort", "high (claude-sonnet-5-5), 2 batches"],
             gh=gh, findings=findings)

    first = gh.posted[0][1]["body"]
    assert sections(first) == [
        f"{pcr.MARKER}\n### 🟡 Changes recommended",
        "Cache init can delete another run's cache",
        "<details open><summary><b>2 open issues</b></summary>\n\n"
        "- ![High](https://img.shields.io/badge/High-d1242f?style=flat-square) Cache init can delete another run's cache\n"
        "- ![Medium](https://img.shields.io/badge/Medium-bf8700?style=flat-square) Child outlives the command\n\n"
        "</details>",
        "<details><summary><b>What changed in this PR</b></summary>\n\nAdds a shared cache.\n\n**Changes:**\n\n"
        "- Adds `cache.sh`.\n- Wires it into CI.\n\n| File | Description |\n| --- | --- |\n"
        "| `src/app.py` | The cache \\| its setup |\n\n</details>",
        "**Advisory (not blocking)**",
        "- A comment is stale.",
        "**Not reviewed**",
        "The vendored code.",
        "<sub>Claude Review · Review effort: high (claude-sonnet-5-5), 2 batches</sub>",
    ]


def test_a_section_left_out_of_the_input_is_left_out_of_the_body():
    _, review = run(structured(("src/app.py", 2, "**T**", None), overview="Only an overview.")).posted[0]

    body = review["body"]
    assert "Only an overview." in body
    for absent in ("**Changes:**", "| File |", "**Advisory", "**Not reviewed**", "Findings outside the diff"):
        assert absent not in body


@pytest.mark.parametrize("effort", [None, "", "  "])
def test_without_an_effort_the_footer_says_only_claude_review(effort):
    argv = ["--repo", "octo/demo", "--pr", "7", "--head", HEAD] + ([] if effort is None else ["--effort", effort])
    gh = FakeGitHub()
    pcr.main(argv, gh=gh, findings=findings_json())

    assert gh.posted[0][1]["body"].endswith("\n\n<sub>Claude Review</sub>")


def test_the_effort_is_kept_on_one_line_and_cant_open_a_tag():
    gh = FakeGitHub()
    pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD, "--effort", "high\n- <b>x</b>"], gh=gh, findings=findings_json())

    assert gh.posted[0][1]["body"].endswith("\n\n<sub>Claude Review · Review effort: high - &lt;b&gt;x&lt;/b&gt;</sub>")


@pytest.mark.parametrize(("severities", "top"), [
    ((None, "low", "medium"), "two"),
    (("low", "high", "high"), "one"),
    ((None, None, None), "zero"),
    (("medium", "medium", "low"), "zero"),
])
def test_the_top_finding_is_the_most_severe_first_on_a_tie(severities, top):
    names = ("zero", "one", "two")
    findings = structured(*[("src/app.py", 2, f"**{name}**\n\nBody.", s) for name, s in zip(names, severities, strict=True)])

    _, review = run(findings).posted[0]

    assert sections(review["body"])[1] == top


def test_open_issues_sort_high_medium_low_then_unrated_keeping_claudes_order_within_each():
    order = [("a", None), ("b", "low"), ("c", "high"), ("d", "medium"), ("e", "high"), ("f", None), ("g", "low")]
    findings = structured(*[("src/app.py", 2, f"**{name}**", s) for name, s in order])

    _, review = run(findings).posted[0]

    listed = [line.rsplit(" ", 1)[-1].strip("[]").split("](")[0] for line in review["body"].split("\n")
              if line.startswith("- ")]
    assert listed == ["c", "e", "d", "b", "g", "a", "f"]


def test_a_finding_without_a_severity_has_no_badge():
    _, review = run(structured(("src/app.py", 2, "**Plain**", None))).posted[0]

    assert "- Plain\n" in review["body"] and "img.shields.io" not in review["body"]


@pytest.mark.parametrize(("body", "title"), [
    ("**Bold title.** Then the rest.\n\nMore.", "Bold title."),
    ("No bold title here.\n\nSecond paragraph.", "No bold title here."),
    ("\n  Leading blank line, **not bold at the start**", "Leading blank line, **not bold at the start**"),
    ("**  Spaced   title  **", "Spaced title"),
])
def test_a_findings_title_is_its_bold_first_line_or_else_its_first_line(body, title):
    assert pcr.title_of(body) == title


def test_a_long_title_is_cut_with_an_ellipsis():
    title = pcr.title_of("**" + "word " * 100 + "**")

    assert len(title) <= pcr.MAX_TITLE and title.endswith("…")


def test_the_markers_and_the_off_diff_section_are_as_before_and_last_with_bullets():
    findings = structured(("src/app.py", 2, "**On**\n\nText.", "high"), ("README.md", 1, "**Off one**\n\nLine two.", "low"),
                          ("src/app.py", 10, "Off two", None), overview="O.", changes=["c"], advisory=["a"],
                          not_reviewed="n")

    _, review = run(findings).posted[0]

    lines = review["body"].split("\n")
    assert lines[0] == pcr.MARKER and lines[1] == pcr.OFF_DIFF_MARKER
    header = lines.index("Findings outside the diff (no thread to resolve, so they hold the merge until the next push "
                         "gets a fresh review):")
    after = lines[header + 1:]
    assert [line for line in after if line.startswith("- ")] == ["- `README.md:1`: **Off one**", "- `src/app.py:10`: Off two"]
    assert "  Line two." in after and after[-1].startswith("<sub>")


def test_the_thread_links_are_added_by_editing_the_posted_review():
    gh = run(structured(("src/app.py", 2, "**Low one**", "low"), ("src/app.py", 22, "**High [one]**", "high"),
                        ("README.md", 1, "Off.", None)))

    first = gh.posted[0][1]["body"]
    assert "discussion_r" not in first
    assert len(gh.updates) == 1
    linked = gh.updates[0]
    assert "- ![High](https://img.shields.io/badge/High-d1242f?style=flat-square) " \
           "[High \\[one\\]](https://github.com/octo/demo/pull/7#discussion_r2)" in linked
    assert "- ![Low](https://img.shields.io/badge/Low-0969da?style=flat-square) " \
           "[Low one](https://github.com/octo/demo/pull/7#discussion_r1)" in linked
    # PR Auto-Merge reads the same markers and off-diff section before and after the edit.
    assert linked.split("\n")[:2] == first.split("\n")[:2]
    assert linked[linked.index("Findings outside"):] == first[first.index("Findings outside"):]


def test_no_edit_when_no_thread_was_opened():
    gh = run(findings_json(("README.md", 1, "Off.")))

    assert gh.updates == []


@pytest.mark.parametrize("fail", ["read", "update"])
def test_a_failed_link_read_or_update_warns_and_keeps_the_first_body(fail, capsys):
    gh = FakeGitHub(fail=fail)
    review = pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD], gh=gh,
                      findings=findings_json(("src/app.py", 2, "**Title**")))

    assert gh.updates == [] and review["body"] == gh.posted[0][1]["body"]
    assert "- Title\n" in review["body"]
    assert "::warning::Couldn't link the review's threads" in capsys.readouterr().out


def test_a_post_that_returns_no_id_warns_and_keeps_the_first_body(capsys):
    class NoId(FakeGitHub):
        def create_review(self, number, review):
            super().create_review(number, review)
            return {}

    gh = NoId()
    run(findings_json(("src/app.py", 2, "**Title**")), gh)

    assert gh.updates == [] and "::warning::Couldn't link" in capsys.readouterr().out


def test_thread_links_match_by_path_and_body_and_fall_back_to_order():
    comments = [{"path": "a", "body": "x"}, {"path": "a", "body": "y"}]

    assert pcr.thread_links(comments, [{"path": "a", "body": "y", "html_url": "https://g/2"},
                                       {"path": "a", "body": "x", "html_url": "https://g/1"}]) == ["https://g/1", "https://g/2"]
    # GitHub changed a body: it takes the URL at its place.
    assert pcr.thread_links(comments, [{"path": "a", "body": "x\r\n", "html_url": "https://g/1"},
                                       {"path": "a", "body": "y", "html_url": "https://g/2"}]) == ["https://g/1", "https://g/2"]
    # A different count, or a URL that isn't https, links nothing it can't match.
    assert pcr.thread_links(comments, [{"path": "a", "body": "q", "html_url": "https://g/1"}]) == [None, None]
    assert pcr.thread_links(comments, [{"path": "a", "body": "x", "html_url": "javascript:x"},
                                       {"path": "a", "body": "y"}]) == [None, None]


def longest_body(tmp_path, threads):
    """The longest report the workflow's batch join can produce, and the findings in the review that hold it."""
    answers = [{"overview": "z" * 100_000, "changes": ["c" * 3_000] * 3, "files": [{"path": "f" * 100, "description": "d" * 3_000}] * 3,
                "advisory": ["a" * 3_000] * 3, "not_reviewed": "n" * 100_000, "findings": []} for _ in range(4)]
    listed = ["001.diff", "002.diff", "003.diff", "004.diff"]
    reads = {n: read_whole_of(tmp_path, name, f"r{n}") for n, name in enumerate(listed, 1)}
    _, answer, _ = batched(tmp_path, answers, listed, reads)
    return json.dumps({**{key: value for key, value in answer.items() if key != "findings"},
                       "findings": [{"path": "src/app.py", "line": 2, "body": f"**{'t' * 300}**", "severity": "high"}
                                    for _ in range(threads)] +
                                   [{"path": "README.md", "line": 1, "body": "Off the diff, whole."}]})


def test_the_longest_joined_summary_fits_githubs_limit_with_the_wrappers_and_links(tmp_path):
    gh = run(longest_body(tmp_path, 20))

    first, linked = gh.posted[0][1]["body"], gh.updates[0]
    assert len(first) <= pcr.MAX_BODY and len(linked) <= pcr.MAX_BODY
    assert "What changed in this PR" in linked and pcr.WHAT_CHANGED_CUT not in linked


def test_a_body_past_githubs_limit_cuts_what_changed_and_keeps_the_markers_and_off_diff(tmp_path):
    gh = run(longest_body(tmp_path, 120))

    for body in (gh.posted[0][1]["body"], gh.updates[0]):
        assert len(body) <= pcr.MAX_BODY
        assert body.startswith(f"{pcr.MARKER}\n{pcr.OFF_DIFF_MARKER}\n")
        assert pcr.WHAT_CHANGED_CUT in body and "zzzz" not in body
        assert "- `README.md:1`: Off the diff, whole." in body
        assert "120 open issues" in body


def test_open_issues_past_the_limit_are_left_out_with_a_note():
    findings = [{"path": "src/app.py", "line": 2, "body": f"**{'t' * 199}**", "severity": "high"} for _ in range(400)]
    _, review = run(json.dumps({"summary": "s" * 1000, "findings": findings})).posted[0]

    assert len(review["body"]) <= pcr.MAX_BODY and pcr.WHAT_CHANGED_CUT in review["body"]
    assert re.search(r"_\d+ more in the threads below, left out to fit GitHub's limit\._", review["body"])
    assert "400 open issues" in review["body"]


@pytest.mark.parametrize("extras", [{"not_reviewed": "n" * 70_000}, {"advisory": ["a" * 1000] * 70}])
def test_long_advisory_or_not_reviewed_is_left_out_to_fit_and_keeps_the_off_diff(extras):
    answer = {"overview": "Short.", "findings": [{"path": "README.md", "line": 1, "body": "Off the diff, whole."}]} | extras
    _, review = run(json.dumps(answer)).posted[0]

    assert len(review["body"]) <= pcr.MAX_BODY
    assert pcr.EXTRAS_CUT in review["body"] and pcr.WHAT_CHANGED_CUT in review["body"]
    assert review["body"].startswith(f"{pcr.MARKER}\n{pcr.OFF_DIFF_MARKER}\n")
    assert "- `README.md:1`: Off the diff, whole." in review["body"]


def test_short_advisory_and_not_reviewed_are_kept():
    answer = {"overview": "Short.", "advisory": ["Rename it."], "not_reviewed": "The lock file.", "findings": []}
    _, review = run(json.dumps(answer)).posted[0]

    assert pcr.EXTRAS_CUT not in review["body"]
    assert "- Rename it." in review["body"] and "The lock file." in review["body"]


def test_a_long_effort_is_capped_in_the_footer():
    gh = FakeGitHub()
    pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD, "--effort", "e" * 70_000], gh=gh, findings=findings_json())

    body = gh.posted[0][1]["body"]
    assert len(body) <= pcr.MAX_BODY
    assert body.endswith(f"<sub>Claude Review · Review effort: {'e' * (pcr.MAX_EFFORT - 1)}…</sub>")


def test_a_files_entry_keeps_only_path_and_description():
    answer = {"overview": "o", "files": [{"path": "src/a.py", "description": "d", "additions": 1}], "findings": []}

    report, _ = pcr.parse_findings(json.dumps(answer))
    _, review = run(json.dumps(answer)).posted[0]

    assert report["files"] == [{"path": "src/a.py", "description": "d"}]
    assert "`src/a.py`" in review["body"]


@pytest.mark.parametrize(
    "findings",
    [
        "",
        "not json",
        json.dumps({"findings": []}),
        json.dumps({"summary": "s", "findings": [{"path": "a.py", "body": "no line"}]}),
        json.dumps({"summary": "s", "findings": [{"path": "a.py", "line": "2", "body": "b"}]}),
        json.dumps({"summary": "s", "findings": "none"}),
        json.dumps({"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": "critical"}]}),
        json.dumps({"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": "High"}]}),
        json.dumps({"summary": "s", "findings": [{"path": "a.py", "line": 2, "body": "b", "severity": None}]}),
        json.dumps({"summary": "s", "changes": "one change", "findings": []}),
        json.dumps({"summary": "s", "changes": ["ok", 3], "findings": []}),
        json.dumps({"summary": "s", "files": [{"path": "a.py"}], "findings": []}),
        json.dumps({"summary": "s", "files": {"path": "a.py", "description": "d"}, "findings": []}),
        json.dumps({"summary": "s", "files": [{"path": 1, "description": "d"}], "findings": []}),
        json.dumps({"summary": "s", "advisory": "a", "findings": []}),
        json.dumps({"summary": "s", "not_reviewed": ["n"], "findings": []}),
        json.dumps({"overview": 3, "findings": []}),
        json.dumps({"summary": 3, "overview": "o", "findings": []}),
    ],
)
def test_malformed_findings_fail_with_a_clear_message_and_post_nothing(findings, capsys):
    gh = FakeGitHub()

    with pytest.raises(SystemExit) as exit_info:
        pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD], gh=gh, findings=findings)

    assert exit_info.value.code == 1
    assert "Claude Review's findings" in capsys.readouterr().err
    assert gh.posted == []


@pytest.mark.parametrize("findings", ["", "  \n"])
def test_empty_findings_say_a_secret_was_withheld_and_post_nothing(findings, capsys):
    # The post job runs only once Claude has answered, so empty findings mean
    # the answer quoted a secret and was withheld.
    gh = FakeGitHub()

    with pytest.raises(SystemExit) as exit_info:
        pcr.main(["--repo", "octo/demo", "--pr", "7", "--head", HEAD], gh=gh, findings=findings)

    assert exit_info.value.code == 1
    out = capsys.readouterr().out
    assert out.startswith("::error::") and "quoted a secret" in out and "rotate CLAUDE_CODE_OAUTH_TOKEN" in out
    assert gh.posted == []


# The answer check, inline in claude-review.yml's review job

WORKFLOW = Path(__file__).resolve().parents[2] / "workflows" / "claude-review.yml"


def answer_check_script():
    """The Python the "Check Claude's answer for secrets" step runs, from its heredoc."""
    lines = WORKFLOW.read_text().splitlines()
    step = next(i for i, line in enumerate(lines) if line.strip() == "id: answer")
    start = next(i for i in range(step, len(lines)) if lines[i].strip() == "python3 -I - <<'PY'") + 1
    end = next(i for i in range(start, len(lines)) if lines[i].strip() == "PY")
    return textwrap.dedent("\n".join(lines[start:end])) + "\n"


def check(tmp_path, answer=None, secrets=(), path=None, raw=None, messages=(), extra=None):
    """Run the step's check: its exit code, its step outputs and what it printed.

    messages go in the execution file between the first two and the result;
    extra adds to the step's environment.
    """
    if path is None:
        path = tmp_path / "execution.json"
        if raw is not None:
            path.write_text(raw)
        else:
            path.write_text(json.dumps([{"type": "system"}, {"type": "assistant"}, *messages,
                                        {"type": "result", "subtype": "success", "structured_output": answer}]))
    output = tmp_path / "output"
    output.write_text("")
    env = {"PATH": os.environ["PATH"], "GITHUB_OUTPUT": str(output), "EXECUTION_FILE_1": str(path), "ANSWERED_1": "true",
           "OAUTH_TOKEN": secrets[0] if secrets else "", "JOB_TOKEN": secrets[1] if len(secrets) > 1 else "",
           **(extra or {})}
    done = subprocess.run([sys.executable, "-I", "-"], input=answer_check_script(), env=env,
                          capture_output=True, text=True)
    return done.returncode, parse_outputs(output.read_text()), done.stdout


def parse_outputs(text):
    outputs, lines = {}, iter(text.splitlines())
    for line in lines:
        if "<<" in line:
            name, delimiter = line.split("<<", 1)
            outputs[name] = "\n".join(iter(lambda: next(lines), delimiter))
        else:
            name, value = line.split("=", 1)
            outputs[name] = value
    return outputs


# No character repeats in either secret part, so a 16-character window
# matches at one offset only, and a check that compared only some windows
# would fail these tests.
ALPHANUMERIC = string.ascii_letters + string.digits
SECRET = "sk-ant-" + "oat01-" + "".join(random.Random(7).sample(ALPHANUMERIC, 62))
JOB_TOKEN = ("ghs_" + "98765_" + "eyJ" + "hbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9" + "." + "eyJ" + "pc3MiOiJnaXRodWIifQ" + "."
             + "".join(random.Random(11).sample(ALPHANUMERIC, 45)))


def withheld(tmp_path, answer, secrets):
    code, outputs, out = check(tmp_path, answer, secrets)
    assert code == 0
    if outputs["findings"] == "":
        assert out.startswith("::error::Claude's answer quotes")
        return True
    return False


def test_the_workflows_credential_pattern_matches_the_scripts():
    script = answer_check_script()
    start = script.index("CREDENTIAL = re.compile(")
    namespace = {"re": re}
    exec(script[start:script.index("\n)\n", start) + 3], namespace)

    assert namespace["CREDENTIAL"].pattern == pcr.CREDENTIAL.pattern


def test_an_answer_passes_on_with_credentials_redacted(tmp_path):
    answer = {"summary": "Uses " + TOKENS[1], "findings": [{"path": "a.py", "line": 1, "body": "See " + JWT}]}

    code, outputs, out = check(tmp_path, answer, [SECRET])

    assert code == 0 and outputs["redacted"] == "2" and "Redacted 2" in out
    assert json.loads(outputs["findings"])["findings"][0]["body"] == "See " + pcr.REDACTED
    assert TOKENS[1] not in outputs["findings"] and JWT not in outputs["findings"]


def test_only_the_schemas_fields_are_passed_on(tmp_path):
    answer = {"summary": "s", "debug": [115, 107], TOKENS[1]: "x",
              "findings": [{"path": "a.py", "line": 3, "body": "b", "extra": "e"}]}

    _, outputs, _ = check(tmp_path, answer, [SECRET])

    assert json.loads(outputs["findings"]) == {"summary": "s", "findings": [{"path": "a.py", "line": 3, "body": "b"}]}
    assert outputs["redacted"] == "0"


def test_the_structured_fields_and_severity_are_passed_on_and_nothing_else(tmp_path):
    answer = {**GOOD_ANSWER, "files": [{"path": "a.py", "description": "d", "extra": [115, 107]}],
              "findings": [{**GOOD_ANSWER["findings"][0], "extra": "e"}]}

    _, outputs, _ = check(tmp_path, answer, [SECRET])

    assert json.loads(outputs["findings"]) == GOOD_ANSWER


def test_a_credential_in_any_structured_field_is_redacted(tmp_path):
    answer = {"overview": "o " + TOKENS[1], "changes": ["Uses " + JWT], "advisory": ["a " + TOKENS[1]],
              "not_reviewed": "n " + JWT, "files": [{"path": "src/" + TOKENS[1], "description": "Holds " + TOKENS[1]}],
              "findings": [{"path": "a.py", "line": 1, "severity": "low", "body": "b"}]}

    code, outputs, _ = check(tmp_path, answer, [SECRET])

    posted = json.loads(outputs["findings"])
    assert code == 0 and outputs["redacted"] == "6"
    assert TOKENS[1] not in outputs["findings"] and JWT not in outputs["findings"]
    assert posted["changes"] == ["Uses " + pcr.REDACTED] and posted["files"][0]["description"] == "Holds " + pcr.REDACTED
    assert posted["files"][0]["path"] == "src/" + pcr.REDACTED


@pytest.mark.parametrize("field, leak", [
    ("overview", "o " + SECRET), ("changes", ["c " + SECRET]), ("advisory", ["a " + SECRET]),
    ("not_reviewed", "n " + SECRET), ("files", [{"path": "a.py", "description": SECRET}]),
    ("files", [{"path": "src/" + SECRET, "description": "d"}]),
], ids=["overview", "changes", "advisory", "not_reviewed", "files-description", "files-path"])
def test_a_secret_in_a_structured_field_withholds_the_answer(tmp_path, field, leak):
    assert withheld(tmp_path, {**GOOD_ANSWER, field: leak}, [SECRET])


@pytest.mark.parametrize("answer", [
    {"summary": "s", "findings": "none"},
    {"summary": "s", "findings": ["not a finding"]},
    {"summary": "s"},
    {"findings": []},
    {"summary": "s", "findings": [{"path": "a.py", "body": "no line"}]},
    {"summary": "s", "findings": [{"path": "a.py", "line": True, "body": "b"}]},
    {"summary": "s", "findings": [{"path": "a.py", "line": int.from_bytes(SECRET.encode(), "big"), "body": "b"}]},
    {"summary": "s", "findings": [{"path": "a.py", "line": -1, "body": "b"}]},
    {"summary": "s", "findings": [[115, 107, 45]]},
    {"summary": "s", "findings": [{"path": "a", "line": [115, 107], "body": "b"}]},
    {"summary": {TOKENS[1]: [115, 107]}, "findings": []},
    [{TOKENS[1]: "x"}],
    {"overview": [115, 107], "findings": []},
    {"summary": "s", "overview": [115, 107], "findings": []},
    {"overview": "o", "summary": 5, "findings": []},
    {"overview": "o", "files": ["a.py"], "findings": []},
    {"overview": "o", "files": [{"path": 1, "description": "d"}], "findings": []},
    {"overview": "o", "not_reviewed": [115, 107], "findings": []},
    {"overview": "o", "changes": "c", "findings": []},
    {"overview": "o", "changes": [115, 107], "findings": []},
    {"overview": "o", "advisory": [{TOKENS[1]: 1}], "findings": []},
    {"overview": "o", "files": [{"path": "a.py"}], "findings": []},
    {"overview": "o", "files": [{"path": "a.py", "description": [115, 107]}], "findings": []},
    {"overview": "o", "files": {"a.py": "d"}, "findings": []},
    {"overview": "o", "findings": [{"path": "a.py", "line": 1, "severity": "critical", "body": "b"}]},
    {"overview": "o", "findings": [{"path": "a.py", "line": 1, "severity": [115, 107], "body": "b"}]},
])
def test_a_malformed_answer_becomes_a_placeholder_the_post_script_rejects(tmp_path, answer):
    _, outputs, out = check(tmp_path, answer, [SECRET])

    placeholder = json.loads(outputs["findings"])
    assert list(placeholder) == ["malformed"] and placeholder["malformed"].startswith("Claude's answer was withheld: ")
    reason = placeholder["malformed"].removeprefix("Claude's answer was withheld: ")
    assert f"::error::Claude's answer doesn't fit the schema ({reason}), so it is withheld" in out
    assert "115" not in out and TOKENS[1] not in out
    assert "115" not in outputs["findings"] and TOKENS[1] not in outputs["findings"] and outputs["redacted"] == "0"
    with pytest.raises(pcr.MalformedFindings):
        pcr.parse_findings(outputs["findings"])


def test_a_secret_spelled_out_in_line_numbers_is_withheld(tmp_path):
    findings = [{"path": "a.py", "line": ord(c), "body": "b"} for c in SECRET[13:33]]

    assert withheld(tmp_path, {"summary": "s", "findings": findings}, [SECRET])


# The whole secret, its body's first window, a middle one and its last one.
@pytest.mark.parametrize("quote", [SECRET, SECRET[13:29], "x" + SECRET[40:56] + "y", SECRET[-16:]])
def test_an_answer_quoting_a_secret_even_in_part_is_withheld(tmp_path, quote):
    answer = {"summary": "s", "findings": [{"path": "a.py", "line": 1, "body": f"The token is {quote}."}]}

    assert withheld(tmp_path, answer, [SECRET])


# Accented letters too, which NFKD would split into a base letter between the
# pieces and a mark; the NFKC comparison strips them whole.
@pytest.mark.parametrize("separator", ["`", "\n", "` `", "-", "\t", "é", "ạ"])
def test_a_secret_quoted_in_short_pieces_is_withheld(tmp_path, separator):
    body = SECRET[13:]
    pieces = separator.join(body[i:i + 15] for i in range(0, len(body), 15))

    assert withheld(tmp_path, {"summary": pieces, "findings": []}, [SECRET])


@pytest.mark.parametrize("offset", [0xFEE0, None])
def test_a_secret_in_look_alike_characters_is_withheld(tmp_path, offset):
    # Fullwidth, or mathematical bold: both normalise to ASCII under NFKC and NFKD.
    def look_alike(c):
        if offset:
            return chr(ord(c) + offset)
        return chr((0x1D400 + ord(c) - 65) if c.isupper() else (0x1D41A + ord(c) - 97) if c.islower()
                   else (0x1D7CE + ord(c) - 48))

    assert withheld(tmp_path, {"summary": "".join(map(look_alike, SECRET[13:33])), "findings": []}, [SECRET])


# Each letter followed by a dot below: as written, and composed into one
# character where Unicode has one. NFKC composes both into non-ASCII letters
# and strips them; the NFKD comparison strips only the marks.
@pytest.mark.parametrize("form", [None, "NFC"])
def test_a_secret_with_a_mark_on_each_letter_is_withheld(tmp_path, form):
    quote = "".join(c + "\u0323" for c in SECRET[13:33])
    if form:
        quote = unicodedata.normalize(form, quote)
        assert any(ord(c) > 127 and not unicodedata.combining(c) for c in quote)

    assert withheld(tmp_path, {"summary": quote, "findings": []}, [SECRET])


def test_a_secret_in_an_extra_field_is_withheld_too(tmp_path):
    assert withheld(tmp_path, {"summary": "s", "findings": [], SECRET[20:40]: 1}, [SECRET])


# Six of the accepted limits in ADR 0004 of the repo-ci-baseline skill (mpaulosky/Application_Factory), pinned so a
# change to them is deliberate. Pieces are joined by a character of the body
# that isn't next to them in it, so no window across a join is a window of
# the secret, whatever order the seed gives.
BODY = SECRET[13:]
SEPARATED = "".join(BODY[j:j + 15] + BODY[j + 20] for j in range(0, 45, 15))


@pytest.mark.parametrize("answer", [
    # Pieces split by letters or digits.
    {"summary": SEPARATED, "findings": []},
    # Pieces in separate fields, with the keys' letters between them.
    {"summary": BODY[:15], "findings": [{"path": "a.py", "line": 1, "body": BODY[15:30]}]},
    # Character codes written as digits.
    {"summary": " ".join(str(ord(c)) for c in BODY[:20]), "findings": []},
    # A reversed quote.
    {"summary": BODY[::-1], "findings": []},
    # Both Unicode tricks at once: a mark on each letter, and pieces joined by
    # an accented letter, built from a body character that isn't next to them.
    {"summary": "".join("".join(c + "\u0323" for c in BODY[j:j + 15]) + BODY[j + 20] + "\u0301"
                        for j in range(0, 45, 15)), "findings": []},
    # Pieces joined by a fullwidth letter, which both forms map to ASCII.
    {"summary": "".join(BODY[j:j + 15] + chr(ord(BODY[j + 20]) + 0xFEE0) for j in range(0, 45, 15)), "findings": []},
])
def test_the_accepted_limits_get_through(tmp_path, answer):
    # The keys between the separate fields start "findings" and end "body";
    # a seed where they'd continue the body would test nothing.
    assert BODY[15] != "f" and BODY[14] != "y", "pick another seed for SECRET"
    assert not withheld(tmp_path, answer, [SECRET])


def test_a_short_overlap_with_a_secret_isnt_a_leak(tmp_path):
    assert not withheld(tmp_path, {"summary": "Mentions " + SECRET[:15], "findings": []}, [SECRET])


def test_a_secret_stored_with_its_name_isnt_leaked_by_naming_it(tmp_path):
    stored = "CLAUDE_CODE_OAUTH_TOKEN=" + SECRET + "\n"
    answer = {"summary": "Rotate CLAUDE_CODE_OAUTH_TOKEN; it starts sk-ant-oat01-.", "findings": []}

    assert not withheld(tmp_path, answer, [stored])
    assert withheld(tmp_path, {"summary": SECRET[20:40], "findings": []}, [stored])


def test_a_job_tokens_jwt_header_and_claims_arent_a_leak_but_its_signature_is(tmp_path):
    header, claims, signature = JOB_TOKEN.split("_", 2)[2].split(".")
    example = header + "." + "eyJ" + "zdWIiOiJleGFtcGxlIn0" + "." + "Qw8" * 12

    assert not withheld(tmp_path, {"summary": f"A JWT starts {header}.{claims}, e.g. {example}", "findings": []},
                        [SECRET, JOB_TOKEN])
    assert withheld(tmp_path, {"summary": signature[:16], "findings": []}, [SECRET, JOB_TOKEN])


def test_a_secret_without_a_token_shape_is_checked_whole(tmp_path):
    assert withheld(tmp_path, {"summary": "x plain-secret-value y", "findings": []}, ["  plain-secret-value-1234 \n"])


def test_a_withheld_answer_writes_empty_findings_and_no_secret(tmp_path):
    code, outputs, out = check(tmp_path, {"summary": "Leaks " + SECRET, "findings": []}, [SECRET])

    assert code == 0 and outputs == {"findings": "", "redacted": "0"}
    assert SECRET not in out and "rotate CLAUDE_CODE_OAUTH_TOKEN" in out


@pytest.mark.parametrize("raw", ["not json", "[]", '[{"type": "result", "subtype": "error"}]'])
def test_no_answer_in_the_execution_file_fails_the_step(tmp_path, raw):
    code, outputs, out = check(tmp_path, raw=raw)

    assert code == 1 and outputs["findings"] == ""
    assert out.startswith("::error::Claude answered batch 1, but its answer isn't in the action's execution file")
    assert "rotate" not in out


def test_a_missing_execution_file_fails_the_step(tmp_path):
    code, _, out = check(tmp_path, path=tmp_path / "missing.json")

    assert code == 1 and "execution file" in out


def test_an_unset_execution_file_fails_the_step(tmp_path):
    # A later action pin that drops the execution_file output.
    code, outputs, out = check(tmp_path, path="")

    assert code == 1 and outputs["findings"] == "" and "(not set)" in out


def test_posting_needs_repo_pr_and_head():
    with pytest.raises(SystemExit) as exit_info:
        pcr.main(["--repo", "octo/demo"], gh=FakeGitHub(), findings=findings_json())

    assert exit_info.value.code == 2


# The post job's gate, which picks the post_claude_review.py the merge
# commit's first parent (the base) holds, never the PR's own copy.

def post_step_script():
    """The bash the "Post the review on the head commit" step runs, from its run: block."""
    lines = WORKFLOW.read_text().splitlines()
    step = next(i for i, line in enumerate(lines) if line.strip() == "- name: Post the review on the head commit")
    run = next(i for i in range(step, len(lines)) if lines[i].strip() == "run: |")
    indent = len(lines[run]) - len(lines[run].lstrip())
    end = next((i for i in range(run + 1, len(lines))
                if lines[i].strip() and len(lines[i]) - len(lines[i].lstrip()) <= indent), len(lines))
    return textwrap.dedent("\n".join(lines[run + 1:end])) + "\n"


def stub_script(name, effort=True):
    """A post_claude_review.py that only says which copy ran, and with what; effort=False is one from before --effort."""
    accepts = "# Takes --effort.\n" if effort else ""
    return f'{accepts}import sys\nprint("{name} copy ran with", " ".join(sys.argv[1:]))\n'


def git(repo, *args):
    env = {"PATH": os.environ["PATH"], "HOME": str(repo), "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
           "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
           "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"}
    subprocess.run(["git", "-C", str(repo), *args], env=env, check=True, capture_output=True)


def commit_script(repo, content, message):
    script = repo / ".github" / "scripts" / "post_claude_review.py"
    if content is None:
        (repo / "README").write_text(message)
    else:
        script.parent.mkdir(parents=True, exist_ok=True)
        script.write_text(content)
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", message)


def merge_repo(tmp_path, base, pr):
    """A repo whose HEAD merges a PR commit (holding pr) into a base commit (holding base); None is no script."""
    repo = tmp_path / "repo"
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    commit_script(repo, base, "base")
    git(repo, "checkout", "-q", "-b", "pr")
    commit_script(repo, pr, "pr")
    git(repo, "checkout", "-q", "main")
    git(repo, "merge", "-q", "--no-ff", "-m", "merge", "pr")
    return repo


def post_step_env():
    yaml = pytest.importorskip("yaml")
    steps = yaml.safe_load(WORKFLOW.read_text())["jobs"]["post"]["steps"]
    return next(step for step in steps if step["name"] == "Post the review on the head commit")["env"]


def run_post_step(repo, tmp_path, findings="{}", batches=None):
    runner_temp = tmp_path / "runner-temp"
    runner_temp.mkdir()
    env = {"PATH": os.environ["PATH"], "HOME": str(tmp_path), "RUNNER_TEMP": str(runner_temp),
           "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull, "FINDINGS": findings, "REDACTED": "0",
           "REPOSITORY": "octo/demo", "PR_NUMBER": "7", "HEAD_SHA": HEAD, "REVIEW_EFFORT": "high (claude-sonnet-5-5)",
           **({"BATCHES": batches} if batches is not None else {})}
    return subprocess.run(["bash", "-e", "-c", post_step_script()], cwd=repo, env=env, capture_output=True, text=True)


def test_the_post_step_runs_the_bases_script_not_the_prs(tmp_path):
    repo = merge_repo(tmp_path, stub_script("base"), stub_script("pr"))

    done = run_post_step(repo, tmp_path)

    assert done.returncode == 0, done.stderr
    assert done.stdout.strip() == (f"base copy ran with --repo octo/demo --pr 7 --head {HEAD} "
                                   "--effort high (claude-sonnet-5-5), 1 batch")


@pytest.mark.parametrize("batches, effort", [("1", "1 batch"), ("2", "2 batches"), ("4", "4 batches"), ("", "1 batch"),
                                             ("0", "1 batch"), ("x", "1 batch"), ("-2", "1 batch"),
                                             ("10", "10 batches"), ("04", "1 batch")])
def test_the_effort_footer_names_the_batch_count(tmp_path, batches, effort):
    repo = merge_repo(tmp_path, stub_script("base"), stub_script("pr"))

    done = run_post_step(repo, tmp_path, batches=batches)

    assert done.returncode == 0, done.stderr
    assert done.stdout.strip().endswith(f"--effort high (claude-sonnet-5-5), {effort}")


def test_the_effort_footer_names_the_review_steps_effort_and_model():
    yaml = pytest.importorskip("yaml")
    args = yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["steps"]
    claude_args = next(step for step in args if step.get("id") == "claude_1")["with"]["claude_args"]
    model, effort = re.search(r"--model (\S+)", claude_args)[1], re.search(r"--effort (\S+)", claude_args)[1]

    env = post_step_env()

    assert env["REVIEW_EFFORT"] == f"{effort} ({model})"
    assert env["BATCHES"] == "${{ needs.review.outputs.batches }}"


def test_the_review_job_passes_on_its_batch_count():
    yaml = pytest.importorskip("yaml")
    outputs = yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["outputs"]
    assert outputs["batches"] == "${{ steps.diff.outputs.batches }}"


def test_a_base_script_from_before_the_structured_answer_warns_and_posts_nothing(tmp_path):
    repo = merge_repo(tmp_path, stub_script("base", effort=False), stub_script("pr"))

    done = run_post_step(repo, tmp_path)

    assert done.returncode == 0
    assert "::warning::The base's .github/scripts/post_claude_review.py predates the answer" in done.stdout
    assert "copy ran" not in done.stdout


def test_a_base_script_from_before_the_structured_answer_fails_on_withheld_findings(tmp_path):
    repo = merge_repo(tmp_path, stub_script("base", effort=False), stub_script("pr"))

    done = run_post_step(repo, tmp_path, findings="")

    assert done.returncode == 1
    assert "::error::Claude's answer was withheld for quoting a secret" in done.stdout
    assert "copy ran" not in done.stdout


@pytest.mark.parametrize("base", [None, stub_script("base", effort=False)], ids=["no-script", "old-script"])
def test_a_malformed_answer_fails_the_post_step_on_a_base_that_cant_post_it(tmp_path, base):
    repo = merge_repo(tmp_path, base, stub_script("pr"))
    malformed = json.dumps({"malformed": "Claude's answer was withheld: findings is not a list"})

    done = run_post_step(repo, tmp_path, findings=malformed)

    assert done.returncode == 1
    assert "::error::Claude's answer didn't fit the schema" in done.stdout
    assert "::warning::" not in done.stdout and "copy ran" not in done.stdout


def test_the_post_step_tells_a_malformed_answer_by_the_answer_checks_placeholder(tmp_path):
    # The step matches the placeholder's text, so the check's json.dumps must still write it that way.
    _, outputs, _ = check(tmp_path, {"summary": "s", "findings": "none"}, [SECRET])
    assert outputs["findings"].startswith('{"malformed": ')


def test_the_real_post_script_is_one_the_post_step_passes_the_effort_to():
    assert "--effort" in Path(pcr.__file__).read_text()


def test_a_base_without_the_script_warns_and_posts_nothing(tmp_path):
    repo = merge_repo(tmp_path, None, stub_script("pr"))

    done = run_post_step(repo, tmp_path)

    assert done.returncode == 0
    assert "::warning::The base has no .github/scripts/post_claude_review.py" in done.stdout
    assert "copy ran" not in done.stdout


def test_a_base_without_the_script_fails_on_withheld_findings(tmp_path):
    repo = merge_repo(tmp_path, None, stub_script("pr"))

    done = run_post_step(repo, tmp_path, findings="")

    assert done.returncode == 1
    assert "::error::Claude's answer was withheld for quoting a secret" in done.stdout
    assert "copy ran" not in done.stdout


def test_a_checkout_that_isnt_a_merge_commit_fails(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    commit_script(repo, stub_script("base"), "base")
    commit_script(repo, stub_script("pr"), "pr")

    done = run_post_step(repo, tmp_path)

    assert done.returncode == 1
    assert "::error::The checkout isn't the PR's merge commit" in done.stdout
    assert "copy ran" not in done.stdout


# The answer check's hold: pieces Claude didn't read whole

ANSWER = {"summary": "s", "findings": [{"path": "a.py", "line": 3, "body": "x"}]}


def read_of(folder, name, call_id, file=None, error=None):
    """A Read of folder/name and its result, as the action's execution file holds them (Claude Code 2.1.29x)."""
    path = f"{folder}/{name}"
    use = {"type": "assistant", "message": {"id": "msg_" + call_id, "content": [
        {"type": "tool_use", "id": call_id, "name": "Read", "input": {"file_path": path}}]}}
    result = {"type": "tool_result", "tool_use_id": call_id, "content": error or "1\tdiff --git a/x b/x\n"}
    if error:
        result["is_error"] = True
    account = f"Error: {error}" if error else {"type": "text", "file": {"filePath": path, "content": "…", "startLine": 1,
                                                                       "numLines": 40, "totalLines": 40, **(file or {})}}
    return [use, {"type": "user", "message": {"role": "user", "content": [result]}, "tool_use_result": account}]


def hold(tmp_path, messages=(), listed="", skipped="", answer=ANSWER, held=""):
    """Run the answer check over pieces in tmp_path: the findings it added after Claude's, and what it printed."""
    folder = tmp_path / "claude-review" / "diff"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "INDEX").write_text("The PR's diff of 4 file(s) in 3 piece(s).\n"
                                  "001.diff\t40 lines\tread\n\tworkflow\t.github/workflows/ci.yml\n"
                                  "002.diff\t40 lines\tread\n\tsource\tsrc/app.py\n\tsource\t\"src/odd\\tname.py\"\n"
                                  "003.diff\t40 lines\tnot read\n\tdocs\tREADME.md\n"
                                  "excluded (Copilot doesn't review these)\n\texcluded\tyarn.lock\n"
                                  "\texcluded\tdist/index.js\n"
                                  "held\t(nobody reads these here, so they hold the merge for a person)\n"
                                  "\texcluded\tdist/index.js\n\tbinary\tlib/Tool.dll\n")
    code, outputs, out = check(tmp_path, answer, [SECRET], messages=messages, extra={
        "PIECES_DIR": str(folder), "LISTED_1": listed, "SKIPPED": skipped, "HELD": held})
    assert code == 0
    return json.loads(outputs["findings"])["findings"][1:], out


def test_pieces_read_whole_hold_nothing(tmp_path):
    folder = tmp_path / "claude-review" / "diff"
    added, out = hold(tmp_path, [*read_of(folder, "001.diff", "a"), *read_of(folder, "002.diff", "b")],
                      listed="001.diff 002.diff")
    assert added == [] and "::warning::" not in out


@pytest.mark.parametrize("reads", [
    # Cut at the token cap: the text carries no banner, only the tool's account says so.
    lambda f: read_of(f, "002.diff", "b", {"numLines": 12, "truncatedByTokenCap": True}),
    lambda f: read_of(f, "002.diff", "b", {"startLine": 30, "numLines": 11}),
    lambda f: read_of(f, "002.diff", "b", error="File content (31000 tokens) exceeds maximum allowed tokens (25000)."),
    lambda f: [],  # never read
    lambda f: read_of(f.parent, "002.diff", "b"),  # the same name somewhere else isn't the piece
], ids=["token-cap", "part", "error", "never", "elsewhere"])
def test_a_listed_piece_not_read_whole_holds_the_merge(tmp_path, reads):
    folder = tmp_path / "claude-review" / "diff"
    added, out = hold(tmp_path, [*read_of(folder, "001.diff", "a"), *reads(folder)], listed="001.diff 002.diff")
    assert [(f["path"], f["line"], f["severity"]) for f in added] == [("diff", 0, "high")]
    assert "didn't read all of diff pieces 002.diff:" in added[0]["body"] and "merge by hand" in added[0]["body"]
    # The log names the piece's files, with workflow commands stopped around the PR's paths.
    token = re.search(r"^::stop-commands::([0-9a-f]{32})$", out, re.M)[1]
    listed = out[out.index(f"::stop-commands::{token}"):out.index(f"::{token}::")]
    assert "src/app.py" in listed and '"src/odd\\tname.py"' in listed and "ci.yml" not in listed


def test_two_results_in_one_message_dont_count_as_read_whole(tmp_path):
    folder = tmp_path / "claude-review" / "diff"
    first, second = read_of(folder, "001.diff", "a"), read_of(folder, "002.diff", "b")
    second[1]["message"]["content"].insert(0, first[1]["message"]["content"][0])
    added, _ = hold(tmp_path, [first[0], second[0], second[1]], listed="001.diff 002.diff")
    assert "pieces 001.diff, 002.diff:" in added[0]["body"]


def test_pieces_past_the_budget_hold_the_merge(tmp_path):
    folder = tmp_path / "claude-review" / "diff"
    added, out = hold(tmp_path, read_of(folder, "001.diff", "a"), listed="001.diff", skipped="003.diff")
    assert "pieces 003.diff:" in added[0]["body"] and added[0]["severity"] == "high"
    assert "README.md" in out and "src/app.py" not in out


def test_files_nobody_read_hold_the_merge(tmp_path):
    folder = tmp_path / "claude-review" / "diff"
    added, out = hold(tmp_path, read_of(folder, "001.diff", "a"), listed="001.diff", held="2")
    assert [(f["path"], f["line"], f["severity"]) for f in added] == [("diff", 0, "high")]
    assert "didn't read 2 changed file(s)" in added[0]["body"] and "merge by hand" in added[0]["body"]
    token = re.search(r"^::stop-commands::([0-9a-f]{32})$", out, re.M)[1]
    listed = out[out.index(f"::stop-commands::{token}"):out.index(f"::{token}::")]
    assert "dist/index.js" in listed and "lib/Tool.dll" in listed and "yarn.lock" not in listed


@pytest.mark.parametrize("held", ["", "0"])
def test_no_held_files_hold_nothing(tmp_path, held):
    folder = tmp_path / "claude-review" / "diff"
    added, out = hold(tmp_path, read_of(folder, "001.diff", "a"), listed="001.diff", held=held)
    assert added == [] and "::warning::" not in out


def test_an_answer_without_findings_gets_no_hold(tmp_path):
    # A malformed answer becomes the placeholder; adding to it would let it through as findings.
    code, outputs, _ = check(tmp_path, {"summary": "s"}, [SECRET], extra={
        "PIECES_DIR": str(tmp_path), "LISTED_1": "001.diff", "SKIPPED": "002.diff"})
    assert code == 0 and list(json.loads(outputs["findings"])) == ["malformed"]


# A diff read in batches, a Review step each


def execution(tmp_path, name, answer, messages=()):
    """An execution file holding messages, then a result with answer (none when answer is None)."""
    path = tmp_path / name
    result = [{"type": "result", "subtype": "success", "structured_output": answer}] if answer is not None else [
        {"type": "result", "subtype": "error_during_execution", "is_error": True}]
    path.write_text(json.dumps([{"type": "system"}, *messages, *result]))
    return path


def batched(tmp_path, answers, listed, reads=None, secrets=(SECRET,), skipped=""):
    """Run the answer check over len(answers) batches; answers[n] is batch n + 1's answer, None when it didn't
    answer, and reads[n] the Reads in its execution file. Returns (code, the posted answer or None, the log)."""
    folder = tmp_path / "claude-review" / "diff"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "INDEX").write_text("The PR's diff.\n" + "".join(
        f"{name}\t40 lines\tread in batch {n} of {len(answers)}\n\tsource\tsrc/{name}.py\n"
        for n, names in enumerate(listed, 1) for name in names.split()))
    extra = {"PIECES_DIR": str(folder), "BATCHES": str(len(answers)), "SKIPPED": skipped}
    for n, answer in enumerate(answers, 1):
        path = execution(tmp_path, f"execution{n}.json", answer, (reads or {}).get(n, ()))
        extra |= {f"EXECUTION_FILE_{n}": str(path), f"ANSWERED_{n}": "true" if answer is not None else "false",
                  f"LISTED_{n}": listed[n - 1]}
    code, outputs, out = check(tmp_path, secrets=secrets, path=tmp_path / "execution1.json", extra=extra)
    return code, json.loads(outputs["findings"]) if outputs.get("findings") else None, out


def read_whole_of(tmp_path, names, tag):
    folder = tmp_path / "claude-review" / "diff"
    return [message for name in names.split() for message in read_of(folder, name, f"{tag}{name}")]


def test_batches_answers_are_posted_as_one_review(tmp_path):
    answers = [{"overview": "first", "changes": ["c1"], "files": [{"path": "a.py", "description": "d1"}],
                "advisory": ["v1"], "findings": [{"path": "a.py", "line": 1, "severity": "high", "body": "one"}]},
               {"overview": "second", "changes": ["c2", "c3"], "files": [{"path": "a.py", "description": "d2"}],
                "not_reviewed": "n2", "findings": [{"path": "b.py", "line": 2, "severity": "low", "body": "two"}]}]
    listed = ["001.diff 002.diff", "003.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, out = batched(tmp_path, answers, listed, reads)

    assert code == 0 and "::warning::" not in out
    assert answer == {
        "overview": "Claude Review read this PR's diff in 2 batches, a review each.\n\n"
                    "**Batch 1 of 2**\n\nfirst\n\n**Batch 2 of 2**\n\nsecond",
        "changes": ["c1", "c2", "c3"],
        # A path in two batches keeps both rows.
        "files": [{"path": "a.py", "description": "d1"}, {"path": "a.py", "description": "d2"}],
        "advisory": ["v1"],
        "not_reviewed": "**Batch 2 of 2**\n\nn2",
        "findings": [{"path": "a.py", "line": 1, "severity": "high", "body": "one"},
                     {"path": "b.py", "line": 2, "severity": "low", "body": "two"}]}
    report, _ = pcr.parse_findings(json.dumps(answer))
    assert report["overview"] == answer["overview"]


def test_a_joined_review_leaves_out_the_rows_no_batch_gave(tmp_path):
    answers = [{"overview": "first", "changes": [], "files": [], "findings": []},
               {"overview": "second", "changes": [], "files": [], "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    _, answer, _ = batched(tmp_path, answers, listed, reads)

    assert set(answer) == {"overview", "findings"}


def test_a_batch_without_an_overview_gets_no_heading_in_it(tmp_path):
    answers = [{"overview": "", "findings": []}, {"overview": "second", "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    _, answer, _ = batched(tmp_path, answers, listed, reads)

    assert "Batch 1 of 2" not in answer["overview"] and "**Batch 2 of 2**\n\nsecond" in answer["overview"]


def test_a_batch_that_answered_with_a_summary_has_it_as_its_overview(tmp_path):
    # An answer from before the structured fields joins as today's does.
    answers = [{"summary": "old", "findings": []}, {"overview": "new", "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    _, answer, _ = batched(tmp_path, answers, listed, reads)

    assert "summary" not in answer and batch_sections(answer["overview"], 2) == {1: "old", 2: "new"}


def test_one_batch_posts_its_answer_as_it_is(tmp_path):
    code, answer, _ = batched(tmp_path, [ANSWER], ["001.diff"], {1: read_whole_of(tmp_path, "001.diff", "a")})
    assert code == 0 and answer == ANSWER


def test_a_batch_that_didnt_answer_holds_the_merge_on_its_pieces(tmp_path):
    listed = ["001.diff", "002.diff 003.diff"]

    code, answer, out = batched(tmp_path, [ANSWER, None], listed, {1: read_whole_of(tmp_path, listed[0], "a")})

    assert code == 0
    assert answer["not_reviewed"] == "**Batch 2 of 2**\n\nThis batch's review ended without an answer, so its pieces weren't reviewed."
    assert "Batch 2" not in answer["overview"]
    assert answer["findings"][0] == ANSWER["findings"][0]
    assert [(f["path"], f["line"], f["severity"]) for f in answer["findings"][1:]] == [("diff", 0, "high")]
    assert "pieces 002.diff, 003.diff:" in answer["findings"][1]["body"]
    assert "src/002.diff.py" in out and "src/001.diff.py" not in out


def test_only_a_later_batch_answering_is_still_posted(tmp_path):
    listed = ["001.diff", "002.diff"]

    code, answer, _ = batched(tmp_path, [None, ANSWER], listed, {2: read_whole_of(tmp_path, listed[1], "b")})

    assert code == 0 and "pieces 001.diff:" in answer["findings"][-1]["body"]


def test_a_later_batchs_unread_piece_holds_the_merge(tmp_path):
    listed = ["001.diff", "002.diff 003.diff"]
    reads = {1: read_whole_of(tmp_path, "001.diff", "a"), 2: read_whole_of(tmp_path, "002.diff", "b")}

    _, answer, _ = batched(tmp_path, [ANSWER, ANSWER], listed, reads)

    assert "pieces 003.diff:" in answer["findings"][-1]["body"]


def test_a_piece_read_by_another_batch_doesnt_count(tmp_path):
    # Batch 2's pieces must be read in batch 2's review: batch 1 reading them proves nothing about batch 2's answer.
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, "001.diff 002.diff", "a")}

    _, answer, _ = batched(tmp_path, [ANSWER, ANSWER], listed, reads)

    assert "pieces 002.diff:" in answer["findings"][-1]["body"]


def test_a_secret_in_any_batch_withholds_the_whole_review(tmp_path):
    leak = {"summary": "s " + SECRET, "findings": []}

    code, answer, out = batched(tmp_path, [ANSWER, leak], ["001.diff", "002.diff"])

    assert code == 0 and answer is None and out.startswith("::error::Claude's answer quotes")


def test_a_malformed_batch_withholds_the_whole_review(tmp_path):
    code, answer, out = batched(tmp_path, [ANSWER, {"summary": "s"}], ["001.diff", "002.diff"])

    assert code == 0 and list(answer) == ["malformed"] and "doesn't fit the schema" in out


def test_a_batch_that_answered_with_no_answer_in_its_file_fails_the_step(tmp_path):
    folder = tmp_path / "claude-review" / "diff"
    folder.mkdir(parents=True)
    (folder / "INDEX").write_text("x\n")
    second = tmp_path / "execution2.json"
    second.write_text("[]")

    code, outputs, out = check(tmp_path, ANSWER, [SECRET], extra={
        "PIECES_DIR": str(folder), "BATCHES": "2", "EXECUTION_FILE_2": str(second), "ANSWERED_2": "true"})

    assert code == 1 and outputs["findings"] == "" and "answered batch 2, but its answer isn't" in out


def test_no_batch_that_could_be_read_fails_the_step(tmp_path):
    # The step runs because a Review step answered, but the batch count leaves it out.
    code, outputs, out = check(tmp_path, ANSWER, [SECRET], extra={"BATCHES": "1", "ANSWERED_1": "false"})

    assert code == 1 and outputs["findings"] == "" and "No answer of the 1 batch(es)" in out


@pytest.mark.parametrize("batches, counted", [("", 1), ("0", 1), ("x", 1), ("9", 4)])
def test_the_batch_count_is_kept_between_one_and_the_review_steps(tmp_path, batches, counted):
    listed = {f"LISTED_{n}": f"00{n}.diff" for n in range(1, 5)}
    answered = {f"ANSWERED_{n}": "false" for n in range(2, 5)}

    _, outputs, _ = check(tmp_path, ANSWER, [SECRET], extra={"BATCHES": batches, "PIECES_DIR": str(tmp_path),
                                                             **listed, **answered})

    held = json.loads(outputs["findings"])["findings"][-1]["body"]
    assert held.startswith("Claude Review didn't read all of diff pieces " + ", ".join(
        f"00{n}.diff" for n in range(1, counted + 1)) + ":")


@pytest.mark.parametrize("batches, counted", [("3", 3), ("4", 4), ("5", 4)])
def test_the_review_has_a_section_for_each_counted_batch_and_no_more(tmp_path, batches, counted):
    answered = {f"ANSWERED_{n}": "false" for n in range(2, 6)}

    _, outputs, _ = check(tmp_path, ANSWER, [SECRET], extra={"BATCHES": batches, "PIECES_DIR": str(tmp_path), **answered})

    posted = json.loads(outputs["findings"])
    assert posted["overview"].startswith(f"Claude Review read this PR's diff in {counted} batches")
    assert batch_sections(posted["overview"], counted) == {1: "s"}
    assert batch_sections(posted["not_reviewed"], counted) == {
        n: "This batch's review ended without an answer, so its pieces weren't reviewed." for n in range(2, counted + 1)}


def test_a_finding_two_batches_both_report_is_posted_once(tmp_path):
    same = {"path": "a.py", "line": 1, "body": "one"}
    answers = [{"summary": "first", "findings": [same, {"path": "a.py", "line": 1, "body": "other"}]},
               {"summary": "second", "findings": [dict(same), {"path": "a.py", "line": 2, "body": "one"},
                                                  {"path": "a.py", "line": 2, "body": "one"}]}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    # Only a repeat of an earlier batch's goes: the same line with another body, the same body on another line,
    # and a batch's own repeat stay.
    assert code == 0 and answer["findings"] == [same, {"path": "a.py", "line": 1, "body": "other"},
                                                {"path": "a.py", "line": 2, "body": "one"},
                                                {"path": "a.py", "line": 2, "body": "one"}]


def test_a_finding_two_batches_grade_differently_is_posted_once_with_the_first_batchs_severity(tmp_path):
    first = {"path": "a.py", "line": 1, "severity": "high", "body": "one"}
    answers = [{**GOOD_ANSWER, "findings": [first]}, {**GOOD_ANSWER, "findings": [{**first, "severity": "low"}]}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    assert code == 0 and answer["findings"] == [first]


def test_a_finding_with_another_path_than_an_earlier_batchs_is_posted(tmp_path):
    first = {"path": "a.py", "line": 1, "severity": "high", "body": "one"}
    answers = [{**GOOD_ANSWER, "findings": [first]}, {**GOOD_ANSWER, "findings": [{**first, "path": "b.py"}]}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    assert code == 0 and answer["findings"] == [first, {**first, "path": "b.py"}]


def test_a_finding_one_batch_reports_twice_is_kept_as_it_was(tmp_path):
    # One batch alone is posted as it answered, as before batches.
    same = {"path": "a.py", "line": 1, "body": "one"}
    answer = {"summary": "s", "findings": [same, dict(same)]}

    code, posted, _ = batched(tmp_path, [answer], ["001.diff"], {1: read_whole_of(tmp_path, "001.diff", "a")})

    assert code == 0 and posted["findings"] == [same, same]


def summary_share():
    """The workflow's MAX_SUMMARY_CHARS: the most of one batch's summary the joined review keeps."""
    return int(re.search(r"^\s+MAX_SUMMARY_CHARS = ([\d_]+)", WORKFLOW.read_text(), re.M)[1])


def batch_sections(summary, count):
    """{number: the text of its section} in a joined review's overview or not_reviewed."""
    parts = re.split(rf"(?:\n\n)?\*\*Batch (\d+) of {count}\*\*\n\n", summary)
    return {int(number): text for number, text in zip(parts[1::2], parts[2::2], strict=True)}


@pytest.mark.parametrize("extra", [-1, 0, 1])
def test_a_batchs_summary_is_cut_to_its_share_with_a_note_and_only_past_it(tmp_path, extra):
    share = summary_share()
    long = "x" * (share + extra)
    findings = [{"path": "a.py", "line": 1, "body": "y" * share}]
    answers = [{"summary": long, "findings": findings}, {"summary": "second", "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    sections = batch_sections(answer["overview"], 2)
    assert code == 0 and sections[2] == "second" and answer["findings"] == findings
    if extra <= 0:
        assert sections[1] == long
    else:
        # One line longer than the share: its start, quoted, room left only for a comment's closer.
        part = quoted_part(sections[1])
        assert part.startswith("> x") and set(part[2:]) == {"x"} and "was cut" in sections[1]
        assert len(sections[1]) == share - len(" -->")


def test_the_joined_summary_leaves_room_for_the_findings_under_githubs_limit(tmp_path):
    # A review's body is at most 65,536 characters; the findings outside the diff go in it after the summary, whole.
    answers = [{"summary": "z" * 100_000, "findings": []} for _ in range(4)]
    listed = ["001.diff", "002.diff", "003.diff", "004.diff"]
    reads = {n: read_whole_of(tmp_path, name, f"r{n}") for n, name in enumerate(listed, 1)}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    assert code == 0 and len(answer["overview"]) <= 4 * summary_share() + 200 <= 65_536 - 20_000


CUT = "\n\n_This batch"


def quoted_part(section):
    """A cut section's summary part, before its note."""
    return section[:section.index(CUT)]


def cut(tmp_path, summary):
    """The first section of a two-batch review whose first batch answered summary, checked for its share and note."""
    answers = [{"summary": summary, "findings": []}, {"summary": "second", "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}
    code, answer, _ = batched(tmp_path, answers, listed, reads)
    assert code == 0
    sections = batch_sections(answer["overview"], 2)
    assert sections[2] == "second" and "was cut" in sections[1]
    # Room is kept for a comment's closer, so a section without one is that much shorter.
    closed = quoted_part(sections[1]).endswith(" -->")
    assert len(sections[1]) <= summary_share() - (0 if closed else len(" -->"))
    return sections[1]


@pytest.mark.parametrize("line", ["abc def gh", "abc def g", "abcdefghijklm"])
def test_a_cut_ends_at_a_lines_end(tmp_path, line):
    # Lines of several lengths, so the share falls inside one.
    section = cut(tmp_path, f"{line}\n" * 2_000)
    assert set(quoted_part(section).split("\n")) == {f"> {line}"}


def test_a_long_line_after_the_first_is_left_out_whole(tmp_path):
    assert quoted_part(cut(tmp_path, "short\n" + "y" * 20_000)) == "> short"


def test_a_cut_summary_is_quoted_line_by_line_blank_lines_too(tmp_path):
    # Markdown ends a code fence with the quote holding it, however the fence is indented, and the HTML parser a
    # <details> at the quote's end, so whatever the cut leaves open ends before the note.
    section = cut(tmp_path, "One.\n\n    - item\n\n      ```python\n" + "code = 1\n" * 3_000)
    lines = quoted_part(section).split("\n")
    assert lines[:6] == ["> One.", ">", ">     - item", ">", ">       ```python", "> code = 1"]
    assert all(line.startswith(">") for line in lines)
    # A blank line ends the quote, and the note is a paragraph of its own after it.
    assert section[len(quoted_part(section)):].startswith(CUT)


def test_a_line_that_would_pass_the_budget_only_by_its_newline_is_left_out(tmp_path):
    share = summary_share()
    one_line = cut(tmp_path, "x" * (share + 1))
    budget = len(quoted_part(one_line))  # what one line longer than the share keeps
    # The first line and 2,000 quoted "a" lines (4 characters with the newline before each) leave 3 characters of the
    # budget, which the next "a" line fits only without its newline.
    first = budget - len("> ") - 4 * 2_000 - 3
    part = quoted_part(cut(tmp_path, "b" * first + "\na" * 5_000))
    assert part.count("\n") == 2_000 and len(part) == budget - 3


@pytest.mark.parametrize("end", ["\r", "\r\n"])
def test_every_line_markdown_sees_in_a_cut_summary_is_quoted(tmp_path, end):
    # Markdown ends a line at "\r\n" and a lone "\r" too: one without its "> " would end the quote, and a fence on it
    # would open outside it.
    part = quoted_part(cut(tmp_path, f"Intro{end}```python{end}" + f"code = 1{end}" * 3_000))
    assert "\r" not in part and all(line.startswith(">") for line in part.split("\n"))
    assert part.split("\n")[:3] == ["> Intro", "> ```python", "> code = 1"]


def test_a_comment_left_open_in_a_crlf_summary_is_closed_on_its_quoted_line(tmp_path):
    part = quoted_part(cut(tmp_path, "Seen.\r\n<!-- hidden\r\n" + "still hidden\r\n" * 1_000))
    assert part.endswith("> still hidden -->") and "\r" not in part


def test_a_summary_within_the_share_once_its_line_ends_are_one_character_is_posted_whole(tmp_path):
    summary = "a\r\n" * 4_000
    answers = [{"summary": summary, "findings": []}, {"summary": "second", "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}

    code, answer, _ = batched(tmp_path, answers, listed, reads)

    assert code == 0 and batch_sections(answer["overview"], 2)[1] == "a\n" * 4_000


def test_quoting_counts_toward_the_share(tmp_path):
    # Short lines nearly double when quoted; cut() checks the share holds, this that it is filled.
    section = cut(tmp_path, "a\n" * 6_000)
    assert len(section) > summary_share() - len(" -->") - len("\n> a")


def test_a_cut_inside_an_html_comment_closes_it_before_the_note(tmp_path):
    part = quoted_part(cut(tmp_path, "Seen.\n<!-- hidden\n" + "still hidden\n" * 1_000))
    assert part.endswith(" -->") and part.count("-->") == 1


@pytest.mark.parametrize("comment", ["<!-- a -->", "<!-->"])
def test_a_comment_closed_before_the_cut_gets_no_closer(tmp_path, comment):
    part = quoted_part(cut(tmp_path, f"{comment}\n" + "plain\n" * 3_000))
    assert part.count("-->") == 1 and not part.endswith(" -->")


ROWS_CUT = "later Changes, File and Advisory rows were left out"


def joined_first(tmp_path, first):
    """The joined review of two batches, the first answering first; the second's fields are tiny."""
    answers = [{"findings": [], **first}, {"overview": "second", "changes": ["s"], "findings": []}]
    listed = ["001.diff", "002.diff"]
    reads = {1: read_whole_of(tmp_path, listed[0], "a"), 2: read_whole_of(tmp_path, listed[1], "b")}
    code, answer, _ = batched(tmp_path, answers, listed, reads)
    assert code == 0 and batch_sections(answer["overview"], 2)[2] == "second" and answer["changes"][-1] == "s"
    return answer


def batch_size(answer, overview):
    """The characters of batch 1's fields in a joined answer, its overview given."""
    return (len(overview) + sum(len(row) for row in answer.get("changes", [])[:-1])
            + sum(len(row) for row in answer.get("advisory", []))
            + sum(len(row["path"]) + len(row["description"]) for row in answer.get("files", [])))


def test_rows_past_a_batchs_share_go_files_first_last_first_with_a_note(tmp_path):
    share = summary_share()
    files = [{"path": f"f{n}.py", "description": "d" * 2_000} for n in range(4)]
    first = {"overview": "o", "changes": ["c" * 2_000] * 2, "files": files, "advisory": ["a" * 1_000]}

    answer = joined_first(tmp_path, first)

    overview = batch_sections(answer["overview"], 2)[1]
    assert overview.startswith("o\n\n") and ROWS_CUT in overview
    assert [row["path"] for row in answer["files"]] == ["f0.py", "f1.py"]
    assert answer["changes"][:-1] == first["changes"] and answer["advisory"] == first["advisory"]
    assert batch_size(answer, overview) <= share


@pytest.mark.parametrize("advisory, kept", [(2, 2), (3, 2)])
def test_changes_then_advisory_go_once_no_files_are_left(tmp_path, advisory, kept):
    first = {"overview": "o", "changes": ["c" * 4_000] * 3, "files": [{"path": "f.py", "description": "d" * 4_000}],
             "advisory": ["a" * 4_000] * advisory}

    answer = joined_first(tmp_path, first)

    overview = batch_sections(answer["overview"], 2)[1]
    assert "files" not in answer and answer["changes"] == ["s"] and answer["advisory"] == first["advisory"][:kept]
    assert ROWS_CUT in overview and batch_size(answer, overview) <= summary_share()


def test_fields_within_a_batchs_share_are_kept_whole(tmp_path):
    first = {"overview": "o", "changes": ["c" * 3_000], "files": [{"path": "f.py", "description": "d" * 3_000}],
             "advisory": ["a" * 3_000]}

    answer = joined_first(tmp_path, first)

    assert ROWS_CUT not in answer["overview"] and answer["files"] == first["files"] and answer["advisory"] == first["advisory"]


def test_a_long_overview_and_not_reviewed_are_cut_once_no_rows_are_left(tmp_path):
    share = summary_share()
    first = {"overview": "o\n" * share, "changes": ["c" * 100], "not_reviewed": "n\n" * share}

    answer = joined_first(tmp_path, first)

    overview, not_reviewed = batch_sections(answer["overview"], 2)[1], batch_sections(answer["not_reviewed"], 2)[1]
    assert answer["changes"] == ["s"] and overview.endswith(ROWS_CUT_TAIL)
    assert "was cut" in overview and "was cut" in not_reviewed
    assert len(not_reviewed) <= share // 4 and len(overview) + len(not_reviewed) <= share
    assert quoted_part(not_reviewed).startswith("> n")


ROWS_CUT_TAIL = "its findings are whole._"


def rows_cut_note():
    """The workflow's ROWS_CUT_NOTE, which a batch whose rows were cut ends its overview with."""
    literal = re.search(r'ROWS_CUT_NOTE = (\((?:\s*"[^"\n]*")+\))', WORKFLOW.read_text())[1]
    return ast.literal_eval(literal)


@pytest.mark.parametrize("extra", [0, 1])
def test_a_batch_answering_a_summary_and_an_overview_gets_both_in_its_section(tmp_path, extra):
    share = summary_share()
    first = {"summary": "s" * 10, "overview": "o" * (share - 13 + extra), "changes": ["c"]}

    answer = joined_first(tmp_path, first)

    # Both count toward the share, with the blank line between them: one more character and the row goes.
    section = batch_sections(answer["overview"], 2)[1]
    if extra == 0:
        assert section == "s" * 10 + "\n\n" + first["overview"] and answer["changes"] == ["c", "s"]
    else:
        # The overview, one line too long to fit quoted after the summary, is left out whole.
        assert quoted_part(section) == "> " + "s" * 10 + "\n>" and ROWS_CUT in section and answer["changes"] == ["s"]


@pytest.mark.parametrize("extra", [-1, 0, 1])
def test_a_batch_is_kept_whole_up_to_its_share_and_loses_a_row_past_it(tmp_path, extra):
    share = summary_share()
    first = {"overview": "o", "changes": ["c" * (share - 1 + extra)]}

    answer = joined_first(tmp_path, first)

    overview = batch_sections(answer["overview"], 2)[1]
    if extra <= 0:
        assert overview == "o" and answer["changes"] == [first["changes"][0], "s"]
    else:
        assert overview == "o" + rows_cut_note() and answer["changes"] == ["s"]


@pytest.mark.parametrize("extra", [-1, 0, 1])
def test_a_kept_row_leaves_room_for_the_rows_note(tmp_path, extra):
    share, note = summary_share(), rows_cut_note()
    kept = "c" * (share - 1 - len(note) + extra)
    first = {"overview": "o", "changes": [kept, "d" * share]}

    answer = joined_first(tmp_path, first)

    overview = batch_sections(answer["overview"], 2)[1]
    assert overview == "o" + note
    assert answer["changes"] == ([kept, "s"] if extra <= 0 else ["s"])
    assert batch_size(answer, overview) <= share


@pytest.mark.parametrize("extra", [-1, 0, 1])
def test_an_overview_is_cut_only_past_what_the_rows_note_leaves(tmp_path, extra):
    share, note = summary_share(), rows_cut_note()
    long = "o" * (share - len(note) + extra)
    first = {"overview": long, "changes": ["c" * share]}

    answer = joined_first(tmp_path, first)

    overview = batch_sections(answer["overview"], 2)[1]
    assert answer["changes"] == ["s"] and overview.endswith(note)
    if extra <= 0:
        assert overview == long + note
    else:
        assert quoted_part(overview).startswith("> o") and "was cut" in overview
        assert len(overview) <= share


@pytest.mark.parametrize("extra", [-1, 0, 1])
def test_not_reviewed_is_cut_to_a_quarter_of_the_share_before_rows_go(tmp_path, extra):
    share = summary_share()
    quarter = share // 4
    not_reviewed = "n" * (quarter + extra)
    # A batch that fits its share keeps not_reviewed whole; one past it, by not_reviewed alone, has it cut and keeps
    # the rest.
    first = {"overview": "o" * (share - quarter - 1), "changes": ["c"], "not_reviewed": not_reviewed}

    answer = joined_first(tmp_path, first)

    section = batch_sections(answer["not_reviewed"], 2)[1]
    assert batch_sections(answer["overview"], 2)[1] == first["overview"]
    assert answer["changes"] == ["c", "s"]
    if extra <= 0:
        assert section == not_reviewed
    else:
        # Cut to the quarter: one line longer than it keeps all it can, room left only for a comment's closer.
        assert quoted_part(section).startswith("> n") and "was cut" in section
        assert len(section) == quarter - len(" -->")


def test_a_not_reviewed_past_a_quarter_in_a_batch_within_its_share_is_kept_whole(tmp_path):
    first = {"overview": "o", "changes": ["c"], "not_reviewed": "n" * (summary_share() // 4 + 500)}

    answer = joined_first(tmp_path, first)

    assert batch_sections(answer["not_reviewed"], 2)[1] == first["not_reviewed"] and answer["changes"] == ["c", "s"]


def test_a_long_not_reviewed_is_cut_and_short_rows_are_kept(tmp_path):
    share = summary_share()
    first = {"overview": "o", "changes": ["c" * 100], "files": [{"path": "f.py", "description": "d"}],
             "not_reviewed": "n\n" * share}

    answer = joined_first(tmp_path, first)

    assert answer["changes"] == ["c" * 100, "s"] and answer["files"] == first["files"]
    assert ROWS_CUT not in answer["overview"] and "was cut" in batch_sections(answer["not_reviewed"], 2)[1]


def test_an_unread_piece_reaches_the_off_diff_section_with_its_marker_on_line_2(tmp_path):
    # Batch 2 didn't answer, so its piece holds the merge: the post script puts the finding under the off-diff marker.
    listed = ["001.diff", "002.diff"]
    answer = {**GOOD_ANSWER, "findings": [{"path": "src/app.py", "line": 2, "severity": "low", "body": "**On the diff**"}]}

    code, posted, _ = batched(tmp_path, [answer, None], listed, {1: read_whole_of(tmp_path, listed[0], "a")})

    assert code == 0 and posted["findings"][-1]["severity"] == "high"
    body = run(json.dumps(posted)).posted[0][1]["body"]
    assert body.split("\n")[:2] == [pcr.MARKER, pcr.OFF_DIFF_MARKER]
    tail = body[body.index(pcr.OFF_DIFF_HEADER):]
    assert [line for line in tail.split("\n") if line.startswith("- ")] == [
        "- `diff:0`: " + posted["findings"][-1]["body"]]


def review_steps():
    """[(name, id, if, the env's REVIEW_PIECES and REVIEW_BATCH)] for each claude-code-action step, in order."""
    yaml = pytest.importorskip("yaml")
    steps = yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["steps"]
    return [(step["name"], step["id"], step.get("if", ""), step.get("env", {}).get("REVIEW_PIECES"),
             step.get("env", {}).get("REVIEW_BATCH"), step["with"])
            for step in steps if str(step.get("uses", "")).startswith("anthropics/claude-code-action@")]


def test_each_batch_has_a_review_step_with_the_same_inputs_and_its_own_pieces():
    steps = review_steps()
    script = WORKFLOW.read_text()
    limits = {int(n) for n in re.findall(r"^\s+MAX_BATCHES = (\d+)", script, re.M)}
    assert len(re.findall(r"^\s+MAX_BATCHES = \d+", script, re.M)) == 3 and limits == {len(steps)}
    for number, (name, step_id, condition, pieces, note, inputs) in enumerate(steps, 1):
        assert step_id == f"claude_{number}"
        assert name == ("Review" if number == 1 else f"Review batch {number}")
        assert pieces == f"${{{{ steps.diff.outputs.pieces_{number} }}}}"
        assert note == f"${{{{ steps.diff.outputs.note_{number} }}}}"
        assert inputs == steps[0][5]
        assert condition.startswith("steps.secret.outputs.enabled == 'true' && runner.debug != '1'")
        if number > 1:
            assert condition.endswith(f"fromJSON(steps.diff.outputs.batches || '0') >= {number}")
    assert "${{ env.REVIEW_PIECES }}" in steps[0][5]["prompt"] and "${{ env.REVIEW_BATCH }}" in steps[0][5]["prompt"]
    # Every batch's answer, outcome and file reach the steps that read them.
    for number in range(1, len(steps) + 1):
        for needed in (f"EXECUTION_FILE_{number}: ${{{{ steps.kept_{number}.outputs.execution_file }}}}",
                       f"ANSWERED_{number}: ${{{{ steps.claude_{number}.outputs.structured_output != '' }}}}"):
            assert script.count(needed) == 2, needed
        assert f"LISTED_{number}: ${{{{ steps.diff.outputs.listed_{number} }}}}" in script
        assert f"OUTCOME_{number}: ${{{{ steps.claude_{number}.outcome }}}}" in script
        # The job's reviewed output, the answer check's condition and the two ANSWERED_N.
        assert script.count(f"steps.claude_{number}.outputs.structured_output != ''") == 4


def test_the_review_steps_differ_only_in_their_name_id_condition_and_pieces():
    yaml = pytest.importorskip("yaml")
    text = WORKFLOW.read_text()
    steps = [step for step in yaml.safe_load(text)["jobs"]["review"]["steps"]
             if str(step.get("uses", "")).startswith("anthropics/claude-code-action@")]
    first = steps[0]
    for step in steps:
        assert set(step) == {"name", "id", "if", "timeout-minutes", "continue-on-error", "uses", "env", "with"}
        assert {key: step[key] for key in ("timeout-minutes", "continue-on-error", "uses")} == {
            key: first[key] for key in ("timeout-minutes", "continue-on-error", "uses")}
        assert step["continue-on-error"] is True
        assert set(step["env"]) == {"ACTIONS_STEP_DEBUG", "REVIEW_PIECES", "REVIEW_BATCH"}
        assert step["env"]["ACTIONS_STEP_DEBUG"] == "false"
    # One set of inputs, written once: every later step takes the first's through the alias.
    assert text.count("with: &review\n") == 1 and text.count("with: *review\n") == len(steps) - 1


# Each batch's transcript, kept apart: the action writes every review's to one fixed path
# (base-action/src/execution-file.ts at the pin), and names it as any later step's execution_file while it's there.

def keep_steps():
    """{N: the step right after Review step claude_N}."""
    yaml = pytest.importorskip("yaml")
    steps = yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["steps"]
    return {int(step["id"].rsplit("_", 1)[1]): steps[index + 1]
            for index, step in enumerate(steps) if str(step.get("id", "")).startswith("claude_")}


def test_each_batchs_transcript_is_moved_to_a_file_of_its_own_straight_after_its_review():
    kept = keep_steps()
    script = WORKFLOW.read_text()
    assert sorted(kept) == list(range(1, len(review_steps()) + 1))
    for number, step in kept.items():
        assert step["id"] == f"kept_{number}"
        # Whatever the Review step's outcome, even when it named no file, and never failing the job.
        assert step["if"] == f"always() && steps.claude_{number}.outcome != 'skipped'"
        assert step["continue-on-error"] is True
        assert step["env"] == {"EXECUTION_FILE": f"${{{{ steps.claude_{number}.outputs.execution_file }}}}",
                               "BATCH": str(number)}
        assert step["run"] == kept[1]["run"]
        # Nothing else reads the Review step's own execution_file: the answer check and the warning read the kept one.
        assert script.count(f"steps.claude_{number}.outputs.execution_file") == 1


def keep(tmp_path, batch, content=None, named=True, code=0, path=None):
    """Run the keep step's script for batch, the action having written content to its fixed path (none when None)
    and named it as the step's execution_file when named. Returns the execution_file output it wrote, or None."""
    temp = tmp_path / "runner"
    temp.mkdir(exist_ok=True)
    fixed = temp / "claude-execution-output.json"
    if content is not None:
        fixed.write_text(content)
    output = temp / f"output{batch}"
    output.write_text("")
    done = subprocess.run(["bash", "-e", "-c", keep_steps()[1]["run"]], capture_output=True, text=True, env={
        "PATH": path or os.environ["PATH"], "RUNNER_TEMP": str(temp), "GITHUB_OUTPUT": str(output),
        "EXECUTION_FILE": str(fixed) if named and content is not None else "", "BATCH": str(batch)})
    assert done.returncode == code, done.stderr
    found = re.fullmatch(r"execution_file=(.*)\n", output.read_text())
    return found[1] if found else None


def test_each_batchs_transcript_survives_the_next_batch(tmp_path):
    first, second = keep(tmp_path, 1, "batch 1"), keep(tmp_path, 2, "batch 2")
    assert first != second
    assert Path(first).read_text() == "batch 1" and Path(second).read_text() == "batch 2"
    # Moved, not copied: a later Review step that writes nothing finds no file to name as its own.
    assert not (tmp_path / "runner" / "claude-execution-output.json").exists()
    # Outside the folder the reviews can read.
    assert all(Path(path).parent == tmp_path / "runner" for path in (first, second))


def test_a_transcript_its_review_step_didnt_name_is_still_kept_as_that_batchs(tmp_path):
    # The step failed after the action wrote the file but before it named it: the next batch mustn't find it.
    first = keep(tmp_path, 1, "batch 1", named=False)
    assert first is not None and Path(first).read_text() == "batch 1"
    assert keep(tmp_path, 2) is None


def test_a_transcript_that_cant_be_moved_is_deleted_so_no_later_batch_takes_it(tmp_path):
    tools = tmp_path / "bin"
    tools.mkdir()
    (tools / "mv").write_text("#!/bin/sh\nexit 1\n")
    (tools / "mv").chmod(0o755)

    kept = keep(tmp_path, 1, "batch 1", code=1, path=f"{tools}:{os.environ['PATH']}")

    assert kept is None and not (tmp_path / "runner" / "claude-execution-output.json").exists()
    assert keep(tmp_path, 2) is None


# The "Warn when Claude didn't review" step

def warn_script():
    """The run script of the step named "Warn when Claude didn't review"."""
    lines = WORKFLOW.read_text().splitlines()
    step = next(i for i, line in enumerate(lines) if line.strip() == "- name: Warn when Claude didn't review")
    run = next(i for i in range(step, len(lines)) if lines[i].strip() == "run: |")
    indent = len(lines[run]) - len(lines[run].lstrip()) + 1
    end = next((i for i in range(run + 1, len(lines))
                if lines[i].strip() and len(lines[i]) - len(lines[i].lstrip()) < indent), len(lines))
    return textwrap.dedent("\n".join(lines[run + 1:end])) + "\n"


@pytest.mark.parametrize("result, sign_in", [
    ({"is_error": True, "result": "Invalid API key · Please run /login"}, True),
    ({"is_error": True, "result": "OAuth token has expired. Please obtain a new token or refresh your existing token."},
     True),
    ({"is_error": True, "result": 'API Error: 401 {"type":"error","error":{"type":"authentication_error"}}'}, True),
    ({"is_error": True, "result": "Claude AI usage limit reached|1759940300"}, False),  # 403 in the reset time
    ({"is_error": True, "result": 'API Error: 529 {"type":"overloaded_error"} request_id: req_011C403abc'}, False),
    # It reached the model, whatever its text says.
    ({"is_error": True, "result": "Invalid API key", "total_cost_usd": 0.41, "modelUsage": {"claude-opus-5-5": {}}},
     False),
    ({"is_error": False, "result": "done"}, False),
], ids=["bad-key", "expired-oauth", "401", "usage-limit", "overloaded", "costed", "no-error"])
def test_the_warning_tells_a_sign_in_failure_from_a_limit(tmp_path, result, sign_in):
    execution = tmp_path / "execution.json"
    execution.write_text(json.dumps([{"type": "system"},
                                     {"type": "result", "total_cost_usd": 0, "modelUsage": {}, **result}]))
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True,
                          env={"PATH": os.environ["PATH"], "OUTCOME_1": "failure", "EXECUTION_FILE_1": str(execution)})
    assert done.returncode == 0, done.stderr
    assert ("Claude never reached the model" in done.stdout) is sign_in
    assert ("once a usage limit has reset" in done.stdout) is not sign_in


@pytest.mark.parametrize("subtype", ["error_max_budget_usd", "error_max_turns"])
def test_the_warning_says_a_capped_review_shouldnt_be_rerun(tmp_path, subtype):
    execution = tmp_path / "execution.json"
    execution.write_text(json.dumps([{"type": "result", "subtype": subtype, "is_error": True, "result": "",
                                      "total_cost_usd": 2.97, "modelUsage": {"claude-opus-5-5": {}}}]))
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True,
                          env={"PATH": os.environ["PATH"], "OUTCOME_1": "failure", "EXECUTION_FILE_1": str(execution)})
    assert done.returncode == 0, done.stderr
    assert "hit its spending or turn cap" in done.stdout and "once a usage limit has reset" not in done.stdout


def test_the_warning_says_a_review_the_action_rejected_for_its_turns_shouldnt_be_rerun(tmp_path):
    # Claude Code reports success; the action rejects num_turns over --max-turns.
    execution = tmp_path / "execution.json"
    execution.write_text(json.dumps([{"type": "result", "subtype": "success", "is_error": False, "num_turns": 31,
                                      "total_cost_usd": 1.9, "modelUsage": {"claude-opus-5-5": {}}}]))
    env = {"PATH": os.environ["PATH"], "OUTCOME_1": "failure", "EXECUTION_FILE_1": str(execution), "MAX_TURNS": "30"}
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True, env=env)
    assert done.returncode == 0, done.stderr
    assert "hit its spending or turn cap" in done.stdout
    within = dict(env, MAX_TURNS="31")
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True, env=within)
    assert "once a usage limit has reset" in done.stdout


def test_the_warning_steps_max_turns_is_the_review_steps():
    # The Review step's claude_args must pass --max-turns, and every number given for it anywhere, comments
    # included, must be the warning step's MAX_TURNS.
    text = WORKFLOW.read_text()
    lines = text.splitlines()
    step = next((i for i, line in enumerate(lines) if line.strip() == "- name: Review"), None)
    assert step is not None, "claude-review.yml has no step named Review"
    step_end = next((i for i in range(step + 1, len(lines)) if lines[i].strip().startswith("- name: ")), len(lines))
    start = next((i for i in range(step, step_end) if lines[i].strip() == "claude_args: >-"), None)
    assert start is not None, "the Review step has no claude_args: >- block"
    indent = len(lines[start]) - len(lines[start].lstrip()) + 1
    end = next((i for i in range(start + 1, len(lines))
                if lines[i].strip() and len(lines[i]) - len(lines[i].lstrip()) < indent), len(lines))
    args = re.findall(r'--max-turns(?:=|\s+)"?(\d+)', "\n".join(lines[start + 1:end]))
    limit = re.findall(r'^\s+MAX_TURNS: "(\d+)"$', text, re.M)
    assert len(args) == 1 and len(limit) == 1 and args == limit
    assert set(re.findall(r'--max-turns(?:=|\s+)"?(\d+)', text)) == set(limit)


def warn(env):
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True,
                          env={"PATH": os.environ["PATH"], **env})
    assert done.returncode == 0, done.stderr
    return done.stdout


def test_no_warning_when_every_batch_answered():
    assert warn({"BATCHES": "2", "ANSWERED_1": "true", "ANSWERED_2": "true"}) == ""


def test_the_warning_runs_on_every_review_it_could_warn_about():
    # Not only when no batch answered: a batch that didn't answer beside one that did is warned about too.
    yaml = pytest.importorskip("yaml")
    step = next(step for step in yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["steps"]
                if step.get("name") == "Warn when Claude didn't review")
    assert step["if"] == "steps.secret.outputs.enabled == 'true' && runner.debug != '1'"


@pytest.mark.parametrize("batches, counted", [("", 1), ("0", 1), ("x", 1), ("3", 3), ("4", 4), ("5", 4), ("9", 4)])
def test_the_warnings_batch_count_is_kept_between_one_and_the_review_steps(batches, counted):
    out = warn({"BATCHES": batches, "ANSWERED_1": "true", **{f"ANSWERED_{n}": "false" for n in range(2, 10)}})

    assert [line.split(" ended")[0] for line in out.splitlines()] == [
        f"::warning::Claude Review's batch {n} of {counted}" for n in range(2, counted + 1)]


def test_a_batch_without_an_answer_beside_one_that_answered_warns_that_its_pieces_hold(tmp_path):
    capped = execution(tmp_path, "capped.json", None)
    capped.write_text(json.dumps([{"type": "result", "subtype": "error_max_budget_usd", "is_error": True}]))

    out = warn({"BATCHES": "3", "ANSWERED_1": "true", "ANSWERED_2": "false", "OUTCOME_2": "failure",
                "EXECUTION_FILE_2": str(capped), "ANSWERED_3": "false", "OUTCOME_3": "failure",
                "EXECUTION_FILE_3": str(tmp_path / "missing.json")})

    lines = out.splitlines()
    assert len(lines) == 2 and all("so its pieces hold the merge" in line for line in lines)
    assert lines[0].startswith("::warning::Claude Review's batch 2 of 3") and "spending or turn cap" in lines[0]
    assert lines[1].startswith("::warning::Claude Review's batch 3 of 3") and "usage limit has reset" in lines[1]


def test_batches_past_the_count_arent_warned_about():
    assert warn({"BATCHES": "1", "ANSWERED_1": "true", "ANSWERED_2": "false"}) == ""


def test_when_no_batch_answered_a_sign_in_failure_comes_first(tmp_path):
    unreached = execution(tmp_path, "unreached.json", None)
    unreached.write_text(json.dumps([{"type": "result", "is_error": True, "result": "Invalid API key · Please run /login",
                                      "total_cost_usd": 0, "modelUsage": {}}]))
    capped = tmp_path / "capped.json"
    capped.write_text(json.dumps([{"type": "result", "subtype": "error_max_turns", "is_error": True}]))

    out = warn({"BATCHES": "2", "EXECUTION_FILE_1": str(capped), "EXECUTION_FILE_2": str(unreached),
                "OUTCOME_1": "failure", "OUTCOME_2": "failure"})

    assert out.count("::warning::") == 1 and "Claude never reached the model" in out


def test_the_warning_survives_a_missing_execution_file(tmp_path):
    done = subprocess.run(["bash", "-e", "-c", warn_script()], capture_output=True, text=True,
                          env={"PATH": os.environ["PATH"], "OUTCOME_1": "failure", "EXECUTION_FILE_1": str(tmp_path / "x")})
    assert done.returncode == 0 and "once a usage limit has reset" in done.stdout


# The comments at the top of claude-review.yml (mpaulosky/dotfiles#214)


def test_header_names_pr_automerge_among_what_adds_review_claude():
    """The header lists what adds review:claude. PR Auto-Merge does too, with the PAT."""
    header = []
    for line in WORKFLOW.read_text().splitlines():
        if line.startswith(('"on":', "on:")):
            break
        if line.startswith("#"):
            header.append(line.lstrip("#").strip())
    sentences = re.split(r"(?<=[.!?])\s+", " ".join(header))
    adders = [s for s in sentences if re.search(r"\badds?\b", s) and "review:claude" in s]
    assert adders, "the header has no sentence about what adds review:claude"
    assert any("PR Auto-Merge" in s for s in adders), adders


def answer_schema():
    """The --json-schema the Review steps give Claude."""
    yaml = pytest.importorskip("yaml")
    steps = yaml.safe_load(WORKFLOW.read_text())["jobs"]["review"]["steps"]
    claude_args = next(step for step in steps if step.get("id") == "claude_1")["with"]["claude_args"]
    return json.loads(re.search(r"--json-schema '([^']*)'", claude_args)[1])


def test_the_schema_asks_for_the_structured_fields_and_a_severity_on_each_finding():
    schema = answer_schema()

    assert schema["required"] == ["overview", "changes", "files", "findings"]
    assert set(schema["properties"]) == {"overview", "changes", "files", "advisory", "not_reviewed", "findings"}
    files, finding = schema["properties"]["files"]["items"], schema["properties"]["findings"]["items"]
    assert files["required"] == ["path", "description"] and set(files["properties"]) == {"path", "description"}
    assert finding["required"] == ["path", "line", "severity", "body"]
    assert finding["properties"]["severity"] == {"type": "string", "enum": list(pcr.SEVERITIES)}
    # The types, pinned here since the jsonschema test below doesn't run in CI.
    strings = {"type": "array", "items": {"type": "string"}}
    assert schema["properties"]["overview"] == schema["properties"]["not_reviewed"] == {"type": "string"}
    assert schema["properties"]["changes"] == schema["properties"]["advisory"] == strings
    assert schema["properties"]["files"]["type"] == schema["properties"]["findings"]["type"] == "array"
    assert files["type"] == finding["type"] == "object"
    assert files["properties"] == {"path": {"type": "string"}, "description": {"type": "string"}}
    assert finding["properties"]["path"] == finding["properties"]["body"] == {"type": "string"}
    assert finding["properties"]["line"] == {"type": "integer", "minimum": 0, "maximum": 1_000_000}
    # Claude may answer only in the fields that get posted.
    assert all(part["additionalProperties"] is False for part in (schema, files, finding))


GOOD_ANSWER = {"overview": "o", "changes": ["c"], "files": [{"path": "a.py", "description": "d"}],
               "advisory": ["v"], "not_reviewed": "n",
               "findings": [{"path": "a.py", "line": 1, "severity": "medium", "body": "**T**\n\nb"}]}


@pytest.mark.parametrize("answer, valid", [
    (GOOD_ANSWER, True),
    ({key: value for key, value in GOOD_ANSWER.items() if key not in ("advisory", "not_reviewed")}, True),
    ({**GOOD_ANSWER, "findings": [{"path": "a.py", "line": 1, "body": "b"}]}, False),
    ({**GOOD_ANSWER, "findings": [{"path": "a.py", "line": 1, "severity": "critical", "body": "b"}]}, False),
    ({**GOOD_ANSWER, "summary": "s"}, False),
    ({key: value for key, value in GOOD_ANSWER.items() if key != "files"}, False),
    ({**GOOD_ANSWER, "files": [{"path": "a.py"}]}, False),
])
def test_the_schema_accepts_a_structured_answer_and_refuses_a_finding_without_a_severity(answer, valid):
    jsonschema = pytest.importorskip("jsonschema")
    errors = list(jsonschema.Draft202012Validator(answer_schema()).iter_errors(answer))
    assert not errors if valid else errors


def test_an_answer_the_schema_accepts_passes_the_check_and_the_post_script(tmp_path):
    code, outputs, _ = check(tmp_path, GOOD_ANSWER, [SECRET])

    assert code == 0 and json.loads(outputs["findings"]) == GOOD_ANSWER
    report, findings = pcr.parse_findings(outputs["findings"])
    assert findings == GOOD_ANSWER["findings"] and report["files"] == GOOD_ANSWER["files"]


def test_the_prompt_asks_for_the_structured_fields_severities_and_titled_findings():
    prompt = " ".join(WORKFLOW.read_text().split())

    for field in ("- overview:", "- changes:", "- files:", "- advisory:", "- not_reviewed:"):
        assert field in prompt
    assert "Give no verdict" in prompt
    for level in pcr.SEVERITIES:
        assert f"- {level}:" in prompt
    assert "Start each finding's body with a bold one-line title" in prompt
    # The posting script that vets suggestions reaches a repo with the re-Apply, but a PR's review
    # runs the base's script: the prompt asks for suggestions only in a later change.
    assert "suggestion" not in prompt.lower()


@pytest.mark.parametrize(
    "body",
    [
        "```suggestion\n" + "a" * 200_000,
        "`" * 200_000,
        "~" * 200_000,
        "`" * 100_000 + " " * 100_000,
        "~" * 100_000 + "\t" * 100_000,
        "```" + " " * 200_000,
    ],
    ids=["long line", "backticks", "tildes", "backticks then spaces", "tildes then tabs", "fence then spaces"],
)
def test_suggestion_handling_takes_linear_time_on_long_input(body):
    start = time.monotonic()
    pcr.vet_suggestions(body)
    pcr.defuse_suggestions(body)

    assert time.monotonic() - start < 2


def workflow_credentials():
    """The workflow answer check's credential definitions, run on their own."""
    script = answer_check_script()
    start = script.index("CREDENTIAL = re.compile(")
    namespace = {"re": re}
    exec(script[start:script.index("\n\n\ndef answer_in", start)], namespace)
    return namespace


def a_jwt(first=12, second=12, third=12, prefix="eyJ"):
    return f"{prefix}{'a' * first}.eyJ{'b' * second}.{'c' * third}"


GLUED = "ghp_" + "A" * 36 + "_" + a_jwt()


@pytest.mark.parametrize(
    "text",
    [
        a_jwt(),
        "x-" + a_jwt(),
        GLUED,
        "ghs_123_" + a_jwt(),
        a_jwt(second=200),  # a payload past MAX_TOKEN_RUN: the signature goes too
        a_jwt(first=200),
        a_jwt(third=300),
        "eyJ" * 70_000,
        "(" + a_jwt() + ")",
    ],
    ids=["jwt", "prefixed", "glued to ghp", "installation token", "long payload", "long header", "long signature",
         "run of eyJ", "in parentheses"],
)
def test_no_credential_shaped_text_survives_redaction(text):
    for redact_text in (pcr.redact_credentials, workflow_credentials()["redact_credentials"]):
        redacted, count = redact_text(f"see {text} end")

        assert count >= 1
        assert redacted.startswith("see ") and redacted.endswith(" end")
        assert not pcr.CREDENTIAL.search(redacted)
        assert "bbbbbbbb" not in redacted and "cccccccc" not in redacted


@pytest.mark.parametrize("text", ["ghp_" + "A" * 36 + a_jwt(), "sk-ant-" + "a" * 30 + "-" + a_jwt()], ids=["ghp then jwt", "sk-ant then jwt"])
def test_a_jwt_run_straight_onto_a_token_is_redacted_as_the_original_pattern_did(text):
    # The token's alternative takes the JWT's first segment too, as it always has, so the rest
    # (two segments) is no longer a JWT; the redaction must still act and never match a JWT.
    redacted, count = pcr.redact_credentials(text)

    assert count >= 1
    assert not pcr.CREDENTIAL.search(redacted)
    assert pcr.CREDENTIAL.sub("[redacted]", text) == redacted


def test_a_long_jwts_signature_is_replaced_with_its_payload():
    redacted, _ = pcr.redact_credentials("t=" + a_jwt(second=400, third=50))

    assert redacted == "t=[redacted]"


def test_ordinary_text_with_short_words_is_left_alone():
    text = "Fix snake_case_names, dotted.names.here and a 64-char hash " + "0" * 64 + " in src/app.py:12."

    assert pcr.redact_credentials(text) == (text, 0)


def test_the_script_and_the_workflow_redact_alike():
    rng = random.Random(299)
    pieces = [a_jwt(), a_jwt(second=200), GLUED, "ghs_9_" + a_jwt(), "sk-ant-" + "q" * 25, "x-", "eyJ", ".", " ", "/", "a" * 130, "ok", LONG_NAME]
    redact_workflow = workflow_credentials()["redact_credentials"]
    for _ in range(300):
        text = "".join(rng.choice(pieces) for _ in range(rng.randint(1, 6)))

        assert pcr.redact_credentials(text) == redact_workflow(text)


def test_the_redaction_leaves_nothing_the_original_pattern_matches():
    rng = random.Random(299)
    pieces = [a_jwt(), a_jwt(first=8, second=8, third=8), a_jwt(second=200), GLUED, "ghp_" + "Z" * 40, "ghs_9_" + a_jwt(),
              "github_pat_" + "a" * 22 + "_" + "b" * 59, "sk-ant-" + "q" * 25, "x-", "_", "-", ".", " ", "eyJ", "/"]
    matched = 0
    for _ in range(3000):
        text = "".join(rng.choice(pieces) for _ in range(rng.randint(1, 7)))
        matched += bool(pcr.CREDENTIAL.search(text))
        redacted, _ = pcr.redact_credentials(text)

        assert not pcr.CREDENTIAL.search(redacted), text
    assert matched > 1500


@pytest.mark.parametrize(
    "text",
    [
        "eyJ" * 70_000,
        "ghp_1_eyJ" * 25_000,
        "eyJ" * 40_000 + "." + "eyJ" * 40_000,
        "x-" * 100_000 + "eyJ",
        ("eyJ" * 42 + "a" + ".") * 1_500,  # many runs just under MAX_TOKEN_RUN, each full of eyJ
        ("ghp_1_eyJ" * 14 + ".") * 1_500,
        "a." * 100_000,
    ],
    ids=["eyJ run", "ghp eyJ run", "two eyJ runs", "long prefix", "many short eyJ runs", "many ghp runs", "many dots"],
)
def test_redacting_takes_linear_time_on_long_input(text):
    redact_workflow = workflow_credentials()["redact_credentials"]
    start = time.monotonic()
    pcr.redact_credentials(text)
    redact_workflow(text)

    assert time.monotonic() - start < 3


LONG_NAME = "tests/Get_Articles_When_" + "A" * 130 + "_Returns_401.cs"

# Long tokens that start no credential: a generated test's name, an identifier, a hex digest, a base64 blob.
LONG_PLAIN_TOKENS = {
    "test name": LONG_NAME,
    "identifier": "Get" + "Articles" * 30 + "ForTheCurrentUser",
    "sha512 digest": "9f86d081884c7d659a2feaa0c55ad015" * 8,
    "base64 blob": "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo" * 5 + "==",
    "dotted name": "Company." + "Segment" * 30 + ".Service",
}


@pytest.mark.parametrize("text", LONG_PLAIN_TOKENS.values(), ids=LONG_PLAIN_TOKENS.keys())
def test_a_long_token_that_starts_no_credential_is_left_alone_with_no_warning(text):
    sentence = f"The finding is in {text}, which is not a secret."
    for redact_text in (pcr.redact_credentials, workflow_credentials()["redact_credentials"]):
        assert redact_text(sentence) == (sentence, 0)


def test_a_long_file_name_quoted_in_a_finding_body_and_the_summary_is_left_alone():
    summary, findings, count = pcr.redact(f"| {LONG_NAME} | added |", [{"path": "a.py", "line": 3, "body": f"See `{LONG_NAME}`."}])

    assert LONG_NAME in summary
    assert LONG_NAME in findings[0]["body"]
    assert count == 0


# Each one starts a credential and holds a run over MAX_TOKEN_RUN. CREDENTIAL alone either leaves part of it behind (the ghp, ghu
# and sk-ant cases) or matches none of it (the others), so only the long-run rule replaces it whole, and each start it looks for is
# pinned.
LONG_CREDENTIAL_STARTS = {
    "ghp": "ghp_" + "A" * 40 + "_" + "b" * 200,
    "ghu": "ghu_" + "A" * 40 + "_" + "b" * 200,
    "sk-ant": "sk-ant-" + "a" * 30 + "." + "b" * 200,
    "github_pat": "github_pat_" + "a" * 300,
    "eyJ in the middle": "prefix" + "q" * 140 + "eyJ" + "q" * 140,
    "start in the first segment": "ghp_1." + "b" * 200,
    "start in a later segment": "a" * 200 + ".eyJ" + "b" * 20,
    "start before the long segment": "sk-ant-" + "a" * 5 + "." + "b" * 200,
}


@pytest.mark.parametrize("text", LONG_CREDENTIAL_STARTS.values(), ids=LONG_CREDENTIAL_STARTS.keys())
def test_a_long_token_that_starts_a_credential_is_still_replaced_whole(text):
    for redact_text in (pcr.redact_credentials, workflow_credentials()["redact_credentials"]):
        redacted, count = redact_text(f"see {text} end")

        assert redacted == "see [redacted] end"
        assert count == 1


def test_a_long_run_in_a_chain_with_no_start_is_left_alone_wherever_it_sits():
    text = "see " + "x" * 200 + ".Service and Company." + "y" * 200 + " end"
    for redact_text in (pcr.redact_credentials, workflow_credentials()["redact_credentials"]):
        assert redact_text(text) == (text, 0)


def test_the_workflow_carries_the_scripts_long_run_rule():
    workflow = workflow_credentials()

    assert workflow["CREDENTIAL_START"].pattern == pcr.CREDENTIAL_START.pattern
    assert workflow["MAX_TOKEN_RUN"] == pcr.MAX_TOKEN_RUN
    assert workflow["TOKEN_CHAIN"].pattern == pcr.TOKEN_CHAIN.pattern
    assert workflow["CREDENTIAL"].pattern == pcr.CREDENTIAL.pattern


@pytest.mark.parametrize(
    "sample",
    [
        "sk-ant-" + "a" * 20,
        *(f"gh{letter}_" + "A" * 36 for letter in "pousr"),
        "ghs_123_" + a_jwt(),
        "github_pat_" + "a" * 22 + "_" + "b" * 59,
        a_jwt(),
    ],
    ids=["sk-ant", "ghp", "gho", "ghu", "ghs", "ghr", "installation token", "github_pat", "jwt"],
)
def test_every_text_credential_matches_starts_with_a_credential_start(sample):
    # CREDENTIAL can only match inside a token that CREDENTIAL_START finds, or the long-run rule would skip a run CREDENTIAL scans slowly.
    for credential, start in ((pcr.CREDENTIAL, pcr.CREDENTIAL_START), (workflow_credentials()["CREDENTIAL"], workflow_credentials()["CREDENTIAL_START"])):
        match = credential.search(sample)

        assert match, sample
        assert start.search(match.group())


def test_a_long_file_name_in_a_finding_path_is_left_alone():
    summary, findings, count = pcr.redact("s", [{"path": LONG_NAME, "line": 3, "body": "b"}])

    assert findings[0]["path"] == LONG_NAME
    assert count == 0


def test_a_long_name_that_holds_a_credential_start_without_being_one_is_left_alone_in_a_finding_path():
    path = "tests/Get_eyJ_" + "A" * 130 + "_Returns_401.cs"
    _, findings, count = pcr.redact("s", [{"path": path, "line": 3, "body": "b"}])

    assert findings[0]["path"] == path
    assert count == 0
    # The long-run rule, which a path does not get, would have replaced it whole.
    assert pcr.redact_credentials(path) == ("tests/[redacted]", 1)


def test_a_credential_in_a_finding_path_is_still_redacted():
    path = "src/" + a_jwt() + ".cs"
    _, findings, count = pcr.redact("s", [{"path": path, "line": 3, "body": "b"}])

    assert "bbbbbbbb" not in findings[0]["path"]
    assert count >= 1


def test_a_path_past_the_limit_gets_the_full_rule():
    path = "x/" + "eyJ" * 70_000
    start = time.monotonic()
    _, findings, _ = pcr.redact("s", [{"path": path, "line": 3, "body": "b"}])

    assert findings[0]["path"] == "x/[redacted]"
    assert time.monotonic() - start < 3


def test_the_workflow_redacts_paths_like_the_script():
    redact = workflow_credentials()["redact_path"]

    assert redact(LONG_NAME) == (LONG_NAME, 0)
    assert redact("src/" + a_jwt()) == pcr.redact_path("src/" + a_jwt())
    assert redact("x/" + "eyJ" * 1000) == pcr.redact_path("x/" + "eyJ" * 1000)


def test_the_workflows_redact_leaves_a_long_file_name_in_a_path_field_alone():
    script = answer_check_script()
    namespace = workflow_credentials()
    exec(script[script.index("def redact(value, key=None):"):script.index("def write_outputs")], namespace)
    answer = {"summary": "see " + a_jwt(), "findings": [{"path": LONG_NAME, "line": 3, "body": "see " + a_jwt()}]}

    redacted, count = namespace["redact"](answer)

    assert redacted["findings"][0]["path"] == LONG_NAME
    assert "bbbbbbbb" not in redacted["summary"] and "bbbbbbbb" not in redacted["findings"][0]["body"]
    assert count == 2

