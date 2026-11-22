import pytest

import check_central_pins as ccp

TRANSITIVE = "<PropertyGroup><CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled></PropertyGroup>"


def central(root, items, properties=TRANSITIVE, folder=""):
    path = root / folder / "Directory.Packages.props"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f"<Project>\n  {properties}\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n")


def project(root, relative, body, sdk="Microsoft.NET.Sdk"):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f'<Project Sdk="{sdk}">\n  <ItemGroup>{body}</ItemGroup>\n</Project>\n')


def unreferenced(root):
    return [name for _, name in ccp.unreferenced_pins(root)[1]]


def test_a_pin_no_project_references_is_reported(tmp_path):
    # IssueManager's: Radzen.Blazor needed a newer Components.Web than the pin, which nothing referenced.
    central(tmp_path, '<PackageVersion Include="Radzen.Blazor" Version="13.1.0" />\n'
            '<PackageVersion Include="Microsoft.AspNetCore.Components.Web" Version="10.0.14" />')
    project(tmp_path, "src/Web/Web.csproj", '<PackageReference Include="Radzen.Blazor" />')

    assert unreferenced(tmp_path) == ["Microsoft.AspNetCore.Components.Web"]
    assert ccp.main(["--root", str(tmp_path)]) == 1


def test_every_pin_referenced_passes(tmp_path, capsys):
    central(tmp_path, '<PackageVersion Include="Radzen.Blazor" Version="13.1.0" />')
    project(tmp_path, "src/Web/Web.csproj", '<PackageReference Include="Radzen.Blazor" />')

    assert ccp.main(["--root", str(tmp_path)]) == 0
    assert "Every central pin" in capsys.readouterr().out


def test_without_transitive_pinning_an_unreferenced_pin_passes(tmp_path):
    # It pins nothing then: TicketManager's and Articles' leftovers.
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties="")

    assert ccp.main(["--root", str(tmp_path)]) == 0


def test_a_repo_without_central_package_management_passes(tmp_path):
    project(tmp_path, "src/App/App.csproj", '<PackageReference Include="Polly" Version="8.0.0" />')

    assert ccp.main(["--root", str(tmp_path)]) == 0


def test_transitive_pinning_set_in_directory_build_props_counts(tmp_path):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />', properties="")
    (tmp_path / "Directory.Build.props").write_text(f"<Project>{TRANSITIVE}</Project>\n")

    assert unreferenced(tmp_path) == ["Polly"]


def test_transitive_pinning_set_to_false_is_off(tmp_path):
    central(tmp_path, '<PackageVersion Include="Polly" Version="8.0.0" />',
            properties="<PropertyGroup><CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled></PropertyGroup>")

    assert ccp.main(["--root", str(tmp_path)]) == 0


def test_a_pin_with_a_reason_comment_right_before_it_is_kept(tmp_path):
    central(tmp_path, "<!-- Transitive pin: GHSA-7jgj-8wvc-jh57, fixed in 4.3.4 -->\n"
            '<PackageVersion Include="System.Net.Http" Version="4.3.4" />\n'
            '<PackageVersion Include="System.Text.RegularExpressions" Version="4.3.1" />')

    assert unreferenced(tmp_path) == ["System.Text.RegularExpressions"]


def test_a_reason_comment_covers_only_the_pin_right_after_it(tmp_path):
    central(tmp_path, "<!-- Transitive pin: a security fix -->\n"
            '<PackageVersion Include="A" Version="1.0.0" />\n'
            '<PackageVersion Include="B" Version="1.0.0" />')

    assert unreferenced(tmp_path) == ["B"]


def test_a_group_heading_comment_is_no_reason(tmp_path):
    central(tmp_path, "<!-- Microsoft -->\n<PackageVersion Include=\"Microsoft.OpenApi\" Version=\"2.13.0\" />")

    assert unreferenced(tmp_path) == ["Microsoft.OpenApi"]


def test_a_reason_comment_that_gives_no_reason_is_no_reason(tmp_path):
    central(tmp_path, "<!-- Transitive pin: -->\n<PackageVersion Include=\"A\" Version=\"1.0.0\" />")

    assert unreferenced(tmp_path) == ["A"]


def test_ids_match_ignoring_case(tmp_path):
    central(tmp_path, '<PackageVersion Include="FluentAssertions" Version="8.0.0" />')
    project(tmp_path, "tests/A/A.csproj", '<PackageReference Include="fluentassertions" />')

    assert unreferenced(tmp_path) == []


def test_a_reference_in_directory_build_props_or_an_update_counts(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />\n<PackageVersion Include="B" Version="1.0.0" />')
    (tmp_path / "Directory.Build.props").write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')
    project(tmp_path, "src/App/App.csproj", '<PackageReference Update="B" />')

    assert unreferenced(tmp_path) == []


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


def test_a_pin_named_through_a_property_is_left_alone(tmp_path):
    central(tmp_path, '<PackageVersion Include="$(PackagePrefix).Core" Version="1.0.0" />')

    assert unreferenced(tmp_path) == []


def test_folders_restore_and_build_write_are_skipped(tmp_path):
    # CI caches the NuGet packages under the workspace's .nuget/, whose packages carry .props files of their own.
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    for folder in (".nuget/packages/x/build", "src/App/obj", "src/App/bin", "node_modules/y"):
        (tmp_path / folder).mkdir(parents=True)
        (tmp_path / folder / "x.props").write_text('<Project><ItemGroup><PackageReference Include="A" /></ItemGroup></Project>\n')

    assert unreferenced(tmp_path) == ["A"]


def test_a_nested_central_file_is_checked_too(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />', folder="samples")

    assert [(path.parent.name, name) for path, name in ccp.unreferenced_pins(tmp_path)[1]] == [("samples", "A")]


def test_ci_gets_an_error_annotation_naming_the_file(tmp_path, capsys, monkeypatch):
    monkeypatch.setenv("GITHUB_ACTIONS", "true")
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')

    assert ccp.main(["--root", str(tmp_path)]) == 1
    captured = capsys.readouterr()
    assert "::error file=Directory.Packages.props::A is pinned in Directory.Packages.props but no project references it" in captured.out
    assert "<!-- Transitive pin: <the reason> -->" in captured.err


def test_a_malformed_file_is_named(tmp_path):
    central(tmp_path, '<PackageVersion Include="A" Version="1.0.0" />')
    project(tmp_path, "src/App/App.csproj", "<PackageReference")

    with pytest.raises(SystemExit, match="src/App/App.csproj is not well-formed XML"):
        ccp.main(["--root", str(tmp_path)])
