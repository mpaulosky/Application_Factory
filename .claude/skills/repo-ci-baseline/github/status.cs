#:include Gh.cs
#:include Repos.cs
#:include Landing.cs
#:include StatusCore.cs
#:property PublishAot=false
// One line per open PR across the repos in github/repos.txt, with the landing decision's next action. StatusCore.cs holds the logic and
// its contract; status.sh runs this.
//
//     dotnet run --file github/status.cs -- [<owner/repo>...]
return Script.Run((stdout, stderr) => Status.Run(args, stdout, stderr));
