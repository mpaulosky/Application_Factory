using System.Text;
using System.Text.Json;

// Repos.cs reads repos.txt as settings.py's read_repo_list() read it, the reader the Python scripts shared until the last of them, rollout.py,
// was ported (mpaulosky/Application_Factory#122): on the Fleet's own file, and on files with the edge cases either could read differently,
// each against what read_repo_list() gave for it, recorded from it before it was deleted.
public class ReposTests
{
	static string Folder(byte[] reposTxt)
	{
		var folder = Directory.CreateTempSubdirectory("repos-test.").FullName;
		File.WriteAllBytes(System.IO.Path.Combine(folder, "repos.txt"), reposTxt);
		return folder;
	}

	// The pairs of json.dumps(list(read_repo_list().items())).
	static List<KeyValuePair<string, string>> Pairs(string json) => [.. JsonSerializer.Deserialize<string[][]>(json)!.Select(pair => KeyValuePair.Create(pair[0], pair[1]))];

	[Fact]
	public void The_fleets_repos_txt_reads_as_settings_py_reads_it()
	{
		// The Fleet's file is plain: ASCII lines of a name and maybe a scope, # comments and blank lines, which read_repo_list() read as
		// below, so its reading is checked against that and the file against being plain.
		var bytes = File.ReadAllBytes(Skill.Path("github", "repos.txt"));
		Assert.All(bytes, b => Assert.True(b is (byte)'\n' or >= 0x20 and < 0x7f, $"repos.txt holds byte {b}, so the plain reading below isn't read_repo_list()'s"));
		// A dict: a repeated name keeps its first place and its last scope (a Dictionary with no removal keeps that order too).
		Dictionary<string, string> want = [];
		foreach (var line in Encoding.ASCII.GetString(bytes).Split('\n'))
		{
			var words = line.Split('#')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0)
				continue;
			want[words[0]] = words.Length > 1 ? words[1] : "full";
		}
		var folder = Folder(bytes);
		try
		{
			var read = Repos.Read(folder).ToList();
			Assert.NotEmpty(read);
			Assert.Equal(want.ToList(), read);
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	public static TheoryData<string, string, string> Files => new()
	{
		{ "comments, blank lines and a scope", "# The Fleet\n\nowner/app\nowner/factory factory # the Factory\n  owner/dots   repo-settings-only\n",
			"""[["owner/app","full"],["owner/factory","factory"],["owner/dots","repo-settings-only"]]""" },
		{ "a repo listed twice keeps its first place and its last scope", "owner/a\nowner/b\nowner/a factory\n", """[["owner/a","factory"],["owner/b","full"]]""" },
		{ "CRLF and a lone CR", "owner/a\r\nowner/b factory\rowner/c\r\n", """[["owner/a","full"],["owner/b","factory"],["owner/c","full"]]""" },
		{ "a byte-order mark, kept as part of the first word", "\ufeffowner/a\nowner/b\n", "[[\"\ufeffowner/a\",\"full\"],[\"owner/b\",\"full\"]]" },
		{ "tabs, form feeds and the other line breaks splitlines() knows", "owner/a\tfactory\fowner/b\vowner/c\u001cowner/d\u001downer/e\u001eowner/f\u0085owner/g\u2028owner/h\u2029owner/i\n",
			"""[["owner/a","factory"],["owner/b","full"],["owner/c","full"],["owner/d","full"],["owner/e","full"],["owner/f","full"],["owner/g","full"],["owner/h","full"],["owner/i","full"]]""" },
		{ "a unit separator splits words but not lines", "owner/a\u001ffactory\n", """[["owner/a","factory"]]""" },
		{ "a no-break space and an ideographic space split words", "owner/a\u00a0factory\nowner/b\u3000repo-settings-only\n",
			"""[["owner/a","factory"],["owner/b","repo-settings-only"]]""" },
		{ "a word after the scope is ignored", "owner/a factory extra\n", """[["owner/a","factory"]]""" },
		{ "a # inside a word ends the line", "owner/a#comment factory\n", """[["owner/a","full"]]""" },
		{ "no file content at all", "", "[]" },
	};

	[Theory]
	[MemberData(nameof(Files))]
	public void An_edge_case_reads_as_settings_py_reads_it(string what, string text, string python)
	{
		var folder = Folder(Encoding.UTF8.GetBytes(text));
		try
		{
			Assert.True(Pairs(python).SequenceEqual(Repos.Read(folder)), what);
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
