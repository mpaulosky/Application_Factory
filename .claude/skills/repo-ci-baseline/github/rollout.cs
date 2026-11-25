#:include Gh.cs
#:include AutoRolloutSandbox.cs
#:property PublishAot=false
// rollout.sh's C#, landing a library at a time ahead of its cut-over (ADR 0017's Update for mpaulosky/Application_Factory#157, library
// first): rollout.sh still runs rollout.py and auto_rollout.py, and nothing runs this. It pulls in each AutoRollout*.cs library as it lands,
// so the gate builds them with warnings as errors; the cut-over (mpaulosky/Application_Factory#122) fills it in.
Console.Error.WriteLine("rollout.cs: not cut over yet; rollout.sh runs github/rollout.py");
return 2;
