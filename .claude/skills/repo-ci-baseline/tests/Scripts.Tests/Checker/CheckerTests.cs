// Checker.cs, which the two checkers in tests/ share: a crash is exit 2, never 1 ("found") or 0, and Python's text rules the ports keep.
// The checkers' parity suites (tests/check-issue-refs.test.sh, tests/check-git-status.test.sh) hold the rules to the Python on files.
public class CheckerTests
{
	[Theory]
	[InlineData("check-issue-refs.cs")]
	[InlineData("check-git-status.cs")]
	public void A_crash_in_the_scan_is_exit_2_saying_it_did_not_check(string name)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var code = Checker.Run(name, (_, _) => throw new DivideByZeroException("boom"), stdout, stderr);
		Assert.Equal(2, code);
		Assert.Equal("", stdout.ToString());
		Assert.Equal($"{name} failed without checking: DivideByZeroException: boom\n", stderr.ToString());
	}

	[Fact]
	public void A_crash_in_the_git_status_scan_is_exit_2()
	{
		// A finding whose printing throws (stdout gone): the throw is caught as exit 2, not left to read as exit 1, "found".
		var root = Directory.CreateTempSubdirectory("checker-test.").FullName;
		File.WriteAllText(Path.Combine(root, "a.sh"), "git status --porcelain\n");
		var code = Checker.Run("check-git-status.cs", (o, e) => GitStatusGuards.Run([root], new ThrowingWriter(), e), new StringWriter(), new StringWriter());
		Assert.Equal(2, code);
	}

	sealed class ThrowingWriter : StringWriter
	{
		public override void Write(string? value) => throw new IOException("stdout is gone");
	}

	[Fact]
	public void Splitlines_ends_a_line_where_python_does()
	{
		Assert.Equal(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", ""], Checker.SplitLines("a\nb\rc\r\nd\ve\ff\x1cg\x1dh\x1ei\u0085j\u2028k\u2029\n"));
		Assert.Equal([], Checker.SplitLines(""));
		Assert.Equal(["a\u001Fb"], Checker.SplitLines("a\u001Fb"));
	}

	[Fact]
	public void Strip_takes_what_python_calls_space()
	{
		Assert.Equal("a b", Checker.Strip("\x1c\x1f\t\u00A0\u3000a b\u2028 \x1d"));
		Assert.Equal("\uFEFFa", Checker.Strip("\uFEFFa"));
	}

	[Fact]
	public void Code_point_order_puts_an_astral_character_after_the_bmp()
	{
		Assert.True(Checker.CompareCodePoints("\U0001F600", "\uFFFD") > 0);
		Assert.True(Checker.CompareCodePoints("a", "a-b") < 0);
		Assert.True(Checker.CompareCodePoints("a-b", "a.md") < 0);
		Assert.Equal(0, Checker.CompareCodePoints("x", "x"));
	}

	[Fact]
	public void Fold_astral_keeps_a_digit_a_digit_and_a_letter_a_word_character()
	{
		Assert.Equal("#0", Checker.FoldAstral("#\U0001D7D3"));
		Assert.Equal("a#5", Checker.FoldAstral("\U0001D400#5"));
		Assert.Equal("\uFFFD x", Checker.FoldAstral("\U0001F600 x"));
		Assert.Equal("plain", Checker.FoldAstral("plain"));
	}
}
