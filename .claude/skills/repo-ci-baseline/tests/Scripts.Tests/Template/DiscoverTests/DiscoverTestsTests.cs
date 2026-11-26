using System.Text.Json;

namespace Baseline.Tests;

// The Template's .github/scripts/DiscoverTests.cs: test_discover_tests.py's tests, one method each under its name (ADR 0017's rule), with
// the same cases and assertions, on throwaway folders. In Baseline's namespace, so Script and Arguments are the Template's, not github/'s.
public sealed class DiscoverTestsTests : IDisposable
{
	const string True = "<IsTestProject>true</IsTestProject>";

	const string ImportParent = "<Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />";

	// pytest's tmp_path: a folder of the test's own, removed when it ends.
	readonly string tmp = Directory.CreateTempSubdirectory("discover-tests.").FullName;

	public void Dispose() => Directory.Delete(tmp, true);

	static void WriteFile(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	// Writes tests/<folder>/<folder>.csproj under root, holding body; returns its path.
	static string Project(string root, string folder, string body = "")
	{
		var path = Path.Join(root, "tests", folder, $"{folder}.csproj");
		WriteFile(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>{body}</PropertyGroup>\n</Project>\n");
		return path;
	}

	static void Props(string directory, string body) =>
			WriteFile(Path.Join(directory, "Directory.Build.props"), $"<Project>\n  <PropertyGroup>{body}</PropertyGroup>\n</Project>\n");

	static void PropsFile(string directory, string body, string outside = "") =>
			WriteFile(Path.Join(directory, "Directory.Build.props"), $"<Project>\n  {outside}\n  <PropertyGroup>{body}</PropertyGroup>\n</Project>\n");

	static string When(string condition, string body) => $"<PropertyGroup Condition=\"{condition}\">{body}</PropertyGroup>";

	static List<string> Names(string root, TextWriter? log = null) =>
			[.. DiscoverTests.Discover(root, log ?? new StringWriter()).Entries.Select(entry => entry.TestName)];

	// main(argv): (exit code, stdout, stderr).
	static (int Code, string Stdout, string Stderr) RunMain(params string[] args)
	{
		StringWriter stdout = new(), stderr = new();
		var code = DiscoverTests.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	// main(["--root", root, "--output", <a file in tmp>]): (exit code, the output's text, or "" when none was written, stdout).
	(int Code, string Output, string Stdout) Run(string root)
	{
		var output = Path.Join(tmp, "github_output");
		var (code, stdout, _) = RunMain("--root", root, "--output", output);
		return (code, File.Exists(output) ? File.ReadAllText(output) : "", stdout);
	}

	[Fact]
	public void A_csproj_that_sets_is_test_project_is_a_test_project()
	{
		Project(tmp, "App.Tests", True);

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void A_project_setting_nothing_is_a_helper_library()
	{
		Project(tmp, "App.Tests", True);
		Project(tmp, "TestingSupport.Library");

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void Is_test_project_set_in_tests_directory_build_props_counts()
	{
		// atelier-store sets it once for every test project.
		Props(Path.Join(tmp, "tests"), True);
		Project(tmp, "Web.Tests.Unit");
		Project(tmp, "Web.Tests.Bunit");

		Assert.Equal(["Web.Tests.Bunit", "Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void The_csproj_overrides_directory_build_props()
	{
		Props(Path.Join(tmp, "tests"), True);
		Project(tmp, "Web.Tests.Unit");
		Project(tmp, "TestingSupport.Library", "<IsTestProject>false</IsTestProject>");

		Assert.Equal(["Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void Only_the_nearest_directory_build_props_is_read()
	{
		// MSBuild imports just the nearest one, so it hides tests/Directory.Build.props.
		Props(Path.Join(tmp, "tests"), True);
		Props(Path.Join(tmp, "tests", "Web.Tests.Unit"), "<Nullable>enable</Nullable>");
		Project(tmp, "Web.Tests.Unit");
		Project(tmp, "Web.Tests.Bunit");

		Assert.Equal(["Web.Tests.Bunit"], Names(tmp));
	}

	[Fact]
	public void A_nearer_directory_build_props_that_imports_its_parent_inherits_it()
	{
		Props(Path.Join(tmp, "tests"), True);
		PropsFile(Path.Join(tmp, "tests", "Web.Tests.Unit"), "<Nullable>enable</Nullable>", ImportParent);
		Project(tmp, "Web.Tests.Unit");

		Assert.Equal(["Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void The_nearer_directory_build_props_value_wins_over_its_parent()
	{
		Props(Path.Join(tmp, "tests"), True);
		PropsFile(Path.Join(tmp, "tests", "Helpers"), "<IsTestProject>false</IsTestProject>", ImportParent);
		Project(tmp, "Helpers");
		Project(tmp, "Web.Tests.Unit");

		Assert.Equal(["Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void A_root_directory_build_props_counts()
	{
		Props(tmp, True);
		Project(tmp, "Web.Tests.Unit");

		Assert.Equal(["Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void A_conditional_is_test_project_applies_only_where_the_condition_holds()
	{
		// Blazor-Server's root Directory.Build.props.
		PropsFile(tmp, "", When("$(MSBuildProjectName.Contains('.Tests'))", True));
		Project(tmp, "Domain.Tests.Unit");
		Project(tmp, "TestingSupport.Library");

		Assert.Equal(["Domain.Tests.Unit"], Names(tmp));
	}

	[Theory]
	[InlineData("$(MSBuildProjectName.Contains('.Tests'))", "Web.Tests.Unit")]
	[InlineData("$(MSBuildProjectName.StartsWith('Web.'))", "Web.Tests.Unit")]
	[InlineData("$(MSBuildProjectName.EndsWith('.Unit'))", "Web.Tests.Unit")]
	// MSBuild compares with == and != case-insensitively.
	[InlineData("'$(MSBuildProjectName)' == 'web.tests.unit'", "Web.Tests.Unit")]
	[InlineData("'$(MSBuildProjectName)' != 'TestingSupport.Library'", "Web.Tests.Unit")]
	[InlineData("!$(MSBuildProjectName.Contains('Support'))", "Web.Tests.Unit")]
	public void Each_condition_shape_is_evaluated(string condition, string expected)
	{
		PropsFile(Path.Join(tmp, "tests"), "", When(condition, True));
		Project(tmp, "Web.Tests.Unit");
		Project(tmp, "TestingSupport.Library");

		Assert.Equal([expected], Names(tmp));
	}

	[Fact]
	public void A_condition_on_the_property_itself_is_evaluated()
	{
		Props(Path.Join(tmp, "tests"), "<IsTestProject Condition=\"$(MSBuildProjectName.Contains('.Tests'))\">true</IsTestProject>");
		Project(tmp, "App.Tests");
		Project(tmp, "TestingSupport.Library");

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void A_condition_that_does_not_hold_leaves_the_earlier_value()
	{
		PropsFile(Path.Join(tmp, "tests"),
				$"{True}</PropertyGroup>\n  {When("'$(MSBuildProjectName)' == 'Helpers'", "<IsTestProject>false</IsTestProject>")}\n  <PropertyGroup>");
		Project(tmp, "Helpers");
		Project(tmp, "Web.Tests.Unit");

		Assert.Equal(["Web.Tests.Unit"], Names(tmp));
	}

	[Fact]
	public void An_unknown_condition_counts_as_holding_and_is_logged()
	{
		PropsFile(tmp, "", When("'$(Configuration)' == 'Debug'", True));
		Project(tmp, "App.Tests");
		var log = new StringWriter();

		Assert.Equal(["App.Tests"], Names(tmp, log));
		var output = log.ToString();
		Assert.Contains("::notice::", output);
		Assert.Contains("'$(Configuration)' == 'Debug'", output);
	}

	[Fact]
	public void A_project_with_the_msbuild_namespace_is_read()
	{
		WriteFile(Path.Join(tmp, "tests", "App.Tests", "App.Tests.csproj"),
				"﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
				"<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>\n");

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void Property_names_ignore_case()
	{
		Project(tmp, "App.Tests", "<isTestProject>true</isTestProject>");

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void Value_matching_ignores_case_and_whitespace()
	{
		Project(tmp, "App.Tests", "<IsTestProject> True </IsTestProject>");

		Assert.Equal(["App.Tests"], Names(tmp));
	}

	[Fact]
	public void A_matrix_entry_carries_the_project_settings()
	{
		Project(tmp, "Web.Tests.E2E", $"{True}<X Include=\"Microsoft.Playwright\" />");

		var entry = Assert.Single(DiscoverTests.Discover(tmp, new StringWriter()).Entries);

		Assert.Equal(new MatrixEntry("tests/Web.Tests.E2E/Web.Tests.E2E.csproj", "Web.Tests.E2E", "web-tests-e2e", true, true, true, "normal"), entry);
		Assert.Equal("{\"include\": [{\"project_path\": \"tests/Web.Tests.E2E/Web.Tests.E2E.csproj\", \"test_name\": \"Web.Tests.E2E\", \"test_id\": \"web-tests-e2e\", " +
				"\"build_before_test\": true, \"no_build\": true, \"requires_playwright\": true, \"verbosity\": \"normal\"}]}", DiscoverTests.Matrix([entry]));
	}

	[Fact]
	public void Main_writes_the_matrix_and_has_tests()
	{
		var root = Path.Join(tmp, "repo");
		Project(root, "App.Tests", True);

		var (code, output, _) = Run(root);

		Assert.Equal(0, code);
		var lines = output.Split('\n')[..^1];
		Assert.Equal("matrix<<EOF", lines[0]);
		var include = JsonDocument.Parse(lines[1]).RootElement.GetProperty("include");
		Assert.Equal(["App.Tests"], include.EnumerateArray().Select(entry => entry.GetProperty("test_name").GetString()));
		Assert.Equal(["EOF", "has_tests=true"], lines[2..]);
	}

	[Fact]
	public void Main_skips_tests_when_there_are_no_projects()
	{
		var root = Path.Join(tmp, "repo");
		Directory.CreateDirectory(root);

		var (code, output, _) = Run(root);

		Assert.Equal(0, code);
		Assert.Equal("has_tests=false", output.Split('\n')[^2]);
	}

	[Fact]
	public void Main_fails_when_projects_exist_but_none_is_a_test_project()
	{
		// A skipped matrix passes "Test Suite", so this must not look like a repo without tests.
		var root = Path.Join(tmp, "repo");
		Project(root, "Web.Tests.Unit");

		var (code, output, stdout) = Run(root);

		Assert.Equal(1, code);
		Assert.Equal("", output);
		Assert.Contains("::error::", stdout);
	}

	[Fact]
	public void List_prints_each_test_project_path()
	{
		// scripts/gate.sh reads these, so it tests what CI tests, at any depth under tests/.
		var root = Path.Join(tmp, "repo");
		Project(root, "App.Tests", True);
		Project(root, "TestingSupport.Library");
		WriteFile(Path.Join(root, "tests", "Integration", "Api.Tests", "Api.Tests.csproj"), $"<Project><PropertyGroup>{True}</PropertyGroup></Project>\n");

		var (code, stdout, _) = RunMain("--root", root, "--list");

		Assert.Equal(0, code);
		Assert.Equal("tests/App.Tests/App.Tests.csproj\ntests/Integration/Api.Tests/Api.Tests.csproj\n", stdout);
	}

	[Fact]
	public void List_keeps_messages_off_stdout()
	{
		var root = Path.Join(tmp, "repo");
		PropsFile(root, "", When("'$(Configuration)' == 'Debug'", True));
		Project(root, "App.Tests");

		var (code, stdout, stderr) = RunMain("--root", root, "--list");

		Assert.Equal(0, code);
		Assert.Equal("tests/App.Tests/App.Tests.csproj\n", stdout);
		Assert.Contains("can't evaluate Condition", stderr);
	}

	[Fact]
	public void List_prints_nothing_when_there_are_no_projects()
	{
		var root = Path.Join(tmp, "repo");
		Directory.CreateDirectory(root);

		var (code, stdout, _) = RunMain("--root", root, "--list");

		Assert.Equal(0, code);
		Assert.Equal("", stdout);
	}

	[Fact]
	public void List_fails_when_projects_exist_but_none_is_a_test_project()
	{
		var root = Path.Join(tmp, "repo");
		Project(root, "Web.Tests.Unit");

		var (code, stdout, stderr) = RunMain("--root", root, "--list");

		Assert.Equal(1, code);
		Assert.Equal("", stdout);
		Assert.Contains("resolves IsTestProject to true", stderr);
	}

	// The port's own: what the parity suite can't reach through the command line.

	[Fact]
	public void Exactly_one_of_output_and_list_is_required()
	{
		var neither = RunMain("--root", tmp);
		var both = RunMain("--output", Path.Join(tmp, "out"), "--list");

		Assert.Equal((2, ""), (neither.Code, neither.Stdout));
		Assert.EndsWith("discover_tests.cs: error: one of the arguments --output --list is required\n", neither.Stderr);
		Assert.Equal(2, both.Code);
		Assert.EndsWith("discover_tests.cs: error: argument --list: not allowed with argument --output\n", both.Stderr);
		Assert.False(File.Exists(Path.Join(tmp, "out")));
	}

	[Fact]
	public void The_matrix_escapes_what_json_dumps_escapes()
	{
		// A name with a line break, which the parity suite can't hold (its line-ending check reads git's file list a line at a time).
		var entry = new MatrixEntry("tests/a\nb\r\"\\\t\b\f\x01\x7f~é😀/x.csproj", "", "", false, false, false, "minimal");

		Assert.Equal("{\"include\": [{\"project_path\": \"tests/a\\nb\\r\\\"\\\\\\t\\b\\f\\u0001\\u007f~\\u00e9\\ud83d\\ude00/x.csproj\", \"test_name\": \"\", \"test_id\": \"\", " +
				"\"build_before_test\": false, \"no_build\": false, \"requires_playwright\": false, \"verbosity\": \"minimal\"}]}", DiscoverTests.Matrix([entry]));
		Assert.Equal("{\"include\": []}", DiscoverTests.Matrix([]));
	}

	[Theory]
	[InlineData("/", "/")]
	[InlineData("/a/./b/", "/a/b")]
	[InlineData("/a/../b", "/b")]
	[InlineData("/..", "/")]
	public void Resolve_reads_a_path_without_symlinks_as_pathlib_does(string path, string resolved) =>
			Assert.Equal(resolved, PyPath.Resolve(path));

	[Fact]
	public void Resolve_follows_each_symlink_before_the_parts_after_it()
	{
		Directory.CreateDirectory(Path.Join(tmp, "real", "sub"));
		File.CreateSymbolicLink(Path.Join(tmp, "link"), "real/sub");
		File.CreateSymbolicLink(Path.Join(tmp, "absolute"), Path.Join(tmp, "real"));

		Assert.Equal(Path.Join(PyPath.Resolve(tmp), "real", "x"), PyPath.Resolve(Path.Join(tmp, "link", "..", "x")));
		Assert.Equal(Path.Join(PyPath.Resolve(tmp), "real", "sub"), PyPath.Resolve(Path.Join(tmp, "absolute", "sub")));
	}

	[Fact]
	public void Resolve_stops_at_a_symlink_loop()
	{
		File.CreateSymbolicLink(Path.Join(tmp, "loop"), "loop");

		Assert.Throws<IOException>(() => PyPath.Resolve(Path.Join(tmp, "loop")));
	}

	[Theory]
	[InlineData("  $(MSBuildProjectName.Contains('.Tests'))\t", "App.Tests", true)]
	[InlineData("$(MSBuildProjectName.Contains(''))", "App", true)]
	[InlineData("!$(MSBuildProjectName.EndsWith('.Tests'))", "App.Tests", false)]
	[InlineData("'$(MSBuildProjectName)'!='APP'", "app", false)]
	[InlineData("$(MSBuildProjectName.contains('.Tests'))", "App.Tests", null)]
	[InlineData("$(MSBuildProjectName.Contains('.Tests')) and true", "App.Tests", null)]
	public void Condition_holds_reads_only_the_shapes_it_knows(string condition, string name, bool? holds) =>
			Assert.Equal(holds, DiscoverTests.ConditionHolds(condition, name));
}
