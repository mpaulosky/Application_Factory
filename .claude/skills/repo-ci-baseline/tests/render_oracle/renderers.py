"""The two renderers a release post and the docs/README.md copy meet, as test oracles, and the C# port they judge.

GitHub renders Markdown with cmark-gfm; GitHub Pages publishes the posts with
kramdown's GFM parser. Each function renders a list of documents in one call.
release_post_cs runs the Template's ReleasePost.cs through release_post.cs
beside this file (ADR 0017's Update for mpaulosky/Application_Factory#164).
"""

import json
import os
import shutil
import subprocess
from html.parser import HTMLParser
from pathlib import Path

import cmarkgfm

# The versions the github-pages gem pins.
KRAMDOWN_GEMS = ("kramdown:2.4.0", "kramdown-parser-gfm:1.1.0")
KRAMDOWN_IMAGE = "repo-ci-baseline-kramdown:2.4.0"
KRAMDOWN_RB = Path(__file__).with_name("kramdown.rb")
RELEASE_POST_CS = Path(__file__).with_name("release_post.cs")
# dotnet prints no logo, sends no usage data and leaves no build server running.
DOTNET_ENV = {
    "DOTNET_NOLOGO": "1",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    "MSBUILDDISABLENODEREUSE": "1",
    "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
    "UseSharedCompilation": "false",
}


def cmark(docs):
    options = cmarkgfm.Options.CMARK_OPT_UNSAFE
    return [cmarkgfm.github_flavored_markdown_to_html(doc, options=options) for doc in docs]


def kramdown_command():
    """A local Ruby with the pinned gems, else a Docker image built with them once."""
    if shutil.which("ruby"):
        probe = subprocess.run(
            ["ruby", "-e", 'gem "kramdown", "2.4.0"; gem "kramdown-parser-gfm", "1.1.0"'],
            capture_output=True,
        )
        if probe.returncode == 0:
            return ["ruby", str(KRAMDOWN_RB)]
    if not shutil.which("docker"):
        raise RuntimeError("kramdown needs Ruby with " + " and ".join(KRAMDOWN_GEMS) + ", or Docker")
    if subprocess.run(["docker", "image", "inspect", KRAMDOWN_IMAGE], capture_output=True).returncode:
        dockerfile = "FROM ruby:3.3-slim\nRUN gem install --no-document " + " ".join(KRAMDOWN_GEMS) + "\n"
        subprocess.run(["docker", "build", "-q", "-t", KRAMDOWN_IMAGE, "-"], input=dockerfile.encode(), check=True)
    return ["docker", "run", "--rm", "-i", "-v", f"{KRAMDOWN_RB}:/kramdown.rb:ro", KRAMDOWN_IMAGE, "ruby", "/kramdown.rb"]


def kramdown(docs):
    result = subprocess.run(kramdown_command(), input=json.dumps(docs), capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def release_post_cs(commit_lists, readmes):
    """ReleasePost.cs's render_commits of each commit list and rebase_readme_links of each README, in one run.

    Built first, from this folder, so the Factory's global.json picks the SDK and the run's stdout holds only the JSON.
    """
    if not shutil.which("dotnet"):
        raise RuntimeError("the C# port needs dotnet, the SDK global.json names")
    env = {**os.environ, **DOTNET_ENV}
    folder = RELEASE_POST_CS.parent
    build = subprocess.run(["dotnet", "build", str(RELEASE_POST_CS)], cwd=folder, env=env, capture_output=True, text=True, stdin=subprocess.DEVNULL)
    if build.returncode:
        raise RuntimeError(f"dotnet build {RELEASE_POST_CS.name} failed:\n{build.stdout}{build.stderr}")
    run = subprocess.run(
        ["dotnet", "run", "--no-build", "--file", str(RELEASE_POST_CS)],
        cwd=folder,
        env=env,
        input=json.dumps({"commits": commit_lists, "readmes": readmes}),
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if run.returncode:
        raise RuntimeError(f"{RELEASE_POST_CS.name} exited {run.returncode}:\n{run.stderr}")
    output = json.loads(run.stdout)
    return output["commits"], output["readmes"]


class _Reader(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links, self.text = [], []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "a" and "href" in attrs:
            self.links.append(attrs["href"])
        elif tag == "img" and "src" in attrs:
            self.links.append(attrs["src"])

    def handle_data(self, data):
        self.text.append(data)


def read(html):
    """(links, text) of rendered HTML: every href and src in order, and the visible text."""
    reader = _Reader()
    reader.feed(html)
    return reader.links, "".join(reader.text)
