#:include Gh.cs
#:include HtmlEntities.cs
#:include ReleasePost.cs
#:property PublishAot=false
// Write the release blog post for a merged PR and refresh the release tables. ReleasePost.cs holds the logic; Gh.cs the argument parser
// and Script.Run.
//
// Usable for any past Release/PR pair, after a build (dotnet build .github/scripts/release_post.cs):
//
//     dotnet run --no-build --file .github/scripts/release_post.cs -- --pr 28 --tag v0.0.32
//
// backfill_blog_posts.cs, which the docs job in .github/workflows/release.yml and .github/workflows/backfill-blog-posts.yml run, writes
// each post and the tables with the same library.
//
// It writes docs/blogs/{merged-date}-pr-{n}-{slug}.md, updates the docs/blogs/README.md index, the RELEASES_START/END table in README.md
// (copied to docs/README.md with its relative links rebased), and in the GitHub Pages site docs/index.html the RELEASES_HTML releases table
// and the BLOGS_HTML blog post cards.
//
// GitHub data comes from the gh CLI (GH_TOKEN). The repository is --repo, else REPOSITORY, else GITHUB_REPOSITORY. When ANTHROPIC_API_KEY
// is set, the post opens with a short summary written by Claude (model from ANTHROPIC_MODEL); a key or model of only blanks counts as
// unset. Any API failure only drops the summary; it never fails the release.
//
// Exit 0 with what it wrote logged on stdout; 1 when a gh call or a file write fails (the exception on stderr); 2 on a usage error
// (argparse's, as release_post.py gave: --pr and --tag are required, --pr must be an int, an option may be given by a unique prefix of its
// name, and --repo is required when neither variable names a repository). The standard library only.
// release_post.py, beside it, is the same script in Python, kept for one Rollout.
using Baseline;

return Script.Run((stdout, stderr) => ReleasePost.Command(args, stdout, stderr));
