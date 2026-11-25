using System.Diagnostics;

using static SandboxFixtures;

// Not moved tests: where .NET reads differently from Python and the port must read as Python does (Copilot's review of
// mpaulosky/Application_Factory#193). A word character is Python's (a letter, a number or _), a .. in realpath is taken after the links
// before it, and RunQuiet's time limit bounds its output as well as the process.
public class PortReadingTests
{
	// U+0301, a combining mark: a word character to .NET's \w, not to Python's re.
	const string Mark = "\u0301";

	[Fact]
	public void A_mark_after_a_dollar_names_no_variable_so_the_password_is_found()
	{
		Assert.Matches(AutoRollout.HelperSecret, $"!f() {{ echo password=${Mark}hunter2; }}; f");
		Assert.Matches(AutoRollout.HelperSecret, $"!my-helper --password=${Mark}hunter2");
		Assert.Matches(AutoRollout.TokenShape, $"Authorization: Bearer ${Mark}abcdefgh");
	}

	[Fact]
	public void A_word_boundary_is_one_Python_reads()
	{
		Assert.Matches(AutoRollout.TokenShape, $"{Mark}ghp_{new string('a', 20)}");
		Assert.Matches(AutoRollout.UrlUserinfo, $"{Mark}https://ghp_abc@github.com/o/r");
		Assert.Matches(AutoRollout.HelperSecret, $"!my-helper --file{Mark}");
		Assert.DoesNotMatch(AutoRollout.HelperSecret, "!my-helper --filename x");
		Assert.DoesNotMatch(AutoRollout.HelperSecret, "!f() { echo password=$PW; }; f");
	}

	[Fact]
	public void A_parent_after_a_link_is_the_link_targets_parent()
	{
		var tmp = TempDir();
		var version = Path.Combine(tmp, "opt", "node", "version");
		Directory.CreateDirectory(version);
		Directory.CreateDirectory(Path.Combine(tmp, "opt", "node", "bin"));
		Directory.CreateDirectory(Path.Combine(tmp, "h"));
		File.CreateSymbolicLink(Path.Combine(tmp, "h", "current"), version);
		Assert.Equal(Path.Combine(tmp, "opt", "node", "bin", "node"), PyLib.RealPath(Path.Combine(tmp, "h", "current", "..", "bin", "node")));
	}

	[Fact]
	public void A_child_left_holding_the_output_is_bounded_by_the_time_limit()
	{
		var clock = Stopwatch.StartNew();
		var ran = AutoRollout.RunQuiet(["/bin/sh", "-c", "sleep 30 & echo started"], seconds: 2);
		clock.Stop();
		Assert.Equal(124, ran.Code);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
	}
}
