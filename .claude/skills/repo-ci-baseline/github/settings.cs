#:include Gh.cs
#:include Repos.cs
#:include SettingsCore.cs
#:property PublishAot=false
// Check, and with --fix apply, the Baseline's GitHub settings on a repo. SettingsCore.cs holds the logic and its contract;
// github-settings.sh runs this.
//
//     dotnet run --file github/settings.cs -- [--all] [--fix [--remove-legacy]] [--suspend] [--json] [<owner/repo>...]
return Script.Run((stdout, stderr) => Settings.Run(args, stdout, stderr));
