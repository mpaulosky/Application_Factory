using System.Runtime.InteropServices;

// Tests for how Signals (github/AutoRolloutGroups.cs) dispatches a signal against a hold being installed or removed on another thread, which
// has no pytest original (Python ran its handlers on the main thread, between bytecodes). A handler of the test's own blocks inside the
// dispatch, and the test checks that a DeferredSignals, or removing a Handling, waits until the dispatch is done: otherwise the round's handler
// could end the process after a hold had started a child, or a hold's could record a signal after its Got was read. Each signal is SIGHUP,
// sent only while a Handling for it is registered, as SessionGroupTests sends its signals.
[Collection(nameof(RoundCollection))]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SignalDispatchTests
{
	public SignalDispatchTests() => Assert.SkipUnless(OperatingSystem.IsLinux(), "--auto runs on Linux only");

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	static extern int Kill(int pid, int sig);

	static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
	static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

	// A Handling of SIGHUP whose handler says it has begun, then waits to be let go.
	sealed class Blocking : IDisposable
	{
		public readonly ManualResetEventSlim Begun = new(), Go = new();
		public readonly List<int> Got = [];
		readonly IDisposable handling;

		public Blocking() => handling = Signals.Handling([Signals.SigHup], signum =>
		{
			Begun.Set();
			Go.Wait(Wait);
			Got.Add(signum);
		});

		public void Dispose() => handling.Dispose();
	}

	static void Send(Blocking blocking)
	{
		Assert.Equal(0, Kill(Environment.ProcessId, Signals.SigHup));
		Assert.True(blocking.Begun.Wait(Wait), "the handler began");
	}

	[Fact]
	public async Task A_hold_installed_while_a_signal_is_dispatched_waits_for_the_dispatch()
	{
		// Arrange
		Assert.SkipUnless(Signals.Held(Signals.SigHup), "the test host ignores SIGHUP");
		using var blocking = new Blocking();
		Send(blocking);

		// Act
		var installing = Task.Run(() => Signals.DeferredSignals([Signals.SigHup]), TestContext.Current.CancellationToken);
		var waited = await Task.WhenAny(installing, Task.Delay(Settle, TestContext.Current.CancellationToken)) != installing;
		blocking.Go.Set();
		using var hold = await installing.WaitAsync(Wait, TestContext.Current.CancellationToken);

		// Assert
		Assert.True(waited, "the hold was installed while the signal was still being dispatched");
		Assert.Equal([Signals.SigHup], blocking.Got);
		Assert.Empty(hold.Got);
	}

	[Fact]
	public async Task A_hold_removed_while_a_signal_is_dispatched_waits_for_the_dispatch()
	{
		// Arrange
		Assert.SkipUnless(Signals.Held(Signals.SigHup), "the test host ignores SIGHUP");
		using var outer = Signals.Handling([Signals.SigHup], _ => { }); // the handler under it, so the signal stays held once it is removed
		var blocking = new Blocking();
		Send(blocking);

		// Act
		var removing = Task.Run(blocking.Dispose, TestContext.Current.CancellationToken);
		var waited = await Task.WhenAny(removing, Task.Delay(Settle, TestContext.Current.CancellationToken)) != removing;
		blocking.Go.Set();
		await removing.WaitAsync(Wait, TestContext.Current.CancellationToken);

		// Assert
		Assert.True(waited, "the hold was removed while the signal was still being dispatched to it");
		Assert.Equal([Signals.SigHup], blocking.Got);
	}
}
