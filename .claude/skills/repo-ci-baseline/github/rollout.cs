#:include Gh.cs
#:include Repos.cs
#:include Py.cs
#:include AutoRollout.cs
#:include AutoRolloutPreflight.cs
#:include AutoRolloutSandbox.cs
#:include AutoRolloutGuard.cs
#:include AutoRolloutRecord.cs
#:property PublishAot=false
// rollout.sh's C#, landing a library at a time (ADR 0017's Update for mpaulosky/Application_Factory#157): AutoRollout*.cs hold auto_rollout.py's logic
// as it is ported. Nothing runs this file until the cut-over (mpaulosky/Application_Factory#122), which fills it in: rollout.sh still runs
// rollout.py, and this only says so.
Console.Error.WriteLine("rollout.cs: not yet the rollout; rollout.sh runs github/rollout.py until the cut-over (mpaulosky/Application_Factory#122)");
return 2;
