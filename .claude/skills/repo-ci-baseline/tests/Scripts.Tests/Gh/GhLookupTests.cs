// Not moved tests: Gh.Which and Gh.Process with a gh in the current folder, which they must never run. The current folder and PATH are
// the process's own, so these run alone, after the parallel tests.
[CollectionDefinition(nameof(GhLookupTests), DisableParallelization = true)]
public sealed class GhLookupCollection;

[Collection(nameof(GhLookupTests))]
public class GhLookupTests
{
	// A folder holding an executable gh that prints what, under root.
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	static string StubGh(DirectoryInfo root, string folder, string what)
	{
		var path = Directory.CreateDirectory(Path.Combine(root.FullName, folder)).FullName;
		File.WriteAllText(Path.Combine(path, "gh"), $"#!/bin/sh\necho {what}\n");
		File.SetUnixFileMode(Path.Combine(path, "gh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
		return path;
	}

	[Theory]
	[InlineData("")]
	[InlineData(".")]
	[InlineData("::")]
	[InlineData("./")]
	public void An_empty_entry_or_a_dot_never_finds_the_current_folders_gh(string path)
	{
		if (OperatingSystem.IsWindows())
			return; // the scripts run on Linux and macOS
		var root = Directory.CreateTempSubdirectory("which-cwd.");
		var before = Environment.CurrentDirectory;
		try
		{
			Environment.CurrentDirectory = StubGh(root, "here", "here");

			Assert.Throws<FileNotFoundException>(() => Gh.Which("gh", path));
		}
		finally
		{
			Environment.CurrentDirectory = before;
			root.Delete(true);
		}
	}

	[Fact]
	public void Process_runs_paths_gh_not_the_current_folders()
	{
		if (OperatingSystem.IsWindows())
			return;
		var root = Directory.CreateTempSubdirectory("which-run.");
		var (before, path) = (Environment.CurrentDirectory, Environment.GetEnvironmentVariable("PATH"));
		try
		{
			Environment.CurrentDirectory = StubGh(root, "here", "here");
			Environment.SetEnvironmentVariable("PATH", $"{StubGh(root, "listed", "listed")}:/usr/bin:/bin");

			Assert.Equal("listed\n", Gh.Process(["version"]).Stdout);
		}
		finally
		{
			Environment.CurrentDirectory = before;
			Environment.SetEnvironmentVariable("PATH", path);
			root.Delete(true);
		}
	}

	// Not a moved test: a gh that hangs is killed, with the child it started, once the call's time runs out, and the call fails with exit
	// 124 and a message naming the call (mpaulosky/Application_Factory#178); a long body it never reads doesn't hold the call either.
	[Theory]
	[InlineData(null)]
	[InlineData(200_000)]
	public void A_gh_that_hangs_is_killed_when_its_time_runs_out(int? body)
	{
		if (OperatingSystem.IsWindows())
			return;
		var root = Directory.CreateTempSubdirectory("gh-hang.");
		var path = Environment.GetEnvironmentVariable("PATH");
		try
		{
			// sleep is gh's child, holding its stdout and stderr: killing gh alone would leave it running.
			var pid = Path.Combine(root.FullName, "pid");
			File.WriteAllText(Path.Combine(root.FullName, "gh"), $"#!/bin/sh\nsleep 60 &\necho $! > '{pid}'\nwait\necho late\n");
			File.SetUnixFileMode(Path.Combine(root.FullName, "gh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
			Environment.SetEnvironmentVariable("PATH", $"{root.FullName}:/usr/bin:/bin");
			var clock = System.Diagnostics.Stopwatch.StartNew();

			var run = Gh.Process(["api", "graphql", "-f", "query=x"], body is int length ? new string('x', length) : null, TimeSpan.FromSeconds(1));

			Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
			Assert.Equal(new GhRun(124, "", "gh didn't answer in 1 s, so it was stopped: gh api graphql\n"), run);
			Assert.Equal((false, "gh didn't answer in 1 s, so it was stopped: gh api graphql"), (run.Code == 0, Gh.Message(run)));
			Assert.Equal(TimeSpan.FromMinutes(2), Gh.Timeout);
			var child = int.Parse(File.ReadAllText(pid).Trim(), System.Globalization.CultureInfo.InvariantCulture);
			Assert.True(SpinWait.SpinUntil(() => Gone(child), TimeSpan.FromSeconds(10)), $"gh's child {child} is still running");
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", path);
			root.Delete(true);
		}
	}

	// Whether no process has that id, or only one that has exited and waits to be reaped.
	static bool Gone(int pid)
	{
		try
		{
			using var process = System.Diagnostics.Process.GetProcessById(pid);
			return process.HasExited;
		}
		catch (ArgumentException)
		{
			return true;
		}
	}
}
