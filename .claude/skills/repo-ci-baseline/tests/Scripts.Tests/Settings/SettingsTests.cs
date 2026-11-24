using System.Text;
using System.Text.Json.Nodes;

// Tests for github/SettingsCore.cs against an in-memory GitHub (moved from the pytest suite it replaced, github/tests/test_settings.py).
// FakeGitHub keeps the state the script reads and writes (repo settings, workflows, rulesets, labels, secrets, protection), so a test can
// run --fix and then check that a second run finds nothing left to change. Every write is logged in fake.Writes.
public partial class SettingsTests
{
	const string RepoName = "owner/app";
	static readonly string Github = Skill.Path("github");
	static readonly JsonObject Standard = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Github, "settings.json")))!;
	static readonly JsonArray StandardLabels = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(Github, "labels.json")))!;

	const string PinnedRef = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
	// Only a workflow that runs on every PR can report a required check.
	const string OnPr = "\"on\":\n  pull_request:\n    types: [opened, synchronize]\n";

	// Every standard required check, reported by workflows the way the Template's do.
	static readonly string Ci = $$$"""
			name: Build and Test Suite
			{{{OnPr}}}jobs:
			  branch-name:
			    name: Branch name
			    steps:
			      - uses: {{{PinnedRef}}}
			  build:
			    name: Build Solution
			    steps:
			      - uses: {{{PinnedRef}}}
			  test:
			    name: "Tests: ${{ matrix.test_name }}"
			  report:
			    name: Test Suite

			""";
	static readonly string Lints = $$"""
			{{OnPr}}jobs:
			  actionlint:
			    name: actionlint
			    steps:
			      - uses: docker://rhysd/actionlint@sha256:{{new string('b', 64)}}
			  zizmor:
			    name: zizmor
			  shellcheck:
			    name: shellcheck
			  markdownlint:
			    name: markdownlint
			  yamllint:
			    runs-on: ubuntu-latest
			    steps:
			      - uses: ./.github/actions/local

			""";
	static readonly string PrTitle = OnPr + "jobs:\n  title:\n    name: PR title\n";
	const string Automerge = "jobs:\n  merge-when-ready:\n    steps:\n      - with:\n          script: |\n            const COPILOT_REVIEW_CAP = 3;\n";

	static JsonNode P(string json) => JsonNode.Parse(json)!;

	static List<string> Strings(JsonNode? node) => [.. ((JsonArray)node!).Select(item => (string)item!)];

	static JsonObject MatchingRuleset(IEnumerable<string> checks)
	{
		var ruleset = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Github, "main-rules.json")))!;
		foreach (var rule in (JsonArray)ruleset["rules"]!)
		{
			if ((string)rule!["type"]! == "required_status_checks")
				rule["parameters"]!["required_status_checks"] = new JsonArray([.. checks.Select(c => (JsonNode)new JsonObject { ["context"] = c, ["integration_id"] = 15368 })]);
		}
		var withId = new JsonObject { ["id"] = 7 };
		foreach (var (key, value) in ruleset.ToList())
		{
			ruleset.Remove(key);
			withId[key] = value;
		}
		return withId;
	}

	sealed class FakeGitHub
	{
		public string FullName = RepoName;
		public JsonObject Repo;
		public bool Alerts = true;
		public bool SecurityFixes = true;
		public string DefaultSetup = "not-configured";
		public JsonObject WorkflowPermissions = (JsonObject)Standard["actions_workflow"]!.DeepClone();
		public JsonObject Actions = (JsonObject)P("""{"enabled": true, "allowed_actions": "all", "sha_pinning_required": true}""");
		public OrderedDictionary<string, string> Workflows = new()
		{
			[".github/workflows/ci.yml"] = Ci,
			[".github/workflows/lint.yml"] = Lints,
			[".github/workflows/pr-title.yml"] = PrTitle,
			[".github/workflows/pr-automerge.yml"] = Automerge,
		};
		public OrderedDictionary<int, JsonObject> Rulesets = new() { [7] = MatchingRuleset(Strings(Standard["required_checks"])) };
		public OrderedDictionary<string, JsonObject> Labels = [];
		public List<string> Secrets = Strings(Standard["required_secrets"]);
		public bool Protection;
		public bool RulesetsForbidden;
		public readonly List<(string Method, string Path, JsonNode? Body)> Writes = [];

		public FakeGitHub()
		{
			Repo = DefaultRepo();
			foreach (var label in StandardLabels)
				Labels[(string)label!["name"]!] = (JsonObject)label.DeepClone();
		}

		public static JsonObject DefaultRepo()
		{
			var repo = new JsonObject { ["default_branch"] = "main" };
			foreach (var (key, value) in (JsonObject)Standard["repo"]!)
				repo[key] = value?.DeepClone();
			repo["security_and_analysis"] = P("""{"secret_scanning": {"status": "enabled"}, "secret_scanning_push_protection": {"status": "enabled"}}""");
			return repo;
		}

		static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

		public JsonNode? Api(string method, string path, JsonNode? body = null)
		{
			if (method != "GET")
				Writes.Add((method, path, Copy(body)));
			path = path.StartsWith($"repos/{FullName}", StringComparison.Ordinal) ? path[$"repos/{FullName}".Length..] : path;
			var route = path.Split('?')[0];
			if (route == "" && method == "GET")
				return Copy(Repo);
			if (route == "" && method == "PATCH")
			{
				foreach (var (key, value) in (JsonObject)body!)
				{
					if (key == "security_and_analysis")
					{
						foreach (var (feature, setting) in (JsonObject)value!)
							Repo[key]![feature] = Copy(setting);
					}
					else
						Repo[key] = Copy(value);
				}
				return new JsonObject();
			}
			if (route == "/vulnerability-alerts")
			{
				if (method == "PUT")
				{
					Alerts = true;
					return null;
				}
				if (!Alerts)
					throw new ApiError(404, "Not Found");
				return null;
			}
			if (route == "/automated-security-fixes")
			{
				if (method == "PUT")
				{
					SecurityFixes = true;
					return null;
				}
				return new JsonObject { ["enabled"] = SecurityFixes, ["paused"] = false };
			}
			if (route == "/code-scanning/default-setup")
			{
				if (method == "PATCH")
				{
					DefaultSetup = (string)body!["state"]!;
					return new JsonObject();
				}
				return new JsonObject { ["state"] = DefaultSetup };
			}
			if (route == "/actions/permissions/workflow")
			{
				if (method == "PUT")
				{
					WorkflowPermissions = (JsonObject)Copy(body)!;
					return null;
				}
				return Copy(WorkflowPermissions);
			}
			if (route == "/actions/permissions")
			{
				if (method == "PUT")
				{
					Actions = (JsonObject)Copy(body)!;
					return null;
				}
				return Copy(Actions);
			}
			if (route == "/contents/.github/workflows")
			{
				if (Workflows.Count == 0)
					throw new ApiError(404, "Not Found");
				return new JsonArray([.. Workflows.Keys.Select(p => (JsonNode)new JsonObject { ["name"] = p.Split('/')[^1], ["path"] = p })]);
			}
			if (route.StartsWith("/contents/", StringComparison.Ordinal))
				return new JsonObject { ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Workflows[route["/contents/".Length..]])) };
			if (route == "/rulesets")
			{
				if (RulesetsForbidden)
					throw new ApiError(403, "Upgrade to GitHub Pro");
				if (method == "POST")
				{
					var created = new JsonObject { ["id"] = 99 };
					foreach (var (key, value) in (JsonObject)body!)
						created[key] = Copy(value);
					Rulesets[99] = created;
					return Copy(created);
				}
				return new JsonArray([.. Rulesets.Select(pair => (JsonNode)new JsonObject { ["id"] = pair.Key, ["name"] = Copy(pair.Value["name"]) })]);
			}
			if (route.StartsWith("/rulesets/", StringComparison.Ordinal))
			{
				var id = int.Parse(route.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
				if (method == "PUT")
				{
					// Like GitHub's, an update must carry the whole ruleset.
					var missing = new[] { "conditions", "enforcement", "name", "rules", "target" }.Where(key => !((JsonObject)body!).ContainsKey(key)).ToList();
					if (missing.Count > 0)
						throw new ApiError(422, $"Invalid request: missing {string.Join(", ", missing)}");
					foreach (var (key, value) in (JsonObject)body!)
						Rulesets[id][key] = Copy(value);
					return Copy(Rulesets[id]);
				}
				if (method == "DELETE")
				{
					Rulesets.Remove(id);
					return null;
				}
				return Copy(Rulesets[id]);
			}
			if (route == "/branches/main/protection")
			{
				if (method == "DELETE")
				{
					Protection = false;
					return null;
				}
				if (!Protection)
					throw new ApiError(404, "Branch not protected");
				return new JsonObject();
			}
			if (route == "/labels")
			{
				if (method == "POST")
				{
					Labels[(string)body!["name"]!] = (JsonObject)Copy(body)!;
					return Copy(body);
				}
				return new JsonArray([.. Labels.Values.Select(label => Copy(label))]);
			}
			if (route.StartsWith("/labels/", StringComparison.Ordinal))
			{
				var name = Uri.UnescapeDataString(route["/labels/".Length..]);
				if (method == "DELETE")
				{
					Labels.Remove(name);
					return null;
				}
				foreach (var (key, value) in (JsonObject)body!)
					Labels[name][key] = Copy(value);
				return Copy(Labels[name]);
			}
			if (route == "/actions/secrets")
				return new JsonObject { ["secrets"] = new JsonArray([.. Secrets.Select(n => (JsonNode)new JsonObject { ["name"] = n })]) };
			throw new InvalidOperationException($"unexpected call: {method} {path}");
		}

		public bool Wrote(string method, string path, JsonNode? body) =>
				Writes.Any(write => write.Method == method && write.Path == path && JsonNode.DeepEquals(write.Body, body));
	}

	static (int Code, string Output) Run(FakeGitHub fake, params string[] args) => RunIn(Github, fake, args);

	static (int Code, string Output) RunIn(string folder, FakeGitHub fake, params string[] args)
	{
		using var output = new StringWriter();
		using var err = new StringWriter();
		var code = Settings.Run([.. args, RepoName], output, err, fake.Api, folder);
		return (code, output.ToString() + err.ToString());
	}

	static Dictionary<string, string> Statuses(FakeGitHub fake, string? folder = null)
	{
		Dictionary<string, string> statuses = [];
		foreach (var finding in Settings.Check(fake.Api, folder ?? Github, RepoName))
			statuses[finding.Area] = finding.Status;
		return statuses;
	}

	static Dictionary<string, Finding> Findings(FakeGitHub fake)
	{
		Dictionary<string, Finding> findings = [];
		foreach (var finding in Settings.Check(fake.Api, Github, RepoName))
			findings[finding.Area] = finding;
		return findings;
	}

	// A copy of github/ (its tests left out), for a test that changes repos.txt or a per-repo file, as the pytest suite's copytree did.
	static string CopyGithub()
	{
		var folder = Directory.CreateTempSubdirectory("settings-test.").FullName;
		foreach (var file in Directory.EnumerateFiles(Github, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(Github, file);
			if (relative.StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				continue;
			Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, relative))!);
			File.Copy(file, Path.Combine(folder, relative));
		}
		return folder;
	}

	static List<string> RequiredContexts(JsonObject ruleset) =>
			[.. ((JsonArray)ruleset["rules"]!).Where(rule => (string)rule!["type"]! == "required_status_checks")
					.Single()!["parameters"]!["required_status_checks"]!.AsArray().Select(check => (string)check!["context"]!)];

	static JsonObject Created(FakeGitHub fake) =>
			(JsonObject)fake.Writes.First(write => write.Method == "POST" && write.Path.EndsWith("/rulesets", StringComparison.Ordinal)).Body!;

	static JsonObject RepoWith(params (string Key, JsonNode? Value)[] changes)
	{
		var repo = FakeGitHub.DefaultRepo();
		foreach (var (key, value) in changes)
			repo[key] = value;
		return repo;
	}

	// ── The happy path and idempotence ──────────────────────────────────────────

	[Fact]
	public void A_repo_at_the_standard_passes_and_fix_writes_nothing()
	{
		var fake = new FakeGitHub();

		var (status, output) = Run(fake, "--fix");

		Assert.True(status == 0, output);
		Assert.Empty(fake.Writes);
	}

	[Fact]
	public void Fix_brings_a_drifted_repo_to_the_standard_and_a_second_fix_writes_nothing()
	{
		var fake = new FakeGitHub
		{
			Repo = RepoWith(("allow_merge_commit", true), ("squash_merge_commit_message", "COMMIT_MESSAGES"),
					("security_and_analysis", P("""{"secret_scanning": {"status": "disabled"}, "secret_scanning_push_protection": {"status": "disabled"}}"""))),
			Alerts = false,
			SecurityFixes = false,
			WorkflowPermissions = (JsonObject)P("""{"default_workflow_permissions": "write", "can_approve_pull_request_reviews": false}"""),
			Actions = (JsonObject)P("""{"enabled": true, "allowed_actions": "all", "sha_pinning_required": false}"""),
			Rulesets = [],
			Labels = [],
		};

		var (first, output) = Run(fake, "--fix");
		var writes = fake.Writes.Count;
		var (second, _) = Run(fake, "--fix");

		Assert.True(first == 0, output);
		Assert.Equal(0, second);
		Assert.Equal(writes, fake.Writes.Count);
	}

	[Fact]
	public void Check_only_never_writes()
	{
		var fake = new FakeGitHub { Rulesets = [], Labels = [], Alerts = false };

		var (status, _) = Run(fake);

		Assert.Equal(1, status);
		Assert.Empty(fake.Writes);
	}

	// ── Repo settings ───────────────────────────────────────────────────────────

	[Fact]
	public void Repo_settings_drift_patches_only_the_differing_keys()
	{
		var fake = new FakeGitHub { Repo = RepoWith(("allow_rebase_merge", true), ("squash_merge_commit_title", "COMMIT_OR_PR_TITLE")) };

		Run(fake, "--fix");

		Assert.True(fake.Wrote("PATCH", $"repos/{RepoName}", P("""{"allow_rebase_merge": false, "squash_merge_commit_title": "PR_TITLE"}""")));
	}

	// Release-blog and Dependabot PRs arm native auto-merge, which fails where the repo disallows it.
	[Theory]
	[InlineData("full")]
	[InlineData("repo-settings-only")]
	public void Auto_merge_disallowed_is_drift_and_fix_allows_it(string scope)
	{
		var folder = Github;
		if (scope != "full")
		{
			folder = CopyGithub();
			File.WriteAllText(Path.Combine(folder, "repos.txt"), $"{RepoName} {scope}\n");
		}
		var fake = new FakeGitHub { Repo = RepoWith(("allow_auto_merge", false)) };

		var (checkedCode, output) = RunIn(folder, fake);

		Assert.Equal(1, checkedCode);
		Assert.Contains("allow_auto_merge: False → True", output, StringComparison.Ordinal);
		Assert.Empty(fake.Writes);

		var (fixedCode, fixedOutput) = RunIn(folder, fake, "--fix");

		Assert.True(fixedCode == 0, fixedOutput);
		Assert.Single(fake.Writes);
		Assert.True(fake.Wrote("PATCH", $"repos/{RepoName}", P("""{"allow_auto_merge": true}""")));
	}

	// ── Ruleset ─────────────────────────────────────────────────────────────────

	[Fact]
	public void A_missing_ruleset_is_created_requiring_only_checks_a_workflow_reports()
	{
		var fake = new FakeGitHub { Rulesets = [], Workflows = new() { [".github/workflows/ci.yml"] = Ci } };

		var (status, output) = Run(fake, "--fix");

		Assert.Equal(["Build Solution", "Test Suite", "Branch name"], RequiredContexts(Created(fake)));
		Assert.True(status == 1, "the checks no workflow reports leave a MANUAL finding");
		Assert.Contains("no workflow on the default branch reports PR title, actionlint", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_repo_with_no_workflows_gets_a_ruleset_without_the_checks_rule()
	{
		var fake = new FakeGitHub { Rulesets = [], Workflows = [] };

		Run(fake, "--fix");

		var created = (JsonObject)fake.Writes.First(write => write.Method == "POST").Body!;
		Assert.DoesNotContain("required_status_checks", ((JsonArray)created["rules"]!).Select(rule => (string)rule!["type"]!));
	}

	[Fact]
	public void Ruleset_drift_is_replaced_with_the_standard()
	{
		var live = MatchingRuleset(Strings(Standard["required_checks"]));
		live["bypass_actors"] = P("""[{"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "pull_request"}]""");
		((JsonArray)live["rules"]!).Add(P("""{"type": "code_quality", "parameters": {"severity": "errors"}}"""));
		foreach (var rule in (JsonArray)live["rules"]!)
		{
			if ((string)rule!["type"]! == "pull_request")
			{
				rule["parameters"]!["required_review_thread_resolution"] = true;
				rule["parameters"]!["allowed_merge_methods"] = P("""["rebase", "squash"]""");
			}
		}
		var fake = new FakeGitHub { Rulesets = new() { [7] = live } };

		var findings = Findings(fake);
		Run(fake, "--fix");

		var detail = findings["ruleset main-rules"].Detail;
		foreach (var expected in new[] { "bypass_actors", "rule code_quality: remove", "required_review_thread_resolution: true → false",
				"""allowed_merge_methods: ["rebase", "squash"] → ["squash"]""" })
			Assert.Contains(expected, detail, StringComparison.Ordinal);
		Assert.Equal(Settings.OK, Statuses(fake)["ruleset main-rules"]);
		Assert.DoesNotContain("code_quality", ((JsonArray)fake.Rulesets[7]["rules"]!).Select(rule => (string)rule!["type"]!));
	}

	// Before a Standardize, the repo's old test gate is its only one.
	[Fact]
	public void A_required_check_outside_the_standard_stays_while_a_workflow_reports_it()
	{
		var oldCi = OnPr + "jobs:\n  gate:\n    name: All Tests Passed\n";
		var live = MatchingRuleset(["All Tests Passed", "Gone Check"]);
		var fake = new FakeGitHub { Rulesets = new() { [7] = live }, Workflows = new() { [".github/workflows/old.yml"] = oldCi } };

		Run(fake, "--fix");

		Assert.Equal(["All Tests Passed"], RequiredContexts(fake.Rulesets[7]));
	}

	[Fact]
	public void A_required_check_from_another_app_is_drift()
	{
		var live = MatchingRuleset(Strings(Standard["required_checks"]));
		foreach (var rule in (JsonArray)live["rules"]!)
		{
			if ((string)rule!["type"]! == "required_status_checks")
				rule["parameters"]!["required_status_checks"]![0]!.AsObject().Remove("integration_id");
		}
		var fake = new FakeGitHub { Rulesets = new() { [7] = live } };

		Assert.Equal(Settings.Drift, Statuses(fake)["ruleset main-rules"]);
		Run(fake, "--fix");
		Assert.Equal(Settings.OK, Statuses(fake)["ruleset main-rules"]);
	}

	[Fact]
	public void A_suspended_ruleset_fails_the_check_and_fix_restores_it()
	{
		var fake = new FakeGitHub();
		var rulesBefore = fake.Rulesets[7]["rules"]!.DeepClone();

		Assert.Equal(0, Run(fake, "--suspend").Code);
		Assert.Equal("disabled", (string)fake.Rulesets[7]["enforcement"]!);
		Assert.True(JsonNode.DeepEquals(rulesBefore, fake.Rulesets[7]["rules"]), "suspending keeps every rule");
		var (status, output) = Run(fake);
		Assert.True(status == 1 && output.Contains("SUSPENDED", StringComparison.Ordinal));

		Run(fake, "--fix");
		Assert.Equal("active", (string)fake.Rulesets[7]["enforcement"]!);
	}

	[Fact]
	public void Rulesets_unavailable_is_reported_but_doesnt_fail()
	{
		var fake = new FakeGitHub { RulesetsForbidden = true };

		var (status, output) = Run(fake);

		Assert.True(status == 0, output);
		Assert.Contains("UNAVAILABLE ruleset main-rules", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Other_rulesets_are_left_alone_and_legacy_ones_deleted_only_on_request()
	{
		var fake = new FakeGitHub();
		fake.Rulesets[8] = (JsonObject)P("""{"id": 8, "name": "dev-rules", "rules": []}""");
		fake.Rulesets[9] = (JsonObject)P("""{"id": 9, "name": "release-rules", "rules": []}""");

		Assert.Equal(Settings.Legacy, Statuses(fake)["ruleset dev-rules"]);
		Run(fake, "--fix");
		Assert.True(fake.Rulesets.ContainsKey(8));

		Run(fake, "--fix", "--remove-legacy");
		Assert.False(fake.Rulesets.ContainsKey(8));
		Assert.True(fake.Rulesets.ContainsKey(9));
	}

	[Fact]
	public void Classic_protection_is_removed_only_on_request_and_only_with_main_rules_active()
	{
		var fake = new FakeGitHub { Protection = true, Rulesets = [] };

		Run(fake, "--fix", "--remove-legacy");
		Assert.True(fake.Protection, "main-rules was only created in this run");

		Run(fake, "--fix", "--remove-legacy");
		Assert.False(fake.Protection);
	}

	// ── Actions ─────────────────────────────────────────────────────────────────

	[Fact]
	public void Pinning_is_required_only_once_every_workflow_is_pinned()
	{
		var unpinned = Ci + "  extra:\n    steps:\n      - uses: actions/setup-node@v4\n";
		var fake = new FakeGitHub
		{
			Actions = (JsonObject)P("""{"enabled": true, "allowed_actions": "all", "sha_pinning_required": false}"""),
			Workflows = new() { [".github/workflows/ci.yml"] = unpinned },
		};

		var findings = Findings(fake);
		Run(fake, "--fix");

		Assert.Equal(Settings.Manual, findings["pinned actions"].Status);
		Assert.Contains("actions/setup-node@v4", findings["pinned actions"].Detail, StringComparison.Ordinal);
		Assert.False((bool)fake.Actions["sha_pinning_required"]!);
	}

	[Fact]
	public void Unpinned_actions_are_reported_even_once_pinning_is_required()
	{
		var fake = new FakeGitHub();
		fake.Workflows[".github/workflows/extra.yml"] = OnPr + "jobs:\n  node:\n    steps:\n      - uses: actions/setup-node@v4\n";

		var finding = Settings.Check(fake.Api, Github, RepoName).First(f => f.Area == "pinned actions");

		Assert.Equal(Settings.Manual, finding.Status);
		Assert.Equal("required, so GitHub refuses to run these until they're pinned to a commit SHA: actions/setup-node@v4", finding.Detail);
	}

	// A scheduled workflow, or one a PR can skip by its paths, never reports on some PRs.
	[Fact]
	public void Only_workflows_that_run_on_every_pr_can_report_a_required_check()
	{
		var fake = new FakeGitHub
		{
			Workflows = new()
			{
				[".github/workflows/ci.yml"] = Ci.Replace(OnPr, "\"on\":\n  schedule:\n    - cron: \"0 0 * * *\"\n", StringComparison.Ordinal),
				[".github/workflows/lint.yml"] = Lints.Replace(OnPr, "\"on\":\n  pull_request:\n    paths-ignore:\n      - \"docs/**\"\n", StringComparison.Ordinal),
				[".github/workflows/pr-title.yml"] = PrTitle,
			},
			Rulesets = [],
		};

		Run(fake, "--fix");

		Assert.Equal(["PR title"], RequiredContexts(fake.Rulesets[99]));
	}

	[Theory]
	[InlineData("\"on\":\n  pull_request:\n    types: [opened, synchronize]\n  push:\n    branches: [main]\n", true)]
	[InlineData("on:\n  pull_request:\n    types:\n      - opened\n      - edited\n      - synchronize\n", true)]
	[InlineData("on:\n  pull_request:\n    types: [opened]\n", false)] // a pushed head gets no check
	[InlineData("on:\n  pull_request:\n    types: [closed]\n", false)] // release.yml
	[InlineData("on:\n  pull_request:\n    branches: [main]\n", true)]
	[InlineData("on:\n  pull_request:\n    branches:\n      - 'release/**'\n      - main\n", true)]
	[InlineData("on:\n  pull_request:\n    branches: ['**']\n", true)]
	[InlineData("on:\n  pull_request:\n    branches: [develop]\n", false)]
	[InlineData("on:\n  pull_request:\n    branches: ['m*', '!main']\n", false)]
	[InlineData("on:\n  pull_request:\n    branches-ignore: [main]\n", false)]
	[InlineData("on:\n  pull_request:\n    branches-ignore: ['release/*']\n", true)]
	[InlineData("on:\n  pull_request:\n    branches: ['[m]ain']\n", true)]
	[InlineData("on: {pull_request: {paths-ignore: ['docs/**']}}\n", false)] // flow mappings aren't parsed
	[InlineData("on: {pull_request: {}}\n", false)]
	[InlineData("on:\n  pull_request:\n  # a comment between triggers\n  push:\n", true)]
	[InlineData("on: pull_request\n", true)]
	[InlineData("on: [push, pull_request]\n", true)]
	[InlineData("on:\n  pull_request:\n    paths-ignore:\n      - docs/**\n", false)]
	[InlineData("on:\n  pull_request:\n    paths:\n      - src/**\n", false)]
	[InlineData("on:\n  pull_request_target:\n    types: [opened]\n", false)]
	[InlineData("on: [push, pull_request_target]\n", false)]
	[InlineData("on:\n  schedule:\n    - cron: \"0 0 * * *\"\n", false)]
	[InlineData("jobs:\n  a:\n    name: A\n", false)]
	public void Runs_on_every_pr(string text, bool runs) => Assert.Equal(runs, Settings.RunsOnEveryPr(text));

	[Theory]
	[InlineData("main", "main", true)]
	[InlineData("mai", "main", false)]
	[InlineData("m*", "main", true)]
	[InlineData("*", "release/1", false)] // * stops at /
	[InlineData("**", "release/1", true)]
	[InlineData("release/**", "release/1/hotfix", true)]
	[InlineData("mains?", "main", true)] // ? is zero or one of the character before it
	[InlineData("mains?", "mains", true)]
	[InlineData("ma?in", "main", true)]
	[InlineData("mai?n", "man", true)]
	[InlineData("mai?n", "maxn", false)] // ? isn't any one character
	[InlineData("ma+in", "maaain", true)] // + is one or more of the character before it
	[InlineData("ma+in", "min", false)]
	[InlineData("[m]ain", "main", true)]
	[InlineData("[a-z]ain", "main", true)]
	[InlineData("[0-9]ain", "main", false)]
	[InlineData("v1.0", "v1x0", false)] // . is literal
	[InlineData("feature\\*", "feature*", true)] // \ escapes
	[InlineData("feature\\*", "feature-x", false)]
	[InlineData("?main", "main", false)] // nothing before ?: a literal ?
	public void Branch_patterns_follow_github_filter_syntax(string pattern, string branch, bool matches) =>
			Assert.Equal(matches, Settings.BranchMatches(pattern, branch));

	[Fact]
	public void A_branch_filter_is_read_against_the_default_branch()
	{
		var text = "on:\n  pull_request:\n    branches: [develop]\n";

		Assert.True(Settings.RunsOnEveryPr(text, "develop"));
		Assert.False(Settings.RunsOnEveryPr(text, "main"));
	}

	[Fact]
	public void Codeql_default_setup_is_turned_off_where_the_workflow_runs_codeql()
	{
		var fake = new FakeGitHub { DefaultSetup = "configured" };
		fake.Workflows[".github/workflows/codeql-analysis.yml"] = "jobs:\n  analyze:\n    name: Analyze\n";

		Run(fake, "--fix");

		Assert.Equal("not-configured", fake.DefaultSetup);
	}

	// ── Labels and secrets ──────────────────────────────────────────────────────

	[Fact]
	public void Labels_are_created_and_corrected_and_squad_labels_deleted()
	{
		var fake = new FakeGitHub();
		fake.Labels.Remove((string)StandardLabels[0]!["name"]!);
		fake.Labels["docs-only"]["color"] = "ededed";
		fake.Labels["squad"] = (JsonObject)P("""{"name": "squad", "color": "000000", "description": ""}""");
		fake.Labels["squad:mal"] = (JsonObject)P("""{"name": "squad:mal", "color": "000000", "description": ""}""");
		fake.Labels["bug"] = (JsonObject)P("""{"name": "bug", "color": "d73a4a", "description": ""}""");

		Assert.Equal(Settings.Drift, Statuses(fake)["label squad:mal"]);
		var (status, output) = Run(fake, "--fix");

		Assert.True(status == 0, output);
		Assert.Equal((string)StandardLabels[0]!["color"]!, (string)fake.Labels[(string)StandardLabels[0]!["name"]!]["color"]!);
		Assert.Equal("0075CA", (string)fake.Labels["docs-only"]["color"]!);
		Assert.True(fake.Wrote("PATCH", $"repos/{RepoName}/labels/docs-only", new JsonObject { ["color"] = "0075CA", ["description"] = StandardLabels[2]!["description"]!.DeepClone() }));
		Assert.False(fake.Labels.ContainsKey("squad") || fake.Labels.ContainsKey("squad:mal"));
		Assert.True(fake.Wrote("DELETE", $"repos/{RepoName}/labels/squad%3Amal", null));
		Assert.True(fake.Labels.ContainsKey("bug"), "labels outside the standard and not retired are left alone");
	}

	[Fact]
	public void Fix_creates_the_nuget_and_dotnet_sdk_labels_the_seed_dependabot_uses()
	{
		string[] wanted = ["nuget", "dotnet-sdk"];
		var fake = new FakeGitHub();
		foreach (var name in wanted)
			fake.Labels.Remove(name);

		Assert.Equal(Settings.Drift, Statuses(fake)["label nuget"]);
		Assert.Equal(Settings.Drift, Statuses(fake)["label dotnet-sdk"]);
		var (status, output) = Run(fake, "--fix");

		Assert.True(status == 0, output);
		foreach (var name in wanted)
		{
			Assert.True(fake.Labels.ContainsKey(name), $"--fix creates the {name} label");
			Assert.True(J.B(fake.Labels[name]["color"]) && J.B(fake.Labels[name]["description"]));
			Assert.True(fake.Wrote("POST", $"repos/{RepoName}/labels", fake.Labels[name]));
		}
	}

	[Fact]
	public void The_review_cap_is_checked_in_pr_automerge()
	{
		Assert.Equal(Settings.OK, Statuses(new FakeGitHub())["review cap"]);

		foreach (var (text, expected) in new (string?, string)[] { (Automerge.Replace("= 3;", "= 5;", StringComparison.Ordinal), "cap 5"),
				("jobs:\n  merge:\n    name: Merge\n", "no review cap"), (null, "no pr-automerge.yml") })
		{
			var fake = new FakeGitHub();
			if (text is null)
				fake.Workflows.Remove(".github/workflows/pr-automerge.yml");
			else
				fake.Workflows[".github/workflows/pr-automerge.yml"] = text;
			var finding = Settings.Check(fake.Api, Github, RepoName).First(f => f.Area == "review cap");
			Assert.True(finding.Status == Settings.Manual && finding.Detail.Contains(expected, StringComparison.Ordinal), finding.Detail);
		}
	}

	[Fact]
	public void A_missing_secret_needs_a_person()
	{
		var fake = new FakeGitHub { Secrets = [] };

		var (status, output) = Run(fake, "--fix");

		Assert.Equal(1, status);
		Assert.Contains("MANUAL      secret RELEASE_PR_PAT", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Claude_reviews_token_is_checked_by_name()
	{
		var missing = Settings.Check(new FakeGitHub { Secrets = ["RELEASE_PR_PAT"] }.Api, Github, RepoName).First(f => f.Area == "secret CLAUDE_CODE_OAUTH_TOKEN");
		var present = Settings.Check(new FakeGitHub { Secrets = ["RELEASE_PR_PAT", "CLAUDE_CODE_OAUTH_TOKEN"] }.Api, Github, RepoName)
				.First(f => f.Area == "secret CLAUDE_CODE_OAUTH_TOKEN");

		Assert.Equal(Settings.Manual, missing.Status);
		Assert.Equal(Settings.OK, present.Status);
	}

	// ── Scope, per-repo additions and options ───────────────────────────────────

	[Fact]
	public void A_repo_settings_only_repo_gets_just_its_merge_settings()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos.txt"), $"{RepoName} repo-settings-only\n");
		var fake = new FakeGitHub
		{
			RulesetsForbidden = true,
			Secrets = [],
			Labels = new() { ["squad:frodo"] = (JsonObject)P("""{"name": "squad:frodo", "color": "000000", "description": ""}""") },
		};

		var (status, output) = RunIn(folder, fake, "--fix");

		Assert.True(status == 0, output);
		Assert.Single(fake.Writes);
		Assert.True(fake.Wrote("DELETE", $"repos/{RepoName}/labels/squad%3Afrodo", null),
				"only the retired label goes; nothing else outside the merge settings is touched");
	}

	[Fact]
	public void A_factory_scope_repo_gets_every_check_of_a_full_repo()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos.txt"), $"{RepoName} factory\n");
		var fake = new FakeGitHub
		{
			Repo = RepoWith(("security_and_analysis", P("""{"secret_scanning": {"status": "disabled"}, "secret_scanning_push_protection": {"status": "disabled"}}"""))),
		};
		fake.Actions["sha_pinning_required"] = false;
		fake.WorkflowPermissions["can_approve_pull_request_reviews"] = false;

		var (status, output) = RunIn(folder, fake);

		Assert.True(status == 1, output);
		foreach (var drift in new[] { "secret_scanning", "pinned actions", "can_approve_pull_request_reviews" })
			Assert.Contains(drift, output, StringComparison.Ordinal);
		Assert.DoesNotContain("repo settings and retired labels only", output, StringComparison.Ordinal);
	}

	[Fact]
	public void A_factory_scope_repo_reports_unreportable_required_checks_as_information()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos.txt"), $"{RepoName} factory\n");
		var fake = new FakeGitHub
		{
			Rulesets = [],
			Workflows = new() { [".github/workflows/ci.yml"] = Ci, [".github/workflows/pr-automerge.yml"] = Automerge },
		};

		var (status, output) = RunIn(folder, fake, "--fix");

		Assert.True(status == 0, output);
		Assert.Contains("INFO        required checks: no workflow on the default branch reports PR title, actionlint", output, StringComparison.Ordinal);
		Assert.DoesNotContain("MANUAL", output, StringComparison.Ordinal);
		Assert.True(RequiredContexts(Created(fake)).SequenceEqual(["Build Solution", "Test Suite", "Branch name"]),
				"only the checks a workflow reports are required; the information line adds none");
	}

	[Fact]
	public void A_per_repo_file_can_add_a_required_check()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos", "app.json"), """{"required_checks": ["Sandcastle"]}""");
		var fake = new FakeGitHub();
		fake.Workflows[".github/workflows/sandcastle.yml"] = OnPr + "jobs:\n  sandcastle:\n    name: Sandcastle\n";

		RunIn(folder, fake, "--fix");

		Assert.Contains("Sandcastle", RequiredContexts(fake.Rulesets[7]));
	}

	[Fact]
	public void A_per_repo_file_cant_loosen_the_standard()
	{
		var folder = CopyGithub();
		File.WriteAllText(Path.Combine(folder, "repos", "app.json"), """{"repo": {"allow_merge_commit": true}}""");

		var error = Assert.Throws<StandardError>(() => Settings.Check(new FakeGitHub().Api, folder, RepoName));
		Assert.Contains("only required_checks and labels", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Remove_legacy_needs_fix()
	{
		using var err = new StringWriter();
		Assert.Equal(2, Settings.Run(["--remove-legacy", RepoName], new StringWriter(), err, new FakeGitHub().Api, Github));
	}

	[Fact]
	public void Json_output_lists_each_finding()
	{
		var fake = new FakeGitHub { Secrets = [] };

		var (status, output) = Run(fake, "--json");

		var findings = (JsonArray)JsonNode.Parse(output)![RepoName]!;
		Assert.Equal(1, status);
		Assert.Contains(findings, finding => JsonNode.DeepEquals(finding,
				P("""{"area": "secret RELEASE_PR_PAT", "status": "MANUAL", "detail": "missing; add it in the repo's Actions secrets"}""")));
	}

	// ── Reading workflows ───────────────────────────────────────────────────────

	[Fact]
	public void Workflow_facts_reads_job_names_and_unpinned_actions()
	{
		var (names, unpinned) = Settings.WorkflowFacts(Ci + Lints.Replace(OnPr + "jobs:\n", "", StringComparison.Ordinal)
				+ "  node:\n    steps:\n      - uses: actions/setup-node@v4\n");

		Assert.Equal(["Branch name", "Build Solution", "Test Suite", "actionlint", "zizmor", "shellcheck", "markdownlint", "yamllint", "node"], names);
		Assert.Equal(["actions/setup-node@v4"], unpinned);
	}
}
