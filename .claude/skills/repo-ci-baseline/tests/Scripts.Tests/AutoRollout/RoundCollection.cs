// The tests that hold or send signals, start process groups, or set the round's lock (AutoRollout.LockFds): one collection that runs alone,
// after the others, since a signal one of them sends to the test process reaches whichever hold is on top (Signals keeps one stack for the
// process), and LockFds is the process's own, as the pytest originals had them one at a time.
[CollectionDefinition(nameof(RoundCollection), DisableParallelization = true)]
public sealed class RoundCollection;
