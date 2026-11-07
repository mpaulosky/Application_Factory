# Coverage thresholds and reports

Beyond the basic `--coverage` run in [SKILL.md](SKILL.md). It assumes Microsoft Testing Platform and
`Microsoft.Testing.Extensions.CodeCoverage`, as Baseline Applications use.

## The threshold is CI's

A Baseline Application's CI runs each test project with `--coverage --coverage-output-format cobertura`, merges the reports with
`reportgenerator`, and fails when the line coverage is below the whole percentage in the Owned `.github/ci/coverage-threshold` (80 when
the file is missing). The file is Owned, so an Application has no threshold of its own: change the Template's number, not the Application's
(the repo-ci-baseline skill's `references/test-matrix.md`).

To see the same number locally, run the tests with Cobertura output, then merge them as CI does:

```bash
dotnet test --coverage --coverage-output-format cobertura --coverage-output unit.cobertura.xml
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"tests/**/*.cobertura.xml" \
  -targetdir:coverage-output \
  -reporttypes:JsonSummary
jq '.summary.linecoverage' coverage-output/Summary.json
```

## HTML report

```bash
reportgenerator \
  -reports:"tests/**/*.cobertura.xml" \
  -targetdir:coverage-report \
  -reporttypes:Html
```
