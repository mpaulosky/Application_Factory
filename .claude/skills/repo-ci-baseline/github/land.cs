#:include Gh.cs
#:include Repos.cs
#:include Landing.cs
#:include StatusCore.cs
#:include LandCore.cs
#:property PublishAot=false
// Watch a round's PRs and act on the landing decision until they land. LandCore.cs holds the logic and its contract; land.sh runs this.
//
//     dotnet run --file github/land.cs -- [options] [<owner/repo>|<owner/repo#n>]...
return Script.Run((stdout, stderr) => Land.Run(args, stdout, stderr));
