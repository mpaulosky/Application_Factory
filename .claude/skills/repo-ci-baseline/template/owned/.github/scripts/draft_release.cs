#:include Gh.cs
#:include DraftRelease.cs
#:property PublishAot=false
// Print the body to publish a draft Release with, or fail if the draft names another PR. DraftRelease.cs holds the logic; Gh.cs the
// argument parser and Script.Run.
//
// Called by the release job in .github/workflows/release.yml when the tag it reserved already has a draft Release, after a step that
// builds it (dotnet build .github/scripts/draft_release.cs), so stdout is the body alone even on a cold build:
//
//     dotnet run --no-build --file .github/scripts/draft_release.cs -- --pr 42 --notes-file notes.txt < draft-body.txt
//
// A draft doesn't count as released anywhere in the workflow, so the release job publishes it rather than leave the PR owed forever. The
// planner and the ordering guard know a Release by its "Source PR: #n" marker, so:
//
// - a draft whose markers all name this PR, including one on a line of its own, is published as it is;
// - a draft with no marker, or one only inside a longer line, gets the release notes appended, which adds a line of its own;
// - a draft naming any other PR is inconsistent, since publishing it would give the Release two sources. That exits 1, with the error on
//   stderr, for a person to sort out.
//
// Markers count anywhere in a line, the same pattern release_post's source_pr_of() uses, so every parser agrees on which PRs a body names.
// Their number is any run of Unicode decimal digits, read by its value, as Python's \d and int() read it: 042 and Arabic-Indic digits
// name PR 42, but aren't its marker, which is ASCII digits with no leading zero. --pr is read as int() reads it too (blanks, a sign and
// underscores between digits).
//
// The draft is read from stdin as its bytes (a byte that isn't UTF-8 is written back as itself, and CRLF line endings are kept); the notes
// file is read as UTF-8, with its \r\n and lone \r read as \n and its trailing newlines dropped.
//
// Exit 0 with the body on stdout; 1 when the draft names another PR (the ::error:: on stderr, naming --tag, or "the draft" without one),
// or when the notes file can't be read (the exception on stderr); 2 on a usage error (argparse's, as draft_release.py gave: --pr and
// --notes-file are required, --pr must be an int, an option may be given by a unique prefix of its name). The standard library only.
// draft_release.py, beside it, is the same script in Python, kept for one Rollout: running it in release.yml instead reverts the port.
using Baseline;

return Script.Run((stdout, stderr) =>
		DraftRelease.Run(args, () => SurrogateEscape.Decode(ReadAll(Console.OpenStandardInput())), stdout, stderr));

static byte[] ReadAll(Stream stream)
{
	using var bytes = new MemoryStream();
	stream.CopyTo(bytes);
	return bytes.ToArray();
}
