#:include GateTimings.cs
#:property PublishAot=false
// Summarizes the step timings log scripts/gate.sh and test.sh keep, as a Markdown table (mpaulosky/Application_Factory#220). GateTimings.cs holds the logic
// and its contract; scripts/gate-timings.sh runs this.
//
//     dotnet run --file scripts/gate-timings.cs -- [--runs N] [--log FILE]
using Factory;

return GateTimings.Run(args, Console.Out, Console.Error, GateTimings.GitCommonDir, Environment.GetEnvironmentVariable);
