using System.Runtime.InteropServices;

// How the tests send the test process a signal: kill(getpid(), signum), then a wait until a handler of Signals has handled it (Signals.Dispatched
// moves on), since the handler runs on the runtime's signal thread. A fixed sleep could end before the handler ran on a loaded runner. Send one only
// while a hold for it is registered: the runtime's own handling of a SIGTERM or SIGHUP would end the test host, and nothing would move Dispatched.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static class SignalSender
{
	static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int sig);

	public static void Send(int signum)
	{
		Assert.NotEqual(Signals.SigInt, signum); // the runner's Console.CancelKeyPress would cancel the run
		var before = Signals.Dispatched;
		Assert.Equal(0, Kill(Environment.ProcessId, signum));
		Assert.True(SpinWait.SpinUntil(() => Signals.Dispatched > before, Wait), $"no handler handled signal {signum} within {Wait.TotalSeconds} seconds");
	}
}
