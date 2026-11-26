using System.Diagnostics;
using System.Text;

using Microsoft.Win32.SafeHandles;

using YamlDotNet.RepresentationModel;

namespace Baseline.Tests;

// The Template's .github/scripts/CheckCentralPins.cs: test_check_central_pins.py's tests, one method each under its name (ADR 0017's rule),
// with the same cases and assertions, on throwaway folders. A pytest that set a module constant gives the check its own Caps; one that
// patched os.lstat, os.open, os.fstat or os.scandir gives it a Files that stands in for that call. Where the pytest named Python (an
// encoding Python can't decode), the name says .NET. test_an_older_python_is_refused_with_a_message has no counterpart: its check went
// with the Python, and the SDK the C# runs on is the one global.json names. The tests after the pytest's are the port's own.
public sealed class CheckCentralPinsTests : IDisposable
{
	const string Transitive = "<PropertyGroup><CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled></PropertyGroup>";
	const string Command = "dotnet run --no-build --file .github/scripts/check_central_pins.cs --";

	// pytest's tmp_path, resolved as the script resolves --root (a temp folder may be under a symlink, /tmp on macOS).
	readonly string tmp = PyPath.Resolve(Directory.CreateTempSubdirectory("check-central-pins.").FullName);

	public void Dispose() => Directory.Delete(tmp, true);

	static readonly Caps Defaults = new();

