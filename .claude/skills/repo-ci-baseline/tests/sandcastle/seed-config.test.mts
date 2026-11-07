import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";

// What the Template says about lib/repo-config.mts, the places its gate reads its checks from, and Owned check.sh, held to the Seed file and
// references/sandcastle.md. These ran in dotfiles' lib/repo-config.test.mts when the Template lived there; its pins of dotfiles' own values stayed there.
// They run in the Factory, inside a repo the Template was Applied to (tests/sandcastle-check.test.sh copies this file into its .sandcastle/lib/ and
// sets REPO_CI_BASELINE_SKILL to this skill).

const skill = process.env.REPO_CI_BASELINE_SKILL;
if (!skill) throw new Error("seed-config.test.mts runs from tests/sandcastle-check.test.sh, which sets REPO_CI_BASELINE_SKILL");
const ownedFolder = join(skill, "template/owned/.sandcastle");
const seedFolder = join(skill, "template/seed/.sandcastle");

describe("the Template owns Sandcastle's code", () => {
  it("carries lib/repo-config.mts as a Seed file, not an Owned one", () => {
    assert.ok(existsSync(join(seedFolder, "lib/repo-config.mts")), "the Seed has no lib/repo-config.mts");
    assert.equal(existsSync(join(ownedFolder, "lib/repo-config.mts")), false);
  });
});

// mpaulosky/dotfiles#339 (R43, R44): the gh-repo check reads the repo's skills and agents, so a change to them reruns the CI and shows
// in a PR's list of changed check files.
describe("the places the gate's checks are read from (mpaulosky/dotfiles#339)", () => {
  const seedConfig = readFileSync(join(seedFolder, "lib/repo-config.mts"), "utf8");
  const stepNine = (): string => {
    const reference = readFileSync(join(skill, "references/sandcastle.md"), "utf8");
    const start = reference.indexOf("\n9. Set `GATE_SCRIPT`");
    assert.notEqual(start, -1, "references/sandcastle.md has no step 9 that sets GATE_SCRIPT");
    return reference.slice(start + 1, reference.indexOf("\n\n", start));
  };

  it("lists the checker and .claude/skills/ in the Seed lib/repo-config.mts CHECK_PATHS", () => {
    const declaration = /export const CHECK_PATHS[^=]*=\s*\[([^\]]*)\]/.exec(seedConfig)?.[1] ?? "";
    const listed = [...declaration.matchAll(/"([^"]+)"/g)].map((match) => match[1]);

    assert.notEqual(declaration, "", "the Seed has no CHECK_PATHS list");
    assert.ok(listed.includes(".github/scripts/check_gh_repo.py"), `the Seed CHECK_PATHS ${JSON.stringify(listed)} lacks the checker`);
    assert.ok(listed.includes(".claude/skills/"), `the Seed CHECK_PATHS ${JSON.stringify(listed)} lacks .claude/skills/`);
  });

  it("names the checker and .claude/skills/ in references/sandcastle.md's step 9", () => {
    const step = stepNine();

    assert.match(step, /check_gh_repo\.py/);
    assert.match(step, /\.claude\/skills\//);
  });
});

// mpaulosky/dotfiles#391: check.sh is Owned, so Apply refreshes it, and a repo's own watched paths live in the Seed check-paths.txt.
describe("the Owned check.sh in an existing repo (mpaulosky/dotfiles#391)", () => {
  it("references/sandcastle.md says Apply refreshes check.sh, and a repo moves its edited paths into check-paths.txt first", () => {
    const reference = readFileSync(join(skill, "references/sandcastle.md"), "utf8").replace(/\s+/g, " ");

    assert.match(reference, /`\.sandcastle\/check\.sh` is Owned, so Apply refreshes it/);
    assert.match(reference, /moves those paths, one per line, into the Seed `\.sandcastle\/check-paths\.txt`/);
    assert.match(reference, /renames it first and points `GATE_COMMAND` and `GATE_SCRIPT` at the new name/);
    assert.match(reference, /Apply keeps a `\.sandcastle\/check\.sh` that `GATE_COMMAND` or `GATE_SCRIPT` names, in `lib\/repo-config\.mts` or the older `lib\/config\.mts`/);
    assert.match(reference, /A list of exclude lines alone is treated as empty/);
    assert.match(reference, /Name it `\.sandcastle\/sandbox-gate\.sh`, not `\.sandcastle\/check\.sh`, which is Owned/);
  });
});
