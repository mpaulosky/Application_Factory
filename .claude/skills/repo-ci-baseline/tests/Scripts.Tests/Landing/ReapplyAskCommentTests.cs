// reapply.sh's ask comment against Landing.cs's. Not a moved test: reapply.test.sh held the two equal before the port, so LandingTests
// keeps only test_landing.py's tests and its count can be checked against that file's.
public class ReapplyAskCommentTests
{
	// reapply.sh posts the same record when a re-Apply PR's request doesn't register (mpaulosky/dotfiles#384): its asked_marker and
	// asked_comment, as bash reads the two lines, word for word.
	[Fact]
	public async Task Reapply_sh_posts_the_same_ask_comment()
	{
		var cancel = TestContext.Current.CancellationToken;
		var start = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var arg in new[] { "-c", "eval \"$(grep -E '^(asked_marker|asked_comment)=' \"$1\")\" && printf %s \"$asked_comment\"", "_", Skill.Path("reapply.sh") })
			start.ArgumentList.Add(arg);
		using var run = System.Diagnostics.Process.Start(start)!;
		var err = run.StandardError.ReadToEndAsync(cancel);
		var comment = await run.StandardOutput.ReadToEndAsync(cancel);
		await run.WaitForExitAsync(cancel);

		Assert.True(run.ExitCode == 0, await err);
		Assert.Equal(Landing.CopilotAskedComment, comment);
	}
}
