import errno
import os
import sys
import time
from pathlib import Path

import pytest

import check_central_pins as ccp

OWNED = Path(__file__).resolve().parents[3]
COMMAND = "python3 -I .github/scripts/check_central_pins.py"

TRANSITIVE = "<PropertyGroup><CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled></PropertyGroup>"


@pytest.fixture(autouse=True)
def plain_output(monkeypatch):
    # CI runs these tests with GITHUB_ACTIONS=true; each test sets it when it means to.
    monkeypatch.delenv("GITHUB_ACTIONS", raising=False)


def central(root, items, properties=TRANSITIVE, folder="", name="Directory.Packages.props"):
    path = root / folder / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f"<Project>\n  {properties}\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n")
    return path


def project(root, relative, body, sdk="Microsoft.NET.Sdk"):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f'<Project Sdk="{sdk}">\n  <ItemGroup>{body}</ItemGroup>\n</Project>\n')
    return path


def unreferenced(root):
    return [name for _, name in ccp.check(root)[1]]


def run(root, capsys):
    status = ccp.main(["--root", str(root)])
    captured = capsys.readouterr()
    return status, captured.out, captured.err


# What is reported, and what passes.


def test_a_pin_no_project_references_is_reported(tmp_path, capsys):
    # IssueManager's: Radzen.Blazor needed a newer Components.Web than the pin, which nothing referenced.
    central(tmp_path, '<PackageVersion Include="Radzen.Blazor" Version="13.1.0" />\n'
            '<PackageVersion Include="Microsoft.AspNetCore.Components.Web" Version="10.0.14" />')
    project(tmp_path, "src/Web/Web.csproj", '<PackageReference Include="Radzen.Blazor" />')

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert out == "❌ Microsoft.AspNetCore.Components.Web is pinned in Directory.Packages.props but no project references it\n"
    assert "::" not in out
    assert "Delete each pin above" in err and "<!-- Transitive pin: <the reason> -->" in err
    assert "a package an SDK references itself" in err


def test_every_pin_referenced_passes(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="Radzen.Blazor" Version="13.1.0" />')
    project(tmp_path, "src/Web/Web.csproj", '<PackageReference Include="Radzen.Blazor" />')

    assert run(tmp_path, capsys) == (0, "Every central pin is referenced by a project, or kept with a reason.\n", "")


def test_without_transitive_pinning_an_unreferenced_pin_passes(tmp_path, capsys):
    # It pins nothing then: TicketManager's and Articles' leftovers.
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties="")

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert out.startswith("Transitive pinning is off")


def test_a_repo_without_central_package_management_passes(tmp_path, capsys):
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="Polly" Version="8.0.0" />')

    assert run(tmp_path, capsys)[0] == 0


@pytest.mark.parametrize("value", ["true", "True", " TRUE "])
def test_transitive_pinning_is_read_ignoring_case_and_spaces(tmp_path, value):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />',
            properties=f"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled></PropertyGroup>")

    assert ccp.check(tmp_path)[0] is True


@pytest.mark.parametrize("value", ["false", "", "yes"])
def test_transitive_pinning_other_than_true_is_off(tmp_path, capsys, value):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />',
            properties=f"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled></PropertyGroup>")

    assert run(tmp_path, capsys)[0] == 0


def test_transitive_pinning_set_in_directory_build_props_counts(tmp_path):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties="")
    (tmp_path / "Directory.Build.props").write_text(f"<Project>{TRANSITIVE}</Project>\n")

    assert ccp.check(tmp_path)[:2] == (True, [(tmp_path.resolve() / "Directory.Packages.props", "Polly")])


def setting(value, *others):
    """The PropertyGroups of a Directory.Packages.props that sets the setting to value, and others as its other properties."""
    rest = "".join(f"<{name}>{text}</{name}>" for name, text in others)
    return f"<PropertyGroup><CentralPackageTransitivePinningEnabled>{value}</CentralPackageTransitivePinningEnabled>{rest}</PropertyGroup>"


@pytest.mark.parametrize("value", ["true", "True "])
def test_a_setting_built_from_a_property_set_to_true_is_on(tmp_path, capsys, value):
    # The finding on mpaulosky/Application_Factory#110: $(PinTransitive) read as off, so every unreferenced pin passed.
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting("$(PinTransitive)"))
    (tmp_path / "Directory.Build.props").write_text(f"<Project><PropertyGroup><PinTransitive>{value}</PinTransitive></PropertyGroup></Project>\n")

    assert ccp.check(tmp_path)[0] is True
    assert ccp.check(tmp_path)[4] == []
    status, out, _ = run(tmp_path, capsys)
    assert status == 1
    assert out == "❌ Polly is pinned in Directory.Packages.props but no project references it\n"


def test_a_setting_built_from_a_property_set_to_anything_else_is_off(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting("$(PinTransitive)", ("PinTransitive", "false")))

    assert ccp.check(tmp_path)[0] is False
    status, out, _ = run(tmp_path, capsys)
    assert status == 0
    assert out.startswith("Transitive pinning is off")


def test_a_setting_built_from_a_property_with_true_among_its_values_is_on(tmp_path):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting("$(P)", ("P", "false")))
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><P>true</P></PropertyGroup></Project>\n")

    assert ccp.check(tmp_path)[:2] == (True, [(tmp_path.resolve() / "Directory.Packages.props", "Polly")])


@pytest.mark.parametrize("value", ["$(NotSetAnywhere)", "@(Items)", "%(Meta)", "$(P)", "$([MSBuild]::ValueOrDefault('', 'true'))"])
def test_a_setting_that_cant_be_read_counts_as_on_and_is_named(tmp_path, capsys, value):
    # Under it an unreferenced pin is held back or does nothing, so deleting it is safe either way: the check fails closed.
    others = [("P", "$(Q)")] if value == "$(P)" else []
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting(value, *others))

    assert ccp.check(tmp_path)[0] is True
    assert ccp.check(tmp_path)[4] == [("Directory.Packages.props", value)]
    status, out, _ = run(tmp_path, capsys)
    assert status == 1
    assert out == (
        f"ℹ️  Directory.Packages.props sets CentralPackageTransitivePinningEnabled to {value}, which this check can't read without "
        "evaluating MSBuild, so it counts transitive pinning as on.\n"
        "❌ Polly is pinned in Directory.Packages.props but no project references it\n"
    )


def test_a_setting_that_cant_be_read_with_every_pin_referenced_passes(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting("$(NotSetAnywhere)"))
    project(tmp_path, "src/A/A.csproj", '<PackageReference Include="Polly" />')

    status, out, _ = run(tmp_path, capsys)
    assert status == 0
    assert out.endswith("Every central pin is referenced by a project, or kept with a reason.\n")
    assert "can't read without evaluating MSBuild" in out


def test_a_setting_that_cant_be_read_is_named_once_however_often_it_is_set(tmp_path, capsys):
    central(tmp_path, "", properties=setting("$(X)") + setting("$(X)"))

    assert ccp.check(tmp_path)[4] == [("Directory.Packages.props", "$(X)")]


def test_a_literal_true_elsewhere_settles_it_and_names_no_setting(tmp_path):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties=setting("$(NotSetAnywhere)"))
    (tmp_path / "Directory.Build.props").write_text(f"<Project>{TRANSITIVE}</Project>\n")

    assert ccp.check(tmp_path)[0] is True
    assert ccp.check(tmp_path)[4] == []


def test_an_unreadable_setting_cant_start_a_workflow_command(tmp_path, capsys, monkeypatch):
    monkeypatch.setenv("GITHUB_ACTIONS", "true")
    central(tmp_path, "", properties=setting("$(A)\n::error::x"))

    out = run(tmp_path, capsys)[1]
    assert out.count("\n::") == 0 and out.startswith("::notice file=Directory.Packages.props::")


