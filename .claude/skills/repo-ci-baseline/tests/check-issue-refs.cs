#:include Checker.cs
#:include IssueRefs.cs
#:property PublishAot=false
// A bare #N in an Owned file: print each one and exit 1. IssueRefs.cs holds the rule and its exit codes; tests/check-issue-refs.sh runs this.
//
//     dotnet run --file tests/check-issue-refs.cs -- <the Template's owned/ folder>
return Checker.Run("check-issue-refs.cs", (stdout, stderr) => IssueRefs.Run(args, stdout, stderr));
