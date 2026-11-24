#:include Checker.cs
#:include GitStatusGuards.cs
#:property PublishAot=false
// A guard that reads `git status --porcelain` in an Owned shell script names its untracked-files setting: print each one that doesn't and exit 1.
// GitStatusGuards.cs holds the rule and its exit codes; tests/check-git-status.sh runs this.
//
//     dotnet run --file tests/check-git-status.cs -- <the Template's owned/ folder>
return Checker.Run("check-git-status.cs", (stdout, stderr) => GitStatusGuards.Run(args, stdout, stderr));