def test_a_pin_listed_twice_is_reported_once(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" Condition="x" />\n<PackageVersion Include="a" Version="2.0.0" Condition="y" />')

    assert unreferenced(tmp_path) == ["A"]


def test_an_include_with_several_ids_is_read_as_each(tmp_path):
    central(tmp_path, '<PackageVersion Include="A; B" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')

    assert unreferenced(tmp_path) == ["B"]


def test_a_pin_under_choose_or_a_conditioned_item_group_is_checked(tmp_path):
    (tmp_path / "Directory.Packages.props").write_text(
        f"<Project>{TRANSITIVE}<Choose><When Condition=\"x\"><ItemGroup><PackageVersion Include=\"A\" Version=\"1\" /></ItemGroup></When></Choose>"
        "<ItemGroup Condition=\"y\"><PackageVersion Include=\"B\" Version=\"1\" /></ItemGroup></Project>\n")

    assert unreferenced(tmp_path) == ["A", "B"]


def test_a_nested_central_file_and_its_name_in_any_case_is_checked(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="samples", name="directory.packages.props")

    assert [(path.parent.name, name) for path, name in ccp.check(tmp_path)[1]] == [("samples", "A")]


# The reason comment.


@pytest.mark.parametrize("comment", ["Transitive pin: GHSA-7jgj-8wvc-jh57, fixed in 4.3.4", "  transitive PIN:a security fix  ", "TRANSITIVE PIN: x"])
def test_a_pin_with_a_reason_comment_right_before_it_is_kept(tmp_path, comment):
    central(tmp_path, f"<!--{comment}-->\n"
            '<PackageVersion Include="System.Net.Http" Version="4.3.4" />\n'
            '<PackageVersion Include="System.Text.RegularExpressions" Version="4.3.1" />')

    assert unreferenced(tmp_path) == ["System.Text.RegularExpressions"]


def test_a_reason_comment_covers_only_the_pin_right_after_it(tmp_path):
    central(tmp_path, "<!-- Transitive pin: a security fix -->\n"
            '<PackageVersion Include="A" Version="1.0.0" />\n'
            '<PackageVersion Include="B" Version="1.0.0" />')

    assert unreferenced(tmp_path) == ["B"]


def test_a_reason_comment_separated_from_the_pin_by_an_element_is_no_reason(tmp_path):
    central(tmp_path, "<!-- Transitive pin: a security fix -->\n<PackageReference Include=\"X\" />\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />")

    assert unreferenced(tmp_path) == ["A"]


def test_only_the_last_of_two_comments_counts(tmp_path):
    central(tmp_path, "<!-- Transitive pin: a security fix -->\n<!-- Testing -->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />\n"
            "<!-- Testing -->\n<!-- Transitive pin: a security fix -->\n<PackageVersion Include=\"B\" Version=\"1.0.0\" />")

    assert unreferenced(tmp_path) == ["A"]


def test_a_group_heading_comment_is_no_reason(tmp_path):
    central(tmp_path, "<!-- Microsoft -->\n<PackageVersion Include=\"Microsoft.OpenApi\" Version=\"2.13.0\" />")

    assert unreferenced(tmp_path) == ["Microsoft.OpenApi"]


@pytest.mark.parametrize("comment", ["Transitive pin:", "Transitive pin:   ", "Transitive pin", "Pinned: transitive pin: x"])
def test_a_reason_comment_that_gives_no_reason_is_no_reason(tmp_path, comment):
    central(tmp_path, f"<!--{comment}-->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />")

    assert unreferenced(tmp_path) == ["A"]


# What counts as a reference.


def test_ids_match_ignoring_case(tmp_path):
    central(tmp_path, '<PackageVersion Include="FluentAssertions" Version="8.0.0" />')
    project(tmp_path, "tests/A/A.csproj", '<PackageReference Include="fluentassertions" />')

    assert unreferenced(tmp_path) == []


@pytest.mark.parametrize("element", ['<PackageReference Update="A" />', '<GlobalPackageReference Include="A" Version="1" />',
                                     '<PackageDownload Include="A" Version="[1]" />', '<packagereference Include="A" />'])
def test_each_kind_of_reference_counts(tmp_path, element):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="B" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", element)

    assert unreferenced(tmp_path) == ["B"]


@pytest.mark.parametrize("suffix", [".csproj", ".fsproj", ".vbproj", ".proj", ".sqlproj", ".wixproj", ".esproj", ".CSPROJ", ".props", ".targets"])
def test_a_reference_in_any_msbuild_file_counts(tmp_path, suffix):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, f"src/App/App{suffix}", '<PackageReference Include="A" />')

    assert unreferenced(tmp_path) == []


@pytest.mark.parametrize("name", ["App.json", "App.csproj.user.txt", "App.propsx", "App.target", "projects"])
def test_a_file_that_isnt_msbuild_is_not_read(tmp_path, name):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, f"src/App/{name}", '<PackageReference Include="A" />')

    assert unreferenced(tmp_path) == ["A"]


def test_an_msbuild_sdk_counts_as_a_reference(tmp_path):
    central(tmp_path, '<PackageVersion Include="Aspire.AppHost.Sdk" Version="13.6.5" />\n'
            '<PackageVersion Include="MSTest.Sdk" Version="3.0.0" />\n'
            '<PackageVersion Include="Other.Sdk" Version="1.0.0" />\n'
            '<PackageVersion Include="Json.Sdk" Version="1.0.0" />')
    project(tmp_path, "src/AppHost/AppHost.csproj", "", sdk="Microsoft.NET.Sdk;Aspire.AppHost.Sdk/13.6.5")
    (tmp_path / "tests/T").mkdir(parents=True)
    (tmp_path / "tests/T/T.csproj").write_text('<Project><Sdk Name="MSTest.Sdk" /><Import Project="x" Sdk="Other.Sdk" /></Project>\n')
    (tmp_path / "global.json").write_text('{"msbuild-sdks": {"Json.Sdk": "1.0.0"}}\n')

    assert unreferenced(tmp_path) == []


def test_a_project_with_the_legacy_namespace_or_a_prefix_is_read(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" /><PackageVersion Include="B" Version="1.0.0" />')
    (tmp_path / "Old.csproj").write_text('<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup>'
                                         '<PackageReference Include="A" /></ItemGroup></Project>\n')
    (tmp_path / "Prefixed.csproj").write_text('<m:Project xmlns:m="urn:x"><m:ItemGroup><m:PackageReference Include="B" /></m:ItemGroup></m:Project>\n')

    assert unreferenced(tmp_path) == []


@pytest.mark.parametrize("encoding,prefix", [("utf-8-sig", ""), ("utf-8", '<?xml version="1.0" encoding="utf-8"?>\n'),
                                             ("utf-16", '<?xml version="1.0" encoding="utf-16"?>\n'),
                                             ("iso-8859-1", '<?xml version="1.0" encoding="iso-8859-1"?>\n<!-- café -->\n')])
def test_a_file_in_any_encoding_msbuild_reads_is_read(tmp_path, encoding, prefix):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    (tmp_path / "App.csproj").write_bytes(f'{prefix}<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n'.encode(encoding))

    assert unreferenced(tmp_path) == []


# Ids built from properties.


def test_a_reference_built_from_a_property_is_resolved(tmp_path):
    central(tmp_path, '<PackageVersion Include="Acme.Analyzers" Version="1.0.0" /><PackageVersion Include="B" Version="1.0.0" />')
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><AnalyzerPackage>Acme.Analyzers</AnalyzerPackage>"
                                                    "<Vendor>acme</Vendor></PropertyGroup></Project>\n")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(AnalyzerPackage)" />')

    assert unreferenced(tmp_path) == ["B"]


def test_a_property_with_several_values_resolves_to_each(tmp_path):
    central(tmp_path, '<PackageVersion Include="A.Core" Version="1" /><PackageVersion Include="B.Core" Version="1" /><PackageVersion Include="C.Core" Version="1" />')
    (tmp_path / "a.props").write_text("<Project><PropertyGroup><Prefix>A</Prefix></PropertyGroup></Project>\n")
    (tmp_path / "b.props").write_text("<Project><PropertyGroup Condition=\"x\"><Prefix>b</Prefix></PropertyGroup></Project>\n")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(PREFIX).Core" />')

    assert unreferenced(tmp_path) == ["C.Core"]


def test_a_pin_built_from_a_property_is_resolved(tmp_path):
    central(tmp_path, '<PackageVersion Include="$(Prefix).Core" Version="1.0.0" />',
            properties=TRANSITIVE + "<PropertyGroup><Prefix>Acme</Prefix></PropertyGroup>")

    assert unreferenced(tmp_path) == ["$(Prefix).Core"]


