using System.Diagnostics;
using System.Text.Json;

// Copilot is its own logins and nothing else (#155).
//
// Landing.Copilot (status.sh, land.sh) and Land.CopilotJq (land.sh's read-back of the request) each decide whether a login is
// Copilot's. A login that merely contains "copilot" (not-copilot-bot, my-copilot) taken for it could review a head for Copilot and arm
// the merge. This runs both on the same logins.
public class CopilotLoginTests
{
	static readonly (string Login, bool IsCopilot)[] Logins =
	[
		// Copilot's names across the reviews API, GraphQL and the requested-reviewer and timeline APIs, in any case, with or without [bot].
		("copilot-pull-request-reviewer", true),
		("copilot-pull-request-reviewer[bot]", true),
		("Copilot", true),
		("copilot", true),
		("Copilot[bot]", true),
		("not-copilot-bot", false),
		("my-copilot", false),
		("copilot-x", false),
		("copilot-pull-request-reviewer-fake", false),
		("evil-copilot-pull-request-reviewer[bot]", false),
		("copilot[bot]x", false),
		("", false),
	];

	public static TheoryData<string, bool> Cases() => [.. Logins.Select(item => (item.Login, item.IsCopilot))];

	[Theory]
	[MemberData(nameof(Cases))]
	public void Landing_copilot_matches_only_copilots_own_logins(string login, bool isCopilot) =>
			Assert.Equal(isCopilot, Landing.Copilot.IsMatch(login));

	// \z, not $: .NET's $ would match before a final newline.
	[Fact]
	public void Landing_copilot_refuses_a_login_with_a_trailing_newline() => Assert.DoesNotMatch(Landing.Copilot, "copilot\n");

	[Fact]
	public void The_read_back_jq_counts_only_copilots_own_logins()
	{
		var jq = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
				.Select(dir => Path.Combine(dir, "jq")).FirstOrDefault(File.Exists);
		if (jq is null)
		{
			Assert.Fail("jq isn't installed, so Land.CopilotJq can't be checked");
			return;
		}
		foreach (var (login, isCopilot) in Logins)
		{
			// The login as a requested reviewer (a Bot or User's login, a team's name) and as the author of a review of the head.
			object[] views =
			[
				new { headRefOid = "h", reviewRequests = new[] { new { login } }, reviews = Array.Empty<object>() },
				new { headRefOid = "h", reviewRequests = new[] { new { name = login } }, reviews = Array.Empty<object>() },
				new { headRefOid = "h", reviewRequests = Array.Empty<object>(), reviews = new[] { new { commit = new { oid = "h" }, author = new { login } } } },
			];
			foreach (var view in views)
				Assert.True(Run(jq, JsonSerializer.Serialize(view)) == (isCopilot ? "1" : "0"), $"CopilotJq on {login}: {JsonSerializer.Serialize(view)}");
		}
	}

	static string Run(string jq, string input)
	{
		var start = new ProcessStartInfo(jq) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		start.ArgumentList.Add(Land.CopilotJq);
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		run.StandardInput.Write(input);
		run.StandardInput.Close();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"jq failed: {err.Result}");
		return output.Result.Trim();
	}
}
