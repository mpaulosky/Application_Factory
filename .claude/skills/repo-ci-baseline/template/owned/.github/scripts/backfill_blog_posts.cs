#:include Gh.cs
#:include HtmlEntities.cs
#:include ReleasePost.cs
#:include BackfillBlogPosts.cs
#:property PublishAot=false
// Write release blog posts for past releases and refresh the release tables. BackfillBlogPosts.cs holds the logic, on ReleasePost.cs's
// posts and tables; Gh.cs the argument parser and Script.Run.
//
// Called by .github/workflows/backfill-blog-posts.yml, after a step that builds it (dotnet build .github/scripts/backfill_blog_posts.cs):
//
//     dotnet run --no-build --file .github/scripts/backfill_blog_posts.cs -- [--regenerate true] [--pr-numbers "1, 3"]
//
// and by the docs job in .github/workflows/release.yml, which names the PRs its run just released:
//
//     dotnet run --no-build --file .github/scripts/backfill_blog_posts.cs -- --wait-for-prs "187, 188"
//
// Every non-draft GitHub Release names its source PR in a "Source PR: #n" line. For each release whose PR has no
// docs/blogs/<date>-pr-{n}-*.md post (or every release with --regenerate true, read without its blanks and in any case), it writes the post as
// release_post.cs does, under the release's existing tag, oldest version first. It never creates tags or Releases. Then the README,
// docs/README.md and docs/index.html tables are rebuilt once, even when no post was written, so docs/README.md follows a README-only
// [skip-release] merge. When main is already up to date, no file changes. The build's output is under the temporary folder, not the
// checkout, so the workflows' git status sees only README.md and docs/.
//
// GitHub's release list can lag a just-published Release by several seconds, so with --wait-for-prs it lists again until every named PR has
// a Release, every 2s for up to half a minute. A PR whose release failed never appears; after the last attempt the run writes what is
// listed, and the next run picks up the rest.
//
// The repository is --repo, else REPOSITORY, else GITHUB_REPOSITORY. ANTHROPIC_API_KEY and ANTHROPIC_MODEL are read as release_post.cs
// reads them. Exit 0 with what it wrote logged on stdout; 1 when a gh call or a file write fails (the exception on stderr); 2 on a usage
// error (argparse's, as backfill_blog_posts.py gave: --pr-numbers and --wait-for-prs must be comma-separated PR numbers, read as
// str.isdigit() and int() read them, an option may be given by a unique prefix of its name, and --repo is required when neither variable
// names a repository). The standard library only. backfill_blog_posts.py, beside it, is the same script in Python, kept for one Rollout:
// running it in the workflows instead reverts the port.
using Baseline;

return Script.Run((stdout, stderr) => BackfillBlogPosts.Command(args, stdout, stderr));
