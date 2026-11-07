// The Template's Sandcastle prompts and docs, checked from the Factory.
//
// These ran in dotfiles when the Template lived there; they read the Template's Seed files, its Owned prompts and its docs. They
// run here inside a repo the Template was Applied to (tests/sandcastle-check.test.sh copies this file into its .sandcastle/lib/ and
// sets REPO_CI_BASELINE_SKILL to this skill), so `read` reaches the Applied copy of an Owned file, as it did dotfiles' copy, and
// `skill` reaches the Template itself.
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { describe, it } from "node:test";
import { pathToFileURL } from "node:url";

const skillDir = process.env.REPO_CI_BASELINE_SKILL;
if (!skillDir) throw new Error("template-prompts.test.mts runs from tests/sandcastle-check.test.sh, which sets REPO_CI_BASELINE_SKILL");
const skill = pathToFileURL(`${skillDir}/`);

const sandcastleFile = (path: string) => new URL(`../${path}`, import.meta.url);
const read = (path: string) => readFileSync(sandcastleFile(path), "utf8");

// The host's secret scan (lib/scan.mts) refuses a commit whose added lines match a secret's shape and cannot tell a fixture from a secret, so
// the roles that write tests build such strings at run time (a tester's fake token stopped an issue from being published). The rule lives in
// the shared rules, which every role reads, not in each role's prompt.
describe("secret-shaped test fixtures", () => {
  const seedRules = new URL("template/seed/.sandcastle/roles/shared-rules.md", skill);
  const ruleCopies = [
    ["the Template's Seed shared rules", () => readFileSync(seedRules, "utf8")],
  ] as const;

  for (const [name, rules] of ruleCopies) {
    it(`tells the roles in ${name} to build a secret-shaped test string at run time, with the example`, () => {
      const text = rules();

      assert.match(text, /secret/i);
      assert.match(text, /at run time/);
      assert.ok(text.includes('`${"gh"}p_${"A".repeat(36)}`'), "the run-time token example is missing");
      assert.match(text, /GitHub token/);
      assert.match(text, /API key/);
    });

    it(`tells the roles in ${name} never to write the whole shape in a source line, a fixture or a comment`, () => {
      const text = rules();

      assert.match(text, /never/i);
      assert.match(text, /source line/);
      assert.match(text, /fixture/);
      assert.match(text, /comment/);
    });

    it(`says in ${name} that the host scans every commit's added lines, a match stops the issue and a later commit doesn't clear it`, () => {
      const text = rules();

      assert.match(text, /scans every commit/);
      assert.match(text, /added lines/);
      assert.match(text, /not (?:be )?published|from being published/);
      assert.match(text, /later commit does not clear it|later commit doesn't clear it/);
    });
  }

  it("reaches the tester and the backend through the shared rules, not a copy in each role's prompt", () => {
    for (const role of ["tester", "backend"]) {
      const prompt = read(`roles/${role}.md`);

      assert.match(prompt, /\{\{SHARED_RULES\}\}/, role);
      assert.ok(!prompt.includes('"A".repeat(36)'), `${role} carries its own copy of the rule`);
      assert.doesNotMatch(prompt, /secret-shaped/, role);
    }
  });
});

// #280: the Template's Seed files under roles/ are dotfiles' text rewritten for any repo. Sandcastle (lib/prompts.mts) fills the {{NAME}}s at
// run time, so a rewrite that drops one leaves a value unused, or an agent without what it needs. Apply fills {{OWNER}}, {{REPO}}
// and {{SOLUTION}} itself. mpaulosky/dotfiles#392 made the role prompts Owned, so only the repo's own two files are left in the Seed.
describe("the Template's Seed prompts", () => {
  const seedSandcastle = new URL("template/seed/.sandcastle/", skill);

  it("keeps none of the nine Owned prompts", () => {
    for (const prompt of ["plan-prompt.md", "review-prompt.md", ...["architect", "backend", "tester", "scribe", "gate-fixer", "follow-up", "ui"].map((role) => `roles/${role}.md`)]) {
      assert.equal(existsSync(new URL(prompt, seedSandcastle)), false, `the Template's Seed still has .sandcastle/${prompt}`);
    }
  });

  it("has an empty roles/skills.md, which a .NET repo fills with its # SKILLS block", () => {
    const seed = new URL("roles/skills.md", seedSandcastle);

    assert.ok(existsSync(seed), "the Template's Seed has no .sandcastle/roles/skills.md");
    assert.equal(readFileSync(seed).length, 0);
  });

  it("gives the yamllint command roles/tester.md sends the tester to, with the config the Template ships at the repo root", () => {
    const rules = readFileSync(new URL("roles/shared-rules.md", seedSandcastle), "utf8");

    assert.match(read("roles/tester.md"), /the command is in the rules below/);
    assert.match(rules, /`yamllint -c \.yamllint\.yml <file>`/);
  });

  it("points every role at what a repo adds, in roles/shared-rules.md: CONTEXT.md and .claude/rules/ where present, and the UI developer's files", () => {
    const rules = readFileSync(new URL("roles/shared-rules.md", seedSandcastle), "utf8");

    assert.match(rules, /CONTEXT\.md/);
    assert.match(rules, /\.claude\/rules\//);
    assert.match(rules, /UI developer/);
  });

  it("tells the tester where tests go in the Seed shared rules, since tester.md leaves that to the rules", () => {
    const rules = readFileSync(new URL("roles/shared-rules.md", seedSandcastle), "utf8");

    assert.match(rules, /Where tests go: in the test project under `tests\/` for the project under `src\/`, and as `<module>\.test\.mts` beside a Sandcastle module/);
  });

  it("lists the Owned prompts in the Seed coding standards, so a repo doesn't edit one a re-Apply overwrites", () => {
    const standards = readFileSync(new URL("CODING_STANDARDS.md", seedSandcastle), "utf8").replace(/\s+/g, " ");
    const owned = new URL("../../owned/.sandcastle/", seedSandcastle);
    const prompts = readdirSync(owned).filter((name) => name.endsWith("-prompt.md"));

    assert.ok(prompts.length >= 4, `expected the Owned top-level prompts, found ${JSON.stringify(prompts)}`);
    for (const name of prompts) assert.ok(standards.includes(`\`${name}\``), `the Seed coding standards don't name the Owned ${name}`);
    assert.match(standards, /every role prompt under `roles\/` but `shared-rules\.md` and `skills\.md` are owned/);
  });
});

// mpaulosky/dotfiles#392: the nine prompts that instruct the pipeline's roles are the Template's Owned files, read in every repo, so they hold
// no repo's text: a repo's content goes in roles/shared-rules.md and roles/skills.md.
describe("the Template's Owned prompts", () => {
  const ownedSandcastle = new URL("template/owned/.sandcastle/", skill);
  const owned = (path: string) => {
    const file = new URL(path, ownedSandcastle);
    assert.ok(existsSync(file), `the Template's Owned folder has no .sandcastle/${path}`);
    return readFileSync(file, "utf8");
  };
  const rolePrompts = ["architect", "backend", "tester", "follow-up", "gate-fixer", "scribe", "ui"].map((role) => `roles/${role}.md`);
  const prompts = ["plan-prompt.md", "review-prompt.md", ...rolePrompts];

  for (const prompt of prompts) {
    it(`${prompt} holds no repo's name, stack or issue number`, () => {
      const text = owned(prompt);

      assert.doesNotMatch(text, /\{\{(OWNER|REPO|SOLUTION)\}\}/, "Apply would fill it, and an Owned file is overwritten");
      assert.doesNotMatch(text, /Blazor|\.razor|bUnit|dotnet|\.slnx/);
      // dotfiles' own text belongs in its shared-rules.md: test.sh greps the Seed's .sandcastle for these words, but not the Owned prompts.
      // `mpaulosky/dotfiles#N` is how an Owned file names a dotfiles issue, so it is let through.
      assert.doesNotMatch(
        text.replace(/mpaulosky\/dotfiles#\d+/g, ""),
        /dotfiles|repo-ci-baseline|Support For New Projects/,
        "dotfiles' own text goes in dotfiles' roles/shared-rules.md",
      );
      assert.doesNotMatch(text, /(?<![\w/])#\d+\b/, "a bare #N reads as the repo's own issue");
    });
  }

  for (const prompt of prompts) {
    it(`${prompt} tells no role a yamllint config path or a CODING_STANDARDS.md the repo may lack`, () => {
      const text = owned(prompt);

      assert.doesNotMatch(text, /yamllint -c \S*\.yamllint\.yml/, "the shared rules give the yamllint command: a repo's config path differs (dotfiles has no root .yamllint.yml)");
      // Whitespace is collapsed first: a prompt wraps its lines anywhere.
      const flat = text.replace(/\s+/g, " ");
      const named = flat.match(/\.sandcastle\/CODING_STANDARDS\.md/g)?.length ?? 0;
      const hedged = flat.match(/\.sandcastle\/CODING_STANDARDS\.md` where the repo has one/g)?.length ?? 0;
      assert.equal(hedged, named, "a repo may have no .sandcastle/CODING_STANDARDS.md: say `.sandcastle/CODING_STANDARDS.md` where the repo has one");
    });
  }

  for (const prompt of ["plan-prompt.md", "review-prompt.md", "roles/architect.md", "roles/tester.md", "roles/backend.md", "roles/ui.md"]) {
    it(`${prompt} points at CLAUDE.md, docs/adr/, the coding standards and whatever else the rules below point to`, () => {
      const text = owned(prompt);

      assert.match(text, /`CLAUDE\.md`/);
      assert.match(text, /\.sandcastle\/CODING_STANDARDS\.md/);
      if (prompt !== "plan-prompt.md" && prompt !== "review-prompt.md") {
        assert.match(text, /`docs\/adr\/`/);
        assert.match(text, /whatever else the rules below point to/);
      }
    });
  }

  for (const role of ["architect", "backend", "tester", "follow-up", "gate-fixer", "scribe", "ui"]) {
    it(`roles/${role}.md gets the shared rules`, () => {
      assert.match(owned(`roles/${role}.md`), /\{\{SHARED_RULES\}\}/);
    });
  }

  it("gives the planner and the reviewer the shared rules too", () => {
    for (const prompt of ["plan-prompt.md", "review-prompt.md"]) assert.match(owned(prompt), /\{\{SHARED_RULES\}\}/, prompt);
  });

  for (const role of ["tester", "backend", "ui"]) {
    it(`roles/${role}.md has {{REPO_SKILLS}} on a line of its own before its # EXECUTION section`, () => {
      const text = owned(`roles/${role}.md`);
      const at = text.search(/^\{\{REPO_SKILLS\}\}$/m);

      assert.notEqual(at, -1);
      assert.ok(at < text.indexOf("\n# EXECUTION"), "it goes before # EXECUTION, where the # SKILLS block sat");
      assert.doesNotMatch(text, /^# SKILLS\s*$/m);
    });
  }

  it("names the developer, not the backend developer, in roles/ui.md, which also calls itself the UI developer", () => {
    const text = owned("roles/ui.md");

    assert.match(text, /the developer/);
    assert.match(text, /UI developer/);
    assert.doesNotMatch(text, /backend developer/);
  });

  it("asks the planner for {{UI_ROLE}} on a line of its own between the architect and scribe bullets, with no static ui bullet", () => {
    const text = owned("plan-prompt.md");
    const architect = text.search(/^- `architect`:/m);
    const slot = text.search(/^\{\{UI_ROLE\}\}$/m);
    const scribe = text.search(/^- `scribe`:/m);

    assert.ok(architect !== -1 && slot !== -1 && scribe !== -1);
    assert.ok(architect < slot && slot < scribe, "{{UI_ROLE}} goes between the architect and scribe bullets");
    assert.doesNotMatch(text, /^- `ui`:/m);
    assert.match(text, /"roles": \[/);
    assert.equal(text.match(/"files": \[/g)?.length, 2, "the example plan keeps two picks, each with files");
  });

  it("tells the tester to build with the build commands in the shared rules, and still shows the stubs for C#, TypeScript and shell or Python", () => {
    const text = owned("roles/tester.md");

    assert.match(text, /build commands in the rules below/);
    assert.match(text, /throw new Error\("Not implemented"\)/);
    assert.match(text, /<module>\.test\.mts/);
    assert.match(text, /C#/);
  });

  it("keeps the full skip and todo list in the backend and gate-fixer prompts", () => {
    for (const role of ["backend", "gate-fixer"]) {
      const text = owned(`roles/${role}.md`);

      for (const form of ["`.skip`", "`.todo`", "`xfail`", "`pytest.skip()`", "`exit 0`"]) assert.ok(text.includes(form), `${role} doesn't name ${form}`);
    }
  });

  it("leaves the Template's own files to the shared rules in the backend, follow-up, scribe and architect prompts", () => {
    assert.doesNotMatch(owned("roles/backend.md"), /copy the Template's file over the copy/);
    const followUp = owned("roles/follow-up.md");
    assert.match(followUp, /only as the shared rules say/);
    assert.match(followUp, /out of scope/);
    assert.match(followUp, /can't push a workflow change/);
    const scribe = owned("roles/scribe.md");
    assert.match(scribe, /anything the shared rules say belongs elsewhere/);
    assert.doesNotMatch(scribe, /template\/owned\//);
    assert.match(owned("roles/architect.md"), /anything the shared rules say about copies of a file/);
    assert.match(owned("roles/gate-fixer.md"), /one at a time/);
  });

  it("follows docs/PROCESS.md in the review prompt only where the repo has one", () => {
    const text = owned("review-prompt.md");

    assert.match(text, /docs\/PROCESS\.md/);
    assert.match(text, /where the repo has one/);
  });
});

// mpaulosky/dotfiles#392: the prose that describes the prompts says they are Owned. Each of these once described the Seed prompts.
describe("the docs about the Owned prompts", () => {
  const doc = (path: string) => readFileSync(new URL(path, skill), "utf8").replace(/\s+/g, " ");

  it("ADR 0006's title no longer calls the prompts Seed, and its owning-the-pipeline option says #392 superseded it in part", () => {
    const adr = doc("docs/adr/0006-sandcastle-section-of-the-template.md");

    assert.doesNotMatch(adr.slice(0, adr.indexOf(" **Status:**")), /prompts are Seed/);
    assert.match(adr, /^# Sandcastle's shared files and prompts are Owned/);
    const option = adr.slice(adr.indexOf("**Owning the whole pipeline**"), adr.indexOf("**Keeping the role prompts Seed**"));
    assert.match(option, /mpaulosky\/dotfiles#392/);
    assert.doesNotMatch(option, /role prompts hold per-repo values/);
  });

  it("SKILL.md says a re-Apply asks for a review when it writes a Sandcastle prompt, not only a judging role's", () => {
    const text = doc("SKILL.md");

    assert.doesNotMatch(text, /a judging-role prompt/);
    assert.match(text, /a Sandcastle prompt \(intake, critique, plan, review or a role's\)/);
  });

  it("references/sandcastle.md says where a sandbox gate's instructions go and why SANDBOX_SKILLS is empty", () => {
    const text = doc("references/sandcastle.md");

    assert.doesNotMatch(text, /a Seed prompt can't promise a skill/);
    assert.match(text, /a skills block that lists a skill a host may lack can't be in the Seed/);
    assert.doesNotMatch(text, /points the gate-fixer and follow-up roles' instructions at it/);
    assert.match(text, /points the gate-fixer and follow-up roles at it in `roles\/shared-rules\.md`/);
    const beforeApply = text.slice(text.indexOf("**Before Apply**"), text.indexOf("Diff the repo's `Dockerfile`"));
    assert.match(beforeApply, /instructions for a sandbox gate \(step 8\)/);
  });

  it("the Seed repo-config.mts tells a repo to list a mounted skill in roles/skills.md, not in the prompts", () => {
    const text = doc("template/seed/.sandcastle/lib/repo-config.mts").replace(/ \/\/ /g, " ");
    const comment = text.slice(text.indexOf("The host skills the role prompts"), text.indexOf("export const SANDBOX_SKILLS"));

    assert.doesNotMatch(comment, /`# SKILLS` section of the tester/);
    assert.match(comment, /`roles\/skills\.md`, which the host passes to the tester, backend and ui prompts as \{\{REPO_SKILLS\}\}/);
  });
});

// The Owned workflow uploads the report folder the host writes outside the checkout, and reads hand-backs from it.
describe("the Owned Sandcastle workflow's report upload", () => {
  it("uploads the report folder the host writes outside the checkout, and reads hand-backs from it", async () => {
    const { REPORT_DIR_NAME } = await import("./report.mts");
    const path = ".github/workflows/sandcastle.yml";
    const workflow = readFileSync(new URL(`../../${path}`, import.meta.url), "utf8");
    const step = workflow.slice(workflow.indexOf("- name: Upload the run report"));
    const block = /path: \|\r?\n((?: {12}\S.*\r?\n)+)/.exec(step)?.[1];

    assert.ok(block, `${path} has no upload path block`);
    assert.deepEqual(
      block.trim().split(/\r?\n/).map((line) => line.trim()),
      ["${{ runner.temp }}/" + REPORT_DIR_NAME],
      `${path} uploads something else than the report folder, which the host writes outside the checkout`,
    );
    assert.ok(workflow.includes(`handbacks="$RUNNER_TEMP/${REPORT_DIR_NAME}/handbacks.json"`), `${path} reads handbacks.json from somewhere else`);
    assert.ok(!workflow.includes(".sandcastle/logs/handbacks.json") && !workflow.includes(".sandcastle/logs/summary.md"), `${path} still names a report file under the checkout`);
  });
});