@pytest.mark.parametrize("reference", ["$(Unset)", "@(Packages)", "%(Name)", "$(Nested)"])
def test_a_reference_that_cant_be_read_turns_the_report_into_warnings(tmp_path, capsys, reference):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + "<PropertyGroup><Nested>$(Unset)</Nested></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", f'<PackageReference Include="{reference}" />')

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert out.startswith("⚠️  A is pinned in Directory.Packages.props but no project references it\n")
    assert f"Reported as warnings: a reference this check can't read without evaluating MSBuild ({reference})" in out


def test_a_pin_that_cant_be_read_is_left_alone(tmp_path):
    central(tmp_path, '<PackageVersion Include="$(Unset).Core" Version="1.0.0" /><PackageVersion Include="@(X)" Version="1.0.0" />')

    assert unreferenced(tmp_path) == []


def test_the_caps_are_the_documented_ones():
    # The boundary tests below are written against the constants, so these pin their values.
    assert (ccp.MAX_BYTES, ccp.MAX_EXPANSIONS, ccp.MAX_DEPTH, ccp.MAX_VALUE) == (4 * 1024 * 1024, 256, 256, 1024)
    assert (ccp.MAX_TOTAL_BYTES, ccp.MAX_NODES, ccp.MAX_TOTAL_EXPANSIONS, ccp.MAX_SHOWN) == (64 * 1024 * 1024, 250_000, 100_000, 100)
    assert (ccp.MAX_ID, ccp.MAX_IDS, ccp.MAX_FILES, ccp.MAX_PRINTED) == (256, 250_000, 50_000, 300)
    assert (ccp.MAX_GLOBAL_JSON_BYTES, ccp.MAX_GLOBAL_JSON_FILES) == (64 * 1024, 100)


def test_an_older_python_is_refused_with_a_message():
    import subprocess

    path = str(Path(ccp.__file__))
    code = f"import sys; sys.version_info = (3, 10, 0); sys.version = '3.10.0 (fake)'; exec(compile(open({path!r}).read(), {path!r}, 'exec'))"
    result = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True)

    assert result.returncode == 1
    assert result.stderr == "check_central_pins.py needs Python 3.11 or later; this is 3.10.0.\n"


def expansions(tmp_path, count):
    """A reference through one property the repo gives count values."""
    values = "".join(f'<PropertyGroup Condition="{i}"><First>p{i}</First></PropertyGroup>' for i in range(count))
    (tmp_path / "Directory.Build.props").write_text(f"<Project>{values}</Project>\n")
    central(tmp_path, '<PackageVersion Include="Orphan" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(First)" />')


@pytest.mark.parametrize("count,readable", [(ccp.MAX_EXPANSIONS - 1, True), (ccp.MAX_EXPANSIONS, True), (ccp.MAX_EXPANSIONS + 1, False)])
def test_a_reference_through_more_than_max_expansions_combinations_cant_be_read(tmp_path, count, readable):
    expansions(tmp_path, count)

    assert (ccp.check(tmp_path)[2] == []) is readable


def test_combinations_of_several_properties_multiply(tmp_path):
    # 129 * 2 = 258 combinations, past the cap, though neither property has more than 129 values.
    values = "".join(f'<PropertyGroup Condition="{i}"><First>p{i}</First></PropertyGroup>' for i in range(129))
    (tmp_path / "Directory.Build.props").write_text(f'<Project>{values}<PropertyGroup><Second>x</Second></PropertyGroup>'
                                                    '<PropertyGroup Condition="z"><Second>y</Second></PropertyGroup></Project>\n')
    central(tmp_path, '<PackageVersion Include="Orphan" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(First).$(Second)" />')

    assert ccp.check(tmp_path)[2] == ["$(First).$(Second)"]


@pytest.mark.parametrize("extra,readable", [(-1, True), (0, True), (1, False)])
def test_a_property_value_over_max_value_characters_cant_be_read(tmp_path, extra, readable):
    # A list of one id padded with separators, so only the value's own length is at the cap.
    value = "A" + ";" * (ccp.MAX_VALUE + extra - 1)
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + f"<PropertyGroup><P>{value}</P></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P)" />')

    _, found, unreadable, _, _ = ccp.check(tmp_path)

    assert (unreadable == [] and found == []) is readable


@pytest.mark.parametrize("extra,readable", [(-1, True), (0, True), (1, False)])
def test_a_name_that_would_grow_past_max_value_characters_isnt_built(tmp_path, extra, readable):
    # $(P)$(Q) substituted is 512 + 511, 512 or 513 characters: one id each, padded with separators.
    first = "A" + ";" * (ccp.MAX_VALUE // 2 - 1)
    second = "B" + ";" * (ccp.MAX_VALUE // 2 - 1 + extra)
    groups = f"<PropertyGroup><P>{first}</P><Q>{second}</Q></PropertyGroup>"
    central(tmp_path, '<PackageVersion Include="AB" Version="1.0.0" />', properties=TRANSITIVE + groups)
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P)$(Q)" />')

    assert len(first) + len(second) == ccp.MAX_VALUE + extra
    assert (ccp.check(tmp_path)[2] == []) is readable


def test_the_length_bound_takes_each_propertys_longest_value(tmp_path):
    # P is "A" or 512 characters: $(P)$(P)x can be 1,025, though its shortest is 3.
    long = "A" + ";" * (ccp.MAX_VALUE // 2 - 1)
    groups = f'<PropertyGroup><P>A</P></PropertyGroup><PropertyGroup Condition="c"><P>{long}</P></PropertyGroup>'
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + groups)
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P)$(P)x" /><PackageReference Include="$(P)" />')

    assert ccp.check(tmp_path)[2] == ["$(P)$(P)x"]


@pytest.mark.parametrize("extra,readable", [(-1, True), (0, True), (1, False)])
def test_an_id_over_max_id_characters_cant_be_read(tmp_path, extra, readable):
    name = "A" * (ccp.MAX_ID + extra)
    central(tmp_path, '<PackageVersion Include="B" Version="1.0.0" />', properties=TRANSITIVE + f"<PropertyGroup><P>{name}</P></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", f'<PackageReference Include="{name}" /><PackageReference Include="$(P)" />')

    unreadable = ccp.check(tmp_path)[2]

    assert unreadable == ([] if readable else [name, "$(P)"])


def test_values_are_counted_before_they_are_sorted(tmp_path, monkeypatch):
    # A name past the cap is refused without sorting the values of each of its properties.
    values = "".join(f'<PropertyGroup Condition="{i}"><P>v{i}</P><Q>w{i}</Q></PropertyGroup>' for i in range(20))
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + values)
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P).$(Q)" />')

    def spy(items, *args, **kwargs):
        items = list(items)
        assert not any(str(item).startswith(("v", "w")) for item in items), "the values were sorted before they were counted"
        return sorted(items, *args, **kwargs)

    monkeypatch.setattr(ccp, "sorted", spy, raising=False)

    assert ccp.check(tmp_path)[2] == ["$(P).$(Q)"]


@pytest.mark.parametrize("group", [
    "<PropertyGroup><P>\n  Acme  \n</P></PropertyGroup>",
    "<PropertyGroup><P>Ac&#109;e</P></PropertyGroup>",
    "<PropertyGroup><P>Ac<!-- why -->me</P></PropertyGroup>",
    '<Target Name="T"><PropertyGroup><P>Acme</P></PropertyGroup></Target>',
    '<Choose><When Condition="x"><PropertyGroup><P>Acme</P></PropertyGroup></When></Choose>',
])
def test_a_property_is_read_wherever_and_however_it_is_set(tmp_path, group):
    central(tmp_path, '<PackageVersion Include="Acme.Core" Version="1.0.0" />', properties=TRANSITIVE + group)
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P).Core" />')

    assert ccp.check(tmp_path)[1:3] == ([], [])


