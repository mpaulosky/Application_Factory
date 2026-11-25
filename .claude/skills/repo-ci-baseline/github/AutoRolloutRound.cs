// The --auto round (auto_rollout.py's gh_pr to run, ADR 0016): reapply.sh on each repo with a headless review each time its gate asks for
// one (Drive), TicketManager's re-Apply PR landed before the rest start and the rest's together (Round), after the preflight and under the
// lock (Run). How a child runs is AutoRolloutGroups.cs's, and the review session AutoRolloutReview.cs's.
using System.Text;

// How one repo's reapply ended: "ok" (pushed, or nothing new), "person" (a decision waits for a person) or "failed", with what to do next.
sealed record Outcome(string Status, string Detail = "");

// The re-Apply PR for a worktree's head couldn't be read or found.
sealed class PrUnknown(string message) : Exception(message);

// The round's outside world, which a test replaces: reapply.sh on a checkout, a review session in a worktree, land.sh on PRs, the newest
// re-Apply PR of a repo (number, state, head) and the state of one by its number, a worktree's (HEAD, whether it has commits main lacks),
// the preflight, the lock, and the waits between PR reads.
sealed record World(Func<string, string, int?> Reapply, Func<string, string, (int Code, string Log, List<string> Broke)> Review,
		Func<IReadOnlyList<string>, int?> Land, Func<string, (long Number, string State, string Head)?> PrOf, Func<string, long, string> PrState,
		Func<string, (string Head, bool Ahead)> BranchOf, Func<List<string>> Check, Func<FileStream?> Lock, Action<double> Sleep)
{
	// The real one: reapply.sh and land.sh in groups of their own, the review session sandboxed, gh and git.
	public static World Real(Folders folders) => new(
		(brings, path) => Groups.RunReapply(brings, path, folders),
		(repo, worktree) => ReviewSession.RunReview(repo, worktree),
		refs => Groups.RunLand(refs, folders),
		AutoRound.GhPr, AutoRound.GhPrState, AutoRound.GitBranch,
		() => AutoRollout.Preflight(),
		() => AutoRollout.TakeLock(),
		Groups.Sleep);
}

static class AutoRound
{
	// reapply.sh's exit codes for a push its gate's local review check refused (a test holds them equal).
	public const int ReviewNeeded = 3;
	public const int FindingsWait = 4;
	// Review sessions per repo per run: a second covers a gate that still asks for a review after the first one recorded (main or the
	// Template moved meanwhile); a session that fails stops the round at once, and more would loop.
	public const int MaxReviews = 2;
	// Reads of a PR that doesn't yet show the worktree's head, and the seconds between them.
	public const int LookupTries = 5;
	public const int LookupWaitSeconds = 10;
	// gh pr list's newest first: the first PR from the repo itself, as "<number> <state> <head>".
	public const string PrJq = "[.[] | select(.isCrossRepository == false)][0] // empty | \"\\(.number) \\(.state) \\(.headRefOid)\"";

	// gh_pr: (number, state, head commit) of repo's newest re-Apply PR from repo itself, or null; throws when gh fails. --head matches the
	// branch name only, so a fork's PR of the same name is left out, as reapply.sh leaves it out.
	public static (long Number, string State, string Head)? GhPr(string repo) => GhPr(repo, null);

