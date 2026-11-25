using static SandboxFixtures;

// Not moved tests: test_auto_rollout.py reaches own_log_dir only through the round (F3d, mpaulosky/Application_Factory#122), so these
// hold github/AutoRolloutSandbox.cs's OwnLogDir to auto_rollout.py's own_log_dir directly (Claude Review on
// mpaulosky/Application_Factory#193): a missing folder is made 0700, one there is set to 0700, and a symlink or another owner's
// folder is refused, naming the folder.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class OwnLogDirTests
{
	const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

	[Fact]
	public void A_missing_folder_is_made_with_its_parents_and_only_the_user_can_use_it()
	{
		var logDir = Path.Combine(TempDir(), "logs", "rollout");
		AutoRollout.OwnLogDir(logDir);
		Assert.True(Directory.Exists(logDir));
		Assert.Equal(Private, File.GetUnixFileMode(logDir));
	}

	[Fact]
	public void A_folder_there_that_others_can_read_is_set_to_0700()
	{
		var logDir = Path.Combine(TempDir(), "logs");
		Directory.CreateDirectory(logDir);
		File.SetUnixFileMode(logDir, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		AutoRollout.OwnLogDir(logDir);
		Assert.Equal(Private, File.GetUnixFileMode(logDir));
	}

	[Fact]
	public void A_symlink_to_a_folder_is_refused_and_its_target_left_as_it_is()
	{
		var tmp = TempDir();
		var target = Path.Combine(tmp, "elsewhere");
		Directory.CreateDirectory(target);
		var open = Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
		File.SetUnixFileMode(target, open);
		var logDir = Path.Combine(tmp, "logs");
		File.CreateSymbolicLink(logDir, target);
		var error = Assert.Throws<IOException>(() => AutoRollout.OwnLogDir(logDir));
		Assert.Equal($"{logDir} isn't a folder of the user's own", error.Message);
		Assert.Equal(open, File.GetUnixFileMode(target));
	}

	[Fact]
	public void A_file_where_the_folder_goes_is_refused()
	{
		var logDir = Path.Combine(TempDir(), "logs");
		File.WriteAllText(logDir, "");
		Assert.ThrowsAny<IOException>(() => AutoRollout.OwnLogDir(logDir));
		Assert.True(File.Exists(logDir));
	}

	[Fact]
	public void Another_owners_folder_is_refused_and_left_as_it_is()
	{
		var logDir = Path.Combine(TempDir(), "logs");
		Directory.CreateDirectory(logDir);
		var open = Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
		File.SetUnixFileMode(logDir, open);
		var error = Assert.Throws<IOException>(() => AutoRollout.OwnLogDir(logDir, uid: PyLib.Owner(logDir) + 1));
		Assert.Equal($"{logDir} isn't a folder of the user's own", error.Message);
		Assert.Equal(open, File.GetUnixFileMode(logDir));
	}
}