def test_a_property_holding_a_list_resolves_to_each_id(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1" /><PackageVersion Include="B" Version="1" /><PackageVersion Include="C" Version="1" />',
            properties=TRANSITIVE + "<PropertyGroup><Pkgs>A; B</Pkgs></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(Pkgs)" />')

    assert unreferenced(tmp_path) == ["C"]


def test_a_value_split_across_many_chunks_is_read_whole(tmp_path):
    # Longer than expat's text buffer, so it arrives in more than one piece.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />',
            properties=f"<PropertyGroup><CentralPackageTransitivePinningEnabled>{' ' * 20000}true{chr(10) * 20000}</CentralPackageTransitivePinningEnabled></PropertyGroup>")

    assert ccp.check(tmp_path)[0] is True


def test_a_pin_resolving_to_several_ids_reports_each_it_finds_unreferenced_in_order(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="$(Prefix).Core" Version="1.0.0" />',
            properties=TRANSITIVE + "".join(f'<PropertyGroup Condition="{v}"><Prefix>{v}</Prefix></PropertyGroup>' for v in ("Z", "X", "Y")))
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="X.Core" />')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert out == ("❌ y.core is pinned in Directory.Packages.props but no project references it\n"
                   "❌ z.core is pinned in Directory.Packages.props but no project references it\n")