	public static (long Number, string State, string Head)? GhPr(string repo, Runner? run)
	{
		run ??= AutoRollout.RunQuiet;
		var (code, output, error) = run(["gh", "pr", "list", "-R", repo, "--head", AutoRollout.Branch, "--state", "all", "--limit", "100",
			"--json", "number,state,headRefOid,isCrossRepository", "--jq", PrJq]);
		if (code != 0)
			throw new InvalidOperationException(PyLib.Strip(error) is { Length: > 0 } why ? why : $"gh pr list exited {code}");
		if (PyLib.Strip(output).Length == 0)
			return null;
		var words = Py.Split(output);
		if (words.Count != 3 || !long.TryParse(words[0], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
			throw new InvalidOperationException($"gh pr list printed {PyLib.Repr(output)}, not a PR's number, state and head");
		return (number, words[1], words[2]);
	}

	// gh_pr_state: the state of repo's PR number (OPEN, MERGED or CLOSED); throws when gh fails.
	public static string GhPrState(string repo, long number) => GhPrState(repo, number, null);

	public static string GhPrState(string repo, long number, Runner? run)
	{
		run ??= AutoRollout.RunQuiet;
		var (code, output, error) = run(["gh", "pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "-R", repo, "--json", "state",
			"--jq", ".state"]);
		if (code != 0 || PyLib.Strip(output).Length == 0)
			throw new InvalidOperationException(PyLib.Strip(error) is { Length: > 0 } why ? why : $"gh pr view exited {code}");
		return PyLib.Strip(output);
	}

	// git_branch: (HEAD, whether the branch has commits main lacks) of the re-Apply worktree; throws with git's own words.
	public static (string Head, bool Ahead) GitBranch(string worktree) => GitBranch(worktree, null);

	public static (string Head, bool Ahead) GitBranch(string worktree, Runner? run)
	{
		run ??= AutoRollout.RunQuiet;

		(int, string, string) Git(params string[] args)
		{
			var (code, output, error) = run(["git", "-C", worktree, .. args]);
			return (code, PyLib.Strip(output), PyLib.Strip(error));
		}

		var (code, head, error) = Git("rev-parse", "HEAD");
		if (code != 0)
			throw new InvalidOperationException($"git rev-parse HEAD in {worktree} exited {code}: {error}");
		(code, _, error) = Git("merge-base", "--is-ancestor", "HEAD", "origin/main");
		if (code is not (0 or 1))
			throw new InvalidOperationException($"git merge-base in {worktree} exited {code}: {error}");
		return (head, code == 1);
	}

	// drive: reapply.sh on one repo, with a headless review each time the gate asks for one.
	public static Outcome Drive(string repo, string path, string brings, Func<string, string, int?> reapply,
			Func<string, string, (int Code, string Log, List<string> Broke)> review, Action<string> say, Func<string, string?>? advisories = null)
	{
		advisories ??= worktree => ReviewSession.OpenAdvisories(worktree);
		var worktree = AutoRollout.ReviewWorktree(path);
		var retry = $"reapply.sh --brings {PyLib.ShQuote(brings)} {PyLib.ShQuote(path)}";
		var (reviews, log) = (0, (string?)null);
		while (true)
		{
			say($"── rollout: {AutoRollout.ShortName(repo)}");
			var code = reapply(brings, path);
			if (code == 0)
				return new("ok");
			if (code == FindingsWait)
				return new("person", $"review findings wait for you (cd {PyLib.ShQuote(worktree)} && scripts/review.sh report): fix them with"
						+ " /pre-push-review there (one in an Owned file goes to the Factory's template/owned/ first: add that PR to --brings), or accept"
						+ $" them: cd {PyLib.ShQuote(worktree)} && scripts/review.sh accept-open");
			if (code != ReviewNeeded)
				return new("failed", $"reapply.sh exited {PyCode(code)}; retry it with: {retry}");
			if (reviews == MaxReviews)
				return new("failed", $"the gate still asks for a review after {reviews} review session(s): main or the Template may have moved since the"
						+ $" last one (see {log}); run /pre-push-review in {PyLib.ShQuote(worktree)}, then resume");
			reviews++;
			say($"── rollout: {AutoRollout.ShortName(repo)}: the gate asks for a review; running /pre-push-review headless in {worktree}");
			(var reviewed, log, var broke) = review(repo, worktree);
			say($"── rollout: {AutoRollout.ShortName(repo)}: review session exited {reviewed}; log: {log}");
			var (here, keep) = (PyLib.ShQuote(worktree), ReviewRecord.StagedCopy(log));
			var head = ReviewRecord.KeptHead(keep);
			var kept = head is not null
					? $" What the session staged is kept in {keep}; record it, once that is put right, with: {ReviewRecord.RecordCommand(worktree, keep, head)}"
					: "";
			if (reviewed == ReviewSession.RecordLeft)
				return new("person", $"the round's record of the review didn't end cleanly: {broke[0]}. Whether the review was recorded can't be"
						+ $" told: cd {here} && scripts/review.sh report shows it (see {log}).{kept}");
			if (broke.Count > 0 && reviewed != ReviewRecord.RecordFailed)
				return new("person", $"the review session broke a rule, so nothing was recorded: {broke[0]}"
						+ (broke.Count > 1 ? $" (and {broke.Count - 1} more)" : "") + $"; see {log}. Put it right before you resume, since a resume"
						+ $" starts from what it finds: git -C {here} status --ignored shows the worktree, cd {here} && scripts/review.sh report shows"
						+ $" the review ledgers, and cd {here} && scripts/review.sh security shows the .security record");
			if (reviewed != 0)
			{
				var why = reviewed switch
				{
					124 => $"ran past {ReviewSession.ReviewTimeoutMinutes} minutes and was stopped",
					127 => ReviewSession.StopLine(log) ?? (File.Exists(log)
							? $"exited 127 and the log doesn't begin with a {PyLib.Repr(PyLib.Strip(ReviewSession.NotStarted))} line, so claude started and"
								+ " exited 127 itself: the log says why"
							: "couldn't be started: no log could be made"),
					ReviewRecord.NothingStaged => "exited 0 having staged no findings, not even [], so no review was recorded",
					ReviewRecord.RecordFailed => "staged findings the round couldn't record (scripts/review.sh prepare or record failed, or the round did;"
							+ " the log says why)" + (broke.Count > 0 ? $": {broke[0]}" : ""),
					_ => $"exited {reviewed}",
				};
				if (reviewed == 127 && why.StartsWith($"couldn't be started: {ReviewSession.WorktreeGone}", StringComparison.Ordinal))
					return new("failed", $"the review session {why} (see {log}).{kept}");
				return new("failed", $"the review session {why} (see {log}); run /pre-push-review in {here}, then resume.{kept}");
			}
			string? open;
			try
			{
				open = advisories(worktree);
			}
			catch (Exception error) when (error is not Interrupted) // only a list is shown; the recorded review still pushes
			{
				open = ReviewSession.AdvisoriesFailed(worktree, SessionGuard.Shown(error.Message));
			}
			if (open is not null)
				say($"── rollout: {AutoRollout.ShortName(repo)}: the review left Advisory findings open, which don't block the push and are filed"
						+ $" nowhere: file them in a follow-up issue.\n{open}");
		}
	}

	// An exit code as Python prints it: None for a command whose code is unknown.
	static string PyCode(int? code) => code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None";

	// auto_round: the round after the preflight; its exit code. resume is the command that resumes it.
	public static int Round(IReadOnlyList<string> order, string brings, string resume, Func<string, string> checkout, World world,
			Action<string> say, TextWriter err, Func<string, string?>? advisories = null)
	{
		var (first, rest) = (order[0], order.Skip(1).ToList());

		// (number, state) of the repo's re-Apply PR for its worktree, or null when nothing was pushed and it has none. Throws PrUnknown when
		// gh keeps failing, or no PR shows the head the worktree pushed.
		(long Number, string State)? Find(string repo)
		{
			var worktree = AutoRollout.ReviewWorktree(checkout(repo));
			string head;
			bool ahead;
			try
			{
				(head, ahead) = world.BranchOf(worktree);
			}
			catch (Exception error) when (error is not Interrupted)
			{
				throw new PrUnknown($"can't read {worktree}'s branch: {error.Message}");
			}
			var last = "no re-Apply PR found";
			for (var attempt = 0; attempt < LookupTries; attempt++)
			{
				if (attempt > 0)
					world.Sleep(LookupWaitSeconds);
				(long Number, string State, string Head)? found;
				try
				{
					found = world.PrOf(repo);
				}
				catch (Exception error) when (error is not Interrupted)
				{
					last = $"gh failed: {error.Message}";
					continue;
				}
				if (found is null)
				{
					if (!ahead)
						return null; // nothing pushed, so no list can lag: there is no PR
					last = $"no re-Apply PR shows head {Short(head)}";
					continue;
				}
				var (number, state, prHead) = found.Value;
				if (prHead != head && !ahead && state == "CLOSED")
					return null; // the last round's PR, closed unmerged: nothing was pushed, so nothing to land
				if (prHead != head && (ahead || state == "OPEN"))
				{
					// The last round's PR while the list lags the push, or a PR whose branch moved since.
					last = $"#{number} is at {Short(prHead)}, the worktree at {Short(head)}";
					continue;
				}
				return (number, state);
			}
			throw new PrUnknown(last);
		}

		// The state of a PR land.sh has finished with, read by its number (land.sh may have updated its branch), once it no longer reads as
		// OPEN. Throws PrUnknown.
		string Settle(string repo, long number)
		{
			var last = $"#{number} still reads as OPEN";
			for (var attempt = 0; attempt < LookupTries; attempt++)
			{
				if (attempt > 0)
					world.Sleep(LookupWaitSeconds);
				string state;
				try
				{
					state = world.PrState(repo, number);
				}
				catch (Exception error) when (error is not Interrupted)
				{
					last = $"gh failed: {error.Message}";
					continue;
				}
				if (state != "OPEN")
					return state;
				last = $"#{number} still reads as OPEN"; // a read that answers replaces an earlier gh failure
			}
			throw new PrUnknown(last);
		}

		// A crash still ends with a stop: the stack trace goes to err, so a bug can be told from the world. It names retry, the command to
		// run by hand, or else the repo's reapply.sh, where its checkout can be told.
		string Crashed(string repo, Exception error, string? retry = null)
		{
			err.Write($"rollout.sh: {AutoRollout.ShortName(repo)} crashed:\n{error}\n");
			if (retry is null)
				try
				{
					retry = $"reapply.sh --brings {PyLib.ShQuote(brings)} {PyLib.ShQuote(checkout(repo))}";
				}
				catch (Exception error2) when (error2 is not Interrupted) // the stop is still printed, without the command
				{
					retry = "";
				}
			return $"{AutoRollout.ShortName(repo)}: {error.GetType().Name}: {error.Message} (traceback on stderr)"
					+ (retry.Length > 0 ? $"; run it by hand with: {retry}" : "");
		}

		// The stop for a PR that reads as neither OPEN nor MERGED (closed unmerged): how to land it, or start afresh.
		string NotMerged(string repo, long number, string state) =>
			$"{AutoRollout.ShortName(repo)}#{number} is {state}, not merged: reopen it (gh pr reopen {number} -R {repo}) and land it with land.sh"
			+ $" {repo}#{number}, or delete its branch and resume for a new PR";

		int Held(string reason)
		{
			say($"rollout: {reason}; held: {(rest.Count > 0 ? string.Join(", ", rest.Select(AutoRollout.ShortName)) : "none")}");
			say($"  resume with: {resume}");
			return 1;
		}

		try
		{
			var lead = Drive(first, checkout(first), brings, world.Reapply, world.Review, say, advisories);
			if (lead.Status != "ok")
				return Held($"{AutoRollout.ShortName(first)}: {lead.Detail}");
			var found = Find(first);
			if (found is null)
				// Nothing pushed and no PR to land: reapply.sh found it already matches the Template, so the rest go on.
				say($"── rollout: {AutoRollout.ShortName(first)} already matches the Template: nothing to land, so the rest go on");
			else
			{
				var (number, state) = found.Value;
				if (state == "OPEN")
				{
					say($"── rollout: landing {AutoRollout.ShortName(first)}#{number}");
					int? landed;
					try
					{
						landed = world.Land([$"{first}#{number}"]);
					}
					catch (GroupLeft error)
					{
						return Held($"{AutoRollout.ShortName(first)}#{number}: {error.Message}; then land it with land.sh {first}#{number}");
					}
					catch (IOException error) // land.sh couldn't start: the stop names land.sh, not the reapply.sh a crash would
					{
						return Held($"{AutoRollout.ShortName(first)}#{number}: land.sh failed: {ReviewSession.PyErrorName(error)}: {error.Message}; land it"
								+ $" with land.sh {first}#{number}");
					}
					catch (Exception error) when (error is not Interrupted) // a bug while landing: its stack trace, and land.sh to run
					{
						return Held(Crashed(first, error, $"land.sh {first}#{number}"));
					}
					if (landed != 0)
						return Held($"{AutoRollout.ShortName(first)}#{number} didn't land (see land.sh's output above): land.sh {first}#{number}");
					state = Settle(first, number);
				}
				if (state != "MERGED")
					return Held(NotMerged(first, number, state));
			}
		}
		catch (PrUnknown error)
		{
			return Held($"{AutoRollout.ShortName(first)}: can't tell its re-Apply PR ({error.Message}): land it with land.sh {first}");
		}
		catch (GroupLeft error)
		{
			return Held($"{AutoRollout.ShortName(first)}: {error.Message}");
		}
		catch (Exception error) when (error is not Interrupted) // a crash still ends with the stop and the resume command
		{
			return Held(Crashed(first, error));
		}

		List<string> stops = [];
		List<(string Repo, long Number)> landing = [];
		HashSet<string> waiting = [];

		// A stop for repo: the summary counts the repos that wait, not the stops, since land.sh's own names none.
		void Hold(string repo, string text)
		{
			stops.Add(text);
			waiting.Add(repo);
		}

		foreach (var repo in rest)
		{
			(long Number, string State)? found;
			try
			{
				var outcome = Drive(repo, checkout(repo), brings, world.Reapply, world.Review, say, advisories);
				if (outcome.Status != "ok")
				{
					Hold(repo, $"{AutoRollout.ShortName(repo)}: {outcome.Detail}");
					continue;
				}
				found = Find(repo);
			}
			catch (PrUnknown error)
			{
				Hold(repo, $"{AutoRollout.ShortName(repo)}: can't tell its re-Apply PR ({error.Message}): land it with land.sh {repo}");
				continue;
			}
			catch (GroupLeft error)
			{
				Hold(repo, $"{AutoRollout.ShortName(repo)}: {error.Message}");
				continue;
			}
			catch (Exception error) when (error is not Interrupted) // reported, and the round goes on
			{
				Hold(repo, Crashed(repo, error));
				continue;
			}
			if (found is null)
				continue; // nothing to push and never a re-Apply PR: nothing to land
			var (number, state) = found.Value;
			if (state == "OPEN")
				landing.Add((repo, number));
			else if (state != "MERGED")
				Hold(repo, NotMerged(repo, number, state));
		}
		if (landing.Count > 0)
		{
			List<string> refs = [.. landing.Select(pr => $"{pr.Repo}#{pr.Number}")];
			say($"── rollout: landing {string.Join(" ", refs)}");
			try
			{
				var landed = world.Land(refs);
				if (landed != 0)
					say($"rollout.sh: land.sh exited {PyCode(landed)} (see its output above)");
			}
			catch (GroupLeft error)
			{
				stops.Add($"land.sh: {error.Message}");
			}
			catch (Exception error) when (error is not Interrupted) // each PR's state below says what landed
			{
				say($"rollout.sh: land.sh failed: {(error is IOException ? ReviewSession.PyErrorName(error) : error.GetType().Name)}: {error.Message}");
			}
			foreach (var (repo, number) in landing)
			{
				string state;
				try
				{
					state = Settle(repo, number);
				}
				catch (PrUnknown error)
				{
					Hold(repo, $"{AutoRollout.ShortName(repo)}#{number} didn't land ({error.Message}): land.sh {repo}#{number}");
					continue;
				}
				if (state != "MERGED")
					Hold(repo, NotMerged(repo, number, state));
			}
		}
		say($"rollout: {AutoRollout.ShortName(first)} and {rest.Count - waiting.Count} of {rest.Count} other repo(s) done" + (stops.Count == 0 ? "" : "; waiting for you:"));
		foreach (var stop in stops)
			say($"  {stop}");
		if (stops.Count > 0)
			say($"  resume with: {resume}");
		return stops.Count > 0 ? 1 : 0;
	}

	// head[:7].
	static string Short(string head) => head.Length > 7 ? head[..7] : head;

	// run: rollout.sh --auto, after Rollout has read the arguments and found every checkout. defaultRoot is Rollout's DefaultRoot: the
	// printed commands carry --root only when the round runs with another. An interrupt, or a SIGTERM or SIGHUP, ends it with 128 + the
	// signal and the command that resumes it: held through a child's group until the group is gone (Interrupted), and otherwise at once.
	public static int Run(IReadOnlyList<string> order, string brings, string root, Func<string, string> checkout, bool dryRun, World world,
			Action<string> say, TextWriter err, string defaultRoot, Func<string, string?>? advisories = null, Action<int, string>? onSignal = null)
	{
		var rootArg = PyLib.NormPath(root) == PyLib.NormPath(defaultRoot) ? "" : $" --root {PyLib.ShQuote(root)}";
		var resume = $"rollout.sh --auto --brings {PyLib.ShQuote(brings)}{rootArg}";
		var problems = world.Check();
		foreach (var problem in problems)
			err.Write($"rollout.sh: {problem}\n");
		if (problems.Count > 0)
		{
			err.Write($"rollout.sh: nothing run; fix those first, then run: {resume}\n");
			return 1;
		}
		if (dryRun)
		{
			say("tools: all there");
			foreach (var repo in order)
				say($"would run: reapply.sh --brings {PyLib.ShQuote(brings)} {PyLib.ShQuote(checkout(repo))}, with /pre-push-review headless in"
						+ $" {AutoRollout.ReviewWorktree(checkout(repo))} when its gate asks for one");
			say($"then land {AutoRollout.ShortName(order[0])}'s re-Apply PR with land.sh before the rest start (at once when it already matches the"
					+ " Template), and the rest's together");
			return 0;
		}
		FileStream? handle;
		try
		{
			handle = world.Lock();
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			err.Write($"rollout.sh: can't take the lock {AutoRollout.Lock}: {error.Message}; nothing run. Once it can be taken, run: {resume}\n");
			return 1;
		}
		if (handle is null)
		{
			err.Write($"rollout.sh: another rollout.sh --auto holds {AutoRollout.Lock}, or a review session or gate an earlier one started is still"
					+ $" running; nothing run. See what holds it with: fuser -v {PyLib.ShQuote(AutoRollout.Lock)} (or lsof {PyLib.ShQuote(AutoRollout.Lock)}),"
					+ $" and end it only when no round is running; then run: {resume}\n");
			return 1;
		}
		try
		{
			AutoRollout.LockFds.Clear();
			AutoRollout.LockFds.Add(AutoRollout.Descriptor(handle));
			onSignal ??= Interrupt;
			using (Signals.RoundSignals(signum => onSignal(signum, resume)))
				return Round(order, brings, resume, checkout, world, say, err, advisories);
		}
		catch (Interrupted stop)
		{
			// An interrupt, or a SIGTERM or SIGHUP held through a child's group (DeferredSignals): its group is gone.
			err.Write($"rollout.sh: interrupted; resume with: {resume}\n");
			return stop.Code;
		}
		finally
		{
			AutoRollout.LockFds.Clear();
			handle.Dispose(); // this process's hold on the lock ends here, and not before
		}
	}

	// A signal outside a hold ends the round at once, as Python's SystemExit and KeyboardInterrupt did: the resume command, then exit
	// 128 + the signal (the lock goes with the process).
	static void Interrupt(int signum, string resume)
	{
		try
		{
			Console.Out.Flush();
			var line = Encoding.UTF8.GetBytes($"rollout.sh: interrupted; resume with: {resume}\n");
			using var stderr = Console.OpenStandardError();
			stderr.Write(line);
		}
		finally
		{
			Environment.Exit(128 + signum);
		}
	}
}
