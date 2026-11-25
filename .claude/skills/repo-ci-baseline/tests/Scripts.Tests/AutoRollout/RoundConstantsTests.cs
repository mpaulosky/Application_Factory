// Tests for what the round's C# (github/AutoRolloutRound.cs, AutoRolloutReview.cs, AutoRolloutGroups.cs) shares with the files on the other
// side: xUnit copies of github/tests/test_auto_rollout.py's tests of the ledger's marker and refusals, the gate's words and the documented
// limits (ADR 0017's Update for mpaulosky/Application_Factory#157), with the same cases and assertions.
[Collection(nameof(RoundCollection))]
public sealed class RoundConstantsTests
{
	// reapply.sh's NAME="value" line, as the round's constants copy it.
	static string ReapplyConstant(string name) =>
		File.ReadAllLines(Skill.Path("reapply.sh")).First(line => line.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1].Trim('"');

	[Fact]
	public void The_session_marker_is_the_ledgers_own()
	{
		var ledger = File.ReadAllLines(Skill.Path("template", "owned", ".sandcastle", "lib", "review-ledger.mts"));
		var advisories = ledger.First(line => line.Contains("left for a follow-up:", StringComparison.Ordinal));
		Assert.Contains($"\"{ReviewSession.SessionMarker}\", \"\"]", advisories, StringComparison.Ordinal);
	}

	[Fact]
	public void Reapply_sh_reads_the_refusals_in_the_ledgers_and_the_gates_own_words()
	{
		// reapply.sh tells the two refusals apart by the text checkReview prints at a line's start, on stderr; a reworded message would
		// turn both into a plain failed push.
		var ledger = File.ReadAllText(Skill.Path("template", "owned", ".sandcastle", "lib", "review-ledger.mts"));
		var check = ledger[ledger.IndexOf("export function checkReview(", StringComparison.Ordinal)..];
		check = check[..check.IndexOf("\n}\n", StringComparison.Ordinal)];
		Assert.Contains($"message: `{ReapplyConstant("no_review_text")}", check, StringComparison.Ordinal);
		Assert.Contains($"`${{blocking.length}} {ReapplyConstant("findings_wait_text")}", check, StringComparison.Ordinal);
		Assert.Contains("process.stderr.write(`${printable(text)}\\n`)", ledger, StringComparison.Ordinal); // fail() writes to stderr, which reapply.sh reads
		var command = ledger[ledger.IndexOf("case \"check\": {", StringComparison.Ordinal)..];
		command = command[..command.IndexOf("\n      }\n", StringComparison.Ordinal)];
		Assert.Contains("if (!result.ok) return fail(result.message);", command, StringComparison.Ordinal); // the check's refusal goes through fail()
		var gate = File.ReadAllText(Skill.Path("template", "owned", "scripts", "gate.sh"));
		Assert.Contains($"step \"{ReapplyConstant("review_step")}\"", gate, StringComparison.Ordinal); // reapply.sh asks only when the gate stopped at this step
		Assert.Contains("step() { echo -e \"\\n${CYAN}▶ $1${RESET}\"; }", gate, StringComparison.Ordinal); // reapply.sh reads this header form
		// reapply.sh asks the check as the gate does.
		Assert.Contains("scripts/review.sh check \"$BASE\"", gate, StringComparison.Ordinal);
		Assert.Contains("BASE=\"$(git merge-base HEAD origin/main", gate, StringComparison.Ordinal);
	}

	[Fact]
	public void The_documented_limits()
	{
		// The ADR: a session that ends without recording is run once more, then the round stops; a session stops after three hours.
		// LookupTries reads LookupWaitSeconds apart: 40 s of waiting before a stop.
		Assert.Equal(2, AutoRound.MaxReviews);
		Assert.Equal(180, ReviewSession.ReviewTimeoutMinutes);
		Assert.Equal((5, 10), (AutoRound.LookupTries, AutoRound.LookupWaitSeconds));
	}

	// This part's lines of test_the_sandbox_and_session_lists_word_for_word (SandboxTests and ReviewRecordTests hold the rest): RECORD_LEFT,
	// and the group's wait, which the record's copy of it equals.
	[Fact]
	public void The_sandbox_and_session_lists_word_for_word()
	{
		Assert.Equal(258, ReviewSession.RecordLeft);
		// Above every exit code, so a session's can't read as one.
		Assert.True(ReviewSession.RecordLeft > 255);
		Assert.Equal((10, 10), (Groups.GroupWaitSeconds, ReviewRecord.GroupWaitSeconds));
	}
}