	static string Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}

	static string WriteBytes(string path, byte[] data)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, data);
		return path;
	}

	static string Central(string root, string items, string properties = Transitive, string folder = "", string name = "Directory.Packages.props") =>
			Write(Path.Join(root, folder, name), $"<Project>\n  {properties}\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n");

	static string Project(string root, string relative, string body, string sdk = "Microsoft.NET.Sdk") =>
			Write(Path.Join(root, relative), $"<Project Sdk=\"{sdk}\">\n  <ItemGroup>{body}</ItemGroup>\n</Project>\n");

	// The PropertyGroups of a Directory.Packages.props that sets the setting to value, and others as its other properties.
	static string Setting(string value, params (string Name, string Text)[] others) =>
			$"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled>{string.Concat(others.Select(o => $"<{o.Name}>{o.Text}</{o.Name}>"))}</PropertyGroup>";

	// The check, with GITHUB_ACTIONS set to actions (unset by default, as the pytest's fixture left it, whatever the test's own environment).
	static CentralPins Pins(Caps? caps = null, Files? files = null, string? actions = null) =>
			new(caps, files, name => name == "GITHUB_ACTIONS" ? actions : null);

	static Findings Check(string root, Caps? caps = null, Files? files = null) => Pins(caps, files).Check(root);

	static List<string> Unreferenced(string root) => [.. Check(root).Unreferenced.Select(pin => pin.Name)];

	// main(["--root", root]): (exit code, stdout, stderr).
	static (int Code, string Out, string Err) Run(string root, Caps? caps = null, Files? files = null, string? actions = null)
	{
		StringWriter stdout = new(), stderr = new();
		var code = Pins(caps, files, actions).Run(["--root", root], stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	static string RunCi(string root, Caps? caps = null) => Run(root, caps, actions: "true").Out;

	static List<string> Lines(string text) => [.. text.Split('\n').SkipLast(1)];

	// A Files that stands in for some of the real calls: Stat for os.lstat, Opened for os.open, Type for os.fstat, Listed for os.scandir.
	sealed class Stand : Files
	{
		public Func<string, Stat?>? Stat;
		public Func<string, SafeFileHandle?>? Opened;
		public Func<SafeFileHandle, FileHandleType?>? Type;
		public Action<string>? Listed;
		public Action<List<string>>? Sorted;
		public readonly List<SafeFileHandle> Handles = [];

		public override Stat LStat(string path) => Stat?.Invoke(path) ?? base.LStat(path);

		public override SafeFileHandle Open(string path)
		{
			var handle = Opened?.Invoke(path) ?? base.Open(path);
			Handles.Add(handle);
			return handle;
		}

		public override FileHandleType TypeOf(SafeFileHandle handle) => Type?.Invoke(handle) ?? base.TypeOf(handle);

		public override List<Entry> List(string folder)
		{
			Listed?.Invoke(folder);
			return base.List(folder);
		}

		public override List<string> SortedValues(IEnumerable<string> values)
		{
			var items = values.ToList();
			Sorted?.Invoke(items);
			return base.SortedValues(items);
		}
	}

	// What is reported, and what passes.

	[Fact]
	public void A_pin_no_project_references_is_reported()
	{
		// IssueManager's: Radzen.Blazor needed a newer Components.Web than the pin, which nothing referenced.
		Central(tmp, "<PackageVersion Include=\"Radzen.Blazor\" Version=\"13.1.0\" />\n<PackageVersion Include=\"Microsoft.AspNetCore.Components.Web\" Version=\"10.0.14\" />");
		Project(tmp, "src/Web/Web.csproj", "<PackageReference Include=\"Radzen.Blazor\" />");

		var (status, output, err) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Equal("❌ Microsoft.AspNetCore.Components.Web is pinned in Directory.Packages.props but no project references it\n", output);
		Assert.DoesNotContain("::", output);
		Assert.Contains("Delete each pin above", err);
		Assert.Contains("<!-- Transitive pin: <the reason> -->", err);
		Assert.Contains("a package an SDK references itself", err);
	}

	[Fact]
	public void Every_pin_referenced_passes()
	{
		Central(tmp, "<PackageVersion Include=\"Radzen.Blazor\" Version=\"13.1.0\" />");
		Project(tmp, "src/Web/Web.csproj", "<PackageReference Include=\"Radzen.Blazor\" />");

		Assert.Equal((0, "Every central pin is referenced by a project, or kept with a reason.\n", ""), Run(tmp));
	}

	[Fact]
	public void Without_transitive_pinning_an_unreferenced_pin_passes()
	{
		// It pins nothing then: TicketManager's and Articles' leftovers.
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: "");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.StartsWith("Transitive pinning is off", output);
	}

	[Fact]
	public void A_repo_without_central_package_management_passes()
	{
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"Polly\" Version=\"8.0.0\" />");

		Assert.Equal(0, Run(tmp).Code);
	}

	[Theory]
	[InlineData("true")]
	[InlineData("True")]
	[InlineData(" TRUE ")]
	public void Transitive_pinning_is_read_ignoring_case_and_spaces(string value)
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />",
				properties: $"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled></PropertyGroup>");

		Assert.True(Check(tmp).Transitive);
	}

	[Theory]
	[InlineData("false")]
	[InlineData("")]
	[InlineData("yes")]
	public void Transitive_pinning_other_than_true_is_off(string value)
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />",
				properties: $"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled></PropertyGroup>");

		Assert.Equal(0, Run(tmp).Code);
	}

	[Fact]
	public void Transitive_pinning_set_in_directory_build_props_counts()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: "");
		Write(Path.Join(tmp, "Directory.Build.props"), $"<Project>{Transitive}</Project>\n");

		var found = Check(tmp);

		Assert.True(found.Transitive);
		Assert.Equal([(Path.Join(tmp, "Directory.Packages.props"), "Polly")], found.Unreferenced);
	}

	[Theory]
	[InlineData("true")]
	[InlineData("True ")]
	public void A_setting_built_from_a_property_set_to_true_is_on(string value)
	{
		// The finding on mpaulosky/Application_Factory#110: $(PinTransitive) read as off, so every unreferenced pin passed.
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(PinTransitive)"));
		Write(Path.Join(tmp, "Directory.Build.props"), $"<Project><PropertyGroup><PinTransitive>{value}</PinTransitive></PropertyGroup></Project>\n");

		Assert.True(Check(tmp).Transitive);
		Assert.Empty(Check(tmp).UnreadSettings);
		var (status, output, _) = Run(tmp);
		Assert.Equal(1, status);
		Assert.Equal("❌ Polly is pinned in Directory.Packages.props but no project references it\n", output);
	}

	[Fact]
	public void A_setting_built_from_a_property_set_to_anything_else_is_off()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(PinTransitive)", ("PinTransitive", "false")));

		Assert.False(Check(tmp).Transitive);
		var (status, output, _) = Run(tmp);
		Assert.Equal(0, status);
		Assert.StartsWith("Transitive pinning is off", output);
	}

	[Fact]
	public void A_setting_built_from_a_property_with_true_among_its_values_is_on()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(P)", ("P", "false")));
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><P>true</P></PropertyGroup></Project>\n");

		var found = Check(tmp);

		Assert.True(found.Transitive);
		Assert.Equal([(Path.Join(tmp, "Directory.Packages.props"), "Polly")], found.Unreferenced);
	}

	[Theory]
	[InlineData("$(NotSetAnywhere)")]
	[InlineData("@(Items)")]
	[InlineData("%(Meta)")]
	[InlineData("$(P)")]
	[InlineData("$([MSBuild]::ValueOrDefault('', 'true'))")]
	public void A_setting_that_cant_be_read_counts_as_on_and_is_named(string value)
	{
		// Under it an unreferenced pin is held back or does nothing, so deleting it is safe either way: the check fails closed.
		(string, string)[] others = value == "$(P)" ? [("P", "$(Q)")] : [];
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting(value, others));

		Assert.True(Check(tmp).Transitive);
		Assert.Equal([("Directory.Packages.props", value)], Check(tmp).UnreadSettings);
		var (status, output, _) = Run(tmp);
		Assert.Equal(1, status);
		Assert.Equal($"ℹ️  Directory.Packages.props sets CentralPackageTransitivePinningEnabled to {value}, which this check can't read without "
				+ "evaluating MSBuild, so it counts transitive pinning as on.\n"
				+ "❌ Polly is pinned in Directory.Packages.props but no project references it\n", output);
	}

	[Fact]
	public void A_setting_that_cant_be_read_with_every_pin_referenced_passes()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(NotSetAnywhere)"));
		Project(tmp, "src/A/A.csproj", "<PackageReference Include=\"Polly\" />");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.EndsWith("Every central pin is referenced by a project, or kept with a reason.\n", output);
		Assert.Contains("can't read without evaluating MSBuild", output);
	}

	[Fact]
	public void A_setting_that_cant_be_read_is_named_once_however_often_it_is_set()
	{
		Central(tmp, "", properties: Setting("$(X)") + Setting("$(X)"));

		Assert.Equal([("Directory.Packages.props", "$(X)")], Check(tmp).UnreadSettings);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void A_later_false_doesnt_turn_off_an_earlier_true(bool split)
	{
		// Directory.Build.props is read before Directory.Packages.props, so its true comes first; in one file, the first group's does.
		string properties;
		if (split)
		{
			Write(Path.Join(tmp, "Directory.Build.props"), $"<Project>{Transitive}</Project>\n");
			properties = Setting("false");
		}
		else
			properties = Transitive + Setting("false");
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: properties);

		var found = Check(tmp);

		Assert.True(found.Transitive);
		Assert.Equal([(Path.Join(tmp, "Directory.Packages.props"), "Polly")], found.Unreferenced);
		Assert.Empty(found.UnreadSettings);
	}

	[Fact]
	public void A_setting_resolving_to_true_in_the_same_file_settles_an_unreadable_one()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(NotSetAnywhere)") + Setting("$(P)", ("P", "true")));

		Assert.True(Check(tmp).Transitive);
		Assert.Empty(Check(tmp).UnreadSettings);
	}

	[Fact]
	public void A_literal_true_elsewhere_settles_it_and_names_no_setting()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(NotSetAnywhere)"));
		Write(Path.Join(tmp, "Directory.Build.props"), $"<Project>{Transitive}</Project>\n");

		Assert.True(Check(tmp).Transitive);
		Assert.Empty(Check(tmp).UnreadSettings);
	}

	[Fact]
	public void An_unreadable_setting_cant_start_a_workflow_command()
	{
		Central(tmp, "", properties: Setting("$(A)\n::error::x"));

		var output = RunCi(tmp);

		Assert.DoesNotContain("\n::", output);
		Assert.StartsWith("::notice file=Directory.Packages.props::", output);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(100)]
	public void A_literal_setting_of_any_length_is_compared_as_written(int extra)
	{
		// Only a value holding $(, @( or %( goes through Resolve(), so MaxId, which would make it unreadable, doesn't apply.
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting(new string('x', Defaults.MaxId + extra)));

		Assert.False(Check(tmp).Transitive);
		Assert.Empty(Check(tmp).UnreadSettings);
		Assert.StartsWith("Transitive pinning is off", Run(tmp).Out);
	}

	public static TheoryData<string> EmptySettings => [Setting(""), Setting("  "), Setting("$(P)", ("P", "")), Setting("$(P)", ("P", " ; "))];

	[Theory]
	[MemberData(nameof(EmptySettings))]
	public void A_setting_empty_or_resolving_to_nothing_is_off_with_no_notice(string properties)
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: properties);

		Assert.False(Check(tmp).Transitive);
		Assert.Empty(Check(tmp).UnreadSettings);
		Assert.Equal((0, "Transitive pinning is off in every MSBuild file read (or there is no central package management): no pin found can hold a package back.\n", ""), Run(tmp));
	}

	[Fact]
	public void A_setting_mixing_properties_is_resolved_whole()
	{
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(A)$(B)", ("A", "tr"), ("B", "UE")));

		var found = Check(tmp);

		Assert.True(found.Transitive);
		Assert.Equal([(Path.Join(tmp, "Directory.Packages.props"), "Polly")], found.Unreferenced);
		Assert.Empty(found.UnreadSettings);
	}

	[Fact]
	public void A_setting_that_refers_to_itself_counts_as_on()
	{
		const string Value = "$(CentralPackageTransitivePinningEnabled)";
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting(Value));

		Assert.True(Check(tmp).Transitive);
		Assert.Equal([("Directory.Packages.props", Value)], Check(tmp).UnreadSettings);
	}

	[Theory]
	[InlineData(256, true)]
	[InlineData(257, false)]
	public void A_setting_past_max_expansions_counts_as_on(int count, bool readable)
	{
		var groups = string.Concat(Enumerable.Range(0, count).Select(index => $"<PropertyGroup><P>v{index}</P></PropertyGroup>"));
		Central(tmp, "<PackageVersion Include=\"Polly\" Version=\"8.0.0\" />", properties: Setting("$(P)") + groups);

		Assert.Equal(256, Defaults.MaxExpansions);
		Assert.Equal(!readable, Check(tmp).Transitive);
		Assert.Equal(readable, Check(tmp).UnreadSettings.Count == 0);
	}

	[Fact]
	public void A_setting_that_spends_past_the_runs_budget_is_not_a_pass()
	{
		var groups = string.Concat(Enumerable.Range(0, 3).Select(index => $"<PropertyGroup><P>v{index}</P></PropertyGroup>"));
		Central(tmp, "", properties: Setting("$(P)") + groups);

		var (status, output, _) = Run(tmp, new Caps { MaxTotalExpansions = 2 });

		Assert.Equal(1, status);
		Assert.StartsWith("❌ Not checked: ", output);
		Assert.Contains("combinations of property values", output);
	}

	[Fact]
	public void A_pin_listed_twice_is_reported_once()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" Condition=\"x\" />\n<PackageVersion Include=\"a\" Version=\"2.0.0\" Condition=\"y\" />");

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	[Fact]
	public void An_include_with_several_ids_is_read_as_each()
	{
		Central(tmp, "<PackageVersion Include=\"A; B\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");

		Assert.Equal(["B"], Unreferenced(tmp));
	}

	[Fact]
	public void A_pin_under_choose_or_a_conditioned_item_group_is_checked()
	{
		Write(Path.Join(tmp, "Directory.Packages.props"),
				$"<Project>{Transitive}<Choose><When Condition=\"x\"><ItemGroup><PackageVersion Include=\"A\" Version=\"1\" /></ItemGroup></When></Choose>"
				+ "<ItemGroup Condition=\"y\"><PackageVersion Include=\"B\" Version=\"1\" /></ItemGroup></Project>\n");

		Assert.Equal(["A", "B"], Unreferenced(tmp));
	}

	[Fact]
	public void A_nested_central_file_and_its_name_in_any_case_is_checked()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", folder: "samples", name: "directory.packages.props");

		Assert.Equal([("samples", "A")], Check(tmp).Unreferenced.Select(pin => (Path.GetFileName(Path.GetDirectoryName(pin.Path))!, pin.Name)));
	}

	// The reason comment.

	[Theory]
	[InlineData("Transitive pin: GHSA-7jgj-8wvc-jh57, fixed in 4.3.4")]
	[InlineData("  transitive PIN:a security fix  ")]
	[InlineData("TRANSITIVE PIN: x")]
	public void A_pin_with_a_reason_comment_right_before_it_is_kept(string comment)
	{
		Central(tmp, $"<!--{comment}-->\n<PackageVersion Include=\"System.Net.Http\" Version=\"4.3.4\" />\n"
				+ "<PackageVersion Include=\"System.Text.RegularExpressions\" Version=\"4.3.1\" />");

		Assert.Equal(["System.Text.RegularExpressions"], Unreferenced(tmp));
	}

	[Fact]
	public void A_reason_comment_covers_only_the_pin_right_after_it()
	{
		Central(tmp, "<!-- Transitive pin: a security fix -->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />\n<PackageVersion Include=\"B\" Version=\"1.0.0\" />");

		Assert.Equal(["B"], Unreferenced(tmp));
	}

	[Fact]
	public void A_reason_comment_separated_from_the_pin_by_an_element_is_no_reason()
	{
		Central(tmp, "<!-- Transitive pin: a security fix -->\n<PackageReference Include=\"X\" />\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />");

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	[Fact]
	public void Only_the_last_of_two_comments_counts()
	{
		Central(tmp, "<!-- Transitive pin: a security fix -->\n<!-- Testing -->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />\n"
				+ "<!-- Testing -->\n<!-- Transitive pin: a security fix -->\n<PackageVersion Include=\"B\" Version=\"1.0.0\" />");

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	[Fact]
	public void A_group_heading_comment_is_no_reason()
	{
		Central(tmp, "<!-- Microsoft -->\n<PackageVersion Include=\"Microsoft.OpenApi\" Version=\"2.13.0\" />");

		Assert.Equal(["Microsoft.OpenApi"], Unreferenced(tmp));
	}

	[Theory]
	[InlineData("Transitive pin:")]
	[InlineData("Transitive pin:   ")]
	[InlineData("Transitive pin")]
	[InlineData("Pinned: transitive pin: x")]
	public void A_reason_comment_that_gives_no_reason_is_no_reason(string comment)
	{
		Central(tmp, $"<!--{comment}-->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />");

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	// What counts as a reference.

	[Fact]
	public void Ids_match_ignoring_case()
	{
		Central(tmp, "<PackageVersion Include=\"FluentAssertions\" Version=\"8.0.0\" />");
		Project(tmp, "tests/A/A.csproj", "<PackageReference Include=\"fluentassertions\" />");

		Assert.Empty(Unreferenced(tmp));
	}

	[Theory]
	[InlineData("<PackageReference Update=\"A\" />")]
	[InlineData("<GlobalPackageReference Include=\"A\" Version=\"1\" />")]
	[InlineData("<PackageDownload Include=\"A\" Version=\"[1]\" />")]
	[InlineData("<packagereference Include=\"A\" />")]
	public void Each_kind_of_reference_counts(string element)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" /><PackageVersion Include=\"B\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", element);

		Assert.Equal(["B"], Unreferenced(tmp));
	}

	[Theory]
	[InlineData(".csproj")]
	[InlineData(".fsproj")]
	[InlineData(".vbproj")]
	[InlineData(".proj")]
	[InlineData(".sqlproj")]
	[InlineData(".wixproj")]
	[InlineData(".esproj")]
	[InlineData(".CSPROJ")]
	[InlineData(".props")]
	[InlineData(".targets")]
	public void A_reference_in_any_msbuild_file_counts(string suffix)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, $"src/App/App{suffix}", "<PackageReference Include=\"A\" />");

		Assert.Empty(Unreferenced(tmp));
	}

	[Theory]
	[InlineData("App.json")]
	[InlineData("App.csproj.user.txt")]
	[InlineData("App.propsx")]
	[InlineData("App.target")]
	[InlineData("projects")]
	public void A_file_that_isnt_msbuild_is_not_read(string name)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, $"src/App/{name}", "<PackageReference Include=\"A\" />");

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	[Fact]
	public void An_msbuild_sdk_counts_as_a_reference()
	{
		Central(tmp, "<PackageVersion Include=\"Aspire.AppHost.Sdk\" Version=\"13.6.5\" />\n<PackageVersion Include=\"MSTest.Sdk\" Version=\"3.0.0\" />\n"
				+ "<PackageVersion Include=\"Other.Sdk\" Version=\"1.0.0\" />\n<PackageVersion Include=\"Json.Sdk\" Version=\"1.0.0\" />");
		Project(tmp, "src/AppHost/AppHost.csproj", "", sdk: "Microsoft.NET.Sdk;Aspire.AppHost.Sdk/13.6.5");
		Write(Path.Join(tmp, "tests/T/T.csproj"), "<Project><Sdk Name=\"MSTest.Sdk\" /><Import Project=\"x\" Sdk=\"Other.Sdk\" /></Project>\n");
		Write(Path.Join(tmp, "global.json"), "{\"msbuild-sdks\": {\"Json.Sdk\": \"1.0.0\"}}\n");

		Assert.Empty(Unreferenced(tmp));
	}

	[Fact]
	public void A_project_with_the_legacy_namespace_or_a_prefix_is_read()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" /><PackageVersion Include=\"B\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Old.csproj"), "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");
		Write(Path.Join(tmp, "Prefixed.csproj"), "<m:Project xmlns:m=\"urn:x\"><m:ItemGroup><m:PackageReference Include=\"B\" /></m:ItemGroup></m:Project>\n");

		Assert.Empty(Unreferenced(tmp));
	}

	[Theory]
	[InlineData("utf-8-sig", "")]
	[InlineData("utf-8", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")]
	[InlineData("utf-16", "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n")]
	[InlineData("iso-8859-1", "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n<!-- café -->\n")]
	public void A_file_in_any_encoding_msbuild_reads_is_read(string encoding, string prefix)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var text = $"{prefix}<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n";
		// Python's utf-8-sig and utf-16 codecs write a byte order mark first.
		byte[] data = encoding switch
		{
			"utf-8-sig" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
			"utf-16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
			"iso-8859-1" => Encoding.Latin1.GetBytes(text),
			_ => Encoding.UTF8.GetBytes(text),
		};
		WriteBytes(Path.Join(tmp, "App.csproj"), data);

		Assert.Empty(Unreferenced(tmp));
	}

	// Ids built from properties.

	[Fact]
	public void A_reference_built_from_a_property_is_resolved()
	{
		Central(tmp, "<PackageVersion Include=\"Acme.Analyzers\" Version=\"1.0.0\" /><PackageVersion Include=\"B\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><AnalyzerPackage>Acme.Analyzers</AnalyzerPackage><Vendor>acme</Vendor></PropertyGroup></Project>\n");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(AnalyzerPackage)\" />");

		Assert.Equal(["B"], Unreferenced(tmp));
	}

	[Fact]
	public void A_property_with_several_values_resolves_to_each()
	{
		Central(tmp, "<PackageVersion Include=\"A.Core\" Version=\"1\" /><PackageVersion Include=\"B.Core\" Version=\"1\" /><PackageVersion Include=\"C.Core\" Version=\"1\" />");
		Write(Path.Join(tmp, "a.props"), "<Project><PropertyGroup><Prefix>A</Prefix></PropertyGroup></Project>\n");
		Write(Path.Join(tmp, "b.props"), "<Project><PropertyGroup Condition=\"x\"><Prefix>b</Prefix></PropertyGroup></Project>\n");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(PREFIX).Core\" />");

		Assert.Equal(["C.Core"], Unreferenced(tmp));
	}

	[Fact]
	public void A_pin_built_from_a_property_is_resolved()
	{
		Central(tmp, "<PackageVersion Include=\"$(Prefix).Core\" Version=\"1.0.0\" />", properties: Transitive + "<PropertyGroup><Prefix>Acme</Prefix></PropertyGroup>");

		Assert.Equal(["$(Prefix).Core"], Unreferenced(tmp));
	}

	[Theory]
	[InlineData("$(Unset)")]
	[InlineData("@(Packages)")]
	[InlineData("%(Name)")]
	[InlineData("$(Nested)")]
	public void A_reference_that_cant_be_read_turns_the_report_into_warnings(string reference)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: Transitive + "<PropertyGroup><Nested>$(Unset)</Nested></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", $"<PackageReference Include=\"{reference}\" />");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.StartsWith("⚠️  A is pinned in Directory.Packages.props but no project references it\n", output);
		Assert.Contains($"Reported as warnings: a reference this check can't read without evaluating MSBuild ({reference})", output);
	}

	[Fact]
	public void A_pin_that_cant_be_read_is_left_alone()
	{
		Central(tmp, "<PackageVersion Include=\"$(Unset).Core\" Version=\"1.0.0\" /><PackageVersion Include=\"@(X)\" Version=\"1.0.0\" />");

		Assert.Empty(Unreferenced(tmp));
	}

	[Fact]
	public void The_caps_are_the_documented_ones()
	{
		// The boundary tests below are written against the caps, so these pin their values.
		Assert.Equal((4 * 1024 * 1024, 256, 256, 1024), (Defaults.MaxBytes, Defaults.MaxExpansions, Defaults.MaxDepth, Defaults.MaxValue));
		Assert.Equal((64L * 1024 * 1024, 250_000L, 100_000L, 100), (Defaults.MaxTotalBytes, Defaults.MaxNodes, Defaults.MaxTotalExpansions, Defaults.MaxShown));
		Assert.Equal((256, 250_000L, 50_000L, 300), (Defaults.MaxId, Defaults.MaxIds, Defaults.MaxFiles, Defaults.MaxPrinted));
		Assert.Equal((64 * 1024, 100), (Defaults.MaxGlobalJsonBytes, Defaults.MaxGlobalJsonFiles));
	}

	// A reference through one property the repo gives count values.
	void Expansions(int count)
	{
		Write(Path.Join(tmp, "Directory.Build.props"), $"<Project>{string.Concat(Enumerable.Range(0, count).Select(i => $"<PropertyGroup Condition=\"{i}\"><First>p{i}</First></PropertyGroup>"))}</Project>\n");
		Central(tmp, "<PackageVersion Include=\"Orphan\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(First)\" />");
	}

	[Theory]
	[InlineData(255, true)]
	[InlineData(256, true)]
	[InlineData(257, false)]
	public void A_reference_through_more_than_max_expansions_combinations_cant_be_read(int count, bool readable)
	{
		Expansions(count);

		Assert.Equal(readable, Check(tmp).Unreadable.Count == 0);
	}

	[Fact]
	public void Combinations_of_several_properties_multiply()
	{
		// 129 * 2 = 258 combinations, past the cap, though neither property has more than 129 values.
		var values = string.Concat(Enumerable.Range(0, 129).Select(i => $"<PropertyGroup Condition=\"{i}\"><First>p{i}</First></PropertyGroup>"));
		Write(Path.Join(tmp, "Directory.Build.props"), $"<Project>{values}<PropertyGroup><Second>x</Second></PropertyGroup><PropertyGroup Condition=\"z\"><Second>y</Second></PropertyGroup></Project>\n");
		Central(tmp, "<PackageVersion Include=\"Orphan\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(First).$(Second)\" />");

		Assert.Equal(["$(First).$(Second)"], Check(tmp).Unreadable);
	}

	[Theory]
	[InlineData(-1, true)]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void A_property_value_over_max_value_characters_cant_be_read(int extra, bool readable)
	{
		// A list of one id padded with separators, so only the value's own length is at the cap.
		var value = "A" + new string(';', Defaults.MaxValue + extra - 1);
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: Transitive + $"<PropertyGroup><P>{value}</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(P)\" />");

		var found = Check(tmp);

		Assert.Equal(readable, found.Unreadable.Count == 0 && found.Unreferenced.Count == 0);
	}

	[Theory]
	[InlineData(-1, true)]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void A_name_that_would_grow_past_max_value_characters_isnt_built(int extra, bool readable)
	{
		// $(P)$(Q) substituted is 512 + 511, 512 or 513 characters: one id each, padded with separators.
		var first = "A" + new string(';', Defaults.MaxValue / 2 - 1);
		var second = "B" + new string(';', Defaults.MaxValue / 2 - 1 + extra);
		Central(tmp, "<PackageVersion Include=\"AB\" Version=\"1.0.0\" />", properties: Transitive + $"<PropertyGroup><P>{first}</P><Q>{second}</Q></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(P)$(Q)\" />");

		Assert.Equal(Defaults.MaxValue + extra, first.Length + second.Length);
		Assert.Equal(readable, Check(tmp).Unreadable.Count == 0);
	}

	[Fact]
	public void The_length_bound_takes_each_propertys_longest_value()
	{
		// P is "A" or 512 characters: $(P)$(P)x can be 1,025, though its shortest is 3.
		var longest = "A" + new string(';', Defaults.MaxValue / 2 - 1);
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />",
				properties: Transitive + $"<PropertyGroup><P>A</P></PropertyGroup><PropertyGroup Condition=\"c\"><P>{longest}</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(P)$(P)x\" /><PackageReference Include=\"$(P)\" />");

		Assert.Equal(["$(P)$(P)x"], Check(tmp).Unreadable);
	}

	[Theory]
	[InlineData(-1, true)]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void An_id_over_max_id_characters_cant_be_read(int extra, bool readable)
	{
		var name = new string('A', Defaults.MaxId + extra);
		Central(tmp, "<PackageVersion Include=\"B\" Version=\"1.0.0\" />", properties: Transitive + $"<PropertyGroup><P>{name}</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", $"<PackageReference Include=\"{name}\" /><PackageReference Include=\"$(P)\" />");

		Assert.Equal(readable ? [] : [name, "$(P)"], Check(tmp).Unreadable);
	}

	[Fact]
	public void Values_are_counted_before_they_are_sorted()
	{
		// A name past the cap is refused without sorting the values of each of its properties.
		var values = string.Concat(Enumerable.Range(0, 20).Select(i => $"<PropertyGroup Condition=\"{i}\"><P>v{i}</P><Q>w{i}</Q></PropertyGroup>"));
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: Transitive + values);
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(P).$(Q)\" />");
		var files = new Stand { Sorted = items => Assert.False(items.Any(item => item.StartsWith('v') || item.StartsWith('w')), "the values were sorted before they were counted") };

		Assert.Equal(["$(P).$(Q)"], Check(tmp, files: files).Unreadable);
	}

	[Theory]
	[InlineData("<PropertyGroup><P>\n  Acme  \n</P></PropertyGroup>")]
	[InlineData("<PropertyGroup><P>Ac&#109;e</P></PropertyGroup>")]
	[InlineData("<PropertyGroup><P>Ac<!-- why -->me</P></PropertyGroup>")]
	[InlineData("<Target Name=\"T\"><PropertyGroup><P>Acme</P></PropertyGroup></Target>")]
	[InlineData("<Choose><When Condition=\"x\"><PropertyGroup><P>Acme</P></PropertyGroup></When></Choose>")]
	public void A_property_is_read_wherever_and_however_it_is_set(string group)
	{
		Central(tmp, "<PackageVersion Include=\"Acme.Core\" Version=\"1.0.0\" />", properties: Transitive + group);
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(P).Core\" />");

		var found = Check(tmp);

		Assert.Empty(found.Unreferenced);
		Assert.Empty(found.Unreadable);
	}

	[Fact]
	public void A_property_holding_a_list_resolves_to_each_id()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1\" /><PackageVersion Include=\"B\" Version=\"1\" /><PackageVersion Include=\"C\" Version=\"1\" />",
				properties: Transitive + "<PropertyGroup><Pkgs>A; B</Pkgs></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(Pkgs)\" />");

		Assert.Equal(["C"], Unreferenced(tmp));
	}

	[Fact]
	public void A_value_split_across_many_chunks_is_read_whole()
	{
		// Longer than the reader's buffer, so it arrives in more than one piece.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />",
				properties: $"<PropertyGroup><CentralPackageTransitivePinningEnabled>{new string(' ', 20000)}true{new string('\n', 20000)}</CentralPackageTransitivePinningEnabled></PropertyGroup>");

		Assert.True(Check(tmp).Transitive);
	}

	[Fact]
	public void A_pin_resolving_to_several_ids_reports_each_it_finds_unreferenced_in_order()
	{
		Central(tmp, "<PackageVersion Include=\"$(Prefix).Core\" Version=\"1.0.0\" />",
				properties: Transitive + string.Concat(new[] { "Z", "X", "Y" }.Select(v => $"<PropertyGroup Condition=\"{v}\"><Prefix>{v}</Prefix></PropertyGroup>")));
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"X.Core\" />");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Equal("❌ y.core is pinned in Directory.Packages.props but no project references it\n"
				+ "❌ z.core is pinned in Directory.Packages.props but no project references it\n", output);
	}

	[Fact]
	public void The_warning_line_lists_each_unreadable_reference_once_in_order()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/B/B.csproj", "<PackageReference Include=\"@(Z)\" /><PackageReference Include=\"$(Unset)\" />");
		Project(tmp, "src/A/A.csproj", "<PackageReference Include=\"@(Z)\" />");

		Assert.Equal("Reported as warnings: a reference this check can't read without evaluating MSBuild ($(Unset), @(Z)) may name any of the pins above.",
				Lines(Run(tmp).Out)[^1]);
	}

	[Fact]
	public void An_unreadable_reference_cant_start_a_workflow_command()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"$(X&#10;::add-mask::y&#13;::stop-commands::t)\" />");

		var output = RunCi(tmp);

		Assert.Equal(["::warning file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it"],
				Lines(output).Where(line => line.StartsWith("::", StringComparison.Ordinal)));
		Assert.Contains("($(X?::add-mask::y?::stop-commands::t))", output);
	}

	[Fact]
	public void The_report_comes_in_order()
	{
		foreach (var folder in new[] { "b", "a" })
			Central(tmp, "<PackageVersion Include=\"Q\" Version=\"1\" /><PackageVersion Include=\"P\" Version=\"1\" />", folder: folder);
		Write(Path.Join(tmp, "z.props"), "<Project>");
		Write(Path.Join(tmp, "y.props"), "<Project>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		var lines = Lines(output);
		// The XML reader's own wording of the error is .NET's, so only up to it.
		const string Prefix = "ℹ️  y.props was skipped: it isn't well-formed MSBuild XML (";
		Assert.Equal([Prefix, "ℹ️  z.props was skipped: it isn't well-formed MSBuild XML ("], lines.Take(2).Select(line => line[..Prefix.Length]));
		Assert.Equal(["❌ Q is pinned in a/Directory.Packages.props but no project references it",
				"❌ P is pinned in a/Directory.Packages.props but no project references it",
				"❌ Q is pinned in b/Directory.Packages.props but no project references it",
				"❌ P is pinned in b/Directory.Packages.props but no project references it"], lines.Skip(2));
	}

	[Fact]
	public void Each_kind_of_line_is_printed_at_most_max_shown_times()
	{
		Central(tmp, string.Concat(Enumerable.Range(0, 3).Select(i => $"<PackageVersion Include=\"P{i}\" Version=\"1\" />")));
		Project(tmp, "src/App/App.csproj", string.Concat(Enumerable.Range(0, 3).Select(i => $"<PackageReference Include=\"@(R{i})\" />")));

		var lines = Lines(Run(tmp, new Caps { MaxShown = 2 }).Out);

		Assert.Equal(["⚠️  P0 is pinned in Directory.Packages.props but no project references it",
				"⚠️  P1 is pinned in Directory.Packages.props but no project references it",
				"... and 1 more like the 2 above."], lines.Take(3));
		Assert.Contains("(@(R0), @(R1) and 1 more)", lines[3]);
	}

	[Fact]
	public void Max_shown_lines_are_all_printed_without_a_more_line()
	{
		Central(tmp, string.Concat(Enumerable.Range(0, 2).Select(i => $"<PackageVersion Include=\"P{i}\" Version=\"1\" />")));

		var output = Run(tmp, new Caps { MaxShown = 2 }).Out;

		Assert.Equal(2, Lines(output).Count(line => line.Contains("is pinned", StringComparison.Ordinal)));
		Assert.DoesNotContain("more like", output);
	}

	// The run's budget.

	[Theory]
	[InlineData("MaxFiles", "files")]
	[InlineData("MaxNodes", "elements, attributes and comments")]
	[InlineData("MaxTotalBytes", "bytes")]
	[InlineData("MaxIds", "ids")]
	public void A_repo_bigger_than_the_runs_budget_fails(string cap, string kind)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" /><!-- Transitive pin: kept --><PackageVersion Include=\"K\" Version=\"1\" />");
		Write(Path.Join(tmp, "global.json"), "{\"msbuild-sdks\": {\"G\": \"1\"}}");
		Directory.CreateDirectory(Path.Join(tmp, "src/App/bin"));
		Write(Path.Join(tmp, "src/App/App.csproj"), "<Project Sdk=\"S\"><!-- a --><!-- b --><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");
		// Neither an MSBuild file nor a global.json, or under bin/: not counted.
		foreach (var name in new[] { "README.md", "src/App/Program.cs", "src/App/bin/Copy.csproj" })
			Write(Path.Join(tmp, name), "x");
		// 3 files; 11 elements, attributes and comments in the central file, 7 in the project with its 2 comments; the three files' bytes;
		// 5 ids (the two pins, the SDK, the reference and global.json's SDK).
		var used = cap switch
		{
			"MaxFiles" => 3,
			"MaxNodes" => 18,
			"MaxIds" => 5,
			_ => new[] { "Directory.Packages.props", "src/App/App.csproj", "global.json" }.Sum(name => new FileInfo(Path.Join(tmp, name)).Length),
		};
		Caps With(long value) => cap switch
		{
			"MaxFiles" => new Caps { MaxFiles = value },
			"MaxNodes" => new Caps { MaxNodes = value },
			"MaxIds" => new Caps { MaxIds = value },
			_ => new Caps { MaxTotalBytes = value },
		};
		Assert.Empty(Check(tmp).Unreferenced);

		Assert.Equal(0, Run(tmp, With(used)).Code);
		var (status, output, _) = Run(tmp, With(used - 1));

		Assert.Equal(1, status);
		Assert.Equal($"❌ Not checked: the repo needs more than the {used - 1} {kind} this check reads.\n", output);
	}

	[Theory]
	[InlineData("A;;;", 6)]
	[InlineData("$(P)", 6)]
	public void Every_list_entry_and_every_resolved_id_counts_against_the_ids_budget(string include, int used)
	{
		// The pin, the SDK and A;;; (four entries, three blank), or the pin, the SDK, $(P) and the three ids it resolves to.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: Transitive + "<PropertyGroup><P>A;B;C</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", $"<PackageReference Include=\"{include}\" />");

		Assert.Equal(0, Run(tmp, new Caps { MaxIds = used }).Code);
		Assert.Contains("needs more than the", Run(tmp, new Caps { MaxIds = used - 1 }).Out);
	}

	[Theory]
	[InlineData(null, 0)]
	[InlineData("", 0)]
	[InlineData(";", 2)]
	[InlineData(" ; A", 2)]
	public void An_absent_or_empty_value_counts_no_ids_and_every_entry_counts_one(string? value, int cost)
	{
		var budget = new Budget(Defaults);

		_ = CentralPins.Ids(value, budget).ToList();

		Assert.Equal(Defaults.MaxIds - cost, budget.Left["ids"]);
	}

	[Fact]
	public void A_global_jsons_sdks_count_as_ids_and_a_long_one_is_left_out()
	{
		var budget = new Budget(Defaults);
		var data = Encoding.UTF8.GetBytes($"{{\"msbuild-sdks\": {{\"{new string('a', Defaults.MaxId + 1)}\": \"1\", \"{new string('b', Defaults.MaxId)}\": \"1\"}}}}");

		Assert.Equal([new string('b', Defaults.MaxId)], Pins().GlobalJsonSdks(data, budget)!);
		Assert.Equal(Defaults.MaxIds - 2, budget.Left["ids"]);
	}

	[Fact]
	public void Combinations_count_against_the_runs_budget_once_per_name()
	{
		var caps = new Caps { MaxTotalExpansions = 2 };
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: Transitive + "<PropertyGroup><P>A</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", string.Concat(Enumerable.Repeat("<PackageReference Include=\"$(P)\" />", 5)) + "<PackageReference Include=\"$(P).x\" />");
		Assert.Equal(0, Run(tmp, caps).Code);

		Project(tmp, "src/Other/Other.csproj", "<PackageReference Include=\"$(P).y\" />");
		var (status, output, _) = Run(tmp, caps);

		Assert.Equal(1, status);
		Assert.Contains("needs more than the 2 combinations of property values", output);
	}

	// Files this check won't parse.

	[Theory]
	[InlineData(255, true)]
	[InlineData(256, true)]
	[InlineData(257, false)]
	public void A_file_nested_over_max_depth_is_skipped(int depth, bool read)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		const string Inner = "<ItemGroup><PackageReference Include=\"A\" /></ItemGroup>";
		Write(Path.Join(tmp, "Deep.props"), "<Project>" + string.Concat(Enumerable.Repeat("<a>", depth - 3)) + Inner + string.Concat(Enumerable.Repeat("</a>", depth - 3)) + "</Project>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(read ? 0 : 1, status);
		Assert.Equal(!read, output.Contains($"Deep.props was skipped: it isn't well-formed MSBuild XML (an element nested over {Defaults.MaxDepth} deep)", StringComparison.Ordinal));
	}

	[Fact]
	public void A_file_nested_far_deeper_is_skipped_not_a_crash()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Write(Path.Join(tmp, "Deep.props"), "<Project>" + string.Concat(Enumerable.Repeat("<a>", 50000)) + string.Concat(Enumerable.Repeat("</a>", 50000)) + "</Project>");
		// Far past the JSON reader's depth limit, and under MaxGlobalJsonBytes.
		Write(Path.Join(tmp, "global.json"), new string('[', 30000) + new string(']', 30000));

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.Contains("Deep.props was skipped", output);
		Assert.Contains("global.json was skipped: it isn't JSON the .NET host reads", output);
	}

	[Theory]
	[InlineData("gb2312")]
	[InlineData("hex")]
	[InlineData("no-such-encoding")]
	[InlineData("windows-1252")]
	public void A_file_in_an_encoding_net_cant_decode_is_skipped(string encoding)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Write(Path.Join(tmp, "Odd.props"), $"<?xml version=\"1.0\" encoding=\"{encoding}\"?><Project />");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.Contains("ℹ️  Odd.props was skipped: it isn't well-formed MSBuild XML (", output);
	}

	[Fact]
	public void A_large_text_is_read_in_linear_time()
	{
		// Adding each chunk of text to a string took minutes on a file this size, in the Python.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Text.props"), "<Project><PropertyGroup><P>" + string.Concat(Enumerable.Repeat("a\n&amp;", (Defaults.MaxBytes - 100) / 7)) + "</P></PropertyGroup></Project>");

		Assert.True(new FileInfo(Path.Join(tmp, "Text.props")).Length < Defaults.MaxBytes);
		var started = Stopwatch.StartNew();
		var problems = Check(tmp).Problems;

		Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
		Assert.Empty(problems);
	}

	// global.json.

	[Theory]
	[InlineData("{\"msbuild-sdks\": {\"Json.Sdk\": \"1.0.0\"}}")]
	[InlineData("﻿{\"msbuild-sdks\": {\"Json.Sdk\": \"1.0.0\"}}")]
	[InlineData("{\n  // the SDKs\n  \"msbuild-sdks\": { \"Json.Sdk\": \"1.0.0\", }, /* end */\n}")]
	[InlineData("{\"msbuild-sdks\": {\"Json.Sdk\": \"1.0.0\" // why\n,}}")]
	[InlineData("{\"sdk\": {\"version\": \"a // not a comment, }\"}, \"msbuild-sdks\": {\"Json.Sdk\": \"1\"}}")]
	[InlineData("{\"sdk\": {\"version\": \"a \\\" /* not a comment */\"}, \"msbuild-sdks\": {\"Json.Sdk\": \"1\"}}")]
	[InlineData("{\"msbuild-sdks\": {\"Json.Sdk\": \"1\"}, \"v\": \"q\\\" // \"}")]
	public void Global_json_msbuild_sdks_count_in_any_form_the_host_reads(string text)
	{
		Central(tmp, "<PackageVersion Include=\"Json.Sdk\" Version=\"1.0.0\" />");
		WriteBytes(Path.Join(tmp, "global.json"), Encoding.UTF8.GetBytes(text));

		var (status, output, _) = Run(tmp);

		Assert.Equal((0, "Every central pin is referenced by a project, or kept with a reason.\n"), (status, output));
	}

	[Fact]
	public void A_nested_global_json_counts()
	{
		Central(tmp, "<PackageVersion Include=\"Json.Sdk\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "src/global.json"), "{\"msbuild-sdks\": {\"Json.Sdk\": \"1.0.0\"}}");

		Assert.Empty(Unreferenced(tmp));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{\"msbuild-sdks\": [\"Json.Sdk\"]}")]
	[InlineData("{\"msbuild-sdks\": null}")]
	[InlineData("\"x\"")]
	public void A_global_json_without_an_msbuild_sdks_object_names_none(string text)
	{
		Central(tmp, "<PackageVersion Include=\"Json.Sdk\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "global.json"), text);

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.DoesNotContain("skipped", output);
	}

	[Theory]
	[InlineData("{not json")]
	[InlineData("{\"msbuild-sdks\": {\"Json.Sdk\": \"1\"}")]
	[InlineData("\u00ff\u00fe{")]
	[InlineData("/* open")]
	public void A_global_json_the_host_cant_read_is_skipped_with_a_notice(string data)
	{
		// The bytes as written, each character one byte (\u00ff\u00fe is the bytes 0xFF 0xFE).
		Central(tmp, "<PackageVersion Include=\"Json.Sdk\" Version=\"1.0.0\" />");
		WriteBytes(Path.Join(tmp, "global.json"), Encoding.Latin1.GetBytes(data));

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("ℹ️  global.json was skipped: it isn't JSON the .NET host reads. A reference or property in it wasn't seen.\n", output);
	}

	// Files that aren't read.

	[Fact]
	public void Folders_restore_and_build_write_are_skipped()
	{
		// CI caches the NuGet packages under the workspace's .nuget/, whose packages carry .props files of their own.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		foreach (var folder in new[] { ".nuget/packages/x/build", "src/App/obj", "src/App/bin", "node_modules/y", ".git/z" })
		{
			Write(Path.Join(tmp, folder, "x.props"), "<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");
			Write(Path.Join(tmp, folder, "global.json"), "{\"msbuild-sdks\": {\"A\": \"1\"}}");
		}

		Assert.Equal(["A"], Unreferenced(tmp));
	}

	[Theory]
	[InlineData("")]
	[InlineData("not xml")]
	[InlineData("<Project><ItemGroup></Project>")]
	[InlineData("<Project>$(Name)</Project><Extra/>")]
	public void A_malformed_file_is_skipped_with_a_notice_when_pinning_is_off(string data)
	{
		// A dotnet new template or a test fixture that restore never reads.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", properties: "");
		Write(Path.Join(tmp, "templates/Fixture.csproj"), data);

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.StartsWith("ℹ️  templates/Fixture.csproj was skipped: it isn't well-formed MSBuild XML (", output);
	}

	[Fact]
	public void A_malformed_file_only_makes_the_check_stricter()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Broken.csproj"), "<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("Broken.csproj was skipped", output);
		Assert.Contains("A is pinned in Directory.Packages.props", output);
	}

	[Fact]
	public void A_doctype_or_entity_declaration_is_refused()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Bomb.csproj"), "<?xml version=\"1.0\"?>\n<!DOCTYPE p [<!ENTITY a \"AAAA\"><!ENTITY b \"&a;&a;&a;&a;\">]>\n"
				+ "<Project><ItemGroup><PackageReference Include=\"&b;\" /></ItemGroup></Project>\n");
		Write(Path.Join(tmp, "Entity.props"), "<!DOCTYPE Project>\n<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("Bomb.csproj was skipped: it isn't well-formed MSBuild XML (a DOCTYPE or entity declaration", output);
		Assert.Contains("Entity.props was skipped: it isn't well-formed MSBuild XML (a DOCTYPE or entity declaration", output);
	}

	[Fact]
	public void A_symlinked_or_dangling_file_is_not_followed()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var outside = Write(tmp + "-outside.props", "<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");
		try
		{
			File.CreateSymbolicLink(Path.Join(tmp, "Linked.props"), outside);
			File.CreateSymbolicLink(Path.Join(tmp, "Dangling.props"), Path.Join(tmp, "missing.props"));
			File.CreateSymbolicLink(Path.Join(tmp, "global.json"), outside);

			var (status, output, _) = Run(tmp);

			Assert.Equal(1, status);
			foreach (var name in new[] { "Linked.props", "Dangling.props" })
				Assert.Contains($"❌ {name} can't be checked: it is a symlink. It may hold central pins, turn transitive pinning on or set a property a pin is built from.\n", output);
			Assert.Contains("ℹ️  global.json was skipped: it is a symlink.", output);
		}
		finally
		{
			File.Delete(outside);
		}
	}

	// mkfifo(1), which the BCL doesn't have: a FIFO in folder.
	static void MakeFifo(string path) => Assert.Equal(0, Script.Capture(["mkfifo", path]).Code);

	// What work gives, failing the test when it takes longer than a few seconds: a FIFO opened for reading waits for a writer.
	static T Within<T>(Func<T> work)
	{
		var run = Task.Run(work, TestContext.Current.CancellationToken);
		Assert.True(run.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the check waited on a FIFO");
		return run.Result;
	}

	static (int Code, string Out, string Err) RunWithin(string root, Files? files = null, Caps? caps = null) => Within(() => Run(root, caps, files));

	[Fact]
	public void A_fifo_is_not_read()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		MakeFifo(Path.Join(tmp, "Pipe.props"));

		var (status, output, _) = RunWithin(tmp);

		Assert.Equal(1, status);
		Assert.Contains("Pipe.props can't be checked: it is not a regular file.", output);
	}

	[Theory]
	[InlineData(-1, true)]
	[InlineData(0, true)]
	[InlineData(1, false)]
	public void A_file_over_max_bytes_is_not_read(int extra, bool read)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		const string Body = "<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>";
		var padding = Defaults.MaxBytes + extra - Body.Length - "<!--".Length - "-->".Length;
		Write(Path.Join(tmp, "Big.props"), $"<!--{new string('x', padding)}-->{Body}");
		Assert.Equal(Defaults.MaxBytes + extra, new FileInfo(Path.Join(tmp, "Big.props")).Length);

		var (status, output, _) = Run(tmp);

		Assert.Equal(read ? 0 : 1, status);
		Assert.Equal(!read, output.Contains($"Big.props can't be checked: it is over {Defaults.MaxBytes} bytes.", StringComparison.Ordinal));
	}

	// A Files whose lstat says the file at path is 10 bytes, as if it grew past its cap after the lstat.
	static Stand Small(string path) => new() { Stat = given => given == path ? new Stat(false, false, 10) : null };

	[Fact]
	public void A_file_that_grew_past_max_bytes_since_lstat_is_not_read()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var grown = Write(Path.Join(tmp, "Grown.props"), $"<!--{new string('x', Defaults.MaxBytes)}--><Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>");

		var (status, output, _) = Run(tmp, files: Small(grown));

		Assert.Equal(1, status);
		Assert.Contains($"Grown.props can't be checked: it is over {Defaults.MaxBytes} bytes.", output);
	}

	[Fact]
	public void A_global_json_that_grew_past_its_cap_since_lstat_is_skipped()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		var json = Write(Path.Join(tmp, "global.json"), "{}" + new string(' ', Defaults.MaxGlobalJsonBytes));

		var (status, output, _) = Run(tmp, files: Small(json));

		Assert.Equal(0, status);
		Assert.Contains($"ℹ️  global.json was skipped: it is over {Defaults.MaxGlobalJsonBytes} bytes.", output);
	}

	[Fact]
	public void An_unreadable_file_cant_be_checked_and_says_why()
	{
		// Stood in for rather than chmod 0, which root (a container, a sandbox) reads anyway.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var locked = Project(tmp, "Locked.csproj", "<PackageReference Include=\"A\" />");
		var files = new Stand { Opened = path => path == locked ? throw new OsError("Permission denied") : null };

		var (status, output, err) = Run(tmp, files: files);

		Assert.Equal(1, status);
		Assert.Contains("❌ Locked.csproj can't be checked: it can't be read (Permission denied). It may hold", output);
		Assert.Contains("must be readable for the check to pass", err);
	}

	// A Files whose lstat says the file at path is a regular one, as if it was swapped after the lstat.
	static Stand Regular(string path) => new() { Stat = given => given == path ? new Stat(false, false, 100) : null };

	[Fact]
	public void A_path_that_became_a_fifo_after_lstat_is_not_read()
	{
		// Opened without waiting for a writer, then refused on what the open file is.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var swapped = Path.Join(tmp, "Swapped.props");
		MakeFifo(swapped);

		var (status, output, _) = RunWithin(tmp, Regular(swapped));

		Assert.Equal(1, status);
		Assert.Contains("Swapped.props can't be checked: it is not a regular file.", output);
	}

	[Fact]
	public void A_path_that_became_a_symlink_after_lstat_is_not_followed()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var outside = Write(tmp + "-outside.props", "<Project><ItemGroup><PackageReference Include=\"A\" /></ItemGroup></Project>\n");
		try
		{
			var swapped = Path.Join(tmp, "Swapped.props");
			File.CreateSymbolicLink(swapped, outside);

			var (status, output, _) = Run(tmp, files: Regular(swapped));

			Assert.Equal(1, status);
			Assert.Contains("Swapped.props can't be checked: it can't be read (", output);
		}
		finally
		{
			File.Delete(outside);
		}
	}

	// What a file that can't be read may hide.

	[Fact]
	public void A_central_file_that_cant_be_read_fails_the_check()
	{
		// Even one cut off before its first pin: by its name, it is the file that holds them.
		Write(Path.Join(tmp, "Directory.Packages.props"), "<Project><ItemGroup>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ Directory.Packages.props can't be checked: it isn't well-formed MSBuild XML (", output);
	}

	[Fact]
	public void A_symlinked_central_file_fails_the_check()
	{
		var real = Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", folder: "shared");
		Directory.CreateDirectory(Path.Join(tmp, "src"));
		File.CreateSymbolicLink(Path.Join(tmp, "src/Directory.Packages.props"), real);
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ src/Directory.Packages.props can't be checked: it is a symlink.", output);
	}

	[Theory]
	[InlineData("<CentralPackageTransitivePinningEnabled>true", "utf-8")]
	[InlineData("<CentralPackageTransitivePinningEnabled>true", "utf-16")]
	[InlineData("<ItemGroup><PackageVersion Include=\"A\" Version=\"1\" />", "utf-8")]
	[InlineData("<ItemGroup><PackageVersion Include=\"A\" Version=\"1\" />", "utf-16")]
	public void A_malformed_file_that_names_pins_or_the_setting_fails_the_check(string body, string encoding)
	{
		// Skipping it could hide the setting, so the check would say pinning is off; or a pin, which it would never check.
		Central(tmp, "<PackageVersion Include=\"B\" Version=\"1.0.0\" />", properties: "");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"B\" />");
		var text = $"<Project><PropertyGroup>{body}";
		WriteBytes(Path.Join(tmp, "Directory.Build.props"), encoding == "utf-16" ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)] : Encoding.UTF8.GetBytes(text));

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (", output);
	}

	[Fact]
	public void The_check_needs_a_root_folder()
	{
		var (status, _, err) = Run(Path.Join(tmp, "typo"));

		Assert.Equal(2, status);
		Assert.Contains("is not a folder", err);
	}

	// A Files that can't list folder, as os.scandir raising PermissionError.
	static Stand Unlistable(string folder) => new() { Listed = given => { if (given == folder) throw new OsError("Permission denied"); } };

	[Fact]
	public void A_folder_that_cant_be_listed_fails_the_check()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Directory.CreateDirectory(Path.Join(tmp, "locked"));

		var (status, output, _) = Run(tmp, files: Unlistable(Path.Join(tmp, "locked")));

		Assert.Equal(1, status);
		Assert.Contains("❌ locked/ can't be checked: it can't be listed (Permission denied).", output);
	}

	[Fact]
	public void A_root_that_cant_be_listed_fails_the_check()
	{
		var (status, output, _) = Run(tmp, files: Unlistable(tmp));

		Assert.Equal(1, status);
		Assert.StartsWith("❌ . can't be checked: it can't be listed (Permission denied).", output);
	}

	[Fact]
	public void A_symlinked_folder_fails_the_check()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "real/App/App.csproj", "<PackageReference Include=\"A\" />");
		Directory.CreateSymbolicLink(Path.Join(tmp, "linked"), Path.Join(tmp, "real"));

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ linked/ can't be checked: it is a symlinked folder, which isn't walked.", output);
	}

	// What a file that can't be checked does to the rest of the report.

	[Fact]
	public void A_file_that_cant_be_checked_fails_even_with_warnings()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"@(Z)\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><ItemGroup><PackageVersion Include=\"B\"");

		var (status, output, err) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (", output);
		Assert.Contains("⚠️  A is pinned in Directory.Packages.props but no project references it\n", output);
		Assert.StartsWith("Not a pass: ", Lines(output)[^1]);
		Assert.Single(err.Split("must be readable for the check to pass")[1..]);
	}

	[Fact]
	public void A_file_that_cant_be_checked_ends_the_report_after_the_pins()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Broken.props"), "<Project><PackageVersion");

		var (status, output, err) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Equal(["❌ A is pinned in Directory.Packages.props but no project references it",
				"Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for."], Lines(output)[^2..]);
		Assert.Contains("Delete each pin above", err);
		Assert.Contains("must be readable for the check to pass", err);
	}

	[Theory]
	[InlineData("", "<PackageVersion Include=\"A\" Version=\"1.0.0\" />")]
	[InlineData(Transitive, "")]
	public void A_file_that_cant_be_checked_is_never_reported_as_a_pass(string properties, string items)
	{
		Central(tmp, items, properties: properties);
		Write(Path.Join(tmp, "Broken.props"), "<Project><PackageVersion");

		var (status, output, err) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Equal("Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for.", Lines(output)[^1]);
		Assert.DoesNotContain("Transitive pinning is off", output);
		Assert.DoesNotContain("Every central pin is referenced", output);
		Assert.Contains($"nested at most {Defaults.MaxDepth} deep, in an encoding .NET decodes", err);
	}

	[Fact]
	public void A_skipped_file_fails_the_check_when_a_pin_cant_be_resolved()
	{
		// The pin is built from $(P), which only the broken file may set.
		Central(tmp, "<PackageVersion Include=\"$(P)\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><P>A</P>");
		Write(Path.Join(tmp, "global.json"), "{");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (", output);
		Assert.Contains("ℹ️  global.json was skipped: it isn't JSON the .NET host reads.", output);
	}

	[Fact]
	public void A_skipped_file_fails_the_check_when_a_pin_is_built_from_a_property()
	{
		// $(P) resolves to A from a file read, but the broken file may give it another value, and so another pin.
		Central(tmp, "<PackageVersion Include=\"$(P)\" Version=\"1.0.0\" />", properties: Transitive + "<PropertyGroup><P>A</P></PropertyGroup>");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup Condition=\"x\"><P>B</P>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(1, status);
		Assert.Contains("❌ Directory.Build.props can't be checked", output);
	}

	[Theory]
	[InlineData("@(Items)")]
	[InlineData("%(Meta)")]
	[InlineData("$(")]
	[InlineData("$(P)@(Items)")]
	[InlineData("$(P)$(1)")]
	public void A_skipped_file_stays_a_notice_for_a_pin_no_file_can_make_readable(string pin)
	{
		// No value of P makes these readable ($(1) isn't a property name), so the broken file hides nothing about them.
		Central(tmp, $"<PackageVersion Include=\"{pin}\" Version=\"1.0.0\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><P>A</P>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.Contains("ℹ️  Directory.Build.props was skipped", output);
	}

	[Theory]
	[InlineData("", "<PackageVersion Include=\"$(Unset)\" Version=\"1.0.0\" />")]
	[InlineData(Transitive, "<!-- Transitive pin: kept --><PackageVersion Include=\"$(Unset)\" Version=\"1.0.0\" />")]
	public void A_skipped_file_stays_a_notice_without_transitive_pinning_or_for_a_kept_pin(string properties, string items)
	{
		Central(tmp, items, properties: properties);
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><Unset>A</Unset>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.Contains("ℹ️  Directory.Build.props was skipped", output);
	}

	[Fact]
	public void A_skipped_file_stays_a_notice_when_every_pin_resolves()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Write(Path.Join(tmp, "Directory.Build.props"), "<Project><PropertyGroup><P>A</P>");

		var (status, output, _) = Run(tmp);

		Assert.Equal(0, status);
		Assert.Contains("ℹ️  Directory.Build.props was skipped", output);
	}

	[Theory]
	[InlineData("notice", "malformed")]
	[InlineData("error", "fifo")]
	public void Notices_and_errors_are_printed_at_most_max_shown_times(string kind, string setup)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		for (var i = 0; i < 3; i++)
		{
			if (setup == "fifo")
				MakeFifo(Path.Join(tmp, $"P{i}.props"));
			else
				Write(Path.Join(tmp, $"P{i}.props"), "<Project>");
		}

		var (status, output, err) = RunWithin(tmp, caps: new Caps { MaxShown = 2 });

		var marker = kind == "notice" ? "ℹ️  P" : "❌ P";
		var lines = Lines(output);
		Assert.Equal([marker + "0", marker + "1"], lines.Take(2).Select(line => line[..(marker.Length + 1)]));
		Assert.Equal("... and 1 more like the 2 above.", lines[2]);
		Assert.Equal(kind == "error" ? 1 : 0, status);
		Assert.Equal(kind == "error" ? 1 : 0, err.Split("must be readable").Length - 1);
	}

	[Fact]
	public void A_long_path_is_cut_in_the_annotations_file_property()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", folder: new string('F', 20));

		var output = RunCi(tmp, new Caps { MaxPrinted = 10 });

		// FFFF.../Directory.Packages.props is 45 characters: 10 kept, 35 cut.
		Assert.Equal("::error file=FFFFFFFFFF... (35 more)::A is pinned in FFFFFFFFFF... (35 more) but no project references it\n", output);
	}

	[Fact]
	public void A_long_name_or_path_is_cut_where_it_is_printed()
	{
		// A pin's name can't be longer than MaxId, which is under MaxPrinted, so a short cap shows the cut on it too.
		var name = new string('N', Defaults.MaxPrinted + 50);
		Central(tmp, "<PackageVersion Include=\"AAAAAAAAAAAAAAA\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", $"<PackageReference Include=\"@({name})\" />");

		var output = Run(tmp).Out;
		var cut = Run(tmp, new Caps { MaxPrinted = 10 }).Out;

		// "@(", the name and ")" are 353 characters: 300 kept, 53 cut.
		Assert.Contains($"(@({new string('N', 298)}... (53 more))", output);
		Assert.StartsWith("⚠️  AAAAAAAAAA... (5 more) is pinned in Directory.... (14 more) but no project references it\n", cut);
	}

	// The open file's descriptor.

	[Fact]
	public void A_descriptor_refused_after_fstat_is_closed()
	{
		var swapped = Path.Join(tmp, "Swapped.props");
		MakeFifo(swapped);
		var files = Regular(swapped);

		RunWithin(tmp, files);

		Assert.NotEmpty(files.Handles);
		Assert.All(files.Handles, handle => Assert.True(handle.IsClosed));
	}

	[Fact]
	public void A_descriptor_whose_fstat_fails_is_closed()
	{
		Project(tmp, "App.csproj", "<PackageReference Include=\"A\" />");
		var files = new Stand { Type = _ => throw new OsError("Input/output error") };

		var (status, output, _) = Run(tmp, files: files);

		Assert.Equal(1, status);
		Assert.Contains("❌ App.csproj can't be checked: it can't be read (Input/output error).", output);
		Assert.NotEmpty(files.Handles);
		Assert.All(files.Handles, handle => Assert.True(handle.IsClosed));
	}

	[Fact]
	public void A_descriptor_read_is_closed_once()
	{
		Project(tmp, "App.csproj", "<PackageReference Include=\"A\" />");
		var files = new Stand();

		Assert.Equal(0, Run(tmp, files: files).Code);
		var handle = Assert.Single(files.Handles);
		Assert.True(handle.IsClosed);
	}

	// Output under GitHub Actions, and text a repo controls.

	[Fact]
	public void Ci_gets_an_error_annotation_naming_the_file()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");

		var (status, output, err) = Run(tmp, actions: "true");

		Assert.Equal(1, status);
		Assert.Equal("::error file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it\n", output);
		Assert.Contains("<!-- Transitive pin: <the reason> -->", err);
	}

	[Theory]
	[InlineData("false")]
	[InlineData("1")]
	[InlineData("TRUE")]
	[InlineData("")]
	public void Github_actions_other_than_true_gets_plain_lines(string value)
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");

		Assert.StartsWith("❌ A is pinned", Run(tmp, actions: value).Out);
	}

	[Fact]
	public void Ci_gets_warning_and_notice_annotations()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"@(Packages)\" />");
		Write(Path.Join(tmp, "Broken.props"), "<Project>");

		var output = RunCi(tmp);

		Assert.Contains("::notice file=Broken.props::Broken.props was skipped: it isn't well-formed MSBuild XML (", output);
		Assert.Contains("::warning file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it\n", output);
	}

	// A character reference keeps its line break through XML attribute normalisation. U+009B is the one-character CSI.
	const string Hostile = "A&#10;::add-mask::x&#13;::stop-commands::t%0A&#x9b;2J";

	[Fact]
	public void An_id_cant_start_a_workflow_command_in_ci()
	{
		Central(tmp, $"<PackageVersion Include=\"{Hostile}\" Version=\"1.0.0\" />");

		Assert.Equal("::error file=Directory.Packages.props::A?::add-mask::x?::stop-commands::t%250A?2J is pinned in Directory.Packages.props but no project references it\n",
				RunCi(tmp));
	}

	[Fact]
	public void An_id_cant_move_the_terminal_in_a_plain_line()
	{
		Central(tmp, $"<PackageVersion Include=\"{Hostile}\" Version=\"1.0.0\" />");

		Assert.Equal("❌ A?::add-mask::x?::stop-commands::t%0A?2J is pinned in Directory.Packages.props but no project references it\n", Run(tmp).Out);
	}

	[Theory]
	[InlineData(-1, false)]
	[InlineData(0, false)]
	[InlineData(1, true)]
	public void Printable_cuts_a_text_over_max_printed_characters(int extra, bool cut)
	{
		var text = new string('x', Defaults.MaxPrinted + extra);

		Assert.Equal(cut ? $"{new string('x', Defaults.MaxPrinted)}... (1 more)" : text, Pins().Printable(text));
	}

	[Theory]
	[InlineData("{\"a\": [1,]}", "{\"a\": [1]}")]
	[InlineData("{\"a\": 1, \n}", "{\"a\": 1 \n}")]
	[InlineData("{\"a\": \"x // y", "{\"a\": \"x // y")]
	[InlineData("{\"a\": 1} /* x", "{\"a\": 1} ")]
	[InlineData("[1, /* a\n */]", "[1 ]")]
	[InlineData("{\"a\": \"x\\", "{\"a\": \"x\\")]
	[InlineData("{\"a\": 1, // c\r\n}", "{\"a\": 1 \n}")]
	[InlineData("", "")]
	[InlineData("/", "/")]
	[InlineData("/*/", "")]
	[InlineData("{\"a\": \"/* // */\"}", "{\"a\": \"/* // */\"}")]
	[InlineData("[\"a\\\n\", 1,]", "[\"a\\\n\", 1]")]
	public void Global_json_comments_and_trailing_commas_go_as_the_host_reads_them(string text, string expected)
	{
		// An unclosed string or comment runs to the end; a trailing comma goes, the space after it stays; an escape may take a line break.
		Assert.Equal(expected, CentralPins.WithoutComments(text));
	}

	[Theory]
	[InlineData("x", 70000)]
	[InlineData("\\n", 35000)]
	[InlineData("\"a\",", 17000)]
	[InlineData("/**/", 17000)]
	[InlineData("1, ", 23000)]
	public void A_global_json_at_its_cap_is_read_in_bounded_time_and_memory(string piece, int count)
	{
		// The pytest's bodies: a string of x or of \n escapes left open (the first two), and the rest as they are.
		var body = (piece is "x" or "\\n" ? "\"" : "") + string.Concat(Enumerable.Repeat(piece, count));
		var text = ("{\"msbuild-sdks\": {\"S\": \"1\"}, \"x\": [" + body)[..Defaults.MaxGlobalJsonBytes];
		Write(Path.Join(tmp, "global.json"), text);
		Check(tmp);
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.StartNew();

		Check(tmp);

		Assert.Equal(Defaults.MaxGlobalJsonBytes, text.Length);
		Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"took {started.Elapsed}");
		// Each pass copies the text once, so the run allocates a small multiple of it, never one copy per character.
		Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 4 * 1024 * 1024, $"allocated {GC.GetAllocatedBytesForCurrentThread() - allocated} bytes");
	}

	[Fact]
	public void A_global_json_at_its_cap_is_read()
	{
		Central(tmp, "<PackageVersion Include=\"S\" Version=\"1.0.0\" />");
		const string Text = "{\"msbuild-sdks\": {\"S\": \"1\"}}";
		Write(Path.Join(tmp, "global.json"), Text + new string(' ', Defaults.MaxGlobalJsonBytes - Text.Length));

		var (status, output, _) = Run(tmp);

		Assert.Equal(Defaults.MaxGlobalJsonBytes, new FileInfo(Path.Join(tmp, "global.json")).Length);
		Assert.Equal((0, "Every central pin is referenced by a project, or kept with a reason.\n"), (status, output));
	}

	[Fact]
	public void Only_the_first_max_global_json_files_are_read()
	{
		Central(tmp, string.Concat("RABCD".Select(sdk => $"<PackageVersion Include=\"{sdk}\" Version=\"1.0.0\" />")));
		// The root's sorts after a/ by path, and a/z/'s before b/: by depth, the root and then a/ and b/ are read.
		foreach (var (folder, sdk) in new[] { ("", "R"), ("a/z", "D"), ("a", "A"), ("b", "B"), ("c/y", "C") })
			Write(Path.Join(tmp, folder, "global.json"), $"{{\"msbuild-sdks\": {{\"{sdk}\": \"1\"}}}}");

		var (status, output, _) = Run(tmp, new Caps { MaxGlobalJsonFiles = 3 });

		Assert.Equal(1, status);
		const string Skipped = "was skipped: it is past the first 3 global.json files, which are all this check reads. A reference or property in it wasn't seen.";
		Assert.Equal([$"ℹ️  a/z/global.json {Skipped}", $"ℹ️  c/y/global.json {Skipped}",
				"❌ C is pinned in Directory.Packages.props but no project references it",
				"❌ D is pinned in Directory.Packages.props but no project references it"], Lines(output));
	}

	[Fact]
	public void A_global_json_over_its_cap_is_skipped_without_being_opened()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		var json = Write(Path.Join(tmp, "global.json"), "{}" + new string(' ', Defaults.MaxGlobalJsonBytes - 1));
		var files = new Stand { Opened = path => path == json ? throw new InvalidOperationException("opened") : null };

		var (status, output, _) = Run(tmp, files: files);

		Assert.Equal(0, status);
		Assert.Contains($"ℹ️  global.json was skipped: it is over {Defaults.MaxGlobalJsonBytes} bytes.", output);
	}

	[Fact]
	public void The_escapes_are_the_runners()
	{
		// Printable() already turns line breaks into '?', so these are the second line of defence, tested on their own.
		Assert.Equal("a%25%0D%0Ab:,", CentralPins.EscapeData("a%\r\nb:,"));
		Assert.Equal("a%25%0D%0Ab%3A%2C", CentralPins.EscapeProperty("a%\r\nb:,"));
	}

	[Fact]
	public void A_path_cant_start_a_line_in_the_file_property()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", folder: "a\n::error::b");

		Assert.Equal("::error file=a?%3A%3Aerror%3A%3Ab/Directory.Packages.props::A is pinned in a?::error::b/Directory.Packages.props but no project references it\n",
				RunCi(tmp));
	}

	[Fact]
	public void A_path_cant_break_the_file_property()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />", folder: "a,b:c%d");

		Assert.Equal("::error file=a%2Cb%3Ac%25d/Directory.Packages.props::A is pinned in a,b:c%25d/Directory.Packages.props but no project references it\n",
				RunCi(tmp));
	}

	// Where the gate and CI run it.

	[Fact]
	public void Build_solution_runs_the_check_after_setup_dotnet_and_before_restore()
	{
		// As C# it needs the SDK, so it runs after Setup .NET (no longer first, ADR 0017's Update for mpaulosky/Application_Factory#126), built in a
		// step of its own, and before restore, which would fail with NU1109 on a bad pin.
		var yaml = new YamlStream();
		yaml.Load(new StringReader(Skill.Workflow("ci.yml")));
		var job = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children["jobs"]).Children["build"];
		var steps = ((YamlSequenceNode)job.Children["steps"]).Children.Cast<YamlMappingNode>().ToList();
		static string? Text(YamlMappingNode node, string key) => node.Children.TryGetValue(key, out var value) ? ((YamlScalarNode)value).Value : null;
		var names = steps.Select(step => Text(step, "name")).ToList();
		var step = steps.Single(step => Text(step, "name") == "Check central package pins");

		Assert.Equal("Build Solution", Text(job, "name"));
		Assert.Equal($"{Command} --root .", Text(step, "run"));
		Assert.False(step.Children.ContainsKey("if") || step.Children.ContainsKey("continue-on-error"));
		Assert.Equal("dotnet build .github/scripts/check_central_pins.cs", Text(steps[names.IndexOf("Check central package pins") - 1], "run"));
		Assert.Equal(names.IndexOf("Setup .NET") + 2, names.IndexOf("Check central package pins"));
		Assert.Equal(names.IndexOf("Checkout code") + 1, names.IndexOf("Setup .NET"));
		Assert.True(names.IndexOf("Check central package pins") < names.IndexOf("Restore dependencies"));
	}

	[Fact]
	public void The_gate_runs_the_check_before_the_build()
	{
		var lines = File.ReadAllLines(Skill.Path("template", "owned", "scripts", "gate.sh")).ToList();
		var run = lines.IndexOf($"{Command} --root \"$ROOT\"");
		var build = lines.FindIndex(line => line.Contains("dotnet build .github/scripts/check_central_pins.cs", StringComparison.Ordinal));

		Assert.True(run >= 0, "the gate doesn't run the check");
		Assert.True(build >= 0 && build < run, "the gate doesn't build the check before it runs it");
		Assert.True(run < lines.IndexOf("step \"Build\""));
	}

	// The port's own.

	[Fact]
	public void Problems_come_in_the_order_os_walk_finds_them()
	{
		// os.walk lists a folder's own symlinked subfolders when it lists it, then walks its subfolders in sorted order, depth first: a/'s
		// problem comes before b/'s, and b/'s before z/'s, though z/ is listed at the root.
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		Project(tmp, "src/App/App.csproj", "<PackageReference Include=\"A\" />");
		Directory.CreateDirectory(Path.Join(tmp, "a/deep"));
		Directory.CreateDirectory(Path.Join(tmp, "b"));
		Directory.CreateSymbolicLink(Path.Join(tmp, "z"), Path.Join(tmp, "b"));
		Directory.CreateSymbolicLink(Path.Join(tmp, "b/link"), Path.Join(tmp, "a"));

		var where = Check(tmp, files: Unlistable(Path.Join(tmp, "a/deep"))).Problems.Select(problem => problem.Where);

		Assert.Equal(["z/", "a/deep/", "b/link/"], where);
	}

	[Fact]
	public void A_name_that_vanished_since_the_walk_cant_be_read()
	{
		Central(tmp, "<PackageVersion Include=\"A\" Version=\"1.0.0\" />");
		var gone = Project(tmp, "Gone.csproj", "<PackageReference Include=\"A\" />");
		var files = new Stand { Stat = path => { if (path == gone) File.Delete(gone); return null; } };

		var (status, output, _) = Run(tmp, files: files);

		Assert.Equal(1, status);
		Assert.Contains("❌ Gone.csproj can't be checked: it can't be read (No such file or directory).", output);
	}

	[Fact]
	public void A_folder_the_os_cant_list_gives_its_strerror()
	{
		Assert.Equal("Permission denied", Files.StrError(new UnauthorizedAccessException("Access to the path '/x' is denied.")));
		Assert.Equal("No such file or directory", Files.StrError(new DirectoryNotFoundException("Could not find a part of the path '/x'.")));
		Assert.Equal("Input/output error", Files.StrError(new IOException("EIO", 5)));
		Assert.Equal("what .NET said", Files.StrError(new IOException("what .NET said")));
	}

	[Fact]
	public void The_real_open_follows_no_symlink_and_waits_on_no_fifo()
	{
		var target = Write(Path.Join(tmp, "target.props"), "<Project />");
		File.CreateSymbolicLink(Path.Join(tmp, "link.props"), target);
		MakeFifo(Path.Join(tmp, "pipe.props"));
		var files = new Files();

		var refused = Assert.Throws<OsError>(() => files.Open(Path.Join(tmp, "link.props")));
		var type = Within(() =>
		{
			using var handle = files.Open(Path.Join(tmp, "pipe.props"));
			return files.TypeOf(handle);
		});

		Assert.Equal("Too many levels of symbolic links", refused.Message);
		Assert.Equal(FileHandleType.Pipe, type);
		using var regular = files.Open(target);
		Assert.Equal(FileHandleType.RegularFile, files.TypeOf(regular));
		Assert.Equal("<Pro"u8.ToArray(), files.Read(regular, 4));
	}

	[Fact]
	public void A_reason_marker_is_read_in_ascii_alone()
	{
		// Python's lower() turns no other letter into one the marker is spelled with: a dotless i and a dotted capital I stay as they are,
		// where .NET's invariant upper and lower casing would match them.
		Central(tmp, "<!-- Transıtive pin: a fix --><PackageVersion Include=\"A\" Version=\"1\" /><!-- TRANSİTIVE PIN: a fix --><PackageVersion Include=\"B\" Version=\"1\" />"
				+ "<!-- TRANSITIVE PIN: a fix --><PackageVersion Include=\"C\" Version=\"1\" />");

		Assert.Equal(["A", "B"], Unreferenced(tmp));
	}

	[Fact]
	public void Lengths_and_cuts_count_code_points()
	{
		var astral = string.Concat(Enumerable.Repeat("\U0001F600", 5));

		Assert.Equal(5, Py.Len(astral));
		Assert.Equal(1, Py.Len("\uD800"));
		Assert.Equal("\U0001F600\U0001F600", Py.Head(astral, 2));
		Assert.Equal("\U0001F600\U0001F600... (3 more)", Pins(new Caps { MaxPrinted = 2 }).Printable(astral));
		Assert.Equal("a?b", Pins().Printable("a\uD800b"));
		Assert.True(Py.CompareCodePoints("\U0001F600", "Ａ") > 0);
	}
}
