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
}
