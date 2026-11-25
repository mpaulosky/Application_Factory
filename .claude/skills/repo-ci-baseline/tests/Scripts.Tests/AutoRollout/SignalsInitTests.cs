using System.Runtime.InteropServices;

// Tests for Signals.Init (github/AutoRolloutGroups.cs), which has no pytest original: Python kept an ignored SIGTERM ignored by itself, and the
// .NET runtime takes SIGTERM over before the round's first line, so rollout.sh passes its caller's ignored signals on in ROLLOUT_SIGIGN and
// the round puts an ignored SIGTERM back (ADR 0017's Update for mpaulosky/Application_Factory#157). SIGTERM's disposition is read and put
// back whole through libc's sigaction, so the test host keeps its own.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SignalsInitTests
{
	public SignalsInitTests() => Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");

	// sigaction with glibc's 64-bit struct sigaction as bytes, larger than it can be, so a disposition is put back with its flags.
	[DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
	static extern int SigAction(int signum, byte[]? act, byte[]? oldact);

	static void WithSigterm(Action test)
	{
		var previous = new byte[256];
		Assert.Equal(0, SigAction(Signals.SigTerm, null, previous));
		var mask = Environment.GetEnvironmentVariable(Signals.IgnoredVariable);
		try
		{
			test();
		}
		finally
		{
			Assert.Equal(0, SigAction(Signals.SigTerm, previous, null));
			Environment.SetEnvironmentVariable(Signals.IgnoredVariable, mask);
		}
	}

	[Theory]
	[InlineData("0000000000004000")] // SIGTERM alone, as /proc/$$/status writes it
	[InlineData("0000000000004001")] // with nohup's SIGHUP
	public void An_ignored_sigterm_the_wrapper_passes_on_is_ignored_again_and_the_variable_dropped(string mask)
	{
		Assert.SkipUnless(Signals.Held(Signals.SigTerm), "the test host already ignores SIGTERM");
		WithSigterm(() =>
		{
			Environment.SetEnvironmentVariable(Signals.IgnoredVariable, mask);
			Signals.Init();
			Assert.False(Signals.Held(Signals.SigTerm));
			// Its children don't see it.
			Assert.Null(Environment.GetEnvironmentVariable(Signals.IgnoredVariable));
		});
	}

	[Theory]
	[InlineData("0000000000000001")] // SIGHUP alone stays the runtime's to keep ignored
	[InlineData("0000000000000000")]
	[InlineData("not hex")]
	[InlineData("")]
	public void A_mask_without_sigterm_or_that_doesnt_read_leaves_sigterm_held(string mask)
	{
		Assert.SkipUnless(Signals.Held(Signals.SigTerm), "the test host already ignores SIGTERM");
		WithSigterm(() =>
		{
			Environment.SetEnvironmentVariable(Signals.IgnoredVariable, mask.Length > 0 ? mask : null);
			Signals.Init();
			Assert.True(Signals.Held(Signals.SigTerm));
			Assert.Null(Environment.GetEnvironmentVariable(Signals.IgnoredVariable));
		});
	}
}
