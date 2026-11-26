#:include Gh.cs
#:include HtmlEntities.cs
#:include ReleasePost.cs
#:property PublishAot=false
// The entry file of ReleasePost.cs, release_post.py's logic in C#, which lands ahead of its cut-over (ADR 0017's library first, in the
// Application Factory). Until the cut-over, release.yml and backfill-blog-posts.yml run release_post.py, and this only says so: it writes
// that to stderr and exits 2. It is here so the gate builds the library through it with warnings as errors; nothing calls it. The cut-over
// gives it release_post.py's command line (--pr, --tag, --repo) and contract.
Console.Error.WriteLine("release_post.cs: not cut over yet; release.yml and backfill-blog-posts.yml run .github/scripts/release_post.py");
return 2;