def test_the_warning_line_lists_each_unreadable_reference_once_in_order(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/B/B.csproj", '<PackageReference Include="@(Z)" /><PackageReference Include="$(Unset)" />')
    project(tmp_path, "src/A/A.csproj", '<PackageReference Include="@(Z)" />')

    out = run(tmp_path, capsys)[1]

    assert out.splitlines()[-1] == ("Reported as warnings: a reference this check can't read without evaluating MSBuild ($(Unset), @(Z)) "
                                    "may name any of the pins above.")


def test_an_unreadable_reference_cant_start_a_workflow_command(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(X&#10;::add-mask::y&#13;::stop-commands::t)" />')

    out = run_ci(tmp_path, capsys, monkeypatch)

    assert [line for line in out.splitlines() if line.startswith("::")] == [
        "::warning file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it"]
    assert "($(X?::add-mask::y?::stop-commands::t))" in out


def test_the_report_comes_in_order(tmp_path, capsys):
    for folder in ("b", "a"):
        central(tmp_path, '<PackageVersion Include="Q" Version="1" /><PackageVersion Include="P" Version="1" />', folder=folder)
    (tmp_path / "z.props").write_text("<Project>")
    (tmp_path / "y.props").write_text("<Project>")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    lines = out.splitlines(keepends=True)
    # expat's own wording of the error may change between releases, so only up to it.
    assert [line[:len("ℹ️  y.props was skipped: it isn't well-formed MSBuild XML (")] for line in lines[:2]] == [
        "ℹ️  y.props was skipped: it isn't well-formed MSBuild XML (", "ℹ️  z.props was skipped: it isn't well-formed MSBuild XML ("]
    assert "".join(lines[2:]) == ("❌ Q is pinned in a/Directory.Packages.props but no project references it\n"
                   "❌ P is pinned in a/Directory.Packages.props but no project references it\n"
                   "❌ Q is pinned in b/Directory.Packages.props but no project references it\n"
                   "❌ P is pinned in b/Directory.Packages.props but no project references it\n")


def test_each_kind_of_line_is_printed_at_most_max_shown_times(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(ccp, "MAX_SHOWN", 2)
    central(tmp_path, "".join(f'<PackageVersion Include="P{i}" Version="1" />' for i in range(3)))
    project(tmp_path, "src/App/App.csproj", "".join(f'<PackageReference Include="@(R{i})" />' for i in range(3)))

    out = run(tmp_path, capsys)[1].splitlines()

    assert out[:3] == ["⚠️  P0 is pinned in Directory.Packages.props but no project references it",
                       "⚠️  P1 is pinned in Directory.Packages.props but no project references it",
                       "... and 1 more like the 2 above."]
    assert "(@(R0), @(R1) and 1 more)" in out[3]


def test_max_shown_lines_are_all_printed_without_a_more_line(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(ccp, "MAX_SHOWN", 2)
    central(tmp_path, "".join(f'<PackageVersion Include="P{i}" Version="1" />' for i in range(2)))

    out = run(tmp_path, capsys)[1]

    assert out.count("is pinned") == 2 and "more like" not in out


# The run's budget.


@pytest.mark.parametrize("cap,kind", [("MAX_FILES", "files"), ("MAX_NODES", "elements, attributes and comments"),
                                      ("MAX_TOTAL_BYTES", "bytes"), ("MAX_IDS", "ids")])
def test_a_repo_bigger_than_the_runs_budget_fails(tmp_path, capsys, monkeypatch, cap, kind):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" /><!-- Transitive pin: kept --><PackageVersion Include="K" Version="1" />')
    (tmp_path / "global.json").write_text('{"msbuild-sdks": {"G": "1"}}')
    (tmp_path / "src/App/bin").mkdir(parents=True)
    (tmp_path / "src/App/App.csproj").write_text('<Project Sdk="S"><!-- a --><!-- b --><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')
    # Neither an MSBuild file nor a global.json, or under bin/: not counted.
    for name in ("README.md", "src/App/Program.cs", "src/App/bin/Copy.csproj"):
        (tmp_path / name).write_text("x")
    # 3 files; 11 elements, attributes and comments in the central file, 7 in the project with its 2 comments; the three files'
    # bytes; 5 ids (the two pins, the SDK, the reference and global.json's SDK).
    used = {"MAX_FILES": 3, "MAX_NODES": 18, "MAX_IDS": 5,
            "MAX_TOTAL_BYTES": sum(path.stat().st_size for path in (tmp_path / "Directory.Packages.props", tmp_path / "src/App/App.csproj",
                                                                    tmp_path / "global.json"))}[cap]
    assert ccp.check(tmp_path)[1] == []

    monkeypatch.setattr(ccp, cap, used)
    assert run(tmp_path, capsys)[0] == 0
    monkeypatch.setattr(ccp, cap, used - 1)
    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert out == f"❌ Not checked: the repo needs more than the {used - 1} {kind} this check reads.\n"


@pytest.mark.parametrize("include,used", [("A;;;", 6), ("$(P)", 6)])
def test_every_list_entry_and_every_resolved_id_counts_against_the_ids_budget(tmp_path, capsys, monkeypatch, include, used):
    # The pin, the SDK and A;;; (four entries, three blank), or the pin, the SDK, $(P) and the three ids it resolves to.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + "<PropertyGroup><P>A;B;C</P></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", f'<PackageReference Include="{include}" />')

    monkeypatch.setattr(ccp, "MAX_IDS", used)
    assert run(tmp_path, capsys)[0] == 0
    monkeypatch.setattr(ccp, "MAX_IDS", used - 1)

    assert "needs more than the" in run(tmp_path, capsys)[1]


@pytest.mark.parametrize("value,cost", [(None, 0), ("", 0), (";", 2), (" ; A", 2)])
def test_an_absent_or_empty_value_counts_no_ids_and_every_entry_counts_one(value, cost):
    budget = ccp.Budget()

    list(ccp.ids(value, budget))

    assert budget.left["ids"] == ccp.MAX_IDS - cost


def test_a_global_jsons_sdks_count_as_ids_and_a_long_one_is_left_out():
    budget = ccp.Budget()
    data = ('{"msbuild-sdks": {"' + "a" * (ccp.MAX_ID + 1) + '": "1", "' + "b" * ccp.MAX_ID + '": "1"}}').encode()

    assert ccp.global_json_sdks(data, budget) == {"b" * ccp.MAX_ID}
    assert budget.left["ids"] == ccp.MAX_IDS - 2


def test_combinations_count_against_the_runs_budget_once_per_name(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(ccp, "MAX_TOTAL_EXPANSIONS", 2)
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties=TRANSITIVE + "<PropertyGroup><P>A</P></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(P)" />' * 5 + '<PackageReference Include="$(P).x" />')
    assert run(tmp_path, capsys)[0] == 0

    project(tmp_path, "src/Other/Other.csproj", '<PackageReference Include="$(P).y" />')
    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "needs more than the 2 combinations of property values" in out


# Files this check won't parse.


@pytest.mark.parametrize("depth,read", [(ccp.MAX_DEPTH - 1, True), (ccp.MAX_DEPTH, True), (ccp.MAX_DEPTH + 1, False)])
def test_a_file_nested_over_max_depth_is_skipped(tmp_path, capsys, depth, read):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    inner = '<ItemGroup><PackageReference Include="A" /></ItemGroup>'
    (tmp_path / "Deep.props").write_text("<Project>" + "<a>" * (depth - 3) + inner + "</a>" * (depth - 3) + "</Project>")

    status, out, _ = run(tmp_path, capsys)

    assert status == (0 if read else 1)
    assert (f"Deep.props was skipped: it isn't well-formed MSBuild XML (an element nested over {ccp.MAX_DEPTH} deep)" in out) is not read


def test_a_file_nested_far_deeper_is_skipped_not_a_crash(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "Deep.props").write_text("<Project>" + "<a>" * 50000 + "</a>" * 50000 + "</Project>")
    # Far past the JSON parser's recursion limit, and under MAX_GLOBAL_JSON_BYTES.
    (tmp_path / "global.json").write_text("[" * 30000 + "]" * 30000)

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert "Deep.props was skipped" in out and "global.json was skipped: it isn't JSON the .NET host reads" in out


@pytest.mark.parametrize("encoding", ["gb2312", "hex", "no-such-encoding"])
def test_a_file_in_an_encoding_python_cant_decode_is_skipped(tmp_path, capsys, encoding):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "Odd.props").write_text(f'<?xml version="1.0" encoding="{encoding}"?><Project />')

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert "ℹ️  Odd.props was skipped: it isn't well-formed MSBuild XML (" in out


def test_a_large_text_is_read_in_linear_time(tmp_path):
    # Adding each chunk of text to a string took minutes on a file this size.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    (tmp_path / "Text.props").write_text("<Project><PropertyGroup><P>" + "a\n&amp;" * ((ccp.MAX_BYTES - 100) // 7) + "</P></PropertyGroup></Project>")

    assert (tmp_path / "Text.props").stat().st_size < ccp.MAX_BYTES
    started = time.monotonic()
    problems = ccp.check(tmp_path)[3]

    assert time.monotonic() - started < 10
    assert problems == []


# global.json.


@pytest.mark.parametrize("text", [
    '{"msbuild-sdks": {"Json.Sdk": "1.0.0"}}',
    '﻿{"msbuild-sdks": {"Json.Sdk": "1.0.0"}}',
    '{\n  // the SDKs\n  "msbuild-sdks": { "Json.Sdk": "1.0.0", }, /* end */\n}',
    '{"msbuild-sdks": {"Json.Sdk": "1.0.0" // why\n,}}',
    '{"sdk": {"version": "a // not a comment, }"}, "msbuild-sdks": {"Json.Sdk": "1"}}',
    '{"sdk": {"version": "a \\" /* not a comment */"}, "msbuild-sdks": {"Json.Sdk": "1"}}',
    '{"msbuild-sdks": {"Json.Sdk": "1"}, "v": "q\\" // "}',
])
def test_global_json_msbuild_sdks_count_in_any_form_the_host_reads(tmp_path, capsys, text):
    central(tmp_path, '<PackageVersion Include="Json.Sdk" Version="1.0.0" />')
    (tmp_path / "global.json").write_bytes(text.encode("utf-8"))

    assert run(tmp_path, capsys)[:2] == (0, "Every central pin is referenced by a project, or kept with a reason.\n")


def test_a_nested_global_json_counts(tmp_path):
    central(tmp_path, '<PackageVersion Include="Json.Sdk" Version="1.0.0" />')
    (tmp_path / "src").mkdir()
    (tmp_path / "src/global.json").write_text('{"msbuild-sdks": {"Json.Sdk": "1.0.0"}}')

    assert unreferenced(tmp_path) == []


@pytest.mark.parametrize("text", ["[]", '{"msbuild-sdks": ["Json.Sdk"]}', '{"msbuild-sdks": null}', '"x"'])
def test_a_global_json_without_an_msbuild_sdks_object_names_none(tmp_path, capsys, text):
    central(tmp_path, '<PackageVersion Include="Json.Sdk" Version="1.0.0" />')
    (tmp_path / "global.json").write_text(text)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "skipped" not in out


@pytest.mark.parametrize("data", [b"{not json", b'{"msbuild-sdks": {"Json.Sdk": "1"}', b"\xff\xfe{", b"/* open"])
def test_a_global_json_the_host_cant_read_is_skipped_with_a_notice(tmp_path, capsys, data):
    central(tmp_path, '<PackageVersion Include="Json.Sdk" Version="1.0.0" />')
    (tmp_path / "global.json").write_bytes(data)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "ℹ️  global.json was skipped: it isn't JSON the .NET host reads. A reference or property in it wasn't seen.\n" in out


# Files that aren't read.


def test_folders_restore_and_build_write_are_skipped(tmp_path):
    # CI caches the NuGet packages under the workspace's .nuget/, whose packages carry .props files of their own.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    for folder in (".nuget/packages/x/build", "src/App/obj", "src/App/bin", "node_modules/y", ".git/z"):
        (tmp_path / folder).mkdir(parents=True)
        (tmp_path / folder / "x.props").write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')
        (tmp_path / folder / "global.json").write_text('{"msbuild-sdks": {"A": "1"}}')

    assert unreferenced(tmp_path) == ["A"]


@pytest.mark.parametrize("data", [b"", b"not xml", b"<Project><ItemGroup></Project>", b"<Project>$(Name)</Project><Extra/>"])
def test_a_malformed_file_is_skipped_with_a_notice_when_pinning_is_off(tmp_path, capsys, data):
    # A dotnet new template or a test fixture that restore never reads.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', properties="")
    (tmp_path / "templates").mkdir()
    (tmp_path / "templates/Fixture.csproj").write_bytes(data)

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert out.startswith("ℹ️  templates/Fixture.csproj was skipped: it isn't well-formed MSBuild XML (")


def test_a_malformed_file_only_makes_the_check_stricter(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    (tmp_path / "Broken.csproj").write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup>')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Broken.csproj was skipped" in out and "A is pinned in Directory.Packages.props" in out


def test_a_doctype_or_entity_declaration_is_refused(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    (tmp_path / "Bomb.csproj").write_text('<?xml version="1.0"?>\n<!DOCTYPE p [<!ENTITY a "AAAA"><!ENTITY b "&a;&a;&a;&a;">]>\n'
                                          '<Project><ItemGroup><PackageReference Include="&b;" /></ItemGroup></Project>\n')
    (tmp_path / "Entity.props").write_text('<!DOCTYPE Project>\n<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Bomb.csproj was skipped: it isn't well-formed MSBuild XML (a DOCTYPE or entity declaration" in out
    assert "Entity.props was skipped: it isn't well-formed MSBuild XML (a DOCTYPE or entity declaration" in out


@pytest.mark.skipif(not hasattr(os, "symlink"), reason="needs symlinks")
def test_a_symlinked_or_dangling_file_is_not_followed(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    outside = tmp_path.parent / f"{tmp_path.name}-outside.props"
    outside.write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')
    (tmp_path / "Linked.props").symlink_to(outside)
    (tmp_path / "Dangling.props").symlink_to(tmp_path / "missing.props")
    (tmp_path / "global.json").symlink_to(outside)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    for name in ("Linked.props", "Dangling.props"):
        assert f"❌ {name} can't be checked: it is a symlink. It may hold central pins, turn transitive pinning on or set a property a pin is built from.\n" in out
    assert "ℹ️  global.json was skipped: it is a symlink." in out


@pytest.mark.skipif(not hasattr(os, "mkfifo"), reason="needs FIFOs")
def test_a_fifo_is_not_read(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    os.mkfifo(tmp_path / "Pipe.props")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Pipe.props can't be checked: it is not a regular file." in out


@pytest.mark.parametrize("extra,read", [(-1, True), (0, True), (1, False)])
def test_a_file_over_max_bytes_is_not_read(tmp_path, capsys, extra, read):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    body = '<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>'
    padding = ccp.MAX_BYTES + extra - len(body) - len("<!--") - len("-->")
    (tmp_path / "Big.props").write_text(f"<!--{'x' * padding}-->{body}")
    assert (tmp_path / "Big.props").stat().st_size == ccp.MAX_BYTES + extra

    status, out, _ = run(tmp_path, capsys)

    assert status == (0 if read else 1)
    assert (f"Big.props can't be checked: it is over {ccp.MAX_BYTES} bytes." in out) is not read


def test_a_file_that_grew_past_max_bytes_since_lstat_is_not_read(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    body = '<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>'
    (tmp_path / "Grown.props").write_text(f"<!--{'x' * ccp.MAX_BYTES}-->{body}")
    real = os.lstat

    def small(path, *args, **kwargs):
        info = real(path, *args, **kwargs)
        if os.fspath(path) == str(tmp_path / "Grown.props"):
            return os.stat_result((info.st_mode, *info[1:6], 10, *info[7:]))
        return info

    monkeypatch.setattr(ccp.os, "lstat", small)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert f"Grown.props can't be checked: it is over {ccp.MAX_BYTES} bytes." in out


def test_a_global_json_that_grew_past_its_cap_since_lstat_is_skipped(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "global.json").write_text("{}" + " " * ccp.MAX_GLOBAL_JSON_BYTES)
    real = os.lstat

    def small(path, *args, **kwargs):
        info = real(path, *args, **kwargs)
        if os.fspath(path) == str(tmp_path / "global.json"):
            return os.stat_result((info.st_mode, *info[1:6], 10, *info[7:]))
        return info

    monkeypatch.setattr(ccp.os, "lstat", small)

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert f"ℹ️  global.json was skipped: it is over {ccp.MAX_GLOBAL_JSON_BYTES} bytes." in out


def test_an_unreadable_file_cant_be_checked_and_says_why(tmp_path, capsys, monkeypatch):
    # Patched rather than chmod 0, which root (a container, a sandbox) reads anyway.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "Locked.csproj", '<PackageReference Include="A" />')
    real = os.open

    def locked(path, *args, **kwargs):
        if os.fspath(path) == str(tmp_path / "Locked.csproj"):
            raise PermissionError(errno.EACCES, "Permission denied", str(path))
        return real(path, *args, **kwargs)

    monkeypatch.setattr(ccp.os, "open", locked)

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Locked.csproj can't be checked: it can't be read (Permission denied). It may hold" in out
    assert "must be readable for the check to pass" in err


@pytest.mark.skipif(not hasattr(os, "mkfifo"), reason="needs FIFOs")
def test_a_path_that_became_a_fifo_after_lstat_is_not_read(tmp_path, capsys, monkeypatch):
    # Opened without waiting for a writer, then refused on what the open file is.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    os.mkfifo(tmp_path / "Swapped.props")
    regular = os.lstat(tmp_path / "Directory.Packages.props")
    real = os.lstat
    monkeypatch.setattr(ccp.os, "lstat", lambda path, *a, **k: regular if os.fspath(path) == str(tmp_path / "Swapped.props") else real(path, *a, **k))

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Swapped.props can't be checked: it is not a regular file." in out


@pytest.mark.skipif(not hasattr(os, "O_NOFOLLOW"), reason="needs O_NOFOLLOW")
def test_a_path_that_became_a_symlink_after_lstat_is_not_followed(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    outside = tmp_path.parent / f"{tmp_path.name}-outside.props"
    outside.write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')
    (tmp_path / "Swapped.props").symlink_to(outside)
    regular = os.lstat(outside)
    real = os.lstat
    monkeypatch.setattr(ccp.os, "lstat", lambda path, *a, **k: regular if os.fspath(path) == str(tmp_path / "Swapped.props") else real(path, *a, **k))

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Swapped.props can't be checked: it can't be read (" in out


# What a file that can't be read may hide.


def test_a_central_file_that_cant_be_read_fails_the_check(tmp_path, capsys):
    # Even one cut off before its first pin: by its name, it is the file that holds them.
    (tmp_path / "Directory.Packages.props").write_text("<Project><ItemGroup>")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Directory.Packages.props can't be checked: it isn't well-formed MSBuild XML (" in out


@pytest.mark.skipif(not hasattr(os, "symlink"), reason="needs symlinks")
def test_a_symlinked_central_file_fails_the_check(tmp_path, capsys):
    real = central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="shared")
    (tmp_path / "src").mkdir()
    (tmp_path / "src/Directory.Packages.props").symlink_to(real)
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ src/Directory.Packages.props can't be checked: it is a symlink." in out


@pytest.mark.parametrize("encoding", ["utf-8", "utf-16"])
@pytest.mark.parametrize("body", ["<CentralPackageTransitivePinningEnabled>true", '<ItemGroup><PackageVersion Include="A" Version="1" />'])
def test_a_malformed_file_that_names_pins_or_the_setting_fails_the_check(tmp_path, capsys, body, encoding):
    # Skipping it could hide the setting, so the check would say pinning is off; or a pin, which it would never check.
    central(tmp_path, '<PackageVersion Include="B" Version="1.0.0" />', properties="")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="B" />')
    (tmp_path / "Directory.Build.props").write_bytes(f"<Project><PropertyGroup>{body}".encode(encoding))

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (" in out


def test_the_check_needs_a_root_folder(tmp_path, capsys):
    with pytest.raises(SystemExit) as raised:
        ccp.main(["--root", str(tmp_path / "typo")])

    assert raised.value.code == 2
    assert "is not a folder" in capsys.readouterr().err


def test_a_folder_that_cant_be_listed_fails_the_check(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "locked").mkdir()
    real = os.scandir

    def scandir(path=".", *args, **kwargs):
        if os.fspath(path) == str(tmp_path / "locked"):
            raise PermissionError(errno.EACCES, "Permission denied", str(path))
        return real(path, *args, **kwargs)

    monkeypatch.setattr(ccp.os, "scandir", scandir)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ locked/ can't be checked: it can't be listed (Permission denied)." in out


def test_a_root_that_cant_be_listed_fails_the_check(tmp_path, capsys, monkeypatch):
    real = os.scandir

    def scandir(path=".", *args, **kwargs):
        if os.fspath(path) == str(tmp_path):
            raise PermissionError(errno.EACCES, "Permission denied", str(path))
        return real(path, *args, **kwargs)

    monkeypatch.setattr(ccp.os, "scandir", scandir)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert out.startswith("❌ . can't be checked: it can't be listed (Permission denied).")


@pytest.mark.skipif(not hasattr(os, "symlink"), reason="needs symlinks")
def test_a_symlinked_folder_fails_the_check(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "real/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "linked").symlink_to(tmp_path / "real")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ linked/ can't be checked: it is a symlinked folder, which isn't walked." in out


# What a file that can't be checked does to the rest of the report.


def test_a_file_that_cant_be_checked_fails_even_with_warnings(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="@(Z)" />')
    (tmp_path / "Directory.Build.props").write_text('<Project><ItemGroup><PackageVersion Include="B"')

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (" in out
    assert "⚠️  A is pinned in Directory.Packages.props but no project references it\n" in out
    assert out.splitlines()[-1].startswith("Not a pass: ")
    assert err.count("must be readable for the check to pass") == 1


def test_a_file_that_cant_be_checked_ends_the_report_after_the_pins(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    (tmp_path / "Broken.props").write_text("<Project><PackageVersion")

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert out.splitlines()[-2:] == ["❌ A is pinned in Directory.Packages.props but no project references it",
                                     "Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for."]
    assert "Delete each pin above" in err and "must be readable for the check to pass" in err


@pytest.mark.parametrize("properties,items", [("", '<PackageVersion Include="A" Version="1.0.0" />'), (TRANSITIVE, "")])
def test_a_file_that_cant_be_checked_is_never_reported_as_a_pass(tmp_path, capsys, properties, items):
    central(tmp_path, items, properties=properties)
    (tmp_path / "Broken.props").write_text("<Project><PackageVersion")

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert out.splitlines()[-1] == "Not a pass: the files and folders above couldn't be checked, and may hold what the check looks for."
    assert "Transitive pinning is off" not in out and "Every central pin is referenced" not in out
    assert f"nested at most {ccp.MAX_DEPTH} deep, in an encoding Python decodes" in err


def test_a_skipped_file_fails_the_check_when_a_pin_cant_be_resolved(tmp_path, capsys):
    # The pin is built from $(P), which only the broken file may set.
    central(tmp_path, '<PackageVersion Include="$(P)" Version="1.0.0" />')
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><P>A</P>")
    (tmp_path / "global.json").write_text("{")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Directory.Build.props can't be checked: it isn't well-formed MSBuild XML (" in out
    assert "ℹ️  global.json was skipped: it isn't JSON the .NET host reads." in out


def test_a_skipped_file_fails_the_check_when_a_pin_is_built_from_a_property(tmp_path, capsys):
    # $(P) resolves to A from a file read, but the broken file may give it another value, and so another pin.
    central(tmp_path, '<PackageVersion Include="$(P)" Version="1.0.0" />', properties=TRANSITIVE + "<PropertyGroup><P>A</P></PropertyGroup>")
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "Directory.Build.props").write_text('<Project><PropertyGroup Condition="x"><P>B</P>')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ Directory.Build.props can't be checked" in out


@pytest.mark.parametrize("pin", ["@(Items)", "%(Meta)", "$(", "$(P)@(Items)", "$(P)$(1)"])
def test_a_skipped_file_stays_a_notice_for_a_pin_no_file_can_make_readable(tmp_path, capsys, pin):
    # No value of P makes these readable ($(1) isn't a property name), so the broken file hides nothing about them.
    central(tmp_path, f'<PackageVersion Include="{pin}" Version="1.0.0" />')
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><P>A</P>")

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert "ℹ️  Directory.Build.props was skipped" in out


@pytest.mark.parametrize("properties,items", [
    ("", '<PackageVersion Include="$(Unset)" Version="1.0.0" />'),
    (TRANSITIVE, '<!-- Transitive pin: kept --><PackageVersion Include="$(Unset)" Version="1.0.0" />'),
])
def test_a_skipped_file_stays_a_notice_without_transitive_pinning_or_for_a_kept_pin(tmp_path, capsys, properties, items):
    central(tmp_path, items, properties=properties)
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><Unset>A</Unset>")

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert "ℹ️  Directory.Build.props was skipped" in out


def test_a_skipped_file_stays_a_notice_when_every_pin_resolves(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "Directory.Build.props").write_text("<Project><PropertyGroup><P>A</P>")

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert "ℹ️  Directory.Build.props was skipped" in out


@pytest.mark.parametrize("kind,setup", [("notice", "malformed"), ("error", "fifo")])
def test_notices_and_errors_are_printed_at_most_max_shown_times(tmp_path, capsys, monkeypatch, kind, setup):
    if setup == "fifo" and not hasattr(os, "mkfifo"):
        pytest.skip("needs FIFOs")
    monkeypatch.setattr(ccp, "MAX_SHOWN", 2)
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    for i in range(3):
        if setup == "fifo":
            os.mkfifo(tmp_path / f"P{i}.props")
        else:
            (tmp_path / f"P{i}.props").write_text("<Project>")

    status, out, err = run(tmp_path, capsys)

    marker = {"notice": "ℹ️  P", "error": "❌ P"}[kind]
    assert [line[:len(marker) + 1] for line in out.splitlines()[:3]] == [marker + "0", marker + "1", "... and 1 m"[:len(marker) + 1]]
    assert out.splitlines()[2] == "... and 1 more like the 2 above."
    assert status == (1 if kind == "error" else 0)
    assert err.count("must be readable") == (1 if kind == "error" else 0)


def test_a_long_path_is_cut_in_the_annotations_file_property(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(ccp, "MAX_PRINTED", 10)
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="F" * 20)

    out = run_ci(tmp_path, capsys, monkeypatch)

    # FFFF.../Directory.Packages.props is 45 characters: 10 kept, 35 cut.
    assert out == "::error file=FFFFFFFFFF... (35 more)::A is pinned in FFFFFFFFFF... (35 more) but no project references it\n"


def test_a_long_name_or_path_is_cut_where_it_is_printed(tmp_path, capsys, monkeypatch):
    # A pin's name can't be longer than MAX_ID, which is under MAX_PRINTED, so a short cap shows the cut on it too.
    name = "N" * (ccp.MAX_PRINTED + 50)
    central(tmp_path, '<PackageVersion Include="AAAAAAAAAAAAAAA" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", f'<PackageReference Include="@({name})" />')

    out = run(tmp_path, capsys)[1]
    monkeypatch.setattr(ccp, "MAX_PRINTED", 10)
    short = run(tmp_path, capsys)[1]

    # "@(", the name and ")" are 353 characters: 300 kept, 53 cut.
    assert f"(@({'N' * 298}... (53 more))" in out
    assert short.startswith("⚠️  AAAAAAAAAA... (5 more) is pinned in Directory.... (14 more) but no project references it\n")


# The open file's descriptor.


def descriptors(monkeypatch):
    opened, closed = [], []
    real_open, real_close = os.open, os.close

    def spy_open(*args, **kwargs):
        opened.append(real_open(*args, **kwargs))
        return opened[-1]

    def spy_close(descriptor):
        closed.append(descriptor)
        real_close(descriptor)

    monkeypatch.setattr(ccp.os, "open", spy_open)
    monkeypatch.setattr(ccp.os, "close", spy_close)
    return opened, closed


@pytest.mark.skipif(not hasattr(os, "mkfifo"), reason="needs FIFOs")
def test_a_descriptor_refused_after_fstat_is_closed(tmp_path, capsys, monkeypatch):
    os.mkfifo(tmp_path / "Swapped.props")
    regular = os.lstat(tmp_path)
    real = os.lstat
    swapped = str(tmp_path / "Swapped.props")
    monkeypatch.setattr(ccp.os, "lstat", lambda path, *a, **k: (
        os.stat_result((0o100644, *regular[1:])) if os.fspath(path) == swapped else real(path, *a, **k)))
    opened, closed = descriptors(monkeypatch)

    run(tmp_path, capsys)

    assert opened and closed == opened


def test_a_descriptor_whose_fstat_fails_is_closed(tmp_path, capsys, monkeypatch):
    project(tmp_path, "App.csproj", '<PackageReference Include="A" />')
    opened, closed = descriptors(monkeypatch)
    real = os.fstat

    def failing(descriptor, *args, **kwargs):
        if descriptor in opened:
            raise OSError(errno.EIO, "Input/output error")
        return real(descriptor, *args, **kwargs)

    monkeypatch.setattr(ccp.os, "fstat", failing)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "❌ App.csproj can't be checked: it can't be read (Input/output error)." in out
    assert opened and closed == opened


def test_a_descriptor_read_is_closed_once(tmp_path, capsys, monkeypatch):
    project(tmp_path, "App.csproj", '<PackageReference Include="A" />')
    opened, closed = descriptors(monkeypatch)

    assert run(tmp_path, capsys)[0] == 0
    assert len(opened) == 1 and closed == []


# Output under GitHub Actions, and text a repo controls.


def test_ci_gets_an_error_annotation_naming_the_file(tmp_path, capsys, monkeypatch):
    monkeypatch.setenv("GITHUB_ACTIONS", "true")
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')

    status, out, err = run(tmp_path, capsys)

    assert status == 1
    assert out == "::error file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it\n"
    assert "<!-- Transitive pin: <the reason> -->" in err


@pytest.mark.parametrize("value", ["false", "1", "TRUE", ""])
def test_github_actions_other_than_true_gets_plain_lines(tmp_path, capsys, monkeypatch, value):
    monkeypatch.setenv("GITHUB_ACTIONS", value)
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')

    assert run(tmp_path, capsys)[1].startswith("❌ A is pinned")


def test_ci_gets_warning_and_notice_annotations(tmp_path, capsys, monkeypatch):
    monkeypatch.setenv("GITHUB_ACTIONS", "true")
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="@(Packages)" />')
    (tmp_path / "Broken.props").write_text("<Project>")

    out = run(tmp_path, capsys)[1]

    assert "::notice file=Broken.props::Broken.props was skipped: it isn't well-formed MSBuild XML (" in out
    assert "::warning file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it\n" in out


def run_ci(tmp_path, capsys, monkeypatch):
    monkeypatch.setenv("GITHUB_ACTIONS", "true")
    return run(tmp_path, capsys)[1]


# A character reference keeps its line break through XML attribute normalisation.
HOSTILE = 'A&#10;::add-mask::x&#13;::stop-commands::t%0A&#x9b;2J'  # U+009B is the one-character CSI


def test_an_id_cant_start_a_workflow_command_in_ci(tmp_path, capsys, monkeypatch):
    central(tmp_path, f'<PackageVersion Include="{HOSTILE}" Version="1.0.0" />')

    out = run_ci(tmp_path, capsys, monkeypatch)

    assert out == "::error file=Directory.Packages.props::A?::add-mask::x?::stop-commands::t%250A?2J is pinned in Directory.Packages.props but no project references it\n"


def test_an_id_cant_move_the_terminal_in_a_plain_line(tmp_path, capsys):
    central(tmp_path, f'<PackageVersion Include="{HOSTILE}" Version="1.0.0" />')

    out = run(tmp_path, capsys)[1]

    assert out == "❌ A?::add-mask::x?::stop-commands::t%0A?2J is pinned in Directory.Packages.props but no project references it\n"


@pytest.mark.parametrize("extra,cut", [(-1, False), (0, False), (1, True)])
def test_printable_cuts_a_text_over_max_printed_characters(extra, cut):
    text = "x" * (ccp.MAX_PRINTED + extra)

    assert ccp.printable(text) == (f"{'x' * ccp.MAX_PRINTED}... (1 more)" if cut else text)


@pytest.mark.parametrize("text,expected", [
    ('{"a": [1,]}', '{"a": [1]}'),
    ('{"a": 1, \n}', '{"a": 1 \n}'),
    ('{"a": "x // y', '{"a": "x // y'),
    ('{"a": 1} /* x', '{"a": 1} '),
    ('[1, /* a\n */]', '[1 ]'),
    ('{"a": "x\\', '{"a": "x\\'),
    ('{"a": 1, // c\r\n}', '{"a": 1 \n}'),
    ('', ''),
    ('/', '/'),
    ('/*/', ''),
    ('{"a": "/* // */"}', '{"a": "/* // */"}'),
    ('["a\\\n", 1,]', '["a\\\n", 1]'),
])
def test_global_json_comments_and_trailing_commas_go_as_the_host_reads_them(text, expected):
    # An unclosed string or comment runs to the end; a trailing comma goes, the space after it stays; an escape may take a line break.
    assert ccp.without_comments(text) == expected


@pytest.mark.parametrize("body", ['"' + "x" * 70000, '"' + "\\n" * 35000 + '"', '"a",' * 17000, "/**/" * 17000, "1, " * 23000])
def test_a_global_json_at_its_cap_is_read_in_bounded_time_and_memory(tmp_path, monkeypatch, body):
    import tracemalloc

    text = ('{"msbuild-sdks": {"S": "1"}, "x": [' + body)[:ccp.MAX_GLOBAL_JSON_BYTES]
    (tmp_path / "global.json").write_text(text)
    read = []
    real = ccp.without_comments
    monkeypatch.setattr(ccp, "without_comments", lambda text: read.append(len(text)) or real(text))
    tracemalloc.start()
    started = time.monotonic()
    ccp.check(tmp_path)
    elapsed = time.monotonic() - started
    peak = tracemalloc.get_traced_memory()[1]
    tracemalloc.stop()

    assert len(text) == ccp.MAX_GLOBAL_JSON_BYTES and read == [len(text)]
    # A string matched with backtracking state per character peaks near 9 MiB here, and one per escape near 5 MiB.
    assert elapsed < 2 and peak < 4 * 1024 * 1024


def test_a_global_json_at_its_cap_is_read(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="S" Version="1.0.0" />')
    text = '{"msbuild-sdks": {"S": "1"}}'
    (tmp_path / "global.json").write_text(text + " " * (ccp.MAX_GLOBAL_JSON_BYTES - len(text)))

    status, out, _ = run(tmp_path, capsys)

    assert (tmp_path / "global.json").stat().st_size == ccp.MAX_GLOBAL_JSON_BYTES
    assert (status, out) == (0, "Every central pin is referenced by a project, or kept with a reason.\n")


def test_only_the_first_max_global_json_files_are_read(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(ccp, "MAX_GLOBAL_JSON_FILES", 3)
    pins = "".join(f'<PackageVersion Include="{sdk}" Version="1.0.0" />' for sdk in "RABCD")
    central(tmp_path, pins)
    # The root's sorts after a/ by path, and a/z/'s before b/: by depth, the root and then a/ and b/ are read.
    for folder, sdk in (("", "R"), ("a/z", "D"), ("a", "A"), ("b", "B"), ("c/y", "C")):
        (tmp_path / folder).mkdir(parents=True, exist_ok=True)
        (tmp_path / folder / "global.json").write_text(f'{{"msbuild-sdks": {{"{sdk}": "1"}}}}')

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    skipped = "was skipped: it is past the first 3 global.json files, which are all this check reads. A reference or property in it wasn't seen."
    assert out.splitlines() == [
        f"ℹ️  a/z/global.json {skipped}",
        f"ℹ️  c/y/global.json {skipped}",
        "❌ C is pinned in Directory.Packages.props but no project references it",
        "❌ D is pinned in Directory.Packages.props but no project references it",
    ]


def test_a_global_json_over_its_cap_is_skipped_without_being_opened(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="A" />')
    (tmp_path / "global.json").write_text("{}" + " " * (ccp.MAX_GLOBAL_JSON_BYTES - 1))
    real = os.open

    def refused(path, *args, **kwargs):
        if os.fspath(path) == str(tmp_path / "global.json"):
            raise AssertionError("opened")
        return real(path, *args, **kwargs)

    monkeypatch.setattr(ccp.os, "open", refused)

    status, out, _ = run(tmp_path, capsys)

    assert status == 0
    assert f"ℹ️  global.json was skipped: it is over {ccp.MAX_GLOBAL_JSON_BYTES} bytes." in out


def test_the_escapes_are_the_runners():
    # printable() already turns line breaks into '?', so these are the second line of defence, tested on their own.
    assert ccp.escape_data("a%\r\nb:,") == "a%25%0D%0Ab:,"
    assert ccp.escape_property("a%\r\nb:,") == "a%25%0D%0Ab%3A%2C"


def test_a_path_cant_start_a_line_in_the_file_property(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="a\n::error::b")

    out = run_ci(tmp_path, capsys, monkeypatch)

    assert out == "::error file=a?%3A%3Aerror%3A%3Ab/Directory.Packages.props::A is pinned in a?::error::b/Directory.Packages.props but no project references it\n"


def test_a_path_cant_break_the_file_property(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="a,b:c%d")

    out = run_ci(tmp_path, capsys, monkeypatch)

    assert out == "::error file=a%2Cb%3Ac%25d/Directory.Packages.props::A is pinned in a,b:c%25d/Directory.Packages.props but no project references it\n"


# Where the gate and CI run it.


def test_build_solution_runs_the_check_first_after_the_checkout():
    # Before Setup .NET, so a bad pin fails fast, and before restore, which would fail with NU1109 on it.
    # Imported here, so the rest of this file doesn't depend on that module loading (it reads two workflows when imported).
    from test_ci_gh_repo_job import load_workflow

    job = load_workflow(OWNED / ".github/workflows/ci.yml")["jobs"]["build"]
    names = [step.get("name") for step in job["steps"]]
    step = next(step for step in job["steps"] if step.get("name") == "Check central package pins")

    assert job["name"] == "Build Solution"
    assert step["run"] == f"{COMMAND} --root ."
    assert not {"if", "continue-on-error"} & step.keys()
    assert names.index("Check central package pins") == names.index("Checkout code") + 1
    assert names.index("Check central package pins") < names.index("Restore dependencies")


def test_the_gate_runs_the_check_before_the_build():
    lines = (OWNED / "scripts/gate.sh").read_text().splitlines()

    assert f'{COMMAND} --root "$ROOT"' in lines
    assert lines.index(f'{COMMAND} --root "$ROOT"') < lines.index('step "Build"')
