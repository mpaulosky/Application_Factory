import os
from pathlib import Path

import pytest

import check_central_pins as ccp
from test_ci_gh_repo_job import load_workflow

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
    assert f"Not failing: a reference this check can't read without evaluating MSBuild ({reference})" in out


def test_a_pin_that_cant_be_read_is_left_alone(tmp_path):
    central(tmp_path, '<PackageVersion Include="$(Unset).Core" Version="1.0.0" /><PackageVersion Include="@(X)" Version="1.0.0" />')

    assert unreferenced(tmp_path) == []


def test_the_caps_are_the_documented_ones():
    # The boundary tests below are written against the constants, so these pin their values.
    assert (ccp.MAX_BYTES, ccp.MAX_EXPANSIONS) == (4 * 1024 * 1024, 256)


def expansions(tmp_path, count):
    """A reference with two properties giving count ids in all (count = width * 2)."""
    width = count // 2
    values = "".join(f'<PropertyGroup Condition="{i}"><First>p{i}</First></PropertyGroup>' for i in range(width))
    (tmp_path / "Directory.Build.props").write_text(
        f'<Project>{values}<PropertyGroup><Second>x</Second></PropertyGroup><PropertyGroup Condition="z"><Second>y</Second></PropertyGroup></Project>\n')
    central(tmp_path, '<PackageVersion Include="Orphan" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="$(First).$(Second)" />')


@pytest.mark.parametrize("count,readable", [(ccp.MAX_EXPANSIONS - 2, True), (ccp.MAX_EXPANSIONS, True), (ccp.MAX_EXPANSIONS + 2, False)])
def test_a_reference_resolving_to_more_than_max_expansions_ids_cant_be_read(tmp_path, count, readable):
    expansions(tmp_path, count)

    assert (ccp.check(tmp_path)[2] == []) is readable


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
    assert "ℹ️  global.json was skipped: it isn't JSON the .NET host reads. A reference in it wasn't seen.\n" in out


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
    for name in ("Linked.props", "Dangling.props", "global.json"):
        assert f"{name} was skipped: it is a symlink." in out


@pytest.mark.skipif(not hasattr(os, "mkfifo"), reason="needs FIFOs")
def test_a_fifo_is_not_read(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    os.mkfifo(tmp_path / "Pipe.props")

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert "Pipe.props was skipped: it is not a regular file." in out


@pytest.mark.parametrize("extra,read", [(-1, True), (0, True), (1, False)])
def test_a_file_over_max_bytes_is_not_read(tmp_path, capsys, extra, read):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    body = '<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>'
    padding = ccp.MAX_BYTES + extra - len(body) - len("<!--") - len("-->")
    (tmp_path / "Big.props").write_text(f"<!--{'x' * padding}-->{body}")
    assert (tmp_path / "Big.props").stat().st_size == ccp.MAX_BYTES + extra

    status, out, _ = run(tmp_path, capsys)

    assert status == (0 if read else 1)
    assert (f"Big.props was skipped: it is over {ccp.MAX_BYTES} bytes." in out) is not read


def test_a_file_that_grew_past_max_bytes_since_lstat_is_not_read(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    body = '<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>'
    (tmp_path / "Grown.props").write_text(f"<!--{'x' * ccp.MAX_BYTES}-->{body}")
    real = os.lstat

    def small(path, *args, **kwargs):
        info = real(path, *args, **kwargs)
        if str(path).endswith("Grown.props"):
            return os.stat_result((info.st_mode, *info[1:6], 10, *info[7:]))
        return info

    monkeypatch.setattr(ccp.os, "lstat", small)

    status, out, _ = run(tmp_path, capsys)

    assert status == 1
    assert f"Grown.props was skipped: it is over {ccp.MAX_BYTES} bytes." in out


@pytest.mark.skipif(hasattr(os, "geteuid") and os.geteuid() == 0, reason="root reads any file")
def test_an_unreadable_file_is_skipped_with_its_reason(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    locked = project(tmp_path, "Locked.csproj", '<PackageReference Include="A" />')
    locked.chmod(0)
    try:
        status, out, _ = run(tmp_path, capsys)
    finally:
        locked.chmod(0o644)

    assert status == 1
    assert "Locked.csproj was skipped: it can't be read (Permission denied)." in out


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


def test_the_escapes_are_the_runners():
    # printable() already turns line breaks into '?', so these are the second line of defence, tested on their own.
    assert ccp.escape_data("a%\r\nb:,") == "a%25%0D%0Ab:,"
    assert ccp.escape_property("a%\r\nb:,") == "a%25%0D%0Ab%3A%2C"


def test_a_path_cant_break_the_file_property(tmp_path, capsys, monkeypatch):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="a,b:c%d")

    out = run_ci(tmp_path, capsys, monkeypatch)

    assert out == "::error file=a%2Cb%3Ac%25d/Directory.Packages.props::A is pinned in a,b:c%25d/Directory.Packages.props but no project references it\n"


# Where the gate and CI run it.


def test_build_solution_runs_the_check_first_after_the_checkout():
    # Before Setup .NET, so a bad pin fails fast, and before restore, which would fail with NU1109 on it.
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
