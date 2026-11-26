#:include ../../template/owned/.github/scripts/Gh.cs
#:include ../../template/owned/.github/scripts/HtmlEntities.cs
#:include ../../template/owned/.github/scripts/ReleasePost.cs
#:property PublishAot=false
// The renderer oracle's way into the Template's ReleasePost.cs (ADR 0017's Update for mpaulosky/Application_Factory#164), so the grid judges
// the C# as it judges release_post.py. renderers.py builds it, then runs it with --no-build. It reads one JSON object on stdin,
//
//     {"commits": [[commit, ...], ...], "readmes": [text, ...]}
//
// where each commit is the GitHub API's (its "sha" and "commit": {"message"}), and prints {"commits": [...], "readmes": [...]}: the Commits
// section RenderCommits writes for each list, and RebaseReadmeLinks of each text. Exit 0, or 1 with the error on stderr.
using System.Text;
using System.Text.Json.Nodes;

using Baseline;

return Script.Run((stdout, stderr) =>
{
	using StreamReader stdin = new(Console.OpenStandardInput(), new UTF8Encoding(false, true));
	var input = JsonNode.Parse(stdin.ReadToEnd()) ?? throw new InvalidDataException("stdin holds no JSON object");
	JsonObject output = new()
	{
		["commits"] = new JsonArray([.. input["commits"]!.AsArray().Select(commits => (JsonNode?)ReleasePost.RenderCommits([.. commits!.AsArray()]))]),
		["readmes"] = new JsonArray([.. input["readmes"]!.AsArray().Select(text => (JsonNode?)ReleasePost.RebaseReadmeLinks((string)text!))]),
	};
	stdout.Write(output.ToJsonString());
	return 0;
});
