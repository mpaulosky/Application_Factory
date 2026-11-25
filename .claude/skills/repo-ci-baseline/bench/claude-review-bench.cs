#:package YamlDotNet@18.1.0
#:include ../github/Gh.cs
#:include ../github/Py.cs
#:include Yaml.cs
#:include ReviewBench.cs
#:property PublishAot=false
#:property RestorePackagesWithLockFile=true
#:property RestoreLockedMode=true
// Score Claude Review against findings Copilot made on the same commits. ReviewBench.cs holds the logic and its contract.
//
//     dotnet run --file bench/claude-review-bench.cs -- [--effort high] [--model claude-opus-5-5] [--prompt-file p.txt] [--tools ...] [--only <head>]
//     dotnet run --file bench/claude-review-bench.cs -- --rescore bench/results/<run>.json...
//
// YamlDotNet is pinned here and by hash in packages.lock.json, in step with tests/Scripts.Tests (test.sh checks both). To move it, change the
// version here and in Scripts.Tests.csproj, and run each once with -p:RestoreLockedMode=false (here: dotnet build --file ... ) to rewrite its lock file.
return Script.Run((stdout, stderr) => Bench.Run(args, stdout, stderr));
