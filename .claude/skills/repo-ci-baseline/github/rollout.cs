#:include Gh.cs
#:include Repos.cs
#:include Py.cs
#:include AutoRollout.cs
#:include AutoRolloutPreflight.cs
#:include AutoRolloutSandbox.cs
#:include AutoRolloutGuard.cs
#:include AutoRolloutRecord.cs
#:include AutoRolloutGroups.cs
#:include AutoRolloutReview.cs
#:include AutoRolloutRound.cs
#:include RolloutCore.cs
#:property PublishAot=false
// Roll a re-Apply round out across the Baseline repos. RolloutCore.cs holds the plain mode and its contract, AutoRollout*.cs the --auto
// round (ADR 0016); rollout.sh builds this and runs the built dll, so a signal sent to rollout.sh reaches the round (ADR 0017).
//
//     dotnet run --file github/rollout.cs -- --brings <prs> [--root dir] [--dry-run] [--auto]
Signals.Init();
return Script.Run((stdout, stderr) => Rollout.Run(args, stdout, stderr));
