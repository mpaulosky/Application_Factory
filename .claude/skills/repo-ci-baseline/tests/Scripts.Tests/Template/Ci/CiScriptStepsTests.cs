using System.Text.RegularExpressions;

using YamlDotNet.RepresentationModel;

namespace Baseline.Tests;

// The Template's ci.yml runs each Owned C# script as ADR 0017 asks: built in an earlier step of its job, after the job's setup-dotnet,
// then run with dotnet run --no-build, so the run's log (and a captured stdout) holds only what the script prints. A build step never has
// TEST_ENV, the secret load_test_env.cs reads, in its environment, and no step runs a ported script's .py.
public partial class CiScriptStepsTests
{
	[GeneratedRegex(@"dotnet run --no-build --file \.github/scripts/(?<script>[A-Za-z_]+)\.cs --")]
	private static partial Regex RunsScript();

	// Each job of ci.yml: its name, its env's names, and its steps in order (each step's run, its env's names, and whether it is setup-dotnet).
	static List<(string Job, HashSet<string> Env, List<(string Run, HashSet<string> Env, bool SetupDotnet)> Steps)> Jobs()
	{
		var yaml = new YamlStream();
		yaml.Load(new StringReader(Skill.Workflow("ci.yml")));
		static HashSet<string> Names(YamlMappingNode node, string key) =>
				node.Children.TryGetValue(new YamlScalarNode(key), out var env) && env is YamlMappingNode map ? [.. map.Children.Keys.Select(k => ((YamlScalarNode)k).Value!)] : [];
		static string Text(YamlMappingNode node, string key) =>
				node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value ?? "" : "";
		var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
		return [.. jobs.Children.Select(job =>
		{
			var body = (YamlMappingNode)job.Value;
			var steps = body.Children.TryGetValue(new YamlScalarNode("steps"), out var list) ? ((YamlSequenceNode)list).Children.Cast<YamlMappingNode>() : [];
			return (((YamlScalarNode)job.Key).Value!, Names(body, "env"),
					steps.Select(step => (Text(step, "run"), Names(step, "env"), Text(step, "uses").StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))).ToList());
		})];
	}

	[Fact]
	public void Each_script_ci_runs_is_built_first_in_a_step_without_test_env()
	{
		var runs = 0;
		foreach (var (job, jobEnv, steps) in Jobs())
		{
			for (var i = 0; i < steps.Count; i++)
			{
				foreach (Match run in RunsScript().Matches(steps[i].Run))
				{
					runs++;
					var build = $"dotnet build .github/scripts/{run.Groups["script"].Value}.cs";
					var built = steps.FindIndex(step => step.Run.Trim() == build);
					Assert.True(built >= 0 && built < i, $"{job}: no step before the run of {run.Value} is exactly '{build}'");
					Assert.True(steps.FindIndex(step => step.SetupDotnet) is var setup && setup >= 0 && setup < built, $"{job}: no setup-dotnet before '{build}'");
					Assert.DoesNotContain("TEST_ENV", steps[built].Env);
					Assert.DoesNotContain("TEST_ENV", jobEnv);
				}
			}
		}
		// detect_changes, discover_tests and load_test_env.
		Assert.Equal(3, runs);
	}

	[Theory]
	[InlineData("detect_changes")]
	[InlineData("discover_tests")]
	[InlineData("load_test_env")]
	public void No_step_runs_a_ported_scripts_py(string script)
	{
		Assert.All(Jobs().SelectMany(job => job.Steps), step => Assert.DoesNotContain($".github/scripts/{script}.py", step.Run));
		Assert.Contains(Jobs().SelectMany(job => job.Steps), step => step.Run.Contains($"--file .github/scripts/{script}.cs --", StringComparison.Ordinal));
	}

	[Fact]
	public void Only_the_load_step_has_test_env()
	{
		var withSecret = Jobs().SelectMany(job => job.Steps.Where(step => step.Env.Contains("TEST_ENV")).Select(step => (job.Job, step.Run))).ToList();

		var (job, run) = Assert.Single(withSecret);
		Assert.Equal("test", job);
		Assert.Equal("dotnet run --no-build --file .github/scripts/load_test_env.cs -- --output \"$GITHUB_ENV\"", run.Trim());
	}
}
