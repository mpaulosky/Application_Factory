#:include Gh.cs
#:include HtmlEntities.cs
#:include ReleasePost.cs
#:include ReleaseQueue.cs
#:property PublishAot=false
// Print, as a JSON list, the merged PRs the release workflow still has to release. ReleaseQueue.cs holds the logic, on ReleasePost.cs's
// gh calls; Gh.cs the argument parser and Script.Run.
//
// Called by the plan job in .github/workflows/release.yml before it releases anything, after a step that builds it (dotnet build
// .github/scripts/release_queue.cs), so stdout is the JSON alone even on a cold build:
//
//     dotnet run --no-build --file .github/scripts/release_queue.cs -- --repo owner/name [--pr 42]
//
// Every release run shares one concurrency group, and GitHub keeps only the newest pending run in a group, cancelling the one it replaces.
// So a run doesn't release only the PR that started it: it releases every merged PR that is still owed a release, oldest merge first. A PR
// whose run was cancelled or failed is picked up by the next one, and versions follow merge order. A manual run (--pr) goes through the
// same list, so a newer PR can't be released ahead of an older one.
//
// The cutoff is the newest published GitHub Release (a vMAJOR.MINOR.PATCH tag whose body names its "Source PR: #n"), never just the newest
// git tag: a run that pushed its tag but failed before the Release was published hasn't released that PR. A PR is owed a release when it
// merged into main after the cutoff PR, its title has no [skip-release] marker, Dependabot didn't open it, no Release names it, and its
// merge commit isn't already inside the cutoff Release's tag. The last rule keeps PRs merged before release automation existed out of the
// list. With no Release at all, only the triggering PR is queued.
//
// A Dependabot PR never gets a Release of its own: a dependency bump isn't worth a Release and blog post. It's folded into the next one
// instead. The next Release's tag is on a later commit, so it contains the bump, and its generated notes list the bump's PR among the
// changes since the previous Release.
//
// Exit 0 with the JSON list on stdout ([3, 2, 4], or []) and the cutoff and queue explained on stderr; 1 when a gh or git call fails, or a
// listed PR's merge commit isn't on the fetched main (the exception on stderr; a retry of the run fixes the second); 2 on a usage error
// (argparse's, as release_queue.py gave: --repo is required, --pr must be an int, an option may be given by a unique prefix of its name).
// The standard library only; git and the gh CLI do the rest. release_queue.py, beside it, is the same script in Python, kept for one
// Rollout: running it in release.yml instead reverts the port.
using Baseline;

return Script.Run((stdout, stderr) => ReleaseQueue.Run(args, stdout, stderr));
