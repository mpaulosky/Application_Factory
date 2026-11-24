using System.Diagnostics;
using System.Text;
using System.Text.Json;

// Repos.cs reads repos.txt as settings.py's read_repo_list() does, which rollout.py and the rest of the Python still use: on the Fleet's
// own file, and on files with the edge cases either could read differently, each run through both.
public class ReposTests
{
	// read_repo_list()'s {repo: scope} for a folder holding repos.txt, as [[repo, scope], ...]: settings.py is copied beside it and imported.
	static List<KeyValuePair<string, string>> PythonRead(string folder)
	{
		File.Copy(Skill.Path("github", "settings.py"), System.IO.Path.Combine(folder, "settings.py"), overwrite: true);
		var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-I", "-c", "import json, sys; sys.path.insert(0, sys.argv[1]); import settings; print(json.dumps(list(settings.read_repo_list().items())))", folder })
			start.ArgumentList.Add(arg);
		start.Environment["PYTHONUTF8"] = "1";
		start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
		using var run = Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = run.StandardOutput.ReadToEnd();
		run.WaitForExit();
		Assert.True(run.ExitCode == 0, $"python3 failed: {err.Result}");
		return [.. JsonSerializer.Deserialize<string[][]>(output)!.Select(pair => KeyValuePair.Create(pair[0], pair[1]))];
	}

	static string Folder(byte[] reposTxt)
	{
		var folder = Directory.CreateTempSubdirectory("repos-test.").FullName;
		File.WriteAllBytes(System.IO.Path.Combine(folder, "repos.txt"), reposTxt);
		return folder;
	}

	[Fact]
	public void The_fleets_repos_txt_reads_as_settings_py_reads_it()
	{
		var folder = Folder(File.ReadAllBytes(Skill.Path("github", "repos.txt")));
		try
		{
			var read = Repos.Read(folder).ToList();
			Assert.NotEmpty(read);
			Assert.Equal(PythonRead(folder), read);
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	public static TheoryData<string, string> Files => new()
	{
		{ "comments, blank lines and a scope", "# The Fleet\n\nowner/app\nowner/factory factory # the Factory\n  owner/dots   repo-settings-only\n" },
		{ "a repo listed twice keeps its first place and its last scope", "owner/a\nowner/b\nowner/a factory\n" },
		{ "CRLF and a lone CR", "owner/a\r\nowner/b factory\rowner/c\r\n" },
		{ "a byte-order mark, kept as part of the first word", "\ufeffowner/a\nowner/b\n" },
		{ "tabs, form feeds and the other line breaks splitlines() knows", "owner/a\tfactory\fowner/b\vowner/c\u001cowner/d\u001downer/e\u001eowner/f\u0085owner/g\u2028owner/h\u2029owner/i\n" },
		{ "a unit separator splits words but not lines", "owner/a\u001ffactory\n" },
		{ "a no-break space and an ideographic space split words", "owner/a\u00a0factory\nowner/b\u3000repo-settings-only\n" },
		{ "a word after the scope is ignored", "owner/a factory extra\n" },
		{ "a # inside a word ends the line", "owner/a#comment factory\n" },
		{ "no file content at all", "" },
	};

	[Theory]
	[MemberData(nameof(Files))]
	public void An_edge_case_reads_as_settings_py_reads_it(string what, string text)
	{
		var folder = Folder(Encoding.UTF8.GetBytes(text));
		try
		{
			Assert.True(PythonRead(folder).SequenceEqual(Repos.Read(folder)), what);
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	[Fact]
	public void Bytes_that_arent_utf8_fail_as_read_text_fails()
	{
		var folder = Folder([(byte)'o', 0xff, (byte)'\n']);
		try
		{
			Assert.ThrowsAny<DecoderFallbackException>(() => Repos.Read(folder));
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	[Fact]
	public void With_no_folder_and_no_script_file_it_says_so()
	{
		var error = Assert.Throws<InvalidOperationException>(() => Repos.Read());
		Assert.Contains("repos.txt", error.Message);
	}
}
