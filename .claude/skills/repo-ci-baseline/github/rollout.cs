#:include Gh.cs
#:include Repos.cs
#:include SettingsCore.cs
#:include AutoRolloutGuard.cs
#:include AutoRolloutRecord.cs
#:property PublishAot=false
// The entry file of rollout's C# (ADR 0017's Update for mpaulosky/Application_Factory#157): auto_rollout's logic lands here as libraries,
// one #:include each, ahead of the cut-over (mpaulosky/Application_Factory#122), which fills this in and switches rollout.sh to it. Until
// then rollout.sh runs rollout.py, and nothing runs this.
Console.Error.WriteLine("rollout.cs: not yet: rollout.sh still runs github/rollout.py (mpaulosky/Application_Factory#122)");
return 2;
