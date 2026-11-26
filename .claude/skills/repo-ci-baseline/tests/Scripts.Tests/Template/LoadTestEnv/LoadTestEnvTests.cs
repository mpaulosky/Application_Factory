namespace Baseline.Tests;

// The Template's .github/scripts/LoadTestEnv.cs: test_load_test_env.py's tests, one method each under its name (ADR 0017's rule), with
// the same cases and assertions. In Baseline's namespace, so Script and Arguments are the Template's, not github/'s.
public sealed class LoadTestEnvTests : IDisposable
{
	// pytest's tmp_path: a folder of the test's own, removed when it ends.
	readonly string tmp = Directory.CreateTempSubdirectory("load-test-env.").FullName;

	public void Dispose() => Directory.Delete(tmp, true);

	// main(["--output", <a file in tmp>]) with TEST_ENV set to testEnv, or unset: (exit code, the output's text, or "" when none was
	// written, the log).
	(int Code, string Output, string Log) Run(string? testEnv = null)
	{
		var output = Path.Join(tmp, "github_env");
		var log = new StringWriter();
		var code = LoadTestEnv.Run(["--output", output], log, new StringWriter(), name => name == "TEST_ENV" ? testEnv : null);
		return (code, File.Exists(output) ? File.ReadAllText(output) : "", log.ToString());
	}

	[Fact]
	public void Parse_reads_name_value_lines_and_skips_blanks_and_comments()
	{
		var text = "# MongoDB\nMongoDB__ConnectionString=mongodb://u:p@host/db?x=1\n\n  Auth0__Domain = example.test  \n";

		Assert.Equal([("MongoDB__ConnectionString", "mongodb://u:p@host/db?x=1"), ("Auth0__Domain", "example.test")], LoadTestEnv.Parse(text));
	}

	[Fact]
	public void Parse_reads_crlf_text() => Assert.Equal([("A", "1"), ("B", "2")], LoadTestEnv.Parse("A=1\r\nB=2\r\n"));

	[Theory]
	[InlineData("no-separator")]
	[InlineData("=value")]
	[InlineData("1BAD=value")]
	[InlineData("has space=value")]
	[InlineData("a-b=value")]
	public void Parse_rejects_a_line_that_isnt_name_value(string line)
	{
		var error = Assert.Throws<FormatException>(() => LoadTestEnv.Parse($"OK=1\n{line}\n"));

		Assert.Contains("line 2 isn't NAME=value", error.Message);
	}

	[Theory]
	[InlineData("GITHUB_TOKEN")]
	[InlineData("RUNNER_TEMP")]
	public void Parse_rejects_reserved_names(string name)
	{
		var error = Assert.Throws<FormatException>(() => LoadTestEnv.Parse($"{name}=x"));

		Assert.Contains("reserved", error.Message);
	}

	[Fact]
	public void Parse_accepts_lowercase_names_that_only_look_reserved() =>
			// Linux names are distinct by case, and the runner blocks no prefix.
			Assert.Equal([("github_client_id", "x"), ("runner_name", "y")], LoadTestEnv.Parse("github_client_id=x\nrunner_name=y"));

	[Theory]
	[InlineData("NODE_OPTIONS")]
	[InlineData("node_options")]
	public void Parse_rejects_node_options_the_runner_wont_export(string name)
	{
		var error = Assert.Throws<FormatException>(() => LoadTestEnv.Parse($"{name}=--max-old-space-size=4096"));

		Assert.Contains("line 1 sets NODE_OPTIONS", error.Message);
	}

	[Fact]
	public void Main_masks_each_value_before_exporting_it()
	{
		var (code, output, log) = Run("A=secret-one\nB=secret-two\n");

		Assert.Equal(0, code);
		Assert.Equal("A=secret-one\nB=secret-two\n", output);
		Assert.Contains("::add-mask::secret-one", log);
		Assert.Contains("::add-mask::secret-two", log);
		Assert.Contains("Exported 2 test secrets: A, B", log);
	}

	[Fact]
	public void Escape_data_encodes_what_the_runner_decodes() => Assert.Equal("a%2525b%25c%0Dd%0Ae", LoadTestEnv.EscapeData("a%25b%c\rd\ne"));

	[Fact]
	public void Main_escapes_a_value_before_masking_it()
	{
		// The runner percent-decodes "::add-mask::" data, so "%25" must reach it as "%2525".
		var (code, output, log) = Run("TOKEN=abc%25def\n");

		Assert.Equal(0, code);
		Assert.Contains("::add-mask::abc%2525def", log);
		Assert.Equal("TOKEN=abc%25def\n", output);
	}

	[Fact]
	public void Main_doesnt_mask_an_empty_value()
	{
		// "::add-mask::" with nothing after it would be a malformed command.
		var (code, output, log) = Run("EMPTY=\n");

		Assert.Equal(0, code);
		Assert.Equal("EMPTY=\n", output);
		Assert.DoesNotContain("add-mask", log);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("\n# only a comment\n")]
	public void Main_exports_nothing_without_a_secret(string? testEnv)
	{
		var (code, output, log) = Run(testEnv);

		Assert.Equal(0, code);
		Assert.Equal("", output);
		Assert.Contains("No TEST_ENV secret", log);
	}

	[Fact]
	public void Main_fails_on_a_malformed_secret_without_printing_values()
	{
		var (code, output, log) = Run("A=hunter2\nnot a pair hunter3\n");

		Assert.Equal(1, code);
		Assert.Equal("", output);
		Assert.Contains("line 2", log);
		Assert.DoesNotContain("hunter2", log);
		Assert.DoesNotContain("hunter3", log);
	}

	// The port's own: what the parity suite can't reach through the command line.

	[Fact]
	public void Main_reads_test_env_from_the_process_environment_by_default()
	{
		var output = Path.Join(tmp, "github_env");
		var log = new StringWriter();
		var before = Environment.GetEnvironmentVariable("TEST_ENV");
		Environment.SetEnvironmentVariable("TEST_ENV", "FROM_PROCESS=1");
		try
		{
			Assert.Equal(0, LoadTestEnv.Run(["--output", output], log, new StringWriter()));
		}
		finally
		{
			Environment.SetEnvironmentVariable("TEST_ENV", before);
		}

		Assert.Equal("FROM_PROCESS=1\n", File.ReadAllText(output));
	}

	[Fact]
	public void Main_refuses_no_output_before_reading_the_secret()
	{
		var stderr = new StringWriter();
		var read = false;

		var code = LoadTestEnv.Run([], new StringWriter(), stderr, _ => { read = true; return "A=1"; });

		Assert.Equal(2, code);
		Assert.False(read);
		Assert.EndsWith("load_test_env.cs: error: the following arguments are required: --output\n", stderr.ToString());
	}
}
